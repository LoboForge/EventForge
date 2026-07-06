#!/usr/bin/env bash
# =============================================================================
# LoboForge GPU Provisioning Script
# Run once on a fresh vast.ai ComfyUI instance to download required models +
# install + launch the LoboForge agent.
#
# Usage (typical):
#   bash provision_gpu.sh --mode image
#
# That's it. HF token + LoboForge secret are baked in below — the admin can
# always override with env vars before invoking if they need to.
#
# Env-var overrides (rarely needed):
#   MODE                  — image | video | music | all (default all)
#   HF_TOKEN              — overrides the baked-in HuggingFace token
#   LOBO_SECRET           — overrides the baked-in LoboForge node secret
#   LOBO_SERVER           — websocket URL (default wss://www.loboforge.com)
#   LOBO_BASE_URL         — http base URL for agent self-fetch (default https://www.loboforge.com)
#   LOBO_COMFYUI_TOKEN    — explicit ComfyUI token; auto-detected if not set
#   LOBO_LTX23            — 1 to download LTX 2.3 AV stack (video+audio output, default 0)
#   LOBO_WAN              — 1 to download Wan 2.2 i2v+t2v on video boxes (default 1)
#   LOBO_MUSIC            — 1 to download ACE-Step music stack (default: same as mode; set 0 on Wan-only boxes)
#
# Recommended fleet split (Wan+i2v+t2v ≈ 56GB UNets; LTX+music ≈ another large stack):
#   Wan box:  MODE=video LOBO_WAN=1 LOBO_LTX23=0 LOBO_MUSIC=0
#   AV box:   MODE=video LOBO_WAN=0 LOBO_LTX23=1 LOBO_MUSIC=1  (+ Ltx23:ProvisionEnabled on API)
#
# Idempotent. Skips already-downloaded files and won't double-launch the agent.
#
# Vast extra_env reboot note: Vast may not pass extra_env to nohup/curl|bash children.
# Onstart inline-exports AWS_* / FORGE_QUEUE_* before this script; agent loops persist
# creds in /workspace/.loboforge-env for post-reboot restarts.
# =============================================================================

set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

# ── Baked-in defaults ────────────────────────────────────────────────────────
# HF_TOKEN from Vast extra_env — never commit tokens to git.
DEFAULT_LOBO_SECRET='change-me-in-admin'

# forge-queue SQS defaults (IAM creds — no IoT/MQTT certs).
# Jobs: forge-queue SQS (loboforge_agent_sqs.py). API: check-in + LoRA prefetch only.
lobo_forge_queue_env_defaults() {
  export FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-${AWS_REGION:-us-east-2}}"
  export FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-}"
  export FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"
  if [[ -n "${FORGE_QUEUE_ACCESS_KEY:-}" && -n "${FORGE_QUEUE_SECRET_KEY:-}" ]]; then
    export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-$FORGE_QUEUE_ACCESS_KEY}"
    export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-$FORGE_QUEUE_SECRET_KEY}"
  fi
  export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-$FORGE_QUEUE_REGION}"
}
lobo_forge_queue_env_defaults

lobo_require_forge_queue_aws_creds() {
  if [[ -n "${AWS_ACCESS_KEY_ID:-}" && -n "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
    return 0
  fi
  die "ForgeQueueWorker IAM required — set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY in Vast extra_env. Admin: Fleet:ForgeQueue:AccessKey/SecretKey in appsettings.Secrets.json."
}

install_forge_queue_sdk() {
  local sdk_dir="${FORGE_QUEUE_SDK_DIR:-/workspace/forge-queue/sdk}"
  resolve_pybin
  if [[ ! -f "$sdk_dir/pyproject.toml" ]]; then
    if curl -fsSL "$LOBO_BASE_URL/agent/forge-queue-sdk.tar.gz" -o /tmp/forge-queue-sdk.tar.gz 2>/dev/null; then
      mkdir -p /workspace
      tar -xzf /tmp/forge-queue-sdk.tar.gz -C /workspace
      rm -f /tmp/forge-queue-sdk.tar.gz
      sdk_dir="/workspace/forge-queue/sdk"
    fi
  fi
  if [[ -f "$sdk_dir/pyproject.toml" ]]; then
    "$PYBIN" -m pip install -q -U -e "$sdk_dir" 2>/dev/null || true
  fi
}

resolve_gen_queue_mode() {
  [[ -n "${LOBO_GEN_QUEUE:-}" ]] && return 0
  [[ -n "${LOBO_SECRET:-}" && "${LOBO_SECRET}" != "change-me-in-admin" ]] || return 0
  local _gq_json
  _gq_json="$(curl -sf --max-time 10 "$LOBO_BASE_URL/api/agent/gen-queue-mode?secret=$LOBO_SECRET" || echo '{}')"
  LOBO_GEN_QUEUE="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('mode',''))" 2>/dev/null || true)"
  if [[ -z "${LOBO_GEN_QUEUE_PREFIX:-}" ]]; then
    LOBO_GEN_QUEUE_PREFIX="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('queuePrefix',''))" 2>/dev/null || true)"
  fi
  export LOBO_GEN_QUEUE="${LOBO_GEN_QUEUE:-sqs}"
  export LOBO_GEN_QUEUE_PREFIX="${LOBO_GEN_QUEUE_PREFIX:-}"
}

# ── Colours ───────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'
info()    { echo -e "${CYAN}[INFO]${NC}  $*"; }
success() { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
die()     { echo -e "${RED}[ERROR]${NC} $*"; exit 1; }

# True if file exists AND is at least `min_bytes` (default 1KB) in size.
# Pass a per-file minimum for big models — the 11.7MB partial Z-Image UNet
# on 2026-05-21 passed the 1KB floor and got skipped on re-runs.
# Usage: file_present /path/to/file [min_bytes]
file_present() {
    local f="$1" min="${2:-1024}"
    [[ -f "$f" ]] && [[ "$(stat -c%s "$f" 2>/dev/null || echo 0)" -ge $min ]]
}

# Sane minimums for the large model files this script downloads. Sized
# conservatively (well below the real file size) so a partial download
# never passes the check, but a legit file always does.
MIN_UNET=$((1024 * 1024 * 1024))           # 1GB — UNets are 7-14GB
MIN_LARGE_TE=$((1024 * 1024 * 1024))        # 1GB — Klein/Qwen text encoders
MIN_VAE=$((100 * 1024 * 1024))              # 100MB — VAEs are 300-500MB
MIN_LIGHT_LORA=$((100 * 1024 * 1024))       # 100MB — lightning LoRAs ~600MB
MIN_CHECKPOINT=$((500 * 1024 * 1024))       # 500MB — ACE-Step ~3.5GB
MIN_CHROMA=$((10 * 1024 * 1024 * 1024))         # 10GB — Chroma HD UNet


# pull_active_loras_from_api — shared by early Klein sync + final pass
# Best-effort; idempotent skips for files already on disk.
pull_active_loras_from_api() {
    local tag="${1:-loras.pull}"
    if [[ -z "${LOBO_SECRET:-}" ]]; then
        warn "LOBO_SECRET not set — skipping active-LoRA pull ($tag)."
        return 0
    fi
    if ! command -v gdown >/dev/null 2>&1; then
        pip install -q -U gdown 2>&1 | tail -2 || true
    fi
    local LORAS_URL="$LOBO_BASE_URL/api/agent/active-loras?modes=$MODE&secret=$LOBO_SECRET"
    local LORA_JSON
    LORA_JSON=$(curl -sS -m 30 "$LORAS_URL" 2>/dev/null || echo "")
    if [[ -z "$LORA_JSON" || "$LORA_JSON" == "[]" ]]; then
        warn "No active LoRAs from LoboForge ($tag)."
        status_post "$tag" "warn" "endpoint returned empty/unreachable; mode=$MODE"
        return 0
    fi
    local LORA_COUNT
    LORA_COUNT=$(printf '%s' "$LORA_JSON" | python3 -c "import json,sys; print(len(json.load(sys.stdin)))" 2>/dev/null || echo "0")
    info "LoboForge returned $LORA_COUNT active LoRA(s) ($tag)."
    printf '%s' "$LORA_JSON" | python3 -c "
import json, sys
for la in json.load(sys.stdin):
    fp = (la.get('file_path') or '').strip()
    su = (la.get('source_url') or '').strip()
    if not fp or not su: continue
    print(f'{fp}|{su}')
" | while IFS='|' read -r FILE_PATH SOURCE_URL; do
        local REL="$FILE_PATH"
        case "$REL" in
            loras/*) ;;
            */*)     REL="loras/$REL" ;;
            *)       REL="loras/$REL" ;;
        esac
        local DEST="$MODELS/$REL"
        mkdir -p "$(dirname "$DEST")"
        if [[ -f "$DEST" ]] && [[ "$(stat -c%s "$DEST" 2>/dev/null || echo 0)" -gt 1048576 ]]; then
            continue
        fi
        info "Downloading LoRA: $REL  ←  $SOURCE_URL"
        case "$SOURCE_URL" in
            *huggingface.co*)
                local HF_PARSE HF_REPO HF_FILE TMP FOUND
                HF_PARSE=$(echo "$SOURCE_URL" | python3 -c "
import re, sys
m = re.match(r'https?://huggingface\.co/([^/]+/[^/]+)/(resolve|blob)/[^/]+/(.+?)(\?.*)?$', sys.stdin.read().strip())
print(f'{m.group(1)}|{m.group(3)}' if m else '')
" 2>/dev/null || echo "")
                if [[ -n "$HF_PARSE" ]]; then
                    IFS='|' read -r HF_REPO HF_FILE <<< "$HF_PARSE"
                    TMP="/tmp/hf_lora_$$_$(basename "$DEST")"
                    mkdir -p "$TMP"
                    if hf download "$HF_REPO" "$HF_FILE" --local-dir "$TMP" 2>/dev/null; then
                        FOUND=$(find "$TMP" -name "$(basename "$HF_FILE")" -type f 2>/dev/null | head -1)
                        [[ -n "$FOUND" ]] && mv -f "$FOUND" "$DEST"
                    fi
                    rm -rf "$TMP"
                else
                    curl -fsSL --max-time 600 -o "$DEST" "$SOURCE_URL" || rm -f "$DEST"
                fi
                ;;
            *drive.google.com*)
                command -v gdown >/dev/null 2>&1 && gdown --no-cookies "$SOURCE_URL" -O "$DEST" 2>/dev/null || rm -f "$DEST"
                ;;
            *)
                curl -fL --max-time 600 -o "$DEST" "$SOURCE_URL" || rm -f "$DEST"
                ;;
        esac
    done
    status_post "$tag" "ok" "active LoRAs pulled (mode=$MODE, count=$LORA_COUNT)"
}

# ── Status reporting to LoboForge ─────────────────────────────────────────────
# Each major step POSTs a one-line status back so the admin sees provisioning
# progress in /admin → Vast.ai → instance row without SSH'ing in. Best-effort:
# if LoboForge is unreachable or the secret is missing, we keep going — the
# rented box still provisions, the admin just doesn't get real-time updates.
#
# Endpoint: POST $LOBO_BASE_URL/api/agent/provision-status
#   query: ?secret=<LOBO_SECRET>&instance_id=<vast_instance_id>
#   body : { step, level, detail }
#
# Vast.ai injects $VAST_CONTAINERLABEL and $CONTAINER_ID env vars; we prefer
# the explicit LOBO_INSTANCE_ID then fall back. status_post() is silent on
# success and only echoes a warn on transport failure.
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-${CONTAINER_ID:-${VAST_CONTAINERLABEL:-unknown}}}"

status_post() {
    # $1 = step ("models.zimage", "agent.fetch", "token.detect", "complete", etc)
    # $2 = level ("ok" | "warn" | "error")
    # $3 = free-text detail
    local step="$1" level="$2" detail="$3"
    if [[ -z "${LOBO_SECRET:-}" ]]; then return 0; fi
    # Escape quotes + backslashes for JSON
    local esc_detail
    esc_detail=$(printf '%s' "$detail" | sed 's/\\/\\\\/g; s/"/\\"/g' | tr '\n' ' ')
    local body="{\"step\":\"$step\",\"level\":\"$level\",\"detail\":\"$esc_detail\"}"
    curl -sS -m 8 \
        -X POST \
        -H "Content-Type: application/json" \
        --data "$body" \
        "$LOBO_BASE_URL/api/agent/provision-status?secret=$LOBO_SECRET&instance_id=$LOBO_INSTANCE_ID" \
        >/dev/null 2>&1 || warn "status_post($step) failed — admin won't see this step"
}

# ── Heartbeat helpers ────────────────────────────────────────────────────────
# Status callbacks above only fire at coarse milestones (models.image,
# models.video, agent.fetch, ...) so the admin's live feed goes dark for
# 10-25 min during big downloads — indistinguishable from "stuck" until
# you SSH in. start_heartbeat spawns a background subshell that posts
# `<step>.heartbeat` every $HEARTBEAT_SECS seconds with current disk usage,
# /tmp HF download progress, and model-dir size. stop_heartbeat kills it.
#
# Why background subshell + PID file:
#   - Bash traps + signals would otherwise compete with `set -e` handling.
#   - PID file in /tmp survives if the script dies mid-download; the agent
#     init can re-clean it.
HEARTBEAT_SECS="${HEARTBEAT_SECS:-60}"
HEARTBEAT_PID_FILE="/tmp/lobo_provision_heartbeat.pid"

