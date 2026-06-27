#!/usr/bin/env python3
"""
A tiny dev stand-in for a vLLM Voxtral realtime server (VLS-009).

It speaks the same OpenAI-Realtime-style `/v1/realtime` envelopes the real server does, but runs no model and
needs no GPU: it accepts audio, and on `input_audio_buffer.commit` it replays a canned transcript as
`transcription.delta` frames followed by a `transcription.done`. Point Voxa at it with
`"Voxa:Voxtral:ServerUrl": "ws://127.0.0.1:8000"` to smoke-test the pipeline end-to-end offline.

    pip install websockets
    python voxtral_realtime_mock.py --port 8000 --transcript "the quick brown fox"

This is a DEVELOPMENT TOOL ONLY — it does not transcribe; it echoes the fixed transcript regardless of audio.
"""
import argparse
import asyncio
import json

import websockets


async def handle(ws, transcript: str):
    # Greet, like the real server. The client ignores unknown types.
    await ws.send(json.dumps({"type": "session.created", "id": "mock"}))
    words = transcript.split()
    async for message in ws:
        try:
            event = json.loads(message)
        except (ValueError, TypeError):
            continue  # ignore malformed frames, like the real engine does
        kind = event.get("type")
        if kind == "input_audio_buffer.commit":
            # Stream the transcript incrementally, then finalize — mirrors delta… then done.
            running = ""
            for word in words:
                running = (running + " " + word).strip()
                await ws.send(json.dumps({"type": "transcription.delta", "delta": (" " + word) if running != word else word}))
                await asyncio.sleep(0.02)
            await ws.send(json.dumps({"type": "transcription.done", "text": transcript}))
        # input_audio_buffer.append / session.update are accepted and ignored (no real decoding).


async def main():
    parser = argparse.ArgumentParser(description="Mock vLLM Voxtral realtime server (dev only).")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--transcript", default="the quick brown fox",
                        help="The canned transcript echoed on every commit.")
    args = parser.parse_args()

    async def handler(ws):
        await handle(ws, args.transcript)

    # The engine connects to ws://host:port/v1/realtime; this server ignores the path and accepts any.
    async with websockets.serve(handler, args.host, args.port):
        print(f"mock voxtral realtime server on ws://{args.host}:{args.port}/v1/realtime "
              f"(transcript: {args.transcript!r}) — Ctrl+C to stop")
        await asyncio.Future()  # run forever


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
