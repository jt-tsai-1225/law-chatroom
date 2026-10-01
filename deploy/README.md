# 部署

全套服務以 `docker-compose.yml` 定義。各項設定為什麼是現在這個值，
註解寫在 compose 檔裡——**改動之前請先讀那些註解**，有幾項不是調效能而是避免服務崩潰。

## 首次部署

```bash
cd ~/law-chatroom

# 1. 網路（僅第一次）
docker network create lawnet

# 2. compose 變數：路徑與容量
cp .env.example .env
vi .env

# 3. 憑證（不在 repo 內）
cat > ~/.law-env <<'EOF'
RAGSettings__EmbeddingApiKey=<embedding gateway 金鑰>
RAGSettings__ObjectStorageBackend=seaweedfs
RAGSettings__S3ServiceUrl=http://law-seaweedfs:8333
RAGSettings__S3Bucket=law-documents
RAGSettings__S3AccessKey=lawchatroom
RAGSettings__S3SecretKey=<與 seaweed conf/s3.json 完全一致>
EOF
chmod 600 ~/.law-env
```

> ⚠ env 檔**不會去掉引號**，`=` 前後也不能有空白。
> 寫成 `KEY="abc"` 時值會包含引號本身，S3 簽章就過不了。

SeaweedFS 的憑證設定：

```bash
mkdir -p ~/law-chatroom-data/seaweed/{data,conf}
SECRET=$(openssl rand -hex 24)
echo "SECRET = $SECRET   # 同時填進 ~/.law-env 的 S3SecretKey"

cat > ~/law-chatroom-data/seaweed/conf/s3.json <<EOF
{
  "identities": [{
    "name": "law-app",
    "credentials": [{ "accessKey": "lawchatroom", "secretKey": "$SECRET" }],
    "actions": ["Read", "Write", "List", "Tagging", "Admin"]
  }]
}
EOF
chmod 700 ~/law-chatroom-data
chmod 600 ~/law-chatroom-data/seaweed/conf/s3.json
```

vLLM 補丁檔必須就位（**不可放 `/tmp`**，見 compose 檔的註解）：

```bash
ls ~/blend-patches/gpu_worker_v0240.py ~/blend-patches/lmc_models_utils_v0240.py
```

啟動：

```bash
docker compose up -d --build
```

`depends_on` + healthcheck 會處理順序：vLLM 就緒（模型載入一兩分鐘）→ 後端 →
前端。不再需要手動 `until curl ...; do sleep 3; done`。

首次還要建 bucket：

```bash
docker compose exec -T seaweedfs weed shell <<'EOF'
s3.bucket.create -name law-documents
s3.bucket.list
EOF
```

## 日常操作

| 要做什麼 | 指令 |
|---|---|
| 改了程式碼 | `git pull && docker compose up -d --build` |
| 改了 `.env` | `docker compose up -d` |
| 只重開某個服務 | `docker compose restart backend` |
| 看狀態 | `docker compose ps` |
| 看日誌 | `docker compose logs -f backend` |
| 全部停掉 | `docker compose down`（**資料在 bind mount，不會遺失**）|

`docker compose up -d --build` 會自己判斷哪些要重建、哪些要重建容器——
不必再記「build 完還要 rm -f 再 run」那套。

## 啟動後必做：KV 預熱

**每次 vLLM 重啟後都要跑一次。** in-process 模式下 LMCache 的 L2 索引存在記憶體，
重啟後磁碟上的 KV 檔案雖然完整卻讀不到，等同冷啟。

```bash
curl -s -X POST http://localhost:8080/api/knowledgebase/warmup \
  -H "Content-Type: application/json" -d '{}' | python3 -m json.tool
```

334 段冷啟約 35 秒；已暖過約 9 秒（幾乎全部命中）。`failedBatches` 要是 0。

> ⚠ 介面上的「已預熱」是**後端自己的送出紀錄推估**，不是向引擎查詢。
> vLLM 單獨重啟時它會錯誤地顯示已預熱——重跑預熱即可對齊。

