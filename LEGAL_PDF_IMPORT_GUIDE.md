# 法律 PDF 匯入指南

將中華民國公司法和民法 PDF 檔案匯入 Qdrant 向量資料庫的完整指南。

## 系統架構

```
PDF 檔案 → PyMuPDF 解析 → Text Chunk → Qwen Embedding → Qdrant Vector DB
```

## 快速開始

### 1. 準備環境

```bash
# 安裝 Python 依賴
pip install -r requirements_import.txt

# 或者手動安裝
pip install pymupdf qdrant-client requests tqdm
```

### 2. 準備 PDF 檔案

將您的 PDF 檔案放在一個資料夾中，例如：

```
./legal_pdfs/
├── company_law.pdf      # 公司法
└── civil_law.pdf        # 民法
```

### 3. 執行匯入

```bash
# 測試 Embedding API 連線
python import_legal_pdfs.py --test-embedding --embedding-api-key YOUR_API_KEY

# 匯入單一 PDF
python import_legal_pdfs.py --file ./legal_pdfs/company_law.pdf --embedding-api-key YOUR_API_KEY

# 匯入整個目錄
python import_legal_pdfs.py --pdf-dir ./legal_pdfs --embedding-api-key YOUR_API_KEY

# 檢查匯入結果
python import_legal_pdfs.py --check
```

## 完整參數說明

```bash
python import_legal_pdfs.py [選項]

基本選項:
  --file FILE             單一 PDF 檔案路徑
  --pdf-dir DIR           包含 PDF 檔案的目錄
  --clear                 清空現有集合
  --check                 檢查集合狀態
  --test-embedding        測試 Embedding API

Qdrant 連接:
  --qdrant-host HOST      Qdrant 主機 (預設: localhost)
  --qdrant-port PORT      Qdrant 端口 (預設: 6333)
  --qdrant-api-key KEY    Qdrant API Key (如果有的話)
  --collection NAME       集合名稱 (預設: legal_documents)

Embedding 配置:
  --embedding-endpoint URL   Embedding API 端點
  --embedding-model MODEL    Embedding 模型名稱
  --embedding-api-key KEY    Embedding API Key (必需)
```

## 使用 Docker 環境

如果您的服務運行在 Docker 中，需要使用 `chatbot-backend` 作為 host:

```bash
# 從 Docker 容器內匯入
docker exec -it chatbot-backend bash
pip install pymupdf qdrant-client requests
python import_legal_pdfs.py --pdf-dir /app/legal_pdfs \
    --qdrant-host qdrant \
    --embedding-api-key YOUR_API_KEY
```

或者從主機使用 Docker 網路:

```bash
python import_legal_pdfs.py --pdf-dir ./legal_pdfs \
    --qdrant-host host.docker.internal \
    --embedding-api-key YOUR_API_KEY
```

## 匯入流程說明

### 1. PDF 解析

使用 PyMuPDF (fitz) 解析 PDF 檔案，提取:
- 全文文字
- 章節結構 (章、節、條)
- 頁碼資訊

### 2. 法律條文提取

自動識別並提取以下結構:
- **章**: 例如 "第一條 總則編"
- **節**: 例如 "第一節 一般規定"
- **條**: 例如 "第一條"

### 3. Embedding 生成

使用 Qwen/Qwen3-Embedding-0.6B 模型生成 1024 維向量。

### 4. Qdrant 儲存

將向量和元數據存入 Qdrant，包含以下 payload:
```json
{
  "title": "公司法",
  "content": "條文內容...",
  "chapter": "總則編",
  "articleNumber": "第一條",
  "domain": "company",
  "sourceFile": "company_law.pdf"
}
```

## 常見問題

### Q1: Embedding API 連線失敗

**問題**: 無法連接到 `https://ainexus.phison.com/api/external/v1/embeddings`

**解決方案**:
1. 確認 API Key 正確
2. 檢查網路連線
3. 確認 API 服務正常運行

### Q2: PDF 無法解析

**問題**: PDF 檔案是掃描版或影像檔

**解決方案**:
- 使用 OCR 處理影像型 PDF
- 或者使用文字型 PDF 檔案

### Q3: 向量維度不匹配

**問題**: Qdrant 集合創建時的向量維度與 embedding 輸出不一致

**解決方案**:
- Qwen3-Embedding-0.6B 輸出 1024 維向量
- 確保集合創建時使用正確的 vector_size

## API 端點

您的 Embedding API 詳細資訊:

- **Endpoint**: `https://ainexus.phison.com/api/external/v1/embeddings`
- **Model**: `Qwen/Qwen3-Embedding-0.6B`
- **Vector Dimensions**: 1024

API 請求格式:
```bash
curl "https://ainexus.phison.com/api/external/v1/embeddings" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -d '{
    "model": "Qwen/Qwen3-Embedding-0.6B",
    "input": "測試文本"
  }'
```

## Qdrant 集合管理

### 查看集合資訊

```bash
python import_legal_pdfs.py --check
```

### 清空集合

```bash
python import_legal_pdfs.py --clear
```

### 使用 Qdrant Dashboard

如果有安裝 Qdrant Dashboard:
```
http://localhost:6333/dashboard
```

## 後端 API 整合

後端已經有現有的 API 可以管理知識庫:

### 新增法律條文

```bash
curl -X POST http://localhost:8000/api/knowledgebase \
  -H "Content-Type: application/json" \
  -d '{
    "title": "公司法",
    "content": "第一條 本法所稱公司，謂以設立股份有限公司為...",
    "chapter": "總則編",
    "articleNumber": "第一條"
  }'
```

### 搜尋知識庫

```bash
curl "http://localhost:8000/api/knowledgebase/search?query=股東權利&q=5"
```

### 清空知識庫

```bash
curl -X DELETE http://localhost:8000/api/knowledgebase/clear
```

## 建議的最佳實踐

1. **分批匯入**: 大檔案建議分批匯入，方便追蹤進度
2. **使用 domain 標籤**: 匯入時自動偵測領域 (company/civil)
3. **檢查結果**: 匯入後使用 `--check` 確認資料數量
4. **備份元數據**: 定期備份 Qdrant 儲存資料

## 技術細節

### Embedding 模型

| 屬性 | 值 |
|------|-----|
| 模型 | Qwen/Qwen3-Embedding-0.6B |
| 向量維度 | 1024 |
| 距離函數 | Cosine |
| 端點 | https://ainexus.phison.com/api/external/v1/embeddings |

### Qdrant 集合配置

| 屬性 | 值 |
|------|-----|
| 集合名稱 | legal_documents |
| 向量大小 | 1024 |
| 距離函數 | Cosine |
| 索引 | Keyword索引 (ArticleNumber, Chapter, Domain) |

## 下一步

1. 匯入您的 PDF 檔案
2. 測試聊天機器人是否能正確回答法律問題
3. 根據需要調整 chunk size 和 topK 參數
4. 考慮建立多個集合 (例如: company_docs, civil_docs) 來分隔不同領域
