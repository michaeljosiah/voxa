#!/usr/bin/env python3
"""Voxa smart-turn sidecar.

Runs the real Pipecat smart-turn model (default pipecat-ai/smart-turn-v3) so the turn-end
preprocessing — Whisper feature extraction — happens natively in Python, with no fragile C#
reimplementation. Speaks the tiny Voxa smart-turn stdio protocol:

    Request : one JSON header line  {"sample_rate": N, "bytes": M}\n   then M bytes of 16-bit mono PCM
    Response: one JSON line         {"probability": x}                 (0..1)  — or {"error": "..."}

stdout is the protocol channel (binary); all logging goes to stderr. If the model or its deps
(transformers, onnxruntime, huggingface_hub) are unavailable, every request answers
{"probability": 1.0} (always "complete" = classic silence behavior) and the reason is logged once,
so a missing dependency degrades gracefully instead of stranding turns.

Install:  pip install onnxruntime transformers huggingface_hub numpy
"""
import argparse
import json
import sys


def log(*args):
    print("voxa-smart-turn:", *args, file=sys.stderr, flush=True)


def load_model(model_id):
    """Return (feature_extractor, onnx_session) or (None, None) if anything is unavailable."""
    try:
        import numpy as np  # noqa: F401
        import onnxruntime as ort
        from huggingface_hub import hf_hub_download, list_repo_files
        from transformers import WhisperFeatureExtractor

        onnx_files = [f for f in list_repo_files(model_id) if f.endswith(".onnx")]
        if not onnx_files:
            raise RuntimeError(f"no .onnx file in {model_id}")
        # Prefer a quantized (int8) export for fast CPU inference if one is published.
        onnx_files.sort(key=lambda f: ("int8" not in f.lower(), f))
        onnx_path = hf_hub_download(model_id, onnx_files[0])
        extractor = WhisperFeatureExtractor.from_pretrained(model_id)
        session = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        log(f"loaded {model_id} ({onnx_files[0]})")
        return extractor, session
    except Exception as exc:  # noqa: BLE001 — degrade gracefully on any load failure
        log("model unavailable, degrading to always-complete:", exc)
        return None, None


def predict(extractor, session, audio_f32):
    """Match pipecat smart-turn-v3 inference: last 8 s, Whisper features (1,80,3000), sigmoid prob."""
    import numpy as np

    n = 8 * 16000
    if len(audio_f32) > n:
        audio_f32 = audio_f32[-n:]
    features = extractor(
        audio_f32,
        sampling_rate=16000,
        return_tensors="np",
        padding="max_length",
        max_length=n,
        do_normalize=True,
    )
    inputs = {"input_features": features["input_features"].astype(np.float32)}
    outputs = session.run(None, inputs)
    return float(np.asarray(outputs[0]).reshape(-1)[0])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="pipecat-ai/smart-turn-v3")
    args = parser.parse_args()

    extractor, session = load_model(args.model)
    stdin, stdout = sys.stdin.buffer, sys.stdout.buffer

    while True:
        header = stdin.readline()
        if not header:
            break  # Voxa closed stdin — exit
        try:
            request = json.loads(header)
            n_bytes = int(request["bytes"])
            sample_rate = int(request.get("sample_rate", 16000))
            pcm = stdin.read(n_bytes)
            if len(pcm) < n_bytes:
                break  # truncated — the parent went away

            if extractor is None:
                response = {"probability": 1.0}
            else:
                import numpy as np

                if sample_rate != 16000:
                    log(f"expected 16 kHz audio, got {sample_rate} — feeding as-is")
                audio = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
                response = {"probability": predict(extractor, session, audio)}
        except Exception as exc:  # noqa: BLE001 — never crash the loop; fail "complete"
            log("request failed:", exc)
            response = {"probability": 1.0}

        stdout.write((json.dumps(response) + "\n").encode("utf-8"))
        stdout.flush()


if __name__ == "__main__":
    main()
