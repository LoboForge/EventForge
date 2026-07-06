#!/usr/bin/env python3
"""Minimal EventForge worker for local wrath testing (image + text_stream)."""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DEFAULT_EF = "http://localhost:8090"
DEFAULT_WORKER_KEY = "wrath-worker-key"


def http_json(method: str, url: str, body: dict | None, token: str, timeout: int = 120):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            if resp.status == 204 or not raw:
                return resp.status, None
            return resp.status, json.loads(raw.decode("utf-8"))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {e.code} {url}: {raw}") from e


def http_put_stream(url: str, path: Path, content_type: str, token: str):
    data = path.read_bytes()
    req = urllib.request.Request(
        url,
        data=data,
        method="PUT",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": content_type,
        },
    )
    with urllib.request.urlopen(req, timeout=300) as resp:
        return resp.status, json.loads(resp.read().decode("utf-8"))


def claim(ef: str, capability: str, tier: str, token: str):
    status, body = http_json(
        "POST",
        f"{ef}/v1/jobs/claim",
        {"capability": capability, "tier": tier, "worker_id": "wrath"},
        token,
    )
    if status == 204 or body is None:
        return None
    return body


def run_text_job(ef: str, job: dict, token: str):
    job_id = job["job_id"]
    payload = job.get("payload") or {}
    prompt = payload.get("prompt") or payload.get("messages") or "Hello"
    reply = f"EventForge roleplay reply to: {prompt}" if isinstance(prompt, str) else "EventForge roleplay reply."
    for word in reply.split(" "):
        http_json(
            "POST",
            f"{ef}/v1/jobs/{job_id}/stream",
            {"delta": word + " "},
            token,
        )
        time.sleep(0.05)
    http_json("POST", f"{ef}/v1/jobs/{job_id}/complete", {"text": reply}, token)
    print(f"completed text_stream job {job_id}")


def run_image_job(ef: str, job: dict, token: str, output_file: Path | None):
    job_id = job["job_id"]
    # Demo: write tiny PNG-like bytes if no real output
    if output_file and output_file.exists():
        src = output_file
        ct = "image/png"
    else:
        demo = Path("/tmp/ef-demo-output.bin")
        demo.write_bytes(b"EF_DEMO_OUTPUT")
        src = demo
        ct = "application/octet-stream"
    http_put_stream(
        f"{ef}/v1/jobs/{job_id}/output?file={src.name}",
        src,
        ct,
        token,
    )
    http_json("POST", f"{ef}/v1/jobs/{job_id}/complete", {}, token)
    print(f"completed image job {job_id}")


def main():
    ap = argparse.ArgumentParser(description="EventForge local worker")
    ap.add_argument("--ef", default=DEFAULT_EF)
    ap.add_argument("--token", default=DEFAULT_WORKER_KEY)
    ap.add_argument("--capability", default="ollama-chat")
    ap.add_argument("--tier", default="bulk")
    ap.add_argument("--poll-seconds", type=float, default=2.0)
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--demo-output", type=Path, default=None)
    args = ap.parse_args()

    while True:
        job = claim(args.ef, args.capability, args.tier, args.token)
        if not job:
            if args.once:
                print("no job")
                return 0
            time.sleep(args.poll_seconds)
            continue
        kind = job.get("kind") or "image"
        print(f"claimed {job['job_id']} kind={kind} app={job.get('app_id')}")
        try:
            if kind == "text_stream":
                run_text_job(args.ef, job, args.token)
            else:
                run_image_job(args.ef, job, args.token, args.demo_output)
        except Exception as ex:
            http_json(
                "POST",
                f"{args.ef}/v1/jobs/{job['job_id']}/fail",
                {"error": str(ex)},
                args.token,
            )
            print(f"failed {job['job_id']}: {ex}", file=sys.stderr)
        if args.once:
            return 0


if __name__ == "__main__":
    raise SystemExit(main())