start_heartbeat() {
    # $1 = step prefix (e.g. "models.image"). Heartbeat posts "<step>.heartbeat".
    local step="$1"
    [[ -z "${LOBO_SECRET:-}" ]] && return 0    # no secret = nothing to post
    stop_heartbeat  # idempotent — kill any prior heartbeat first
    (
        # Detach: closing stdin + redirecting fds so the parent shell's
        # `wait` / SIGTERM propagation behave predictably.
        exec </dev/null >/dev/null 2>&1
        while true; do
            sleep "$HEARTBEAT_SECS"
            # Compose detail: $disk used %, biggest /tmp HF dir size, models
            # root size. Cheap to compute, very informative when staring
            # at the live feed wondering "is it actually downloading?"
            local disk_pct hf_size models_size
            disk_pct=$(df -P /workspace 2>/dev/null | awk 'NR==2 {print $5}' || echo "?")
            hf_size=$(du -sh /tmp/hf_* 2>/dev/null | sort -h | tail -1 | awk '{print $1}' || echo "")
            models_size=$(du -sh "$MODELS" 2>/dev/null | awk '{print $1}' || echo "")
            local detail="disk=$disk_pct models=${models_size:-?}"
            [[ -n "$hf_size" ]] && detail="$detail hf_tmp=$hf_size"
            status_post "${step}.heartbeat" "ok" "$detail"
        done
    ) &
    echo "$!" > "$HEARTBEAT_PID_FILE"
}

stop_heartbeat() {
    if [[ -f "$HEARTBEAT_PID_FILE" ]]; then
        local pid
        pid=$(cat "$HEARTBEAT_PID_FILE" 2>/dev/null || echo "")
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            kill "$pid" 2>/dev/null || true
            # Give it a beat to exit before unlinking the pid file.
            wait "$pid" 2>/dev/null || true
        fi
        rm -f "$HEARTBEAT_PID_FILE"
    fi
}

# Make sure a hung heartbeat doesn't outlive the script on early exit / die.
trap 'stop_heartbeat' EXIT

# ── GPU compatibility + naming ─────────────────────────────────────────────────
# Our vastai/comfy:v0.15.1-cuda-12.9-py312 stack ships PyTorch wheels built for
# sm_70+ (Volta and newer). Tesla P100 (sm_60) connects fine, lists models, then
# every Comfy job dies with "CUDA error: no kernel image is available for
# execution on the device". Catch that BEFORE downloading 50GB and registering
# with the queue. Incident: 2026-05-28, two P100 rents failed 84 jobs.
MIN_COMPUTE_MAJOR="${MIN_COMPUTE_MAJOR:-7}"

resolve_pybin() {
    # Prefer ComfyUI's venv on vast.ai images — system python3 has no torch and
    # breaks verify_pytorch_cuda / agent launch after a mid-life re-provision.
    if [[ -n "${LOBO_PYBIN:-}" ]]; then
        PYBIN="$LOBO_PYBIN"
    elif [[ -x /venv/main/bin/python ]]; then
        PYBIN="/venv/main/bin/python"
    else
        PYBIN="$(command -v python3 || command -v python || true)"
    fi
    [[ -n "$PYBIN" ]] || die "Python not found. Install Python or export LOBO_PYBIN=/path/to/python."
}

LOBO_HOSTNAME_FILE="${LOBO_HOSTNAME_FILE:-/workspace/.loboforge-hostname}"

sanitize_hostname_part() {
    printf '%s' "$1" | tr ' ' '-' | tr -cd '[:alnum:]-_' | tr '[:upper:]' '[:lower:]' | cut -c1-48
}

resolve_instance_suffix() {
    local inst="${LOBO_INSTANCE_ID:-}"
    if [[ -z "$inst" || "$inst" == "unknown" ]]; then
        inst="${CONTAINER_ID:-${VAST_CONTAINERLABEL:-$(hostname -s 2>/dev/null || hostname)}}"
    fi
    inst="$(sanitize_hostname_part "$inst")"
    [[ -z "$inst" ]] && inst="unknown"
    # Vast contract ids are numeric — use the tail so hostnames stay readable.
    if [[ "$inst" =~ ^[0-9]+$ ]]; then
        printf '%s' "${inst: -8}"
    else
        printf '%s' "${inst:0:8}"
    fi
}

derive_agent_hostname() {
    # Stable per box — never collide when many rents share the same Vast label.
    if [[ -f "$LOBO_HOSTNAME_FILE" ]]; then
        cat "$LOBO_HOSTNAME_FILE"
        return
    fi

    local suffix prefix mode="${MODE:-all}"
    suffix="$(resolve_instance_suffix)"

    if [[ -n "${LOBO_LABEL:-}" ]]; then
        prefix="$(sanitize_hostname_part "$LOBO_LABEL")"
    elif [[ -n "${LOBO_HOSTNAME:-}" ]]; then
        prefix="$(sanitize_hostname_part "$LOBO_HOSTNAME")"
        # Legacy rents set LOBO_HOSTNAME=label only — strip a trailing suffix if present.
        if [[ "$prefix" == *"-${suffix}" ]]; then
            prefix="${prefix%-${suffix}}"
        fi
    else
        local gpu_slug
        gpu_slug=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1 \
            | tr ' ' '-' | tr -cd '[:alnum:]-' | tr '[:upper:]' '[:lower:]' | cut -c1-28)
        [[ -z "$gpu_slug" ]] && gpu_slug="gpu"
        prefix="loboforge-$(sanitize_hostname_part "${mode//,/-}")-${gpu_slug}"
    fi

    local hn="${prefix}-${suffix}"
    hn="$(sanitize_hostname_part "$hn")"
    [[ -n "$hn" ]] && printf '%s' "$hn" > "$LOBO_HOSTNAME_FILE" 2>/dev/null || true
    printf '%s' "$hn"
}

is_p100_gpu() {
    nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1 | grep -qi p100
}

fix_pytorch_for_p100() {
    resolve_pybin
    if [[ -x /venv/main/bin/python ]]; then
        PYBIN="/venv/main/bin/python"
    fi
    info "P100 detected — reinstalling PyTorch cu118 (sm_60 kernel support)..."
    status_post "gpu.p100" "ok" "reinstalling torch cu118"
    "$PYBIN" -m pip install -q -U pip
    "$PYBIN" -m pip install --force-reinstall torch torchvision \
        --index-url https://download.pytorch.org/whl/cu118 \
        2>&1 | tail -8 || warn "P100 torch reinstall had errors — verify below"
}

assert_gpu_compatible() {
    if ! command -v nvidia-smi &>/dev/null; then
        die "nvidia-smi not found — this box has no usable NVIDIA driver/GPU."
    fi

    local gpu_name compute_cap gpu_lower major
    gpu_name=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1 | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
    compute_cap=$(nvidia-smi --query-gpu=compute_cap --format=csv,noheader 2>/dev/null | head -1 | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
    gpu_lower=$(printf '%s' "$gpu_name" | tr '[:upper:]' '[:lower:]')

    if [[ -z "$gpu_name" || "$gpu_name" == "Unknown" ]]; then
        die "Could not detect GPU name via nvidia-smi — aborting before model downloads."
    fi

    # Block GPUs with no fix path (P100 is fixed via cu118 torch below).
    case "$gpu_lower" in
        *k80*|*m40*|*m10*)
            die "GPU '$gpu_name' is too old for our Comfy/PyTorch stack. Rent V100, A4000, or RTX instead."
            ;;
    esac

    if is_p100_gpu; then
        fix_pytorch_for_p100
    elif [[ -n "$compute_cap" ]]; then
        major="${compute_cap%%.*}"
        if [[ "$major" =~ ^[0-9]+$ ]] && (( major < MIN_COMPUTE_MAJOR )); then
            die "GPU '$gpu_name' (compute capability $compute_cap) is below minimum sm_${MIN_COMPUTE_MAJOR}0 for our default PyTorch build. Pick a newer GPU offer on vast.ai."
        fi
    else
        warn "Could not read GPU compute capability — continuing based on GPU name only."
    fi

    info "GPU OK: $gpu_name (compute ${compute_cap:-unknown})"
    status_post "gpu.check" "ok" "gpu=$gpu_name compute=${compute_cap:-unknown}"
}

verify_pytorch_cuda() {
    resolve_pybin
    info "Verifying PyTorch can execute CUDA kernels on this GPU (via $PYBIN)..."
    if ! "$PYBIN" - <<'PY' >/tmp/lobo_cuda_test.log 2>&1
import sys
try:
    import torch
except ImportError as e:
    print(f"torch import failed: {e}", file=sys.stderr)
    sys.exit(1)
if not torch.cuda.is_available():
    print("torch.cuda.is_available() is False", file=sys.stderr)
    sys.exit(2)
try:
    x = torch.zeros(1, device="cuda")
    _ = (x + 1).item()
    torch.cuda.synchronize()
except Exception as e:
    print(f"CUDA kernel test failed: {e}", file=sys.stderr)
    sys.exit(3)
cap = torch.cuda.get_device_capability(0)
print(f"ok device={torch.cuda.get_device_name(0)!r} capability={cap[0]}.{cap[1]}")
PY
    then
        cat /tmp/lobo_cuda_test.log >&2 || true
        die "PyTorch CUDA smoke test FAILED on $(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1). This GPU cannot run Comfy jobs — rent V100/A4000/RTX instead."
    fi
    success "PyTorch CUDA smoke test passed ($(tail -1 /tmp/lobo_cuda_test.log 2>/dev/null || echo ok))."
    status_post "gpu.cuda" "ok" "$(tail -1 /tmp/lobo_cuda_test.log 2>/dev/null | tr '\n' ' ')"
}

count_comfy_models() {
    COMFYUI_PORT="${COMFYUI_PORT:-18188}"
    resolve_pybin
    "$PYBIN" - <<PY
import json, urllib.request
port = int("${COMFYUI_PORT:-18188}")
queries = [
    ("UNETLoader", "unet_name"),
    ("CheckpointLoaderSimple", "ckpt_name"),
    ("LoraLoader", "lora_name"),
    ("LoraLoaderModelOnly", "lora_name"),
    ("VAELoader", "vae_name"),
    ("CLIPLoader", "clip_name"),
]
total = 0
for node, param in queries:
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/object_info/{node}", timeout=8) as r:
            d = json.load(r)
        vals = d.get(node, {}).get("input", {}).get("required", {}).get(param, [[]])[0]
        if isinstance(vals, list):
            total += len(vals)
    except Exception:
        pass
print(total)
PY
}

wait_for_comfy_inventory() {
    local min_models="$1" timeout="${2:-300}" elapsed=0 count=0
    info "Waiting for ComfyUI to index at least $min_models model asset(s)..."
    while (( elapsed < timeout )); do
        count=$(count_comfy_models 2>/dev/null || echo 0)
        if [[ "$count" =~ ^[0-9]+$ ]] && (( count >= min_models )); then
            success "ComfyUI model inventory ready ($count assets, min $min_models)."
            status_post "comfyui.inventory" "ok" "count=$count min=$min_models"
            return 0
        fi
        sleep 10
        elapsed=$((elapsed + 10))
        info "ComfyUI model index: $count / $min_models (${elapsed}s)..."
    done
    die "ComfyUI never indexed enough models ($count/$min_models after ${timeout}s). Agent will NOT start — fix ComfyUI (/tmp/comfyui.log) and re-run."
}

required_comfy_model_count() {
    local min=0
    (( WANT_IMAGE )) && min=$((min + 6))
    (( WANT_VIDEO )) && min=$((min + 6))
    (( WANT_MUSIC )) && min=$((min + 1))
    (( min < 3 )) && min=3
    echo "$min"
}

assert_ready_for_agent() {
    local min_models
    min_models=$(required_comfy_model_count)
    if ! wait_for_comfyui 30; then
        die "ComfyUI is not healthy — agent will NOT start."
    fi
    verify_pytorch_cuda
    wait_for_comfy_inventory "$min_models" 300
    if [[ -d "$AGENT_DIR/loboforge_worker" ]] && "$PYBIN" -c "import loboforge_worker" 2>/dev/null; then
        info "Running Python fleet preflight (gpu + hostname)..."
        LOBO_MODE="$MODE" LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-}" \
            "$PYBIN" -m loboforge_worker preflight-gpu --secret "${LOBO_SECRET:-local}" --mode "$MODE" \
            || die "Python GPU preflight failed — agent will NOT start."
    fi
    return 0
}

assert_minimal_for_connect() {
    if ! wait_for_comfyui 30; then
        warn "ComfyUI not healthy yet — joining pool as provisioning (downloads continue in background)."
    fi
    verify_pytorch_cuda || die "PyTorch/CUDA check failed — agent will NOT start."
    if [[ -d "${AGENT_DIR:-/workspace}/loboforge_worker" ]] && "$PYBIN" -c "import loboforge_worker" 2>/dev/null; then
        info "Running Python fleet preflight (gpu + hostname)..."
        LOBO_MODE="$MODE" LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-}" \
            "$PYBIN" -m loboforge_worker preflight-gpu --secret "${LOBO_SECRET:-local}" --mode "$MODE" \
            || die "Python GPU preflight failed — agent will NOT start."
    fi
    return 0
}

COMFYUI_PORT="${COMFYUI_PORT:-18188}"
LOBO_AGENT_LAUNCHED=0
LOBO_EARLY_POOL_JOIN=0

