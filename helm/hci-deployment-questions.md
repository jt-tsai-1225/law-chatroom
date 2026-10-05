# 法律 RAG 聊天室上 HCI（Kubernetes）——問題清單 v2

2026/10/05 更新。相對 v1 的差異見最後一節「與 v1 的差異」。

## 這套系統是什麼

目前以 docker compose 在單機（10.102.197.193）運作，**八個容器**：

| 容器 | 角色 | workspace |
|---|---|---|
| vLLM × 2（另有第三個按需啟用） | 推論引擎，**需要 GPU**。CacheBlend、純 LMCache、（無快取） | `models` |
| 後端（.NET） | API、RAG 流程、聊天室 | `ai-apps` |
| 前端（nginx） | 靜態頁面 + `/api` 反向代理 | `ai-apps` |
| PostgreSQL | 聊天紀錄 | `ai-apps` |
| Qdrant | 向量庫 | `ai-apps` |
| SeaweedFS | 原始 PDF 物件儲存 | `ai-apps` |

已確認的平台慣例（來自同事的 litellm-nemo-guardrails 範例，以及主管與同事的回覆）：

- Kubernetes，`helm install <release> ./helm/<chart> -n <namespace>`
- Harbor：`cr.phison.com/<project>/<image>:<tag>`；**所有映像都要先推上 Harbor**，包含公開映像
- `models` workspace **可以** `helm install` 我們自己的映像
- 所有映像由我們自己推上去

Chart 已寫好（`helm/law-model`、`helm/law-app`），`helm lint` 與 `helm template` 通過，
**但沒有在真實叢集上跑過**。以下是在那之前必須先確認的事。

---

## 先問這五題（沒有答案就沒辦法往下）

| # | 問題 | 為什麼 |
|---|---|---|
| 1 | **叢集有 GPU 嗎？型號與記憶體？我們能拿幾張？** | 見第一節。沒有 GPU 就上不了 |
| 2 | **GPU 能不能被兩個 Pod 共用（時間切片 / MIG）？** | 決定兩個引擎要兩張卡還是共用一張 |
| 3 | **`ai-apps` 的 Pod 連得到 `models` 的 Service 嗎？** | 後端要跨 workspace 呼叫引擎 |
| 4 | **單一 Pod 要得到 64 GB 記憶體、節點有 60 GB 本機暫存空間嗎？** | LMCache 的 L1/L2 |
| 5 | **叢集連得到外部 embedding gateway（ainexus.phison.com）嗎？** | 硬需求，連不到檢索完全失效 |

---

## 一、GPU（最大的未知數）

**這一項不滿足，整個專案上不了叢集。** 同事的範例全是 CPU 服務，看不出叢集是否有 GPU 節點。

### 我們需要什麼

每個引擎的權重 14 GB + KV 快取池。**低於 40 GB 的卡跑不動目前的設定**（用 0.9 的記憶體配置時 KV 池 210,092 tokens）。

### 三種部署方式，取決於問題 1 與 2 的答案

| 方案 | 條件 | 結果 |
|---|---|---|
| **A. 兩張卡，各跑一個引擎** | 能拿 2 張 ≥ 40 GB 的卡 | **建議。** 兩個引擎互不干擾，三種模式可即時切換 |
| B. 一張卡，兩個引擎時間切片 | 平台支援 GPU 共用 | 各設 `memoryUtilization: "0.45"`，**必須**讓第二個引擎等第一個就緒才啟動（chart 的 `startAfter` 已處理）。每個引擎的 KV 池縮到約 4 萬 token，單一對話夠用，併發不行 |
| C. 只有一張卡且不能共用 | — | 只能常駐 CacheBlend，另兩種模式按需切換（把其中一個 `enabled` 關掉），**無法即時切換** |

為什麼方案 B 要排程：兩個引擎同時載入模型時，vLLM 以「GPU 記憶體的前後差」估算自己的用量，
另一個引擎的配置被算成自己的，結果 KV 池被扣成零而啟動失敗
（2026/10/05 在 193 上實測踩到）。

### 要問的細節

