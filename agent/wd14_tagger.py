#!/usr/bin/env python3
"""
WD14 image tagger (library + CLI).

Runs SmilingWolf's WD SwinV2 v3 ONNX model.

Import-time use (from the GPU agent):

    from wd14_tagger import tag_image, preload

    preload()                              # optional: warm the session
    result = tag_image("/path/to/out.png") # dict — see RESULT SCHEMA below

CLI use (for debugging):

    python wd14_tagger.py /path/to/image.png   # prints JSON result

RESULT SCHEMA
-------------
{
  "tags":            [str, ...],   # general tags over GENERAL_THRESH
  "character_tags":  [str, ...],   # character tags over CHAR_THRESH
  "rating":          str,          # "general" | "sensitive" | "questionable" | "explicit"
  "rating_scores":   {str: float}, # all four rating confidences
  "is_nsfw":         bool,         # True iff rating in {"questionable","explicit"}
  "source_model":    str
}

On failure `tag_image` returns {"error": "..."} — never raises.

Model + tag CSV are cached to ./wd14_cache/ on first run (~365 MB download).
Requires: numpy, Pillow, onnxruntime (or onnxruntime-gpu).
"""

import csv
import json
import os
import sys
import threading
import urllib.request
from typing import Dict, List, Optional, Tuple

# ── Model & thresholds ───────────────────────────────────────────────────
MODEL_REPO     = "SmilingWolf/wd-swinv2-tagger-v3"
GENERAL_THRESH = 0.35
CHAR_THRESH    = 0.75

DEFAULT_INPUT_SIZE = 448
NSFW_RATINGS = {"questionable", "explicit"}

# ── Paths ────────────────────────────────────────────────────────────────
_HERE      = os.path.dirname(os.path.abspath(__file__))
CACHE_DIR  = os.path.join(_HERE, "wd14_cache")
MODEL_FILE = os.path.join(CACHE_DIR, "model.onnx")
TAGS_FILE  = os.path.join(CACHE_DIR, "selected_tags.csv")

# Lazy-initialised singletons. Loading the ONNX model costs ~1s + GPU VRAM,
# so we do it once and reuse the session for every subsequent call.
_session_lock   = threading.Lock()
_session        = None                 # onnxruntime.InferenceSession
_tags_cache:    Optional[List[Tuple[str, int]]] = None
_input_name:    Optional[str]          = None
_input_size:    int                    = DEFAULT_INPUT_SIZE


def _log(msg: str) -> None:
    """Diagnostic lines to stderr so CLI stdout stays pure JSON."""
    print(msg, file=sys.stderr, flush=True)


# ─────────────────────────────────────────────────────────────────────────
# MODEL DOWNLOAD
# ─────────────────────────────────────────────────────────────────────────
# Minimum bytes a valid model.onnx must be. The real file is ~365 MB. Anything
# under 100 MB is either an HF rate-limit HTML page, a half-finished download,
# or a transparent gzip body urllib gave up on — all of which used to slip
# through as "exists with size > 0" and then ONNX would die with
# `InvalidProtobuf: Protobuf parsing failed`. We refuse to keep it.
_MIN_MODEL_BYTES = 100 * 1024 * 1024
# Tags CSV is tiny (~250 KB). Just check >10 KB so we don't accept HTML.
_MIN_TAGS_BYTES  = 10 * 1024

# urllib's default User-Agent ("Python-urllib/X") sometimes gets bot-blocked
# or rate-limited by HF; a realistic UA works around that without auth.
_UA = "Mozilla/5.0 (LoboForge/wd14_tagger)"


def _looks_like_html(path: str) -> bool:
    """Catch the case where HF returned an error page that urllib wrote as
    'model.onnx'. Real ONNX files start with a protobuf varint, never '<'."""
    try:
        with open(path, "rb") as f:
            head = f.read(64)
        return head.lstrip().startswith((b"<", b"<!", b"<html", b"<HTML"))
    except OSError:
        return False


