from __future__ import annotations

import argparse
from contextlib import contextmanager
from dataclasses import dataclass
import gc
import importlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading
import time
from typing import Any

from fastapi import FastAPI, File, Form, HTTPException, UploadFile

from .gpu_runtime import RuntimeModelKey, RuntimeOperationNotReady, RuntimeResidencyTracker, normalise_priority_class, runtime_preemption_policy
from .gpu_scheduler import GpuLeaseRejected, GpuLeaseTimeout, GpuModelResidency, get_gpu_scheduler, task_profile


ASR_MODEL_ALIASES = {
    "large-v3-turbo": "mobiuslabsgmbh/faster-whisper-large-v3-turbo",
}
DEFAULT_ASR_MODEL = "large-v3-turbo"
DEFAULT_ASR_MODEL_PATH = "/models/faster-whisper-large-v3-turbo"
REQUIRED_MODEL_FILES = ("config.json", "model.bin", "tokenizer.json", "preprocessor_config.json")


@dataclass(frozen=True)
class AsrServiceConfig:
    model: str = DEFAULT_ASR_MODEL
    model_path: Path = Path(DEFAULT_ASR_MODEL_PATH)
    device: str = "cuda"
    compute_type: str = "float16"

    @classmethod
    def from_env(cls) -> "AsrServiceConfig":
        return cls(
            model=os.environ.get("FLUX_KB_ASR_MODEL") or DEFAULT_ASR_MODEL,
            model_path=Path(os.environ.get("FLUX_KB_ASR_MODEL_PATH") or DEFAULT_ASR_MODEL_PATH),
            device=os.environ.get("FLUX_KB_ASR_DEVICE") or "cuda",
            compute_type=os.environ.get("FLUX_KB_ASR_COMPUTE_TYPE") or "float16",
        )


def resolve_model_alias(model: str) -> str:
    return ASR_MODEL_ALIASES.get(model, model)


def model_file_status(model_path: Path) -> dict[str, Any]:
    missing = [name for name in REQUIRED_MODEL_FILES if not (model_path / name).exists()]
    return {
        "model_files_present": not missing,
        "required_files": list(REQUIRED_MODEL_FILES),
        "missing_files": missing,
    }


def download_model(model: str, output_dir: Path) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    utils = importlib.import_module("faster_whisper.utils")
    resolved = resolve_model_alias(model)
    utils.download_model(resolved, output_dir=str(output_dir))
    return output_dir


class AsrRuntime:
    def __init__(self, config: AsrServiceConfig) -> None:
        self.config = config
        self.loaded = False
        self._model: Any | None = None
        self._model_lock = threading.Lock()

    def health(self) -> dict[str, Any]:
        return {"loaded": self.loaded}

    def _ensure_model_files(self) -> None:
        status = model_file_status(self.config.model_path)
        if not status["model_files_present"]:
            missing = ", ".join(status["missing_files"])
            raise FileNotFoundError(f"required model files are missing: {missing}")

    def _load_model(self) -> Any:
        with self._model_lock:
            if self._model is not None:
                return self._model
            self._ensure_model_files()
            faster_whisper = importlib.import_module("faster_whisper")
            kwargs: dict[str, Any] = {"local_files_only": True}
            if self.config.device != "auto":
                kwargs["device"] = self.config.device
            if self.config.compute_type != "default":
                kwargs["compute_type"] = self.config.compute_type
            self._model = faster_whisper.WhisperModel(str(self.config.model_path), **kwargs)
            self.loaded = True
            _record_asr_residency(get_gpu_scheduler(), self.config, resident=True)
            return self._model

    def unload_model(self) -> bool:
        with self._model_lock:
            unloaded = self._model is not None
            self._model = None
            self.loaded = False
        _release_gpu_memory()
        return unloaded

    def transcribe(self, audio_path: Path) -> dict[str, Any]:
        model = self._load_model()
        segments_iter, _info = model.transcribe(str(audio_path))
        segments: list[dict[str, Any]] = []
        parts: list[str] = []
        for segment in segments_iter:
            text = str(getattr(segment, "text", "") or "").strip()
            item = {
                "start": float(getattr(segment, "start", 0.0) or 0.0),
                "end": float(getattr(segment, "end", 0.0) or 0.0),
                "text": text,
            }
            segments.append(item)
            if text:
                parts.append(text)
        return {"text": "\n".join(parts).strip(), "segments": segments}


