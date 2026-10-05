# 部署

全套服務以 `docker-compose.yml` 定義。各項設定為什麼是現在這個值，
註解寫在 compose 檔裡——**改動之前請先讀那些註解**，有幾項不是調效能而是避免服務崩潰。

## 服務一覽

| 服務 | 容器名 | 用途 | 主機埠 |
|---|---|---|---|
| `frontend` | `law-frontend` | 靜態頁面 + 代理 `/api`、`/docs` 到後端 | **8080（唯一對外）** |
| `backend` | `law-backend` | API、檢索、組 prompt、聊天紀錄 | 不發佈 |
| `vllm` | `blend-vllm-024` | **CacheBlend** 引擎 | 8000（僅供除錯） |
| `vllm-lmcache` | `lmcache-vllm` | **純 LMCache**（前綴快取）引擎 | 8001（僅供除錯） |
| `postgres` | `law-postgres` | 聊天室與訊息紀錄 | 不發佈 |
| `qdrant` | `law-qdrant` | 向量與片段 | 6333、6334 |
| `seaweedfs` | `law-seaweedfs` | 原始 PDF（S3） | 8334 等 |
| （按需） | `nocache-vllm` | **無快取**引擎，由 `switch-nocache.sh` 管理，不在 compose 內 | 8002 |

三個推論端點對應介面上的三種快取模式。**兩個引擎常駐、第三個按需**：
Mistral-7B fp16 的權重每份約 14 GB，三份加起來連 KV 池都放不下 48 GB 的卡。
全部都在 **GPU 0** 上（193 是共用機器，只能用這一張）。

## 首次部署

```bash
cd ~/law-chatroom

# 1. 網路（僅第一次）
docker network create lawnet

# 2. compose 變數：路徑與容量
cp .env.example .env
vi .env

# 3. 憑證（不在 repo 內）
cat > ~/.law-env <<'EOT'
RAGSettings__EmbeddingApiKey=<embedding gateway 金鑰>
RAGSettings__ObjectStorageBackend=seaweedfs
RAGSettings__S3ServiceUrl=http://law-seaweedfs:8333
RAGSettings__S3Bucket=law-documents
RAGSettings__S3AccessKey=lawchatroom
RAGSettings__S3SecretKey=<與 seaweed conf/s3.json 完全一致>
POSTGRES_PASSWORD=<openssl rand -hex 24 產生>
EOT
chmod 600 ~/.law-env
```

> ⚠ env 檔**不會去掉引號**，`=` 前後也不能有空白。
> 寫成 `KEY="abc"` 時值會包含引號本身，S3 簽章就過不了。

### ⚠ 每個 `docker compose` 指令都要帶 `--env-file`

`POSTGRES_PASSWORD` 在 compose 檔裡是**必填**（沒設定就拒絕啟動，刻意的：預設密碼比沒密碼更糟），
但 `docker compose` **不會自己去讀 `~/.law-env`**——`.env` 只提供路徑與容量。
沒帶的話會得到「請在 .env 或 ~/.law-env 設定 POSTGRES_PASSWORD」而整個指令失敗。

建議在 `~/.bashrc` 定義一次：

```bash
export DC="docker compose --env-file $HOME/.law-env"
```

下面的指令一律寫成 `$DC`。

### SeaweedFS 的憑證設定

```bash
mkdir -p ~/law-chatroom-data/seaweed/{data,conf}
SECRET=$(openssl rand -hex 24)
echo "SECRET = $SECRET   # 同時填進 ~/.law-env 的 S3SecretKey"

cat > ~/law-chatroom-data/seaweed/conf/s3.json <<EOT
{
  "identities": [{
    "name": "law-app",
    "credentials": [{ "accessKey": "lawchatroom", "secretKey": "$SECRET" }],
    "actions": ["Read", "Write", "List", "Tagging", "Admin"]
  }]
}
EOT
chmod 700 ~/law-chatroom-data
chmod 600 ~/law-chatroom-data/seaweed/conf/s3.json
```

### 其他前置

vLLM 補丁檔必須就位（**不可放 `/tmp`**，見 compose 檔的註解）：

```bash
ls ~/blend-patches/gpu_worker_v0240.py ~/blend-patches/lmc_models_utils_v0240.py
```

兩個引擎各有一份 L1（CPU 記憶體）與各自的 L2 目錄，啟動前確認：

```bash
free -g                                   # CPU 記憶體：兩份 L1，334 段約需 58 GB 以上
mkdir -p /mnt/nvme0/mistral-fs /mnt/nvme0/mistral-fs-lmcache
mkdir -p ~/law-chatroom-data/postgres
```

### 啟動

```bash
$DC up -d --build
```

**啟動順序**（`depends_on` + healthcheck 處理，不需要手動等）：

```
postgres ─┐
vllm（CacheBlend，載入約 3.5 分鐘）─┬─► backend ─► frontend
                                   └─► vllm-lmcache（等 vllm healthy 才開始載入，再約 3.5 分鐘）
```

