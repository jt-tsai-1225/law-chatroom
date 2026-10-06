# 從零重建（機器重灌後）

這份是「現在這套是怎麼架起來的」的實作筆記，目的是重灌後不必重新摸索。
日常操作見 `README.md`；這裡只記**還原順序**與**哪些東西不在 git 裡**。

> 寫於 2026/10/06，依 193 重灌前的實際狀況整理。標了「**未知**」的是當時沒有確認的事，
> 重灌後遇到請先查證，不要照這份文件假設。

## 哪些東西不在 git 裡（要從備份還原）

| 項目 | 原位置 | 用途 | 沒有它會怎樣 |
|---|---|---|---|
| vLLM 補丁 `gpu_worker_v0240.py`、`lmc_models_utils_v0240.py` | `~/blend-patches/` | CacheBlend 的核心；compose 把它們掛進容器 | 引擎起得來但 blend 靜默失效（**不報錯**）。**不可放 `/tmp`** |
| 憑證 | `~/.law-env` | embedding 金鑰、S3 金鑰、`POSTGRES_PASSWORD` | 後端起不來或匯入失敗 |
| compose 變數 | `~/law-chatroom/.env` | 路徑與容量 | 用 `.env.example` 重建即可，**以 `.env.example` 為準**，備份的 `.env` 只取路徑 |
| 聊天紀錄 | PostgreSQL | 聊天室與訊息 | 紀錄消失（`lawchat.sql`） |
| SeaweedFS 資料與 `s3.json` | `~/law-chatroom-data/seaweed/` | 上傳過的原始 PDF | 見下方「原始 PDF」 |
| 壓測腳本與結果 | `~/bench-concurrency.*`、`~/concurrency-*` | 14.10 的證據與腳本 | 腳本在 repo 外 |

**不用備份、可以重建的：** Qdrant 向量（重新匯入）、兩個引擎的 KV（預熱）、`/mnt/nvme0/mistral-fs*` 的 L2。

## `~/.law-env` 的內容（只記鍵名，值不在這裡）

```
RAGSettings__EmbeddingApiKey=
RAGSettings__ObjectStorageBackend=seaweedfs
RAGSettings__S3ServiceUrl=http://law-seaweedfs:8333
RAGSettings__S3Bucket=law-documents
RAGSettings__S3AccessKey=lawchatroom   # 與 s3.json 的 accessKey 一致
RAGSettings__S3SecretKey=            # 必須與 seaweed/conf/s3.json 完全一致
POSTGRES_PASSWORD=
# 切換「無快取」時 switch-nocache.sh 會自己加減這一行：
# RAGSettings__LlmEndpoints__none=http://nocache-vllm:8000
```

⚠ 不去引號、`=` 前後不可有空白。

## 還原順序

### 0. 前置

