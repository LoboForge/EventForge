#!/usr/bin/env bash
# Thin bootstrap for new Vast.ai boxes — delegates to loboforge_worker bootstrap.
# Served at /agent/provision_worker.sh (replaces provision_gpu.sh for new rents).
#
# Vast extra_env reboot note: Vast may not pass extra_env to nohup/curl|bash children.
# Onstart scripts inline-export AWS_* / FORGE_QUEUE_* before invoking this script.
# After reboot, agent loops re-read /workspace/.loboforge-env written at first provision.
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

mkdir -p /workspace
cd /workspace

MODE="${MODE:-all}"
if [[ -z "${LOBO_SECRET:-}" || "${LOBO_SECRET}" == "change-me-in-admin" ]]; then
  echo "ERROR: LOBO_SECRET must be Workers:Secret from Vast extra_env" >&2
  exit 1
fi
LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
# LOBO_BASE_URL must be the LoboForge hub (active-loras / hub auth), NEVER EventForge.
case "$(printf '%s' "$LOBO_BASE_URL" | tr '[:upper:]' '[:lower:]')" in
  *eventforge.loboforge.com*) LOBO_BASE_URL="https://www.loboforge.com" ;;
esac
LOBO_BASE_URL="${LOBO_BASE_URL%/}"
LOBO_SCRIPT_FALLBACK="${LOBO_SCRIPT_FALLBACK:-https://www.loboforge.com}"
LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-${CONTAINER_ID:-unknown}}"

# HF_TOKEN must come from Vast extra_env (never commit tokens to git).

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode) MODE="$2"; shift 2 ;;
        --help|-h)
            echo "Usage: $0 [--mode image|video|music|all|<comma-list>]"
            exit 0
            ;;
        *) shift ;;
    esac
done

# After --mode is resolved — default label must not be computed while MODE still defaults to all.
LOBO_LABEL="${LOBO_LABEL:-loboforge-${MODE}}"

HF_TOKEN="${HF_TOKEN:-}"
export MODE LOBO_SECRET LOBO_SERVER LOBO_BASE_URL LOBO_INSTANCE_ID LOBO_LABEL
export LOBO_MODE="$MODE"
export LOBO_WAN="${LOBO_WAN:-1}"
_norm_mode="$(printf '%s' "$MODE" | tr '[:upper:]' '[:lower:]' | cut -d, -f1)"
if [[ "$_norm_mode" == "all" || "$_norm_mode" == "both" || "$_norm_mode" == "ltx" || "$_norm_mode" == "ltx-native" ]]; then
  export LOBO_LTX23="${LOBO_LTX23:-1}"
else
  export LOBO_LTX23="${LOBO_LTX23:-0}"
fi
export HF_TOKEN HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"
export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/dev/null
  . /workspace/worker-bootstrap-env.sh
  lobo_resolve_forge_queue_capabilities "$MODE"
else
  _caps=""
  case "$_norm_mode" in
    image) _caps="flux-klein,flux-klein-edit,zimage,chroma" ;;
    video)
      [[ "${LOBO_WAN:-1}" != "0" ]] && _caps="wan"
      if [[ "${LOBO_LTX23:-0}" == "1" ]]; then
        [[ -n "$_caps" ]] && _caps="${_caps},ltx" || _caps="ltx"
      fi
      [[ -z "$_caps" ]] && _caps="wan"
      ;;
    music) _caps="ltx" ;;
    all|both)
      _caps="flux-klein,flux-klein-edit,zimage,chroma"
      [[ "${LOBO_WAN:-1}" != "0" ]] && _caps="${_caps},wan"
      [[ "${LOBO_LTX23:-0}" == "1" ]] && _caps="${_caps},ltx"
      ;;
    ltx-native|ltx) _caps="ltx" ;;
    *) _caps="flux-klein" ;;
  esac
  export FORGE_QUEUE_CAPABILITY="$_caps"
fi

# forge-queue SQS env (IAM creds from Vast extra_env — no IoT certs)
# Jobs: forge-queue SQS only. API: check-in + LoRA prefetch (request-work) only.
export FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-${AWS_REGION:-us-east-2}}"
export FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-}"
export FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"
if [[ -n "${FORGE_QUEUE_ACCESS_KEY:-}" && -n "${FORGE_QUEUE_SECRET_KEY:-}" ]]; then
  export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-$FORGE_QUEUE_ACCESS_KEY}"
  export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-$FORGE_QUEUE_SECRET_KEY}"
fi
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-$FORGE_QUEUE_REGION}"

_gen_queue="${LOBO_GEN_QUEUE:-}"
if [[ -z "$_gen_queue" ]] && type lobo_fetch_gen_queue_mode &>/dev/null; then
  lobo_fetch_gen_queue_mode || true
  _gen_queue="${LOBO_GEN_QUEUE:-}"
