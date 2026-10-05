# Helm charts

| chart | 內容 | workspace |
|---|---|---|
| `law-model` | vLLM 推論引擎（CacheBlend、純 LMCache、可選的無快取） | `models` |
| `law-app` | 前端、後端、PostgreSQL、Qdrant、SeaweedFS | `ai-apps` |

部署前要先有答案的問題見 `hci-deployment-questions.md`。
**這兩個 chart 通過 `helm lint` 與 `helm template`，但沒有在真實叢集上跑過。**

## 本機檢查（不需要叢集）

`law-model` 需要兩個補丁檔才能渲染（見 `law-model/files/README.md`）。只想檢查語法時可放假檔：

```bash
cd helm/law-model/files
echo '# dummy' > gpu_worker_v0240.py && echo '# dummy' > lmc_models_utils_v0240.py
cd ../..
helm lint law-model
helm template law-model law-model | less
rm law-model/files/gpu_worker_v0240.py law-model/files/lmc_models_utils_v0240.py   # 檢查完刪掉，別把假檔提交

helm lint law-app --set secrets.postgresPassword=x --set secrets.embeddingApiKey=y --set secrets.s3SecretKey=z
```

## 有意為之的失敗

下列情況 `helm template` 會直接報錯，不是 bug：

- `law-model`：缺補丁檔、`blend` 模式卻關掉補丁、`limits.memory` 不大於 L1、`startAfter` 指向不存在或未啟用的引擎
- `law-app`：沒給密碼、`backend.replicas` 不是 1、沒填 `llmEndpoints.cacheblend`

原因都一樣：靜默地部署出一個「看起來正常但其實不對」的東西，比當場失敗更糟。
