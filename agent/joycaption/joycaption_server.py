#!/usr/bin/env python3
"""JoyCaption stdin server — loads model once, captions images on request.

Protocol (one JSON object per line on stdin/stdout):
  {"cmd":"caption","path":"/abs/image.jpg","prepend":"","append":""}
  -> {"caption":"..."} or {"error":"..."}

Logs go to stderr. Emits {"ready":true} on stdout when model is loaded.
"""
from __future__ import annotations

import os
import sys
from pathlib import Path


def _fix_cuda_lib_path() -> None:
    """Point at pip-installed nvidia libs in this venv (bitsandbytes needs these)."""
    venv_lib = Path(sys.prefix) / "lib"
    py_tag = f"python{sys.version_info.major}.{sys.version_info.minor}"
    nvidia = venv_lib / py_tag / "site-packages" / "nvidia"
    extra = [nvidia / "cu13" / "lib", nvidia / "cublas" / "lib"]
    paths = [str(p) for p in extra if p.is_dir()]
    if not paths and venv_lib.is_dir():
        for py_dir in sorted(venv_lib.glob("python3.*")):
            base = py_dir / "site-packages" / "nvidia"
            for sub in ("cu13/lib", "cublas/lib"):
                p = base / sub
                if p.is_dir():
                    paths.append(str(p))
    if paths:
        existing = os.environ.get("LD_LIBRARY_PATH", "")
        os.environ["LD_LIBRARY_PATH"] = ":".join(paths + ([existing] if existing else []))


_fix_cuda_lib_path()

import argparse
import json
from pathlib import Path

import torch
import torchvision.transforms.functional as TVF
from PIL import Image
from transformers import AutoTokenizer, BitsAndBytesConfig, LlavaForConditionalGeneration

SYSTEM = (
    "You are a dataset captioning assistant. Describe only what is clearly visible "
    "in the photograph. Do not invent, infer, or assume details that are not shown."
)


def load_prompts(prompt_file: Path) -> list[str]:
    data = json.loads(prompt_file.read_text(encoding="utf-8"))
    if not isinstance(data, list) or not data:
        raise ValueError("prompt file must be a non-empty JSON list")
    out: list[str] = []
    for item in data:
        if isinstance(item, str):
            out.append(item)
        elif isinstance(item, dict) and isinstance(item.get("prompt"), str):
            out.append(item["prompt"])
    if not out:
        raise ValueError("no prompts in file")
    return out


def load_prompt_registry(prompt_file: Path) -> dict[str, str]:
    """Load default prompt plus optional joycaption_prompt_{key}.json siblings."""
    registry: dict[str, str] = {"default": load_prompts(prompt_file)[0]}
    parent = prompt_file.parent
    stem = prompt_file.stem  # joycaption_prompt
    for key in ("nude", "act"):
        sibling = parent / f"{stem}_{key}.json"
        if sibling.is_file():
            registry[key] = load_prompts(sibling)[0]
    return registry


def build_sample_tensors(
    path: Path, prompt: str, tokenizer, image_token_id: int, image_seq_length: int
) -> dict:
    image = Image.open(path).convert("RGB").resize((384, 384), Image.LANCZOS)
    pixel_values = TVF.pil_to_tensor(image)

    convo = [
        {"role": "system", "content": SYSTEM},
        {"role": "user", "content": prompt},
    ]
    convo_string = tokenizer.apply_chat_template(convo, tokenize=False, add_generation_prompt=True)
    convo_tokens = tokenizer.encode(convo_string, add_special_tokens=False, truncation=False)

    input_tokens: list[int] = []
    for token in convo_tokens:
        if token == image_token_id:
            input_tokens.extend([image_token_id] * image_seq_length)
        else:
            input_tokens.append(token)

    input_ids = torch.tensor(input_tokens, dtype=torch.long)
    attention_mask = torch.ones_like(input_ids)
    return {"pixel_values": pixel_values, "input_ids": input_ids, "attention_mask": attention_mask}


def build_batch(path: Path, prompt: str, tokenizer, image_token_id: int, image_seq_length: int):
    sample = build_sample_tensors(path, prompt, tokenizer, image_token_id, image_seq_length)
    return sample["pixel_values"].unsqueeze(0), sample["input_ids"].unsqueeze(0), sample["attention_mask"].unsqueeze(0)


