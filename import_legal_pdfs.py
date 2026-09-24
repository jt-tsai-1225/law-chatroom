#!/usr/bin/env python3
"""
法律 PDF 檔案匯入 Qdrant 向量資料庫工具

此腳本用於將中華民國公司法和民法的 PDF 檔案解析、切分、生成向量並存入 Qdrant。

使用方法:
    python import_legal_pdfs.py --pdf-dir ./legal_pdfs --collection legal_documents

依賴安裝:
    pip install fitz pymupdf qdrant-client requests tqdm
"""

import argparse
import asyncio
import hashlib
import json
import logging
import os
import re
import sys
import time
from dataclasses import dataclass, asdict
from pathlib import Path
from typing import Optional

from dotenv import load_dotenv
import requests
from qdrant_client import QdrantClient

# 載入 .env 檔案
load_dotenv()
from qdrant_client.models import (
    Distance,
    FieldCondition,
    MatchValue,
    PayloadSchemaType,
    PointStruct,
    VectorParams,
)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
)
logger = logging.getLogger(__name__)

# ============================================================
# 配置區
# ============================================================

@dataclass
class ImportConfig:
    """匯入配置"""
    # Qdrant 連接配置
    qdrant_host: str = "localhost"
    qdrant_port: int = 6333
    qdrant_api_key: str = ""
    collection_name: str = "legal_documents"
    
    # Embedding 配置 (Qwen3-Embedding-0.6B)
    embedding_endpoint: str = "https://ainexus.phison.com/api/external/v1/embeddings"
    embedding_model: str = "Qwen/Qwen3-Embedding-0.6B"
    embedding_api_key: str = ""
    embedding_dimensions: int = 1024
    
    # PDF 處理配置
    pdf_directory: str = "./legal_pdfs"
    chunk_size: int = 500  # 每個 text chunk 的字元數
    chunk_overlap: int = 50  # chunk 之間的重疊字元數
    
    # 批量處理配置
    batch_size: int = 10  # 每次請求的 embedding 數量
    max_concurrent: int = 5  # 最大併發請求數


# ============================================================
# PDF 解析器
# ============================================================

