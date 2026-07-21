# Archon worker-provisioning decision policy

This is a machine-followable policy for babysitting EventForge Vast workers. It is based on `docs/worker-provisioning.md`, the packaged provision validators, and the two RTX 4060 Ti Wan provisions observed on 2026-07-21 (instances `45498501` and `45498502`).

## Non-negotiable safety rules

1. Never stop, destroy, or replace an existing worker while it is the only worker serving a capability. Rent first; require the replacement to check in, report the capability in `claim_ready_capabilities`, and successfully claim a job (or verify two or more healthy replacements), then ask for explicit approval before retirement.
2. Never change ECS `desiredCount`. This runbook has no ECS action.
3. Never pause a consumer app for worker maintenance. Quarantine a broken worker through EventForge ops if isolation is required.
4. Never inspect, download, classify, score, or submit queue images or generated outputs. Use only text logs, process state, filenames, byte sizes, HTTP status, and human labels.
5. During a progressing model or LoRA download, do nothing. A long download is not a failure.
6. Do not fabricate missing LoRAs or substitute model files.
7. Never restart an agent or ComfyUI while Comfy has a running/pending prompt or the worker reports a current job. Wait for idle; an agent restart can strand the active lease.

## Poll cadence and evidence record

Poll Vast and SSH every 60 seconds. Record UTC time, instance ID, Vast state, SSH result, relevant process IDs, log size/mtime, partial/final model byte sizes, Comfy health, agent check-in, `claim_ready`, and claim events. Keep a separate intervention log with:

```text
UTC | instance | symptom | diagnosis/evidence | exact action | result
```

Load local credentials without printing them:

```bash
cd "$(git rev-parse --show-toplevel)"
source scripts/lib/secrets.sh
```

List only the target instances:

```bash
python3 - <<'PY'
import json, os, urllib.request
wanted = {45498501, 45498502}
req = urllib.request.Request(
    "https://console.vast.ai/api/v0/instances/?owner=me",
    headers={"Authorization": f"Bearer {os.environ['VAST_API_KEY']}"},
)
for item in json.load(urllib.request.urlopen(req, timeout=30))["instances"]:
    if item["id"] in wanted:
        print(item["id"], item.get("actual_status"), item.get("status_msg"),
              item.get("ssh_host"), item.get("ssh_port"), item.get("label"))
PY
```

SSH form:

```bash
ssh -p "$SSH_PORT" -o StrictHostKeyChecking=no -o ConnectTimeout=12 root@"$SSH_HOST"
```

## Phase 1: Vast allocation and SSH

Expected signals:

- `actual_status` progresses from loading/starting to `running`.
- `status_msg` ends with a successful image/container start.
- `ssh_host` and `ssh_port` become populated.
- SSH commonly trails `running` by several minutes.

WAIT when:

- Vast still reports loading/starting and the state or status message changed within 15 minutes.
- The instance is `running` but SSH has been unavailable for less than 15 minutes.

INTERVENE when:

- Vast reports an explicit terminal error.
- `running` has valid SSH coordinates but TCP/SSH has failed continuously for 15 minutes.
- State and status message have not changed for 15 minutes.

Action order:

1. Re-query the Vast API to exclude stale coordinates.
2. Retry SSH once with `ConnectTimeout=12`.
3. Record the terminal Vast error. Do not destroy the instance automatically.
4. Re-rent only when the failure is terminal or the host is unreachable for 30 minutes, and only after verifying capacity/fleet safety. Keep the failed instance until the replacement is healthy unless it never became a serving worker.

## Phase 2: bootstrap and durable environment

Expected files and processes:

```bash
stat -c '%n %s %y' \
  /workspace/provision.log \
  /workspace/model-provision.log \
  /workspace/loboforge-agent.log \
  /workspace/.loboforge-env 2>/dev/null
pgrep -af 'loboforge_agent|loboforge_worker provision|ComfyUI|main.py|provision_worker|sync-loras'
grep -E '^(export )?(EVENT_FORGE_URL|LOBO_GEN_QUEUE|FORGE_QUEUE_CAPABILITY|MODE|LOBO_MODE|LOBO_WAN|LOBO_LTX23)=' \
  /workspace/.loboforge-env
```

Required safe values for a Comfy Wan-only box:

```text
EVENT_FORGE_URL=https://eventforge.loboforge.com
LOBO_GEN_QUEUE=eventforge
FORGE_QUEUE_CAPABILITY=wan
MODE=video
LOBO_MODE=video
LOBO_WAN=1
LOBO_LTX23=0
LOBO_BASE_URL=https://www.loboforge.com
```

Do not print worker, hub, AWS, or Hugging Face secrets.

WAIT when `provision.log` or `model-provision.log` is growing, a provision/download process exists, or any required artifact's byte count changed in the last 15 minutes.

INTERVENE when bootstrap exited nonzero, no bootstrap/model-provision process exists, required logs have not changed for 15 minutes, and required files remain absent. First retry the idempotent bootstrap with persisted environment:

```bash
set -a
. /workspace/.loboforge-env
set +a
export PYTHONPATH=/workspace
export PATH="/venv/main/bin:$PATH"
/venv/main/bin/python3 -m loboforge_worker bootstrap --mode video \
  >> /workspace/provision.log 2>&1
```

If `.loboforge-env` is absent or required values are wrong, do not guess secrets. Patch the box with the repository fleet script using credentials from `secrets.local.json`:

```bash
INSTANCE_IDS="$INSTANCE_ID" bash scripts/fix-gen-eventforge-now.sh
```

## Phase 3: Wan model downloads and fail-closed completion

For the observed Comfy template, the model root was:

```text
/opt/workspace-internal/ComfyUI/models
```

Required Wan I2V stack and conservative acceptance floors:

```text
diffusion_models/Wan2.2/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors  >= 1,000,000,000
diffusion_models/Wan2.2/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors   >= 1,000,000,000
text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors                      >= 1,000,000,000
vae/wan_2.1_vae.safetensors                                                >=   100,000,000
```

Observed complete sizes on both 4060 Ti boxes:

```text
i2v high UNET  14,294,742,832 bytes
i2v low UNET   14,294,742,832 bytes
T2V UNETs      14,293,923,632 bytes each
UMT5 encoder    6,735,906,897 bytes
Wan VAE           253,815,318 bytes
```

Record byte progress without reading model contents:

```bash
python3 - <<'PY'
import os
root = "/opt/workspace-internal/ComfyUI/models"
for directory, _, names in os.walk(root):
    for name in names:
        low = name.lower()
        if name.endswith((".safetensors", ".safetensors.part")) and any(
            key in low for key in ("wan2.2", "umt5", "wan_2.1_vae")
        ):
            path = os.path.join(directory, name)
            print(os.path.getsize(path), path)
PY
```

WAIT when any required file or `.part` file gained bytes within 15 minutes, the downloader consumes network/CPU, or `model-provision.log` gained output.

INTERVENE when there is no byte/log progress for 15 minutes and one of these is true:

- the download process is gone;
- the log contains a traceback, authentication failure, disk-full error, or repeated terminal download failure;
- `/workspace/.loboforge-provision-done` exists but a required file is absent, below its floor, or structurally invalid.

The actual packaged completion marker is `/workspace/.loboforge-provision-done`. Completion must fail closed: the packaged `is_provision_complete` validator accepts it only when every required artifact passes integrity checks. If a false marker is found:

```bash
rm -f /workspace/.loboforge-provision-done
set -a; . /workspace/.loboforge-env; set +a
export PATH="/venv/main/bin:$PATH" PYTHONPATH=/workspace
/venv/main/bin/python3 -m loboforge_worker provision \
  --secret "$LOBO_SECRET" --mode video --hostname "$LOBO_HOSTNAME" \
  >> /workspace/model-provision.log 2>&1
```

Do not delete a large final file merely because its name is unexpected. Let the packaged validator identify required missing/corrupt artifacts.

## Phase 4: LoRA synchronization and integrity

LoRAs do not gate vanilla Wan readiness. An incomplete active-LoRA set is degraded service, not permission to hold the whole worker offline.

Expected signals:

- `lora-sync.log` reports pulled/skipped/failed counts.
- EventForge job-scoped pulls log a final byte count.
- The agent's `Models found` LoRA count can rise while it self-heals.
- Valid files live below `models/loras/`, not at the `models/` root.

Check the downloader environment:

```bash
pid="$(pgrep -f '[l]oboforge_agent_eventforge.py' | tail -1)"
tr '\0' '\n' < "/proc/$pid/environ" | grep '^PATH='
/venv/main/bin/python3 -c 'import gdown; print(gdown.__version__)'
PATH="/venv/main/bin:$PATH" command -v gdown
```