wait_for_comfyui() {
    local timeout="$1"
    local elapsed=0
    while (( elapsed < timeout )); do
        local code
        code=$(curl -sS -m 3 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$COMFYUI_PORT/" 2>/dev/null || echo "000")
        if [[ "$code" =~ ^2 ]]; then return 0; fi
        sleep 5
        elapsed=$((elapsed + 5))
    done
    return 1
}

start_comfyui_manual() {
    supervisorctl stop comfyui 2>/dev/null || true
    tmux kill-session -t comfyui 2>/dev/null || true
    sleep 1
    local venv_activate='true'
    [[ -f /venv/main/bin/activate ]] && venv_activate='. /venv/main/bin/activate'
    tmux new-session -d -s comfyui "cd '$COMFY_DIR' && $venv_activate && LD_PRELOAD=libtcmalloc_minimal.so.4 python main.py --disable-auto-launch --port $COMFYUI_PORT --listen 127.0.0.1 --enable-cors-header 2>&1 | tee /tmp/comfyui.log"
}

ensure_comfyui_serving() {
    start_heartbeat "comfyui.up"
    info "Waiting up to 60s for supervisord-managed ComfyUI to respond..."
    if wait_for_comfyui 60; then
        success "ComfyUI is serving on $COMFYUI_PORT (via supervisord)."
        status_post "comfyui.up" "ok" "supervisord path"
    else
        warn "ComfyUI not responding under supervisord — bypassing with manual tmux"
        status_post "comfyui.up" "warn" "supervisord crash-loop suspected; starting manual tmux"
        start_comfyui_manual
        info "Waiting up to 180s for manual ComfyUI tmux to start serving..."
        if wait_for_comfyui 180; then
            success "ComfyUI is serving on $COMFYUI_PORT (via manual tmux bypass)."
            status_post "comfyui.up" "ok" "manual tmux bypass succeeded"
        else
            warn "ComfyUI did not respond on $COMFYUI_PORT — agent joins pool as provisioning."
            status_post "comfyui.up" "error" "ComfyUI not HTTP 200 yet — agent will connect as provisioning"
        fi
    fi
    stop_heartbeat
}

ensure_loboforge_worker_package() {
    local dir="${LOBO_AGENT_DIR:-/workspace}"
    mkdir -p "$dir"
    LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
    if [[ ! -f "$dir/loboforge_worker/__init__.py" ]]; then
        info "Fetching loboforge_worker for ComfyUI Lens upgrade..."
        if curl -fsSL "$LOBO_BASE_URL/agent/loboforge_worker.tar.gz" -o /tmp/loboforge_worker.tar.gz; then
            tar -xzf /tmp/loboforge_worker.tar.gz -C "$dir"
            rm -f /tmp/loboforge_worker.tar.gz
            success "loboforge_worker extracted to $dir/loboforge_worker/"
        else
            warn "loboforge_worker.tar.gz unavailable — cannot run Python Lens upgrade"
            return 1
        fi
    fi
    export AGENT_PARENT="$dir"
    export PYTHONPATH="${dir}${PYTHONPATH:+:$PYTHONPATH}"
    AGENT_DIR="$dir"
    return 0
}

upgrade_comfyui_for_lens() {
    [[ "${LOBO_SKIP_COMFYUI_UPGRADE:-}" == "1" ]] && { warn "LOBO_SKIP_COMFYUI_UPGRADE=1 — skipping ComfyUI pull"; return 0; }
    ensure_loboforge_worker_package || return 1
    resolve_pybin
    local iid
    iid="$(resolve_instance_suffix 2>/dev/null || echo "${LOBO_INSTANCE_ID:-unknown}")"
    info "Ensuring ComfyUI supports Lens + ACE-Step (via loboforge_worker)..."
    if "$PYBIN" -m loboforge_worker ensure-lens-comfyui \
        --secret "${LOBO_SECRET:-}" \
        --mode "${MODE:-all}" \
        --comfyui-http "${LOBO_COMFYUI_HTTP:-http://127.0.0.1:18188}" \
        --instance-id "$iid"; then
        success "ComfyUI Lens/ACE-Step support OK"
        return 0
    fi
    warn "ComfyUI upgrade failed — Lens and/or music jobs may fail until ComfyUI master is installed"
    status_post "comfyui.upgrade" "warn" "ensure-lens-comfyui failed (non-fatal; zimage/flux still OK)"
    return 0
}


lobo_fetch_agent_scripts() {
    local dir="${1:-${LOBO_AGENT_DIR:-/workspace}}"
    local base="${LOBO_BASE_URL:-https://www.loboforge.com}"
    mkdir -p "$dir"
    local f
    for f in loboforge_agent.py loboforge_agent_sqs.py loboforge_agent_common.py wd14_tagger.py; do
        if ! curl -fsSL -A 'LoboForge-Worker/1.1' "$base/agent/$f" -o "$dir/$f"; then
            [[ "$f" == "wd14_tagger.py" ]] && continue
            return 1
        fi
    done
    export LOBO_AGENT_DIR="$dir"
    export AGENT_DIR="$dir"
    export AGENT_PARENT="$dir"
    export PYTHONPATH="${dir}${PYTHONPATH:+:$PYTHONPATH}"
    return 0
}

lobo_verify_sqs_agent_imports() {
    local dir="${1:-${LOBO_AGENT_DIR:-/workspace}}"
    resolve_pybin
    PYTHONPATH="${dir}${PYTHONPATH:+:$PYTHONPATH}" \
        "$PYBIN" -c "import loboforge_agent_common; import loboforge_agent_sqs" 2>/dev/null
}

fetch_agent_bundle() {
    LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
    AGENT_DIR="${LOBO_AGENT_DIR:-/workspace}"
    mkdir -p "$AGENT_DIR"
    info "Fetching/updating agent scripts from $LOBO_BASE_URL/agent/..."
    resolve_gen_queue_mode
    if ! lobo_fetch_agent_scripts "$AGENT_DIR"; then
        status_post "agent.fetch" "error" "could not curl agent bundle from $LOBO_BASE_URL/agent/"
        return 1
    fi
    local agent_file="loboforge_agent_sqs.py"
    if [[ "${LOBO_GEN_QUEUE:-sqs}" != "sqs" ]]; then
        agent_file="loboforge_agent.py"
    fi
    AGENT_SCRIPT="$AGENT_DIR/$agent_file"
    if [[ "${LOBO_GEN_QUEUE:-sqs}" == "sqs" ]]; then
        install_forge_queue_sdk || true
        lobo_verify_sqs_agent_imports "$AGENT_DIR" || die "SQS agent imports failed — loboforge_agent_common missing"
    fi
    status_post "agent.fetch" "ok" "from $LOBO_BASE_URL/agent/ → $AGENT_DIR/"
    ensure_loboforge_worker_package || true
    if [[ ! -f "$AGENT_DIR/loboforge_worker/__init__.py" ]]; then
        curl -fsSL "$LOBO_BASE_URL/agent/loboforge_worker.tar.gz" -o /tmp/loboforge_worker.tar.gz \
            && tar -xzf /tmp/loboforge_worker.tar.gz -C "$AGENT_DIR" \
            && rm -f /tmp/loboforge_worker.tar.gz || true
    fi
    return 0
}

launch_loboforge_agent_tmux() {
    lobo_require_forge_queue_aws_creds
    local ready_mode="${1:-minimal}"
    TMUX_SESSION="${LOBO_TMUX_SESSION:-loboforge-agent}"
    AGENT_LOG="${LOBO_AGENT_LOG:-/workspace/loboforge-agent.log}"
    LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
    LOBO_HOSTNAME_VAR="$(derive_agent_hostname)"

    [[ -n "${LOBO_SECRET:-}" && -n "${AGENT_SCRIPT:-}" ]] || return 1
    command -v tmux &>/dev/null || return 1

    if [[ "$ready_mode" == "full" ]]; then
        assert_ready_for_agent || die "Readiness checks failed — agent NOT started."
    else
        assert_minimal_for_connect || die "Minimal connect checks failed — agent NOT started."
    fi

    tmux kill-session -t "$TMUX_SESSION" 2>/dev/null || true
    resolve_pybin
    _lo_bootstrap="/workspace/worker-bootstrap-env.sh"
    if [[ ! -f "$_lo_bootstrap" ]]; then
        curl -fsSL -A 'LoboForge-Worker/1.1' "${LOBO_BASE_URL:-https://www.loboforge.com}/agent/worker-bootstrap-env.sh" -o "$_lo_bootstrap" 2>/dev/null || true
    fi
    if [[ -f "$_lo_bootstrap" ]]; then
        # shellcheck source=/dev/null
        . "$_lo_bootstrap"
        export LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
        lobo_write_persisted_env /workspace/.loboforge-env || true
    fi


    LOBO_COMFYUI_HTTP="${LOBO_COMFYUI_HTTP:-http://127.0.0.1:18188}"
    LOBO_COMFYUI_WS="${LOBO_COMFYUI_WS:-ws://127.0.0.1:18188}"
    resolve_gen_queue_mode
    local AGENT_CMD="\"$PYBIN\" \"$AGENT_SCRIPT\""
    if [[ "${LOBO_GEN_QUEUE:-sqs}" == "sqs" ]] || [[ "$(basename "$AGENT_SCRIPT")" == "loboforge_agent_sqs.py" ]]; then
        "$PYBIN" -c "import aiohttp, boto3, websockets" \
            || die "SQS agent deps missing on $PYBIN (pip install aiohttp boto3 websockets)"
    else
        "$PYBIN" -c "import websockets, aiohttp" \
            || die "Agent Python deps missing on $PYBIN (pip install websockets aiohttp)"
        AGENT_CMD+=" --server \"$LOBO_SERVER\""
    fi
    AGENT_CMD+=" --secret \"$LOBO_SECRET\""
    AGENT_CMD+=" --hostname \"$LOBO_HOSTNAME_VAR\""
    AGENT_CMD+=" --comfyui-http \"$LOBO_COMFYUI_HTTP\""
    AGENT_CMD+=" --comfyui-ws \"$LOBO_COMFYUI_WS\""
    [[ -n "${LOBO_COMFYUI_TOKEN:-}" ]] && AGENT_CMD+=" --comfyui-token \"$LOBO_COMFYUI_TOKEN\""
    [[ -n "${HF_TOKEN:-}" ]]           && AGENT_CMD+=" --hf-token \"$HF_TOKEN\""
    [[ -n "${LOBO_EXTRA_ARGS:-}" ]]    && AGENT_CMD+=" $LOBO_EXTRA_ARGS"

    local AGENT_PARENT
    AGENT_PARENT="$(dirname "$AGENT_SCRIPT")"
    local LOOP_CMD
    LOOP_CMD="set -a; [[ -f /workspace/.loboforge-env ]] && . /workspace/.loboforge-env; set +a"
    LOOP_CMD+="; export PYTHONPATH=\"$AGENT_PARENT\${PYTHONPATH:+:\$PYTHONPATH}\""
    LOOP_CMD+="; while true; do set -a; [[ -f /workspace/.loboforge-env ]] && . /workspace/.loboforge-env; set +a"
    LOOP_CMD+="; echo \"[\$(date -Is)] starting agent...\" | tee -a \"$AGENT_LOG\"; $AGENT_CMD 2>&1 | tee -a \"$AGENT_LOG\"; echo \"[\$(date -Is)] agent exited, restart in 5s\" | tee -a \"$AGENT_LOG\"; sleep 5; done"
    tmux new-session -d -s "$TMUX_SESSION" "$LOOP_CMD"
    LOBO_AGENT_LAUNCHED=1
    status_post "agent.launch" "ok" "tmux session $TMUX_SESSION hostname=$LOBO_HOSTNAME_VAR connecting to $LOBO_SERVER mode=$ready_mode"
    success "Agent launched ($ready_mode) — watch Admin → Queue / GPU Pool."
    return 0
}

phase_early_pool_join() {
    [[ "${LOBO_SKIP_EARLY_AGENT:-}" == "1" ]] && return 0
    LOBO_EARLY_POOL_JOIN=1
    echo ""
    info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    info "Early pool join — Comfy + agent BEFORE model downloads"
    info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    is_p100_gpu && fix_pytorch_for_p100
    ensure_comfyui_serving
    if (( WANT_IMAGE || WANT_MUSIC || LOBO_LTX23 )); then
        upgrade_comfyui_for_lens || warn "ComfyUI upgrade failed — continuing; agent joins pool as provisioning"
    fi

    if ! command -v tmux &>/dev/null; then
        apt-get update -qq && apt-get install -y -qq tmux 2>/dev/null || true
    fi
    resolve_pybin
    if command -v pip &>/dev/null || "$PYBIN" -m pip --version &>/dev/null; then
        "$PYBIN" -m pip install -q -U websockets aiohttp gdown boto3 2>/dev/null \
            || pip install -q -U websockets aiohttp gdown boto3 2>/dev/null || true
    fi
    fetch_agent_bundle || warn "Agent fetch failed — will retry at end of script"
    if [[ -n "${AGENT_SCRIPT:-}" ]]; then
        launch_loboforge_agent_tmux minimal || warn "Early agent launch failed — will retry after downloads"
    fi
}

# ── Force-start supervisord ──────────────────────────────────────────────────
# The vastai/comfy:v0.15.1-cuda-12.9-py312 image variants we provision on do
# NOT always start supervisord automatically on container boot. When that
# happens, ComfyUI never launches (it's a supervisor program), the agent
# connects but its forward to 127.0.0.1:8188 fails forever with
# "ComfyUI WS dropped: Connect call failed", and every dispatched job to that
# box would fail at runtime. Symptom we hit 2026-05-22: 5 fresh V100 rents in
# a row connected as agents but reported empty model inventory and could not
# run anything; `supervisorctl status` returned "unix:///var/run/supervisor.sock
# no such file" on every box. Manually starting supervisord brings everything
# up cleanly. Idempotent: pgrep first so we don't double-start if vast's init
# did fire.
if ! pgrep -x supervisord >/dev/null 2>&1; then
    info "supervisord not running — force-starting (image init didn't fire)"
    rm -f /var/run/supervisor.sock
    SUPERD=$(command -v supervisord || echo /usr/local/bin/supervisord)
    SUPERCONF=/etc/supervisor/supervisord.conf
    if [[ -x "$SUPERD" && -f "$SUPERCONF" ]]; then
        "$SUPERD" -c "$SUPERCONF" 2>&1 &
        sleep 5
        info "supervisorctl status: $(supervisorctl status 2>&1 | head -5 | tr '\n' ';' || echo unknown)"
    else
        warn "supervisord binary or config missing — ComfyUI may not start. Inspect /etc/supervisor/ on this image."
    fi
else
    info "supervisord already running"
fi

# (first heartbeat fires below, after arg parsing, so MODE is populated)

# ── Arg + env parsing ────────────────────────────────────────────────────────
# Modes (single value or comma-list of: image, video, music, all):
#   image — Z-Image + Flux.2 Klein + Microsoft Lens + ACE-Step music (~3.5 GB)
#   video — Wan 2.2 i2v + FLF + ACE-Step music (music bundled with video)
#   music — ACE-Step only (legacy standalone; video/all/image include music)
#   all   — image + video + music (full kit)
#
# "both" is a back-compat alias for image+video+music.
MODE="${MODE:-all}"
# Fall back to the baked-in defaults when the caller didn't set env vars.
# Empty-string env vars (HF_TOKEN= bash ...) also fall through to the
# default — use that pattern only when the admin explicitly wants no token.
HF_TOKEN="${HF_TOKEN:-}"
LOBO_SECRET="${LOBO_SECRET:-$DEFAULT_LOBO_SECRET}"
if [[ "${LOBO_SECRET}" == "change-me-in-admin" ]]; then
    warn "LOBO_SECRET is placeholder — set Workers:Secret in Vast extra_env"
fi
resolve_gen_queue_mode

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode)
            MODE="$2"; shift 2 ;;
        --hf-token)
            HF_TOKEN="$2"; shift 2 ;;
        --help|-h)
            echo "Usage: $0 [--mode image|video|music|all|<comma-list>] [--hf-token TOKEN]"
            exit 0 ;;
        *)
            # Back-compat: pre-existing callers pass HF token positionally.
            if [[ -z "$HF_TOKEN" ]]; then HF_TOKEN="$1"; fi
            shift ;;
    esac