class PDFParser:
    """
    使用 PyMuPDF (fitz) 解析法規 PDF，切出可供檢索與快取的條文片段。

    ── 為什麼整份重寫（2026/09/24）─────────────────────────────────
    舊版的條號正則只認中文數字：

        ARTICLE_PATTERN = re.compile(r"第[零一二三四五六七八九十百千万○]+條")

    但全國法規資料庫匯出的 PDF，條號是「阿拉伯數字加空格」：

        第 一 章 總則
        第 1 條
        1   本法所稱公司，謂以營利為目的……
        第 2 條

    造成兩個後果，且都是靜默發生的：

      1. 真正的條號一個都沒被認出來，全部變成前一段的內文。
      2. 被認出來的，全是內文裡的**交叉引用**——例如
         「準用第二十九條第一項規定」「公司有第一百五十六條之四之情形者」。
         腳本把這些引用當成新條文的開始，在句子中間切開，
         並用「被引用的條號」當作該片段的標籤。

    實測結果：公司法 533 條 + 民法 1,439 條，共 1,972 條，
    舊版只產出 270 個片段，且標籤與內容全部對不上。

    ── 這一版的作法 ───────────────────────────────────────────────
      * 條號、章、節、目一律**整行比對**，交叉引用不會誤判
      * 條號那一行保留在內文中，片段因此自帶條號，模型引用得出來，
        快取鍵也與顯示內容一致（見下方 group_articles 的說明）
      * 連續條文合併到約 500 字，對齊 LMCache 的 blend_min_tokens=256；
        過短的片段不會走 blend，合併後才有快取效益
    """

    # 條號：獨立成行，例如「第 1 條」「第 319-1 條」
    # 中文數字的寫法也一併接受，但同樣要求整行，避免匹配到內文的交叉引用
    ARTICLE_PATTERN = re.compile(
        r"^第\s*(\d+(?:\s*-\s*\d+)?|[零一二三四五六七八九十百千○]+(?:\s*之\s*[零一二三四五六七八九十百千○]+)?)\s*條\s*$"
    )
    CHAPTER_PATTERN = re.compile(r"^第\s*([\d零一二三四五六七八九十百千○]+)\s*章\s*(.*)$")
    SECTION_PATTERN = re.compile(r"^第\s*([\d零一二三四五六七八九十百千○]+)\s*節\s*(.*)$")
    ITEM_PATTERN = re.compile(r"^第\s*([\d零一二三四五六七八九十百千○]+)\s*目\s*(.*)$")
    # 法規名稱：公司法
    LAW_NAME_PATTERN = re.compile(r"^法規名稱[：:]\s*(.+)$")

    # 片段長度（字元）。500 字的中文約 500～600 tokens，穩定高於 blend_min_tokens=256
    TARGET_CHARS = 500
    MIN_CHARS = 300

    def __init__(self, chunk_size: int = TARGET_CHARS, chunk_overlap: int = 0):
        # chunk_overlap 保留只為相容既有呼叫端；條文是自然邊界，重疊沒有意義，
        # 而且會讓同一段文字出現在兩個片段裡，產生兩個不同的快取鍵。
        self.chunk_size = chunk_size or self.TARGET_CHARS
        self.chunk_overlap = chunk_overlap
        self._fitz = None
        try:
            import fitz
            self._fitz = fitz
        except ImportError:
            raise ImportError("請安裝 pymupdf: pip install pymupdf")

    # ------------------------------------------------------------------
    # 對外介面
    # ------------------------------------------------------------------

    def parse_pdf(self, pdf_path: str) -> list[dict]:
        """解析 PDF，回傳合併後的條文片段列表。"""
        doc = self._fitz.open(pdf_path)
        try:
            raw_lines = []
            for page in doc:
                raw_lines.extend(page.get_text().split("\n"))
        finally:
            doc.close()

        lines = [ln.strip() for ln in raw_lines]
        title = self._extract_title(lines, pdf_path)
        articles = self._extract_articles(lines)

        if not articles:
            logger.warning(f"{pdf_path} 沒有解析到任何條文，請檢查 PDF 格式")
            return []

        chunks = self._group_articles(articles, title)

        logger.info(
            f"{os.path.basename(pdf_path)}：解析到 {len(articles)} 條，"
            f"合併為 {len(chunks)} 個片段"
        )
        return chunks

    # ------------------------------------------------------------------
    # 內部：逐行掃描
    # ------------------------------------------------------------------

    def _extract_articles(self, lines: list[str]) -> list[dict]:
        """
        逐行掃描，切出單一條文。

        回傳的每一筆是「一條」，尚未合併；合併交給 _group_articles。
        """
        articles: list[dict] = []
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

            m = self.CHAPTER_PATTERN.match(line)
            if m:
                flush()
                chapter = line
                section = item = ""
                continue

            m = self.SECTION_PATTERN.match(line)
            if m:
                flush()
                section = line
                item = ""
                continue

            m = self.ITEM_PATTERN.match(line)
            if m:
                flush()
                item = line
                continue

            m = self.ARTICLE_PATTERN.match(line)
            if m:
                flush()
                current = {
                    "number": self._normalize_article_number(m.group(1)),
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

    @staticmethod
    def _normalize_article_number(raw: str) -> str:
        """把「319 - 1」之類的寫法正規化成「319-1」。"""
        return re.sub(r"\s+", "", raw)

    # ------------------------------------------------------------------
    # 內部：合併成片段
    # ------------------------------------------------------------------

    def _group_articles(self, articles: list[dict], title: str) -> list[dict]:
        """
        把連續的條文合併到約 TARGET_CHARS 字。

        為什麼要合併：
          單一條文平均只有一百多字，低於 LMCache 的 blend_min_tokens（預設 256），
          那樣的片段不會走 blend，快取等於沒有作用。合併到 500 字左右，
          與 CacheBlend 驗證報告第八節所用的語料顆粒度一致。

        為什麼不跨章節合併：
          章節是語意邊界，跨章合併會讓檢索回來的片段包含不相關的條文，
          既稀釋相關度，也讓 prompt 變長。

        ★ 片段文字就是快取鍵的來源 ★
          合併後的 text 已包含條號那一行，後端不需要、也不應該再在前面
          補上標籤——任何隨位置變動的前綴（例如「[資料 1]」）都會讓同一條
          條文在不同次檢索算出不同的快取鍵。
        """
        chunks: list[dict] = []
        buf: list[dict] = []

        def buf_chars() -> int:
            return sum(len(a["text"]) for a in buf)

        def flush():
            nonlocal buf
            if not buf:
                return
            chunks.append(self._make_chunk(buf, title))
            buf = []

        prev_scope = None
        for art in articles:
            scope = (art["chapter"], art["section"], art["item"])
            if prev_scope is not None and scope != prev_scope:
                flush()
            prev_scope = scope

            buf.append(art)
            if buf_chars() >= self.chunk_size:
                flush()

        flush()

        # 章節尾端可能留下過短的片段，往前合併（同章節才合併）
        merged: list[dict] = []
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
                prev["n_chars"] = len(prev["content"])
                prev["id"] = self._generate_id(
                    f"{prev['title']}-{prev['article']}-{prev['content'][:50]}"
                )
            else:
                merged.append(chunk)

        return merged

    def _make_chunk(self, arts: list[dict], title: str) -> dict:
        content = "\n".join(a["text"] for a in arts)
        numbers = [a["number"] for a in arts]
        label = self._format_label(numbers)
        first = arts[0]

        chapter_section = first["chapter"]
        if first["section"]:
            chapter_section = f"{chapter_section}/{first['section']}" if chapter_section else first["section"]

        return {
            "id": self._generate_id(f"{title}-{label}-{content[:50]}"),
            "title": title,
            "content": content,
            "chapter": chapter_section,
            "section": first["section"],
            "article": label,
            "articles": numbers,
            "n_chars": len(content),
        }

    @staticmethod
    def _format_label(numbers: list[str]) -> str:
        """['292','293','295'] → '第 292、293、295 條'"""
        if not numbers:
            return ""
        if len(numbers) == 1:
            return f"第 {numbers[0]} 條"
        return "第 " + "、".join(numbers) + " 條"

    # ------------------------------------------------------------------
    # 內部：雜項
    # ------------------------------------------------------------------

    def _extract_title(self, lines: list[str], pdf_path: str) -> str:
        """優先讀 PDF 內的「法規名稱：」，讀不到才退回檔名。"""
        for line in lines[:20]:
            m = self.LAW_NAME_PATTERN.match(line)
            if m:
                return m.group(1).strip()

        name = os.path.splitext(os.path.basename(pdf_path))[0]
        return name.replace("_", " ").replace("-", " ") or "未知文件"

    def _generate_id(self, text: str) -> int:
        """生成唯一的 ID (使用整數)"""
        return int(hashlib.md5(text.encode("utf-8")).hexdigest()[:16], 16)


# ============================================================
# Embedding 服務
# ============================================================

class EmbeddingService:
    """
    Qwen3-Embedding 服務
    
    使用公司的 LLM proxy 生成向量
    """
    
    def __init__(self, endpoint: str, api_key: str, model: str = "Qwen/Qwen3-Embedding-0.6B"):
        self.endpoint = endpoint
        self.api_key = api_key
        self.model = model
    
    def generate_embedding(self, text: str) -> Optional[list[float]]:
        """
        生成單個文本的 embedding
        
        Args:
            text: 輸入文本
            
        Returns:
            向量列表，失敗時返回 None
        """
        try:
            response = requests.post(
                self.endpoint,
                headers={
                    "Content-Type": "application/json",
                    "Authorization": f"Bearer {self.api_key}",
                },
                json={
                    "model": self.model,
                    "input": text,
                },
                timeout=30,
            )
            response.raise_for_status()
            
            result = response.json()
            
            # 解析回應格式
            # 標準 OpenAI 格式: {"data": [{"embedding": [...]}]}
            if "data" in result and len(result["data"]) > 0:
                return result["data"][0]["embedding"]
            
            logger.warning(f"非預期的 API 回應格式: {result}")
            return None
            
        except requests.exceptions.RequestException as e:
            logger.error(f"Embedding 請求失敗: {e}")
            return None
        except (KeyError, IndexError, TypeError) as e:
            logger.error(f"解析 embedding 回應失敗: {e}")
            return None
    
    def generate_embeddings_batch(self, texts: list[str]) -> list[Optional[list[float]]]:
        """
        批量生成 embedding
        
        Args:
            texts: 文本列表
            
        Returns:
            向量列表列表
        """
        results = []
        for text in texts:
            embedding = self.generate_embedding(text)
            results.append(embedding)
        
        return results


# ============================================================
# Qdrant 服務
# ============================================================

class QdrantService:
    """Qdrant 向量資料庫服務"""
    
    COLLECTION_NAME = "legal_documents"
    VECTOR_SIZE = 1024  # Qwen3-Embedding-0.6B 使用 1024 維
    
    def __init__(self, host: str, port: int, api_key: str = ""):
        if api_key:
            self.client = QdrantClient(host=host, port=port, api_key=api_key)
        else:
            self.client = QdrantClient(host=host, port=port)
    
    def collection_exists(self, collection_name: str) -> bool:
        """檢查集合是否存在"""
        collections = self.client.get_collections()
        return any(c.name == collection_name for c in collections.collections)
    
    def create_collection(self, collection_name: str, vector_size: int = VECTOR_SIZE) -> bool:
        """創建向量集合"""
        if self.collection_exists(collection_name):
            logger.warning(f"集合 '{collection_name}' 已存在")
            return True
        
        try:
            self.client.create_collection(
                collection_name=collection_name,
                vectors_config=VectorParams(
                    size=vector_size,
                    distance=Distance.COSINE,
                ),
            )
            
            # 創建 payload 索引
            self.client.create_payload_index(
                collection_name=collection_name,
                field_name="articleNumber",
                field_schema=PayloadSchemaType.KEYWORD,
            )
            self.client.create_payload_index(
                collection_name=collection_name,
                field_name="chapter",
                field_schema=PayloadSchemaType.KEYWORD,
            )
            self.client.create_payload_index(
                collection_name=collection_name,
                field_name="domain",
                field_schema=PayloadSchemaType.KEYWORD,
            )
            
            logger.info(f"成功創建集合 '{collection_name}'")
            return True
            
        except Exception as e:
            logger.error(f"創建集合失敗: {e}")
            return False
    
    def upsert_documents(self, collection_name: str, points: list[PointStruct]) -> bool:
        """
        批量插入/更新文檔
        
        Args:
            collection_name: 集合名稱
            points: 點列表
            
        Returns:
            成功與否
        """
        if not points:
            logger.warning("沒有要插入的點")
            return False
        
        try:
            # Qdrant 建議每次最多 100-500 個點
            batch_size = 100
            for i in range(0, len(points), batch_size):
                batch = points[i:i + batch_size]
                self.client.upsert(
                    collection_name=collection_name,
                    points=batch,
                )
                logger.info(f"已插入批次 {i // batch_size + 1} ({len(batch)} 個點)")
            
            logger.info(f"成功插入 {len(points)} 個點")
            return True
            
        except Exception as e:
            logger.error(f"插入文檔失敗: {e}")
            return False
    
    def search(self, collection_name: str, query_vector: list[float], top_k: int = 5) -> list:
        """
        搜尋相似文檔
        
        Args:
            collection_name: 集合名稱
            query_vector: 查詢向量
            top_k: 返回結果數量
            
        Returns:
            搜尋結果列表
        """
        try:
            results = self.client.search(
                collection_name=collection_name,
                query_vector=query_vector,
                limit=top_k,
            )
            return results
        except Exception as e:
            logger.error(f"搜尋失敗: {e}")
            return []
    
    def get_collection_info(self, collection_name: str) -> dict:
        """獲取集合資訊"""
        try:
            info = self.client.get_collection(collection_name)
            return {
                "points_count": info.points_count,
                "vector_size": info.config.params.vectors.size,
                "distance": info.config.params.vectors.distance.name,
            }
        except Exception as e:
            logger.error(f"獲取集合資訊失敗: {e}")
            return {}
    
    def truncate_collection(self, collection_name: str) -> bool:
        """清空集合（刪除並重建）"""
        try:
            self.client.delete_collection(collection_name)
            self.create_collection(collection_name)
            logger.info(f"成功清空集合 '{collection_name}'")
            return True
        except Exception as e:
            logger.error(f"清空集合失敗: {e}")
            return False


# ============================================================
# 主要匯入邏輯
# ============================================================

class LegalPDFImporter:
    """
    法律 PDF 匯入器
    
    完整流程:
    1. 解析 PDF 檔案
    2. 提取法律條文結構
    3. 生成 embedding 向量
    4. 存入 Qdrant
    """
    
    def __init__(self, config: ImportConfig):
        self.config = config
        
        # 初始化組件
        self.pdf_parser = PDFParser(
            chunk_size=config.chunk_size,
            chunk_overlap=config.chunk_overlap,
        )
        self.embedding_service = EmbeddingService(
            endpoint=config.embedding_endpoint,
            api_key=config.embedding_api_key,
            model=config.embedding_model,
        )
        self.qdrant_service = QdrantService(
            host=config.qdrant_host,
            port=config.qdrant_port,
            api_key=config.qdrant_api_key,
        )
    
    def import_pdf(self, pdf_path: str) -> int:
        """
        匯入單一 PDF 檔案
        
        Returns:
            成功匯入的條文數量
        """
        logger.info(f"開始匯入: {pdf_path}")
        
        # 1. 解析 PDF
        documents = self.pdf_parser.parse_pdf(pdf_path)
        if not documents:
            logger.warning(f"從 {pdf_path} 未提取到任何條文")
            return 0
        
        # 2. 準備 Qdrant 集合
        self.qdrant_service.create_collection(self.config.collection_name)
        
        # 3. 生成 embedding 並準備點
        points = []
        successful_count = 0
        
        for doc in documents:
            content = doc["content"]
            
            # 生成 embedding
            embedding = self.embedding_service.generate_embedding(content)
            
            if embedding is None:
                logger.warning(f"無法生成 embedding，跳過: {doc['article'] or doc['title']}")
                continue
            
            # 確定 domain (公司法 or 民法)
            domain = self._detect_domain(doc)
            
            # 創建 Qdrant point
            point = PointStruct(
                id=doc["id"],
                vector=embedding,
                payload={
                    "title": doc["title"],
                    # content 已包含條號那一行，後端直接拿它當片段文字與快取鍵，
                    # 不可再於前面補上任何隨位置變動的標記（見 PDFParser 的說明）
                    "content": content,
                    "chapter": doc.get("chapter", ""),
                    "articleNumber": doc.get("article", ""),
                    "articles": doc.get("articles", []),
                    "nChars": doc.get("n_chars", len(content)),
                    "domain": domain,
                    "sourceFile": os.path.basename(pdf_path),
                },
            )
            points.append(point)
            successful_count += 1
            
            # 每 batch_size 個點批次插入
            if len(points) >= self.config.batch_size:
                self.qdrant_service.upsert_documents(
                    self.config.collection_name, points
                )
                points = []
        
        # 插入剩餘的點
        if points:
            self.qdrant_service.upsert_documents(
                self.config.collection_name, points
            )
        
        logger.info(f"完成匯入 {pdf_path}: {successful_count}/{len(documents)} 個條文")
        return successful_count
    
    def import_directory(self, directory: str) -> dict:
        """
        匯入目錄中所有 PDF 檔案
        
        Args:
            directory: PDF 目錄路徑
            
        Returns:
            匯入統計結果
        """
        pdf_dir = Path(directory)
        
        if not pdf_dir.exists():
            logger.error(f"目錄不存在: {directory}")
            return {"error": f"目錄不存在: {directory}"}
        
        pdf_files = list(pdf_dir.glob("*.pdf"))
        if not pdf_files:
            logger.warning(f"在 {directory} 中找不到 PDF 檔案")
            return {"error": f"在 {directory} 中找不到 PDF 檔案"}
        
        logger.info(f"找到 {len(pdf_files)} 個 PDF 檔案")
        
        # 初始化集合
        self.qdrant_service.create_collection(self.config.collection_name)
        
        # 匯入所有 PDF
        total_documents = 0
        results = {}
        
        for pdf_file in pdf_files:
            try:
                count = self.import_pdf(str(pdf_file))
                results[pdf_file.name] = count
                total_documents += count
            except Exception as e:
                logger.error(f"匯入 {pdf_file.name} 失敗: {e}")
                results[pdf_file.name] = f"錯誤: {e}"
        
        # 輸出統計
        collection_info = self.qdrant_service.get_collection_info(
            self.config.collection_name
        )
        
        stats = {
            "total_files": len(pdf_files),
            "total_documents": total_documents,
            "collection_points": collection_info.get("points_count", "N/A"),
            "file_results": results,
        }
        
        logger.info("=" * 50)
        logger.info("匯入完成!")
        logger.info(f"  檔案數量: {len(pdf_files)}")
        logger.info(f"  條文數量: {total_documents}")
        logger.info(f"  Qdrant 點數量: {collection_info.get('points_count', 'N/A')}")
        logger.info("=" * 50)
        
        return stats
    
    def _detect_domain(self, doc: dict) -> str:
        """
        偵測文件領域（公司法 or 民法）
        
        根據標題和章節內容判斷
        """
        title = doc.get("title", "").lower()
        chapter = doc.get("chapter", "").lower()
        content = doc.get("content", "").lower()
        
        # 公司法關鍵字
        company_keywords = ["公司法", "公司", "有限公司", "股份有限公司", "股東", "董事", "監察人"]
        # 民法關鍵字
        civil_keywords = ["民法", "民法總則", "債編", "物權編", "親屬編", "繼承編"]
        
        for keyword in company_keywords:
            if keyword in title or keyword in chapter:
                return "company"
        
        for keyword in civil_keywords:
            if keyword in title or keyword in chapter:
                return "civil"
        
        # 根據條文內容判斷
        article_indicators_company = ["公司登記", "股份", "資本", "股東會"]
        article_indicators_civil = ["債權", "債務", "契約", "侵權", "所有權"]
        
        for indicator in article_indicators_company:
            if indicator in content:
                return "company"
        
        for indicator in article_indicators_civil:
            if indicator in content:
                return "civil"
        
        return "unknown"


# ============================================================
# 互動式工具函數
# ============================================================

def clear_collection(config: ImportConfig):
    """清空現有集合"""
    qdrant = QdrantService(
        host=config.qdrant_host,
        port=config.qdrant_port,
        api_key=config.qdrant_api_key,
    )
    
    if qdrant.collection_exists(config.collection_name):
        response = input(f"確定要清空集合 '{config.collection_name}' 嗎？(y/N): ")
        if response.lower() == 'y':
            qdrant.truncate_collection(config.collection_name)
        else:
            print("已取消")
    else:
        print(f"集合 '{config.collection_name}' 不存在")


def check_collection(config: ImportConfig):
    """檢查集合狀態"""
    qdrant = QdrantService(
        host=config.qdrant_host,
        port=config.qdrant_port,
        api_key=config.qdrant_api_key,
    )
    
    if qdrant.collection_exists(config.collection_name):
        info = qdrant.get_collection_info(config.collection_name)
        print(f"集合 '{config.collection_name}' 資訊:")
        for key, value in info.items():
            print(f"  {key}: {value}")
    else:
        print(f"集合 '{config.collection_name}' 不存在")


def test_embedding(config: ImportConfig):
    """測試 embedding API"""
    service = EmbeddingService(
        endpoint=config.embedding_endpoint,
        api_key=config.embedding_api_key,
    )
    
    test_text = "公司法第1條規定"
    print(f"測試 embedding API: {test_text}")
    
    embedding = service.generate_embedding(test_text)
    
    if embedding:
        print(f"成功! 向量維度: {len(embedding)}")
    else:
        print("失敗! 請檢查 API Key 和網路連線")


# ============================================================
# 主程式
# ============================================================

def main():
    parser = argparse.ArgumentParser(
        description="法律 PDF 檔案匯入 Qdrant 向量資料庫工具",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
使用範例:
  # 匯入單一 PDF
  python import_legal_pdfs.py --file ./公司法.pdf

  # 匯入整個目錄的 PDF
  python import_legal_pdfs.py --pdf-dir ./legal_pdfs

  # 清空現有集合
  python import_legal_pdfs.py --clear

  # 檢查集合狀態
  python import_legal_pdfs.py --check

  # 測試 Embedding API
  python import_legal_pdfs.py --test-embedding
        """,
    )
    
    # 輸入模式
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--file", type=str, help="單一 PDF 檔案路徑")
    mode.add_argument("--pdf-dir", type=str, help="包含 PDF 檔案的目錄")
    mode.add_argument("--clear", action="store_true", help="清空現有集合")
    mode.add_argument("--check", action="store_true", help="檢查集合狀態")
    mode.add_argument("--test-embedding", action="store_true", help="測試 Embedding API")
    
    # 連接配置
    parser.add_argument("--qdrant-host", default="localhost", help="Qdrant 主機 (預設: localhost)")
    parser.add_argument("--qdrant-port", type=int, default=6333, help="Qdrant 端口 (預設: 6333)")
    parser.add_argument("--qdrant-api-key", default="", help="Qdrant API Key")
    parser.add_argument("--collection", default="legal_documents", help="集合名稱 (預設: legal_documents)")
    
    # Embedding 配置
    parser.add_argument("--embedding-endpoint", default="https://ainexus.phison.com/api/external/v1/embeddings")
    parser.add_argument("--embedding-model", default="Qwen/Qwen3-Embedding-0.6B")
    parser.add_argument("--embedding-api-key", help="Embedding API Key (或從 .env 檔案載入)")
    
    args = parser.parse_args()
    
    # 從 .env 或環境變數載入 API Key (如果沒有從命令列提供)
    embedding_api_key = args.embedding_api_key or os.getenv("EMBEDDING_API_KEY", "")
    if not embedding_api_key:
        logger.error("請提供 Embedding API Key (--embedding-api-key 參數或 .env 檔案中的 EMBEDDING_API_KEY)")
        return
    
    # 建立配置
    config = ImportConfig(
        qdrant_host=args.qdrant_host,
        qdrant_port=args.qdrant_port,
        qdrant_api_key=args.qdrant_api_key,
        collection_name=args.collection,
        embedding_endpoint=args.embedding_endpoint,
        embedding_model=args.embedding_model,
        embedding_api_key=embedding_api_key,   # 已含 .env / 環境變數的後備
    )
    
    # 執行操作
    if args.test_embedding:
        test_embedding(config)
        return
    
    if args.clear:
        clear_collection(config)
        return
    
    if args.check:
        check_collection(config)
        return
    
    importer = LegalPDFImporter(config)
    
    if args.file:
        # 匯入單一檔案
        result = importer.import_pdf(args.file)
        print(f"匯入完成: {result} 個條文")
    
    elif args.pdf_dir:
        # 匯入整個目錄
        stats = importer.import_directory(args.pdf_dir)
        print(json.dumps(stats, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()