| 問題 | 為什麼要問 |
|---|---|
| 有裝 NVIDIA device plugin 嗎？資源名稱是 `nvidia.com/gpu` 嗎？ | Pod 要宣告這個才排得到卡。名稱不同要改 `engines.*.gpu.resourceName` |
| GPU 節點有什麼標籤與汙點？ | 要填進 `scheduling.nodeSelector` / `tolerations` |
| 這些卡會被別的團隊搶嗎？ | 這是常駐服務，不是批次工作 |

---

## 二、記憶體與儲存

### 各 Pod 的需求

| Pod | 記憶體 | 本機暫存（emptyDir） | 持久儲存 |
|---|---|---|---|
| vLLM CacheBlend | **64 GB** | 60 GB（L2，要快） | 權重 20 GB |
| vLLM 純 LMCache | 48 GB | 45 GB（L2，要快） | 權重 20 GB（可與上面共用） |
| 後端 | 2 GB | — | — |
| PostgreSQL | 1 GB | — | 5 GB |
| Qdrant | 2 GB | — | 10 GB |
| SeaweedFS | 1 GB | — | 10 GB |

CacheBlend 的記憶體大是因為 L1 快取放在 CPU 記憶體（50 GB）。
**這不是可以隨意縮的數字**：L1 滿了會觸發上游的缺陷，整個引擎終止（LMCache issue #4133）。
334 個片段實測需要 28.6 GiB，所以 50 GB 是有餘裕的安全值，不是浪費。

### 要問的

1. 單一 Pod 要得到 64 GB 記憶體嗎？有沒有 LimitRange / ResourceQuota 擋住？兩個引擎加起來約 112 GB。
2. **L2 需要本機 SSD，不是網路儲存。** 節點的暫存空間（ephemeral storage）有多少？是本機 NVMe 嗎？
   走網路儲存的話 KV 載入會變慢，快取就失去意義。
   （L2 用 emptyDir：L2 索引存在記憶體裡，Pod 重啟後不會重建，保留磁碟內容沒有價值。）
3. 有哪些 StorageClass？預設是哪一個？支援 `ReadWriteMany` 嗎？
   - 支援：兩個引擎共用一份權重（14 GB）
   - 不支援：兩個引擎若在不同節點，要各存一份（`model.weights.perEngine: true`，多 14 GB）
4. 模型權重（14 GB）要怎麼放進叢集？叢集連得到 HuggingFace 嗎？
   連不到的話要先把權重灌進 PVC：我們可以從 193 `kubectl cp`，請確認我們有權限。
5. **PostgreSQL 的資料有備份機制嗎？** 聊天紀錄是使用者資料，不像向量庫可以重建。
   平台有沒有提供卷快照或定期備份？沒有的話我們需要自己做 `pg_dump` 的排程。

---

## 三、Harbor

| 問題 | 說明 |
|---|---|
| 要用哪個 Harbor 專案？由誰建立？ | 範例是 `litellm_nemo_guardrails`（底線） |
| **單一映像有大小上限或配額嗎？** | **vLLM 映像 18.5 GB**（實測），比一般服務大一個量級。v1 寫的 10 GB 是錯的 |
| 從 193 推得上去嗎？網路與權限？ | 目前只在 193 本機 build |
| 專案設 Public 的話全公司可見——後端映像含我們的程式碼，這樣可以嗎？ | 若不行就要 `imagePullSecret`，chart 支援 |
| 叢集第一次拉 18.5 GB 要多久？節點有沒有映像快取？ | chart 的啟動探測最多等 10 分鐘，不夠要調 |

---

## 四、網路

| 問題 | 說明 |
|---|---|
| **`ai-apps` 的後端連得到 `models` 的引擎嗎？** 有 NetworkPolicy 預設擋跨 namespace 嗎？ | 後端用 `http://law-model-cacheblend.models.svc.cluster.local:8000` 呼叫 |
| Ingress controller 是哪一套？ingressClassName？網域與憑證怎麼申請？ | 目前是 193 的 8080 埠 |
| Ingress 能放寬請求大小與逾時嗎？ | 上傳法規 PDF 可達數十 MB（需 100m），預熱是同步端點（需 600 秒） |
| **叢集連得到外部 embedding gateway 嗎？** | 硬需求 |
| 有 NetworkPolicy 預設擋外連嗎？ | — |
| 叢集 DNS 是哪一套？ | 前端的 nginx 會依 `/etc/resolv.conf` 自動偵測，通常不用管，只是確認 |

---

## 五、權限與流程

