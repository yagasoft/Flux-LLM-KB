from __future__ import annotations

import importlib
import ipaddress
import json
import os
from pathlib import Path
import platform
import shutil
from typing import Any, Callable
from urllib.parse import urlparse
from urllib.request import urlopen as _urlopen

from .watcher import resolve_watcher_backend


JOB_FAMILIES: tuple[str, ...] = (
    "text",
    "office",
    "image",
    "diagram",
    "archive",
    "media",
    "embedding",
    "preview",
    "general",
)

FAMILY_RESOURCE_CLASS: dict[str, str] = {
    "text": "cpu",
    "office": "cpu",
    "image": "gpu",
    "diagram": "cpu",
    "archive": "io",
    "media": "gpu",
    "embedding": "gpu",
    "preview": "cpu",
    "general": "cpu",
}

FAMILY_DEFAULT_PRIORITY: dict[str, int] = {
    "text": 80,
    "office": 70,
    "diagram": 65,
    "archive": 55,
    "image": 45,
    "media": 40,
    "embedding": 35,
    "preview": 25,
    "general": 10,
}

FAMILY_DEFAULT_TIME_BUDGET_SECONDS: dict[str, int] = {
    "text": 120,
    "office": 300,
    "diagram": 180,
    "archive": 300,
    "image": 600,
    "media": 900,
    "embedding": 300,
    "preview": 180,
    "general": 180,
}

FAMILY_DEFAULT_CAPS: dict[str, int] = {
    "text": 2,
    "office": 1,
    "image": 1,
    "diagram": 1,
    "archive": 1,
    "media": 1,
    "embedding": 1,
    "preview": 1,
    "general": 1,
}

BENCHMARK_FIXTURES: tuple[dict[str, str], ...] = (
    {"name": "text-heavy", "description": "Markdown, code, and plain text files"},
    {"name": "code-heavy", "description": "Synthetic repositories with source, tests, routes, SQL, config, generated code, and parser fallbacks"},
    {"name": "office-pdf-heavy", "description": "Office documents, spreadsheets, presentations, and PDFs"},
    {"name": "archive-container-heavy", "description": "Nested archives, packages, and embedded documents"},
    {"name": "image-heavy", "description": "Images, diagrams, and scanned PDFs"},
    {"name": "audio-video-heavy", "description": "Audio, video, and sidecar transcripts"},
)


def job_family_for_type(job_type: str | None) -> str:
    normalized = str(job_type or "").lower()
    if normalized in {"corpus_extract_text", "corpus_extract_code"}:
        return "text"
    if normalized in {
        "corpus_extract_document",
        "corpus_extract_pdf",
        "corpus_extract_spreadsheet",
        "corpus_extract_presentation",
    }:
        return "office"
    if normalized == "corpus_extract_image":
        return "image"
    if normalized == "corpus_extract_diagram":
        return "diagram"
    if normalized in {"corpus_extract_archive", "corpus_extract_container"}:
        return "archive"
    if normalized in {"corpus_extract_audio", "corpus_extract_video"}:
        return "media"
    if normalized == "corpus_embed":
        return "embedding"
    if normalized == "corpus_preview":
        return "preview"
    return "general"


def kind_to_job_families(kind: str | None) -> tuple[str, ...] | None:
    normalized = str(kind or "all").lower()
    if normalized == "all":
        return None
    if normalized == "images":
        return ("image",)
    if normalized == "diagrams":
        return ("diagram",)
    if normalized in {"archives", "containers"}:
        return ("archive",)
    if normalized == "media":
        return ("media",)
    if normalized == "text":
        return ("text", "office")
    if normalized == "embeddings":
        return ("embedding",)
    return None


def resource_class_for_family(family: str | None) -> str:
    return FAMILY_RESOURCE_CLASS.get(str(family or "general"), "cpu")


def default_priority_for_family(family: str | None) -> int:
    return FAMILY_DEFAULT_PRIORITY.get(str(family or "general"), FAMILY_DEFAULT_PRIORITY["general"])