def _download_one(url: str, dest: str, min_bytes: int) -> None:
    """Download `url` to `dest` with integrity checks. Atomic via .tmp rename.

    Validates: HTTP 200, body is not an HTML error page, final size >= min_bytes.
    On any failure raises RuntimeError — caller decides whether to retry.
    """
    tmp = dest + ".tmp"
    # Clean any half-finished previous attempt.
    for p in (tmp, dest):
        if os.path.exists(p) and not (p == dest and os.path.getsize(p) >= min_bytes and not _looks_like_html(p)):
            try: os.remove(p)
            except OSError: pass

    req = urllib.request.Request(url, headers={"User-Agent": _UA})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            if resp.status != 200:
                raise RuntimeError(f"HTTP {resp.status} from {url}")
            # Stream in 1 MB chunks so we don't double-allocate 365 MB in RAM.
            with open(tmp, "wb") as f:
                while True:
                    chunk = resp.read(1024 * 1024)
                    if not chunk:
                        break
                    f.write(chunk)
    except Exception as e:
        try: os.remove(tmp)
        except OSError: pass
        raise RuntimeError(f"download failed from {url}: {e}")

    # Post-conditions.
    size = os.path.getsize(tmp)
    if size < min_bytes:
        try: os.remove(tmp)
        except OSError: pass
        raise RuntimeError(f"download too small ({size} bytes < {min_bytes}) — partial or error body: {url}")
    if _looks_like_html(tmp):
        try: os.remove(tmp)
        except OSError: pass
        raise RuntimeError(f"download looks like an HTML error page (not a model): {url}")

    os.replace(tmp, dest)  # atomic on POSIX


def _download_if_missing() -> None:
    os.makedirs(CACHE_DIR, exist_ok=True)
    base = f"https://huggingface.co/{MODEL_REPO}/resolve/main"
    for local_path, remote_name, min_bytes in [
        (MODEL_FILE, "model.onnx",         _MIN_MODEL_BYTES),
        (TAGS_FILE,  "selected_tags.csv",  _MIN_TAGS_BYTES),
    ]:
        # Consider the file "present" only if it's big enough AND not HTML.
        # A truncated file with size>0 used to pass this check — that's the
        # exact bug that left both GPUs failing with InvalidProtobuf.
        if (
            os.path.exists(local_path)
            and os.path.getsize(local_path) >= min_bytes
            and not _looks_like_html(local_path)
        ):
            continue
        url = f"{base}/{remote_name}"
        _log(f"[wd14] downloading {url} -> {local_path}")
        _download_one(url, local_path, min_bytes)