done

# Normalise mode → set of want-flags. Empty want-set is an error.
WANT_IMAGE=0; WANT_VIDEO=0; WANT_MUSIC=0
IFS=',' read -ra MODE_PARTS <<< "$MODE"
for part in "${MODE_PARTS[@]}"; do
    case "${part// /}" in
        image)        WANT_IMAGE=1; WANT_MUSIC=1 ;;
        video)        WANT_VIDEO=1; WANT_MUSIC=1 ;;
        music)        WANT_MUSIC=1 ;;
        all)          WANT_IMAGE=1; WANT_VIDEO=1; WANT_MUSIC=1 ;;
        both)         WANT_IMAGE=1; WANT_VIDEO=1; WANT_MUSIC=1 ;;   # legacy alias
        "")           ;;                              # ignore empty
        *) die "Invalid mode component '$part'. Use image, video, music, all, or a comma-list." ;;
    esac
done
if (( WANT_IMAGE + WANT_VIDEO + WANT_MUSIC == 0 )); then
    die "Mode resolved to nothing — pass --mode with at least one of image/video/music/all."
fi

LOBO_WAN="${LOBO_WAN:-1}"
LOBO_LTX23="${LOBO_LTX23:-0}"
if [[ -n "${LOBO_MUSIC:-}" ]]; then
    if [[ "$LOBO_MUSIC" == "0" ]]; then WANT_MUSIC=0; else WANT_MUSIC=1; fi
fi

info "Mode: $MODE (image=$WANT_IMAGE video=$WANT_VIDEO music=$WANT_MUSIC wan=$LOBO_WAN ltx23=$LOBO_LTX23)"

resolve_forge_queue_capabilities() {
  local mode="${1:-$MODE}"
  mode="${mode%%,*}"
  mode="$(printf '%s' "$mode" | tr '[:upper:]' '[:lower:]')"
  [[ "$mode" == "both" ]] && mode="all"
  local caps=""
  case "$mode" in
    image) caps="flux-klein,flux-klein-edit,zimage,chroma" ;;
    video)
      [[ "${LOBO_WAN:-1}" != "0" ]] && caps="wan"
      if [[ "${LOBO_LTX23:-0}" == "1" || "${LOBO_MUSIC:-1}" != "0" ]]; then
        [[ -n "$caps" ]] && caps="${caps},ltx" || caps="ltx"
      fi
      [[ -z "$caps" ]] && caps="wan"
      ;;
    music) caps="ltx" ;;
    all)
      caps="flux-klein,flux-klein-edit,zimage,chroma"
      [[ "${LOBO_WAN:-1}" != "0" ]] && caps="${caps},wan"
      if [[ "${LOBO_LTX23:-0}" == "1" || "${LOBO_MUSIC:-1}" != "0" ]]; then
        caps="${caps},ltx"
      fi
      ;;
    ltx-native|ltx) caps="ltx" ;;
    dolphin) caps="dolphin" ;;
    *) caps="flux-klein" ;;
  esac
  export FORGE_QUEUE_CAPABILITY="$caps"
}
resolve_forge_queue_capabilities "$MODE"
export LOBO_MODE="$MODE"

assert_gpu_compatible

required_container_gb() {
    if (( WANT_IMAGE && WANT_VIDEO )); then echo 130
    elif (( WANT_VIDEO )); then echo 100
    elif (( WANT_IMAGE )); then echo 100
    else echo 50
    fi
}

required_headroom_mb() {
    if (( WANT_IMAGE && WANT_VIDEO )); then echo 15360
    elif (( WANT_IMAGE )); then echo 7168
    elif (( WANT_VIDEO )); then echo 8192
    else echo 5120
    fi
}

# mode=all needs ≥130GB. If the box is smaller, downgrade to image or video with a note.
maybe_downgrade_from_all() {
    (( WANT_IMAGE && WANT_VIDEO )) || return 0
    local total_mb total_gb need_gb note blob target alt
    total_mb=$(df -Pm / 2>/dev/null | awk 'NR==2 {print $2}' || echo "")
    [[ -n "$total_mb" && "$total_mb" =~ ^[0-9]+$ ]] || return 0
    total_gb=$(( total_mb / 1024 ))
    need_gb=$(required_container_gb)
    (( total_gb >= need_gb )) && return 0

    blob="${LOBO_LABEL:-} $(derive_agent_hostname 2>/dev/null || true)"
    blob="${blob,,}"
    if [[ "$blob" == *video* && "$blob" != *image* ]]; then target=video
    elif [[ "$blob" == *image* && "$blob" != *video* ]]; then target=image
    elif [[ "${LOBO_WAN:-1}" == "0" ]]; then target=image
    else target=video
    fi
    if [[ "$target" == video ]]; then alt=image; else alt=video; fi

    for try in "$target" "$alt"; do
        need_gb=100
        if (( total_gb >= need_gb )); then
            note="Disk ${total_gb}GB too small for mode=all (needs ≥130GB). Downgraded to mode=${try} (≥${need_gb}GB)."
            warn "$note"
            echo "$note" > /workspace/.loboforge-provision-mode-note.txt
            MODE="$try"
            export MODE LOBO_MODE="$MODE"
            WANT_IMAGE=0; WANT_VIDEO=0; WANT_MUSIC=0
            case "$try" in
                image) WANT_IMAGE=1; WANT_MUSIC=1 ;;
                video) WANT_VIDEO=1; WANT_MUSIC=1 ;;
            esac
            if [[ -n "${LOBO_MUSIC:-}" ]]; then
                if [[ "$LOBO_MUSIC" == "0" ]]; then WANT_MUSIC=0; else WANT_MUSIC=1; fi
            fi
            info "Mode after downgrade: $MODE (image=$WANT_IMAGE video=$WANT_VIDEO music=$WANT_MUSIC)"
            status_post "disk.preflight" "warn" "$note"
            return 0
        fi
    done
}
maybe_downgrade_from_all

assert_container_has_room() {
    local need_gb total_mb free_mb total_gb
    need_gb=$(required_container_gb)
    total_mb=$(df -Pm / 2>/dev/null | awk 'NR==2 {print $2}' || echo "")
    free_mb=$(df -Pm / 2>/dev/null | awk 'NR==2 {print $4}' || echo "")
    if [[ -z "$total_mb" || ! "$total_mb" =~ ^[0-9]+$ ]]; then
        warn "Could not read container disk size"
        return 0
    fi
    total_gb=$(( total_mb / 1024 ))
    info "Container disk: ${total_gb}GB total, ${free_mb:-?}MB free (mode=$MODE needs ≥${need_gb}GB)"
    status_post "disk.preflight" "ok" "total_gb=$total_gb free_mb=${free_mb:-?} need_gb=$need_gb mode=$MODE"
    if (( total_gb < need_gb )); then
        die "Container is only ${total_gb}GB but mode=$MODE needs ≥${need_gb}GB for models + headroom. Re-rent with a larger disk (image≥120GB, video≥120GB, all≥150GB)."
    fi
}
assert_container_has_room

# Earliest possible heartbeat — confirms the box can reach LoboForge.
status_post "provision.start" "ok" "mode=$MODE hostname=$(derive_agent_hostname) gpu=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1)"

if [[ -n "$HF_TOKEN" ]]; then
    # Both the `hf` CLI and the huggingface_hub Python lib honor these env
    # vars without an interactive login. This is more reliable than running
    # `hf auth login` / `hf login` which has shifted between versions and
    # sometimes prompts even with --token. Export both names because older
    # huggingface_hub builds only read HUGGINGFACE_HUB_TOKEN.
    export HF_TOKEN
    export HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"
    info "HF_TOKEN exported for downloads (${#HF_TOKEN} chars)."

    # Best-effort login for tooling that explicitly checks the credential
    # store (e.g. some `hf upload` paths). Non-fatal — env var alone is
    # enough for the `hf download` calls used below.
    if hf auth login --token "$HF_TOKEN" --add-to-git-credential 2>/dev/null \
       || hf login      --token "$HF_TOKEN" --add-to-git-credential 2>/dev/null; then
        success "HuggingFace credential store also populated."
    else
        info "hf credential-store login skipped (env var auth is sufficient for downloads)."
    fi
else
    warn "No HF_TOKEN provided — downloads may be rate-limited for gated models."
fi

# WANT_IMAGE / WANT_VIDEO / WANT_MUSIC are set above by the mode parser.

# ── Locate ComfyUI models root ────────────────────────────────────────────────
# vast.ai's vastai/comfy:* image uses lowercase /workspace/comfyui (confirmed
# 2026-05-19 against image vastai/comfy:v0.15.1-cuda-12.9-py312). Older or
# alternate templates use other paths; we check the lowercase one first
# since that's the current default. MODELS env var override always wins.
COMFY_DIR=""
# /opt/workspace-internal/ComfyUI is where the vastai/comfy:v0.15.1-cuda-12.9-py312
# image installs ComfyUI when rented as the "SSH-only" variant (no Jupyter
# auto-launch). Two V100 rents on 2026-05-22 17:23 UTC died at this lookup
# until we added the path here.
for cand in \
    "${COMFYUI_DIR:-}" \
    "/opt/workspace-internal/ComfyUI" \
    "/opt/workspace-internal/comfyui" \
    "/workspace/comfyui" \
    "/workspace/ComfyUI" \
    "/opt/ComfyUI" \
    "/ComfyUI" \
    "/comfyui" \
    "$HOME/comfyui" \
    "$HOME/ComfyUI"
do
    if [[ -n "$cand" && -d "$cand/models" ]]; then
        COMFY_DIR="$cand"
        break
    fi
done
if [[ -z "$COMFY_DIR" && -n "${MODELS:-}" && -d "$MODELS" ]]; then
    MODELS="${MODELS}"
else
    [[ -z "$COMFY_DIR" ]] && die "Cannot find ComfyUI dir. Set COMFYUI_DIR or MODELS env var manually."
    MODELS="$COMFY_DIR/models"
fi
info "ComfyUI dir:    $COMFY_DIR"
info "Models root:    $MODELS"
status_post "comfy.locate" "ok" "dir=$COMFY_DIR models=$MODELS"

phase_early_pool_join

# ── Ensure huggingface_hub CLI ('hf') is installed ───────────────────────────
# The vastai/comfy:* "full" image variants ship it; the SSH-only variants do
# not. Z-Image text-encoder + Klein UNet downloads use 'hf' specifically
# because wget gets Cloudflare-dropped on HF's Z-Image bucket (incident
# 2026-05-21, 11.7MB partial files survived the 1KB file_present floor).
# Quick check + install when missing keeps the SSH-only image rents working.
if ! command -v hf >/dev/null 2>&1; then
    info "huggingface_hub CLI not found — installing..."
    pip install -q -U "huggingface_hub[cli]" 2>&1 | tail -3
    if ! command -v hf >/dev/null 2>&1; then
        die "Failed to install 'hf' CLI. Try: pip install 'huggingface_hub[cli]'"
    fi
    success "hf CLI installed."
fi

# ── Create subdirectories ─────────────────────────────────────────────────────
# Always create these — even video-only mode uses text_encoders + vae paths.
mkdir -p "$MODELS/text_encoders"
mkdir -p "$MODELS/vae"
if (( WANT_IMAGE )); then
    mkdir -p "$MODELS/diffusion_models/Zimage"
    mkdir -p "$MODELS/diffusion_models/F2Klein"
    mkdir -p "$MODELS/loras/Z-Image"
    mkdir -p "$MODELS/loras/F2Klein"
fi
if (( WANT_VIDEO )); then
    mkdir -p "$MODELS/diffusion_models/Wan2.2"
    mkdir -p "$MODELS/loras/Wan2.2"
fi
if (( WANT_MUSIC )); then
    mkdir -p "$MODELS/checkpoints"
fi

# =============================================================================
# IMAGE STACK — Flux Klein first, then Z-Image, Lens, Chroma (image mode only)
# =============================================================================
if (( WANT_IMAGE )); then
start_heartbeat "models.image"
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading Flux.2 Klein 9B models (first — jobs can start after this)..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

KLEIN_TE_DEST="$MODELS/text_encoders/qwen_3_8b_fp8mixed.safetensors"
if file_present "$KLEIN_TE_DEST"; then
    warn "Flux.2 Klein text encoder already present, skipping."