def collate_samples(samples: list[dict], pad_token_id: int) -> dict:
    max_length = max(item["input_ids"].shape[0] for item in samples)
    n_pad = [max_length - item["input_ids"].shape[0] for item in samples]
    input_ids = torch.stack(
        [
            torch.nn.functional.pad(item["input_ids"], (n, 0), value=pad_token_id)
            for item, n in zip(samples, n_pad)
        ]
    )
    attention_mask = torch.stack(
        [
            torch.nn.functional.pad(item["attention_mask"], (n, 0), value=0)
            for item, n in zip(samples, n_pad)
        ]
    )
    pixel_values = torch.stack([item["pixel_values"] for item in samples])
    return {"pixel_values": pixel_values, "input_ids": input_ids, "attention_mask": attention_mask}


def model_devices(model):
    vision_tower = model.model.vision_tower if hasattr(model, "model") else model.vision_tower
    language_model = model.model.language_model if hasattr(model, "model") else model.language_model
    patch_embed = vision_patch_embedding_weight(vision_tower)
    return patch_embed.device, patch_embed.dtype, language_model.get_input_embeddings().weight.device


@torch.no_grad()
def generate_captions_from_batch(model, tokenizer, collated: dict) -> list[str]:
    vision_device, vision_dtype, language_device = model_devices(model)
    pixel_values = collated["pixel_values"].to(vision_device, non_blocking=True)
    input_ids = collated["input_ids"].to(language_device, non_blocking=True)
    attention_mask = collated["attention_mask"].to(language_device, non_blocking=True)
    pixel_values = TVF.normalize(pixel_values / 255.0, [0.5], [0.5]).to(vision_dtype)

    generate_ids = model.generate(
        input_ids=input_ids,
        pixel_values=pixel_values,
        attention_mask=attention_mask,
        max_new_tokens=256,
        do_sample=False,
        use_cache=True,
    )

    eoh = int(tokenizer.convert_tokens_to_ids("<|end_header_id|>"))
    eot = int(tokenizer.convert_tokens_to_ids("<|eot_id|>"))
    rows = generate_ids.tolist() if isinstance(generate_ids, torch.Tensor) else generate_ids
    out: list[str] = []
    for ids in rows:
        ids = trim_off_prompt(ids, eoh, eot)
        text = tokenizer.decode(ids, skip_special_tokens=False, clean_up_tokenization_spaces=False).strip()
        out.append(text)
    return out


def trim_off_prompt(input_ids: list[int], eoh_id: int, eot_id: int) -> list[int]:
    while True:
        try:
            i = input_ids.index(eoh_id)
        except ValueError:
            break
        input_ids = input_ids[i + 1 :]
    try:
        i = input_ids.index(eot_id)
        return input_ids[:i]
    except ValueError:
        return input_ids


def vision_patch_embedding_weight(vision_tower):
    for obj in (vision_tower, getattr(vision_tower, "vision_model", None)):
        if obj is None:
            continue
        emb = getattr(obj, "embeddings", None)
        if emb is not None and hasattr(emb, "patch_embedding"):
            return emb.patch_embedding.weight
    raise AttributeError(f"Cannot find vision patch embedding on {type(vision_tower).__name__}")


@torch.no_grad()
def caption_image(model, tokenizer, prompt: str, path: Path, prepend: str, append: str) -> str:
    collated = collate_samples(
        [build_sample_tensors(path, prompt, tokenizer, model.config.image_token_index, model.config.image_seq_length)],
        pad_token_id=_pad_token_id(tokenizer),
    )
    text = generate_captions_from_batch(model, tokenizer, collated)[0]
    return f"{prepend}{text}{append}"


def _pad_token_id(tokenizer) -> int:
    pad = tokenizer.pad_token_id
    if pad is not None:
        return int(pad)
    eos = tokenizer.eos_token_id
    if eos is not None:
        return int(eos)
    return 0


