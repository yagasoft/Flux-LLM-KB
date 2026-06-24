from __future__ import annotations

from types import SimpleNamespace

import pytest

from flux_llm_kb.acceleration import (
    collect_acceleration_status,
    job_family_for_type,
    kind_to_job_families,
    resolve_cache_layout,
    validate_local_model_base_url,
)


def test_cache_layout_defaults_under_install_root(tmp_path, monkeypatch):
    install_root = tmp_path / "FluxLLMKB"
    monkeypatch.setenv("FLUX_KB_INSTALL_ROOT", str(install_root))

    payload = resolve_cache_layout("")

    assert payload["source"] == "install_root"
    assert payload["root"] == str(install_root / "private" / "cache")
    assert payload["directories"]["models"] == str(install_root / "private" / "cache" / "models")
    assert payload["directories"]["embeddings"] == str(install_root / "private" / "cache" / "embeddings")


def test_local_model_base_url_accepts_only_loopback_addresses():
    assert validate_local_model_base_url("http://127.0.0.1:11434") == "http://127.0.0.1:11434"
    assert validate_local_model_base_url("http://localhost:11434") == "http://localhost:11434"
    assert validate_local_model_base_url("http://[::1]:11434") == "http://[::1]:11434"

    with pytest.raises(ValueError, match="loopback"):
        validate_local_model_base_url("https://api.openai.com/v1")

    with pytest.raises(ValueError, match="http"):
        validate_local_model_base_url("file:///tmp/model.sock")


def test_collect_status_reports_disabled_local_model_without_probe(monkeypatch):
    calls = []

    def forbidden_probe(*_args, **_kwargs):
        calls.append("probe")
        raise AssertionError("disabled local model probing must not make network calls")

    payload = collect_acceleration_status(
        settings={
            "acceleration.cache_root": "",
            "acceleration.local_inference.enabled": False,
            "acceleration.local_inference.provider": "ollama",
            "acceleration.local_inference.base_url": "http://127.0.0.1:11434",
            "acceleration.local_inference.probe_timeout_seconds": 1,
        },
        command_runner=lambda *_args, **_kwargs: SimpleNamespace(returncode=1, stdout="", stderr="missing"),
        module_importer=lambda _name: (_ for _ in ()).throw(ModuleNotFoundError("missing")),
        urlopen=forbidden_probe,
        worker_family_stats=lambda: [],
    )

    assert payload["capabilities"]["local_model"]["state"] == "disabled"
    assert payload["capabilities"]["local_model"]["ok"] is False
    assert calls == []