def time_budget_for_family(family: str | None) -> int:
    return FAMILY_DEFAULT_TIME_BUDGET_SECONDS.get(
        str(family or "general"),
        FAMILY_DEFAULT_TIME_BUDGET_SECONDS["general"],
    )


def validate_local_model_base_url(value: str) -> str:
    parsed = urlparse(str(value).strip())
    if parsed.scheme not in {"http", "https"}:
        raise ValueError("local model base URL must use http or https")
    hostname = parsed.hostname
    if not hostname:
        raise ValueError("local model base URL requires a host")
    if hostname.lower() == "localhost":
        return str(value).strip().rstrip("/")
    try:
        if ipaddress.ip_address(hostname).is_loopback:
            return str(value).strip().rstrip("/")
    except ValueError:
        pass
    raise ValueError("local model base URL must use a loopback host")


def resolve_cache_layout(cache_root: str | None = None) -> dict[str, Any]:
    explicit = str(cache_root or "").strip()
    if explicit:
        root = Path(explicit).expanduser()
        source = "setting"
    elif os.environ.get("FLUX_KB_CACHE_ROOT"):
        root = Path(os.environ["FLUX_KB_CACHE_ROOT"]).expanduser()
        source = "env"
    elif os.environ.get("FLUX_KB_INSTALL_ROOT"):
        root = Path(os.environ["FLUX_KB_INSTALL_ROOT"]) / "private" / "cache"
        source = "install_root"
    elif os.environ.get("FLUX_KB_PRIVATE_DIR"):
        root = Path(os.environ["FLUX_KB_PRIVATE_DIR"]) / "cache"
        source = "private_dir"
    else:
        root = Path.home() / ".flux-llm-kb" / "cache"
        source = "user_cache"
    names = ("models", "ocr", "asr", "vision", "thumbnails", "parser", "embeddings", "temp")
    return {
        "root": str(root),
        "source": source,
        "directories": {name: str(root / name) for name in names},
    }


def collect_acceleration_status(
    *,
    settings: dict[str, Any] | None = None,
    command_runner: Callable[..., Any] | None = None,
    module_importer: Callable[[str], Any] | None = None,
    urlopen: Callable[..., Any] | None = None,
    worker_family_stats: Callable[[], list[dict[str, Any]]] | None = None,
    benchmark_stats: Callable[[], list[dict[str, Any]]] | None = None,
    benchmark_history: Callable[[], list[dict[str, Any]]] | None = None,
) -> dict[str, Any]:
    resolved = _resolved_settings(settings)
    runner = command_runner or _run_command
    importer = module_importer or importlib.import_module
    opener = urlopen or _urlopen
    cache = resolve_cache_layout(str(resolved.get("acceleration.cache_root") or ""))
    family_stats = _worker_family_status_rows(resolved, worker_family_stats)
    benchmarks = _benchmark_status_rows(benchmark_stats, benchmark_history)
    return {
        "capabilities": {
            "cpu": _cpu_status(),
            "memory": _memory_status(),
            "disk": _disk_status(cache["root"]),
            "nvidia": _nvidia_status(runner),
            "onnxruntime": _onnxruntime_status(importer),
            "local_model": _local_model_status(resolved, opener),
            "watcher_backend": _watcher_backend_status(importer, str(resolved.get("watcher.backend") or "auto")),
        },
        "cache": cache,
        "worker_families": family_stats,
        "benchmarks": benchmarks,
    }


def _resolved_settings(overrides: dict[str, Any] | None) -> dict[str, Any]:
    defaults: dict[str, Any] = {
        "acceleration.cache_root": "",
        "acceleration.local_inference.enabled": False,
        "acceleration.local_inference.provider": "ollama",
        "acceleration.local_inference.base_url": "http://127.0.0.1:11434",
        "acceleration.local_inference.probe_timeout_seconds": 1,
        "watcher.backend": os.environ.get("FLUX_KB_WATCHER_BACKEND", "auto"),
    }
    for family, cap in FAMILY_DEFAULT_CAPS.items():
        defaults[f"acceleration.worker_cap.{family}"] = cap
    if overrides is not None:
        return {**defaults, **overrides}
    try:
        from .settings import SettingsService

        service = SettingsService()
        for key in list(defaults):
            defaults[key] = service.resolve(key).raw_value
    except Exception:
        pass
    return defaults


