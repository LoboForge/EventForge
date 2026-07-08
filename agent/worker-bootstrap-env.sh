#!/usr/bin/env bash
# Shared bootstrap helpers for Vast GPU onstart / heal / provision.
# Served at /agent/worker-bootstrap-env.sh — source from curl|bash onstart scripts.
# Keep lobo_ensure_ops_ssh in sync with ensure_ops_ssh.sh and WorkerBootstrapDefaults.EnsureOpsSshOnstartLines.

lobo_ensure_ops_ssh() {
  mkdir -p /root/.ssh
  DEVKEY_LINE='ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIE3GcWhqotDSyTJVMf+PfE8rACP9OZryO+jrMrlMzrok dev@loboforge.com'
  grep -qF 'dev@loboforge.com' /root/.ssh/authorized_keys 2>/dev/null || echo "$DEVKEY_LINE" >> /root/.ssh/authorized_keys
  chmod 700 /root/.ssh 2>/dev/null || true
  chmod 600 /root/.ssh/authorized_keys 2>/dev/null || true
}

lobo_worker_secret_ok() {
  [[ -n "${LOBO_SECRET:-}" && "${LOBO_SECRET}" != "change-me-in-admin" ]]
}

lobo_forge_queue_env_defaults() {
  export FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-${AWS_REGION:-us-east-2}}"
  export FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-}"
  export FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"
  # Alias creds from Vast extra_env
  if [[ -n "${FORGE_QUEUE_ACCESS_KEY:-}" && -n "${FORGE_QUEUE_SECRET_KEY:-}" ]]; then
    export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-$FORGE_QUEUE_ACCESS_KEY}"
    export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-$FORGE_QUEUE_SECRET_KEY}"
  fi
  export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-$FORGE_QUEUE_REGION}"
}

lobo_aws_creds_present() {
  [[ -n "${AWS_ACCESS_KEY_ID:-}" && -n "${AWS_SECRET_ACCESS_KEY:-}" ]]
}

lobo_require_aws_creds() {
  lobo_forge_queue_env_defaults
  if lobo_aws_creds_present; then
    return 0
  fi
  echo "ERROR: ForgeQueueWorker IAM required — set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY in Vast extra_env" >&2
  echo "  Admin: Fleet:ForgeQueue:AccessKey/SecretKey in appsettings.Secrets.json (new rents inject automatically)." >&2
  return 1
}

lobo_use_sqs_agent() {
  lobo_forge_queue_env_defaults
  case "${LOBO_GEN_QUEUE:-sqs}" in
    sqs|SQS) return 0 ;;
    *) return 1 ;;
  esac
}


# Fetch all GPU agent scripts required for SQS mode (loboforge_agent_sqs imports loboforge_agent_common).
lobo_fetch_agent_scripts() {
  local dir="${1:-${LOBO_AGENT_DIR:-/workspace}}"
  local base="${LOBO_BASE_URL:-https://www.loboforge.com}"
  mkdir -p "$dir"
  local f
  for f in loboforge_agent.py loboforge_agent_sqs.py loboforge_agent_common.py wd14_tagger.py; do
    if ! curl -fsSL -A 'LoboForge-Worker/1.1' "$base/agent/$f" -o "$dir/$f"; then
      [[ "$f" == "wd14_tagger.py" ]] && continue
      echo "ERROR: could not fetch $base/agent/$f" >&2
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
  local py="${2:-/venv/main/bin/python3}"
  [[ -x "$py" ]] || py="$(command -v python3)"
  PYTHONPATH="${dir}${PYTHONPATH:+:$PYTHONPATH}" \
    "$py" -c "import loboforge_agent_common; import loboforge_agent_sqs" 2>/dev/null
}

