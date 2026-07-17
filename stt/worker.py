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
import sys
import time
from pathlib import Path
from typing import Any

import numpy as np
import soundfile as sf
import torch
from scipy.signal import resample_poly
from transformers import AutoModelForTDT, AutoProcessor


# The setup script downloads the pinned model revision into this plain folder
# of real files. Loading from a local directory involves no Hugging Face cache,
# no symlinks, and no network - the failure modes that broke installed apps.
MODEL_DIR = Path(__file__).resolve().parent.parent / "speech-model"
REQUIRED_MODEL_FILES = ("config.json", "model.safetensors", "processor_config.json", "tokenizer.json")


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


def model_files_present() -> bool:
    return all((MODEL_DIR / name).is_file() for name in REQUIRED_MODEL_FILES)


def load_speech_stack() -> tuple[Any, Any]:
    """Load processor and model from the plain local model folder.

    Freshly written files can be briefly unreadable (e.g. an antivirus scanning
    a large download), so the load is retried before giving up.
    """
    last_error: Exception = RuntimeError("The speech model could not be loaded.")
    for attempt in range(3):
        if attempt:
            time.sleep(5)
        try:
            processor = AutoProcessor.from_pretrained(str(MODEL_DIR))
            model = AutoModelForTDT.from_pretrained(str(MODEL_DIR), dtype=torch.float16)
            return processor, model
        except Exception as exc:
            last_error = exc
            print(
                f"Model load failed (attempt {attempt + 1}/3): {exc}",
                file=sys.stderr,
                flush=True,
            )
    raise last_error


def main() -> int:
    if not torch.cuda.is_available():
        # Setup cannot fix a missing GPU, so do not send the user back there.
        emit(
            {
                "type": "ready",
                "ready": False,
                "error": "An NVIDIA GPU with a working CUDA PyTorch install is required",
                "setup_required": False,
            }
        )
        return 1

    try:
        if not model_files_present():
            # setup_required makes the app reopen the one-time setup panel,
            # which re-downloads the model folder.
            emit(
                {
                    "type": "ready",
                    "ready": False,
                    "error": f"The speech model files are missing from {MODEL_DIR}.",
                    "setup_required": True,
                }
            )
            return 1

        processor, model = load_speech_stack()
        model.to("cuda")
        model.eval()
    except Exception as exc:
        # A load failure with the files present usually means an interrupted
        # download left them corrupt; re-running setup overwrites them.
        emit(
            {
                "type": "ready",
                "ready": False,
                "error": str(exc),
                "setup_required": True,
            }
        )
        return 1

    emit(
        {
            "type": "ready",
            "ready": True,
            "model": str(MODEL_DIR),
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
