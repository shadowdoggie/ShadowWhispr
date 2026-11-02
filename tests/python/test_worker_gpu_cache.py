"""Regression tests for the speech worker's CUDA cache release policy.

`transcribe()` must return the CUDA memory the caching allocator keeps after a
long recording, while leaving the loaded model resident and the JSONL protocol
untouched. Running the real worker needs CUDA, torch, transformers and a 2.4 GB
model folder, so its imports are replaced with small fakes before worker.py is
loaded; no GPU, model or third-party package is involved.

Run from the repository root:

    python -m unittest discover -s tests/python -v
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import sys
import types
import unittest
from pathlib import Path
from unittest import mock

WORKER_PATH = Path(__file__).resolve().parents[2] / "stt" / "worker.py"

MiB = 1024 * 1024

# Figures from the reproduced leak: a loaded model holds ~1.2 GiB that must stay
# allocated, an 870 s recording leaves ~10 GiB of unused cache behind.
MODEL_ALLOCATED = 1206 * MiB
LONG_RECORDING_PEAK = 10000 * MiB
SHORT_REQUEST_PEAK = 42 * MiB
# A failing frame kept this much alive through transcribe's finally in the
# reproduced failure-path leak.
FAULT_TENSOR = 800 * MiB


class FakeCuda:
    """Stands in for torch.cuda and models the caching allocator's two counters."""

    def __init__(self) -> None:
        self.available = True
        self.allocated = MODEL_ALLOCATED
        self.reserved = MODEL_ALLOCATED
        self.empty_cache_calls = 0
        self.empty_cache_error: Exception | None = None

    def is_available(self) -> bool:
        return self.available

    def memory_allocated(self) -> int:
        return self.allocated

    def memory_reserved(self) -> int:
        return self.reserved

    def get_device_name(self, index: int) -> str:
        return "Fake RTX 5060 Ti"

    def empty_cache(self) -> None:
        self.empty_cache_calls += 1
        if self.empty_cache_error is not None:
            raise self.empty_cache_error
        # What torch does: drop every cached block the allocator holds.
        self.reserved = self.allocated

    def allocate(self, size: int) -> "FakeTensor":
        """Allocate like torch: reserved rises to the new high-water mark."""
        self.allocated += size
        self.reserved = max(self.reserved, self.allocated)
        return FakeTensor(self, size)

    def release(self, size: int) -> None:
        """Free like torch: the block stays cached, only `allocated` drops."""
        self.allocated -= size


class FakeTensor:
    """GPU allocation that stays live for exactly as long as it is referenced.

    A failing frame holds one of these in a local, so the tensor is freed only
    when the traceback of the raised exception is destroyed - the lifecycle the
    failure-path leak depended on.
    """

    def __init__(self, cuda: FakeCuda, size: int) -> None:
        self.cuda = cuda
        self.size = size
        self.released = False

    def __del__(self) -> None:
        if not self.released:
            self.released = True
            self.cuda.release(self.size)


def install_stub_modules() -> tuple[types.ModuleType, dict[str, types.ModuleType]]:
    """Build the fake imports worker.py needs, all inert by themselves."""
    torch = types.ModuleType("torch")
    torch.cuda = FakeCuda()
    torch.float16 = "float16"
    torch.inference_mode = contextlib.nullcontext

    numpy = types.ModuleType("numpy")
    numpy.float32 = "float32"
    numpy.ascontiguousarray = lambda audio, dtype=None: audio
    numpy.ndarray = object

    soundfile = types.ModuleType("soundfile")
    soundfile.read = mock.Mock(side_effect=AssertionError("tests stub load_audio"))

    signal = types.ModuleType("scipy.signal")
    signal.resample_poly = mock.Mock(side_effect=AssertionError("tests stub load_audio"))
    scipy = types.ModuleType("scipy")
    scipy.signal = signal

    transformers = types.ModuleType("transformers")
    transformers.AutoModelForTDT = object
    transformers.AutoProcessor = object

    stubs = {
        "numpy": numpy,
        "soundfile": soundfile,
        "scipy": scipy,
        "scipy.signal": signal,
        "torch": torch,
        "transformers": transformers,
    }
    return torch, stubs