兩個引擎**刻意序列啟動**：vLLM 以「載入前後的 GPU 記憶體差」估算自己的用量，
同一張卡上另一個實例同時在配置記憶體，會被算成自己的，結果 KV 池被扣成零，
啟動失敗（「No available memory for the cache blocks」）。
後端不等第二個引擎（`service_started`）——它不是問答的必要條件，沒起來時
後端只會把「純 LMCache」標成不可用。因此**整套全部就緒要約 7 分鐘**（估計值），
但介面幾分鐘內就能用 CacheBlend。

首次還要建 bucket：

```bash
$DC exec -T seaweedfs weed shell <<'EOT'
s3.bucket.create -name law-documents
s3.bucket.list
EOT
```

聊天室的資料表由後端啟動時自動建立（冪等 DDL），不需要手動建表或跑 migration。

## 日常操作

| 要做什麼 | 指令 |
|---|---|
| 改了程式碼 | `git pull && $DC up -d --build` |
| 改了 `.env` | `$DC up -d` |
| 只重開某個服務 | `$DC restart backend` |
| 看狀態 | `$DC ps` |
| 看日誌 | `$DC logs -f backend` |
| 全部停掉 | `$DC down`（**資料在 bind mount，不會遺失**）|

> ⚠ **`$DC up -d backend` 會動到別的容器。** 後端 `depends_on` 兩個引擎，
> 不加 `--no-deps` 時 compose 會把停著的引擎一併拉起來。
> 只想換後端、不想動引擎時：`$DC up -d --no-deps backend`。
> （`switch-nocache.sh` 就是因為漏了這一點，曾把剛停掉的 LMCache 又拉起來，
> 三個引擎同時搶 GPU 0。）

## 快取模式與「無快取」切換

介面可選三種模式，由請求的 `cacheMode` 決定後端打哪個端點：

| 模式 | 端點 | 狀態 |
|---|---|---|
| CacheBlend | `blend-vllm-024:8000` | 常駐 |
| 純 LMCache | `lmcache-vllm:8000` | 常駐 |
| 無快取 | `nocache-vllm:8000` | 按需，預設**未設定** |

**不可用的模式會回 503**，不會靜默退回 CacheBlend——否則使用者會以為在比較兩種快取，
實際打的是同一個引擎。可用性由後端探測各端點的 `/v1/models`（結果快取 15 秒），
介面據此把連不到的選項標成「未啟動」：

```bash
curl -s http://localhost:8080/api/cachemodes | python3 -m json.tool | grep -E '"mode"|reachable'
```

要用「無快取」時，**必須先讓出純 LMCache 的記憶體**，整個流程收在一支腳本：

```bash
./deploy/switch-nocache.sh on       # 停 LMCache → 起無快取（埠 8002）→ 寫端點 → 重建後端
./deploy/switch-nocache.sh off      # 停無快取 → 起回 LMCache → 移除端點 → 重建後端
./deploy/switch-nocache.sh status
```

注意：
- 腳本會讀 `~/.law-env`，並且一律以 `--no-deps` 重建後端（理由見上）。
- `on` 會把 `RAGSettings__LlmEndpoints__none=...` 寫進 `~/.law-env`，`off` 會刪掉。
- **`off` 之後 LMCache 的快取是空的**（引擎重啟），會隨提問重新累積。
- 切換約需 4 分鐘（主要是載入模型，約 3.5 分鐘），腳本會印出等待進度，**不要中途中斷**——
  `docker run -d` 起的容器會在背景繼續載入，狀態會變得難以判斷。

## 啟動後必做：KV 預熱

**每次 CacheBlend 引擎重啟後都要跑一次。** in-process 模式下 LMCache 的 L2 索引存在記憶體，
重啟後磁碟上的 KV 檔案雖然完整卻讀不到，等同冷啟。

```bash
curl -s -X POST http://localhost:8080/api/knowledgebase/warmup \
  -H "Content-Type: application/json" -d '{}' | python3 -m json.tool
```

334 段冷啟約 35–40 秒；已暖過約 9–13 秒（幾乎全部命中）。`failedBatches` 要是 0。

> ⚠ **預熱只對 CacheBlend 有效。** 預熱請求不帶快取模式，永遠只打 CacheBlend。
> 純 LMCache 是前綴快取，預熱用的是多片段串接的 prompt，與日後逐題組出的前綴不同，
> 預熱不了——它的快取靠實際提問自然累積，冷啟後第一題較慢是正常的。

> ⚠ 介面上的「已預熱」是**後端自己的送出紀錄推估**，不是向引擎查詢。
> vLLM 單獨重啟時它會錯誤地顯示已預熱——重跑預熱即可對齊。

## 驗收

```bash
$DC ps                                    # 全部 healthy；lmcache-vllm 可能還在 starting

curl -s http://localhost:8080/health
curl -s http://localhost:8080/api/knowledgebase/documents | head -c 200
curl -s http://localhost:8080/api/cachemodes | python3 -m json.tool | grep -E '"mode"|reachable'
# 預期：cacheblend true、lmcache true、none false（未切換時正常）

$DC exec postgres psql -U lawchat lawchat -c '\dt'      # conversations、messages 兩張表
$DC exec backend python3 -c "import pymupdf; print('pymupdf OK')"
$DC logs backend 2>&1 | grep "LMCache 初始化"
```

