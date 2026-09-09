# ChatBot Service

一個使用 C# (ASP.NET Core) 後端和 Vue 3 前端的法律領域聊天機器人應用服務，支援 RAG (Retrieval-Augmented Generation) 功能，可打包成 Docker 映像並在 Kubernetes (Rancher Desktop) 中執行。

## 專案結構

```
├── backend/
│   └── ChatBot.Api/              # ASP.NET Core Web API
│       ├── ChatBot.Api.csproj
│       ├── Program.cs
│       ├── Controllers/
│       │   ├── ChatController.cs
│       │   └── KnowledgeBaseController.cs
│       ├── Models/
│       │   ├── ChatMessage.cs
│       │   ├── KnowledgeDocument.cs
│       │   └── KnowledgeRequest.cs
│       ├── Services/
│       │   ├── ChatBotService.cs
│       │   ├── QdrantService.cs
│       │   ├── EmbeddingService.cs
│       │   ├── IPDFParser.cs
│       │   └── PDFParser.cs
│       ├── Configuration/
│       │   └── RAGSettings.cs
│       ├── appsettings.json
│       ├── Dockerfile
│       └── ...
├── frontend/                     # Vue 3 + Vite
│   ├── package.json
│   ├── vite.config.js
│   ├── index.html
│   ├── Dockerfile
│   ├── nginx.conf
│   └── src/
│       ├── main.js
│       └── App.vue
├── k8s/                          # Kubernetes manifests
│   ├── backend.yaml
│   ├── frontend.yaml
│   ├── qdrant.yaml
│   └── ingress.yaml
├── docker-compose.yml            # Local Docker development
└── README.md
```

## 功能特色

### 🤖 法律諮詢聊天機器人
- 專注於中華民國**民法**與**公司法**領域
- 關鍵字比對提供法律條文參考
- 免責聲明提醒用戶諮詢專業律師

### 📚 RAG (Retrieval-Augmented Generation) 功能
- **向量資料庫**: 使用 Qdrant 儲存法律條文向量
- **PDF 解析**: 自動解析法律文件並提取條文結構
- **向量化搜尋**: 使用 Embedding 技術實現語意搜尋
- **知識庫管理**: 提供 API 上傳、刪除、搜尋法律文件

## 快速開始

### 1. 本地開發 (使用 Docker Compose)

> **注意**: 使用 Rancher Desktop 時，請先在設定中將 Container Runtime 切換為 **Docker Engine**（不是 containerd），才能使用 docker compose 命令。

```bash
# 建構並啟動所有服務（包含 Qdrant）
docker compose up --build

# 前端: http://localhost:80
# 後端 API: http://localhost:8000
# Qdrant UI: http://localhost:6333
```

**服務說明：**
| 服務 | 連接埠 | 說明 |
|------|--------|------|
| Frontend | 80 | Vue 3 前端介面 |
| Backend | 8000 | ASP.NET Core API |
| Qdrant | 6333 | 向量資料庫 HTTP API |
| Qdrant | 6334 | 向量資料庫 gRPC |

**如果仍然無法執行，可以手動分別建構:**

```bash
# 1. 建構後端映像
cd backend/ChatBot.Api
docker build -t chatbot-backend:latest .

# 2. 建構前端映像
cd ../../frontend
docker build -t chatbot-frontend:latest .

# 3. 手動啟動容器
cd ../..
docker run -d --name chatbot-backend -p 8000:8000 chatbot-backend:latest
docker run -d --name chatbot-frontend -p 80:80 chatbot-frontend:latest
```

### 2. 本地開發 (不使用 Docker)

**後端:**
```bash
cd backend/ChatBot.Api
dotnet restore
dotnet run
# API: http://localhost:8000
# Swagger: http://localhost:8000/swagger
```

**前端:**
```bash
cd frontend
npm install
npm run dev
# 前端: http://localhost:5173
```

### 3. 部署到 Rancher Desktop (Kubernetes)

```bash
# 1. 啟動 Rancher Desktop 並啟用 Kubernetes

# 2. 建構 Docker 映像 (在 Rancher Desktop 中使用 containerd)
docker build -t chatbot-backend:latest ./backend/ChatBot.Api
docker build -t chatbot-frontend:latest ./frontend

# 3. 部署到 Kubernetes
kubectl apply -f k8s/qdrant.yaml

# 3.1 先用真實的 API Key 建立/更新 Secret，
#     不要直接套用 k8s/backend.yaml 內建的 REPLACE_ME 佔位值
kubectl create secret generic chatbot-backend-secret \
  --from-literal=RAGSettings__EmbeddingApiKey=<your-embedding-api-key> \
  --from-literal=RAGSettings__LlmApiKey=<your-llm-api-key> \
  --dry-run=client -o yaml | kubectl apply -f -

kubectl apply -f k8s/backend.yaml
kubectl apply -f k8s/frontend.yaml
kubectl apply -f k8s/ingress.yaml

# 4. 確認部署狀態
kubectl get pods
kubectl get services
kubectl get ingress

# 5. 查看 Pod 日誌
kubectl logs -l app=chatbot-backend
kubectl logs -l app=chatbot-frontend
```