def load_worker() -> tuple[types.ModuleType, FakeCuda]:
    """Import stt/worker.py against the fakes and return it with its fake CUDA."""
    torch, stubs = install_stub_modules()
    with mock.patch.dict(sys.modules, stubs):
        spec = importlib.util.spec_from_file_location("shadowwhispr_worker", WORKER_PATH)
        assert spec is not None and spec.loader is not None
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)
    cuda = torch.cuda
    assert isinstance(cuda, FakeCuda)
    return worker, cuda


class FakeWaveform:
    """Host-side sample buffer the worker wipes once it is done with it."""

    def __init__(self) -> None:
        self.fill_values: list[object] = []

    def fill(self, value: object) -> None:
        self.fill_values.append(value)

    @property
    def zeroed(self) -> bool:
        return bool(self.fill_values) and all(value == 0 for value in self.fill_values)


class FakeBatch:
    """Processor output: a mapping that can be moved to the model's device."""

    def __init__(self, **values: object) -> None:
        self.values = values

    def to(self, device: object = None, dtype: object = None) -> "FakeBatch":
        return self

    def keys(self):
        return self.values.keys()

    def __getitem__(self, key: str) -> object:
        return self.values[key]


class FakeProcessor:
    def __init__(self, text: str = "hello there") -> None:
        self.feature_extractor = types.SimpleNamespace(sampling_rate=16000)
        self.text = text
        self.decode_calls = 0

    def __call__(self, audio: object, sampling_rate: int, return_tensors: str) -> FakeBatch:
        return FakeBatch(input_features=audio)

    def decode(self, sequences: object, skip_special_tokens: bool = True) -> str:
        self.decode_calls += 1
        return self.text


class FailingProcessor(FakeProcessor):
    def __call__(self, audio: object, sampling_rate: int, return_tensors: str) -> FakeBatch:
        raise RuntimeError("bad audio")


class FaultInjectingProcessor(FakeProcessor):
    """Fails like a corrupt input while holding a GPU allocation in its frame.

    The allocation is a local of the raising frame, so it stays live exactly as
    long as the exception's traceback references that frame - how a real
    processor or a failing generate call holds its tensors after it fails.
    """

    def __init__(self, cuda: FakeCuda, size: int = FAULT_TENSOR, failures: int = 1) -> None:
        super().__init__()
        self.cuda = cuda
        self.size = size
        self.failures = failures

    def __call__(self, audio: object, sampling_rate: int, return_tensors: str) -> FakeBatch:
        if self.failures == 0:
            return FakeBatch(input_features=audio)
        self.failures -= 1
        temporary = self.cuda.allocate(self.size)  # held by this frame until its traceback dies
        raise ValueError("input processing failed")


class FakeModel:
    """Model that claims `peak` bytes of GPU memory while it generates."""

    device = "cuda"
    dtype = "float16"

    def __init__(self, cuda: FakeCuda, peak: int = SHORT_REQUEST_PEAK, error: Exception | None = None) -> None:
        self.cuda = cuda
        self.peak = peak
        self.error = error
        self.generate_calls = 0

    def to(self, device: str) -> "FakeModel":
        return self

    def eval(self) -> "FakeModel":
        return self

    def generate(self, **inputs: object) -> types.SimpleNamespace:
        self.generate_calls += 1
        # Peak usage the worker's del statements later return to the cache; it is
        # real even when generation fails partway.
        self.cuda.reserved = max(self.cuda.reserved, self.cuda.allocated + self.peak)
        if self.error is not None:
            raise self.error
        return types.SimpleNamespace(sequences=FakeBatch(tokens=[7]))


class ReleaseCachedGpuMemoryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.worker, self.cuda = load_worker()

    def test_returns_excess_cache_from_a_long_recording(self) -> None:
        self.cuda.reserved = MODEL_ALLOCATED + LONG_RECORDING_PEAK

        self.worker.release_cached_gpu_memory()

        self.assertEqual(self.cuda.empty_cache_calls, 1)
        self.assertEqual(self.cuda.reserved, MODEL_ALLOCATED)

    def test_keeps_a_short_request_cache_for_reuse(self) -> None:
        self.cuda.reserved = MODEL_ALLOCATED + SHORT_REQUEST_PEAK

        self.worker.release_cached_gpu_memory()

        self.assertEqual(self.cuda.empty_cache_calls, 0)
        self.assertEqual(self.cuda.reserved, MODEL_ALLOCATED + SHORT_REQUEST_PEAK)

    def test_releases_at_the_threshold(self) -> None:
        self.cuda.reserved = MODEL_ALLOCATED + self.worker.GPU_CACHE_RELEASE_THRESHOLD_BYTES

        self.worker.release_cached_gpu_memory()

        self.assertEqual(self.cuda.empty_cache_calls, 1)

    def test_does_nothing_without_cuda(self) -> None:
        self.cuda.available = False
        self.cuda.reserved = MODEL_ALLOCATED + LONG_RECORDING_PEAK

        self.worker.release_cached_gpu_memory()

        self.assertEqual(self.cuda.empty_cache_calls, 0)

    def test_release_failure_is_reported_but_not_raised(self) -> None:
        self.cuda.reserved = MODEL_ALLOCATED + LONG_RECORDING_PEAK
        self.cuda.empty_cache_error = RuntimeError("driver went away")
        stderr = io.StringIO()

        with contextlib.redirect_stderr(stderr):
            self.worker.release_cached_gpu_memory()

        self.assertIn("GPU memory release failed: driver went away", stderr.getvalue())

    def test_counter_failure_is_reported_but_not_raised(self) -> None:
        self.cuda.memory_reserved = mock.Mock(side_effect=RuntimeError("no device"))
        stderr = io.StringIO()

        with contextlib.redirect_stderr(stderr):
            self.worker.release_cached_gpu_memory()

        self.assertIn("GPU memory release failed: no device", stderr.getvalue())


