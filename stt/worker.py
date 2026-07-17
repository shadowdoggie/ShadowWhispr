"""Persistent JSONL worker for local Parakeet v3 transcription.

Protocol (one JSON object per line):
  stdout: {"type":"ready","ready":true,...}
  stdin:  {"id":<value>,"audio_path":"C:\\path\\to\\recording.wav"}
  stdout: {"id":<value>,"text":"...","error":null}

Diagnostics go to stderr so stdout remains safe for machine-readable messages.
The worker reads audio directly from the supplied path and never saves a copy.
"""

from __future__ import annotations

import json
import math
import os
import sys
from pathlib import Path
from typing import Any

# The setup script pre-downloads the pinned model revision, so the worker
# loads purely from the local cache. Must be set before importing
# transformers/huggingface_hub; override with HF_HUB_OFFLINE=0 if needed.
os.environ.setdefault("HF_HUB_OFFLINE", "1")

import numpy as np
import soundfile as sf
import torch
from scipy.signal import resample_poly
from transformers import AutoModelForTDT, AutoProcessor


MODEL_ID = "nvidia/parakeet-tdt-0.6b-v3"
# Exact model revision verified against the pinned transformers commit, so an
# upstream repo update can never break installed apps.
MODEL_REVISION = "7c35754d166cca382ad1e53e68b01e7c575f3a1d"


def emit(message: dict[str, Any]) -> None:
    """Write exactly one protocol message and make it immediately visible."""
    sys.stdout.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def load_audio(audio_path: Path, target_rate: int) -> np.ndarray:
    """Load an audio file as mono float32 and resample when necessary."""
    if not audio_path.is_file():
        raise FileNotFoundError(f"Audio file does not exist: {audio_path}")

    audio, sample_rate = sf.read(audio_path, dtype="float32", always_2d=True)
    if audio.shape[0] == 0:
        raise ValueError("Audio file is empty")

    # Average channels rather than silently using only the left channel.
    mono = audio.mean(axis=1, dtype=np.float32)
    del audio

    if sample_rate != target_rate:
        divisor = math.gcd(sample_rate, target_rate)
        mono = resample_poly(
            mono,
            up=target_rate // divisor,
            down=sample_rate // divisor,
        ).astype(np.float32, copy=False)

    return np.ascontiguousarray(mono, dtype=np.float32)


def decode_text(processor: Any, sequences: Any) -> str:
    """Normalize the processor's single-item decode result to plain text."""
    decoded = processor.decode(sequences, skip_special_tokens=True)
    if isinstance(decoded, (list, tuple)):
        decoded = decoded[0] if decoded else ""
    if not isinstance(decoded, str):
        decoded = str(decoded)
    return decoded.strip()


def transcribe(model: Any, processor: Any, audio_path: Path) -> str:
    target_rate = int(processor.feature_extractor.sampling_rate)
    waveform = load_audio(audio_path, target_rate)
    inputs = None
    output = None

    try:
        inputs = processor(
            audio=waveform,
            sampling_rate=target_rate,
            return_tensors="pt",
        )
        inputs = inputs.to(device=model.device, dtype=model.dtype)

        with torch.inference_mode():
            output = model.generate(**inputs, return_dict_in_generate=True)

        return decode_text(processor, output.sequences)
    finally:
        # Do not keep the recording or its derived tensors between requests.
        waveform.fill(0)
        del waveform, inputs, output


def main() -> int:
    try:
        if not torch.cuda.is_available():
            raise RuntimeError("An NVIDIA GPU with a working CUDA PyTorch install is required")

        processor = AutoProcessor.from_pretrained(MODEL_ID, revision=MODEL_REVISION)
        model = AutoModelForTDT.from_pretrained(MODEL_ID, revision=MODEL_REVISION, dtype=torch.float16)
        model.to("cuda")
        model.eval()
    except Exception as exc:
        emit({"type": "ready", "ready": False, "error": str(exc)})
        return 1

    emit(
        {
            "type": "ready",
            "ready": True,
            "model": MODEL_ID,
            "device": torch.cuda.get_device_name(0),
            "error": None,
        }
    )

    for raw_line in sys.stdin:
        if not raw_line.strip():
            continue

        request_id: Any = None
        try:
            request = json.loads(raw_line)
            if not isinstance(request, dict):
                raise ValueError("Request must be a JSON object")
            if "id" not in request:
                raise ValueError("Request is missing 'id'")
            request_id = request["id"]

            audio_path_value = request.get("audio_path")
            if not isinstance(audio_path_value, str) or not audio_path_value.strip():
                raise ValueError("Request is missing a valid 'audio_path'")

            text = transcribe(model, processor, Path(audio_path_value))
            emit({"id": request_id, "text": text, "error": None})
        except Exception as exc:
            print(f"Transcription failed: {exc}", file=sys.stderr, flush=True)
            emit({"id": request_id, "text": "", "error": str(exc)})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
