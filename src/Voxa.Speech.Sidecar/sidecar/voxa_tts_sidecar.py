#!/usr/bin/env python3
"""Voxa TTS sidecar (VVL-002).

Speaks the Voxa stdio protocol so the .NET SidecarTtsEngine can drive it out-of-process:

  request  (stdin) : one JSON line, e.g. {"text": "hello", "voice": "default", "language": "en",
                     "sample_rate": 24000, "mode": "speak"}
  response (stdout): one JSON header line  {"sample_rate": N}  (or {"error": "..."}),
                     then length-prefixed PCM16 frames  [uint32 LE length][bytes],
                     ended by a zero-length frame.

Designed to be frozen per-platform with PyInstaller and pinned in the Voxa model cache; until then,
run it directly with Python (Voxa:Sidecar:PythonScript). Real synthesis uses XTTS-v2 (cloning +
expressive, multilingual) via Coqui TTS when installed; pass --debug-tone (or run with TTS missing)
to emit a sine wave instead, so the wire protocol is exercisable with no model installed.
"""
import argparse
import json
import math
import os
import struct
import sys


def _binary_stdout():
    # On Windows, stdout defaults to text mode and would translate 0x0A bytes in the PCM stream into
    # 0x0D 0x0A, corrupting the audio. Force binary mode so frames pass through untouched.
    if sys.platform == "win32":
        import msvcrt
        msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)
    return sys.stdout.buffer


OUT = _binary_stdout()


def write_header(sample_rate=None, error=None):
    obj = {"error": error} if error else {"sample_rate": sample_rate}
    OUT.write((json.dumps(obj) + "\n").encode("utf-8"))
    OUT.flush()


def write_frame(pcm: bytes):
    OUT.write(struct.pack("<I", len(pcm)))
    OUT.write(pcm)
    OUT.flush()


def end_utterance():
    OUT.write(struct.pack("<I", 0))
    OUT.flush()


def tone_pcm(text: str, sample_rate: int) -> bytes:
    # A deterministic stand-in for real synthesis: a 220 Hz sine whose length scales with the text.
    seconds = max(0.3, min(5.0, len(text) * 0.06))
    out = bytearray()
    for i in range(int(seconds * sample_rate)):
        out += struct.pack("<h", int(0.2 * 32767 * math.sin(2 * math.pi * 220 * i / sample_rate)))
    return bytes(out)


class XttsEngine:
    """XTTS-v2 via Coqui TTS — zero-shot cloning (speaker_wav) + expressive multilingual synthesis."""

    def __init__(self):
        from TTS.api import TTS  # Coqui TTS; bundled into the frozen binary
        self.tts = TTS(model_name="tts_models/multilingual/multi-dataset/xtts_v2")
        self.sample_rate = 24000

    def synthesize(self, text: str, voice: str, language: str) -> bytes:
        import numpy as np
        speaker_wav = voice if voice and voice not in ("", "default") else None
        wav = self.tts.tts(text=text, language=language or "en", speaker_wav=speaker_wav)
        return (np.clip(np.asarray(wav, dtype="float32"), -1.0, 1.0) * 32767.0).astype("<i2").tobytes()


def main() -> int:
    parser = argparse.ArgumentParser(description="Voxa TTS sidecar")
    parser.add_argument("--model", default="xtts-v2")
    parser.add_argument("--sample-rate", type=int, default=24000)
    parser.add_argument("--debug-tone", action="store_true", help="emit a sine wave instead of real TTS")
    args = parser.parse_args()

    engine = None
    if not args.debug_tone:
        try:
            engine = XttsEngine()
        except Exception as exc:  # model/deps missing → fall back so the protocol still works
            sys.stderr.write(f"voxa sidecar: falling back to debug tone ({exc})\n")
            sys.stderr.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            text = req.get("text", "")
            sample_rate = int(req.get("sample_rate") or args.sample_rate)
            if engine is not None:
                pcm = engine.synthesize(text, req.get("voice"), req.get("language"))
                sample_rate = engine.sample_rate
            else:
                pcm = tone_pcm(text, sample_rate)

            write_header(sample_rate=sample_rate)
            frame = max(2, int(sample_rate * 0.02) * 2)  # ~20 ms PCM16 frames for streaming
            for off in range(0, len(pcm), frame):
                write_frame(pcm[off:off + frame])
            end_utterance()
        except Exception as exc:
            write_header(error=str(exc))
            end_utterance()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