| 問題 | 說明 |
|---|---|
| 我的 namespace 是什麼？由誰建立？ | 主管說前後端與資料庫在 `ai-apps`，模型在 `models`。需要兩個 namespace 都能 `helm install` |
| 我可以自己 `helm install`，還是要走平台團隊？ | `models` 已確認可以；`ai-apps` 請確認 |
| 機密怎麼管？ | 需要三個：Postgres 密碼、embedding 金鑰、S3 金鑰。目前在 193 的 `~/.law-env`（`chmod 600`）。chart 支援既有 Secret，或 `--set` 傳入（後者會記在 release 資訊裡，叢集管理員看得到） |
| 使用者驗證怎麼做？ | 目前聊天室**沒有登入機制**，任何連到網址的人看得到所有聊天室。上線前需要決定是否由 ingress / 公司 SSO 擋在前面 |

---

## 六、要讓平台團隊先知道的限制

不是問題，是這套系統的性質，**排程策略要配合**。

### 1. 引擎只能一個副本，不能水平擴充

LMCache 的 L2 索引存在行程記憶體裡。開兩個副本不會共用快取，
會變成各自預熱、各自吃 50 GB 記憶體。每個引擎 `replicas` 固定為 1（chart 以 Recreate 策略部署，
因為 GPU 一次只能給一個 Pod）。後端也固定為 1（預熱紀錄在行程記憶體裡）。

提高吞吐只能換更大的卡或加卡做張量平行，不能加副本。
**不要對這些 Deployment 設 HPA。**

### 2. 引擎每次 Pod 重啟都要預熱，期間是冷啟狀態

vLLM 重啟後 L1 快取消失，**L2 的索引也不會自動重建**——磁碟上的 KV 檔案還在卻讀不到。
必須重新執行預熱（`POST /api/knowledgebase/warmup`，約 36 秒）才會恢復。

節點排空、滾動更新、驅逐都會造成效能低落期。
**目前沒有自動重新預熱**，需要在上叢集前補上。排程上應避免頻繁搬動這些 Pod。

只有 CacheBlend 引擎需要預熱；純 LMCache 是前綴快取，靠實際提問累積，無法預熱。

### 3. 上游有未修的缺陷，觸發時整個引擎終止

LMCache issue #4133：L1 滿了觸發淘汰時引擎直接終止，不是降級。
我們用容量上限迴避（334 段用 29 GB，上限設 50 GB）。

k8s 預設會自動重建終止的 Pod，**這會讓問題變得不可見**：服務看起來正常，實際上快取是空的。因此需要：

- 存活探測要能反映「引擎還在且快取可用」，不能只看埠通不通（目前只看 `/v1/models`）
- 重建後自動重新預熱
- 重建次數要有告警

### 4. 兩個引擎共用一張卡時，必須依序啟動

見第一節方案 B。chart 以 initContainer 處理，不需要平台配合。

### 5. 併發的表現

單一使用者使用時 CacheBlend 的首字延遲約 160 ms（無快取約 590 ms）。**併發 40 以上、
流量集中在少數固定問題時，CacheBlend 的首字延遲會輸給純前綴快取**（15% 重算吃 GPU 算力）。
亂序流量下的併發結果仍在驗證中。聊天室預期的使用量是單一對話，不是高併發，
所以這是已知限制而不是阻礙。

---

## 七、要推上 Harbor 的映像

`<project>` 待定。**第三方映像全部釘版本**——`latest` 不可重現，Pod 重建時可能拉到不同版本。

| 來源 | 大小 | 推上 Harbor 後 |
|---|---|---|
| `lmcache/vllm-openai:v0.5.1` | **18.5 GB** | `cr.phison.com/<project>/vllm-openai:v0.5.1` |
| `postgres:16-alpine` | ~250 MB | `cr.phison.com/<project>/postgres:16-alpine` |
| `qdrant/qdrant:v1.19.1` | ~200 MB | `cr.phison.com/<project>/qdrant:v1.19.1` |
| `chrislusf/seaweedfs:4.48` | ~100 MB | `cr.phison.com/<project>/seaweedfs:4.48` |
| `law-backend`（自建） | ~300 MB | `cr.phison.com/<project>/law-backend:0.1.0` |
| `law-frontend`（自建） | ~50 MB | `cr.phison.com/<project>/law-frontend:0.1.0` |