else
    info "Flux.2 Klein: text encoder (qwen_3_8b_fp8mixed)..."
    hf download Comfy-Org/vae-text-encorder-for-flux-klein-9b         --include "split_files/text_encoders/qwen_3_8b_fp8mixed.safetensors"         --local-dir "/tmp/hf_klein_te"
    find /tmp/hf_klein_te -name "qwen_3_8b_fp8mixed.safetensors" -exec mv -f {} "$KLEIN_TE_DEST" \;
    rm -rf /tmp/hf_klein_te
    success "Flux.2 Klein text encoder ready."
fi

KLEIN_VAE_DEST="$MODELS/vae/flux2-vae.safetensors"
if file_present "$KLEIN_VAE_DEST"; then
    warn "Flux.2 Klein VAE already present, skipping."
else
    info "Flux.2 Klein: VAE..."
    hf download Comfy-Org/flux2-dev         --include "split_files/vae/flux2-vae.safetensors"         --local-dir "/tmp/hf_klein_vae"
    find /tmp/hf_klein_vae -name "flux2-vae.safetensors" -exec mv -f {} "$KLEIN_VAE_DEST" \;
    rm -rf /tmp/hf_klein_vae
    success "Flux.2 Klein VAE ready."
fi

KLEIN_DEST="$MODELS/diffusion_models/F2Klein/flux-2-klein-9b-fp8.safetensors"
if file_present "$KLEIN_DEST" $MIN_UNET; then
    warn "Flux.2 Klein diffusion model already present, skipping."
else
    info "Flux.2 Klein: diffusion model (fp8, ~9GB)..."
    mkdir -p "$(dirname "$KLEIN_DEST")"
    hf download black-forest-labs/FLUX.2-klein-9b-fp8 flux-2-klein-9b-fp8.safetensors         --local-dir "$(dirname "$KLEIN_DEST")"
fi
success "Flux.2 Klein diffusion model ready."

if file_present "$KLEIN_DEST" $MIN_UNET; then
    info "Klein stack ready — early LoRA pull so flux-klein jobs can run during remaining downloads..."
    pull_active_loras_from_api "loras.klein-early" || warn "Early LoRA pull had errors — will retry at end."
    status_post "models.klein" "ok" "Flux Klein ready; early LoRA sync started"
fi

echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading Z-Image Turbo models..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

ZIMAGE_TE_DEST="$MODELS/text_encoders/zImage_textEncoder.safetensors"
if file_present "$ZIMAGE_TE_DEST"; then
    warn "Z-Image text encoder already present, skipping."
else
    info "Z-Image: text encoder (qwen_3_4b)..."
    hf download Comfy-Org/z_image_turbo         --include "split_files/text_encoders/qwen_3_4b.safetensors"         --local-dir "/tmp/hf_zimage_te"
    find /tmp/hf_zimage_te -name "qwen_3_4b.safetensors" -exec mv -f {} "$ZIMAGE_TE_DEST" \;
    rm -rf /tmp/hf_zimage_te
    success "Z-Image text encoder ready."
fi

ZIMAGE_VAE_DEST="$MODELS/vae/zImage_vae.safetensors"
if file_present "$ZIMAGE_VAE_DEST"; then
    warn "Z-Image VAE already present, skipping."
else
    info "Z-Image: VAE..."
    hf download Comfy-Org/z_image_turbo         --include "split_files/vae/ae.safetensors"         --local-dir "/tmp/hf_zimage_vae"
    find /tmp/hf_zimage_vae -name "ae.safetensors" -exec mv -f {} "$ZIMAGE_VAE_DEST" \;
    rm -rf /tmp/hf_zimage_vae
    success "Z-Image VAE ready."
fi

ZIMAGE_DEST="$MODELS/diffusion_models/Zimage/z_image_turbo_bf16_nsfw_v2.safetensors"
if file_present "$ZIMAGE_DEST" $MIN_UNET; then
    warn "Z-Image diffusion model already present, skipping."
else
    info "Z-Image: diffusion model (bf16 NSFW v2, ~12GB) via hf CLI..."
    mkdir -p "$(dirname "$ZIMAGE_DEST")"
    hf download tewea/z_image_turbo_bf16_nsfw         z_image_turbo_bf16_nsfw_v2.safetensors         --local-dir "$(dirname "$ZIMAGE_DEST")"         || warn "Z-Image UNet hf download had errors — verify size below."
fi
success "Z-Image diffusion model ready."

info "Z-Image: base skinny LoRA..."
wget -q --show-progress     "https://huggingface.co/tewea/z_image_turbo_bf16_nsfw/resolve/main/z-image_SkinnyV2.safetensors"     -O "$MODELS/loras/Z-Image/z-image_SkinnyV2.safetensors" ||     warn "Z-Image skinny LoRA not found at expected URL — download manually if needed."
success "Z-Image LoRAs ready."

echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading Microsoft Lens Turbo models..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

LENS_UNET_DEST="$MODELS/diffusion_models/lens_turbo_bf16.safetensors"
if file_present "$LENS_UNET_DEST" $MIN_UNET; then
    warn "Microsoft Lens UNet already present, skipping."
else
    info "Microsoft Lens: UNet (lens_turbo_bf16, ~8GB)..."
    hf download Comfy-Org/Lens         --include "diffusion_models/lens_turbo_bf16.safetensors"         --local-dir "/tmp/hf_lens_unet"
    find /tmp/hf_lens_unet -name "lens_turbo_bf16.safetensors" -exec mv -f {} "$LENS_UNET_DEST" \;
    rm -rf /tmp/hf_lens_unet
    success "Microsoft Lens UNet ready."
fi

LENS_CLIP_DEST="$MODELS/text_encoders/gpt_oss_20b_nvfp4.safetensors"
if file_present "$LENS_CLIP_DEST" $MIN_LARGE_TE; then
    warn "Microsoft Lens text encoder already present, skipping."
else
    info "Microsoft Lens: text encoder (gpt_oss_20b_nvfp4, ~13GB)..."
    hf download Comfy-Org/Lens         --include "text_encoders/gpt_oss_20b_nvfp4.safetensors"         --local-dir "/tmp/hf_lens_te"
    find /tmp/hf_lens_te -name "gpt_oss_20b_nvfp4.safetensors" -exec mv -f {} "$LENS_CLIP_DEST" \;
    rm -rf /tmp/hf_lens_te
    success "Microsoft Lens text encoder ready."
fi

echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading Chroma HD models..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

mkdir -p "$MODELS/diffusion_models/Chroma"
CHROMA_UNET="$MODELS/diffusion_models/Chroma/chroma_v10HD.safetensors"
if file_present "$CHROMA_UNET" $MIN_CHROMA; then
    warn "Chroma UNet already present, skipping."
else
    info "Chroma: UNet (chroma_v10HD, ~12GB)..."
    hf download RomixERR/Chroma_HD_v1.0_bf16 chroma_v10HD.safetensors         --local-dir "$(dirname "$CHROMA_UNET")"
fi
success "Chroma UNet ready."

CHROMA_T5="$MODELS/text_encoders/t5xxl_fp8_e4m3fn.safetensors"
if file_present "$CHROMA_T5" $((4 * 1024 * 1024 * 1024)); then
    warn "Chroma T5 text encoder already present, skipping."
else
    info "Chroma: T5 text encoder (t5xxl_fp8_e4m3fn)..."
    hf download comfyanonymous/flux_text_encoders t5xxl_fp8_e4m3fn.safetensors         --local-dir "/tmp/hf_chroma_t5"
    find /tmp/hf_chroma_t5 -name "t5xxl_fp8_e4m3fn.safetensors" -exec mv -f {} "$CHROMA_T5" \;
    rm -rf /tmp/hf_chroma_t5
fi
success "Chroma T5 ready."

CHROMA_VAE="$MODELS/vae/ae.safetensors"
if file_present "$CHROMA_VAE" $MIN_VAE; then
    warn "Chroma VAE alias (ae.safetensors) already present, skipping."
else
    info "Chroma: VAE alias (ae.safetensors from z_image_turbo)..."
    hf download Comfy-Org/z_image_turbo         --include "split_files/vae/ae.safetensors"         --local-dir "/tmp/hf_chroma_vae"
    find /tmp/hf_chroma_vae -name "ae.safetensors" -exec mv -f {} "$CHROMA_VAE" \;
    rm -rf /tmp/hf_chroma_vae
fi
success "Chroma VAE ready."

stop_heartbeat
status_post "models.image" "ok" "Klein + Z-Image + Lens + Chroma downloaded"
fi  # end WANT_IMAGE

# =============================================================================
# WAN 2.2 IMAGE-TO-VIDEO  (video mode only)
# Repo: Comfy-Org/Wan_2.2_ComfyUI_Repackaged  — official ComfyUI-packaged
# release. Files live under split_files/{diffusion_models,vae,text_encoders}.
# The dispatcher routes wan2/wan2flf/wan2t2v jobs to nodes whose KnownUnets contain
# "wan" / "i2v" / "t2v" so the filenames here must end up in $MODELS/diffusion_models/.
# =============================================================================
if (( WANT_VIDEO && LOBO_WAN )); then
start_heartbeat "models.video"
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading Wan 2.2 i2v models..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# High-noise UNet (the "early-steps" half of the Wan 2.2 split-LoRA flow).
info "Wan 2.2: UNet high-noise (~14 GB fp8 scaled)..."
WAN_HI_DEST="$MODELS/diffusion_models/Wan2.2/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors"
if file_present "$WAN_HI_DEST" $MIN_UNET; then
    warn "Wan 2.2 high-noise UNet already exists, skipping."
else
    hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
        --include "split_files/diffusion_models/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" \
        --local-dir "/tmp/hf_wan_hi"
    find /tmp/hf_wan_hi -name "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" -exec mv -f {} "$WAN_HI_DEST" \;
    rm -rf /tmp/hf_wan_hi
fi
success "Wan 2.2 high-noise UNet ready."

# Low-noise UNet (the "refinement-steps" half).
info "Wan 2.2: UNet low-noise (~14 GB fp8 scaled)..."
WAN_LO_DEST="$MODELS/diffusion_models/Wan2.2/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors"
if file_present "$WAN_LO_DEST" $MIN_UNET; then
    warn "Wan 2.2 low-noise UNet already exists, skipping."
else
    hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
        --include "split_files/diffusion_models/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors" \
        --local-dir "/tmp/hf_wan_lo"
    find /tmp/hf_wan_lo -name "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors" -exec mv -f {} "$WAN_LO_DEST" \;
    rm -rf /tmp/hf_wan_lo
fi
success "Wan 2.2 low-noise UNet ready."

# UMT5-XXL text encoder (shared with other Wan/UMT5 workflows).
info "Wan 2.2: text encoder (umt5_xxl_fp8)..."
WAN_TE_DEST="$MODELS/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors"
if file_present "$WAN_TE_DEST" $MIN_LARGE_TE; then
    warn "Wan text encoder already exists, skipping."
else
    hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
        --include "split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors" \
        --local-dir "/tmp/hf_wan_te"
    find /tmp/hf_wan_te -name "umt5_xxl_fp8_e4m3fn_scaled.safetensors" -exec mv -f {} "$WAN_TE_DEST" \;
    rm -rf /tmp/hf_wan_te
fi
success "Wan 2.2 text encoder ready."

# VAE (shared between Wan 2.1 and 2.2).
info "Wan 2.2: VAE (wan_2.1_vae)..."
WAN_VAE_DEST="$MODELS/vae/wan_2.1_vae.safetensors"
if file_present "$WAN_VAE_DEST" $MIN_VAE; then
    warn "Wan VAE already exists, skipping."
else
    hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
        --include "split_files/vae/wan_2.1_vae.safetensors" \
        --local-dir "/tmp/hf_wan_vae"
    find /tmp/hf_wan_vae -name "wan_2.1_vae.safetensors" -exec mv -f {} "$WAN_VAE_DEST" \;
    rm -rf /tmp/hf_wan_vae
fi
success "Wan 2.2 VAE ready."

# Lightning 4-step accelerator LoRAs — REQUIRED by the wan2 i2v workflow
# (QueueService.BuildPromptGraphWan2I2V hardcodes them at nodes 101/102).
# Without these on disk, every wan2 job fails ComfyUI validation with
# "lora_name 'wan2.2_i2v_lightx2v_4steps_*' not in [...]". Same repo as the
# UNet/text-encoder/VAE above; just in split_files/loras/.
for stage in high low; do
    DEST="$MODELS/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_${stage}_noise.safetensors"
    if file_present "$DEST" $MIN_LIGHT_LORA; then
        warn "Wan 2.2 lightning ${stage}-noise LoRA already present, skipping."
    else
        info "Wan 2.2: lightning ${stage}-noise LoRA (~600MB)..."
        TMP="/tmp/hf_wan_light_${stage}"
        mkdir -p "$TMP"
        hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
            --include "split_files/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_${stage}_noise.safetensors" \
            --local-dir "$TMP"
        find "$TMP" -name "wan2.2_i2v_lightx2v_4steps_lora_v1_${stage}_noise.safetensors" \
            -exec mv -f {} "$DEST" \;
        rm -rf "$TMP"
    fi
done
success "Wan 2.2 i2v lightning LoRAs ready."

# T2V UNets (~28 GB each half) — wan2t2v queue model.
info "Wan 2.2: T2V UNet high-noise (~14 GB fp8 scaled)..."
WAN_T2V_HI="$MODELS/diffusion_models/Wan2.2/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors"
if file_present "$WAN_T2V_HI" $MIN_UNET; then
    warn "Wan 2.2 T2V high-noise UNet already exists, skipping."
else
    hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
        --include "split_files/diffusion_models/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors" \
        --local-dir "/tmp/hf_wan_t2v_hi"
    find /tmp/hf_wan_t2v_hi -name "wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors" -exec mv -f {} "$WAN_T2V_HI" \;
    rm -rf /tmp/hf_wan_t2v_hi
