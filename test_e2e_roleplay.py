#!/usr/bin/env python3
"""Local end-to-end: EventForge + wrath worker + optional LoboForge API check-in."""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
import uuid
import urllib.error
import urllib.request

try:
    import aiohttp
except ImportError:
    print("pip install aiohttp", file=sys.stderr)
    raise


def http_json(method: str, url: str, body: dict | None, token: str | None = None, timeout: int = 30):
    data = None if body is None else json.dumps(body).encode("utf-8")
    headers = {"Content-Type": "application/json", "Accept": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    req = urllib.request.Request(url, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read()
            if resp.status == 204 or not raw:
                return resp.status, None
            return resp.status, json.loads(raw.decode("utf-8"))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {e.code} {url}: {raw}") from e


async def ws_collect_stream(ws_url: str, api_key: str, job_id: str, timeout: float) -> list[str]:
    import websockets

    url = f"{ws_url}?token={api_key}" if "?" not in ws_url else f"{ws_url}&token={api_key}"
    tokens: list[str] = []
    done = asyncio.Event()

    async def handle(ev: dict) -> None:
        nonlocal tokens
        if ev.get("type") == "forge.stream.token" and ev.get("job_id") == job_id:
            delta = ev.get("delta") or ""
            if delta:
                tokens.append(delta)
        if ev.get("type") == "forge.stream.done" and ev.get("job_id") == job_id:
            done.set()
        if ev.get("type") == "forge.job.completed" and ev.get("job_id") == job_id:
            done.set()

    async def reader(ws):
        async for msg in ws:
            try:
                ev = json.loads(msg)
            except json.JSONDecodeError:
                continue
            t = ev.get("type", "")
            if t == "replay.batch":
                for item in ev.get("events") or []:
                    await handle(item)
                continue
            await handle(ev)

    async with websockets.connect(url) as ws:
        await ws.send(json.dumps({"type": "hello", "protocol": 1}))
        await ws.send(json.dumps({
            "type": "subscribe",
            "events": ["forge.stream.token", "forge.stream.done", "forge.job.completed", "forge.job.failed"],
        }))
        await ws.send(json.dumps({"type": "replay", "since": "1970-01-01T00:00:00Z"}))
        task = asyncio.create_task(reader(ws))
        try:
            await asyncio.wait_for(done.wait(), timeout=timeout)
        finally:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass
    return tokens


async def api_check_in(api_base: str, secret: str) -> None:
    payload = {
        "node_uuid": str(uuid.uuid4()),
        "hostname": "e2e-test",
        "agent_type": "ollama",
        "ollama_url": "http://127.0.0.1:11434",
        "ollama_models": ["dolphin3:8b"],
        "busy": False,
        "comfy_ok": True,
    }
    async with aiohttp.ClientSession() as session:
        async with session.post(
            f"{api_base.rstrip('/')}/api/agent/check-in",
            json=payload,
            headers={"Authorization": f"Bearer {secret}", "Content-Type": "application/json"},
            timeout=aiohttp.ClientTimeout(total=15),
        ) as r:
            body = await r.text()
            if r.status != 200:
                raise RuntimeError(f"API check-in failed {r.status}: {body[:200]}")
            print(f"API check-in OK: {body[:120]}")


async def main_async(args: argparse.Namespace) -> int:
    ef = args.ef.rstrip("/")
    for path in ("/health", "/healthws"):
        status, body = http_json("GET", f"{ef}{path}", None)
        if status != 200 or not (body or {}).get("ok"):
            print(f"FAIL: {path} unhealthy: {body}")
            return 1
    print("EventForge health OK")

    if args.api_base and args.worker_secret:
        try:
            await api_check_in(args.api_base, args.worker_secret)
        except Exception as ex:
            print(f"WARN: API check-in skipped/failed: {ex}")

    job_id = uuid.uuid4().hex
    payload = {
        "type": "chat_request",
        "request_id": job_id,
        "model": "dolphin3:8b",
        "messages": [{"role": "user", "content": "Say hello in one short sentence."}],
        "temperature": 0.7,
        "num_predict": 64,
        "num_ctx": 4096,
    }
    status, body = http_json(
        "POST",
        f"{ef}/v1/jobs",
        {
            "job_id": job_id,
            "capability": args.capability,
            "tier": args.tier,
            "kind": "text_stream",
            "payload": payload,
        },
        token=args.app_key,
    )
    if status != 200:
        print(f"FAIL: create job: {body}")
        return 1
    print(f"Enqueued job {job_id}")

    try:
        import websockets  # noqa: F401
    except ImportError:
        print("pip install websockets for stream verification")
        return 1

    tokens = await ws_collect_stream(args.ws, args.app_key, job_id, args.timeout)
    text = "".join(tokens)
    print(f"Stream received {len(tokens)} chunk(s), {len(text)} chars")
    print(f"Text preview: {text[:200]!r}")

    if len(text.strip()) < 3:
        print("FAIL: empty or too-short stream")
        return 1

    print("PASS: EventForge roleplay E2E")
    return 0


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ef", default="http://localhost:8090")
    ap.add_argument("--ws", default="ws://localhost:8090/v1/ws")
    ap.add_argument("--app-key", default="loboforge-local-key")
    ap.add_argument("--capability", default="ollama-chat")
    ap.add_argument("--tier", default="normal")
    ap.add_argument("--timeout", type=float, default=90.0)
    ap.add_argument("--api-base", default="http://localhost:5250")
    ap.add_argument("--worker-secret", default="dev-local-placeholder")
    args = ap.parse_args()
    raise SystemExit(asyncio.run(main_async(args)))


if __name__ == "__main__":
    main()