def _run_command(command: list[str], **kwargs: Any) -> Any:
    from .processes import run_no_window

    return run_no_window(command, text=True, capture_output=True, **kwargs)


def _cpu_status() -> dict[str, Any]:
    count = os.cpu_count() or 1
    return {
        "ok": True,
        "count": count,
        "architecture": platform.machine(),
        "platform": platform.platform(),
    }


def _memory_status() -> dict[str, Any]:
    total_bytes = _windows_total_memory_bytes()
    return {
        "ok": total_bytes is not None,
        "total_bytes": total_bytes,
        "message": "available" if total_bytes is not None else "memory total unavailable from stdlib",
    }


def _windows_total_memory_bytes() -> int | None:
    if platform.system().lower() != "windows":
        return None
    try:
        import ctypes

        class MemoryStatus(ctypes.Structure):
            _fields_ = [
                ("dwLength", ctypes.c_ulong),
                ("dwMemoryLoad", ctypes.c_ulong),
                ("ullTotalPhys", ctypes.c_ulonglong),
                ("ullAvailPhys", ctypes.c_ulonglong),
                ("ullTotalPageFile", ctypes.c_ulonglong),
                ("ullAvailPageFile", ctypes.c_ulonglong),
                ("ullTotalVirtual", ctypes.c_ulonglong),
                ("ullAvailVirtual", ctypes.c_ulonglong),
                ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
            ]

        status = MemoryStatus()
        status.dwLength = ctypes.sizeof(MemoryStatus)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.ullTotalPhys)
    except Exception:
        return None
    return None


def _disk_status(cache_root: str) -> dict[str, Any]:
    path = Path(cache_root)
    probe = path if path.exists() else path.parent
    try:
        usage = shutil.disk_usage(probe)
    except Exception as exc:
        return {"ok": False, "path": str(path), "message": str(exc)}
    return {
        "ok": True,
        "path": str(path),
        "total_bytes": usage.total,
        "free_bytes": usage.free,
    }


def _nvidia_status(command_runner: Callable[..., Any]) -> dict[str, Any]:
    command = [
        "nvidia-smi",
        "--query-gpu=name,memory.total,driver_version",
        "--format=csv,noheader,nounits",
    ]
    try:
        result = command_runner(command, timeout=2)
    except FileNotFoundError:
        return {"ok": False, "state": "missing", "message": "nvidia-smi not found", "gpus": []}
    except Exception as exc:
        return {"ok": False, "state": "unavailable", "message": str(exc), "gpus": []}
    if getattr(result, "returncode", 1) != 0:
        message = (getattr(result, "stderr", "") or getattr(result, "stdout", "") or "nvidia-smi unavailable").strip()
        return {"ok": False, "state": "unavailable", "message": message, "gpus": []}
    gpus = []
    for line in str(getattr(result, "stdout", "")).splitlines():
        parts = [part.strip() for part in line.split(",")]
        if len(parts) >= 3:
            gpus.append({"name": parts[0], "memory_total_mb": _int_or_none(parts[1]), "driver_version": parts[2]})
    return {
        "ok": bool(gpus),
        "state": "available" if gpus else "unavailable",
        "message": f"{len(gpus)} NVIDIA GPU(s) detected" if gpus else "nvidia-smi returned no GPUs",
        "gpus": gpus,
    }


def _onnxruntime_status(module_importer: Callable[[str], Any]) -> dict[str, Any]:
    try:
        module = module_importer("onnxruntime")
    except ModuleNotFoundError:
        return {"ok": False, "state": "missing", "providers": [], "message": "onnxruntime not installed"}
    try:
        providers = list(module.get_available_providers())
    except Exception as exc:
        return {"ok": False, "state": "unavailable", "providers": [], "message": str(exc)}
    return {
        "ok": bool(providers),
        "state": "available" if providers else "unavailable",
        "providers": providers,
        "message": ", ".join(providers) if providers else "no providers reported",
    }