最後一行必須是 **`啟用: False`**。那是 C# 自己那層的**回應字串快取**，
開著會讓第二次請求直接回舊答案、TTFT 假性歸零，效能量測全部失效。
它與 LMCache 無關，只是命名相近。

瀏覽器：`http://10.102.197.193:8080`（API 文件在 `/docs`）

## 對外只開一個埠

公司網路只放行 8080。前端的 nginx 代理 `/api` 與 `/docs` 到後端，
因此**後端不發佈主機埠**。引擎、Qdrant、SeaweedFS 的主機埠只供在 193 上除錯；
PostgreSQL 完全不發佈，要查資料用 `$DC exec postgres psql ...`。

> ⚠ 若 `/api/...` 突然全回 `{"detail":"Not Found"}`（那是 vLLM 的錯誤格式），
> 是前端 nginx 記住了後端的舊 IP、請求被送到別的容器。
> `docker restart law-frontend` 即恢復。nginx 設定已改為每次請求重新解析後端位址，
> 理論上不會再發生，**但那次修復只驗證過一次且後端 IP 當時不一定有變，尚未充分證明**。

## 重啟的連帶影響

| 重啟誰 | 後果 |
|---|---|
| **CacheBlend 引擎** | L1 KV 消失、L2 索引不重建 → **必須重新預熱** |
| **純 LMCache 引擎** | 快取清空，隨提問重新累積；無法預熱 |
| **後端** | `ChunkCacheTracker` 清空。**KV 本身不受影響**，但介面會顯示「未預熱」、依快取重排暫時失效。重跑預熱即可對齊（全部命中，約 10 秒）。**聊天紀錄不受影響**（在 PostgreSQL） |
| **PostgreSQL** | 資料在 bind mount，不受影響；重啟期間聊天無法寫入，回答仍會產生，回應的 `persisted` 為 false、介面提示「未保存」 |
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

**兩個引擎各有一份 L1**，CPU 記憶體需求加倍（334 段約 58 GB 以上）。
`L1_SIZE_GB` 與 `L2_SIZE_GB` 兩個引擎共用同一個變數；調小時不可低於語料實際需要的量，
理由同上。

**GPU 記憶體：** 兩個引擎各配置 `GPU_UTIL_EACH=0.45`，合計不可超過約 0.95。
兩個引擎時每個的 KV 池約 4 萬 token，單一對話綽綽有餘；
只跑一個引擎時可設 0.9（KV 池約 21 萬 token、約 46 條同時序列）。
**這是針對「每個模式只服務單一對話」的配置，不是為多人併發設計的**——
併發實測見驗證報告 14.10。

## 資料位置

全部在 git 儲存庫**之外**——資料庫檔案放進 repo 會被 `git checkout` / `git clean`
在運行中改寫（2026/09/24 的教訓）。

| 路徑 | 內容 |
|---|---|
| `~/qdrant_storage` | 向量與片段 |
| `~/law-chatroom-data/postgres/` | **聊天室與訊息** |
| `~/law-chatroom-data/seaweed/` | 原始 PDF、S3 憑證 |
| `~/blend-patches/` | vLLM 補丁 |
| `~/.law-env` | 憑證（含 `POSTGRES_PASSWORD`；`switch-nocache.sh` 也會改寫其中的無快取端點行）|
| `~/lawfiles/` | 舊的本機上傳目錄（切到 SeaweedFS 後不再寫入）|
| `/mnt/nvme0/mistral-model` | 模型權重 |
| `/mnt/nvme0/mistral-fs` | CacheBlend 引擎的 L2 KV 快取 |
| `/mnt/nvme0/mistral-fs-lmcache` | 純 LMCache 引擎的 L2（**必須與上面分開**：兩個行程寫同一個 LevelDB 目錄會壞檔，而且壞得很難看出來）|

備份就是備份這些目錄。PostgreSQL 建議另外用 `pg_dump` 做邏輯備份，
直接複製運行中的資料目錄不保證一致：

```bash
$DC exec -T postgres pg_dump -U lawchat lawchat > lawchat-$(date +%F).sql
```

## 已知限制

- **沒有使用者驗證。** 任何連到網址的人都看得到所有聊天室。上線前要決定是否由 ingress 或公司 SSO 擋在前面。
- 預熱沒有自動化：引擎每次重啟後要手動預熱（k8s 上 Pod 重建會讓這個問題隱形）。
- 追問時檢索會把上一個提問一併納入（`RetrievalHistoryUserTurns`，預設 1，設 0 關閉）；
  效果只在單一例子上觀察過，沒有量化。
- `ASPNETCORE_ENVIRONMENT=Development`（為了 Swagger 與詳細錯誤），正式上線前要改。

## 待辦

- [ ] Helm chart 已寫成（`helm/`，`helm lint/template` 通過），**尚未在實際叢集驗證**
- [ ] 偵測引擎重啟並自動重新預熱
- [ ] 使用者驗證
- [ ] 自架 embedding（外部 gateway 偶發 10–14 秒，且是單點故障）