class TranscribeReleaseTests(unittest.TestCase):
    def setUp(self) -> None:
        self.worker, self.cuda = load_worker()
        self.waveform = FakeWaveform()

    def transcribe(self, model: FakeModel, processor: FakeProcessor | None = None) -> str:
        with mock.patch.object(self.worker, "load_audio", return_value=self.waveform):
            return self.worker.transcribe(model, processor or FakeProcessor(), Path("recording.wav"))

    def test_releases_excess_cache_after_success(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK)

        text = self.transcribe(model)

        self.assertEqual(text, "hello there")
        self.assertEqual(self.cuda.empty_cache_calls, 1)
        self.assertTrue(self.waveform.zeroed)

    def test_keeps_cache_after_a_short_success(self) -> None:
        model = FakeModel(self.cuda)

        self.assertEqual(self.transcribe(model), "hello there")
        self.assertEqual(self.cuda.empty_cache_calls, 0)
        self.assertTrue(self.waveform.zeroed)

    def test_releases_excess_cache_after_model_failure(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK, error=ValueError("generate failed"))

        with self.assertRaises(ValueError):
            self.transcribe(model)

        self.assertEqual(self.cuda.empty_cache_calls, 1)
        self.assertTrue(self.waveform.zeroed)

    def test_releases_excess_cache_after_decode_failure(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK)

        with mock.patch.object(self.worker, "decode_text", side_effect=RuntimeError("decode failed")):
            with self.assertRaises(RuntimeError):
                self.transcribe(model)

        self.assertEqual(self.cuda.empty_cache_calls, 1)

    def test_releases_excess_cache_after_audio_failure(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK)
        self.cuda.reserved = MODEL_ALLOCATED + LONG_RECORDING_PEAK

        with mock.patch.object(self.worker, "load_audio", side_effect=FileNotFoundError("missing")):
            with self.assertRaises(FileNotFoundError):
                self.worker.transcribe(model, FakeProcessor(), Path("missing.wav"))

        self.assertEqual(self.cuda.empty_cache_calls, 1)
        self.assertEqual(model.generate_calls, 0)

    def test_processor_failure_still_releases(self) -> None:
        model = FakeModel(self.cuda)
        self.cuda.reserved = MODEL_ALLOCATED + LONG_RECORDING_PEAK

        with mock.patch.object(self.worker, "load_audio", return_value=self.waveform):
            with self.assertRaises(RuntimeError):
                self.worker.transcribe(model, FailingProcessor(), Path("recording.wav"))

        self.assertEqual(self.cuda.empty_cache_calls, 1)

    def test_model_failure_survives_cleanup_failure(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK, error=ValueError("generate failed"))
        self.cuda.empty_cache_error = RuntimeError("driver went away")
        stderr = io.StringIO()

        with contextlib.redirect_stderr(stderr):
            with self.assertRaises(ValueError) as raised:
                self.transcribe(model)

        self.assertEqual(str(raised.exception), "generate failed")
        self.assertIn("GPU memory release failed: driver went away", stderr.getvalue())

    def test_a_failed_frame_holds_its_tensors_through_transcribes_finally(self) -> None:
        model = FakeModel(self.cuda)
        processor = FaultInjectingProcessor(self.cuda)

        def state_inside_handler() -> tuple[int, int]:
            try:
                with mock.patch.object(self.worker, "load_audio", return_value=self.waveform):
                    self.worker.transcribe(model, processor, Path("bad.wav"))
            except ValueError:
                return self.cuda.allocated, self.cuda.reserved
            self.fail("transcribe should have failed")

        alive_allocated, alive_reserved = state_inside_handler()

        # transcribe's finally ran while the traceback still held the tensor, so
        # it saw no unused cache and could not give the block back.
        self.assertEqual(alive_allocated, MODEL_ALLOCATED + FAULT_TENSOR)
        self.assertEqual(alive_reserved, MODEL_ALLOCATED + FAULT_TENSOR)
        self.assertEqual(self.cuda.empty_cache_calls, 0)
        # When the handler ended the tensor was freed, but only into the cache:
        # nothing returned it to the driver.
        self.assertEqual(self.cuda.allocated, MODEL_ALLOCATED)
        self.assertGreaterEqual(self.cuda.reserved - self.cuda.allocated, FAULT_TENSOR)

    def test_repeated_requests_keep_the_model_and_release_each_time(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK)

        results = [self.transcribe(model) for _ in range(3)]

        self.assertEqual(results, ["hello there"] * 3)
        self.assertEqual(model.generate_calls, 3)
        self.assertEqual(self.cuda.empty_cache_calls, 3)
        self.assertEqual(self.cuda.reserved, MODEL_ALLOCATED)


