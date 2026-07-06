#!/usr/bin/env bash
# Ops SSH bootstrap for Vast GPU boxes.
# Vast account SSH injection often fails on new rents and reboots — keep dev@loboforge.com
# in root authorized_keys. Sourced by provision_*.sh and vast-agent-only-onstart.sh.
# Keep in sync with WorkerBootstrapDefaults.EnsureOpsSshOnstartLines (Vast onstart injection).
mkdir -p /root/.ssh
DEVKEY_LINE='ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIE3GcWhqotDSyTJVMf+PfE8rACP9OZryO+jrMrlMzrok dev@loboforge.com'
grep -qF 'dev@loboforge.com' /root/.ssh/authorized_keys 2>/dev/null || echo "$DEVKEY_LINE" >> /root/.ssh/authorized_keys
chmod 700 /root/.ssh 2>/dev/null || true
chmod 600 /root/.ssh/authorized_keys 2>/dev/null || true
