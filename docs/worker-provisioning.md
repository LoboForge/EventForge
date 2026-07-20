# Worker provisioning runbook

**Purpose:** Daily ops cheat sheet for renting Vast boxes, bootstrapping workers, and patching the EventForge fleet. Read this first — do not grep the repo for keys or script paths.

**Related:** [AGENTS.md](../AGENTS.md) (architecture), [deploy-to-prod.md](deploy-to-prod.md) (ECS deploy), [QueueIntegration.md](QueueIntegration.md) (integrator API).

---

## Agent quick-start (run this first)

From repo root:

```bash
cd "$(git rev-parse --show-toplevel)"
source scripts/lib/secrets.sh
export EVENT_FORGE_OPS_KEY="$(python3 -c "import json; print(json.load(open('secrets.local.json'))['EventForge']['OpsKey'])")"
```

Or read credentials in one shot (Cursor agents: `Read secrets.local.json`):

| JSON path | Shell variable | Used for |
|-----------|----------------|----------|
| `EventForge.VastAi.ApiKey` | `$VAST_API_KEY` | Vast console API (`console.vast.ai`) |
| `EventForge.OpsKey` | `$EVENT_FORGE_OPS_KEY` | EventForge ops HTTP + ops UI |
| `EventForge.WorkerKey` | `$EVENT_FORGE_WORKER_KEY` | Worker check-in / claim / fleet GET |
| `EventForge.PublicUrl` | `$EVENT_FORGE_URL` | Default `https://eventforge.loboforge.com` |
| `LoboForge.WorkersSecret` | `$LOBO_SECRET` | LoRA prefetch, bootstrap, hub auth |
| `LoboForge.BaseUrl` | `$LOBO_BASE_URL` | Default `https://www.loboforge.com` |

**Setup:** copy `secrets.example.json` → `secrets.local.json` (gitignored). Never commit real keys.

**Prod URLs (fixed):**

| What | URL |
|------|-----|
| EventForge public | https://eventforge.loboforge.com |
| Ops console | https://eventforge.loboforge.com/ops |
| Health (no auth) | https://eventforge.loboforge.com/health |
| Agent scripts | https://eventforge.loboforge.com/agent/{script} |
| LoboForge hub | https://www.loboforge.com |

---

## Provision modes

| Mode | Vast label | Bootstrap script | Disk (rent) | Host disk floor | `FORGE_QUEUE_CAPABILITY` |
|------|------------|------------------|-------------|-----------------|--------------------------|
| `image` | `loboforge-image` | [agent/provision_worker.sh](../agent/provision_worker.sh) | 120 GB | 80 GB | `flux-klein,flux-klein-edit,zimage,chroma` |
| `video` | `loboforge-video` | [agent/provision_worker.sh](../agent/provision_worker.sh) | 130 GB | 90 GB | `wan` (+ `ltx` if music/LTX enabled) |
| `wan-native` | `loboforge-wan-native` | [agent/provision_wan_native.sh](../agent/provision_wan_native.sh) | 256 GB | 220 GB | `wan` |
| `ltx-native` | `loboforge-ltx` | [agent/provision_ltx_native.sh](../agent/provision_ltx_native.sh) | 130 GB | 120 GB | `ltx` |
| `music` | `loboforge-music` | [agent/provision_worker.sh](../agent/provision_worker.sh) | 80 GB | 50 GB | `wan` (ACE-Step rides wan queue) |
| `all` / `both` | `loboforge-all` | [agent/provision_worker.sh](../agent/provision_worker.sh) | 150 GB | 120 GB | all Comfy caps |
| Ollama | `loboforge-ollama` | [agent/provision_ollama.sh](../agent/provision_ollama.sh) | — | — | `dolphin` |

Disk constants: [VastAi/VastAiDiskRequirements.cs](../VastAi/VastAiDiskRequirements.cs).

**Expected prod gen fleet (baseline):** 6 workers — 3× image (V100), 1× video/wan, 1× ltx or wan-native, 1× ollama (+ joycaption ancillary).

---

## Scripts (most recent — use these paths)