def create_app(config: AsrServiceConfig | None = None, *, runtime: Any | None = None, gpu_scheduler: Any | None = None):
    service_config = config or AsrServiceConfig.from_env()
    service_runtime = runtime or AsrRuntime(service_config)
    scheduler = gpu_scheduler or get_gpu_scheduler()
    tracker = RuntimeResidencyTracker(owner_component="asr")
    model_key = RuntimeModelKey("asr", service_config.model)
    app = FastAPI(title="Flux local ASR service")
    _clear_startup_residency(scheduler)

    @app.get("/livez")
    def livez() -> dict[str, Any]:
        return {"ok": True, "service": "asr"}

    @app.get("/health")
    def health() -> dict[str, Any]:
        file_status = model_file_status(service_config.model_path)
        runtime_health = service_runtime.health() if hasattr(service_runtime, "health") else {}
        return {
            "ready": bool(file_status["model_files_present"]),
            "model": service_config.model,
            "resolved_model": resolve_model_alias(service_config.model),
            "model_path": str(service_config.model_path),
            "device": service_config.device,
            "compute_type": service_config.compute_type,
            **file_status,
            **runtime_health,
            "gpu_scheduler": scheduler.status() if hasattr(scheduler, "status") else {"mode": "unknown"},
        }

    @app.post("/v1/gpu/unload")
    def gpu_unload(payload: dict[str, Any]) -> dict[str, Any]:
        task_type = str(payload.get("task_type") or "")
        model_id = str(payload.get("model_id") or "")
        if task_type != "asr":
            raise HTTPException(status_code=400, detail=f"unsupported GPU unload task_type: {task_type}")
        if model_id and model_id not in {service_config.model, resolve_model_alias(service_config.model), str(service_config.model_path)}:
            raise HTTPException(status_code=404, detail=f"model is not served: {model_id}")
        expected_generation = str(payload.get("expected_generation") or "")
        if not expected_generation:
            raise HTTPException(status_code=400, detail="expected_generation is required")
        try:
            expected_activity_sequence = int(payload["expected_activity_sequence"])
        except (KeyError, TypeError, ValueError) as exc:
            raise HTTPException(status_code=400, detail="expected_activity_sequence is required") from exc
        loaded_models = [model_key] if getattr(service_runtime, "_model", None) is not None else []
        target_present = bool(loaded_models)
        allocator_before = tracker.inventory(loaded_models)["allocator"]
        result = tracker.unload(
            model_key,
            expected_generation=expected_generation,
            expected_activity_sequence=expected_activity_sequence,
            remove=lambda: bool(service_runtime.unload_model()) if hasattr(service_runtime, "unload_model") else False,
        )
        if result["reason"] in {"generation_mismatch", "activity_mismatch", "in_flight", "process_in_flight", "queued"}:
            raise HTTPException(status_code=409, detail={"reason": result["reason"]})
        unload_confirmed = bool(result["unloaded"]) or not target_present
        if not unload_confirmed:
            raise HTTPException(status_code=409, detail={"reason": result["reason"]})
        _record_asr_residency(scheduler, service_config, resident=False)
        return {
            "ok": True,
            "task_type": "asr",
            "model_id": service_config.model,
            "unloaded": bool(result["unloaded"]),
            "unload_confirmed": True,
            "target_present": target_present,
            "resident": False,
            "allocator_before": allocator_before,
            "allocator_after": tracker.inventory([])["allocator"],
        }

    @app.post("/v1/gpu/trim")
    def gpu_trim(payload: dict[str, Any]) -> dict[str, Any]:
        task_type = str(payload.get("task_type") or "")
        model_id = str(payload.get("model_id") or "")
        if task_type != "asr":
            raise HTTPException(status_code=400, detail=f"unsupported GPU trim task_type: {task_type}")
        if model_id and model_id not in {service_config.model, resolve_model_alias(service_config.model), str(service_config.model_path)}:
            raise HTTPException(status_code=404, detail=f"model is not served: {model_id}")
        expected_generation = str(payload.get("expected_generation") or "")
        if not expected_generation:
            raise HTTPException(status_code=400, detail="expected_generation is required")
        try:
            expected_activity_sequence = int(payload["expected_activity_sequence"])
        except (KeyError, TypeError, ValueError) as exc:
            raise HTTPException(status_code=400, detail="expected_activity_sequence is required") from exc
        loaded_models = [model_key] if getattr(service_runtime, "_model", None) is not None else []
        if not loaded_models:
            raise HTTPException(status_code=404, detail=f"model is not resident: {service_config.model}")
        allocator_before = tracker.inventory(loaded_models)["allocator"]
        result = tracker.trim_allocator(
            model_key,
            expected_generation=expected_generation,
            expected_activity_sequence=expected_activity_sequence,
            trim=_release_gpu_memory,
        )
        if not result["trimmed"]:
            raise HTTPException(status_code=409, detail={"reason": result["reason"]})
        return {
            "ok": True,
            "task_type": "asr",
            "model_id": service_config.model,
            "unloaded": False,
            "trim_confirmed": True,
            "resident": True,
            "allocator_before": allocator_before,
            "allocator_after": tracker.inventory([model_key])["allocator"],
        }

    @app.get("/v1/gpu/residency")
    def gpu_residency() -> dict[str, Any]:
        loaded_models = [model_key] if getattr(service_runtime, "_model", None) is not None else []
        return {
            "owner_component": "asr",
            "worker_count": 1,
            "preemption": runtime_preemption_policy("asr", ("asr",)),
            **tracker.inventory(loaded_models),
        }

    @app.post("/v1/audio/transcriptions")
    async def transcriptions(
        file: UploadFile = File(...),
        model: str = Form(DEFAULT_ASR_MODEL),
        response_format: str = Form("json"),
        request_class: str = Form("background"),
        request_id: str = Form(""),
    ) -> dict[str, Any]:
        if model and model != service_config.model:
            raise HTTPException(status_code=400, detail=f"model is not served: {model}")
        if response_format and response_format != "json":
            raise HTTPException(status_code=400, detail="only response_format=json is supported")
        status = model_file_status(service_config.model_path)
        if not status["model_files_present"]:
            missing = ", ".join(status["missing_files"])
            raise HTTPException(status_code=503, detail=f"required model files are missing: {missing}")
        suffix = Path(file.filename or "audio.wav").suffix or ".wav"
        with tempfile.NamedTemporaryFile(prefix="flux-kb-asr-upload-", suffix=suffix, delete=False) as handle:
            temp_path = Path(handle.name)
            shutil.copyfileobj(file.file, handle)
        try:
            resolved_request_class = _runtime_request_class(request_class)
            resolved_request_id = request_id if isinstance(request_id, str) else ""
            profile = task_profile(
                "asr",
                model_id=service_config.model,
                component="asr",
                request_id=resolved_request_id,
                priority_class=resolved_request_class,
                metadata={"workload_class": "unknown"},
            )
            ticket = tracker.enqueue(
                model_key,
                priority_class=resolved_request_class,
                priority=profile.priority,
                request_id=resolved_request_id,
            )
            with _runtime_gpu_operation(scheduler, profile, ticket, tracker) as measurement:
                pre_load = _allocator_reservation_snapshot(tracker, [model_key])
                loader = getattr(service_runtime, "_load_model", None)
                if callable(loader):
                    loader()
                post_load = _allocator_reservation_snapshot(tracker, [model_key])
                try:
                    return dict(service_runtime.transcribe(temp_path))
                finally:
                    _record_vram_measurement(scheduler, profile, tracker, measurement, [model_key], pre_load, post_load)
        except GpuLeaseTimeout as exc:
            raise HTTPException(
                status_code=429,
                detail={
                    "code": "gpu.scheduler_busy",
                    "message": str(exc),
                    "retryable": True,
                    "retry_after_seconds": float(exc.retry_after_seconds),
                },
            ) from exc
        except GpuLeaseRejected as exc:
            raise HTTPException(
                status_code=503,
                detail={"code": "gpu.scheduler_rejected", "message": str(exc), "retryable": False},
            ) from exc
        except FileNotFoundError as exc:
            raise HTTPException(status_code=503, detail=str(exc)) from exc
        finally:
            temp_path.unlink(missing_ok=True)

    return app