def _watcher_backend_status(module_importer: Callable[[str], Any], policy: str) -> dict[str, Any]:
    try:
        status = resolve_watcher_backend(policy, module_finder=module_importer)
    except RuntimeError as exc:
        return {
            "ok": False,
            "state": "missing",
            "provider": "watchdog",
            "policy": policy,
            "selected_backend": "watchdog",
            "native": False,
            "fallback_reason": "watchdog_missing",
            "message": str(exc),
        }
    except Exception as exc:
        return {
            "ok": False,
            "state": "unavailable",
            "provider": "watchdog",
            "policy": policy,
            "selected_backend": None,
            "native": False,
            "fallback_reason": "backend_resolution_error",
            "message": str(exc),
        }
    return {
        "ok": True,
        "state": "available" if status["native"] else "fallback",
        "provider": status["selected_backend"],
        **status,
    }


def _local_model_status(settings: dict[str, Any], opener: Callable[..., Any]) -> dict[str, Any]:
    provider = str(settings.get("acceleration.local_inference.provider") or "ollama")
    enabled = bool(settings.get("acceleration.local_inference.enabled"))
    base_url = str(settings.get("acceleration.local_inference.base_url") or "")
    timeout = int(settings.get("acceleration.local_inference.probe_timeout_seconds") or 1)
    if not enabled:
        return {"ok": False, "state": "disabled", "provider": provider, "base_url": base_url}
    try:
        base_url = validate_local_model_base_url(base_url)
    except ValueError as exc:
        return {"ok": False, "state": "blocked_config", "provider": provider, "base_url": base_url, "message": str(exc)}
    probe_url = f"{base_url}/api/tags" if provider == "ollama" else base_url
    try:
        response = opener(probe_url, timeout=max(1, timeout))
        raw = response.read(2048).decode("utf-8", errors="replace")
        models = _model_names(raw)
    except Exception as exc:
        return {
            "ok": False,
            "state": "unavailable",
            "provider": provider,
            "base_url": base_url,
            "message": str(exc),
        }
    return {
        "ok": True,
        "state": "available",
        "provider": provider,
        "base_url": base_url,
        "models": models,
    }


def _model_names(raw: str) -> list[str]:
    try:
        payload = json.loads(raw)
    except Exception:
        return []
    models = payload.get("models") if isinstance(payload, dict) else []
    if not isinstance(models, list):
        return []
    names = []
    for item in models:
        if isinstance(item, dict) and item.get("name"):
            names.append(str(item["name"]))
    return names[:20]


def _worker_family_status_rows(
    settings: dict[str, Any],
    stats_loader: Callable[[], list[dict[str, Any]]] | None,
) -> list[dict[str, Any]]:
    if stats_loader is None:
        try:
            from . import database

            stats_loader = database.worker_family_stats
        except Exception:
            stats_loader = lambda: []
    try:
        rows = [dict(row) for row in stats_loader()]
    except Exception:
        rows = []
    seen: set[str] = set()
    enriched: list[dict[str, Any]] = []
    for row in rows:
        family = str(row.get("family") or "general")
        seen.add(family)
        enriched.append(_family_row(settings, family, row))
    for family in JOB_FAMILIES:
        if family not in seen:
            enriched.append(_family_row(settings, family, {}))
    return enriched