- Docker 與 NVIDIA container toolkit。確認容器看得到 GPU：`docker run --rm --gpus '"device=0"' --entrypoint nvidia-smi lmcache/vllm-openai:v0.5.1`。
- **只用 GPU 0**（193 是共用機器）。compose 與 `switch-nocache.sh` 都寫死 `device=0`，換機器要改。
- **Docker Hub 可能限流**：重灌前 193 的 IP 被擋過（429），先 `docker pull nginx:1.27-alpine` 試一次。
  需要的 Docker Hub 映像：`lmcache/vllm-openai:v0.5.1`（約 18.5 GB）、`postgres:16-alpine`、
  `qdrant/qdrant:latest`、`chrislusf/seaweedfs:latest`、`nginx:1.27-alpine`、`node:18-alpine`。
  後端的 .NET 映像來自 `mcr.microsoft.com`，不受影響。
  `qdrant` 與 `seaweedfs` 在 compose 裡是 `latest`，容易在重建時拉到不同版本。
  重灌前（2026/10/06）193 上的映像 digest 如下，**要還原當時的版本就用 digest 拉**
  （`docker pull <名稱>@sha256:…`；Docker Hub 上的 `latest` 之後可能已指向別的內容，digest 不會變）：

  | 映像（compose 使用的） | digest |
  |---|---|
  | `lmcache/vllm-openai:v0.5.1` | `sha256:0921e04dc715d997dfdf815827b98e5c880d8264f7cc70c4608f818ffadc2942` |
  | `postgres:16-alpine` | `sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea` |
  | `qdrant/qdrant:latest` | `sha256:12364fe851b9f17356fc88189fc06d1b521262e04659ec7345975b00c9246a10` |
  | `chrislusf/seaweedfs:latest` | `sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d` |

  另外 193 上同時存在 `lmcache/vllm-openai:v0.5.5`（`sha256:59b35075…a951d`，compose 沒用）、
  `chrislusf/seaweedfs:4.47`（Docker Hub，`sha256:ce9e796f…bf882`）與 `:4.46`。
  **這些 digest 是本機存的映像，沒有對應到「哪個 Pod／容器實際在跑」的證據**——
  重灌前若能執行 `docker inspect law-qdrant law-seaweedfs --format '{{.Name}} {{.Image}}'` 比對映像 ID，才算確認。
  **程式版本號（2026/10/06 在運行中的容器內查得）：** Qdrant **1.19.1**（`qdrant --version`）、
  SeaweedFS **4.48**（`weed version`，`530be3e37`）。兩者在 compose 裡都是 `latest`，所以
  `latest` 在當時恰好就是這兩個版本。Helm `values.yaml` 的 `v1.19.1` 與 `4.48` 與此一致。
  兩個容器實際使用的映像 ID：`law-qdrant` → `sha256:dd57172b…e740a92b`、
  `law-seaweedfs` → `sha256:7b5ca1f1…4c52f5`。**映像 ID 不是上表的 repo digest**，
  兩者不能直接比對；要對應請在重灌前執行
  `docker images --no-trunc --format '{{.ID}} {{.Repository}}:{{.Tag}}' | grep -E 'dd57172b|7b5ca1f1'`。
  要釘版本時用 `qdrant/qdrant:v1.19.1`、`chrislusf/seaweedfs:4.48`，
  **這兩個標籤是否存在於 Docker Hub 沒有確認**，第一次拉取時留意；失敗就改用上表的 digest。

  **193 上沒有 `nginx` 與 `node` 的本機映像**，前端建置一定會連 Docker Hub。
  公司內部另有映像庫（193 上看到 `cr-ext.phison.com/phisonai/images/…`、
  `10.102.196.43:32180/phisonai/images/…`、`harbor.phison.com/phisonai/…`，其中有
  `chrislusf/seaweedfs:4.47` 與數個 `postgres`）。**這些庫裡有沒有 `nginx`、`qdrant`、`node`、
  `vllm` 沒有確認**，Docker Hub 被限流時值得查，但重灌後要先確認有權限拉取。

### 1. 取得程式與目錄

```bash
git clone https://github.com/jt-tsai-1225/law-chatroom.git ~/law-chatroom
cd ~/law-chatroom && git checkout feature/vllm-cacheblend-integration
git pull                                   # 一定要拉到最新
git log --oneline -5                       # 預期看得到「Add a rebuild runbook」

docker network create lawnet

mkdir -p ~/blend-patches ~/qdrant_storage ~/lawfiles
mkdir -p ~/law-chatroom-data/postgres ~/law-chatroom-data/seaweed/{data,conf}
mkdir -p /mnt/nvme0/mistral-model /mnt/nvme0/mistral-fs /mnt/nvme0/mistral-fs-lmcache
```

### 2. 還原檔案

```bash
# 補丁 → 家目錄，不是 /tmp
cp backup/blend-patches/*.py ~/blend-patches/

# 憑證
cp backup/law-env ~/.law-env && chmod 600 ~/.law-env

# compose 變數：從範本開始，再對照備份的 .env 取路徑
cp .env.example .env && vi .env

# SeaweedFS
tar xzf seaweed-20261006.tar.gz -C ~     # 解出 law-chatroom-data/seaweed
chmod 700 ~/law-chatroom-data && chmod 600 ~/law-chatroom-data/seaweed/conf/s3.json
```

在 `~/.bashrc` 加上，之後所有指令都用 `$DC`：

```bash
export DC="docker compose --env-file $HOME/.law-env"
```

### 3. 模型權重

放在 `/mnt/nvme0/mistral-model`（掛進容器的 `/root/.cache/huggingface`），
模型是 `mistralai/Mistral-7B-Instruct-v0.2`，約 14 GB。
**未知：** 當初是怎麼放進去的，以及這個模型現在是否需要 Hugging Face 登入或同意條款才能下載。
先確認新環境連得到 Hugging Face，再決定是由 vLLM 首次啟動時自動下載，還是手動下載。

### 4. 先只起 PostgreSQL，還原聊天紀錄

**順序重要。** 後端啟動時會自動建表；若後端先起來，再還原 dump 會因為「表已存在」而衝突。

```bash
$DC up -d postgres
until docker exec law-postgres pg_isready -U lawchat -d lawchat >/dev/null 2>&1; do sleep 2; done

$DC exec -T postgres psql -U lawchat lawchat < backup/lawchat.sql
$DC exec postgres psql -U lawchat lawchat -c 'SELECT count(*) FROM messages;'
```

### 5. 起整套

```bash
$DC up -d --build
```

啟動順序：`vllm`（CacheBlend，載入約 3.5 分鐘）healthy 之後才開始載入 `vllm-lmcache`
（再約 3.5 分鐘）；後端等 `vllm` 與 `postgres` healthy。**整套就緒約 7 分鐘**（估計）。

