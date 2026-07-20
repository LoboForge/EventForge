#!/usr/bin/env bash
# Onstart entry for Wan 2.2 native boxes (served at /agent/provision_wan_native.sh).
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

mkdir -p /workspace
cd /workspace

# Fail fast before any downloads — tiny Vast containers wedge at ~32–45GB.
WAN_NEED_GB=200
WAN_TOTAL_GB=$(df -Pm / 2>/dev/null | awk 'NR==2 {print int($2/1024)}' || echo "")
if [[ -n "$WAN_TOTAL_GB" && "$WAN_TOTAL_GB" =~ ^[0-9]+$ && "$WAN_TOTAL_GB" -lt "$WAN_NEED_GB" ]]; then
  echo "FATAL: Container disk ${WAN_TOTAL_GB}GB < ${WAN_NEED_GB}GB required for wan-native. The native runner (LOBO_SKIP_COMFY=1, no ComfyUI) needs the full Wan2.2-I2V-A14B checkpoint (high 57GB + low 57GB + umt5 T5 11.4GB + VAE 0.5GB = ~126GB) plus Wan2.2 repo/venv (~12GB), lightning LoRAs (~1.5GB), hf staging and video temp. Healthy boxes run on 200GB+ (ref inst 45013488 ~23GB free on 200GB). A 170GB box (inst 45255378, 2026-07-18) filled up before the umt5 text-encoder finished and never became claim-ready. Re-rent with disk>=220GB (256GB recommended)." | tee -a /workspace/provision.log
  exit 1
fi

export LOBO_EXECUTOR="${LOBO_EXECUTOR:-native}"
export LOBO_SKIP_COMFY="${LOBO_SKIP_COMFY:-1}"
export LOBO_WAN="${LOBO_WAN:-1}"
export LOBO_LTX23="${LOBO_LTX23:-0}"
export LOBO_MUSIC="${LOBO_MUSIC:-0}"
export LOBO_UNLOAD_MODELS="${LOBO_UNLOAD_MODELS:-}"
export MODE="${MODE:-wan-native}"
export LOBO_MODE="${LOBO_MODE:-wan-native}"
export WAN_MODEL_ROOT="${WAN_MODEL_ROOT:-/workspace/wan-models}"
export WAN_REPO="${WAN_REPO:-/workspace/Wan2.2}"

LOBO_SECRET="${LOBO_SECRET:-change-me-in-admin}"
if [[ -z "${LOBO_INSTANCE_ID:-}" ]]; then
  LOBO_INSTANCE_ID="${CONTAINER_ID:-unknown}"
fi
HF_TOKEN="${HF_TOKEN:-}"
export LOBO_SECRET LOBO_INSTANCE_ID HF_TOKEN HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"

PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"

# Durable hf-hub pin (incident 2026-07-18): transformers 4.x REQUIRES huggingface_hub<1.0, but
# `pip install -U peft` and the child `python -m loboforge_worker` provisioning (Wan2.2
# requirements + agent deps) silently upgrade hf-hub to 1.24.0. That makes the native Wan runner
# fail EVERY job with "huggingface-hub>=0.30.0,<1.0 is required". Export PIP_CONSTRAINT so every
# pip install in this process tree (including child python workers) is blocked from installing
# hf-hub 1.x. Skipped only when transformers 5.x is present (which needs hf-hub 1.x).
if ! "$PY" -c 'import transformers,sys; sys.exit(0 if int(transformers.__version__.split(".")[0])>=5 else 1)' 2>/dev/null; then
  echo 'huggingface_hub>=0.34.0,<1.0' > /workspace/pip-constraints.txt
  export PIP_CONSTRAINT="/workspace/pip-constraints.txt"
  echo "hf-hub: PIP_CONSTRAINT=/workspace/pip-constraints.txt (transformers 4.x — block hf-hub 1.x)" | tee -a /workspace/provision.log
fi

EF_BASE="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
EF_BASE="${EF_BASE%/}"
BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
BASE="${BASE%/}"
if [[ "$BASE" == *eventforge.loboforge.com* ]]; then
  BASE="https://www.loboforge.com"
fi
export LOBO_BASE_URL="$BASE"
LF_UA="LoboForge-Worker/1.1"

lobo_pin_hf_hub() {
  local py="${1:-$PY}"
  if "$py" -c 'import transformers; exit(0 if int(transformers.__version__.split(".")[0]) >= 5 else 1)' 2>/dev/null; then
    echo "hf-hub: transformers 5.x — keeping hf-hub 1.x" | tee -a /workspace/provision.log
    return 0
  fi
  echo "hf-hub: pinning >=0.34,<1.0 for transformers 4.x" | tee -a /workspace/provision.log
  "$py" -m pip install -q "huggingface_hub>=0.34.0,<1.0" 2>/dev/null || true
}



