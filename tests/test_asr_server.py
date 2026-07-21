import sys
import threading
from contextlib import nullcontext
from pathlib import Path
from types import SimpleNamespace

from fastapi.testclient import TestClient

from flux_llm_kb.asr_server import ASR_MODEL_ALIASES, REQUIRED_MODEL_FILES, AsrRuntime, AsrServiceConfig, create_app, download_model


def _model_dir(path: Path) -> Path:
    path.mkdir(parents=True)
    for name in REQUIRED_MODEL_FILES:
        (path / name).write_text("placeholder", encoding="utf-8")
    return path


def test_asr_health_reports_missing_model_files(tmp_path):
    model_path = tmp_path / "faster-whisper-large-v3-turbo"
    client = TestClient(
        create_app(
            AsrServiceConfig(
                model="large-v3-turbo",
                model_path=model_path,
                device="cuda",
                compute_type="float16",
            )
        )
    )

    response = client.get("/health")

    assert response.status_code == 200
    payload = response.json()
    assert payload["ready"] is False
    assert payload["model"] == "large-v3-turbo"
    assert payload["resolved_model"] == ASR_MODEL_ALIASES["large-v3-turbo"]
    assert payload["model_path"] == str(model_path)
    assert payload["device"] == "cuda"
    assert payload["compute_type"] == "float16"
    assert payload["model_files_present"] is False
    assert set(payload["missing_files"]) == set(REQUIRED_MODEL_FILES)


