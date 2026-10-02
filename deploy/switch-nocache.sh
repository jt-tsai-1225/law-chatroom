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

# .env 若存在就讀進來，讓路徑與上面的預設一致
[ -f .env ] && set -a && . ./.env && set +a

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

wait_ready() {
  local url=$1 n=0
  until curl -sf -o /dev/null --max-time 3 "$url/v1/models"; do
    sleep 3; n=$((n+1))
    [ $n -gt 60 ] && { echo "逾時：$url"; return 1; }
  done
}

case "${1:-status}" in
  on)
    echo "停掉純 LMCache，讓出記憶體…"
    docker compose stop vllm-lmcache
    echo "啟動無快取引擎（埠 $PORT）…"
    start_nocache
    wait_ready "http://localhost:$PORT"
    echo
    echo "完成。還要把端點告訴後端——在 .env 或 ~/.law-env 加上："
    echo "  RAGSettings__LlmEndpoints__none=http://$NAME:8000"
    echo "然後 docker compose up -d backend"
    echo
    echo "⚠ 此時「純 LMCache」模式會變成不可用，介面上會標示出來。"
    ;;

  off)
    echo "停掉無快取引擎…"
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    echo "起回純 LMCache…"
    docker compose up -d vllm-lmcache
    wait_ready "http://localhost:8001"
    echo
    echo "完成。記得把 RAGSettings__LlmEndpoints__none 從設定移除，"
    echo "否則介面會列出一個連不到的模式。"
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
