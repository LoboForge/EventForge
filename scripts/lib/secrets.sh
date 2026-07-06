#!/usr/bin/env bash
# Load EventForge / fleet patch secrets from secrets.local.json or env.
set -euo pipefail

_LIB="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$_LIB/../.." && pwd)"
SECRETS_FILE="${EVENTFORGE_SECRETS:-$ROOT/secrets.local.json}"

read_json() {
  local key="$1"
  python3 - "$SECRETS_FILE" "$key" <<'PY'
import json, sys
path, dotted = sys.argv[1], sys.argv[2]
try:
    with open(path) as f:
        d = json.load(f)
except FileNotFoundError:
    sys.exit(0)
cur = d
for part in dotted.split("."):
    if not isinstance(cur, dict) or part not in cur:
        sys.exit(0)
    cur = cur[part]
if cur is None:
    sys.exit(0)
print(cur)
PY
}

export EVENT_FORGE_URL="${EVENT_FORGE_URL:-$(read_json EventForge.PublicUrl)}"
export EVENT_FORGE_WORKER_KEY="${EVENT_FORGE_WORKER_KEY:-$(read_json EventForge.WorkerKey)}"
export VAST_API_KEY="${VAST_API_KEY:-$(read_json EventForge.VastAi.ApiKey)}"
export LOBO_BASE_URL="${LOBO_BASE_URL:-$(read_json LoboForge.BaseUrl)}"
export LOBO_SECRET="${LOBO_SECRET:-$(read_json LoboForge.WorkersSecret)}"