def _record_asr_residency(scheduler: Any, config: AsrServiceConfig, *, resident: bool) -> None:
    try:
        profile = task_profile("asr", model_id=config.model, component="asr")
        scheduler.record_model_residency(
            GpuModelResidency(
                model_id=config.model,
                task_type="asr",
                estimated_vram_mb=profile.estimated_vram_mb,
                resident=resident,
                metadata={"component": "asr"},
            )
        )
    except Exception:
        pass


def _clear_startup_residency(scheduler: Any) -> None:
    try:
        scheduler.reset_component_residency("asr")
    except Exception:
        pass


def _release_gpu_memory() -> None:
    gc.collect()
    try:
        import torch

        if hasattr(torch, "cuda") and torch.cuda.is_available():
            torch.cuda.empty_cache()
            if hasattr(torch.cuda, "ipc_collect"):
                torch.cuda.ipc_collect()
    except Exception:
        pass


def _runtime_request_class(value: Any) -> str:
    try:
        return normalise_priority_class(str(value or "background"))
    except ValueError:
        return "background"


def _yield_wait_for_ticket(ticket: Any, tracker: Any) -> Any:
    should_yield = getattr(tracker, "should_yield", None)
    if callable(should_yield):
        return lambda: bool(should_yield(ticket))
    return lambda: getattr(ticket, "priority_class", "background") == "background" and not bool(getattr(ticket, "is_head", True))