fi
success "Wan 2.2 T2V high-noise UNet ready."

info "Wan 2.2: T2V UNet low-noise (~14 GB fp8 scaled)..."
WAN_T2V_LO="$MODELS/diffusion_models/Wan2.2/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors"
if file_present "$WAN_T2V_LO" $MIN_UNET; then
    warn "Wan 2.2 T2V low-noise UNet already exists, skipping."
else
    hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
        --include "split_files/diffusion_models/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors" \
        --local-dir "/tmp/hf_wan_t2v_lo"
    find /tmp/hf_wan_t2v_lo -name "wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors" -exec mv -f {} "$WAN_T2V_LO" \;
    rm -rf /tmp/hf_wan_t2v_lo
fi
success "Wan 2.2 T2V low-noise UNet ready."

mkdir -p "$MODELS/loras/wan2.2"
for stage in high low; do
    DEST="$MODELS/loras/wan2.2/wan2.2_t2v_lightx2v_4steps_lora_v1.1_${stage}_noise.safetensors"
    if file_present "$DEST" $MIN_LIGHT_LORA; then
        warn "Wan 2.2 T2V lightning ${stage}-noise LoRA already present, skipping."
    else
        info "Wan 2.2: T2V lightning ${stage}-noise LoRA..."
        TMP="/tmp/hf_wan_t2v_light_${stage}"
        mkdir -p "$TMP"
        hf download Comfy-Org/Wan_2.2_ComfyUI_Repackaged \
            --include "split_files/loras/wan2.2_t2v_lightx2v_4steps_lora_v1.1_${stage}_noise.safetensors" \
            --local-dir "$TMP"
        find "$TMP" -name "wan2.2_t2v_lightx2v_4steps_lora_v1.1_${stage}_noise.safetensors" \
            -exec mv -f {} "$DEST" \;
        rm -rf "$TMP"
    fi
done
success "Wan 2.2 T2V lightning LoRAs ready."

stop_heartbeat
status_post "models.video" "ok" "Wan 2.2 i2v+t2v UNets + UMT5 + VAE + lightning LoRAs downloaded"
elif (( WANT_VIDEO )); then
    info "Wan 2.2 stack skipped (LOBO_WAN=0 — use on dedicated Wan boxes)."
fi  # end WANT_VIDEO && LOBO_WAN

# =============================================================================
# LTX 2.3 (AV — synchronized video + audio, not silent) — disabled by default.
# Set LOBO_LTX23=1 on dedicated AV boxes when Ltx23:ProvisionEnabled=true on the API.
# Pair with LOBO_MUSIC=1 on the same box; keep LOBO_WAN=0 to save disk.
# Requires ComfyUI-LTXVideo, rgthree, KJNodes custom nodes on the image.
# =============================================================================
if (( WANT_VIDEO && LOBO_LTX23 )); then
start_heartbeat "models.ltx23"
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading LTX 2.3 official t2v stack (LOBO_LTX23=1)..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
warn "LTX 2.3 is admin-preview — ensure HF repos in ltx23_stack.json are reachable before enabling fleet-wide."

LTX_VAE_REPO="${LTX_VAE_REPO:-Kijai/LTX2.3_comfy}"
LTX_FP8_REPO="${LTX_FP8_REPO:-Lightricks/LTX-2.3-fp8}"
LTX_WEIGHTS_REPO="${LTX_WEIGHTS_REPO:-Lightricks/LTX-2.3}"
LTX_COMFY_REPO="${LTX_COMFY_REPO:-Comfy-Org/ltx-2}"
mkdir -p "$MODELS/checkpoints" "$MODELS/loras" "$MODELS/latent_upscale_models" \
    "$MODELS/text_encoders" "$MODELS/vae" "$MODELS/checkpoints/LTX2"

_ltx_hf() {
    local repo="$1" include="$2" dest="$3" min="$4" label="$5"
    if file_present "$dest" "$min"; then
        warn "$label already exists, skipping."
        return 0
    fi
    local tmp="/tmp/hf_ltx_${RANDOM}"
    mkdir -p "$tmp" "$(dirname "$dest")"
    info "$label..."
    hf download "$repo" --include "$include" --local-dir "$tmp" || {
        rm -rf "$tmp"
        warn "LTX download failed for $label (repo=$repo include=$include)."
        return 1
    }
    local found
    found=$(find "$tmp" -type f -name "$(basename "$dest")" 2>/dev/null | head -1 || true)
    if [[ -n "$found" && -f "$found" ]]; then
        mv -f "$found" "$dest"
        success "$label ready."
    else
        warn "$label not found in HF output."
    fi
    rm -rf "$tmp"
}

_ltx_hf "$LTX_FP8_REPO" "ltx-2.3-22b-dev-fp8.safetensors" \
    "$MODELS/checkpoints/ltx-2.3-22b-dev-fp8.safetensors" \
    $MIN_UNET "LTX 2.3 FP8 checkpoint" || true
_ltx_hf "$LTX_WEIGHTS_REPO" "ltx-2.3-22b-distilled-lora-384.safetensors" \
    "$MODELS/loras/ltx-2.3-22b-distilled-lora-384.safetensors" \
    $MIN_LIGHT_LORA "LTX distilled LoRA" || true
_ltx_hf "$LTX_WEIGHTS_REPO" "ltx-2.3-spatial-upscaler-x2-1.1.safetensors" \
    "$MODELS/latent_upscale_models/ltx-2.3-spatial-upscaler-x2-1.1.safetensors" \
    10000000 "LTX spatial upscaler" || true
_ltx_hf "$LTX_VAE_REPO" "vae/LTX23_video_vae_bf16.safetensors" \
    "$MODELS/vae/LTX23_video_vae_bf16.safetensors" \
    $MIN_VAE "LTX video VAE" || true
_ltx_hf "$LTX_VAE_REPO" "vae/LTX23_audio_vae_bf16.safetensors" \
    "$MODELS/vae/LTX23_audio_vae_bf16.safetensors" \
    $MIN_VAE "LTX audio VAE" || true
_ltx_hf "$LTX_COMFY_REPO" "split_files/text_encoders/gemma_3_12B_it_fp4_mixed.safetensors" \
    "$MODELS/text_encoders/gemma_3_12B_it_fp4_mixed.safetensors" \
    $MIN_LARGE_TE "LTX Gemma text encoder (fp4)" || true

if [[ -f "$MODELS/vae/LTX23_audio_vae_bf16.safetensors" ]]; then
    ln -sf "../../vae/LTX23_audio_vae_bf16.safetensors" "$MODELS/checkpoints/LTX2/LTX23_audio_vae_bf16.safetensors" 2>/dev/null || \
        cp -f "$MODELS/vae/LTX23_audio_vae_bf16.safetensors" "$MODELS/checkpoints/LTX2/LTX23_audio_vae_bf16.safetensors" || true
fi

stop_heartbeat
status_post "models.ltx23" "ok" "LTX 2.3 t2v stack pass finished (see warnings for missing artifacts)"
elif (( WANT_VIDEO )); then
    info "LTX 2.3 stack skipped (LOBO_LTX23=0 — enable via Ltx23:ProvisionEnabled when ready)."
fi

# =============================================================================
# ACE-STEP MUSIC  (bundled with video + all modes; also available via music-only)
# Graph expects checkpoints/ace_step_v1_3.5b.safetensors (ComfyGraphBuilder).
# =============================================================================
if (( WANT_MUSIC )); then
start_heartbeat "models.music"
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Downloading ACE-Step music model..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

ACE_REPO="${ACE_REPO:-Comfy-Org/ACE-Step_ComfyUI_repackaged}"
ACE_INCLUDE="${ACE_INCLUDE:-all_in_one/ace_step_v1_3.5b.safetensors}"
ACE_DEST="$MODELS/checkpoints/ace_step_v1_3.5b.safetensors"

if file_present "$ACE_DEST" $MIN_CHECKPOINT; then
    warn "ACE-Step model already exists, skipping."
else
    info "ACE-Step: checkpoint ($ACE_REPO / $ACE_INCLUDE)..."
    mkdir -p "$MODELS/checkpoints"
    rm -rf /tmp/hf_ace
    mkdir -p /tmp/hf_ace
    if hf download "$ACE_REPO" \
        --include "$ACE_INCLUDE" \
        --local-dir "/tmp/hf_ace"; then
        found_ace=$(find /tmp/hf_ace -name "ace_step_v1_3.5b.safetensors" -type f 2>/dev/null | head -1 || true)
        if [[ -n "$found_ace" && -f "$found_ace" ]]; then
            mv -f "$found_ace" "$ACE_DEST"
            success "ACE-Step model ready at checkpoints/ace_step_v1_3.5b.safetensors"
        else
            die "ACE-Step download finished but ace_step_v1_3.5b.safetensors not found in HF output."
        fi
    else
        die "ACE-Step download failed (repo=$ACE_REPO include=$ACE_INCLUDE). Music jobs will not run on this box."
    fi
    rm -rf /tmp/hf_ace 2>/dev/null || true
fi

stop_heartbeat
status_post "models.music" "ok" "ACE-Step checkpoint ready"
fi  # end WANT_MUSIC

# =============================================================================
# ACTIVE LORAS — pull every admin-enabled LoRA whose BaseModel matches this
# box's mode. Without this step, a fresh agent connects with ONLY the hardcoded
# base LoRAs (Z-Image Skinny, Wan lightning) — every job referencing a user
# LoRA fails the dispatcher's NodeHasRequiredLoras gate and stays on whichever
# box was provisioned BEFORE the LoRA was enabled. The endpoint returns
# {file_path, source_url, base_model, wan_stage} per LoRA; we route by URL
# shape: huggingface.co → hf CLI, drive.google.com → gdown, anything else →
# curl. Skip silently when source_url is empty (admin-only direct-upload LoRA
# with no remote copy yet — admin should populate SourceUrl via the S3 sidecar
# or GDrive). Best-effort: a failure on a single LoRA logs + continues, so one
# bad URL never blocks the whole box.
# =============================================================================
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Pulling active LoRAs from LoboForge (mode=$MODE)..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
start_heartbeat "loras.pull"
pull_active_loras_from_api "loras.pull" || warn "Final LoRA pull had errors."
stop_heartbeat

# =============================================================================
# COMFYUI CUSTOM NODES — must exist BEFORE ComfyUI starts (or before we accept
# an already-running ComfyUI). LoboForge graphs use "Power Lora Loader (rgthree)".
# Previously this block ran AFTER ComfyUI was already serving, and the
# supervisorctl restart fallback never works on vast.ai tmux boxes — so rgthree
# sat on disk but ComfyUI never loaded it until someone SSH'd in.
# =============================================================================
install_rgthree_custom_nodes() {
    echo ""
    info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    info "Installing ComfyUI custom nodes (rgthree)..."
    info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    local custom_nodes_dir="$COMFY_DIR/custom_nodes"
    mkdir -p "$custom_nodes_dir"

    local rgthree_dir="$custom_nodes_dir/rgthree-comfy"
    if [[ -d "$rgthree_dir/.git" ]]; then
        info "rgthree-comfy already cloned — pulling latest..."
        git -C "$rgthree_dir" pull --ff-only || warn "rgthree-comfy pull failed (non-fatal)"
        success "rgthree-comfy up to date."
    else
        info "Cloning rgthree-comfy..."
        if git clone --depth 1 https://github.com/rgthree/rgthree-comfy.git "$rgthree_dir"; then
            success "rgthree-comfy installed."
        else
            warn "rgthree-comfy clone failed — Power Lora Loader workflows will FAIL on this node."
            status_post "nodes.rgthree" "error" "git clone failed"
            return 1
        fi
    fi

    local node_pip=""
    if [[ -x /venv/main/bin/python ]]; then
        node_pip="/venv/main/bin/python"
    elif [[ -n "${PYBIN:-}" ]]; then
        node_pip="$PYBIN"
    fi
    if [[ -n "$node_pip" && -s "$rgthree_dir/requirements.txt" ]]; then
        "$node_pip" -m pip install --quiet -r "$rgthree_dir/requirements.txt" 2>&1 | tail -3 \
            || warn "rgthree requirements install had issues (non-fatal)."
    fi

    status_post "nodes.install" "ok" "rgthree-comfy ready"
}

power_lora_loader_registered() {
    curl -sS -m 5 "http://127.0.0.1:${COMFYUI_PORT:-18188}/object_info" 2>/dev/null \
        | python3 -c "import sys,json; d=json.load(sys.stdin); sys.exit(0 if 'Power Lora Loader (rgthree)' in d else 1)" \
        2>/dev/null
}

ensure_power_lora_loader_loaded() {
    if power_lora_loader_registered; then
        success "Power Lora Loader (rgthree) registered in ComfyUI."
        status_post "nodes.rgthree" "ok" "Power Lora Loader registered"
        return 0
    fi

    warn "Power Lora Loader (rgthree) missing from ComfyUI — restarting to load custom nodes..."
    status_post "nodes.rgthree" "warn" "Power Lora Loader missing; restarting ComfyUI"
    start_comfyui_manual
    if ! wait_for_comfyui 180; then
        warn "ComfyUI did not come back after rgthree restart."
        status_post "nodes.rgthree" "error" "ComfyUI restart after rgthree failed"
        return 1
    fi

    if power_lora_loader_registered; then
        success "Power Lora Loader (rgthree) registered after ComfyUI restart."
        status_post "nodes.rgthree" "ok" "Power Lora Loader registered after restart"
        return 0
    fi

    warn "Power Lora Loader (rgthree) STILL missing — check /tmp/comfyui.log and $COMFY_DIR/custom_nodes/rgthree-comfy"
    status_post "nodes.rgthree" "error" "Power Lora Loader still missing after restart"
    return 1
}