lobo_install_forge_queue_sdk() {
  local py="${1:-/venv/main/bin/python3}"
  [[ -x "$py" ]] || py="$(command -v python3)"
  local sdk_dir="${FORGE_QUEUE_SDK_DIR:-/workspace/forge-queue/sdk}"
  if [[ ! -f "$sdk_dir/pyproject.toml" ]]; then
    local base="${LOBO_BASE_URL:-https://www.loboforge.com}"
    if curl -fsSL -A 'LoboForge-Worker/1.1' "$base/agent/forge-queue-sdk.tar.gz" -o /tmp/forge-queue-sdk.tar.gz 2>/dev/null; then
      mkdir -p /workspace
      tar -xzf /tmp/forge-queue-sdk.tar.gz -C /workspace
      rm -f /tmp/forge-queue-sdk.tar.gz
      sdk_dir="/workspace/forge-queue/sdk"
    fi
  fi
  if [[ -f "$sdk_dir/pyproject.toml" ]]; then
    "$py" -m pip install -q -U -e "$sdk_dir" 2>/dev/null || "$py" -m pip install -q -U -e "$sdk_dir"
    return 0
  fi
  echo "WARN: forge-queue SDK not found at $sdk_dir" >&2
  return 1
}

lobo_fetch_gen_queue_mode() {
  lobo_forge_queue_env_defaults
  if [[ -n "${LOBO_GEN_QUEUE:-}" ]]; then
    export LOBO_GEN_QUEUE_PREFIX="${LOBO_GEN_QUEUE_PREFIX:-}"
    lobo_resolve_forge_queue_capabilities "${MODE:-${LOBO_MODE:-all}}"
    return 0
  fi
  if ! lobo_worker_secret_ok; then
    export LOBO_GEN_QUEUE="${LOBO_GEN_QUEUE:-sqs}"
    export LOBO_GEN_QUEUE_PREFIX="${LOBO_GEN_QUEUE_PREFIX:-}"
    lobo_resolve_forge_queue_capabilities "${MODE:-${LOBO_MODE:-all}}"
    return 0
  fi
  local base="${LOBO_BASE_URL:-https://www.loboforge.com}"
  local _gq_json
  _gq_json="$(curl -sf --max-time 10 "${base%/}/api/agent/gen-queue-mode?secret=${LOBO_SECRET}" || echo '{}')"
  LOBO_GEN_QUEUE="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('mode',''))" 2>/dev/null || true)"
  if [[ -z "${LOBO_GEN_QUEUE_PREFIX:-}" ]]; then
    LOBO_GEN_QUEUE_PREFIX="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('queuePrefix',''))" 2>/dev/null || true)"
  fi
  export LOBO_GEN_QUEUE="${LOBO_GEN_QUEUE:-sqs}"
  export LOBO_GEN_QUEUE_PREFIX="${LOBO_GEN_QUEUE_PREFIX:-}"
  if [[ "$LOBO_GEN_QUEUE" == "eventforge" ]]; then
    EVENT_FORGE_URL="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('eventForgeUrl',''))" 2>/dev/null || true)"
    EVENT_FORGE_WORKER_KEY="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('eventForgeWorkerKey',''))" 2>/dev/null || true)"
    export EVENT_FORGE_URL="${EVENT_FORGE_URL:-}"
    export EVENT_FORGE_WORKER_KEY="${EVENT_FORGE_WORKER_KEY:-}"
  elif [[ "$LOBO_GEN_QUEUE" == "sqs" ]]; then
    FORGE_QUEUE_REGION="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueueRegion',''))" 2>/dev/null || true)"
    FORGE_QUEUE_BUCKET="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueueBucket',''))" 2>/dev/null || true)"
    FORGE_QUEUE_PREFIX="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueuePrefix',''))" 2>/dev/null || true)"
    export FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-us-east-2}"
    export FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-}"
    export FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"
  fi
  lobo_resolve_forge_queue_capabilities "${MODE:-${LOBO_MODE:-all}}"
}

# Map provision MODE → comma-separated FORGE_QUEUE_CAPABILITY (fq-{cap}-{tier} queues).
lobo_resolve_forge_queue_capabilities() {
  local mode="${1:-all}"
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
    wan-native) caps="wan" ;;
    dolphin) caps="dolphin" ;;
    *) caps="flux-klein" ;;
  esac
  export FORGE_QUEUE_CAPABILITY="$caps"
}

