#!/usr/bin/env bash
# Terminate all Vast instances labeled joycaption (or caption).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS="${SECRETS_JSON:-$ROOT/secrets.local.json}"
if [[ ! -f "$SECRETS" ]]; then
  echo "Missing secrets: $SECRETS (set SECRETS_JSON)" >&2
  exit 1
fi

VAST_KEY=$(python3 -c "import json; print(json.load(open('$SECRETS'))['EventForge']['VastAi']['ApiKey'])")

mapfile -t IDS < <(python3 - "$SECRETS" <<'PY'
import json, sys, urllib.request
secrets = json.load(open(sys.argv[1]))
key = secrets["EventForge"]["VastAi"]["ApiKey"]
req = urllib.request.Request(
    "https://console.vast.ai/api/v0/instances/",
    headers={"Authorization": f"Bearer {key}"},
)
with urllib.request.urlopen(req, timeout=60) as r:
    data = json.load(r)
for inst in data.get("instances") or []:
    label = (inst.get("label") or "").lower()
    if "joycaption" in label or label == "caption":
        print(inst["id"])
PY
)

if [[ ${#IDS[@]} -eq 0 ]]; then
  echo "No joycaption instances found."
  exit 0
fi

echo "Destroying ${#IDS[@]} joycaption instance(s): ${IDS[*]}"
for id in "${IDS[@]}"; do
  echo -n "  destroy #$id … "
  if curl -sf -X DELETE \
    "https://console.vast.ai/api/v0/instances/${id}/" \
    -H "Authorization: Bearer ${VAST_KEY}" >/dev/null; then
    echo "ok"
  else
    echo "FAILED" >&2
    exit 1
  fi
done
echo "✓ Joycaption fleet destroyed"