# VRAM-aware offload defaults: GPUs below 48GB must not warm both MoE experts.
lobo_detect_wan_vram_gb() {
  local mib gb
  mib="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1 | tr -d " " || true)"
  if [[ -n "$mib" && "$mib" =~ ^[0-9]+$ ]]; then
    gb=$(( (mib + 512) / 1024 ))
    echo "$gb"
    return 0
  fi
  echo "0"
}

lobo_apply_wan_vram_env() {
  local gb="${1:-0}"
  export WAN_VRAM_GB="$gb"
  if [[ -z "${LOBO_UNLOAD_MODELS:-}" ]]; then
    # The native bf16 A14B stack (high 28GB + low 28GB + umt5 + activations) does NOT
    # fit warm even on an 80GB A100 — it OOMs every job. Only skip expert-swap when the
    # card is big enough for the whole warm stack (>=140GB, effectively never on one GPU).
    # This matches the active durable-env selection below; 48GB was wrong (warm OOM).
    if [[ "$gb" -ge 140 ]]; then
      export LOBO_UNLOAD_MODELS=0
      export WAN_LOW_VRAM=0
    else
      export LOBO_UNLOAD_MODELS=1
      export WAN_LOW_VRAM=1
    fi
  fi
  echo "wan-vram: ${gb}GB unload_models=${LOBO_UNLOAD_MODELS:-?} low_vram=${WAN_LOW_VRAM:-?}" | tee -a /workspace/provision.log
}

lobo_apply_wan_expert_swap_patch() {
  local py="${PY:-/venv/main/bin/python3}"
  for patch_url in "${EF_BASE}/agent/force_expert_swap.py" "${BASE}/agent/force_expert_swap.py"; do
    if lf_fetch "$patch_url" /tmp/force_expert_swap.py 2>/dev/null; then break; fi
  done
  if [[ ! -f /tmp/force_expert_swap.py ]]; then
    echo "WARN: force_expert_swap.py unavailable — skipping runner patch" | tee -a /workspace/provision.log
    return 0
  fi
  "$py" /tmp/force_expert_swap.py 2>&1 | tee -a /workspace/provision.log || {
    echo "WARN: force_expert_swap.py failed" | tee -a /workspace/provision.log
    return 0
  }
}

lobo_write_start_wan_agent_script() {
  cat > /workspace/start-wan-agent.sh <<'AGENT'
#!/bin/bash
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
nohup /venv/main/bin/python3 /workspace/loboforge_agent_eventforge.py   --secret "${LOBO_SECRET}"   --hostname "${HN}"   --capability wan >> /workspace/agent.log 2>&1 &
echo "agent_pid=$! hostname=${HN}"
AGENT
  chmod +x /workspace/start-wan-agent.sh
}


lf_fetch() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1 && curl -fsSL -A "$LF_UA" "$url" -o "$dest" 2>/dev/null; then return 0; fi
  LOBO_FETCH_URL="$url" LOBO_FETCH_DEST="$dest" LOBO_FETCH_UA="$LF_UA" "$PY" - <<'PY'
import os, urllib.request
req = urllib.request.Request(os.environ["LOBO_FETCH_URL"], headers={"User-Agent": os.environ.get("LOBO_FETCH_UA", "LoboForge-Worker/1.1")})
with urllib.request.urlopen(req, timeout=120) as resp, open(os.environ["LOBO_FETCH_DEST"], "wb") as out:
    out.write(resp.read())
PY
}

# Shared bootstrap helpers (forge-queue install, env merge, agent fetch).
for bootstrap_url in "${EF_BASE}/agent/worker-bootstrap-env.sh" "${BASE}/agent/worker-bootstrap-env.sh"; do
  if lf_fetch "$bootstrap_url" /workspace/worker-bootstrap-env.sh; then break; fi
done
if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/workspace/worker-bootstrap-env.sh
  source /workspace/worker-bootstrap-env.sh
  type lobo_ensure_ops_ssh &>/dev/null && lobo_ensure_ops_ssh || true
  type lobo_install_forge_queue_sdk &>/dev/null && lobo_install_forge_queue_sdk "$PY" || true
  type lobo_fetch_agent_scripts &>/dev/null && lobo_fetch_agent_scripts /workspace || true
  if type lobo_write_persisted_env &>/dev/null; then
    lobo_write_persisted_env /workspace/.loboforge-env
  fi
fi