install_ltx_custom_nodes() {
    (( LOBO_LTX23 )) || return 0
    echo ""
    info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
    info "Installing ComfyUI-LTXVideo (LOBO_LTX23=1)..."
    info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

    local custom_nodes_dir="$COMFY_DIR/custom_nodes"
    mkdir -p "$custom_nodes_dir"
    local ltx_dir="$custom_nodes_dir/ComfyUI-LTXVideo"
    if [[ -d "$ltx_dir/.git" ]]; then
        git -C "$ltx_dir" pull --ff-only || warn "ComfyUI-LTXVideo pull failed (non-fatal)"
    else
        git clone --depth 1 https://github.com/Lightricks/ComfyUI-LTXVideo.git "$ltx_dir" \
            || { status_post "nodes.ltxvideo" "error" "git clone failed"; return 1; }
    fi
    local node_pip=""
    if [[ -x /venv/main/bin/python ]]; then
        node_pip="/venv/main/bin/python"
    elif [[ -n "${PYBIN:-}" ]]; then
        node_pip="$PYBIN"
    fi
    if [[ -n "$node_pip" ]]; then
        "$node_pip" -m pip install --quiet simpleeval 2>/dev/null || true
        [[ -s "$ltx_dir/requirements.txt" ]] && \
            "$node_pip" -m pip install --quiet -r "$ltx_dir/requirements.txt" 2>&1 | tail -5 \
                || warn "ComfyUI-LTXVideo requirements had issues (non-fatal)."
    fi
    status_post "nodes.ltxvideo" "ok" "ComfyUI-LTXVideo cloned"
}

ltx_nodes_registered() {
    curl -sS -m 10 "http://127.0.0.1:${COMFYUI_PORT:-18188}/object_info" 2>/dev/null \
        | python3 -c "import sys,json; d=json.load(sys.stdin); need=['ComfyMathExpression','LTXAVTextEncoderLoader','EmptyLTXVLatentVideo']; miss=[n for n in need if n not in d]; sys.exit(0 if not miss else 1)" \
        2>/dev/null
}

ensure_ltx_comfy_nodes_ready() {
    (( LOBO_LTX23 )) || return 0
    if ltx_nodes_registered; then
        success "LTX 2.3 Comfy nodes registered (math + LTXVideo)."
        status_post "nodes.ltx" "ok" "LTX t2v nodes registered"
        return 0
    fi
    warn "LTX nodes missing — installing LTXVideo + minimal Comfy upgrade (safe restart)..."
    status_post "nodes.ltx" "warn" "LTX nodes missing; running ensure-ltx-comfyui"
    ensure_loboforge_worker_package || return 1
    resolve_pybin
    local iid
    iid="$(resolve_instance_suffix 2>/dev/null || echo "${LOBO_INSTANCE_ID:-unknown}")"
    if "$PYBIN" -m loboforge_worker ensure-ltx-comfyui \
        --secret "${LOBO_SECRET:-}" \
        --comfyui-http "${LOBO_COMFYUI_HTTP:-http://127.0.0.1:18188}" \
        --instance-id "$iid"; then
        success "LTX 2.3 Comfy nodes registered."
        status_post "nodes.ltx" "ok" "LTX t2v nodes registered"
        return 0
    fi
    warn "LTX nodes STILL missing — run scripts/fix-ltx-comfy-stack.sh on this box"
    status_post "nodes.ltx" "error" "ComfyMathExpression or LTXVideo nodes still missing"
    return 1
}

install_rgthree_custom_nodes
install_ltx_custom_nodes

# Comfy serving + Lens upgrade run in phase_early_pool_join (before downloads).
if [[ "${LOBO_EARLY_POOL_JOIN:-}" != "1" ]]; then
    ensure_comfyui_serving
    if (( WANT_IMAGE || WANT_MUSIC || LOBO_LTX23 )); then
        upgrade_comfyui_for_lens || warn "ComfyUI upgrade failed — continuing"
    fi
    ensure_ltx_comfy_nodes_ready || warn "LTX Comfy stack not fully registered"
fi

ensure_power_lora_loader_loaded || warn "Jobs using Power Lora Loader will fail until ComfyUI loads rgthree-comfy."

ace_step_nodes_listed() {
    curl -sS -m 8 "http://127.0.0.1:${COMFYUI_PORT:-18188}/object_info" 2>/dev/null \
        | python3 -c "import sys,json; d=json.load(sys.stdin); sys.exit(0 if 'TextEncodeAceStepAudio' in d else 1)" \
        2>/dev/null
}

ensure_ace_step_nodes_ready() {
    (( WANT_MUSIC )) || return 0
    if ace_step_nodes_listed; then
        success "ACE-Step ComfyUI nodes registered (TextEncodeAceStepAudio)."
        status_post "nodes.ace_step" "ok" "TextEncodeAceStepAudio registered"
        return 0
    fi
    warn "ACE-Step nodes missing — ComfyUI needs master (comfy_extras/nodes_ace.py)"
    status_post "nodes.ace_step" "warn" "TextEncodeAceStepAudio missing; upgrading ComfyUI"
    upgrade_comfyui_for_lens || true
    if ace_step_nodes_listed; then
        success "ACE-Step nodes registered after ComfyUI upgrade."
        status_post "nodes.ace_step" "ok" "TextEncodeAceStepAudio registered after upgrade"
        return 0
    fi
    warn "ACE-Step nodes STILL missing — music jobs will fail with missing_node_type"
    status_post "nodes.ace_step" "error" "TextEncodeAceStepAudio still missing after upgrade"
    return 1
}

ensure_ace_step_nodes_ready || warn "Music jobs will fail until ComfyUI master loads ACE-Step nodes."

ace_step_checkpoint_listed() {
    curl -sS -m 8 "http://127.0.0.1:${COMFYUI_PORT:-18188}/object_info/CheckpointLoaderSimple" 2>/dev/null \
        | python3 -c "import sys,json; d=json.load(sys.stdin); vals=d.get('CheckpointLoaderSimple',{}).get('input',{}).get('required',{}).get('ckpt_name',[[]])[0]; sys.exit(0 if any('ace_step' in str(v).lower() for v in vals) else 1)" \
        2>/dev/null
}

ensure_ace_step_checkpoint_visible() {
    (( WANT_MUSIC )) || return 0
    if ace_step_checkpoint_listed; then
        success "ACE-Step checkpoint visible to ComfyUI (CheckpointLoaderSimple)."
        status_post "models.music" "ok" "ace_step listed in ComfyUI"
        return 0
    fi
    warn "ACE-Step checkpoint on disk but not listed by ComfyUI — restarting ComfyUI to refresh model index..."
    status_post "models.music" "warn" "ace_step missing from ComfyUI ckpt list; restarting"
    start_comfyui_manual
    if wait_for_comfyui 180 && ace_step_checkpoint_listed; then
        success "ACE-Step checkpoint visible after ComfyUI restart."
        status_post "models.music" "ok" "ace_step listed after ComfyUI restart"
        return 0
    fi
    warn "ACE-Step checkpoint still not visible in ComfyUI — music jobs may fail until ComfyUI rescans models/."
    status_post "models.music" "error" "ace_step not in ComfyUI ckpt list after restart"
    return 1
}

ensure_ace_step_checkpoint_visible || warn "Music jobs will fail until ace_step_v1_3.5b.safetensors is visible to ComfyUI."

# =============================================================================
# WD14 TAGGER DEPS — required by loboforge_agent.py for NSFW auto-detection
# The WD14 model itself (~365 MB) auto-downloads to ./wd14_cache/ on first use.
# =============================================================================
# Resolve the Python interpreter we'll use for EVERYTHING below:
#   1. installing the WD14 deps (via "$PYBIN -m pip", NOT bare pip)
#   2. verifying the deps import correctly
#   3. launching the agent in tmux later in this script
#
# Bare `pip` and bare `python` can point to different interpreters/envs
# (Jupyter venv vs system python vs conda base, etc.), which is why packages
# install successfully but the running agent can't import them. Pinning
# everything to the same $PYBIN binary makes the install/import/run cycle
# self-consistent.
#
# Override with LOBO_PYBIN=/path/to/python if you have a specific env to use.
resolve_pybin
info "Using Python: $PYBIN ($("$PYBIN" --version 2>&1))"

info "Installing WD14 tagger Python deps via $PYBIN..."
if "$PYBIN" -m pip --version &>/dev/null; then
    # Prefer GPU runtime if CUDA is present
    if command -v nvidia-smi &>/dev/null && nvidia-smi &>/dev/null; then
        "$PYBIN" -m pip install --upgrade onnxruntime-gpu Pillow numpy 2>&1 | tail -5 \
            || die "pip install (onnxruntime-gpu Pillow numpy) FAILED via $PYBIN. Inspect the output above."
    else
        "$PYBIN" -m pip install --upgrade onnxruntime      Pillow numpy 2>&1 | tail -5 \
            || die "pip install (onnxruntime Pillow numpy) FAILED via $PYBIN. Inspect the output above."
    fi

    # VERIFY the packages are importable from the EXACT same python we'll launch
    # the agent with. If this passes, the agent (when also run via $PYBIN) cannot
    # hit the "ModuleNotFoundError: PIL" failure mode.
    info "Verifying WD14 deps import correctly..."
    if ! "$PYBIN" -c "import PIL; import numpy; import onnxruntime; print('PIL', PIL.__version__, '| numpy', numpy.__version__, '| onnxruntime', onnxruntime.__version__)"; then
        die "WD14 deps installed but $PYBIN cannot import them. Something is wrong with this Python env. Try: rm -rf its site-packages cache, or set LOBO_PYBIN to a known-good interpreter."
    fi
    success "WD14 deps installed and importable from $PYBIN."
else
    die "$PYBIN has no pip module ('$PYBIN -m pip' failed). Install pip into this env: '$PYBIN -m ensurepip --upgrade' then re-run."
fi

# =============================================================================
# CLEANUP — remove any leftover hf scaffolding
# =============================================================================
rm -rf "$MODELS/loras/F2Klein/loras"

# =============================================================================
# SUMMARY (models)
# =============================================================================
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
success "Model provisioning complete. Final model layout:"
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
find "$MODELS/diffusion_models" "$MODELS/text_encoders" "$MODELS/vae" "$MODELS/loras" "$MODELS/checkpoints" \
    -name "*.safetensors" 2>/dev/null | sort | while read -r f; do
    size=$(du -sh "$f" 2>/dev/null | cut -f1)
    echo -e "  ${GREEN}✓${NC} $size  ${f#$MODELS/}"
done

# ── Hard validation — fail fast if this mode's required files are missing ────
require_file() {
    local path="$1" min="$2" label="$3"
    if file_present "$path" "$min"; then
        success "Verified: $label"
    else
        die "Missing required model for mode=$MODE: $label ($path)"
    fi
}

echo ""
info "Validating required models for mode=$MODE..."
if (( WANT_IMAGE )); then
    require_file "$MODELS/diffusion_models/Zimage/z_image_turbo_bf16_nsfw_v2.safetensors" $MIN_UNET "Z-Image UNet"
    require_file "$MODELS/diffusion_models/F2Klein/flux-2-klein-9b-fp8.safetensors" $MIN_UNET "Flux.2 Klein UNet"
    require_file "$MODELS/diffusion_models/lens_turbo_bf16.safetensors" $MIN_UNET "Microsoft Lens UNet"
    require_file "$MODELS/text_encoders/gpt_oss_20b_nvfp4.safetensors" $MIN_LARGE_TE "Microsoft Lens text encoder"
    require_file "$MODELS/vae/flux2-vae.safetensors" $MIN_VAE "Microsoft Lens / Klein VAE (flux2-vae)"
    require_file "$MODELS/diffusion_models/Chroma/chroma_v10HD.safetensors" $MIN_CHROMA "Chroma HD UNet"
    require_file "$MODELS/text_encoders/t5xxl_fp8_e4m3fn.safetensors" $((4 * 1024 * 1024 * 1024)) "Chroma T5 text encoder"
fi
if (( WANT_VIDEO )); then
    require_file "$MODELS/diffusion_models/Wan2.2/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors" $MIN_UNET "Wan 2.2 high-noise UNet"
    require_file "$MODELS/diffusion_models/Wan2.2/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors" $MIN_UNET "Wan 2.2 low-noise UNet"
    require_file "$MODELS/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors" $MIN_LARGE_TE "Wan UMT5 text encoder"
    require_file "$MODELS/vae/wan_2.1_vae.safetensors" $MIN_VAE "Wan VAE"
    require_file "$MODELS/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors" $MIN_LIGHT_LORA "Wan lightning high LoRA"
    require_file "$MODELS/loras/wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors" $MIN_LIGHT_LORA "Wan lightning low LoRA"
fi
if (( WANT_MUSIC )); then
    require_file "$MODELS/checkpoints/ace_step_v1_3.5b.safetensors" $MIN_CHECKPOINT "ACE-Step checkpoint"
fi
if (( WANT_VIDEO && LOBO_LTX23 )); then
    if file_present "$MODELS/checkpoints/ltx-2.3-22b-dev-fp8.safetensors" $MIN_UNET; then
        success "Verified: LTX FP8 checkpoint"
    else
        warn "LTX FP8 checkpoint missing — ltx23-fp8 jobs will fail until downloaded."
    fi
    if file_present "$MODELS/text_encoders/gemma_3_12B_it_fp4_mixed.safetensors" $MIN_LARGE_TE; then
        success "Verified: LTX Gemma text encoder"
    else
        warn "LTX Gemma text encoder missing — ltx23-fp8 jobs will fail until downloaded."
    fi
    if file_present "$MODELS/vae/LTX23_video_vae_bf16.safetensors" $MIN_VAE; then
        success "Verified: LTX video VAE"
    else
        warn "LTX video VAE missing — ltx23-fp8 jobs will fail until downloaded."
    fi
fi
status_post "models.validate" "ok" "all required files present for mode=$MODE"