def test_collect_status_reports_fake_nvidia_and_onnx_providers():
    def fake_run(command, **_kwargs):
        if command[0] == "nvidia-smi":
            return SimpleNamespace(returncode=0, stdout="NVIDIA RTX 4090, 24564, 550.54\n", stderr="")
        return SimpleNamespace(returncode=1, stdout="", stderr="missing")

    def fake_import(name):
        if name == "onnxruntime":
            return SimpleNamespace(get_available_providers=lambda: ["CUDAExecutionProvider", "CPUExecutionProvider"])
        if name == "watchdog.observers":
            return SimpleNamespace()
        raise ModuleNotFoundError(name)

    payload = collect_acceleration_status(
        settings={
            "acceleration.cache_root": "",
            "acceleration.local_inference.enabled": False,
            "acceleration.local_inference.provider": "ollama",
            "acceleration.local_inference.base_url": "http://127.0.0.1:11434",
            "acceleration.local_inference.probe_timeout_seconds": 1,
        },
        command_runner=fake_run,
        module_importer=fake_import,
        worker_family_stats=lambda: [
            {
                "family": "media",
                "pending": 2,
                "p95_duration_ms": 95,
                "ocr_cache_hits": 5,
                "ocr_cache_misses": 2,
                "asr_cache_hits": 3,
                "asr_cache_misses": 1,
                "asr_segments": 7,
                "container_member_count": 0,
                "container_parsed_child_count": 0,
                "container_skipped_child_count": 0,
                "container_blocked_dependency_count": 0,
            }
        ],
        benchmark_stats=lambda: [
            {
                "name": "archive-container-heavy",
                "file_count": 8,
                "elapsed_ms": 42,
                "jobs_queued": 3,
                "jobs_completed": 2,
                "jobs_blocked": 1,
                "cache_hits": 4,
                "cache_misses": 2,
            }
        ],
    )

    assert payload["capabilities"]["nvidia"]["ok"] is True
    assert payload["capabilities"]["nvidia"]["gpus"][0]["name"] == "NVIDIA RTX 4090"
    assert payload["capabilities"]["onnxruntime"]["providers"] == ["CUDAExecutionProvider", "CPUExecutionProvider"]
    assert payload["capabilities"]["watcher_backend"]["ok"] is True
    assert payload["worker_families"][0]["family"] == "media"
    assert payload["worker_families"][0]["p95_duration_ms"] == 95
    assert payload["worker_families"][0]["ocr_cache_hits"] == 5
    assert payload["worker_families"][0]["ocr_cache_misses"] == 2
    assert payload["worker_families"][0]["asr_cache_hits"] == 3
    assert payload["worker_families"][0]["asr_cache_misses"] == 1
    assert payload["worker_families"][0]["asr_segments"] == 7
    fixtures_by_name = {fixture["name"]: fixture for fixture in payload["benchmarks"]["fixtures"]}
    assert fixtures_by_name["archive-container-heavy"] == {
        "name": "archive-container-heavy",
        "description": "Nested archives, packages, and embedded documents",
        "file_count": 8,
        "elapsed_ms": 42,
        "jobs_queued": 3,
        "jobs_completed": 2,
        "jobs_blocked": 1,
        "cache_hits": 4,
        "cache_misses": 2,
    }
    assert payload["benchmarks"]["totals"] == {
        "file_count": 8,
        "elapsed_ms": 42,
        "jobs_queued": 3,
        "jobs_completed": 2,
        "jobs_blocked": 1,
        "cache_hits": 4,
        "cache_misses": 2,
    }


def test_collect_status_reports_empty_deterministic_benchmark_fixtures():
    payload = collect_acceleration_status(
        settings={
            "acceleration.cache_root": "",
            "acceleration.local_inference.enabled": False,
            "acceleration.local_inference.provider": "ollama",
            "acceleration.local_inference.base_url": "http://127.0.0.1:11434",
            "acceleration.local_inference.probe_timeout_seconds": 1,
        },
        command_runner=lambda *_args, **_kwargs: SimpleNamespace(returncode=1, stdout="", stderr="missing"),
        module_importer=lambda _name: (_ for _ in ()).throw(ModuleNotFoundError("missing")),
        worker_family_stats=lambda: [],
        benchmark_stats=lambda: [],
    )

    names = [fixture["name"] for fixture in payload["benchmarks"]["fixtures"]]
    assert names == [
        "text-heavy",
        "office-pdf-heavy",
        "archive-container-heavy",
        "image-heavy",
        "audio-video-heavy",
    ]
    assert all(fixture["file_count"] == 0 for fixture in payload["benchmarks"]["fixtures"])


def test_job_family_mapping_keeps_existing_kind_compatibility():
    assert job_family_for_type("corpus_extract_video") == "media"
    assert job_family_for_type("corpus_extract_archive") == "archive"
    assert job_family_for_type("corpus_extract_document") == "office"
    assert job_family_for_type("corpus_extract_code") == "text"
    assert job_family_for_type("corpus_embed") == "embedding"
    assert job_family_for_type("unknown") == "general"
    assert kind_to_job_families("media") == ("media",)
    assert kind_to_job_families("diagrams") == ("diagram",)
    assert kind_to_job_families("archives") == ("archive",)
    assert kind_to_job_families("text") == ("text", "office")
    assert kind_to_job_families("all") is None