### 4. 使用 Rancher GUI 部署

1. 開啟 Rancher Desktop
2. 啟動 Kubernetes
3. 使用 `kubectl apply -f k8s/` 部署，或在 Rancher UI 中:
   - 進入 Projects → Namespace
   - 使用 "Deploy to Cluster" 上傳 YAML
   - 或使用 "Workload" 建立 Deployment

## API Endpoints

### 聊天 API

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/chat/health` | 健康檢查 |
| POST | `/api/chat` | 發送訊息 |

### 知識庫管理 API

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/knowledgebase/upload` | 上傳 PDF 文件 |
| GET | `/api/knowledgebase/documents` | 列出所有文件（尚未實作，回傳 501，請改用 search） |
| GET | `/api/knowledgebase/search?query=xxx` | 搜尋知識庫 |
| DELETE | `/api/knowledgebase/documents/{id}` | 刪除文件 |
| DELETE | `/api/knowledgebase/clear` | 清空知識庫 |

### 發送訊息範例

```bash
curl -X POST http://localhost:8000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "買賣契約的賣方有什麼義務？"}'
```

### 上傳 PDF 範例

```bash
curl -X POST http://localhost:8000/api/knowledgebase/upload \
  -F "file=@civil_law.pdf" \
  -F "title=民法總則" \
  -F "domain=民法"
```

### 搜尋知識庫範例

```bash
curl "http://localhost:8000/api/knowledgebase/search?query=消滅時效&topK=5"
```

## RAG 功能說明

### 工作流程

```
┌─────────────┐     ┌──────────────┐     ┌─────────────┐
│  PDF 文件   │ ──▶ │ PDF 解析器   │ ──▶ │ 條文提取    │
└─────────────┘     └──────────────┘     └─────────────┘
                                                │
                                                ▼
┌─────────────┐     ┌──────────────┐     ┌─────────────┐
│  用戶問題   │ ──▶ │ Embedding    │ ──▶ │ Qdrant 搜尋 │
│  回覆結果   │ ◀── │ LLM 組合    │     │ 相似條文    │
└─────────────┘     └──────────────┘     └─────────────┘
```

### 配置說明

在 [`appsettings.Development.json`](backend/ChatBot.Api/appsettings.Development.json) 中配置 RAG 設定：

```json
{
  "RAGSettings": {
    "QdrantHost": "qdrant",
    "QdrantPort": 6333,
    "EmbeddingApiKey": "your-api-key-here",
    "EmbeddingEndpoint": "https://api.openai.com/v1/embeddings",
    "EmbeddingModel": "text-embedding-ada-002",
    "EmbeddingDimensions": 1536
  }
}
```

### 環境變數

| 變數 | 說明 | 預設值 |
|------|------|--------|
| `QDRANT_HOST` | Qdrant 主機位址 | `qdrant` |
| `QDRANT_PORT` | Qdrant Port | `6333` |
| `EMBEDDING_API_KEY` | Embedding API 金鑰 | `your-api-key-here` |

## 技術堆疊

- **後端**: .NET 8 ASP.NET Core Web API
- **前端**: Vue 3 + Vite + Axios
- **向量資料庫**: Qdrant
- **Embedding**: OpenAI text-embedding-ada-002
- **PDF 解析**: iText7
- **容器化**: Docker (多階段建構)
- **編排**: Kubernetes (Rancher Desktop / RKE2)
- **反向代理**: Nginx (前端)

## 擴充開發

### 串接真實 AI API

修改 [`backend/ChatBot.Api/Services/ChatBotService.cs`](backend/ChatBot.Api/Services/ChatBotService.cs) 中的 `GetReplyAsync` 方法，整合 OpenAI、Azure OpenAI 或其他 AI 服務，將搜尋到的法律條文作為 Context 發送給 LLM。

### 增加環境變數設定

在 `appsettings.json` 中增加可組態的設定:

```json
{
  "AI": {
    "Provider": "OpenAI",
    "ApiKey": "",
    "Model": "gpt-3.5-turbo"
  },
  "RAGSettings": {
    "QdrantHost": "qdrant",
    "QdrantPort": 6333,
    "EmbeddingApiKey": "",
    "EmbeddingEndpoint": "https://api.openai.com/v1/embeddings",
    "EmbeddingModel": "text-embedding-ada-002",
    "EmbeddingDimensions": 1536
  }
}
```

## License

MIT