@torch.no_grad()
def caption_images_batch(model, tokenizer, prompt_registry: dict[str, str], items: list[dict]) -> list[dict]:
    """True GPU batch inference — groups by prompt_key, one generate() per group."""
    if not items:
        return []

    image_token_id = model.config.image_token_index
    image_seq_length = model.config.image_seq_length
    pad_token_id = _pad_token_id(tokenizer)
    results: list[dict | None] = [None] * len(items)

    groups: dict[str, list[tuple[int, dict]]] = {}
    for idx, item in enumerate(items):
        key = str(item.get("prompt_key") or "default")
        groups.setdefault(key, []).append((idx, item))

    for prompt_key, indexed in groups.items():
        prompt = prompt_registry.get(prompt_key) or prompt_registry["default"]
        pending: list[tuple[int, dict, dict]] = []
        for idx, item in indexed:
            job_id = item.get("jobId")
            path = Path(item.get("path", ""))
            if not path.is_file():
                results[idx] = {"jobId": job_id, "error": f"not found: {path}"}
                continue
            try:
                sample = build_sample_tensors(path, prompt, tokenizer, image_token_id, image_seq_length)
                pending.append((idx, item, sample))
            except Exception as ex:
                results[idx] = {"jobId": job_id, "error": str(ex)}

        if not pending:
            continue

        print(
            f"GPU batch generate: {len(pending)} images prompt_key={prompt_key}",
            file=sys.stderr,
            flush=True,
        )
        collated = collate_samples([sample for _, _, sample in pending], pad_token_id)
        try:
            captions = generate_captions_from_batch(model, tokenizer, collated)
        except Exception as ex:
            for idx, item, _ in pending:
                results[idx] = {"jobId": item.get("jobId"), "error": str(ex)}
            continue

        for (idx, item, _), text in zip(pending, captions):
            prepend = item.get("prepend") or ""
            append = item.get("append") or ""
            results[idx] = {"jobId": item.get("jobId"), "caption": f"{prepend}{text}{append}"}

    ordered: list[dict] = []
    for idx, item in enumerate(items):
        row = results[idx]
        if row is None:
            ordered.append({"jobId": item.get("jobId"), "error": "batch caption missing result"})
        else:
            ordered.append(row)
    return ordered


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="fancyfeast/llama-joycaption-beta-one-hf-llava")
    parser.add_argument("--prompt-file", required=True)
    parser.add_argument("--load-in-4bit", action="store_true")
    parser.add_argument("--load-in-8bit", action="store_true")
    args = parser.parse_args()

    prompt_registry = load_prompt_registry(Path(args.prompt_file))
    print(
        f"Prompt keys: {', '.join(sorted(prompt_registry))}",
        file=sys.stderr,
        flush=True,
    )

    print("Loading JoyCaption model...", file=sys.stderr, flush=True)
    tokenizer = AutoTokenizer.from_pretrained(args.model, use_fast=True)
    if args.load_in_4bit and args.load_in_8bit:
        raise SystemExit("Use only one of --load-in-4bit or --load-in-8bit")
    if args.load_in_4bit:
        print("Precision: 4-bit NF4", file=sys.stderr, flush=True)
        quant = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_quant_type="nf4",
        )
        model = LlavaForConditionalGeneration.from_pretrained(
            args.model, quantization_config=quant, device_map="auto"
        )
    elif args.load_in_8bit:
        print("Precision: 8-bit", file=sys.stderr, flush=True)
        quant = BitsAndBytesConfig(load_in_8bit=True)
        model = LlavaForConditionalGeneration.from_pretrained(
            args.model, quantization_config=quant, device_map="auto"
        )
    else:
        print("Precision: bf16 (full)", file=sys.stderr, flush=True)
        model = LlavaForConditionalGeneration.from_pretrained(
            args.model, torch_dtype=torch.bfloat16, device_map="auto"
        )
    model.eval()
    print(json.dumps({"ready": True}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            print(json.dumps({"error": f"bad json: {e}"}), flush=True)
            continue

        cmd = req.get("cmd")
        if cmd == "quit":
            break
        if cmd == "caption_batch":
            items = req.get("items") or []
            results = caption_images_batch(model, tokenizer, prompt_registry, items)
            print(json.dumps({"results": results}), flush=True)
            continue
        if cmd != "caption":
            print(json.dumps({"error": f"unknown cmd: {cmd}"}), flush=True)
            continue

        path = Path(req.get("path", ""))
        if not path.is_file():
            print(json.dumps({"error": f"not found: {path}"}), flush=True)
            continue

        prepend = req.get("prepend") or ""
        append = req.get("append") or ""
        prompt_key = str(req.get("prompt_key") or "default")
        prompt = prompt_registry.get(prompt_key) or prompt_registry["default"]
        try:
            cap = caption_image(model, tokenizer, prompt, path, prepend, append)
            print(json.dumps({"caption": cap}), flush=True)
        except Exception as e:
            print(json.dumps({"error": str(e)}), flush=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
