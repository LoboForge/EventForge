#!/usr/bin/env bash
# Poll wan-native Vast boxes — checkpoint download % until READY.
set -uo pipefail

INTERVAL="${1:-90}"
LOG="${WAN_WATCH_LOG:-/tmp/wan-box-progress.log}"
ONCE="${WAN_WATCH_ONCE:-0}"

BOXES=(
  "44182904:22904:ssh8.vast.ai"
  "44566437:16436:ssh2.vast.ai"
  "44566445:16444:ssh8.vast.ai"
  "44566756:16756:ssh2.vast.ai"
  "44566757:16756:ssh2.vast.ai"
)

REMOTE_BODY='set -u
root=/workspace/wan-models
ckpt="$root/Wan2.2-I2V-A14B"
hn=$(grep -m1 LOBO_HOSTNAME /workspace/.loboforge-env 2>/dev/null | sed "s/.*=//" | tr -d "\"'"'"'" || true)
hn=${hn:-unknown}
high=$(du -sb "$ckpt/high_noise_model" 2>/dev/null | awk "{print \$1}" || echo 0)
low=$(du -sb "$ckpt/low_noise_model" 2>/dev/null | awk "{print \$1}" || echo 0)
t5=0
[[ -f "$ckpt/models_t5_umt5-xxl-enc-bf16.pth" ]] && t5=$(stat -c%s "$ckpt/models_t5_umt5-xxl-enc-bf16.pth")
vae=0; [[ -f "$ckpt/Wan2.1_VAE.pth" ]] && vae=$(stat -c%s "$ckpt/Wan2.1_VAE.pth")
loras=$(find "$root/loras" -maxdepth 1 -name "*.safetensors" 2>/dev/null | wc -l | tr -d " ")
df_free=$(df -BG /workspace 2>/dev/null | tail -1 | awk "{gsub(/G/,\"\",\$4); print \$4}")
agent=$(pgrep -cf loboforge_agent_eventforge 2>/dev/null || true); agent=${agent:-0}
hf_dl=$(pgrep -cf "hf download" 2>/dev/null || true); hf_dl=${hf_dl:-0}
provision=$(pgrep -cf provision-wan-native 2>/dev/null || true); provision=${provision:-0}
layout=$([[ -f "$root/layout.json" ]] && echo 1 || echo 0)
pct() { awk -v h="$1" -v n="$2" "BEGIN{if(n<=0||h>=n)printf \"100.0\"; else printf \"%.1f\", (h/n)*100}"; }
p_high=$(pct "$high" 9000000000); p_low=$(pct "$low" 9000000000); p_t5=$(pct "$t5" 11000000000); p_vae=$(pct "$vae" 485000000)
overall=$(awk -v a="$p_high" -v b="$p_low" -v c="$p_t5" -v d="$p_vae" "BEGIN{printf \"%.1f\", (a*25+b*25+c*35+d*15)/100}")
printf "%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s|%s\n" "$hn" "$overall" "$p_t5" "$p_high" "$p_low" "$p_vae" "$loras" "$df_free" "$agent" "$hf_dl" "$provision" "$layout" "$t5"'

poll_box() {
  local id="$1" port="$2" host="$3"
  local line
  line=$(ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=18 "root@${host}" "$REMOTE_BODY" 2>/dev/null \
    | grep '|' | grep -v 'Have fun' | tail -1 || true)
  if [[ -z "$line" ]]; then
    echo "id-$id|?|?|?|?|?|?|?|?|?|?|?|0|0"
    return
  fi
  echo "$line|port:$id"
}

poll_once() {
  local ts spec id rest port host line
  declare -A SEEN=()
  local any_ready=0
  ts=$(date -Is)
  echo "=== Wan fleet progress @ $ts ===" | tee -a "$LOG"
  printf '%-34s %6s %6s %6s %6s %6s  %5s  %s\n' HOST TOTAL T5 HIGH LOW VAE DISK STATUS | tee -a "$LOG"
  printf '%-34s %6s %6s %6s %6s %6s  %5s  %s\n' ---- ----- --- ---- --- --- ---- ------ | tee -a "$LOG"

  for spec in "${BOXES[@]}"; do
    id=${spec%%:*}; rest=${spec#*:}; port=${rest%%:*}; host=${rest##*:}
    line=$(poll_box "$id" "$port" "$host")
    local hn overall p_t5 p_high p_low p_vae loras df_free agent hf_dl provision layout t5b port_tag status
    IFS='|' read -r hn overall p_t5 p_high p_low p_vae loras df_free agent hf_dl provision layout t5b port_tag <<< "$line"
    if [[ "$hn" == id-* ]]; then
      printf '%-34s %6s %6s %6s %6s %6s  %5s  %s\n' "$hn" "?" "?" "?" "?" "?" "?" "offline" | tee -a "$LOG"
      continue
    fi
    if [[ -n "${SEEN[$hn]:-}" ]]; then hn="${hn}→${port_tag#port:}"; else SEEN[$hn]=1; fi
    status=""
    [[ "$hf_dl" != "0" ]] && status+="dl "
    [[ "$provision" != "0" ]] && status+="prov "
    [[ "$agent" != "0" ]] && status+="agent "
    [[ "$layout" == "1" ]] && status+="layout "
    [[ -z "$status" ]] && status="idle"
    if awk -v o="$overall" 'BEGIN{exit !(o>=99)}'; then status="READY"; any_ready=1; fi
    if [[ "$df_free" != "?" ]] && awk -v f="$df_free" 'BEGIN{exit !(f<12)}'; then status+=" LOW_DISK"; fi
    local t5gb; t5gb=$(awk -v b="$t5b" 'BEGIN{printf "%.1f", b/1e9}')
    printf '%-34s %5s%% %5s%% %5s%% %5s%% %5s%%  %4sG  %s (loras:%s t5:%sGB)\n' \
      "$hn" "$overall" "$p_t5" "$p_high" "$p_low" "$p_vae" "$df_free" "$status" "$loras" "$t5gb" | tee -a "$LOG"
  done
  return "$any_ready"
}

if [[ "$ONCE" == "1" ]]; then poll_once; exit 0; fi
echo "Watching wan boxes every ${INTERVAL}s → $LOG"
ready_streak=0
while true; do
  if poll_once; then ready_streak=$((ready_streak+1)); else ready_streak=0; fi
  if [[ "$ready_streak" -ge 2 ]]; then
    echo "[$(date -Is)] READY — 2 consecutive polls with a box >=99%" | tee -a "$LOG"
    exit 0
  fi
  sleep "$INTERVAL"
done