WAIT when `.part` or final LoRA bytes changed in the last 15 minutes, `Models found` LoRA counts are rising, or job-scoped EventForge pulls are succeeding.

INTERVENE when:

- `gdown not installed` appears even though `/venv/main/bin/python3 -c 'import gdown'` succeeds: the venv is missing from the agent's `PATH`;
- `gdown` runs but reports `Too many users have viewed or downloaded this file recently`: Google Drive has rate-limited the source for up to 24 hours;
- no LoRA bytes/counts/logs changed for 15 minutes and the sync process crashed;
- a final `.safetensors` is under 1 MB or fails structural validation;
- a LoRA was placed directly under `models/`.

Remediation order:

```bash
# 1. Make installed console tools visible and persist the corrected environment.
export PATH="/venv/main/bin:$PATH"
set -a; . /workspace/.loboforge-env; set +a
. /workspace/worker-bootstrap-env.sh
lobo_write_persisted_env /workspace/.loboforge-env

# 2. Put stray root LoRAs where Comfy indexes them.
. /workspace/worker-bootstrap-env.sh
lobo_reconcile_comfy_loras

# 3. Confirm idle. This prints counts only; it does not read prompt/media data.
python3 - <<'PY'
import json, sys, urllib.request
q=json.load(urllib.request.urlopen("http://127.0.0.1:18188/queue", timeout=5))
running=len(q.get("queue_running", []))
pending=len(q.get("queue_pending", []))
print("running", running, "pending", pending)
sys.exit(0 if running == 0 and pending == 0 else 1)
PY

# 4. Only after the idle check succeeds, restart so subprocesses inherit PATH.
bash /workspace/restart-wan-agent.sh
```

For Google Drive's `Too many users` quota, do not restart, re-rent, or retry in a tight loop. The box is healthy. Continue serving vanilla and already-covered LoRA jobs; let the existing idle sync retry on its normal cadence. The durable fix is to publish those real LoRAs to the EventForge asset library or another non-quota source. Wait up to the 24-hour interval stated by Google before classifying the source as persistently unavailable.

The repository sync path downloads to a staging/partial path and only promotes a finished file. Validation reads the safetensors header and declared data offsets; corrupt final copies are removed before retry. Never treat `size > 1 MB` alone as sufficient.

## Phase 5: ComfyUI model visibility

Health and inventory:

```bash
curl -sf http://127.0.0.1:18188/system_stats >/dev/null
curl -sf http://127.0.0.1:18188/object_info/UNETLoader | python3 -c '
import json, sys
d=json.load(sys.stdin)
items=d["UNETLoader"]["input"]["required"]["unet_name"][0]
print(*[x for x in items if "wan" in str(x).lower()], sep="\n")
'
```

The two observed boxes listed all four files as `Wan2.2/...`; no symlinks were required on this template.

WAIT when Comfy returns all required nested `Wan2.2/...` names. Do not add redundant aliases.

INTERVENE when required files pass disk validation but Comfy lists zero or an incomplete high/low pair for more than two agent reconciliation intervals (default 45 seconds; allow 3 minutes). The current agent should log that disk is ready but inventory is stale and restart Comfy automatically.

If self-heal did not run:

```bash
# Refresh/restart the current agent first; it owns the idle-only Comfy re-index.
bash /workspace/restart-wan-agent.sh
```

Only if the image refuses to index nested directories, add top-level aliases idempotently:

```bash
cd /opt/workspace-internal/ComfyUI/models/diffusion_models
for name in \
  wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors \
  wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors \
  wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors \
  wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors
do
  [[ -f "Wan2.2/$name" && ! -e "$name" ]] && ln -s "Wan2.2/$name" "$name"
done
bash /workspace/restart-wan-agent.sh
```

Never restart Comfy during a current job. The agent's automatic path is idle-only and rate-limited.

## Phase 6: agent check-in and claim readiness

Expected sequence:

```text
Models found — UNETs:4 ...
EventForge check-in OK — claim_ready=wan (... loras ack)
EventForge consumer capabilities=('wan',) ...
```

Check:

```bash
pgrep -af loboforge_agent_eventforge.py
python3 - <<'PY'
p="/workspace/loboforge-agent.log"
for line in open(p, errors="replace").read().splitlines()[-200:]:
    if any(k in line.lower() for k in ("check-in", "claim_ready", "claim-ready", "claimed", "released")):
        print(line)
PY
```

