#!/usr/bin/env python3
"""Idempotent MoE expert-swap patch for native Wan on VRAM-limited GPUs (<48GB).

Applied by provision_wan_native.sh after loboforge_worker extract. Safe to re-run.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

RUNNER = Path("/workspace/loboforge_worker/inference/wan/runner.py")
IMAGE2VIDEO = Path("/workspace/Wan2.2/wan/image2video.py")


def patch_runner(path: Path) -> bool:
    if not path.is_file():
        print("RUNNER_MISSING", path)
        return False
    t = path.read_text(encoding="utf-8")
    changed = False

    markers = (
        "experts parked on CPU",
        "offload = True  # forced expert swap",
        "pre-generate expert park",
        "Do not keep warm dual-expert cache",
    )
    if all(m in t for m in markers):
        print("RUNNER_ALREADY_OK", path)
        return True

    bak = path.with_suffix(".py.bak_swap")
    if not bak.exists():
        bak.write_text(t, encoding="utf-8")

    old_create = '''    convert_dtype = True
    init_on_cpu = not prefer_warm_pipeline()
    common = dict(
        config=cfg,
        checkpoint_dir=ckpt,
        device_id=0,
        rank=0,
        t5_fsdp=False,
        dit_fsdp=False,
        use_sp=False,
        t5_cpu=False,
        init_on_cpu=init_on_cpu,
        convert_model_dtype=convert_dtype,
    )
    if task.startswith("i2v"):
        pipeline = WanI2V(**common)
    else:
        pipeline = WanT2V(**common)

    if prefer_warm_pipeline() and torch.cuda.is_available():
        try:
            pipeline.low_noise_model.to(pipeline.device)
            pipeline.high_noise_model.to(pipeline.device)
        except Exception as ex:
            log.warning("Warm pipeline GPU move partial: %s", ex)

    return pipeline
'''

    new_create = '''    convert_dtype = True
    # Always keep both DiT experts on CPU between timesteps; generate() swaps one in.
    init_on_cpu = True
    common = dict(
        config=cfg,
        checkpoint_dir=ckpt,
        device_id=0,
        rank=0,
        t5_fsdp=False,
        dit_fsdp=False,
        use_sp=False,
        t5_cpu=True,
        init_on_cpu=init_on_cpu,
        convert_model_dtype=convert_dtype,
    )
    if task.startswith("i2v"):
        pipeline = WanI2V(**common)
    else:
        pipeline = WanT2V(**common)

    try:
        pipeline.low_noise_model.cpu()
        pipeline.high_noise_model.cpu()
        torch.cuda.empty_cache()
        log.info("Native Wan: experts parked on CPU (swap-per-timestep); t5_cpu=True")
    except Exception as ex:
        log.warning("Native Wan CPU park failed: %s", ex)

    return pipeline
'''

    if old_create in t:
        t = t.replace(old_create, new_create, 1)
        changed = True
    elif "experts parked on CPU" not in t:
        t2 = re.sub(
            r"init_on_cpu = not prefer_warm_pipeline\(\)",
            "init_on_cpu = True  # forced: swap experts, never both on GPU",
            t,
            count=1,
        )
        t2 = re.sub(r"t5_cpu=False", "t5_cpu=True", t2, count=1)
        t2 = re.sub(
            r"if prefer_warm_pipeline\(\) and torch\.cuda\.is_available\(\):\n"
            r"        try:\n"
            r"            pipeline\.low_noise_model\.to\(pipeline\.device\)\n"
            r"            pipeline\.high_noise_model\.to\(pipeline\.device\)\n"
            r"        except Exception as ex:\n"
            r"            log\.warning\(\"Warm pipeline GPU move partial: %s\", ex\)\n",
            "try:\n"
            "        pipeline.low_noise_model.cpu()\n"
            "        pipeline.high_noise_model.cpu()\n"
            "        torch.cuda.empty_cache()\n"
            "        log.info(\"Native Wan: experts parked on CPU (swap-per-timestep); t5_cpu=True\")\n"
            "    except Exception as ex:\n"
            "        log.warning(\"Native Wan CPU park failed: %s\", ex)\n",
            t2,
            count=1,
        )
        if t2 != t:
            t = t2
            changed = True

    if "offload = prefer_offload_during_generate()" in t:
        t = t.replace(
            "offload = prefer_offload_during_generate()",
            "offload = True  # forced expert swap during sampling",
        )
        changed = True

    if "pre-generate expert park" not in t:
        needle = "    max_area = int(params[\"width\"]) * int(params[\"height\"])\n    video = pipeline.generate("
        repl = (
            "    try:\n"
            "        import torch\n"
            "        pipeline.low_noise_model.cpu()\n"
            "        pipeline.high_noise_model.cpu()\n"
            "        import gc\n"
            "        gc.collect()\n"
            "        torch.cuda.empty_cache()\n"
            "        log.info(\"pre-generate expert park (swap-only)\")\n"
            "    except Exception as ex:\n"
            "        log.warning(\"pre-generate expert park failed: %s\", ex)\n\n"
            "    max_area = int(params[\"width\"]) * int(params[\"height\"])\n"
            "    video = pipeline.generate("
        )
        if needle in t:
            t = t.replace(needle, repl, 1)
            changed = True

    old_cache = (
        "    if prefer_warm_pipeline():\n"
        "        _PIPELINE_CACHE.clear()\n"
        "        _PIPELINE_CACHE[key] = pipeline\n"
        "    return pipeline"
    )
    new_cache = (
        "    # Do not keep warm dual-expert cache on VRAM-limited boxes.\n"
        "    clear_native_wan_pipeline_cache()\n"
        "    return pipeline"
    )
    if old_cache in t:
        t = t.replace(old_cache, new_cache, 1)
        changed = True

    compile(t, str(path), "exec")
    if changed:
        path.write_text(t, encoding="utf-8")
        print("RUNNER_SWAP_OK", path)
    else:
        print("RUNNER_NOOP", path)
    return True


def patch_image2video(path: Path) -> bool:
    if not path.is_file():
        print("NO_IMAGE2VIDEO", path)
        return False
    w = path.read_text(encoding="utf-8")
    if "swap empty_cache" in w:
        print("IMAGE2VIDEO_ALREADY", path)
        return True
    needle = (
        "            if next(getattr(\n"
        "                    self,\n"
        "                    required_model_name).parameters()).device.type == 'cpu':\n"
        "                getattr(self, required_model_name).to(self.device)\n"
        "        return getattr(self, required_model_name)\n"
    )
    repl = (
        "            if next(getattr(\n"
        "                    self,\n"
        "                    required_model_name).parameters()).device.type == 'cpu':\n"
        "                getattr(self, required_model_name).to(self.device)\n"
        "            # swap empty_cache: free fragments after parking inactive expert\n"
        "            torch.cuda.empty_cache()\n"
        "        return getattr(self, required_model_name)\n"
    )
    if needle not in w:
        print("IMAGE2VIDEO_ANCHOR_MISS", path)
        return False
    path.write_text(w.replace(needle, repl, 1), encoding="utf-8")
    print("IMAGE2VIDEO_SWAP_OK", path)
    return True


def main() -> int:
    ok = patch_runner(RUNNER)
    patch_image2video(IMAGE2VIDEO)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