def test_asr_app_start_clears_stale_component_residency(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")
    events: list[str] = []

    class FakeScheduler:
        def reset_component_residency(self, component):
            events.append(component)

        def status(self):
            return {"enabled": True, "mode": "test"}

    create_app(
        AsrServiceConfig(
            model="large-v3-turbo",
            model_path=model_path,
            device="cuda",
            compute_type="float16",
        ),
        gpu_scheduler=FakeScheduler(),
    )

    assert events == ["asr"]


def test_asr_livez_does_not_run_scheduler_or_runtime_health(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")

    class FakeRuntime:
        def health(self):
            raise AssertionError("/livez must not run ASR runtime health")

    class FakeScheduler:
        def reset_component_residency(self, _component):
            return None

        def status(self):
            raise AssertionError("/livez must not query GPU scheduler status")

    client = TestClient(
        create_app(
            AsrServiceConfig(
                model="large-v3-turbo",
                model_path=model_path,
                device="cuda",
                compute_type="float16",
            ),
            runtime=FakeRuntime(),
            gpu_scheduler=FakeScheduler(),
        )
    )

    response = client.get("/livez")

    assert response.status_code == 200
    assert response.json() == {"ok": True, "service": "asr"}


def test_asr_transcription_endpoint_uses_runtime_without_downloading(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")
    calls = {"transcribe": 0}

    class FakeRuntime:
        loaded = False

        def health(self):
            return {"loaded": self.loaded}

        def transcribe(self, audio_path: Path):
            calls["transcribe"] += 1
            assert audio_path.exists()
            self.loaded = True
            return {
                "text": "hello world",
                "segments": [{"start": 0.0, "end": 1.0, "text": "hello world"}],
            }

    client = TestClient(
        create_app(
            AsrServiceConfig(
                model="large-v3-turbo",
                model_path=model_path,
                device="cuda",
                compute_type="float16",
            ),
            runtime=FakeRuntime(),
        )
    )

    response = client.post(
        "/v1/audio/transcriptions",
        data={"model": "large-v3-turbo", "response_format": "json"},
        files={"file": ("sample.wav", b"RIFF....WAVEfmt ", "audio/wav")},
    )

    assert response.status_code == 200
    assert response.json() == {
        "text": "hello world",
        "segments": [{"start": 0.0, "end": 1.0, "text": "hello world"}],
    }
    assert calls == {"transcribe": 1}


def test_asr_transcription_endpoint_wraps_runtime_in_gpu_lease(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")
    events: list[str] = []

    class FakeLease:
        def __enter__(self):
            events.append("lease-enter")
            return self

        def __exit__(self, *_args):
            events.append("lease-exit")
            return False

    class FakeScheduler:
        def acquire(self, profile):
            events.append(f"acquire:{profile.task_type}:{profile.model_id}")
            return FakeLease()

        def status(self):
            return {"enabled": True, "mode": "test"}

    class FakeRuntime:
        def health(self):
            return {"loaded": False}

        def transcribe(self, audio_path: Path):
            assert audio_path.exists()
            events.append("transcribe")
            return {"text": "leased", "segments": []}

    client = TestClient(
        create_app(
            AsrServiceConfig(
                model="large-v3-turbo",
                model_path=model_path,
                device="cuda",
                compute_type="float16",
            ),
            runtime=FakeRuntime(),
            gpu_scheduler=FakeScheduler(),
        )
    )

    response = client.post(
        "/v1/audio/transcriptions",
        data={"model": "large-v3-turbo"},
        files={"file": ("sample.wav", b"RIFF....WAVEfmt ", "audio/wav")},
    )

    assert response.status_code == 200
    assert response.json()["text"] == "leased"
    assert events == ["acquire:asr:large-v3-turbo", "lease-enter", "transcribe", "lease-exit"]


def test_asr_transcription_endpoint_returns_structured_gpu_rejection(tmp_path):
    from flux_llm_kb.gpu_scheduler import GpuLeaseRejected

    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")

    class RejectingScheduler:
        def acquire(self, _profile):
            raise GpuLeaseRejected("GPU task exceeds scheduler budget")

        def status(self):
            return {"enabled": True, "mode": "test"}

    class RuntimeShouldNotRun:
        def health(self):
            return {"loaded": False}

        def transcribe(self, _audio_path: Path):
            raise AssertionError("runtime should not run when scheduler rejects the task")

    client = TestClient(
        create_app(
            AsrServiceConfig(
                model="large-v3-turbo",
                model_path=model_path,
                device="cuda",
                compute_type="float16",
            ),
            runtime=RuntimeShouldNotRun(),
            gpu_scheduler=RejectingScheduler(),
        )
    )

    response = client.post(
        "/v1/audio/transcriptions",
        data={"model": "large-v3-turbo"},
        files={"file": ("sample.wav", b"RIFF....WAVEfmt ", "audio/wav")},
    )

    assert response.status_code == 503
    assert response.json()["detail"] == {
        "code": "gpu.scheduler_rejected",
        "message": "GPU task exceeds scheduler budget",
        "retryable": False,
    }


def test_asr_transcription_endpoint_returns_503_when_model_files_are_missing(tmp_path):
    client = TestClient(
        create_app(
            AsrServiceConfig(
                model="large-v3-turbo",
                model_path=tmp_path / "missing",
                device="cuda",
                compute_type="float16",
            )
        )
    )

    response = client.post(
        "/v1/audio/transcriptions",
        data={"model": "large-v3-turbo"},
        files={"file": ("sample.wav", b"RIFF....WAVEfmt ", "audio/wav")},
    )

    assert response.status_code == 503
    assert "required model files are missing" in response.json()["detail"]


def test_asr_gpu_unload_endpoint_clears_loaded_model_and_is_idempotent(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")
    config = AsrServiceConfig(
        model="large-v3-turbo",
        model_path=model_path,
        device="cuda",
        compute_type="float16",
    )
    runtime = AsrRuntime(config)
    runtime._model = object()
    runtime.loaded = True
    records: list[object] = []

    class FakeScheduler:
        def record_model_residency(self, residency):
            records.append(residency)

        def status(self):
            return {"enabled": True, "mode": "test"}

    client = TestClient(create_app(config, runtime=runtime, gpu_scheduler=FakeScheduler()))

    residency = client.get("/v1/gpu/residency").json()
    repeated_residency = client.get("/v1/gpu/residency").json()
    target = residency["models"][0]
    assert repeated_residency["models"][0]["process_generation"] == target["process_generation"]
    request = {
        "task_type": "asr",
        "model_id": "large-v3-turbo",
        "expected_generation": target["process_generation"],
        "expected_activity_sequence": target["activity_sequence"],
    }
    response = client.post("/v1/gpu/unload", json=request)
    repeat = client.post("/v1/gpu/unload", json=request)

    assert response.status_code == 200
    assert response.json()["unloaded"] is True
    assert response.json()["unload_confirmed"] is True
    assert response.json()["target_present"] is True
    assert set(response.json()) >= {"allocator_before", "allocator_after"}
    assert repeat.status_code == 200
    assert repeat.json()["unloaded"] is False
    assert repeat.json()["unload_confirmed"] is True
    assert repeat.json()["target_present"] is False
    assert runtime.loaded is False
    assert runtime._model is None
    assert records[-1].task_type == "asr"
    assert records[-1].model_id == "large-v3-turbo"
    assert records[-1].resident is False

    second_runtime = AsrRuntime(config)
    second_runtime._model = object()
    second_runtime.loaded = True
    second_generation = TestClient(create_app(config, runtime=second_runtime, gpu_scheduler=FakeScheduler())).get(
        "/v1/gpu/residency"
    ).json()["models"][0]["process_generation"]
    assert second_generation != target["process_generation"]


def test_asr_residency_is_fast_and_unload_refuses_an_active_transcription(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")
    started = threading.Event()
    release = threading.Event()
    responses = []

    class BlockingRuntime:
        def __init__(self):
            self._model = object()
            self.unload_calls = 0

        def health(self):
            raise AssertionError("residency must not call runtime health")

        def transcribe(self, _audio_path):
            started.set()
            assert release.wait(timeout=5)
            return {"text": "done", "segments": []}

        def unload_model(self):
            self.unload_calls += 1
            return True

    class FakeScheduler:
        def reset_component_residency(self, _component):
            return None

        def acquire(self, _profile):
            return nullcontext()

        def status(self):
            raise AssertionError("residency must not query scheduler status")

    runtime = BlockingRuntime()
    client = TestClient(
        create_app(
            AsrServiceConfig(model_path=model_path),
            runtime=runtime,
            gpu_scheduler=FakeScheduler(),
        )
    )
    worker = threading.Thread(
        target=lambda: responses.append(
            client.post(
                "/v1/audio/transcriptions",
                data={"model": "large-v3-turbo"},
                files={"file": ("sample.wav", b"RIFF....WAVEfmt ", "audio/wav")},
            )
        )
    )
    worker.start()
    assert started.wait(timeout=5)
    try:
        residency = client.get("/v1/gpu/residency").json()
        target = residency["models"][0]
        assert residency["owner_component"] == "asr"
        assert residency["worker_count"] == 1
        assert target["model_id"] == "large-v3-turbo"
        assert target["in_flight"] == 1
        assert target["last_started_at"] is not None
        assert target["last_activity_at"] is None

        unload = client.post(
            "/v1/gpu/unload",
            json={
                "task_type": "asr",
                "model_id": "large-v3-turbo",
                "expected_generation": target["process_generation"],
                "expected_activity_sequence": target["activity_sequence"],
            },
        )

        assert unload.status_code == 409
        assert unload.json()["detail"]["reason"] == "in_flight"
        assert runtime.unload_calls == 0
    finally:
        release.set()
        worker.join(timeout=5)

    assert responses[0].status_code == 200


def test_asr_serialises_concurrent_transcriptions_without_leaking_tracker_runtime_error(tmp_path):
    model_path = _model_dir(tmp_path / "faster-whisper-large-v3-turbo")
    first_started = threading.Event()
    release_first = threading.Event()
    second_finished = threading.Event()
    responses = []

    class BlockingRuntime:
        def __init__(self):
            self._model = object()
            self.calls = 0
            self.lock = threading.Lock()

        def transcribe(self, _audio_path):
            with self.lock:
                self.calls += 1
                call = self.calls
            if call == 1:
                first_started.set()
                assert release_first.wait(timeout=5)
            return {"text": f"response-{call}", "segments": []}

    class FakeScheduler:
        def reset_component_residency(self, _component):
            return None

        def acquire(self, _profile):
            return nullcontext()

    client = TestClient(
        create_app(AsrServiceConfig(model_path=model_path), runtime=BlockingRuntime(), gpu_scheduler=FakeScheduler()),
        raise_server_exceptions=False,
    )

    def transcribe():
        response = client.post(
            "/v1/audio/transcriptions",
            data={"model": "large-v3-turbo"},
            files={"file": ("sample.wav", b"RIFF....WAVEfmt ", "audio/wav")},
        )
        responses.append(response)
        second_finished.set()

    first = threading.Thread(target=transcribe)
    first.start()
    assert first_started.wait(timeout=5)
    second = threading.Thread(target=transcribe)
    second.start()
    assert not second_finished.wait(timeout=0.2)
    release_first.set()
    first.join(timeout=5)
    second.join(timeout=5)

    assert len(responses) == 2
    assert [response.status_code for response in responses] == [200, 200]


def test_download_model_resolves_large_v3_turbo_alias(monkeypatch, tmp_path):
    calls = {}

    def fake_download_model(model, *, output_dir, **_kwargs):
        calls["model"] = model
        calls["output_dir"] = output_dir
        Path(output_dir).mkdir(parents=True, exist_ok=True)
        return output_dir

    fake_utils = SimpleNamespace(download_model=fake_download_model)
    monkeypatch.setitem(sys.modules, "faster_whisper", SimpleNamespace(utils=fake_utils))
    monkeypatch.setitem(sys.modules, "faster_whisper.utils", fake_utils)

    target = tmp_path / "faster-whisper-large-v3-turbo"
    result = download_model("large-v3-turbo", target)

    assert result == target
    assert calls == {
        "model": "mobiuslabsgmbh/faster-whisper-large-v3-turbo",
        "output_dir": str(target),
    }


def test_asr_vram_measurement_skips_unmeasured_allocator_without_fabricating_values():
    from flux_llm_kb import asr_server

    recorded = []

    class Scheduler:
        def record_vram_sample(self, profile, **kwargs):
            recorded.append((profile, kwargs))

    class Tracker:
        def inventory(self, _loaded):
            return {"allocator": [{"capability": "known_unmeasured", "reserved_mb": None, "peak_reserved_mb": None}]}

    profile = asr_server.task_profile("asr", model_id="large-v3-turbo")
    asr_server._record_vram_measurement(
        Scheduler(), profile, Tracker(), SimpleNamespace(in_flight=1), [], ("known_unmeasured", None), ("known_unmeasured", None)
    )

    assert recorded[0][1]["allocator_capability"] == "known_unmeasured"
    assert recorded[0][1]["pre_load_reserved_mb"] is None
    assert recorded[0][1]["sample_skipped_reason"] == "allocator_unmeasured"
