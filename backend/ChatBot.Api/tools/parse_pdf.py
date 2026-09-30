#!/usr/bin/env python3
"""
法規 PDF／純文字 → 條文片段（JSON）

這支腳本只做一件事：解析。embedding、寫入 Qdrant、預算 KV 都在 C# 後端。

── 為什麼解析留在 Python ───────────────────────────────────────────
這裡的 PDFParser 與 import_legal_pdfs.py 中那一份是同一套邏輯，而那一套
是唯一經過驗證的版本：公司法 533 條 + 民法 1,439 條，產出 327 個片段、
0 個重複標籤。

先前後端另有一份 C# 實作，行為與這裡不同（條號行被抽離內文、沒有 500 字
合併），若讓上傳流程走那一份，產出的片段會與資料庫中既有的 327 段顆粒度
不一致，而且片段內文不含條號、模型引用不出來——那正是 2026/09/24 修掉的
缺陷。與其在另一個語言、另一個 PDF 文字引擎上重新實作並祈禱行為相同，
不如讓後端以子行程呼叫這份已驗證的程式。

輸出到 stdout 的只有 JSON；所有訊息走 stderr，呼叫端才能安全地直接剖析。

用法：
    python3 parse_pdf.py --file 公司法.pdf
    python3 parse_pdf.py --text --title 公司法 < plain.txt
"""

import argparse
import hashlib
import json
import os
import re
import sys
from typing import Optional


def log(msg: str) -> None:
    print(msg, file=sys.stderr)