def _family_row(settings: dict[str, Any], family: str, row: dict[str, Any]) -> dict[str, Any]:
    configured_cap = int(settings.get(f"acceleration.worker_cap.{family}") or FAMILY_DEFAULT_CAPS.get(family, 1))
    running = int(row.get("running") or 0)
    pending = int(row.get("pending") or 0)
    blocked = int(row.get("blocked") or 0)
    failed = int(row.get("failed") or 0)
    cap_available = max(0, configured_cap - running)
    return {
        "family": family,
        "resource_class": row.get("resource_class") or resource_class_for_family(family),
        "configured_cap": configured_cap,
        "cap_available": cap_available,
        "backpressure": _backpressure_reason(pending=pending, running=running, blocked=blocked, failed=failed, cap_available=cap_available),
        "pending": pending,
        "running": running,
        "blocked": blocked,
        "failed": failed,
        "oldest_pending_age_seconds": _int_or_none(row.get("oldest_pending_age_seconds")),
        "slowest_recent_jobs": _slow_job_rows(row.get("slowest_recent_jobs")),
        "retrying_locked": int(row.get("retrying_locked") or 0),
        "blocked_locked": int(row.get("blocked_locked") or 0),
        "avg_duration_ms": _int_or_none(row.get("avg_duration_ms")),
        "p95_duration_ms": _int_or_none(row.get("p95_duration_ms")),
        "max_duration_ms": _int_or_none(row.get("max_duration_ms")),
        "ocr_cache_hits": int(row.get("ocr_cache_hits") or 0),
        "ocr_cache_misses": int(row.get("ocr_cache_misses") or 0),
        "asr_cache_hits": int(row.get("asr_cache_hits") or 0),
        "asr_cache_misses": int(row.get("asr_cache_misses") or 0),
        "asr_segments": int(row.get("asr_segments") or 0),
        "container_member_count": int(row.get("container_member_count") or 0),
        "container_parsed_child_count": int(row.get("container_parsed_child_count") or 0),
        "container_skipped_child_count": int(row.get("container_skipped_child_count") or 0),
        "container_blocked_dependency_count": int(row.get("container_blocked_dependency_count") or 0),
        "vision_cache_hits": int(row.get("vision_cache_hits") or 0),
        "vision_cache_misses": int(row.get("vision_cache_misses") or 0),
        "vision_descriptions": int(row.get("vision_descriptions") or 0),
        "vision_blocked_dependency_count": int(row.get("vision_blocked_dependency_count") or 0),
        "decorative_image_skips": int(row.get("decorative_image_skips") or 0),
        "frame_sample_count": int(row.get("frame_sample_count") or 0),
        "thumbnail_cache_hits": int(row.get("thumbnail_cache_hits") or 0),
        "thumbnail_cache_misses": int(row.get("thumbnail_cache_misses") or 0),
        "parser_cache_hits": int(row.get("parser_cache_hits") or 0),
        "parser_cache_misses": int(row.get("parser_cache_misses") or 0),
        "manifest_skipped_unchanged": int(row.get("manifest_skipped_unchanged") or 0),
        "embedding_vectors": int(row.get("embedding_vectors") or 0),
        "embedding_skipped_unchanged": int(row.get("embedding_skipped_unchanged") or 0),
        "embedding_batches": int(row.get("embedding_batches") or 0),
        "embedding_cache_hits": int(row.get("embedding_cache_hits") or 0),
        "embedding_cache_misses": int(row.get("embedding_cache_misses") or 0),
    }


def _backpressure_reason(*, pending: int, running: int, blocked: int, failed: int, cap_available: int) -> str | None:
    if pending > 0 and running > 0 and cap_available <= 0:
        return "cap_reached"
    if blocked > 0:
        return "blocked_jobs"
    if failed > 0:
        return "failed_jobs"
    if pending > 0:
        return "pending"
    return None