| Task | Script |
|------|--------|
| Rent + patch entire Vast gen fleet | [scripts/patch-vast-fleet-eventforge.sh](../scripts/patch-vast-fleet-eventforge.sh) |
| Patch image/video/ltx/wan-native boxes | [scripts/fix-gen-eventforge-now.sh](../scripts/fix-gen-eventforge-now.sh) |
| Patch one video box (SSH args) | [scripts/patch-video-box.sh](../scripts/patch-video-box.sh) `<id> <host> <port>` |
| Patch ollama box | [scripts/patch-ollama-box-eventforge.sh](../scripts/patch-ollama-box-eventforge.sh) |
| Wan native watchdog (cron) | [agent/wan-agent-watchdog.sh](../agent/wan-agent-watchdog.sh) |
| LTX native watchdog (cron) | [agent/ltx-agent-watchdog.sh](../agent/ltx-agent-watchdog.sh) |
| Local dev GPU worker | [scripts/start-local-wrath-worker.sh](../scripts/start-local-wrath-worker.sh) |
| Load secrets | [scripts/lib/secrets.sh](../scripts/lib/secrets.sh) |
| Secrets template | [secrets.example.json](../secrets.example.json) |

**Patch one box by instance id:**

```bash
source scripts/lib/secrets.sh
INSTANCE_IDS="44323527" bash scripts/fix-gen-eventforge-now.sh
```

**Patch entire fleet:**

```bash
bash scripts/patch-vast-fleet-eventforge.sh
GEN_WORKERS_EXPECT=6 bash scripts/patch-vast-fleet-eventforge.sh
```

**Agent files copied by patch scripts:**

- [agent/loboforge_agent_eventforge.py](../agent/loboforge_agent_eventforge.py) — production EventForge transport
- [agent/loboforge_agent_common.py](../agent/loboforge_agent_common.py)
- [agent/loboforge_agent.py](../agent/loboforge_agent.py)
- [agent/worker-bootstrap-env.sh](../agent/worker-bootstrap-env.sh)
- [agent/loboforge_worker.tar.gz](../agent/loboforge_worker.tar.gz)

### Customer LoRAs

Customer LoRAs are downloaded from EventForge at job-claim time when the worker does not already have the requested file. Hub LoRA synchronization remains a LoboForge operation; it is the fallback/preload path, not the customer asset transport.

New image/video Comfy boxes fetch `/agent/*` from `EVENT_FORGE_URL` before falling back to LoboForge, so they receive the deployed claim-time pull logic. Existing boxes do not self-update: after an agent change, deploy EventForge and run `bash scripts/patch-vast-fleet-eventforge.sh`.

---

## EventForge ops API (Vast)

Auth header: `X-EventForge-Ops-Key: $EVENT_FORGE_OPS_KEY` (or `Authorization: Bearer …`).

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/v1/ops/vast/search` | Search offers |
| POST | `/v1/ops/vast/rent` | Rent offer → new instance |
| GET | `/v1/ops/vast/instances/live` | Running instances + SSH |
| POST | `/v1/ops/vast/terminate/{id}` | Destroy instance |
| GET | `/v1/ops/vast/provision-command` | Manual on-start one-liner |
| GET | `/v1/ops/fleet` | Fleet snapshot (ops) |

Implementation: [Api/VastEndpoints.cs](../Api/VastEndpoints.cs), [VastAi/VastAiClient.cs](../VastAi/VastAiClient.cs).

### Search offers (RTX 3090 under $0.20)

```bash
source scripts/lib/secrets.sh
export EVENT_FORGE_OPS_KEY="$(python3 -c "import json; print(json.load(open('secrets.local.json'))['EventForge']['OpsKey'])")"

curl -sf -X POST \
  -H "X-EventForge-Ops-Key: $EVENT_FORGE_OPS_KEY" \
  -H "Content-Type: application/json" \
  "$EVENT_FORGE_URL/v1/ops/vast/search" \
  -d '{
    "minGpuRamGb": 24,
    "maxDollarsPerHr": 0.20,
    "minReliability": 0.95,
    "verifiedOnly": true,
    "gpuNameContains": "3090",
    "sortBy": "cheap",
    "limit": 20
  }' | python3 -m json.tool
```

`sortBy`: `cheap` | `fast` | `bang` | `best`.

### Rent instance

```bash
curl -sf -X POST \
  -H "X-EventForge-Ops-Key: $EVENT_FORGE_OPS_KEY" \
  -H "Content-Type: application/json" \
  "$EVENT_FORGE_URL/v1/ops/vast/rent" \
  -d '{
    "offerId": 40767214,
    "mode": "wan-native",
    "label": "loboforge-wan-native",
    "diskGb": 180
  }' | python3 -m json.tool
```

`mode` values: `image`, `video`, `music`, `all`, `ltx-native`, `wan-native`.

Rent injects EventForge env via [Infrastructure/WorkerBootstrapDefaults.cs](../Infrastructure/WorkerBootstrapDefaults.cs):

- `LOBO_GEN_QUEUE=eventforge`
- `EVENT_FORGE_URL`, `EVENT_FORGE_WORKER_KEY`
- `FORGE_QUEUE_CAPABILITY` (from mode)

---

## Vast console API (direct)

Base: `https://console.vast.ai/api/v0`  
Auth: `Authorization: Bearer $VAST_API_KEY`