def _load_tags() -> List[Tuple[str, int]]:
    with open(TAGS_FILE, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        return [(row["name"], int(row["category"])) for row in reader]


def _providers() -> List[str]:
    try:
        import onnxruntime as ort
        avail = ort.get_available_providers()
        out = []
        if "CUDAExecutionProvider" in avail:
            out.append("CUDAExecutionProvider")
        out.append("CPUExecutionProvider")
        return out
    except Exception:
        return ["CPUExecutionProvider"]


def _ensure_session():
    """Create (once) and return (session, tags, input_name, input_size)."""
    global _session, _tags_cache, _input_name, _input_size
    if _session is not None:
        return _session, _tags_cache, _input_name, _input_size

    with _session_lock:
        if _session is not None:
            return _session, _tags_cache, _input_name, _input_size

        import onnxruntime as ort
        _download_if_missing()
        _tags_cache = _load_tags()

        _log(f"[wd14] loading model with providers={_providers()}")
        # First load attempt. If the on-disk file is *technically* the right
        # size + not HTML but still mangled (mid-stream corruption, mismatched
        # gzip framing, whatever produces InvalidProtobuf), nuke it and let
        # _download_if_missing re-fetch ONCE. Without this, every box stays
        # broken until someone SSHs in and rm's the file by hand.
        try:
            sess = ort.InferenceSession(MODEL_FILE, providers=_providers())
        except Exception as e:
            _log(f"[wd14] InferenceSession load FAILED ({type(e).__name__}: {e}) — "
                 f"discarding model.onnx and re-downloading once.")
            try: os.remove(MODEL_FILE)
            except OSError: pass
            _download_if_missing()
            sess = ort.InferenceSession(MODEL_FILE, providers=_providers())

        inp = sess.get_inputs()[0]
        try:
            _, h, _, _ = inp.shape
            if isinstance(h, int) and h > 0:
                _input_size = h
        except Exception:
            _input_size = DEFAULT_INPUT_SIZE

        _input_name = inp.name
        _session    = sess
        _log(f"[wd14] session ready: input={_input_name}, size={_input_size}, tags={len(_tags_cache)}")

    return _session, _tags_cache, _input_name, _input_size


def preload() -> None:
    """Optional: call once at agent startup to download + warm the model."""
    try:
        _ensure_session()
    except Exception as e:
        _log(f"[wd14] preload failed: {e}")


# ─────────────────────────────────────────────────────────────────────────
# PREPROCESS + INFER
# ─────────────────────────────────────────────────────────────────────────
def _preprocess_image(img, size: int):
    """Shared path for anything openable by PIL — path, bytes, or Image."""
    from PIL import Image
    import numpy as np

    if img.mode != "RGBA":
        img = img.convert("RGBA")

    # Flatten transparency onto white
    canvas = Image.new("RGBA", img.size, (255, 255, 255, 255))
    canvas.paste(img, mask=img.split()[3])
    img = canvas.convert("RGB")

    # Square-pad with white
    w, h = img.size
    s = max(w, h)
    square = Image.new("RGB", (s, s), (255, 255, 255))
    square.paste(img, ((s - w) // 2, (s - h) // 2))

    square = square.resize((size, size), Image.BICUBIC)
    arr = np.asarray(square, dtype=np.float32)
    arr = arr[:, :, ::-1]                  # RGB → BGR
    return np.expand_dims(arr, axis=0)     # NHWC, batch 1


def _score_output(output, tags):
    """Common scoring used by both tag_image and tag_image_bytes."""
    general:   List[Tuple[str, float]] = []
    character: List[Tuple[str, float]] = []
    ratings:   Dict[str, float]        = {}

    for (name, cat), score in zip(tags, output):
        s = float(score)
        if cat == 9:
            ratings[name] = s
        elif cat == 0 and s >= GENERAL_THRESH:
            general.append((name, s))
        elif cat == 4 and s >= CHAR_THRESH:
            character.append((name, s))

    general.sort(key=lambda x: -x[1])
    character.sort(key=lambda x: -x[1])

    rating = max(ratings.items(), key=lambda kv: kv[1])[0] if ratings else "unknown"

    return {
        "tags":           [t for t, _ in general],
        "character_tags": [t for t, _ in character],
        "rating":         rating,
        "rating_scores": {k: round(v, 4) for k, v in ratings.items()},
        "is_nsfw":        rating in NSFW_RATINGS,
        "source_model":   MODEL_REPO,
    }


def tag_image(image_path: str) -> Dict:
    """Tag an image file on disk. Returns result dict or {"error": "..."}."""
    if not os.path.isfile(image_path):
        return {"error": f"image not found: {image_path}"}
    try:
        from PIL import Image
        sess, tags, input_name, input_size = _ensure_session()
        with Image.open(image_path) as img:
            tensor = _preprocess_image(img, input_size)
        output = sess.run(None, {input_name: tensor})[0][0]
        return _score_output(output, tags)
    except Exception as e:
        return {"error": f"{type(e).__name__}: {e}"}


def tag_image_bytes(data: bytes) -> Dict:
    """Tag an image from raw bytes (no disk I/O)."""
    if not data:
        return {"error": "empty image bytes"}
    try:
        import io
        from PIL import Image
        sess, tags, input_name, input_size = _ensure_session()
        with Image.open(io.BytesIO(data)) as img:
            tensor = _preprocess_image(img, input_size)
        output = sess.run(None, {input_name: tensor})[0][0]
        return _score_output(output, tags)
    except Exception as e:
        return {"error": f"{type(e).__name__}: {e}"}


# ─────────────────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────────────────
def _cli() -> int:
    if len(sys.argv) < 2:
        print(json.dumps({"error": "usage: wd14_tagger.py <image_path>"}))
        return 1
    result = tag_image(sys.argv[1])
    print(json.dumps(result))
    return 0 if "error" not in result else 2


if __name__ == "__main__":
    sys.exit(_cli())
