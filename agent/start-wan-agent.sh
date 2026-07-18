#!/bin/bash
# Template — provision_wan_native.sh / wan_native.py rewrite WAN_LOW_VRAM + LOBO_UNLOAD_MODELS per GPU.
set -euo pipefail
cd /workspace
set -a
source /workspace/.loboforge-env
set +a
export PYTHONPATH=/workspace${PYTHONPATH:+:$PYTHONPATH}
export LOBO_EXECUTOR=native
export LOBO_SKIP_COMFY=1
export LOBO_WAN=1
export LOBO_LTX23=0
export LOBO_MUSIC=0
export LOBO_UNLOAD_MODELS="${LOBO_UNLOAD_MODELS:-1}"
export WAN_LOW_VRAM="${WAN_LOW_VRAM:-1}"
export MODE=wan-native
export LOBO_MODE=wan-native
export WAN_MODEL_ROOT=/workspace/wan-models
export WAN_REPO=/workspace/Wan2.2
export HF_HUB_DISABLE_XET=1
export PYTORCH_CUDA_ALLOC_CONF="${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}"
unset HF_ENDPOINT
: "${LOBO_SECRET:?LOBO_SECRET missing}"
HN="${LOBO_HOSTNAME:-loboforge-wan-native-${CONTAINER_ID:-box}}"
pids=$(pgrep -f '[l]oboforge_agent_eventforge.py' || true)
if [ -n "${pids:-}" ]; then kill $pids 2>/dev/null || true; sleep 1; kill -9 $pids 2>/dev/null || true; fi
tmux kill-session -t loboforge-agent 2>/dev/null || true
sleep 1
nohup /venv/main/bin/python3 /workspace/loboforge_agent_eventforge.py \
  --secret "${LOBO_SECRET}" \
  --hostname "${HN}" \
  --capability wan >> /workspace/agent.log 2>&1 &
echo "agent_pid=$! hostname=${HN}"