### List my instances (SSH host/port)

```bash
source scripts/lib/secrets.sh
python3 <<'PY'
import json, urllib.request, os
key = os.environ["VAST_API_KEY"]
req = urllib.request.Request(
    "https://console.vast.ai/api/v0/instances/?owner=me",
    headers={"Authorization": f"Bearer {key}"})
for inst in json.load(urllib.request.urlopen(req, timeout=30))["instances"]:
    print(inst["id"], inst.get("actual_status"), inst.get("gpu_name"),
          inst.get("dph_total"), inst.get("ssh_host"), inst.get("ssh_port"), inst.get("label"))
PY
```

### Search bundles (correct query shape — POST body alone returns 400)

```bash
source scripts/lib/secrets.sh
python3 <<'PY'
import json, urllib.request, os, urllib.parse
key = os.environ["VAST_API_KEY"]
query = {
    "rentable": {"eq": True},
    "verified": {"eq": True},
    "gpu_ram": {"gte": 24576},
    "dph_total": {"lte": 0.1999},
    "reliability2": {"gte": 0.95},
    "disk_space": {"gte": 120},
    "num_gpus": {"gte": 1},
    "order": [["dph_total", "asc"]],
    "limit": 50,
}
url = "https://console.vast.ai/api/v0/bundles/?q=" + urllib.parse.quote(json.dumps(query))
req = urllib.request.Request(url, headers={"Authorization": f"Bearer {key}"})
for o in json.load(urllib.request.urlopen(req, timeout=60)).get("offers", []):
    if "3090" in (o.get("gpu_name") or ""):
        print(o["id"], o.get("dph_total"), o.get("gpu_name"), o.get("disk_space"), o.get("reliability2"))
PY
```

**Note:** Listed offer `$dph` is GPU-only; actual bill includes disk (130 GB rent ≈ $0.18/hr on cheap 3090 offers).

---

## After rent: bootstrap timeline

1. **Vast on-start** curls `provision_*.sh` from EventForge `/agent/*`, sets env, starts bootstrap in background.
2. **Wait for SSH** — `ssh -p {port} root@{ssh_host}` (often `ssh4.vast.ai`, etc.).
3. **Bootstrap** — models download 20–60 min depending on mode; logs:
   - `/workspace/provision.log` (wan-native on-start)
   - `/workspace/bootstrap.log` (`loboforge_worker bootstrap`)
4. **Agent** — `loboforge_agent_eventforge.py` loop; log: `/workspace/loboforge-agent.log`
5. **Fleet check-in** — worker appears on ops Fleet tab when agent posts check-in (~60s).

### Verify fleet

```bash
source scripts/lib/secrets.sh
curl -sf -H "Authorization: Bearer $EVENT_FORGE_WORKER_KEY" \
  "$EVENT_FORGE_URL/v1/fleet/workers" | python3 -c "
import json,sys
for w in json.load(sys.stdin).get('workers',[]):
    print(w.get('hostname'), w.get('claim_ready_capabilities'), w.get('ef_queue_status'))
"

curl -sf -H "X-EventForge-Ops-Key: $EVENT_FORGE_OPS_KEY" \
  "$EVENT_FORGE_URL/v1/ops/fleet" | python3 -m json.tool | head -80
```

**Claim-ready:** worker must have models on disk *and* pass capability gates. Empty `claim_ready_capabilities` during bootstrap is normal.

### SSH diagnostics (on box)

```bash
grep -E 'EVENT_FORGE|LOBO_GEN|FORGE_QUEUE' /workspace/.loboforge-env
tail -30 /workspace/loboforge-agent.log
pgrep -af 'loboforge_agent|bootstrap|comfy|provision'
curl -sf http://127.0.0.1:18188/system_stats | head -c 200   # Comfy (video/image)
nvidia-smi --query-gpu=name --format=csv,noheader
```

Hostname pattern: `loboforge-{mode}-{last8_of_instance_id}` (e.g. `loboforge-wan-native-44323527`).

---

## Common failures (and fixes)

