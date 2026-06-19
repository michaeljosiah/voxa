#!/usr/bin/env python3
"""Voxa smart-turn sidecar.

Runs the real smart-turn ONNX model (default pipecat-ai/smart-turn-v3) so the turn-end preprocessing —
Whisper log-mel feature extraction — happens natively in Python, with no fragile C# reimplementation.
Speaks the tiny Voxa smart-turn stdio protocol over stdout (binary); all logging goes to stderr:

    Startup : one JSON line        {"ready": true}                              (after the model loads)
    Request : one JSON header line {"sample_rate": N, "bytes": M}\n  then M bytes of 16-bit mono PCM
    Response: one JSON line        {"probability": x}              (0..1)  — or {"error": "..."}

If the model or its deps (transformers, onnxruntime, huggingface_hub) are unavailable, the sidecar still
signals ready and answers {"probability": 1.0} (always "complete" = classic silence behavior), logging the
reason — so a missing dependency degrades gracefully instead of stranding turns.

Install:  pip install onnxruntime transformers huggingface_hub numpy
"""
import argparse
import json
import sys


def log(*args):
    print("voxa-smart-turn:", *args, file=sys.stderr, flush=True)


def load_model(model_id):
    """Return (feature_extractor, onnx_session, input_name) or (None, None, None) if anything is unavailable."""
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

        # Build the Whisper log-mel extractor LOCALLY: the smart-turn weights repo ships only the ONNX
        # (no preprocessor_config), so from_pretrained(model_id) would raise. smart-turn-v3 uses Whisper
        # features over an 8-second context.
        extractor = WhisperFeatureExtractor(chunk_length=8)
        session = ort.InferenceSession(onnx_path, providers=["CPUExecutionProvider"])
        input_name = session.get_inputs()[0].name
        log(f"loaded {model_id} ({onnx_files[0]}); input {input_name} {session.get_inputs()[0].shape}")
        return extractor, session, input_name
    except Exception as exc:  # noqa: BLE001 — degrade gracefully on any load failure
        log("model unavailable, degrading to always-complete:", exc)
        return None, None, None


def resample_to_16k(audio_f32, src_rate):
    """Linear resample to 16 kHz (smart-turn-v3's rate). Voxa may run the VAD at 8 kHz; this is coarse and
    dependency-free, but far better than feeding off-rate PCM as if it were 16 kHz (which shifts the
    apparent duration and pitch and makes the verdict unreliable)."""
    import numpy as np

    if src_rate == 16000 or len(audio_f32) == 0:
        return audio_f32
    n_out = max(1, round(len(audio_f32) * 16000 / src_rate))
    x_old = np.linspace(0.0, 1.0, num=len(audio_f32), endpoint=False)
    x_new = np.linspace(0.0, 1.0, num=n_out, endpoint=False)
    return np.interp(x_new, x_old, audio_f32).astype(np.float32)


def predict(extractor, session, input_name, audio_f32):
    """Match smart-turn-v3 inference: last 8 s, Whisper features (1, 80, 800), sigmoid probability."""
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
        truncation=True,
        do_normalize=True,
    )
    outputs = session.run(None, {input_name: features["input_features"].astype(np.float32)})
    return float(np.asarray(outputs[0]).reshape(-1)[0])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="pipecat-ai/smart-turn-v3")
    args = parser.parse_args()

    extractor, session, input_name = load_model(args.model)
    stdin, stdout = sys.stdin.buffer, sys.stdout.buffer

    # Signal readiness once the (slow, one-time) model load is done, so the host can bound startup
    # separately from per-turn inference.
    stdout.write(b'{"ready": true}\n')
    stdout.flush()

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

                audio = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
                if sample_rate != 16000:
                    audio = resample_to_16k(audio, sample_rate)
                response = {"probability": predict(extractor, session, input_name, audio)}
        except Exception as exc:  # noqa: BLE001 — never crash the loop; fail "complete"
            log("request failed:", exc)
            response = {"probability": 1.0}

        stdout.write((json.dumps(response) + "\n").encode("utf-8"))
        stdout.flush()


if __name__ == "__main__":
    main()
