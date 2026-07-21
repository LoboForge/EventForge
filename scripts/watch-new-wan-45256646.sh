#!/usr/bin/env bash
# Temp monitor for freshly-rented wan-native box 45256646.
# Watches Vast provisioning, retries SSH key attach, polls EventForge fleet
# for check-in + claim_ready=wan. Safe to delete after cutover.
set -uo pipefail
cd "$(git rev-parse --show-toplevel)"
source scripts/lib/secrets.sh
PUB="$(cat ~/.ssh/vast_tmp.pub 2>/dev/null || true)"
IID=45256646
HOST_MATCH="45256646"
SSH_DONE=0

vast_json() {
  python3 - "$1" <<'PY'
import json,sys,urllib.request,os
key=os.environ["VAST_API_KEY"]
iid=int(sys.argv[1])
req=urllib.request.Request("https://console.vast.ai/api/v0/instances/?owner=me",
    headers={"Authorization":f"Bearer {key}"})
for i in json.load(urllib.request.urlopen(req,timeout=30))["instances"]:
    if i["id"]==iid:
        print(json.dumps({k:i.get(k) for k in ("actual_status","cur_state","ssh_host","ssh_port","dph_total")}))
        break
PY
}

attach_ssh() {
  [ -z "$PUB" ] && return 1
  python3 - "$IID" "$PUB" <<'PY'
import json,sys,urllib.request,os
key=os.environ["VAST_API_KEY"]; iid=sys.argv[1]; pub=sys.argv[2]
req=urllib.request.Request(f"https://console.vast.ai/api/v0/instances/{iid}/ssh/",
    data=json.dumps({"ssh_key":pub}).encode(),
    headers={"Authorization":f"Bearer {key}","Content-Type":"application/json"},method="PUT")
try:
    r=urllib.request.urlopen(req,timeout=40); b=r.read().decode()
except urllib.error.HTTPError as e:
    b=e.read().decode()
print(b)
PY
}

for i in $(seq 1 240); do
  TS="$(date -u +%H:%M:%S)"
  V="$(vast_json "$IID" 2>/dev/null)"
  ASTAT="$(printf '%s' "$V" | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d.get("actual_status"))' 2>/dev/null || echo "?")"
  SSHHOST="$(printf '%s' "$V" | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d.get("ssh_host"),d.get("ssh_port"))' 2>/dev/null || echo "?")"

  if [ "$SSH_DONE" != "1" ] && [ "$ASTAT" = "running" ]; then
    R="$(attach_ssh 2>/dev/null || true)"
    if printf '%s' "$R" | grep -q '"success": true'; then
      echo "[$TS] SSH_KEY_ATTACHED ok ($SSHHOST)"; SSH_DONE=1
    else
      echo "[$TS] ssh-attach retry: $(printf '%s' "$R" | head -c 120)"
    fi
  fi

  FLEET="$(curl -sf --max-time 20 -H "Authorization: Bearer $EVENT_FORGE_WORKER_KEY" "$EVENT_FORGE_URL/v1/fleet/workers" 2>/dev/null || true)"
  LINE="$(printf '%s' "$FLEET" | python3 -c "
import json,sys
try: d=json.load(sys.stdin)
except: sys.exit()
for w in d.get('workers',[]):
    h=str(w.get('hostname') or '')
    if '$HOST_MATCH' in h:
        cr=w.get('claim_ready_capabilities')
        print(h,'claim_ready=',cr,'ef_queue=',w.get('ef_queue_status'),'busy=',w.get('busy'))
" 2>/dev/null || true)"

  if [ -n "$LINE" ]; then
    echo "[$TS] vast=$ASTAT CHECKED_IN $LINE"
    if printf '%s' "$LINE" | grep -q "wan"; then
      echo "[$TS] CLAIM_READY_WAN reached — $LINE"
    fi
  else
    echo "[$TS] vast=$ASTAT ssh=$SSHHOST not-checked-in-yet"
  fi
  sleep 90
done
echo "monitor loop ended"
