import sys
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

    response = client.post("/v1/gpu/unload", json={"task_type": "asr", "model_id": "large-v3-turbo"})
    repeat = client.post("/v1/gpu/unload", json={"task_type": "asr", "model_id": "large-v3-turbo"})

    assert response.status_code == 200
    assert response.json()["unloaded"] is True
    assert repeat.status_code == 200
    assert repeat.json()["unloaded"] is False
    assert runtime.loaded is False
    assert runtime._model is None
    assert records[-1].task_type == "asr"
    assert records[-1].model_id == "large-v3-turbo"
    assert records[-1].resident is False


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
