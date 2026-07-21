"""Download a single artifact URL (streaming HTTP / hf / gdown)."""

from __future__ import annotations

import logging
import re
import shutil
import subprocess
from pathlib import Path

from ..http_util import urlopen as agent_urlopen

log = logging.getLogger("worker.provision.download_url")

MIN_BYTES = 1_048_576
_HF_RE = re.compile(
    r"https?://huggingface\.co/([^/]+/[^/]+)/(resolve|blob)/[^/]+/(.+?)(\?.*)?$",
)


def normalize_lora_rel(file_path: str) -> str:
    rel = file_path.strip()
    if not rel:
        return rel
    if rel.startswith("loras/"):
        return rel
    if "/" in rel:
        return f"loras/{rel}"
    return f"loras/{rel}"


def download_to(source_url: str, dest: Path, *, hf_token: str | None = None) -> bool:
    dest.parent.mkdir(parents=True, exist_ok=True)
    url = source_url.strip()
    if not url:
        return False

    if "drive.google.com" in url or "docs.google.com" in url:
        return _gdown(url, dest)

    if "huggingface.co" in url:
        if _hf_download(url, dest, hf_token):
            return dest.is_file() and dest.stat().st_size > MIN_BYTES
        log.warning("HF download failed, trying streaming HTTP: %s", url)
        return _stream_http(url, dest)

    return _stream_http(url, dest)


def _stream_http(url: str, dest: Path) -> bool:
    """Stream to a sibling partial and atomically promote a complete response."""
    part = dest.with_suffix(dest.suffix + ".part")
    part.unlink(missing_ok=True)
    try:
        with agent_urlopen(url, timeout=600) as response, part.open("wb") as stream:
            while chunk := response.read(1024 * 1024):
                stream.write(chunk)
        if part.stat().st_size <= MIN_BYTES:
            log.warning("HTTP download too small %s: %d bytes", dest.name, part.stat().st_size)
            part.unlink(missing_ok=True)
            return False
        part.replace(dest)
        return True
    except Exception as ex:
        log.warning("HTTP download failed %s: %s", dest.name, ex)
        part.unlink(missing_ok=True)
        return False


def _gdown(url: str, dest: Path) -> bool:
    executable = shutil.which("gdown")
    if not executable:
        log.warning("gdown not installed or not on PATH — skipping %s", url)
        return False
    part = dest.with_suffix(dest.suffix + ".part")
    part.unlink(missing_ok=True)
    try:
        result = subprocess.run(
            [executable, "--no-cookies", url, "-O", str(part)],
            check=False,
            capture_output=True,
            text=True,
            timeout=900,
        )
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "no downloader output").strip()
            log.warning(
                "gdown failed %s (exit=%d): %s",
                dest.name,
                result.returncode,
                detail[-1200:],
            )
            part.unlink(missing_ok=True)
            return False
        if part.is_file() and part.stat().st_size > MIN_BYTES:
            part.replace(dest)
            return True
        log.warning(
            "gdown output too small %s: %d bytes",
            dest.name,
            part.stat().st_size if part.exists() else 0,
        )
        part.unlink(missing_ok=True)
        return False
    except Exception as ex:
        log.warning("gdown failed %s: %s", dest.name, ex)
        part.unlink(missing_ok=True)
        return False


def _hf_download(url: str, dest: Path, hf_token: str | None) -> bool:
    match = _HF_RE.match(url)
    if not match:
        return False
    repo, _, file_path = match.group(1), match.group(2), match.group(3)
    executable = shutil.which("hf")
    if not executable:
        return False
    tmp = dest.parent / f".hf_tmp_{dest.name}"
    if tmp.exists():
        shutil.rmtree(tmp, ignore_errors=True)
    tmp.mkdir(parents=True, exist_ok=True)
    env = None
    if hf_token:
        import os

        env = os.environ.copy()
        env["HF_TOKEN"] = hf_token
    try:
        subprocess.run(
            [executable, "download", repo, file_path, "--local-dir", str(tmp)],
            check=True,
            capture_output=True,
            text=True,
            timeout=900,
            env=env,
        )
        found = next(tmp.rglob(Path(file_path).name), None)
        if found and found.is_file():
            shutil.move(str(found), dest)
            return dest.stat().st_size > MIN_BYTES
        return False
    except Exception as ex:
        log.warning("hf download failed %s/%s: %s", repo, file_path, ex)
        return False
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