193 上目前 Qdrant 與 SeaweedFS 跑的是 `latest`。**釘版本前先確認 193 實際跑的版本**，
再決定釘哪一個，否則等於換了版本卻沒測過：

```bash
docker exec law-qdrant ./qdrant --version
docker exec law-seaweedfs weed version
```

```bash
docker login cr.phison.com
P=cr.phison.com/<project>

docker tag lmcache/vllm-openai:v0.5.1   $P/vllm-openai:v0.5.1
docker tag postgres:16-alpine           $P/postgres:16-alpine
docker tag qdrant/qdrant:v1.19.1        $P/qdrant:v1.19.1
docker tag chrislusf/seaweedfs:4.48     $P/seaweedfs:4.48
docker tag law-backend:latest           $P/law-backend:0.1.0
docker tag law-frontend:latest          $P/law-frontend:0.1.0

docker push $P/vllm-openai:v0.5.1       # 最久，18.5 GB
docker push $P/postgres:16-alpine
docker push $P/qdrant:v1.19.1
docker push $P/seaweedfs:4.48
docker push $P/law-backend:0.1.0
docker push $P/law-frontend:0.1.0
```

**不要用 `latest` 當自建映像的標籤。** `imagePullPolicy` 遇到 `latest` 預設每次都重拉，而且版本無從追溯。
用 `0.1.0`、`0.1.1` 這樣遞增。

---

## 八、部署步驟（問題都有答案之後）

```bash
# 1. models workspace：推論引擎
helm install law-model ./helm/law-model -n models \
  --set image.repository=$P/vllm-openai \
  --set model.weights.existingClaim=<已灌好權重的 PVC>      # 或讓 chart 建，再灌

# 2. ai-apps workspace：應用層
helm install law-app ./helm/law-app -n ai-apps \
  --set images.backend.repository=$P/law-backend \
  --set images.frontend.repository=$P/law-frontend \
  --set images.postgres.repository=$P/postgres \
  --set images.qdrant.repository=$P/qdrant \
  --set images.seaweedfs.repository=$P/seaweedfs \
  --set secrets.postgresPassword=$(openssl rand -hex 24) \
  --set secrets.s3SecretKey=$(openssl rand -hex 24) \
  --set secrets.embeddingApiKey=<金鑰> \
  --set frontend.ingress.enabled=true --set frontend.ingress.host=<網域>
```

之後要做的事（`helm install` 後 NOTES 會再列一次）：

1. 在 SeaweedFS 建 bucket
2. **知識庫是空的**：新 PVC 上的 Qdrant 沒有資料。重新上傳法規 PDF，或從 193 複製
3. 對 CacheBlend 引擎預熱
4. 確認 `/api/cachemodes` 兩個模式都 reachable

---

## 附：目前的資源用量（實測，非估算）

```
GPU 記憶體（單引擎）  44 GB / 48 GB     （--gpu-memory-utilization 0.9）
GPU KV 快取池         210,092 tokens    （vLLM 自報）
同時序列上限          約 46             （每個約 4,600 tokens）
兩引擎共用一張卡      各 0.45，KV 池各約 4 萬 tokens
L1（CPU 記憶體）      CacheBlend 上限 50 GB，純 LMCache 35 GB
L2（本機 SSD）        CacheBlend 實佔 29 GB
語料                  334 個片段，234,395 tokens
預熱時間              約 36 秒
引擎載入時間          約 3.5 分鐘（模型載入）
```

## 與 v1 的差異

- 新增 **PostgreSQL**（聊天紀錄），要備份策略與儲存
- 引擎從一個變**兩到三個**：GPU 需求、記憶體需求（約 112 GB）、本機暫存都跟著加倍
- **vLLM 映像是 18.5 GB，不是 10 GB**
- 新增跨 workspace 連線（`ai-apps` → `models`）
- 兩個引擎共用一張卡時的**啟動順序問題**（實測踩到）
- **前端的 nginx 要在每次請求重新解析後端位址**（後端重啟後 IP 會變，舊 IP 被別的容器接手，
  實測發生過）；映像已改為範本，由環境變數與 DNS 偵測帶入，docker 與 k8s 共用同一個映像
- Qdrant 與 SeaweedFS 要釘版本
- 新增：沒有使用者驗證、Postgres 備份、併發表現
