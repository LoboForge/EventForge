#!/usr/bin/env bash
# Remote smoke test — orchestrator runs after bootstrap + script deploy
set -euo pipefail
PY=/workspace/joycaption/venv/bin/python3
test -x "$PY"
test -f /workspace/joycaption/.bootstrapped
test -f /workspace/joycaption/joycaption_eventforge_worker.py
test -f /workspace/joycaption/start_worker.sh
test -x /workspace/joycaption/start_worker.sh
"$PY" -m py_compile /workspace/joycaption/joycaption_eventforge_worker.py
"$PY" -c "import ast; ast.parse(open('/workspace/joycaption/joycaption_eventforge_worker.py').read())"
"$PY" -c "import torch; assert torch.cuda.is_available()"
cap=$("$PY" -c "import torch; print(torch.cuda.get_device_capability(0)[0])")
test "$cap" -ge 7
curl -sf --max-time 20 -o /dev/null https://huggingface.co
echo OK