fi

if [[ "$_gen_queue" != "eventforge" ]]; then
  if [[ -z "${AWS_ACCESS_KEY_ID:-}" || -z "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
    echo "ERROR: ForgeQueueWorker IAM required — AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY missing." >&2
    echo "  Admin: Fleet:ForgeQueue:AccessKey/SecretKey in appsettings.Secrets.json (new rents inject via Vast extra_env)." >&2
    exit 1
  fi
fi
unset _gen_queue

PY="/venv/main/bin/python3"
[[ -x "$PY" ]] || PY="$(command -v python3)"
# Worker helpers invoke the `hf` and `gdown` console scripts by name. Vast's
# venv is not consistently on PATH even though those packages are installed,
# which made every Google Drive LoRA falsely report "gdown not installed".
export PATH="$(dirname "$PY"):$PATH"

# Durable hf-hub pin BEFORE any pip install — transformers 4.x needs hf-hub<1.0, and
# later job-time pip installs otherwise upgrade it to 1.x and break every job. Skipped
# for transformers 5.x (needs hf-hub 1.x). Persisted into .loboforge-env below so cron /
# watchdog launchers inherit it. See worker-bootstrap-env.sh:lobo_ensure_hf_hub_pin.
if [[ -z "${PIP_CONSTRAINT:-}" ]] && ! "$PY" -c 'import transformers,sys; sys.exit(0 if int(transformers.__version__.split(".")[0])>=5 else 1)' 2>/dev/null; then
  echo 'huggingface_hub>=0.34.0,<1.0' > /workspace/pip-constraints.txt
  export PIP_CONSTRAINT="/workspace/pip-constraints.txt"
fi

# Agent imports need these before bootstrap touches loboforge_agent.py. Do not
# silently treat a failed install as success: retry verbosely, then fail closed
# if the required transport modules are still unavailable. gdown is optional
# for vanilla jobs but must be diagnosed explicitly for hub-backed LoRA sync.
_lf_agent_deps=(websockets aiohttp gdown "huggingface_hub>=0.36.2,<1.0" boto3)
if ! "$PY" -m pip install -q -U "${_lf_agent_deps[@]}"; then
  echo "[WARN] Quiet agent dependency install failed; retrying with diagnostics." >&2
  "$PY" -m pip install -U "${_lf_agent_deps[@]}" || true
fi
if ! "$PY" -c 'import aiohttp, boto3, huggingface_hub, websockets'; then
  echo "ERROR: required EventForge agent dependencies are not importable after retry." >&2
  exit 1
fi
if ! "$PY" -c 'import gdown' || ! command -v gdown >/dev/null 2>&1; then
  echo "[WARN] gdown unavailable after install; vanilla jobs remain enabled, Drive-backed LoRA sync will self-heal after dependency repair." >&2
fi
unset _lf_agent_deps

# Always refresh from prod — onstart only runs once; a failed first attempt must not
# leave a stale loboforge_worker tree (regression: import-before-pip in old bundle).
_lf_fetch() {
  local file="$1" dest="$2" optional="${3:-0}"
  local base
  local bases=("${EVENT_FORGE_URL:-https://eventforge.loboforge.com}")
  bases+=("$LOBO_BASE_URL" "$LOBO_SCRIPT_FALLBACK")
  for base in "${bases[@]}"; do
    if curl -fsSL -A 'LoboForge-Worker/1.1' "${base%/}/agent/$file" -o "$dest"; then
      return 0
    fi
  done
  [[ "$optional" == 1 ]] && return 0
  return 1
}
_lf_fetch loboforge_worker.tar.gz /tmp/loboforge_worker.tar.gz
tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
rm -f /tmp/loboforge_worker.tar.gz
_lf_fetch loboforge_agent.py /workspace/loboforge_agent.py
_lf_fetch loboforge_agent_common.py /workspace/loboforge_agent_common.py
_lf_fetch loboforge_agent_sqs.py /workspace/loboforge_agent_sqs.py
_lf_fetch loboforge_agent_eventforge.py /workspace/loboforge_agent_eventforge.py
_lf_fetch wd14_tagger.py /workspace/wd14_tagger.py 1 || true
_lf_fetch worker-bootstrap-env.sh /workspace/worker-bootstrap-env.sh
if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/dev/null
  . /workspace/worker-bootstrap-env.sh
  lobo_fetch_gen_queue_mode || true
  lobo_install_forge_queue_sdk "$PY" || true
fi

if type lobo_write_persisted_env &>/dev/null; then
  lobo_write_persisted_env /workspace/.loboforge-env
fi

"$PY" -m loboforge_worker bootstrap --mode "$MODE" 2>&1 | tee /workspace/provision.log

# The bootstrap package starts its supervisor before returning. Older bundles
# refreshed the agent from LOBO_BASE_URL (the hub), which could overwrite the
# current EventForge transport with a stale copy. Reassert the authoritative
# EventForge-first copies and restart the supervisor once so new agent logic is
# actually loaded; do not wait for the long-running model download.
if [[ "$_norm_mode" == "video" || "$_norm_mode" == "all" ]]; then
  _lf_fetch loboforge_agent.py /workspace/loboforge_agent.py
  _lf_fetch loboforge_agent_common.py /workspace/loboforge_agent_common.py
  _lf_fetch loboforge_agent_eventforge.py /workspace/loboforge_agent_eventforge.py
  _lf_fetch restart-wan-agent.sh /workspace/restart-wan-agent.sh
  chmod +x /workspace/restart-wan-agent.sh
  if ! bash /workspace/restart-wan-agent.sh >> /workspace/provision.log 2>&1; then
    echo "[WARN] Fresh agent scripts installed but supervisor restart failed; cron watchdog will retry." \
      | tee -a /workspace/provision.log
  fi
fi

# LoRA sync for this box's mode (image/video/all) from loboforge.com.
#
# LoRAs never gate wan claim_ready — a box with the full Wan model stack must
# serve vanilla (no-LoRA) jobs immediately. The EventForge claim gate routes
# LoRA jobs only to workers whose validated known_loras (or the EF asset
# library) cover them, so an incomplete LoRA set cannot cause job failures.
# For video/all we still sync eagerly, validate every safetensors, remove
# corrupt copies, and keep retrying in the background until the set is whole.
_lora_mode="all"
case "$_norm_mode" in
  video) _lora_mode="video" ;;
  image) _lora_mode="image" ;;
