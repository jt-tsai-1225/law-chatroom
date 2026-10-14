# ChatBot.Api 單元測試

## 執行

```bash
cd backend
./run-tests.sh          # Windows：.\run-tests.ps1
```

腳本會依序：`dotnet tool restore`（裝 ReportGenerator）→ `dotnet test` 並收集 Cobertura 覆蓋率 → 產生 HTML 報表。

報表位置：`backend/TestResults/CoverageReport/index.html`（`Summary.txt` 是文字摘要）。

## 原則

- 不打網路、不連資料庫：HttpClient 用 `StubHttpMessageHandler`，其餘相依用 Moq 或 `FakeTokenizer`。
- 數值都是固定規格（例如 `ChunkOrderStrategy` 的洗牌排列、`LMCacheService` 的 SHA-256），不靠隨機或時間。
- 只驗證「該類別自己決定的事」；外部服務的行為留給整合測試。

## 覆蓋率範圍

`coverlet.runsettings` 排除了下列項目，不列入覆蓋率分母：

| 排除項目 | 原因 |
|---|---|
| `Program.cs` | 啟動組態，無邏輯 |
| `QdrantService`、`SeaweedObjectStorage`、`ConversationStore` | 對外部服務的薄轉接層，需真實 Qdrant / S3 / PostgreSQL，屬整合測試範圍 |
| `LegalDocumentParser` | 目前未納入；要納入就從 `Exclude` 拿掉並補測試 |

要調整範圍直接改 `coverlet.runsettings`；排除越多，數字越好看但越不誠實，建議報告時一併列出排除清單。

## 尚未覆蓋

`LLMService`（SSE 解析）、`KnowledgeBaseController`、`DiagnosticsController`、`IngestionService`。