# Deprecated — IoT/MQTT transport removed from GPU gen fleet (sqs mode uses IAM).
lobo_iot_certs_present() {
  return 1
}

# Persist forge-queue IAM + SQS settings for agent restarts / heal / reboot.
# Vast extra_env is not always visible to tmux children — .loboforge-env is the source of truth.
lobo_write_persisted_env() {
  local env_file="${1:-/workspace/.loboforge-env}"
  lobo_forge_queue_env_defaults
  type lobo_fetch_gen_queue_mode &>/dev/null && lobo_fetch_gen_queue_mode || true
  mkdir -p "$(dirname "$env_file")"
  python3 - "$env_file" <<'PY'
import os, pathlib, sys
path = sys.argv[1]
lines = pathlib.Path(path).read_text(encoding="utf-8").splitlines() if pathlib.Path(path).is_file() else []
ak = os.environ.get("AWS_ACCESS_KEY_ID") or os.environ.get("FORGE_QUEUE_ACCESS_KEY") or ""
sk = os.environ.get("AWS_SECRET_ACCESS_KEY") or os.environ.get("FORGE_QUEUE_SECRET_KEY") or ""
keys = {
    "LOBO_GEN_QUEUE": os.environ.get("LOBO_GEN_QUEUE", "sqs"),
    "LOBO_BASE_URL": os.environ.get("LOBO_BASE_URL", "https://www.loboforge.com"),
    "LOBO_MODE": os.environ.get("LOBO_MODE") or os.environ.get("MODE", ""),
    "MODE": os.environ.get("MODE") or os.environ.get("LOBO_MODE", ""),
    "LOBO_WAN": os.environ.get("LOBO_WAN", ""),
    "LOBO_LTX23": os.environ.get("LOBO_LTX23", ""),
    "LOBO_MUSIC": os.environ.get("LOBO_MUSIC", ""),
    "FORGE_QUEUE_REGION": os.environ.get("FORGE_QUEUE_REGION", "us-east-2"),
    "FORGE_QUEUE_BUCKET": os.environ.get("FORGE_QUEUE_BUCKET", ""),
    "FORGE_QUEUE_PREFIX": os.environ.get("FORGE_QUEUE_PREFIX", "fq"),
    "FORGE_QUEUE_CAPABILITY": os.environ.get("FORGE_QUEUE_CAPABILITY", ""),
    "EVENT_FORGE_URL": os.environ.get("EVENT_FORGE_URL", ""),
    "EVENT_FORGE_WORKER_KEY": os.environ.get("EVENT_FORGE_WORKER_KEY", ""),
    "AWS_ACCESS_KEY_ID": ak,
    "AWS_SECRET_ACCESS_KEY": sk,
    "FORGE_QUEUE_ACCESS_KEY": ak,
    "FORGE_QUEUE_SECRET_KEY": sk,
    "AWS_DEFAULT_REGION": os.environ.get("AWS_DEFAULT_REGION") or os.environ.get("FORGE_QUEUE_REGION", "us-east-2"),
}
hf = os.environ.get("HF_TOKEN", "")
if hf:
    keys["HF_TOKEN"] = hf
out, seen = [], set()
for line in lines:
    stripped = line.strip()
    if not stripped or stripped.startswith("#"):
        out.append(line)
        continue
    body = stripped[7:].strip() if stripped.startswith("export ") else stripped
    key = body.split("=", 1)[0].strip()
    if key in keys:
        out.append(f'export {key}="{keys[key]}"')
        seen.add(key)
    else:
        out.append(line)
for key, val in keys.items():
    if key not in seen and val != "":
        out.append(f'export {key}="{val}"')
pathlib.Path(path).write_text("\n".join(out).rstrip() + "\n", encoding="utf-8")
PY
}

lobo_source_persisted_env() {
  set -a
  [[ -f /workspace/.loboforge-env ]] && . /workspace/.loboforge-env
  set +a
  lobo_forge_queue_env_defaults
}

