#!/usr/bin/env bash
# Patch all running loboforge-image/video/ltx Vast boxes + loboforge-ollama: deploy EventForge agent + env, restart.
# Wrapper around fix-gen-eventforge-now.sh (SSH + scp required).
#
# Usage:
#   bash scripts/patch-vast-fleet-eventforge.sh
#   INSTANCE_IDS="43664556" bash scripts/patch-vast-fleet-eventforge.sh
#   GEN_WORKERS_EXPECT=3 STAGGER_SEC=60 bash scripts/patch-vast-fleet-eventforge.sh
#
# Requires apps/api/appsettings.Secrets.json (VastAi:ApiKey, Workers:Secret) and prod API
# exposing EventForge URL/worker key via /api/agent/gen-queue-mode.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export EXPECT_MODE="${EXPECT_MODE:-eventforge}"
export LOBO_GEN_QUEUE="${LOBO_GEN_QUEUE:-eventforge}"
export GEN_WORKERS_EXPECT="${GEN_WORKERS_EXPECT:-6}"
bash "$ROOT/scripts/fix-gen-eventforge-now.sh" "$@"
echo ""
echo "▶ Patching loboforge-ollama box..."
bash "$ROOT/scripts/patch-ollama-box-eventforge.sh"