class PDFParser:
    """
    切出可供檢索與快取的條文片段。

    ── 條號必須整行比對 ───────────────────────────────────────────
    全國法規資料庫匯出的 PDF，條號是「阿拉伯數字加空格」：

        第 一 章 總則
        第 1 條
        1   本法所稱公司，謂以營利為目的……

    若正則只認中文數字、且未要求整行，會發生兩件事，而且都是靜默的：

      1. 真正的條號一個都沒被認出來，全部併進前一段的內文。
      2. 被認出來的全是內文裡的**交叉引用**——「準用第二十九條第一項規定」
         「公司有第一百五十六條之四之情形者」。腳本把這些引用當成新條文的
         開始，在句子中間切開，並用被引用的條號當作該片段的標籤。

    ── 條號那一行保留在內文中 ─────────────────────────────────────
    片段因此自帶「第 293 條」，模型引用得出來；快取鍵也與顯示內容一致。
    後端不需要、也不可以再於片段前面補上任何隨位置變動的標記
    （例如「[資料 1]」）——那會讓同一條條文在不同次檢索算出不同的快取鍵。
    """

    ARTICLE_PATTERN = re.compile(
        r"^第\s*(\d+(?:\s*-\s*\d+)?|[零一二三四五六七八九十百千○]+(?:\s*之\s*[零一二三四五六七八九十百千○]+)?)\s*條\s*$"
    )
    CHAPTER_PATTERN = re.compile(r"^第\s*([\d零一二三四五六七八九十百千○]+)\s*章\s*(.*)$")
    SECTION_PATTERN = re.compile(r"^第\s*([\d零一二三四五六七八九十百千○]+)\s*節\s*(.*)$")
    ITEM_PATTERN = re.compile(r"^第\s*([\d零一二三四五六七八九十百千○]+)\s*目\s*(.*)$")
    LAW_NAME_PATTERN = re.compile(r"^法規名稱[：:]\s*(.+)$")

    # 片段長度（字元）。500 字的中文約 500～600 tokens。
    # 合併的理由不是任何長度門檻（LMCache 的 blend_min_tokens 是未實作的設定），
    # 而是片段數越多，lookup 越容易在前面踩到未命中而中斷後續（驗證報告 8.3）。
    TARGET_CHARS = 500
    MIN_CHARS = 300

    def __init__(self, chunk_size: int = TARGET_CHARS):
        self.chunk_size = chunk_size or self.TARGET_CHARS

    # ------------------------------------------------------------------
    # 對外介面
    # ------------------------------------------------------------------

    def parse_pdf(self, pdf_path: str) -> dict:
        try:
            import fitz
        except ImportError:
            raise SystemExit("缺少 pymupdf，請在容器內安裝：pip install pymupdf")

        doc = fitz.open(pdf_path)
        try:
            raw_lines = []
            for page in doc:
                raw_lines.extend(page.get_text().split("\n"))
        finally:
            doc.close()

        lines = [ln.strip() for ln in raw_lines]
        title = self._extract_title(lines, pdf_path)
        return self._build(lines, title, os.path.basename(pdf_path))

    def parse_text(self, text: str, title: str, source: str = "") -> dict:
        lines = [ln.strip() for ln in text.split("\n")]
        resolved = self._extract_title(lines, title) if not title else title
        return self._build(lines, resolved, source)

    def _build(self, lines: list, title: str, source: str) -> dict:
        articles = self._extract_articles(lines)

        if not articles:
            return {
                "title": title,
                "sourceFile": source,
                "articleCount": 0,
                "chunks": [],
                "warning": "沒有解析到任何條文，請確認檔案是否為條列式法規格式",
            }

        chunks = self._group_articles(articles, title)
        for c in chunks:
            c["sourceFile"] = source

        log(f"{source or title}：解析到 {len(articles)} 條，合併為 {len(chunks)} 個片段")

        return {
            "title": title,
            "sourceFile": source,
            "articleCount": len(articles),
            "chunks": chunks,
        }

    # ------------------------------------------------------------------
    # 逐行掃描
    # ------------------------------------------------------------------

    def _extract_articles(self, lines: list) -> list:
        articles: list = []
        chapter = section = item = ""
        current: Optional[dict] = None

        def flush():
            nonlocal current
            if current and current["body"]:
                current["text"] = "\n".join([current["header"]] + current["body"]).strip()
                del current["body"]
                articles.append(current)
            current = None

        for line in lines:
            if not line:
                continue

            if self.CHAPTER_PATTERN.match(line):
                flush()
                chapter = line
                section = item = ""
                continue

            if self.SECTION_PATTERN.match(line):
                flush()
                section = line
                item = ""
                continue

            if self.ITEM_PATTERN.match(line):
                flush()
                item = line
                continue

            m = self.ARTICLE_PATTERN.match(line)
            if m:
                flush()
                current = {
                    "number": re.sub(r"\s+", "", m.group(1)),
                    "header": line,
                    "body": [],
                    "chapter": chapter,
                    "section": section,
                    "item": item,
                }
                continue

            if current is not None:
                current["body"].append(line)

        flush()
        return articles

    # ------------------------------------------------------------------
    # 合併成片段
    # ------------------------------------------------------------------

    def _group_articles(self, articles: list, title: str) -> list:
        """
        把連續的條文合併到約 TARGET_CHARS 字。

        不跨章節合併：章節是語意邊界，跨章合併會讓檢索回來的片段夾帶
        不相關的條文，既稀釋相關度也讓 prompt 變長。
        """
        chunks: list = []
        buf: list = []

        def flush():
            nonlocal buf
            if buf:
                chunks.append(self._make_chunk(buf, title))
                buf = []

        prev_scope = None
        for art in articles:
            scope = (art["chapter"], art["section"], art["item"])
            if prev_scope is not None and scope != prev_scope:
                flush()
            prev_scope = scope

            buf.append(art)
            if sum(len(a["text"]) for a in buf) >= self.chunk_size:
                flush()

        flush()

        # 章節尾端可能留下過短的片段，往前合併（限同章同節）
        merged: list = []
        for chunk in chunks:
            if (
                merged
                and len(chunk["content"]) < self.MIN_CHARS
                and merged[-1]["chapter"] == chunk["chapter"]
                and merged[-1]["section"] == chunk["section"]
            ):
                prev = merged[-1]
                prev["content"] = prev["content"] + "\n" + chunk["content"]
                prev["articles"] = prev["articles"] + chunk["articles"]
                prev["article"] = self._format_label(prev["articles"])
                prev["nChars"] = len(prev["content"])
                prev["id"] = self._generate_id(
                    f"{prev['title']}-{prev['article']}-{prev['content'][:50]}"
                )
            else:
                merged.append(chunk)

        return merged

    def _make_chunk(self, arts: list, title: str) -> dict:
        content = "\n".join(a["text"] for a in arts)
        numbers = [a["number"] for a in arts]
        label = self._format_label(numbers)
        first = arts[0]

        chapter_section = first["chapter"]
        if first["section"]:
            chapter_section = (
                f"{chapter_section}/{first['section']}" if chapter_section else first["section"]
            )

        return {
            "id": self._generate_id(f"{title}-{label}-{content[:50]}"),
            "title": title,
            "content": content,
            "chapter": chapter_section,
            "section": first["section"],
            "article": label,
            "articles": numbers,
            "nChars": len(content),
        }

    @staticmethod
    def _format_label(numbers: list) -> str:
        """['292','293','295'] → '第 292、293、295 條'"""
        if not numbers:
            return ""
        if len(numbers) == 1:
            return f"第 {numbers[0]} 條"
        return "第 " + "、".join(numbers) + " 條"

    # ------------------------------------------------------------------
    # 雜項
    # ------------------------------------------------------------------

    def _extract_title(self, lines: list, fallback_path: str) -> str:
        """優先讀檔內的「法規名稱：」，讀不到才退回檔名。"""
        for line in lines[:20]:
            m = self.LAW_NAME_PATTERN.match(line)
            if m:
                return m.group(1).strip()

        name = os.path.splitext(os.path.basename(fallback_path))[0]
        return name.replace("_", " ").replace("-", " ") or "未知文件"

    @staticmethod
    def _generate_id(text: str) -> str:
        """
        以內容決定的 UUID。

        用 UUID 而非整數，是為了與後端 Qdrant 客戶端的 PointId 型別一致。
        取 md5 的 16 bytes 剛好是一個 UUID，且完全由內容決定——
        同一份檔案重新上傳會得到相同的 id，不會產生重複的點。
        """
        digest = hashlib.md5(text.encode("utf-8")).digest()
        h = digest.hex()
        return f"{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}"


def main() -> None:
    ap = argparse.ArgumentParser(description="法規 PDF／文字 → 條文片段 JSON")
    ap.add_argument("--file", help="PDF 檔案路徑")
    ap.add_argument("--text", action="store_true", help="改從 stdin 讀純文字")
    ap.add_argument("--title", default="", help="法典名稱；PDF 模式下留空則讀檔內的「法規名稱：」")
    ap.add_argument("--source", default="", help="來源檔名，僅記錄用")
    ap.add_argument("--chunk-size", type=int, default=PDFParser.TARGET_CHARS)
    args = ap.parse_args()

    parser = PDFParser(chunk_size=args.chunk_size)

    if args.text:
        if not args.title:
            raise SystemExit("--text 模式必須提供 --title")
        result = parser.parse_text(sys.stdin.read(), args.title, args.source)
    elif args.file:
        if not os.path.exists(args.file):
            raise SystemExit(f"找不到檔案：{args.file}")
        result = parser.parse_pdf(args.file)
        if args.title:
            result["title"] = args.title
            for c in result["chunks"]:
                c["title"] = args.title
    else:
        raise SystemExit("請指定 --file 或 --text")

    json.dump(result, sys.stdout, ensure_ascii=False)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
