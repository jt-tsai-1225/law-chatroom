#!/usr/bin/env bash
#
# 「無快取」端點的開關。
#
# 為什麼需要手動切換而不是常駐三個引擎：
#   Mistral-7B fp16 的權重每份約 14 GB，三份就 43.5 GB，
#   48 GB 的卡連 KV 池都不剩。因此常駐兩個（CacheBlend 與純 LMCache），
#   第三個要用時把純 LMCache 讓出來。
#
# 用法：
#   ./deploy/switch-nocache.sh on     # 停掉純 LMCache，起無快取
#   ./deploy/switch-nocache.sh off    # 停掉無快取，起回純 LMCache
#   ./deploy/switch-nocache.sh status
#
# 切換後端點狀態約 15 秒內會反映到介面上（後端的探測快取時間）。

set -euo pipefail

REPO=${REPO:-$(cd "$(dirname "$0")/.." && pwd)}
cd "$REPO"

NAME=nocache-vllm
PORT=8002
MODEL_DIR=${MODEL_DIR:-/mnt/nvme0/mistral-model}
LLM_MODEL=${LLM_MODEL:-mistralai/Mistral-7B-Instruct-v0.2}
UTIL=${GPU_UTIL_EACH:-0.45}
API=${API:-http://localhost:8080}

# 環境檔：docker compose 不會自己讀 ~/.law-env，沒帶 --env-file 的話
# 會因為缺 POSTGRES_PASSWORD 而整個失敗（compose 檔裡該變數是必填）。
ENV_FILE=${ENV_FILE:-$HOME/.law-env}
[ -f .env ] && set -a && . ./.env && set +a
[ -f "$ENV_FILE" ] && set -a && . "$ENV_FILE" && set +a
DC=(docker compose)
[ -f "$ENV_FILE" ] && DC=(docker compose --env-file "$ENV_FILE")

start_nocache() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker run -d --name "$NAME" \
    --network lawnet -p "$PORT":8000 \
    --gpus '"device=0"' --ipc=host \
    --restart unless-stopped \
    -v "$MODEL_DIR":/root/.cache/huggingface \
    --entrypoint vllm lmcache/vllm-openai:v0.5.1 \
    serve "$LLM_MODEL" \
      --tensor-parallel-size 1 \
      --no-enable-prefix-caching \
      --enable-prompt-tokens-details \
      --max-model-len 32648 \
      --max-num-batched-tokens 32768 \
      --gpu-memory-utilization "$UTIL" \
      --enforce-eager >/dev/null
}

# 等端點就緒，並印出進度。載入模型要一兩分鐘，靜靜等的話
# 很容易被當成卡住而中斷——中斷後 docker run -d 起的容器還在背景載入，
# 狀態就變得難以判斷。
wait_ready() {
  local url=$1 n=0
  printf '  等待 %s ' "$url"
  until curl -sf -o /dev/null --max-time 3 "$url/v1/models"; do
    printf '.'; sleep 3; n=$((n+1))
    [ $n -gt 80 ] && { echo; echo "逾時：$url"; return 1; }
  done
  echo ' 就緒'
}

wait_api() {
  local n=0
  printf '  等待後端 '
  until curl -sf -o /dev/null --max-time 3 "$API/api/cachemodes"; do
    printf '.'; sleep 3; n=$((n+1))
    [ $n -gt 40 ] && { echo; echo "逾時：後端沒有回應"; return 1; }
  done
  echo ' 就緒'
}

# 端點設定寫進環境檔。一律用 --no-deps 重建後端：
# 後端依賴 vllm-lmcache，沒有 --no-deps 的話 compose 會把剛停掉的
# LMCache 又拉起來，三個引擎同時搶 GPU 0 而互相啟動失敗。
set_none_endpoint() {
  grep -q '^RAGSettings__LlmEndpoints__none=' "$ENV_FILE" 2>/dev/null ||
    echo "RAGSettings__LlmEndpoints__none=http://$NAME:8000" >> "$ENV_FILE"
  "${DC[@]}" up -d --no-deps backend >/dev/null
}

clear_none_endpoint() {
  [ -f "$ENV_FILE" ] && sed -i '/^RAGSettings__LlmEndpoints__none=/d' "$ENV_FILE"
  "${DC[@]}" up -d --no-deps backend >/dev/null
}

case "${1:-status}" in
  on)
    echo "停掉純 LMCache，讓出記憶體…"
    "${DC[@]}" stop vllm-lmcache
    echo "啟動無快取引擎（埠 $PORT）…"
    start_nocache
    wait_ready "http://localhost:$PORT"
    echo "把端點告訴後端並重建（--no-deps，不會把 LMCache 拉起來）…"
    set_none_endpoint
    wait_api
    echo
    echo "完成。「無快取」已可選用；「純 LMCache」目前不可用，介面會標示。"
    ;;

  off)
    echo "停掉無快取引擎…"
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    echo "起回純 LMCache…"
    "${DC[@]}" up -d vllm-lmcache >/dev/null
    wait_ready "http://localhost:8001"
    echo "移除無快取端點並重建後端…"
    clear_none_endpoint
    wait_api
    echo
    echo "完成。LMCache 的快取是空的，會隨提問重新累積。"
    ;;

  status)
    echo "GPU 0 上的 vLLM 實例："
    docker ps --filter "ancestor=lmcache/vllm-openai:v0.5.1" \
      --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
    echo
    nvidia-smi -i 0 --query-gpu=memory.used,memory.total --format=csv
    ;;

  *)
    echo "用法：$0 {on|off|status}" >&2
    exit 1
    ;;
esac