esac

sync_loras_verified() {
  local output rc failed invalid
  set +e
  output=$(
    "$PY" -m loboforge_worker sync-loras \
      --base-url "$LOBO_BASE_URL" \
      --secret "$LOBO_SECRET" \
      --mode "$_lora_mode" 2>&1
  )
  rc=$?
  set -e
  printf '%s\n' "$output" | tee -a /workspace/lora-sync.log
  (( rc == 0 )) || return "$rc"
  failed=$(printf '%s\n' "$output" | "$PY" -c '
import ast, sys
failed = 0
for line in sys.stdin:
    try:
        value = ast.literal_eval(line.strip())
    except (SyntaxError, ValueError):
        continue
    if isinstance(value, dict):
        failed += int(value.get("failed") or 0)
print(failed)
')
  invalid=$(
    LOBO_VERIFY_BASE="$LOBO_BASE_URL" \
    LOBO_VERIFY_SECRET="$LOBO_SECRET" \
    LOBO_VERIFY_MODE="$_lora_mode" \
    "$PY" - <<'PY'
import json
import os
import urllib.parse
from pathlib import Path

from loboforge_worker.http_util import urlopen
from loboforge_worker.paths import find_models_root
from loboforge_worker.provision.download_url import normalize_lora_rel

root = find_models_root()
if root is None:
    print(1)
    raise SystemExit

base = os.environ["LOBO_VERIFY_BASE"].rstrip("/")
secret = urllib.parse.quote(os.environ["LOBO_VERIFY_SECRET"], safe="")
mode = urllib.parse.quote(os.environ["LOBO_VERIFY_MODE"], safe="")
url = f"{base}/api/agent/active-loras?modes={mode}&secret={secret}"
with urlopen(url, timeout=120) as response:
    rows = json.loads(response.read().decode())

def valid(path: Path) -> bool:
    try:
        size = path.stat().st_size
        if size < 1_000_000:
            return False
        if path.suffix.lower() != ".safetensors":
            return True
        with path.open("rb") as stream:
            prefix = stream.read(8)
            if len(prefix) != 8:
                return False
            header_len = int.from_bytes(prefix, "little")
            if header_len <= 0 or header_len > 100 * 1024 * 1024 or 8 + header_len > size:
                return False
            header = json.loads(stream.read(header_len).decode("utf-8"))
        max_end = max(
            (
                int(meta["data_offsets"][1])
                for name, meta in header.items()
                if name != "__metadata__"
                and isinstance(meta, dict)
                and isinstance(meta.get("data_offsets"), list)
                and len(meta["data_offsets"]) == 2
            ),
            default=0,
        )
        return size >= 8 + header_len + max_end
    except (OSError, ValueError, UnicodeDecodeError, json.JSONDecodeError):
        return False

invalid = []
downloadable = 0
for row in rows if isinstance(rows, list) else []:
    if not isinstance(row, dict):
        continue
    file_path = str(row.get("file_path") or "").strip()
    source_url = str(row.get("source_url") or "").strip()
    if not file_path or not source_url:
        continue
    downloadable += 1
    path = root / normalize_lora_rel(file_path)
    if not valid(path):
        invalid.append(path)
        # Remove corrupt/partial files so the next retry does not hit the
        # worker package's legacy ">1MB means skip" shortcut.
        path.unlink(missing_ok=True)

if downloadable == 0:
    print(
        "[WARN] Active LoRA catalog was empty; refusing false-complete video provisioning.",
        file=os.sys.stderr,
    )
    print(1)
elif invalid:
    print(
        "[WARN] Invalid active LoRAs removed for retry: "
        + ", ".join(sorted({path.name for path in invalid}, key=str.lower)),
        file=os.sys.stderr,
    )
    print(len(invalid))
else:
    print(0)
PY
  )
  [[ "${failed:-1}" == "0" && "${invalid:-1}" == "0" ]]
}

if [[ "$_norm_mode" == "video" || "$_norm_mode" == "all" ]]; then
  _lora_synced=0
  for _lora_attempt in 1 2 3; do
    if sync_loras_verified; then
      _lora_synced=1
      break
    fi
    echo "[WARN] Active LoRA sync incomplete (attempt $_lora_attempt/3); retrying in 60s." | tee -a /workspace/lora-sync.log
    sleep 60
  done
  type lobo_reconcile_comfy_loras &>/dev/null && lobo_reconcile_comfy_loras || true
  if (( ! _lora_synced )); then
    # Do NOT fail the bootstrap: the box can still run vanilla wan jobs.
    # Keep self-healing in the background until the validated set is complete.
    echo "[WARN] Active LoRA sync still incomplete after 3 attempts; continuing (vanilla jobs OK) with background self-heal." | tee -a /workspace/lora-sync.log
    nohup bash -lc "
      source /workspace/.loboforge-env 2>/dev/null || true
      [[ -f /workspace/worker-bootstrap-env.sh ]] && . /workspace/worker-bootstrap-env.sh 2>/dev/null || true
      export PYTHONPATH=/workspace
      for _i in \$(seq 1 48); do
        sleep 300
        $PY -m loboforge_worker sync-loras --base-url \"$LOBO_BASE_URL\" --secret \"$LOBO_SECRET\" --mode \"$_lora_mode\" && \
          { type lobo_reconcile_comfy_loras &>/dev/null && lobo_reconcile_comfy_loras || true; break; }
      done
    " >> /workspace/lora-sync.log 2>&1 &
  fi
  unset _lora_attempt _lora_synced
else
  nohup bash -lc "
    source /workspace/.loboforge-env 2>/dev/null || true
    [[ -f /workspace/worker-bootstrap-env.sh ]] && . /workspace/worker-bootstrap-env.sh 2>/dev/null || true
    export PYTHONPATH=/workspace
    $PY -m loboforge_worker sync-loras --base-url \"$LOBO_BASE_URL\" --secret \"$LOBO_SECRET\" --mode \"$_lora_mode\"
    type lobo_reconcile_comfy_loras &>/dev/null && lobo_reconcile_comfy_loras || true
  " >> /workspace/lora-sync.log 2>&1 &
fi

# Durable supervisor watchdog for Comfy WAN/video boxes (cron every 3 min).
# The bootstrap starts the agent inside a `loboforge-agent` tmux supervisor loop,
# but if that whole tmux session dies (OOM / tmux crash) nothing respawns it and
# the GPU sits idle (incident 2026-07-21). This external cron watchdog survives
# agent + tmux death and re-launches via restart-wan-agent.sh.
if [[ "$_norm_mode" == "video" || "$_norm_mode" == "all" ]]; then
  _lf_fetch wan-supervisor-watchdog.sh /workspace/wan-supervisor-watchdog.sh 1 || true
  _lf_fetch restart-wan-agent.sh /workspace/restart-wan-agent.sh 1 || true
  chmod +x /workspace/wan-supervisor-watchdog.sh /workspace/restart-wan-agent.sh 2>/dev/null || true
  service cron start 2>/dev/null || (command -v cron >/dev/null && ! pgrep -x cron >/dev/null && cron) 2>/dev/null || true
  (crontab -l 2>/dev/null | grep -v 'wan-supervisor-watchdog.sh' || true
   echo '*/3 * * * * bash /workspace/wan-supervisor-watchdog.sh >> /workspace/wan-supervisor-watchdog.log 2>&1') | crontab - 2>/dev/null || true
  echo "installed wan-supervisor-watchdog cron (*/3)" | tee -a /workspace/provision.log
fi

# Model downloads: tmux session loboforge-provision (started by bootstrap)
#   tail -f /workspace/model-provision.log
# LoRA sync log: tail -f /workspace/lora-sync.log