# ---------------------------------------------------------------------------
# Durable critical env (incident 2026-07-18, inst 45265221 / 45265222).
# The long-running agent is (re)started by the cron watchdog and by the
# loboforge_worker module -- NOT by this provision shell, and cron does NOT
# inherit this shell's environment. Those launchers ONLY `source
# /workspace/.loboforge-env`, but lobo_write_persisted_env persists just a
# whitelist and DROPS the three vars below. Without them:
#   1. PIP_CONSTRAINT missing -> job-time `pip install` (peft/diffusers/Wan2.2
#      deps) silently upgrades huggingface_hub to 1.x, and the transformers 4.x
#      native runner then fails EVERY job with
#      "huggingface-hub>=0.30.0,<1.0 is required ... found 1.24.0".
#   2. PYTORCH_CUDA_ALLOC_CONF missing -> VRAM fragments and the runner OOMs.
#   3. LOBO_UNLOAD_MODELS=0 (warm) is fatal for the NATIVE bf16 A14B stack:
#      high 28GB + low 28GB + umt5 ~11GB + VAE + activations keeps BOTH experts
#      resident (~80GB) and OOMs on every job even on an 80GB A100 (observed
#      80843/81153 MiB used). The fp8/Comfy pipeline can run warm on 80GB; the
#      native runner cannot -- it needs expert-swap (unload=1). Only skip swap on
#      a card big enough for the whole warm stack (>=140GB, i.e. effectively
#      never on one GPU). Set LOBO_UNLOAD_MODELS explicitly to override.
# Append idempotently so EVERY launcher (cron, module, manual) gets them.
# ---------------------------------------------------------------------------
lobo_persist_env_kv() {
  local key="$1" val="$2" file="/workspace/.loboforge-env"
  touch "$file"
  if grep -q "^export ${key}=" "$file" 2>/dev/null; then
    sed -i "s|^export ${key}=.*|export ${key}=\"${val}\"|" "$file"
  else
    echo "export ${key}=\"${val}\"" >> "$file"
  fi
}
if [[ -z "${PIP_CONSTRAINT:-}" ]] && ! "$PY" -c 'import transformers,sys; sys.exit(0 if int(transformers.__version__.split(".")[0])>=5 else 1)' 2>/dev/null; then
  echo 'huggingface_hub>=0.34.0,<1.0' > /workspace/pip-constraints.txt
  export PIP_CONSTRAINT="/workspace/pip-constraints.txt"
fi
[[ -n "${PIP_CONSTRAINT:-}" ]] && lobo_persist_env_kv PIP_CONSTRAINT "${PIP_CONSTRAINT}"
lobo_persist_env_kv PYTORCH_CUDA_ALLOC_CONF "${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}"
wan_vram_gb="$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1 | awk '{print int(($1+512)/1024)}')"
if [[ -n "${LOBO_UNLOAD_MODELS:-}" && "${LOBO_UNLOAD_MODELS}" != "0" ]]; then
  wan_unload="${LOBO_UNLOAD_MODELS}"
elif [[ -n "${wan_vram_gb:-}" && "${wan_vram_gb}" -ge 140 ]]; then
  wan_unload=0
else
  wan_unload=1
fi
lobo_persist_env_kv LOBO_UNLOAD_MODELS "${wan_unload}"
echo "durable-env: unload=${wan_unload} vram=${wan_vram_gb:-?}GB alloc=expandable_segments pip_constraint=${PIP_CONSTRAINT:-none}" | tee -a /workspace/provision.log

cuda_ok() { "$PY" -c 'import torch,sys; sys.exit(0 if (torch.cuda.is_available() and (torch.zeros(1,device="cuda")+1).item()==1.0) else 1)' >/dev/null 2>&1; }
if ! cuda_ok; then
  echo "CUDA smoke test failed — neutralizing forward-compat libcuda" | tee -a /workspace/provision.log
  mkdir -p /workspace/.cuda-compat-disabled
  for d in /usr/local/cuda/compat /usr/local/cuda-*/compat; do
    [[ -d "$d" ]] || continue
    mv "$d"/libcuda.so* "/workspace/.cuda-compat-disabled/" 2>/dev/null || true
  done
  ldconfig 2>/dev/null || true
fi

"$PY" -m pip install -q -U websockets aiohttp gdown peft safetensors 2>/dev/null || true
lobo_pin_hf_hub "$PY"

rm -rf /workspace/loboforge_worker
for tarball_url in "${EF_BASE}/agent/loboforge_worker.tar.gz" "${BASE}/agent/loboforge_worker.tar.gz"; do
  if lf_fetch "$tarball_url" /tmp/loboforge_worker.tar.gz 2>/dev/null; then break; fi
done
if [[ -f /tmp/loboforge_worker.tar.gz ]]; then
  TMP_EX="$(mktemp -d)"
  tar -xzf /tmp/loboforge_worker.tar.gz -C "$TMP_EX"
  rm -rf /workspace/loboforge_worker
  if [[ -f "$TMP_EX/loboforge_worker/__init__.py" ]]; then
    mv "$TMP_EX/loboforge_worker" /workspace/
  elif [[ -f "$TMP_EX/__init__.py" ]]; then
    mv "$TMP_EX" /workspace/loboforge_worker
  fi
  rm -rf "$TMP_EX" /tmp/loboforge_worker.tar.gz
  export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
  "$PY" -c "import loboforge_worker"