## 驗收

```bash
docker compose ps

curl -s http://localhost:8080/health
curl -s http://localhost:8080/api/knowledgebase/documents | head -c 200

docker compose exec backend python3 -c "import pymupdf; print('pymupdf OK')"
docker compose logs backend 2>&1 | grep "LMCache 初始化"
```

最後一行必須是 **`啟用: False`**。那是 C# 自己那層的**回應字串快取**，
開著會讓第二次請求直接回舊答案、TTFT 假性歸零，效能量測全部失效。
它與 LMCache 無關，只是命名相近。

瀏覽器：`http://10.102.197.193:8080`（API 文件在 `/docs`）

## 對外只開一個埠

公司網路只放行 8080。前端的 nginx 代理 `/api` 與 `/docs` 到後端，
因此**後端不發佈主機埠**。vLLM、Qdrant、SeaweedFS 的主機埠只供在 193 上除錯。

## 重啟的連帶影響

| 重啟誰 | 後果 |
|---|---|
| **vLLM** | L1 KV 消失、L2 索引不重建 → **必須重新預熱** |
| **後端** | `ChunkCacheTracker` 清空。**KV 本身不受影響**，但介面會顯示「未預熱」、依快取重排暫時失效。重跑預熱即可對齊（全部命中，約 9 秒）|
| **Qdrant** | 資料在 bind mount，不受影響 |
| **SeaweedFS** | 資料在 bind mount，不受影響 |

## 容量上限

**可承載的語料量不是由記憶體決定，而是由「不得觸發淘汰」決定。**

L1 裝滿後開始淘汰，而淘汰路徑有缺陷（LMCache issue #4133，上游未修），
結果是整個引擎終止——不是效能下降，沒有優雅降級。

```
每 token 的 KV = 2 × 32 層 × 8 KV heads × 128 dim × 2 bytes = 128 KiB
所需容量 = 相異 token 數 × 128 KiB
```

公式已與實測對照過（2026/10/01）。預熱回報 `totalPromptTokens 249,905`、
`totalCachedTokens 15,510`（≈ 67 批 × 237 固定前綴），相異 token 為 234,395：

| | 值 |
|---|---|
| 理論 | 234,395 × 128 KiB = **28.6 GiB** |
| `du -sh /mnt/nvme0/mistral-fs` | **29G** |

| | |
|---|---|
| 目前 | 334 段、234,395 tokens、29 GB |
| 每段平均 | 702 tokens |
| `L1_SIZE_GB=50` 的上限 | 409,600 tokens ≈ **584 段** |
| 還能再加 | 約 250 段 |

目前用掉 58%。**新增法典前先用上表算過再調大 `.env` 的 `L1_SIZE_GB`，不要等它崩潰。**
加完語料後重跑預熱並 `du -sh` 對一次，是最快的驗證方式。

## 資料位置

全部在 git 儲存庫**之外**——資料庫檔案放進 repo 會被 `git checkout` / `git clean`
在運行中改寫（2026/09/24 的教訓）。

| 路徑 | 內容 |
|---|---|
| `~/qdrant_storage` | 向量與片段 |
| `~/law-chatroom-data/seaweed/` | 原始 PDF、S3 憑證 |
| `~/blend-patches/` | vLLM 補丁 |
| `~/.law-env` | 憑證 |
| `~/lawfiles/` | 舊的本機上傳目錄（切到 SeaweedFS 後不再寫入）|
| `/mnt/nvme0/mistral-model` | 模型權重 |
| `/mnt/nvme0/mistral-fs` | L2 KV 快取 |

備份就是備份這些目錄。

## 待辦

- [ ] 包成 Helm chart（compose 的服務名已與 k8s Service 名稱對齊）
- [ ] 偵測 vLLM 重啟並自動標記需重新預熱
- [ ] 自架 embedding（外部 gateway 偶發 10–14 秒，且是單點故障）