WAIT when check-ins repeat and `claim_ready=wan`. A ready worker may remain idle if queued jobs require LoRAs it does not yet have. `Claim-ready worker has ... LoRA-blocked queued job(s)` means continue LoRA self-heal; do not restart or quarantine while bytes/counts progress.

INTERVENE when:

- no successful check-in occurs within 3 minutes of agent start;
- check-in succeeds but claim-ready stays empty for 3 minutes after disk and Comfy validation pass;
- the tmux supervisor or agent PID is absent for more than 3 minutes;
- claims repeatedly release because a required LoRA is missing and neither EventForge nor the hub has a fetchable asset.

Actions:

```bash
# Missing/dead supervisor
bash /workspace/wan-supervisor-watchdog.sh

# Current agent scripts + persisted env + surgical restart
INSTANCE_IDS="$INSTANCE_ID" bash scripts/fix-gen-eventforge-now.sh
```

For an unpublished job LoRA, upload the real asset to EventForge (`POST /v1/assets/loras`) or publish it through the LoboForge active-LoRA catalog. Do not create a placeholder.

## Phase 7: first successful claim

Claim-ready is necessary but is not proof that the queue offered an eligible job. Success requires a textual claim line and a current job/busy transition. Record the job UUID only; do not inspect input/output media.

WAIT when the worker is `claim_ready=wan` and:

- there are no Wan jobs;
- all queued jobs are explicitly LoRA-blocked and LoRA sync is progressing;
- another worker wins the available claim.

INTERVENE when Wan jobs are eligible for this worker, check-ins remain healthy, and no claim occurs for 10 minutes. Inspect bounded claim diagnostics in the agent log. Fix the stated model/LoRA gate; do not churn the box.

## Give-up and re-rent policy

Re-rent instead of further repair only when one of these is verified:

- terminal Vast host/container failure;
- SSH unreachable for 30 minutes after coordinates stabilized;
- unrecoverable disk I/O/filesystem errors;
- physical GPU/CUDA initialization repeatedly fails after one clean container-level retry;
- disk is below the documented mode floor and cannot hold the required stack plus headroom;
- repeated model download authentication/source failure persists for 30 minutes with no bytes and the same source works on another host.

Before re-renting, record evidence. Never destroy the original automatically. Keep it until replacement cutover satisfies the safety rule.

## 2026-07-21 verified intervention log

```text
22:31Z | both | Vast running; SSH available; required Wan I2V/T2V UNETs, UMT5, and VAE present; Comfy healthy | normal completed downloads | no action
22:34Z | both | model provision complete and Comfy listed four Wan UNETs, but check-ins stayed at zero acknowledged models / no claim-ready | bootstrap supervisor had overwritten EventForge agent hashes with older LoboForge-hosted copies; stale copy lacked claim-ready reconciliation | copied current EventForge agent/common/bootstrap helper, persisted correct env, restarted only the agent supervisor | both reported claim_ready=wan immediately
22:37Z | both | every Google Drive active LoRA logged "gdown not installed"; venv package existed but `gdown` was absent from agent PATH | Vast venv console directory was not inherited by supervisor/background sync | persisted `/venv/main/bin` first on PATH and restarted only the agent supervisor | agent process environment now resolves gdown; EventForge job-scoped LoRA downloads continued throughout
22:37Z | both | live PATH rollout restart was issued after each worker had already logged a claim | idle state was not checked before restart; this was unsafe because an active lease can be stranded | both agents came back and reclaimed immediately; no further restart was performed while busy | policy corrected: all future agent/Comfy restarts require zero running and zero pending Comfy prompts plus no current worker job
22:37Z | both | gdown executed but all remaining Drive-backed LoRAs returned exit 1 | one direct diagnostic returned Google's "Too many users have viewed or downloaded this file recently" 24-hour quota message | deleted the diagnostic partial; did not churn workers or retry sources aggressively; staged improved downloader diagnostics for the next idle restart | workers continued Wan jobs with 44 structurally valid LoRAs each
```

No worker was stopped or destroyed. No ECS setting was read or changed. No consumer app was paused.

Observed ready/claim milestones:

```text
45498501 | model provision complete 22:19:05Z | claim_ready=wan 22:34:30Z | first claim 22:35:33Z
45498502 | model provision complete 22:28:31Z | claim_ready=wan 22:34:43Z | first claim 22:35:47Z
```