| Symptom | Cause | Fix |
|---------|-------|-----|
| Agent exits: `forge-queue SDK not installed` | Fixed in repo: lazy SDK import in `loboforge_agent_sqs.py` | Deploy EventForge; old boxes: copy `forge-queue` from working box or patch fleet |
| On-start stalls after `connect-only` | `set -e` + import failure stopped background download | Deploy latest `provision_wan_native.sh` + worker tarball; `wan-agent-watchdog.sh` retries agent |
| `claim_ready=none` | Models still downloading or Comfy empty | Wait for bootstrap; check `/workspace/bootstrap.log` |
| `EF queue` → HTTP 403 | Ops middleware blocking `/health` | `/health` must stay public (prod bug — fix in EventForge deploy) |
| Music stuck on `ltx` queue | Legacy capability routing | Deploy music→wan fix; remap runs on queue load |
| Only 1 fleet row flashing | UI keyed by worker id | Deploy ops UI that keys by `node_uuid`/hostname |
| **Every wan-native job fails: `huggingface-hub>=0.30.0,<1.0 is required ... found 1.24.0`** | Job-time `pip install` (peft/diffusers/Wan2.2 deps) upgrades hf-hub to 1.x. The cron watchdog + module agent only `source .loboforge-env`, which did **not** carry `PIP_CONSTRAINT` (cron doesn't inherit the onstart shell env). | Fixed in repo: `provision_wan_native.sh` now persists `PIP_CONSTRAINT` into `.loboforge-env`. Live box: `echo 'export PIP_CONSTRAINT=/workspace/pip-constraints.txt' >> /workspace/.loboforge-env; /venv/main/bin/pip install 'huggingface_hub>=0.34.0,<1.0'; restart agent tmux. |
| **wan-native OOMs every job on 80GB A100** (`CUDA out of memory`, ~80.5/81 GB used) | `LOBO_UNLOAD_MODELS=0` (warm) keeps BOTH native bf16 A14B experts (28+28GB) + umt5 resident. Fits fp8/Comfy, NOT the native runner. Watchdog line 59 also hardcoded `unload=0` + `SKIP_COMFY=0`, clobbering `.loboforge-env` on every reconnect. | Fixed in repo: provision auto-selects `unload=1` (expert-swap) for VRAM<140GB + `PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True`; watchdog now `SKIP_COMFY=1` and respects persisted `unload`. `WorkerBootstrapDefaults.cs` no longer forces `unload=0`. |
| **Manually-rented wan-native boxes vanish within ~1h; wrong-GPU replacements appear** | `archon-fleet-monitor` (`/media/wrath/AI/archon-fleet-monitor`, `run-fleet-watcher.sh` every 120s) treats `wan-native` as a **singleton** and remediates "not-contributing" boxes; a second cursor-agent-worker also rents on the same Vast account. Net effect: A100 80GB rentals get churned and replaced with RTX 5090/3090 (too small for native A14B). | **Not a provisioning-script bug.** Resolve the competing controller: pause `run-fleet-watcher.sh`, or set `fleet.json` wan-native `min_running`/`singleton` + pin the desired A100 IDs in `pinned_roles`, before renting 80GB boxes. |
| **Image LoRA never indexed; `flux-klein-edit` (or any LoRA-gated) jobs stuck forever** | A LoRA sync from LoboForge dropped the `*.safetensors` at the Comfy `models/` root (or a non-loras subdir, e.g. `klein-deepthroat` under `models/`). ComfyUI only indexes LoRAs under `models/loras/`, so the dispatcher's LoRA gate can never be satisfied. | Fixed in repo: `provision_gpu.sh` + `provision_worker.sh` now relocate any stray root `*.safetensors` into `models/loras/` after every sync (`lobo_reconcile_comfy_loras` in `worker-bootstrap-env.sh`). Live box: `mkdir -p $COMFY/models/loras && mv $COMFY/models/*.safetensors $COMFY/models/loras/` then restart the agent/Comfy so it re-indexes. |
| **hf-hub 1.x breaks jobs on image/video/ltx boxes too** (`huggingface-hub>=0.30.0,<1.0 is required ... found 1.24.0`) | The `PIP_CONSTRAINT` hf-hub pin previously shipped only in `provision_wan_native.sh`. `provision_worker.sh` / `provision_ltx_native.sh` / `provision_gpu.sh` had no pin, and `lobo_write_persisted_env` did not carry `PIP_CONSTRAINT` into `.loboforge-env`. | Fixed in repo: all provision scripts now write `/workspace/pip-constraints.txt` + export `PIP_CONSTRAINT` before any pip install (skipped on transformers 5.x), and `worker-bootstrap-env.sh:lobo_write_persisted_env` persists `PIP_CONSTRAINT` so cron/watchdog launchers inherit it. |
| **Box pulls active-loras from the wrong host / gets zero customer LoRAs** | `LOBO_BASE_URL` was set to `eventforge.loboforge.com`. Active-loras + hub auth live on the LoboForge hub only. | Fixed in repo: every provision script + both watchdogs coerce `LOBO_BASE_URL` away from EventForge to `https://www.loboforge.com` (`lobo_normalize_base_url`); `lobo_write_persisted_env` already coerces the persisted value. |

### Incident 2026-07-18 — wan-native 80GB churn + config bugs

Two manually-rented A100 80GB boxes (45265221, 45265222) plus two earlier ones (45262021, 45262028) kept vanishing. Findings:

1. **Root cause of "always problems" is NOT the script** — a competing controller (`archon-fleet-monitor` + a second cursor-agent-worker in that repo) churns the wan-native fleet on the shared Vast account and rents undersized GPUs (RTX 5090/3090). Manually-rented 80GB boxes get replaced.
2. **hf-hub 1.x re-upgrade** broke every job — the pin lived in the onstart/provision shell but the cron watchdog + module agent only read `.loboforge-env`, which lacked `PIP_CONSTRAINT`.
3. **unload=0 OOM** — native bf16 A14B can't run warm on 80GB; needs expert-swap. The watchdog reconnect path also reset `SKIP_COMFY=0` (breaking native mode / claim-ready) and `unload=0`.
4. **Proof the script works**: once patched live, box 45265221 reached `claim_ready=wan`, ack'd 32 LoRAs, and actively claimed wan jobs. Only the 2 config bugs (now fixed durably) + a stuck-CUDA-context zombie (from force-killing an agent mid-job — drain VRAM with `fuser -k /dev/nvidia*` on restart) blocked completion.

Repo fixes shipped: `agent/provision_wan_native.sh`, `agent/wan-agent-watchdog.sh`, `Infrastructure/WorkerBootstrapDefaults.cs`. **Deploy required** for prod-served `/agent/*` scripts to take effect on new/rebooted boxes.

### Manual recovery (wan-native / video box)

After SSH is up, if on-start died:

```bash
# On the Vast box — env already in process from rent; persist + bootstrap
source /workspace/.loboforge-env 2>/dev/null || true
export PYTHONPATH=/workspace
export LOBO_GEN_QUEUE=eventforge
export EVENT_FORGE_URL=https://eventforge.loboforge.com
export EVENT_FORGE_WORKER_KEY='…'   # from secrets.local.json
export FORGE_QUEUE_CAPABILITY=wan

# If forge-queue SDK missing — copy from working box first (see table above)
/venv/main/bin/pip install -q -e /workspace/forge-queue/sdk

/venv/main/bin/python3 -m loboforge_worker bootstrap --mode video \
  --secret "$LOBO_SECRET" --base-url "$LOBO_BASE_URL" \
  --instance-id "$LOBO_INSTANCE_ID" --label "$LOBO_HOSTNAME" \
  >> /workspace/bootstrap.log 2>&1 &

# Then from repo laptop:
INSTANCE_IDS="<id>" bash scripts/fix-gen-eventforge-now.sh
```

Copy `forge-queue` from working box to new box:

```bash
ssh -p {src_port} root@{src_host} 'tar czf - -C /workspace forge-queue' \
  | ssh -p {dst_port} root@{dst_host} 'tar xzf - -C /workspace'
```

---

## Local worker (wrath machine)

```bash
bash scripts/start-local-wrath-worker.sh
```

Caps (no LTX23 on 16 GB): `flux-klein,flux-klein-edit,zimage,chroma,wan` + `LOBO_MUSIC=1`, `LOBO_LTX23=0`.

---

## Deploy reminder

Changing agent/bootstrap in repo **does not** update running Vast boxes. After merge:

1. Deploy EventForge (CodeBuild → ECS) — [docs/deploy-to-prod.md](deploy-to-prod.md)
2. Patch fleet — `bash scripts/patch-vast-fleet-eventforge.sh`

New rents pick up latest `/agent/*` scripts from the deployed EventForge task immediately.

---

## Code map (only if runbook is insufficient)

| Concern | File |
|---------|------|
| Vast rent + on-start | [VastAi/VastAiClient.cs](../VastAi/VastAiClient.cs) |
| Ops routes | [Api/VastEndpoints.cs](../Api/VastEndpoints.cs) |
| Capability routing | [Infrastructure/GenQueueCapabilities.cs](../Infrastructure/GenQueueCapabilities.cs) |
| Fleet state | [Services/WorkerFleetTracker.cs](../Services/WorkerFleetTracker.cs) |
| Agent allowlist | [Api/AgentEndpoints.cs](../Api/AgentEndpoints.cs) |
| Ops UI Vast tab | [web/src/OpsVastTab.tsx](../web/src/OpsVastTab.tsx) |
