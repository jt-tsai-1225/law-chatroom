# 補丁檔放這裡

`helm template` 與 `helm install` 需要這兩個檔案，沒有就會直接失敗（這是故意的）：

```
gpu_worker_v0240.py          → 覆蓋 vllm/v1/worker/gpu_worker.py
lmc_models_utils_v0240.py    → 覆蓋 lmcache/v1/compute/models/utils.py
```

來源是 193 的 `~/blend-patches/`：

```bash
scp -P 18330 oem@10.102.197.193:~/blend-patches/gpu_worker_v0240.py       helm/law-model/files/
scp -P 18330 oem@10.102.197.193:~/blend-patches/lmc_models_utils_v0240.py helm/law-model/files/
```

為什麼不放 .gitignore：它們是 CacheBlend 能運作的前提，版本要跟 chart 一起追蹤。
沒有它們，CacheBlend 會靜默失效——引擎照樣啟動、數據看起來正常，但非前綴複用不會發生。