```bash
$DC ps
curl -s http://localhost:8080/api/cachemodes | python3 -m json.tool | grep -E '"mode"|reachable'
```

預期：`cacheblend true`、`lmcache true`（載入中時暫為 false）、`none false`。

建 bucket（若還原的 SeaweedFS 資料裡沒有）：

```bash
$DC exec -T seaweedfs weed shell <<'EOT'
s3.bucket.list
s3.bucket.create -name law-documents
EOT
```

### 6. 重新匯入法典（Qdrant 是空的）

repo 內有兩份來源：`legal_pdfs/公司法.pdf`、`legal_pdfs/民法.pdf`（已進 git）。

**用介面或後端的上傳端點**，這是目前實際用過、產出 334 段的路徑：

```bash
curl -s -X POST "http://localhost:8080/api/knowledgebase/upload?warmup=false" \
  -F "file=@legal_pdfs/民法.pdf"
# 回傳 jobId；進度：GET /api/knowledgebase/jobs/{jobId}
# 公司法同樣一次
```

- `title` 省略時由 PDF 內的「法規名稱：」讀取。
- 重新上傳同名法典是「取代」，不會疊加。
- 匯入需要打外部 embedding gateway，**用到 `~/.law-env` 的金鑰**。
- 根目錄的 `import_legal_pdfs.py` 與 `LEGAL_PDF_IMPORT_GUIDE.md` 是較早的做法，
  **未確認它的切分方式與現在的後端一致**，沒有驗證前不要用它重建。

匯入完成後確認片段數（2026/10/05 的值是 334；若不同，先查原因再往下）：

```bash
curl -s http://localhost:8080/api/knowledgebase/documents | head -c 400
```

### 7. 預熱 CacheBlend

```bash
curl -s -X POST http://localhost:8080/api/knowledgebase/warmup \
  -H "Content-Type: application/json" -d '{}' | python3 -m json.tool | head -8
```

預期 `processedChunks == totalChunks`、`failedBatches 0`。冷啟約 35–40 秒。
純 LMCache 無法預熱，快取隨提問累積。

### 8. 驗收

照 `README.md` 的「驗收」一節，再補兩項：

- 瀏覽器開 `http://<主機>:8080`，新增聊天室、送一題、重新整理後從側欄載回。
- 還原的聊天紀錄在側欄看得到。

## 這套踩過的坑（重建時最容易再踩）

| 現象 | 原因 | 處理 |
|---|---|---|
| `請在 .env 或 ~/.law-env 設定 POSTGRES_PASSWORD` | `docker compose` 不會自己讀 `~/.law-env` | 一律用 `$DC` |
| 換後端時引擎被連帶拉起 | 後端 `depends_on` 兩個引擎 | `$DC up -d --no-deps backend` |
| 兩個 vLLM 同時起，其中一個報 `No available memory for the cache blocks` | 記憶體估算被另一個引擎干擾 | compose 已序列化；手動起時一個一個來 |
| `/api/...` 全回 `{"detail":"Not Found"}` | 前端 nginx 記住後端舊 IP（請求被送到 vLLM） | `docker restart law-frontend`；nginx 已改為每次解析 |
| CacheBlend 命中率是 0 或行為怪異 | 補丁沒掛進去（放在 `/tmp` 被清空後 docker 建了空目錄蓋住它） | 補丁放 `~/blend-patches/`，啟動後檢查容器內檔案大小 |
| 兩個引擎共用 L2 目錄 | LevelDB 被兩個行程寫會壞檔，且壞得很隱蔽 | `L2_DIR` 與 `L2_LMCACHE_DIR` 必須不同 |
| env 檔值帶引號 | env 檔不去引號 | 不加引號、`=` 兩側無空白 |
| 切到「無快取」後三個引擎同時搶 GPU | 沒有先停純 LMCache | 用 `switch-nocache.sh`，不要手動 |
| 預熱後介面顯示「已預熱」但引擎是冷的 | 那是後端送出紀錄的推估，不是向引擎查詢 | 引擎或後端重啟後重跑預熱 |
| `docker build` 報 `429 Too Many Requests` | Docker Hub 對共用 IP 限流 | 等待，或改用本地已有的映像；後端映像不受影響 |

## 重灌後要重新確認的事

1. 主機 IP 有沒有變。`.env` 的 `HOST_IP` 與使用者連線的網址都要跟著改。
2. SSH 埠（先前是 18330）與帳號。
3. 可用的 GPU 是否仍只有 GPU 0。
4. `/mnt/nvme0` 是否仍在、有多少空間（模型 14 GB、兩份 L2 各約 29 GB）。
5. 公司網路是否仍只放行 8080。
