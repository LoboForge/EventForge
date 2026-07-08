#!/usr/bin/env python3
"""CLI hook for JOYCAPTION_CMD — captions one image via joycaption_server stdin protocol."""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

PY = os.environ.get("JOYCAPTION_PYTHON", "/workspace/joycaption/venv/bin/python3")
SERVER = os.environ.get("JOYCAPTION_SERVER_PY", "/workspace/joycaption/joycaption_server.py")
PROMPT = os.environ.get("JOYCAPTION_PROMPT", "/workspace/joycaption/joycaption_prompt.json")
PREPEND = os.environ.get("JOYCAPTION_PREPEND", "")


def server_quant_flags() -> list[str]:
    try:
        import torch

        if torch.cuda.is_available():
            vram_gb = torch.cuda.get_device_properties(0).total_memory / (1024**3)
            if vram_gb > 16:
                return []
            return ["--load-in-8bit"]
    except Exception:
        pass
    return ["--load-in-8bit"]


class JoyCaptionServer:
    def __init__(self) -> None:
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        if self.proc is not None and self.proc.poll() is None:
            return
        quant = server_quant_flags()
        cmd = [PY, SERVER, "--prompt-file", PROMPT, *quant]
        self.proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=sys.stderr,
            text=True,
            bufsize=1,
        )
        assert self.proc.stdout is not None
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("JoyCaption server exited before ready")
        data = json.loads(line)
        if not data.get("ready"):
            raise RuntimeError(f"JoyCaption server failed to start: {line.strip()}")

    def caption(self, image_path: Path, prepend: str) -> str:
        self.start()
        assert self.proc and self.proc.stdin and self.proc.stdout
        req = {"cmd": "caption", "path": str(image_path), "prepend": prepend, "append": ""}
        self.proc.stdin.write(json.dumps(req) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            self.proc = None
            raise RuntimeError("JoyCaption server died during caption")
        data = json.loads(line)
        if "error" in data:
            raise RuntimeError(data["error"])
        return str(data.get("caption", "")).strip()


_SERVER: JoyCaptionServer | None = None


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: joycaption_cli.py IMAGE_PATH", file=sys.stderr)
        return 2
    path = Path(sys.argv[1])
    if not path.is_file():
        print(f"file not found: {path}", file=sys.stderr)
        return 1
    global _SERVER
    if _SERVER is None:
        _SERVER = JoyCaptionServer()
    try:
        text = _SERVER.caption(path, PREPEND)
    except Exception as ex:
        print(str(ex), file=sys.stderr)
        return 1
    if not text:
        print("empty caption", file=sys.stderr)
        return 1
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