# =============================================================================
# AGENT RUNTIME — install tmux + python deps so the LoboForge agent can run
# detached from Jupyter (i.e. survives the browser tab being closed).
# =============================================================================
echo ""
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
info "Setting up agent runtime..."
info "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# tmux — required so the agent can run detached from the Jupyter kernel.
# Without it, closing the browser tab kills the agent.
if ! command -v tmux &>/dev/null; then
    info "Installing tmux..."
    if command -v apt-get &>/dev/null; then
        apt-get update -qq && apt-get install -y -qq tmux || warn "apt-get install tmux failed — install it manually."
    else
        warn "apt-get not found — install tmux manually."
    fi
fi
command -v tmux &>/dev/null && success "tmux ready ($(tmux -V))."

# Python websocket + HTTP deps used by loboforge_agent.py
if command -v pip &>/dev/null; then
    info "Installing agent Python deps (websockets, aiohttp, gdown)..."
    # gdown is required for Google Drive LoRA downloads — Drive serves a
    # virus-warning HTML page for files >100 MB which plain aiohttp would
    # silently save AS the model. gdown handles the cookie/token dance.
    pip install --quiet --upgrade websockets aiohttp gdown || warn "pip install agent deps failed — agent may not run."
    success "Agent Python deps installed."
fi

# Locate the agent script — checked in priority order
AGENT_SCRIPT=""
for candidate in \
    "${LOBO_AGENT_PATH:-}" \
    "/workspace/loboforge_agent.py" \
    "$PWD/loboforge_agent.py" \
    "$HOME/loboforge_agent.py" \
    "/root/loboforge_agent.py"
do
    if [[ -n "$candidate" && -f "$candidate" ]]; then
        AGENT_SCRIPT="$candidate"
        break
    fi
done

# Self-fetch from prod if not on disk yet. This is the path that lets a
# vast.ai onstart script bootstrap the whole node with just `curl ... | bash`
# — no manual scp / git clone step. Server-side routes for these files
# are added in Program.cs (`/agent/loboforge_agent.py` etc).
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
if [[ -z "$AGENT_SCRIPT" ]]; then
    info "Agent script missing — fetching from $LOBO_BASE_URL/agent/..."
    AGENT_DIR="${LOBO_AGENT_DIR:-/workspace}"
    mkdir -p "$AGENT_DIR"
    resolve_gen_queue_mode
    agent_file="loboforge_agent_sqs.py"
    if [[ "${LOBO_GEN_QUEUE:-sqs}" != "sqs" ]]; then
        agent_file="loboforge_agent.py"
    fi
    if curl -fsSL "$LOBO_BASE_URL/agent/$agent_file" -o "$AGENT_DIR/$agent_file" \
       && curl -fsSL "$LOBO_BASE_URL/agent/wd14_tagger.py"    -o "$AGENT_DIR/wd14_tagger.py"; then
        AGENT_SCRIPT="$AGENT_DIR/$agent_file"
        if [[ "$agent_file" == "loboforge_agent_sqs.py" ]]; then
            curl -fsSL "$LOBO_BASE_URL/agent/loboforge_agent_common.py" -o "$AGENT_DIR/loboforge_agent_common.py" || true
            install_forge_queue_sdk || true
        fi
        success "Agent files fetched to $AGENT_DIR/"
        status_post "agent.fetch" "ok" "from $LOBO_BASE_URL/agent/ → $AGENT_DIR/"
        WORKER_DIR="$AGENT_DIR/loboforge_worker"
        if [[ ! -f "$WORKER_DIR/__init__.py" ]]; then
            if curl -fsSL "$LOBO_BASE_URL/agent/loboforge_worker.tar.gz" -o /tmp/loboforge_worker.tar.gz; then
                tar -xzf /tmp/loboforge_worker.tar.gz -C "$AGENT_DIR"
                rm -f /tmp/loboforge_worker.tar.gz
                success "loboforge_worker package extracted to $WORKER_DIR/"
            else
                warn "loboforge_worker.tar.gz not available — server commands disabled until package is present"
            fi
        fi
    else
        warn "Self-fetch failed. Set LOBO_AGENT_PATH manually or rsync the files in."
        status_post "agent.fetch" "error" "could not curl $LOBO_BASE_URL/agent/loboforge_agent.py"
    fi
fi

if [[ -z "$AGENT_SCRIPT" ]]; then
    warn "loboforge_agent.py still not available — agent auto-launch will be skipped."
    status_post "agent.fetch" "error" "no agent script anywhere; auto-launch skipped"
else
    success "Agent script: $AGENT_SCRIPT"
    AGENT_DIR="$(dirname "$AGENT_SCRIPT")"
    info "Refreshing agent + loboforge_worker from $LOBO_BASE_URL ..."
    resolve_gen_queue_mode
    if lobo_fetch_agent_scripts "$AGENT_DIR"; then
        AGENT_SCRIPT="${AGENT_DIR}/loboforge_agent_sqs.py"
        [[ "${LOBO_GEN_QUEUE:-sqs}" != "sqs" ]] && AGENT_SCRIPT="${AGENT_DIR}/loboforge_agent.py"
        install_forge_queue_sdk || true
        success "Agent scripts refreshed ($AGENT_SCRIPT)"
    else
        warn "Agent refresh failed — using on-disk copy"
    fi
    WORKER_DIR="$AGENT_DIR/loboforge_worker"
    if [[ ! -f "$WORKER_DIR/__init__.py" ]] || [[ "${LOBO_FORCE_WORKER_REFRESH:-0}" == "1" ]]; then
        if curl -fsSL "$LOBO_BASE_URL/agent/loboforge_worker.tar.gz" -o /tmp/loboforge_worker.tar.gz; then
            tar -xzf /tmp/loboforge_worker.tar.gz -C "$AGENT_DIR"
            rm -f /tmp/loboforge_worker.tar.gz
            success "loboforge_worker package extracted to $WORKER_DIR/"
        else
            warn "loboforge_worker.tar.gz not available — server commands disabled until package is present"
        fi
    else
        success "loboforge_worker already present at $WORKER_DIR/"
    fi
fi

assert_disk_headroom() {
    local free_mb min_free
    min_free=$(required_headroom_mb)
    free_mb=$(df -Pm / 2>/dev/null | awk 'NR==2 {print $4}' || echo "")
    if [[ -z "$free_mb" || ! "$free_mb" =~ ^[0-9]+$ ]]; then
        warn "Could not read free disk space on /"
        return 0
    fi
    info "Root disk free: ${free_mb}MB (want ≥${min_free}MB after models for mode=$MODE)"
    status_post "disk.check" "ok" "free_mb=$free_mb min_mb=$min_free mode=$MODE"
    if (( free_mb < min_free )); then
        die "Only ${free_mb}MB free on / after model downloads — need ≥$(( min_free / 1024 ))GB headroom for refs/outputs. Re-rent with more disk (image≥120GB, video≥120GB, all≥150GB)."
    fi
}
assert_disk_headroom

# ── ComfyUI token auto-detect ────────────────────────────────────────────────
# vast.ai's ComfyUI image fronts the ComfyUI HTTP API with a proxy that
# expects an auth token. We don't know definitively where the current image
# stores it (template-dependent), so probe a wide set of likely locations
# AND likely env vars. status_post() below reports the result so the next
# rent never has to guess again.
#
# Force-initialise so `set -u` doesn't blow up when nothing is found
# (SSH-only image variants don't pre-set OPEN_BUTTON_TOKEN — incident
# 2026-05-22 17:30 UTC, both V100 rents died here right after model
# downloads finished).
LOBO_COMFYUI_TOKEN="${LOBO_COMFYUI_TOKEN:-}"
TOKEN_SOURCE=""
if [[ -z "$LOBO_COMFYUI_TOKEN" ]]; then
    # 1. Env vars the vast.ai image might inject.
    for envname in OPEN_BUTTON_TOKEN COMFYUI_TOKEN AUTH_TOKEN JUPYTER_TOKEN; do
        v="${!envname:-}"
        if [[ -n "$v" ]]; then
            LOBO_COMFYUI_TOKEN="$v"
            TOKEN_SOURCE="env:$envname"
            break
        fi
    done

    # 2. Filesystem locations across known vast.ai / Comfy / Jupyter conventions.
    if [[ -z "$LOBO_COMFYUI_TOKEN" ]]; then
        for tokpath in \
            "/etc/vast-auth/comfyui-token" \
            "/etc/vast-auth/auth_token" \
            "/etc/portal-token" \
            "/etc/jupyter/token" \
            "/root/.cache/comfyui-auth/auth_token" \
            "/root/.cache/comfyui/auth_token" \
            "/root/.jupyter/jupyter_server_config.json" \
            "/workspace/.vast/comfyui-token" \
            "/workspace/comfyui-auth/auth_token" \
            "/workspace/comfyui/auth_token" \
            "/workspace/auth.txt" \
            "/var/run/comfyui/token"
        do
            if [[ -f "$tokpath" ]]; then
                LOBO_COMFYUI_TOKEN="$(tr -d '[:space:]' < "$tokpath" 2>/dev/null | head -c 256)"
                TOKEN_SOURCE="file:$tokpath"
                [[ -n "$LOBO_COMFYUI_TOKEN" ]] && break
            fi
        done
    fi

    # 3. Last resort — grep recently-modified vast/portal config for a long
    # hex-looking token. Bounded depth so we don't scan the whole disk.
    if [[ -z "$LOBO_COMFYUI_TOKEN" ]]; then
        for dir in /etc /workspace /root/.cache /root/.config; do
            [[ -d "$dir" ]] || continue
            f=$(grep -rIlE '[0-9a-f]{40,}' "$dir" 2>/dev/null | head -1)
            if [[ -n "$f" ]]; then
                tok=$(grep -oE '[0-9a-f]{40,}' "$f" 2>/dev/null | head -1)
                if [[ -n "$tok" ]]; then
                    LOBO_COMFYUI_TOKEN="$tok"
                    TOKEN_SOURCE="grep:$f"
                    break
                fi
            fi
        done
    fi

    if [[ -n "$LOBO_COMFYUI_TOKEN" ]]; then
        info "ComfyUI token auto-detected via $TOKEN_SOURCE (${#LOBO_COMFYUI_TOKEN} chars)."
        export LOBO_COMFYUI_TOKEN
        status_post "token.detect" "ok" "source=$TOKEN_SOURCE len=${#LOBO_COMFYUI_TOKEN}"
    else
        warn "ComfyUI token not found via any probe. Agent will start without --comfyui-token; if local ComfyUI requires auth, set LOBO_COMFYUI_TOKEN env var before re-rent."
        status_post "token.detect" "warn" "no token found via env/files/grep — agent will start without; image fetchback may fail"
    fi
else
    TOKEN_SOURCE="env:LOBO_COMFYUI_TOKEN"
    info "Using ComfyUI token from env (${#LOBO_COMFYUI_TOKEN} chars)."
    status_post "token.detect" "ok" "source=$TOKEN_SOURCE len=${#LOBO_COMFYUI_TOKEN}"
fi

# =============================================================================
# AGENT LAUNCH — start the agent inside a persistent tmux session with
# auto-restart on crash. Idempotent: re-running this script does NOT
# double-start the agent if a session already exists.
#
# Required env vars for auto-launch:
#   LOBO_SECRET           — node secret (admin panel → GPU Agents)
# Optional env vars:
#   LOBO_SERVER           — server URL (default: wss://www.loboforge.com)
#   LOBO_COMFYUI_TOKEN    — vast.ai ComfyUI auth token for this instance
#   LOBO_LABEL            — Vast label prefix (default: loboforge-{mode}); unique suffix added automatically
#   LOBO_HOSTNAME         — deprecated alias for LOBO_LABEL (suffix still appended)
#   MIN_COMPUTE_MAJOR     — minimum GPU compute major version (default 7 = Volta/V100+)
#   LOBO_EXTRA_ARGS       — extra flags appended to the agent command line
# =============================================================================
TMUX_SESSION="${LOBO_TMUX_SESSION:-loboforge-agent}"
AGENT_LOG="${LOBO_AGENT_LOG:-/workspace/loboforge-agent.log}"
LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
LOBO_HOSTNAME_VAR="$(derive_agent_hostname)"

if [[ -n "${LOBO_SECRET:-}" && -n "${AGENT_SCRIPT:-}" ]] && command -v tmux &>/dev/null; then
    if (( LOBO_AGENT_LAUNCHED )); then
        info "Agent already running from early pool join — skipping relaunch."
    else
        fetch_agent_bundle || true
        launch_loboforge_agent_tmux full || die "Readiness checks failed — agent NOT started."
        echo ""
        info "Management commands:"
        echo "  Attach (watch live):   tmux attach -t ${LOBO_TMUX_SESSION:-loboforge-agent}    (detach: Ctrl+B then D)"
        echo "  Tail log:              tail -f ${LOBO_AGENT_LOG:-/workspace/loboforge-agent.log}"
    fi
else
    # Auto-launch skipped — print the manual command so the user can copy/paste it
    echo ""
    if [[ -z "${LOBO_SECRET:-}" ]]; then
        warn "LOBO_SECRET env var not set — skipping auto-launch."
    fi
    if [[ -n "$AGENT_SCRIPT" ]]; then
        info "To start the agent manually inside a persistent tmux session, run:"
        echo ""
        echo "  export LOBO_SECRET='your-node-secret-here'"
        echo "  export LOBO_COMFYUI_TOKEN='your-comfyui-token-here'   # optional"
        echo "  bash $0   # re-runs the whole script — fast, model downloads are idempotent"
        echo ""
        info "Or invoke the agent directly (NOT detached — will die when this shell exits):"
        echo "  \"$PYBIN\" \"$AGENT_SCRIPT\" --server \"$LOBO_SERVER\" --secret <your-secret> [--comfyui-token <token>]"
    fi
fi

echo ""
success "All done."
status_post "provision.complete" "ok" "provisioning finished — watch /admin → GPU Pool for the agent to register"
echo ""
