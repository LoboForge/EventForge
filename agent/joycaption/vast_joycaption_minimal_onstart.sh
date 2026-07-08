#!/usr/bin/env bash
# Vast create --onstart: SSH key + dirs only. Full JoyCaption setup is done by the orchestrator over SSH.
set -euo pipefail
mkdir -p /root/.ssh /workspace/joycaption /workspace/jobs
DEVKEY_LINE='ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIE3GcWhqotDSyTJVMf+PfE8rACP9OZryO+jrMrlMzrok dev@loboforge.com'
grep -qF 'dev@loboforge.com' /root/.ssh/authorized_keys 2>/dev/null || echo "$DEVKEY_LINE" >> /root/.ssh/authorized_keys
chmod 700 /root/.ssh 2>/dev/null || true
chmod 600 /root/.ssh/authorized_keys 2>/dev/null || true
echo "[$(date -Is)] minimal onstart done" >> /workspace/joycaption/bootstrap.log
