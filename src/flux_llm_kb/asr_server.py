from __future__ import annotations

import argparse
from dataclasses import dataclass
import importlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading
from typing import Any

from fastapi import FastAPI, File, Form, HTTPException, UploadFile

from .gpu_scheduler import GpuLeaseRejected, GpuLeaseTimeout, get_gpu_scheduler, task_profile


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
            return self._model

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
    app = FastAPI(title="Flux local ASR service")

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

    @app.post("/v1/audio/transcriptions")
    async def transcriptions(
        file: UploadFile = File(...),
        model: str = Form(DEFAULT_ASR_MODEL),
        response_format: str = Form("json"),
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
            profile = task_profile("asr", model_id=service_config.model, component="asr")
            with scheduler.acquire(profile):
                return dict(service_runtime.transcribe(temp_path))
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