def _slow_job_rows(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    rows: list[dict[str, Any]] = []
    for item in value[:10]:
        if not isinstance(item, dict):
            continue
        row = {
            "id": str(item.get("id") or ""),
            "path": Path(str(item.get("path") or "")).name,
            "duration_ms": _int_or_none(item.get("duration_ms")),
        }
        rows.append({key: val for key, val in row.items() if val not in {"", None}})
    return rows


def _benchmark_status_rows(
    stats_loader: Callable[[], list[dict[str, Any]]] | None,
    history_loader: Callable[[], list[dict[str, Any]]] | None = None,
) -> dict[str, Any]:
    if stats_loader is None:
        try:
            from . import database

            stats_loader = database.benchmark_fixture_stats
        except Exception:
            stats_loader = lambda: []
    try:
        raw_rows = {str(row.get("name") or ""): dict(row) for row in stats_loader()}
    except Exception:
        raw_rows = {}
    fixtures: list[dict[str, Any]] = []
    totals = {
        "file_count": 0,
        "elapsed_ms": 0,
        "jobs_queued": 0,
        "jobs_completed": 0,
        "jobs_blocked": 0,
        "cache_hits": 0,
        "cache_misses": 0,
    }
    for fixture in BENCHMARK_FIXTURES:
        row = raw_rows.get(fixture["name"], {})
        item = {
            "name": fixture["name"],
            "description": fixture["description"],
            "file_count": int(row.get("file_count") or 0),
            "elapsed_ms": int(row.get("elapsed_ms") or 0),
            "jobs_queued": int(row.get("jobs_queued") or 0),
            "jobs_completed": int(row.get("jobs_completed") or 0),
            "jobs_blocked": int(row.get("jobs_blocked") or 0),
            "cache_hits": int(row.get("cache_hits") or 0),
            "cache_misses": int(row.get("cache_misses") or 0),
        }
        for key in totals:
            totals[key] += int(item[key])
        fixtures.append(item)
    if history_loader is None:
        try:
            from . import database

            history_loader = database.latest_benchmark_runs
        except Exception:
            history_loader = lambda: []
    try:
        history = [_benchmark_history_row(row) for row in history_loader()]
    except Exception:
        history = []
    return {"fixtures": fixtures, "totals": totals, "history": history}


def _benchmark_history_row(row: dict[str, Any]) -> dict[str, Any]:
    payload = dict(row)
    normalized: dict[str, Any] = {
        "id": str(payload.get("id") or ""),
        "fixture": str(payload.get("fixture") or payload.get("name") or ""),
        "mode": str(payload.get("mode") or "scan"),
        "label": payload.get("label"),
        "compare_label": payload.get("compare_label"),
        "status": str(payload.get("status") or "completed"),
        "file_count": int(payload.get("file_count") or 0),
        "elapsed_ms": int(payload.get("elapsed_ms") or 0),
        "throughput_files_per_second": float(payload.get("throughput_files_per_second") or 0.0),
        "p50_ms": _int_or_none(payload.get("p50_ms")),
        "p95_ms": _int_or_none(payload.get("p95_ms")),
        "max_ms": _int_or_none(payload.get("max_ms")),
        "warm_state": str(payload.get("warm_state") or "cold"),
        "cache_hits": int(payload.get("cache_hits") or 0),
        "cache_misses": int(payload.get("cache_misses") or 0),
        "jobs_queued": int(payload.get("jobs_queued") or 0),
        "jobs_completed": int(payload.get("jobs_completed") or 0),
        "jobs_blocked": int(payload.get("jobs_blocked") or 0),
        "previous_elapsed_delta_ms": _int_or_none(payload.get("previous_elapsed_delta_ms")),
        "previous_throughput_delta": _float_or_none(payload.get("previous_throughput_delta")),
        "pass_index": _int_or_none(payload.get("pass_index")),
        "hash_parallelism": _int_or_none(payload.get("hash_parallelism")),
        "worker_count": _int_or_none(payload.get("worker_count")),
        "manifest_skipped_unchanged": int(payload.get("manifest_skipped_unchanged") or 0),
        "worker_family_breakdown": payload.get("worker_family_breakdown") if isinstance(payload.get("worker_family_breakdown"), dict) else {},
        "scope_type": str(payload.get("scope_type") or "synthetic"),
        "scope_hash": payload.get("scope_hash"),
        "deployment_label": payload.get("deployment_label"),
        "build_metadata": payload.get("build_metadata") if isinstance(payload.get("build_metadata"), dict) else {},
        "settings_snapshot": payload.get("settings_snapshot") if isinstance(payload.get("settings_snapshot"), dict) else {},
        "model_telemetry": payload.get("model_telemetry") if isinstance(payload.get("model_telemetry"), dict) else {},
        "recommendation_metadata": payload.get("recommendation_metadata") if isinstance(payload.get("recommendation_metadata"), dict) else {},
        "created_at": payload.get("created_at"),
    }
    return {key: value for key, value in normalized.items() if value is not None and value != ""}


def _float_or_none(value: Any) -> float | None:
    try:
        return None if value is None else float(value)
    except (TypeError, ValueError):
        return None


def _int_or_none(value: Any) -> int | None:
    try:
        if value is None:
            return None
        return int(float(str(value).strip()))
    except (TypeError, ValueError):
        return None