def _acquire_gpu_lease(scheduler: Any, profile: Any, ticket: Any, tracker: Any) -> Any:
    try:
        return scheduler.acquire(profile, yield_wait=_yield_wait_for_ticket(ticket, tracker))
    except TypeError as exc:
        if "yield_wait" not in str(exc):
            raise
        return scheduler.acquire(profile)


def _wait_for_runtime_turn(ticket: Any, tracker: Any) -> None:
    ready_to_start = getattr(tracker, "ready_to_start", None)
    if not callable(ready_to_start):
        return
    while not bool(ready_to_start(ticket)):
        time.sleep(0.001)


@contextmanager
def _runtime_gpu_operation(scheduler: Any, profile: Any, ticket: Any, tracker: Any):
    """Acquire global capacity before marking an ASR runtime call active."""
    try:
        while True:
            _wait_for_runtime_turn(ticket, tracker)
            try:
                with _acquire_gpu_lease(scheduler, profile, ticket, tracker):
                    with tracker.operation(ticket) as measurement:
                        yield measurement
                        return
            except RuntimeOperationNotReady:
                # The local head changed after admission.  Release the global
                # lease before yielding to the higher-priority local operation.
                continue
    finally:
        discard_waiting = getattr(tracker, "discard_waiting", None)
        if callable(discard_waiting):
            discard_waiting(ticket)


