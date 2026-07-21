#!/usr/bin/env bash
# Run a command on a STOPPED Vast instance via the execute API and print the result.
# Usage: bash scripts/vast_exec.sh <instance_id> '<command>'
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/lib/secrets.sh"
IID="$1"; shift
CMD="$*"
RESP=$(curl -s -X PUT "https://console.vast.ai/api/v0/instances/command/$IID/" \
  -H "Authorization: Bearer $VAST_API_KEY" -H "Content-Type: application/json" \
  -d "$(python3 -c 'import json,sys; print(json.dumps({"command": sys.argv[1]}))' "$CMD")")
URL=$(printf '%s' "$RESP" | python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("result_url",""))
except: print("")')
if [ -z "$URL" ]; then
  echo "SUBMIT_FAILED: $RESP"; exit 1
fi
for i in $(seq 1 20); do
  sleep 3
  OUT=$(curl -s "$URL" 2>/dev/null)
  if [ -n "$OUT" ]; then printf '%s\n' "$OUT"; exit 0; fi
done
echo "(no result after wait) url=$URL"
