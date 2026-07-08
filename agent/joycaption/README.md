# JoyCaption EventForge workers (batched GPU inference)

Batched caption workers for EventForge `caption` / `joycaption` jobs.

## Scripts

| File | Role |
|------|------|
| `joycaption_eventforge_worker.py` | Claims jobs in bursts, downloads in parallel, GPU-batches up to `JOYCAPTION_GPU_BATCH_SIZE` |
| `joycaption_server.py` | Subprocess model server — `caption_batch` command |
| `vast_joycaption_onstart.sh` | Vast bootstrap: venv + HF model cache |
| `vast_joycaption_minimal_onstart.sh` | Minimal Vast onstart (SSH + dirs); full setup via deploy script |
| `vast_joycaption_eventforge_worker.sh` | Start worker + watchdog on box |
| `vast_joycaption_watchdog.sh` | Auto-restart hung workers |

## Tunables (env)

- `JOYCAPTION_BATCH_SIZE` — claim burst (default 40)
- `JOYCAPTION_GPU_BATCH_SIZE` — images per `generate()` call (default 6)
- `JOYCAPTION_GPU_BATCH_WAIT_SEC` — wait to fill GPU batch (default 0.12)

## Fleet ops (from repo root)

```bash
# Deploy bundle to running joycaption boxes
bash scripts/deploy-joycaption-fleet.sh

# Tear down entire joycaption fleet
bash scripts/destroy-joycaption-fleet.sh
```

Served at `/agent/joycaption/*` after EventForge deploy.