def _allocator_reservation_snapshot(tracker: RuntimeResidencyTracker, loaded_models: list[RuntimeModelKey]) -> tuple[str, int | None]:
    try:
        inventory = tracker.inventory(loaded_models)
        allocators = inventory.get("allocator") if isinstance(inventory, dict) else None
        measured = [
            max(int(item.get("reserved_mb") or 0), int(item.get("peak_reserved_mb") or 0))
            for item in allocators or ()
            if isinstance(item, dict) and item.get("capability") == "measured"
        ]
        return ("measured", max(measured)) if measured else ("known_unmeasured", None)
    except Exception:
        return "unknown", None


def _record_vram_measurement(
    scheduler: Any,
    profile: Any,
    tracker: RuntimeResidencyTracker,
    measurement: Any,
    loaded_models: list[RuntimeModelKey],
    pre_load: tuple[str, int | None],
    post_load: tuple[str, int | None],
) -> None:
    record = getattr(scheduler, "record_vram_sample", None)
    if not callable(record):
        return
    peak = _allocator_reservation_snapshot(tracker, loaded_models)
    current_process_in_flight = getattr(tracker, "process_in_flight", None)
    if callable(current_process_in_flight):
        process_in_flight = int(current_process_in_flight() or 0)
    else:
        process_in_flight = int(getattr(measurement, "process_in_flight", getattr(measurement, "in_flight", 0)) or 0)
    current_epoch = getattr(tracker, "process_activity_epoch", None)
    measurement_epoch = getattr(measurement, "process_activity_epoch", None)
    epoch_changed = callable(current_epoch) and measurement_epoch is not None and int(current_epoch() or 0) != int(measurement_epoch)
    overlapped = process_in_flight != 1 or epoch_changed
    if (pre_load[0], post_load[0], peak[0]) != ("measured", "measured", "measured"):
        record(profile, pre_load_reserved_mb=None, post_load_reserved_mb=None, execution_peak_reserved_mb=None,
               allocator_capability="known_unmeasured", tracker_overlapped=overlapped, sample_skipped_reason="allocator_unmeasured")
        return
    record(profile, pre_load_reserved_mb=pre_load[1], post_load_reserved_mb=post_load[1], execution_peak_reserved_mb=peak[1],
           allocator_capability="measured", tracker_overlapped=overlapped,
           sample_skipped_reason="tracker_overlap" if overlapped else "")


def _health_command(config: AsrServiceConfig) -> int:
    payload = {
        "ready": model_file_status(config.model_path)["model_files_present"],
        "model": config.model,
        "resolved_model": resolve_model_alias(config.model),
        "model_path": str(config.model_path),
        "device": config.device,
        "compute_type": config.compute_type,
        **model_file_status(config.model_path),
    }
    print(json.dumps(payload, sort_keys=True))
    return 0 if payload["ready"] else 1


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Flux local ASR service")
    subparsers = parser.add_subparsers(dest="command", required=True)
    serve_parser = subparsers.add_parser("serve", help="Run the ASR HTTP service")
    serve_parser.add_argument("--host", default="0.0.0.0")
    serve_parser.add_argument("--port", type=int, default=8788)
    download_parser = subparsers.add_parser("download-model", help="Download the configured faster-whisper model")
    download_parser.add_argument("--model", default=DEFAULT_ASR_MODEL)
    download_parser.add_argument("--output-dir", required=True)
    subparsers.add_parser("health", help="Check whether required model files are present")
    args = parser.parse_args(argv)

    if args.command == "download-model":
        target = download_model(args.model, Path(args.output_dir))
        print(str(target))
        return 0
    config = AsrServiceConfig.from_env()
    if args.command == "health":
        return _health_command(config)
    if args.command == "serve":
        import uvicorn

        uvicorn.run(create_app(config), host=args.host, port=args.port)
        return 0
    return 2


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main(sys.argv[1:]))
