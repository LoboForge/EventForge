#!/usr/bin/env bash
# Sync loboforge-aws tunnel bridge when DNS is not yet on eventforge-aws CNAME.
# Sidecar tunnel (eventforge-aws) is preferred; this keeps public URL alive until DNS is fixed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=eventforge-tunnel-lib.sh
source "${ROOT}/scripts/eventforge-tunnel-lib.sh"

dry_run=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=1 ;;
  esac
done

sync_eventforge_tunnel_bridge "$dry_run"