class MainProtocolTests(unittest.TestCase):
    """main() keeps its JSONL contract and still releases GPU memory per request."""

    def setUp(self) -> None:
        self.worker, self.cuda = load_worker()

    def run_main(
        self,
        model: FakeModel,
        stdin_text: str,
        processor: FakeProcessor | None = None,
    ) -> tuple[int, list[dict], str, list[tuple[dict, int]]]:
        """Run main() over stdin and snapshot the unused cache at every emit."""
        processor = processor or FakeProcessor()
        stdout = io.StringIO()
        stderr = io.StringIO()
        snapshots: list[tuple[dict, int]] = []
        original_emit = self.worker.emit

        def recording_emit(message: dict) -> None:
            snapshots.append((message, self.cuda.reserved - self.cuda.allocated))
            original_emit(message)

        with mock.patch.object(self.worker, "emit", recording_emit), \
                mock.patch.object(self.worker, "model_files_present", return_value=True), \
                mock.patch.object(self.worker, "load_speech_stack", return_value=(processor, model)), \
                mock.patch.object(self.worker, "load_audio", return_value=FakeWaveform()), \
                mock.patch.object(sys, "stdin", io.StringIO(stdin_text)), \
                contextlib.redirect_stdout(stdout), \
                contextlib.redirect_stderr(stderr):
            exit_code = self.worker.main()
        lines = [json.loads(line) for line in stdout.getvalue().splitlines()]
        return exit_code, lines, stderr.getvalue(), snapshots

    def test_success_response_unchanged_and_memory_released(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK)

        exit_code, lines, _, _ = self.run_main(model, '{"id":"a","audio_path":"rec.wav"}\n')

        self.assertEqual(exit_code, 0)
        self.assertTrue(lines[0]["ready"])
        self.assertEqual(lines[1], {"id": "a", "text": "hello there", "error": None})
        self.assertEqual(self.cuda.empty_cache_calls, 1)

    def test_failure_response_and_stderr_unchanged_and_memory_released(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK, error=ValueError("generate failed"))

        exit_code, lines, stderr, _ = self.run_main(model, '{"id":7,"audio_path":"rec.wav"}\n')

        self.assertEqual(exit_code, 0)
        self.assertEqual(lines[1], {"id": 7, "text": "", "error": "generate failed"})
        self.assertIn("Transcription failed: generate failed", stderr)
        self.assertEqual(self.cuda.empty_cache_calls, 1)

    def test_invalid_request_does_not_touch_gpu_memory(self) -> None:
        model = FakeModel(self.cuda, peak=LONG_RECORDING_PEAK)

        _, lines, _, _ = self.run_main(model, '{"id":1}\n')

        self.assertEqual(lines[1], {"id": 1, "text": "", "error": "Request is missing a valid 'audio_path'"})
        self.assertEqual(model.generate_calls, 0)
        self.assertEqual(self.cuda.empty_cache_calls, 0)
        self.assertEqual(self.cuda.allocated, MODEL_ALLOCATED)

    def test_error_response_waits_for_the_failed_tracebacks_tensors(self) -> None:
        model = FakeModel(self.cuda)
        processor = FaultInjectingProcessor(self.cuda)

        exit_code, lines, stderr, snapshots = self.run_main(
            model, '{"id":"bad","audio_path":"rec.wav"}\n', processor
        )

        self.assertEqual(exit_code, 0)
        self.assertEqual(lines[1], {"id": "bad", "text": "", "error": "input processing failed"})
        self.assertIn("Transcription failed: input processing failed", stderr)
        # The allocation the failing frame held is gone and the allocator's cache
        # was already returned to the driver when the error line was written.
        self.assertEqual([unused for message, unused in snapshots if message.get("error")], [0])
        self.assertEqual(self.cuda.allocated, MODEL_ALLOCATED)
        self.assertEqual(self.cuda.reserved, MODEL_ALLOCATED)
        self.assertEqual(model.generate_calls, 0)

    def test_failure_then_success_returns_to_baseline(self) -> None:
        model = FakeModel(self.cuda)
        processor = FaultInjectingProcessor(self.cuda)

        exit_code, lines, _, snapshots = self.run_main(
            model,
            '{"id":1,"audio_path":"bad.wav"}\n{"id":2,"audio_path":"good.wav"}\n',
            processor,
        )

        self.assertEqual(exit_code, 0)
        self.assertEqual(lines[1], {"id": 1, "text": "", "error": "input processing failed"})
        self.assertEqual(lines[2], {"id": 2, "text": "hello there", "error": None})
        self.assertEqual(model.generate_calls, 1)
        self.assertEqual([unused for message, unused in snapshots if message.get("error")], [0])
        self.assertEqual(self.cuda.allocated, MODEL_ALLOCATED)
        # The short success keeps its own small cache, as designed.
        self.assertEqual(self.cuda.reserved, MODEL_ALLOCATED + SHORT_REQUEST_PEAK)

    def test_repeated_failures_release_each_time(self) -> None:
        model = FakeModel(self.cuda)
        processor = FaultInjectingProcessor(self.cuda, failures=3)
        requests = "".join(f'{{"id":{index},"audio_path":"bad.wav"}}\n' for index in range(3))

        exit_code, lines, _, snapshots = self.run_main(model, requests, processor)

        self.assertEqual(exit_code, 0)
        self.assertEqual([line["error"] for line in lines[1:]], ["input processing failed"] * 3)
        self.assertEqual(self.cuda.empty_cache_calls, 3)
        self.assertEqual([unused for message, unused in snapshots if message.get("error")], [0, 0, 0])
        self.assertEqual(self.cuda.reserved, MODEL_ALLOCATED)


if __name__ == "__main__":
    unittest.main()