fi

# Do not let a partially refreshed or stale worker package claim Wan jobs. A bad
# runner previously reached generation with its module cache missing and failed
# every job with `name '_PIPELINE_CACHE' is not defined`.
"$PY" - <<'PY' || { echo "FATAL: installed WAN runner cache self-check failed" | tee -a /workspace/provision.log; exit 1; }
from loboforge_worker.inference.wan import runner

if not isinstance(getattr(runner, "_PIPELINE_CACHE", None), dict):
    raise RuntimeError("WAN runner _PIPELINE_CACHE is absent or invalid")
PY

# Disk + GPU gates (fatal — matches provision_gpu.sh / bootstrap_box.py).
"$PY" - <<'PY' || { echo "FATAL: disk preflight failed" | tee -a /workspace/provision.log; exit 1; }
import os, sys
from loboforge_worker.provision.disk import disk_preflight
d = disk_preflight(
    "wan-native",
    label=os.environ.get("LOBO_LABEL", ""),
    hostname=os.environ.get("LOBO_HOSTNAME", ""),
)
if not d.get("ok"):
    print(d.get("error") or "disk preflight failed", file=sys.stderr)
    sys.exit(1)
print(f"disk ok: {d.get('total_gb')}GB total need>={d.get('need_gb')}GB")
PY

"$PY" -m loboforge_worker preflight-gpu \
  --secret "$LOBO_SECRET" \
  --mode wan-native \
  --instance-id "$LOBO_INSTANCE_ID" \
  2>&1 | tee -a /workspace/provision.log \
  || { echo "FATAL: GPU preflight failed" | tee -a /workspace/provision.log; exit 1; }

for agent_file in loboforge_agent_eventforge.py loboforge_agent_sqs.py loboforge_agent_common.py loboforge_agent.py; do
  fetched=""
  for agent_url in "${EF_BASE}/agent/${agent_file}" "${BASE}/agent/${agent_file}"; do
    if lf_fetch "$agent_url" "/workspace/${agent_file}" 2>/dev/null; then fetched=1; break; fi
  done
  [[ -n "$fetched" || "$agent_file" == loboforge_agent.py ]] || echo "WARN: could not fetch ${agent_file}" | tee -a /workspace/provision.log
done

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

# Mandatory LoRA sync from loboforge.com before agent claims jobs.
LORA_MODE="video"
"$PY" -m loboforge_worker sync-loras \
  --base-url "$BASE" \
  --secret "$LOBO_SECRET" \
  --mode "$LORA_MODE" \
  2>&1 | tee -a /workspace/lora-sync.log || \
  echo "WARN: initial sync-loras failed — agent will retry via ef_lora_sync_loop" | tee -a /workspace/provision.log

if ! "$PY" -m loboforge_worker provision-wan-native --help 2>/dev/null | grep -q connect-only; then
  echo "ERROR: loboforge_worker missing provision-wan-native — deploy updated worker tarball" | tee -a /workspace/provision.log
  exit 1
fi

# Early fleet join — fatal on GPU/disk incompatibility so Vast box does not burn queue jobs.
if ! "$PY" -m loboforge_worker provision-wan-native --connect-only \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-wan-native}" \
  --hf-token "$HF_TOKEN" \
  2>&1 | tee -a /workspace/provision.log; then
  echo "FATAL: wan-native connect-only failed (GPU/disk/models)" | tee -a /workspace/provision.log
  exit 1
fi

nohup "$PY" -m loboforge_worker provision-wan-native --skip-agent-launch \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-wan-native}" \
  --hf-token "$HF_TOKEN" \
  >> /workspace/provision.log 2>&1 &
echo "background Wan native downloads pid=$!" | tee -a /workspace/provision.log

for wd_url in "${EF_BASE}/agent/wan-agent-watchdog.sh" "${BASE}/agent/wan-agent-watchdog.sh"; do
  lf_fetch "$wd_url" /workspace/wan-agent-watchdog.sh 2>/dev/null && break
done
chmod +x /workspace/wan-agent-watchdog.sh 2>/dev/null || true
service cron start 2>/dev/null || (command -v cron >/dev/null && pgrep -x cron >/dev/null || cron) 2>/dev/null || true
(crontab -l 2>/dev/null | grep -v wan-agent-watchdog.sh || true
 echo '*/5 * * * * bash /workspace/wan-agent-watchdog.sh >> /workspace/wan-watchdog.log 2>&1') | crontab - 2>/dev/null || true

lobo_pin_hf_hub "$PY"
