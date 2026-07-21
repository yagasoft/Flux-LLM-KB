from __future__ import annotations

from collections import OrderedDict, deque
from contextlib import contextmanager, nullcontext
from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import time
from typing import Any, Callable, Iterable
from urllib.parse import quote, urlsplit, urlunsplit
from uuid import NAMESPACE_URL, UUID, uuid4, uuid5

from .acceleration import (
    JOB_FAMILIES,
    default_priority_for_family,
    job_family_for_type,
    resource_class_for_family,
    time_budget_for_family,
)
from .crawler import CrawlPlan
from .code_index import scope_hash as code_scope_hash
from .embeddings import (
    EmbeddingInput,
    EmbeddingResult,
    embedding_source_hash,
)
from . import acceleration, mail_content_store
from .migrations import load_migrations
from .redaction import redactions_enabled
from .scoring import LifecycleScoreInput, RankedItem, lifecycle_score, reciprocal_rank_fusion
from .text_safety import sanitize_postgres_text_value, strip_postgres_nul
from . import messaging


class GpuControlLockTimeout(RuntimeError):
    """Raised when a separate reconciler already owns the GPU control lock."""


@dataclass(frozen=True)
class GpuEvictionMaintenanceLeader:
    """Session-bound ownership of one idle-eviction maintenance sweep."""

    connection: Any

    def is_valid(self) -> bool:
        try:
            with self.connection.cursor() as cur:
                cur.execute("SELECT 1")
                return bool(cur.fetchone())
        except Exception:
            return False


DEFAULT_DATABASE_URL = "postgresql://flux:flux@127.0.0.1:5432/flux_llm_kb?connect_timeout=1"
_MIGRATION_ADVISORY_LOCK_ID = 570_221_876_500_815
DEFAULT_IMAP_SYNC_STALE_AFTER_SECONDS = 3600
_RETENTION_MEMORY_CLASSES = {"episode", "claim", "corpus"}
_RETENTION_ACTIONS = {"review", "deprioritize", "retire"}
_SEMANTIC_DUPLICATE_MEMORY_CLASSES = {"corpus", "episode", "claim"}
_SEMANTIC_DUPLICATE_OWNER_TABLES = {
    "corpus": "asset_chunks",
    "episode": "episodes",
    "claim": "claims",
}
_SEMANTIC_DUPLICATE_DEFAULT_THRESHOLD = 0.86
_SEMANTIC_DUPLICATE_ALGORITHM = "snowflake-vespa-cosine-v1"
UNSEEN_ASSET_CANCELLED_STATUS = "cancelled_unseen_asset"
DEFAULT_UNSEEN_ASSET_PURGE_GRACE_SECONDS = 86400
DEFAULT_UNSEEN_ASSET_PURGE_BATCH_SIZE = 500
DEFAULT_SEARCH_INDEX_JOB_LIMIT = 250
DEFAULT_SEARCH_INDEX_PAGE_SIZE = 100
DEFAULT_SEARCH_INDEX_EMBEDDING_BATCH_SIZE = 16
DEFAULT_SEARCH_INDEX_TEXT_MAX_CHARS = 12000
MAX_SEARCH_INDEX_JOB_LIMIT = 1000
MAX_SEARCH_INDEX_PAGE_SIZE = 500
MAX_SEARCH_INDEX_TEXT_MAX_CHARS = 200000
OUTBOX_RETRY_BACKOFF_SECONDS = 30
_CODE_EXACT_SYMBOL_BOOST = 0.10
_CODE_IMPLEMENTATION_INTENT_BOOST = 0.025
_VESPA_RRF_K = 60
_VESPA_RRF_RANK_PROFILE = "hybrid_rrf"
_VESPA_RRF_QUERY_MODE = "vespa_hybrid_rrf"
_VESPA_LEXICAL_RANK_PROFILE = "bm25"
_VESPA_LEXICAL_QUERY_MODE = "vespa_lexical_fallback"
_VESPA_RRF_STREAM = "vespa_rrf"
_VESPA_LEXICAL_STREAM = "vespa_lexical"
_VESPA_DENSE_STREAM = "vespa_dense"
_VESPA_VALID_OWNER_TABLES = {"asset_chunks", "episodes", "claims"}
_VESPA_BM25_FEATURES = ("bm25(title)", "bm25(body)", "bm25(source_path)", "bm25(symbols)")
_VESPA_DENSE_FEATURES = ("dense_score", "closeness(field, embedding)", "closeness(field,embedding)")
_CODE_IMPLEMENTATION_RELATIONSHIPS = {"definition", "route", "config", "migration"}
_DEFAULT_RERANK_POOL_TOP_N = 12
_MIN_RERANK_POOL_SIZE = 4
_RERANK_POOL_LIMIT_MULTIPLIER = 1
_DEFAULT_QUERY_EMBEDDING_CACHE_TTL_SECONDS = 120
_DEFAULT_QUERY_EMBEDDING_CACHE_MAX_ENTRIES = 256
_DEFAULT_EMBEDDING_WAIT_TIMEOUT_SECONDS = 5
_QUERY_EMBEDDING_CACHE: OrderedDict[tuple[str, int, str], tuple[float, EmbeddingResult]] = OrderedDict()
_CODE_NON_IMPLEMENTATION_INTENT_TERMS = {
    "call",
    "called",
    "caller",
    "callers",
    "calls",
    "example",
    "examples",
    "fixture",
    "fixtures",
    "import",
    "imports",
    "mock",
    "mocks",
    "reference",
    "references",
    "referenced",
    "spec",
    "specs",
    "test",
    "tests",
    "usage",
    "uses",
}
_CODE_IMPLEMENTATION_INTENT_TERMS = {
    "class",
    "code",
    "config",
    "definition",
    "def",
    "function",
    "handler",
    "implementation",
    "implemented",
    "method",
    "migration",
    "route",
    "source",
    "where",
}
_AMBIGUOUS_CODE_IMPLEMENTATION_INTENT_TERMS = {"config", "source", "where"}
_CODE_SYMBOL_TOKEN_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_$.]*")
_FILENAME_EXTENSION_TOKENS = {
    "c",
    "cc",
    "cs",
    "css",
    "go",
    "h",
    "hpp",
    "html",
    "java",
    "js",
    "json",
    "jsx",
    "kt",
    "md",
    "mdx",
    "php",
    "ps1",
    "py",
    "rb",
    "rs",
    "sql",
    "ts",
    "tsx",
    "txt",
    "xml",
    "yaml",
    "yml",
}
REQUEUE_DOCUMENT_EXTENSIONS = {
    ".doc",
    ".docm",
    ".dot",
    ".dotm",
    ".dotx",
    ".odp",
    ".ods",
    ".odt",
    ".otp",
    ".ots",
    ".ott",
    ".pot",
    ".potm",
    ".potx",
    ".pps",
    ".ppsm",
    ".ppsx",
    ".ppt",
    ".pptm",
    ".rtf",
    ".xls",
    ".xlsb",
    ".xlsm",
    ".xlt",
    ".xltm",
    ".xltx",
}


@dataclass(frozen=True)
class DatabaseStatus:
    ok: bool
    message: str


def database_url() -> str:
    return os.environ.get("FLUX_KB_DATABASE_URL", DEFAULT_DATABASE_URL)


def host_database_url() -> str | None:
    return os.environ.get("FLUX_KB_HOST_DATABASE_URL")


def redact_database_url(url: str | None) -> str | None:
    if not url:
        return None
    if not redactions_enabled():
        return url
    try:
        parts = urlsplit(url)
    except Exception:
        return url
    if not parts.netloc or not parts.password:
        return url
    return _replace_database_url_auth(parts, password="***")


def database_health_report() -> dict[str, Any]:
    service_url = database_url()
    service_status = _check_database_for_report(service_url)
    host_url = host_database_url()
    host_probe_url = _host_database_probe_url(host_url) if host_url else None
    service_check = _database_check_payload(
        service_status,
        target_url=service_url,
        label="API database",
        required=True,
    )
    checks: dict[str, dict[str, Any]] = {"service": service_check}
    if host_url:
        host_status = _check_database_for_report(host_probe_url)
        checks["host_published"] = _database_check_payload(
            host_status,
            target_url=host_url,
            probe_url=host_probe_url,
            label="Host database",
            required=True,
        )
    else:
        checks["host_published"] = {
            "ok": True,
            "message": "host-published database URL not configured",
            "required": False,
            "label": "Host database",
            "target": None,
            "probe_target": None,
        }

    blocked = [name for name, check in checks.items() if check.get("required", True) and not check.get("ok")]
    if not blocked:
        message = "database reachable"
    elif blocked == ["service"]:
        message = "service database blocked"
    elif blocked == ["host_published"]:
        message = "host-published database blocked"
    else:
        message = "database blocked"
    return {
        "ok": not blocked,
        "message": message,
        "checks": checks,
    }


def run_migrations(url: str | None = None) -> list[str]:
    psycopg = _load_psycopg()
    applied: list[str] = []
    with psycopg.connect(url or database_url(), autocommit=True) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT pg_advisory_lock(%s)", (_MIGRATION_ADVISORY_LOCK_ID,))
            try:
                cur.execute(
                    """
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version integer PRIMARY KEY,
                        name text NOT NULL,
                        applied_at timestamptz NOT NULL DEFAULT now()
                    )
                    """
                )
                for migration in load_migrations():
                    cur.execute(
                        "SELECT 1 FROM schema_migrations WHERE version = %s",
                        (migration.version,),
                    )
                    if cur.fetchone():
                        continue
                    cur.execute(migration.sql)
                    cur.execute(
                        """
                        INSERT INTO schema_migrations (version, name)
                        VALUES (%s, %s)
                        ON CONFLICT (version) DO NOTHING
                        RETURNING version
                        """,
                        (migration.version, migration.name),
                    )
                    if cur.fetchone():
                        applied.append(migration.name)
            finally:
                cur.execute("SELECT pg_advisory_unlock(%s)", (_MIGRATION_ADVISORY_LOCK_ID,))
    return applied


def check_database(url: str | None = None) -> DatabaseStatus:
    try:
        psycopg = _load_psycopg()
        with psycopg.connect(url or database_url(), connect_timeout=3) as conn:
            with conn.cursor() as cur:
                cur.execute("SELECT 1")
                cur.fetchone()
        return DatabaseStatus(ok=True, message="database reachable")
    except Exception as exc:  # pragma: no cover - message is environment-specific
        return DatabaseStatus(ok=False, message=str(exc))


@contextmanager
def gpu_control_lock(*, timeout_seconds: float, url: str | None = None):
    """Acquire the session-scoped reconciliation lock with bounded polling."""
    psycopg = _load_psycopg()
    deadline = time.monotonic() + max(0.01, float(timeout_seconds))
    with psycopg.connect(url or database_url()) as conn:
        conn.autocommit = True
        with conn.cursor() as cur:
            acquired = False
            while time.monotonic() < deadline:
                cur.execute("SELECT pg_try_advisory_lock(hashtextextended('flux.gpu.control', 0))")
                row = cur.fetchone()
                if bool(row and row[0]):
                    acquired = True
                    break
                time.sleep(min(0.05, max(0.0, deadline - time.monotonic())))
            if not acquired:
                raise GpuControlLockTimeout("GPU reconciliation control lock timed out")
            try:
                yield conn
            finally:
                cur.execute("SELECT pg_advisory_unlock(hashtextextended('flux.gpu.control', 0))")


@contextmanager
def gpu_eviction_maintenance_leader_lock(*, url: str | None = None):
    """Yield whether this process owns the independent idle-eviction sweep."""
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        conn.autocommit = True
        with conn.cursor() as cur:
            cur.execute("SELECT pg_try_advisory_lock(hashtextextended('flux.gpu.eviction.maintenance', 0))")
            row = cur.fetchone()
            if not bool(row and row[0]):
                yield False
                return
            try:
                yield GpuEvictionMaintenanceLeader(connection=conn)
            finally:
                cur.execute("SELECT pg_advisory_unlock(hashtextextended('flux.gpu.eviction.maintenance', 0))")


def persist_gpu_runtime_observation(
    observation: Any, *, url: str | None = None, connection: Any | None = None,
) -> None:
    """Persist one completed observation atomically, without changing admission policy."""
    connection_context = nullcontext(connection) if connection is not None else _load_psycopg().connect(url or database_url())
    with connection_context as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                for inventory in observation.inventories:
                    component = str(inventory.component or "")
                    owner = str(inventory.owner_component or component)
                    error_metadata = _gpu_reconciliation_error_metadata(getattr(inventory, "error_metadata", {}))
                    state = str(inventory.state or "unknown")
                    cur.execute(
                        """
                        INSERT INTO gpu_runtime_inventory (
                            observation_id, component, owner_component, process_generation,
                            runtime_fingerprint, state, allocator_capability, driver_used_mb,
                            driver_free_mb, known_measured_mb, known_reported_mb,
                            context_allowance_mb, unresolved_known_owner_mb, unattributed_mb,
                            models, allocators, error_code, error_metadata, observed_at
                        ) VALUES (
                            %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s,
                            %s::jsonb, %s::jsonb, %s, %s::jsonb, to_timestamp(%s)
                        )
                        ON CONFLICT (observation_id, component) DO UPDATE
                        SET state = EXCLUDED.state, models = EXCLUDED.models,
                            allocators = EXCLUDED.allocators, error_code = EXCLUDED.error_code,
                            error_metadata = EXCLUDED.error_metadata, observed_at = EXCLUDED.observed_at
                        """,
                        (
                            str(observation.observation_id), component, owner,
                            str(inventory.process_generation or ""), str(inventory.runtime_fingerprint or ""),
                            state, str(inventory.allocator_capability or "unknown"),
                            int(observation.driver_used_mb), int(observation.driver_free_mb),
                            int(inventory.known_measured_mb), int(inventory.known_reported_mb),
                            int(observation.context_allowance_mb), int(observation.unresolved_known_owner_mb),
                            int(observation.unattributed_mb), _json(list(inventory.models)), _json(list(inventory.allocators)),
                            str(inventory.error_code or "")[:80], _json(error_metadata), float(inventory.observed_at),
                        ),
                    )
                    if state in {"unknown", "conflicted"}:
                        cur.execute(
                            """
                            UPDATE gpu_model_residency
                               SET runtime_state = %s, runtime_failure_reason = %s,
                                   runtime_observed_at = now()
                             WHERE owner_component = %s
                            """,
                            (state, str(inventory.error_code or "inventory_failed")[:300], owner),
                        )
                        if state == "conflicted":
                            for model in inventory.models:
                                if not isinstance(model, dict):
                                    continue
                                task_type = str(model.get("task_type") or "")
                                model_id = str(model.get("model_id") or "")
                                if task_type and model_id:
                                    cur.execute(
                                        """
                                        UPDATE gpu_model_residency
                                           SET runtime_state = 'conflicted', runtime_failure_reason = 'owner_conflict',
                                               runtime_observed_at = to_timestamp(%s)
                                         WHERE task_type = %s AND model_id = %s
                                        """,
                                        (float(inventory.observed_at), task_type, model_id),
                                    )
                        continue
                    generation = str(inventory.process_generation or "")
                    cur.execute(
                        """
                        UPDATE gpu_model_residency
                           SET runtime_state = 'absent', runtime_observed_at = to_timestamp(%s),
                               runtime_failure_reason = ''
                         WHERE owner_component = %s
                           AND (
                                runtime_observed_at IS NULL
                                OR (runtime_generation = %s AND runtime_observed_at <= to_timestamp(%s))
                                OR (runtime_generation <> %s AND runtime_observed_at < to_timestamp(%s))
                           )
                        """,
                        (
                            float(inventory.observed_at), owner,
                            generation, float(inventory.observed_at),
                            generation, float(inventory.observed_at),
                        ),
                    )
                    for model in inventory.models:
                        if not isinstance(model, dict):
                            continue
                        task_type = str(model.get("task_type") or "")
                        model_id = str(model.get("model_id") or "")
                        if not task_type or not model_id:
                            continue
                        operation_started_at = _runtime_operation_epoch(model.get("last_operation_started_at", model.get("last_started_at")))
                        operation_completed_at = _runtime_operation_epoch(model.get("last_operation_completed_at", model.get("last_activity_at")))
                        cur.execute(
                            """
                            INSERT INTO gpu_model_residency (
                                task_type, model_id, estimated_vram_mb, resident, last_used_at,
                                metadata, runtime_state, owner_component, runtime_generation,
                                runtime_fingerprint, runtime_activity_sequence, runtime_in_flight,
                                last_operation_started_at, last_operation_completed_at,
                                runtime_observed_at, runtime_failure_reason
                            ) VALUES (%s, %s, 0, true, now(), '{}'::jsonb, 'present', %s, %s, %s, %s, %s, to_timestamp(%s), to_timestamp(%s), to_timestamp(%s), '')
                            ON CONFLICT (model_id, task_type) DO UPDATE
                            SET resident = true, runtime_state = 'present', owner_component = EXCLUDED.owner_component,
                                runtime_generation = EXCLUDED.runtime_generation,
                                runtime_fingerprint = EXCLUDED.runtime_fingerprint,
                                runtime_activity_sequence = EXCLUDED.runtime_activity_sequence,
                                runtime_in_flight = EXCLUDED.runtime_in_flight,
                                last_operation_started_at = EXCLUDED.last_operation_started_at,
                                last_operation_completed_at = EXCLUDED.last_operation_completed_at,
                                runtime_observed_at = EXCLUDED.runtime_observed_at, runtime_failure_reason = ''
                            WHERE gpu_model_residency.runtime_observed_at IS NULL
                               OR (
                                    gpu_model_residency.runtime_generation = EXCLUDED.runtime_generation
                                    AND gpu_model_residency.runtime_observed_at <= EXCLUDED.runtime_observed_at
                               )
                               OR (
                                    gpu_model_residency.runtime_generation <> EXCLUDED.runtime_generation
                                    AND gpu_model_residency.runtime_observed_at < EXCLUDED.runtime_observed_at
                               )
                            """,
                            (
                                task_type, model_id, owner, generation,
                                str(inventory.runtime_fingerprint or ""), int(model.get("activity_sequence") or 0),
                                int(model.get("in_flight") or 0), operation_started_at, operation_completed_at,
                                float(inventory.observed_at),
                            ),
                        )


def _gpu_reconciliation_error_metadata(value: Any) -> dict[str, Any]:
    if not isinstance(value, dict):
        return {}
    clean: dict[str, Any] = {}
    for key, item in value.items():
        safe_key = str(key).replace("\x00", "")[:80]
        if isinstance(item, str):
            clean[safe_key] = item.replace("\x00", " ")[:300]
        elif isinstance(item, (int, float, bool)) or item is None:
            clean[safe_key] = item
    return clean


def _runtime_operation_epoch(value: Any) -> float | None:
    """Normalise runtime-operation timestamps supplied as epoch seconds or ISO text."""
    if value is None or value == "":
        return None
    if isinstance(value, (int, float)):
        return float(value)
    if isinstance(value, str):
        try:
            parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return None
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=UTC)
        return parsed.timestamp()
    return None


def record_gpu_vram_sample(
    *,
    task_type: str,
    model_id: str,
    shape_bucket: str,
    load_delta_mb: int | None,
    working_set_mb: int | None,
    allocator_capability: str,
    tracker_overlapped: bool,
    observed_at: float | None = None,
    url: str | None = None,
) -> bool:
    """Persist one measured, non-overlapping shape sample and retain only 32."""
    if str(allocator_capability or "") != "measured" or tracker_overlapped:
        return False
    if load_delta_mb is None or working_set_mb is None:
        return False
    try:
        load_delta = max(0, int(load_delta_mb))
        working_set = max(0, int(working_set_mb))
    except (TypeError, ValueError):
        return False
    captured_at = time.time() if observed_at is None else float(observed_at)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                cur.execute(
                    """
                    INSERT INTO gpu_vram_samples (
                        task_type, model_id, shape_bucket, load_delta_mb, working_set_mb,
                        allocator_capability, tracker_overlapped, observed_at
                    ) VALUES (%s, %s, %s, %s, %s, 'measured', false, to_timestamp(%s))
                    """,
                    (str(task_type)[:80], str(model_id)[:300], str(shape_bucket)[:300], load_delta, working_set, captured_at),
                )
                cur.execute(
                    """
                    DELETE FROM gpu_vram_samples
                     WHERE id IN (
                        SELECT id
                          FROM gpu_vram_samples
                         WHERE task_type = %s AND model_id = %s AND shape_bucket = %s
                         ORDER BY observed_at DESC, id DESC
                         OFFSET 32
                     )
                    """,
                    (str(task_type)[:80], str(model_id)[:300], str(shape_bucket)[:300]),
                )
    return True


def resolve_gpu_vram_calibration(
    *,
    task_type: str,
    model_id: str,
    shape_bucket: str,
    now: float | None = None,
    max_age_seconds: float = 3600.0,
    url: str | None = None,
):
    """Resolve a conservative fresh calibration without accepting contended samples."""
    from .gpu_scheduler import GpuVramCalibration

    timestamp = time.time() if now is None else float(now)
    minimum_observed_at = timestamp - max(0.0, float(max_age_seconds))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT load_delta_mb, working_set_mb, EXTRACT(EPOCH FROM observed_at), tracker_overlapped
                  FROM gpu_vram_samples
                 WHERE task_type = %s AND model_id = %s AND shape_bucket = %s
                   AND allocator_capability = 'measured'
                   AND tracker_overlapped = false
                   AND observed_at >= to_timestamp(%s)
                 ORDER BY observed_at DESC, id DESC
                 LIMIT 32
                """,
                (str(task_type)[:80], str(model_id)[:300], str(shape_bucket)[:300], minimum_observed_at),
            )
            rows = list(cur.fetchall() or ())

    samples: list[tuple[int, int]] = []
    for row in rows:
        if isinstance(row, dict):
            load, working, observed_at, overlapped = row.get("load_delta_mb"), row.get("working_set_mb"), row.get("extract"), row.get("tracker_overlapped")
        else:
            load, working, observed_at, overlapped = row[:4]
        try:
            if bool(overlapped) or float(observed_at) < minimum_observed_at:
                continue
            samples.append((max(0, int(load)), max(0, int(working))))
        except (TypeError, ValueError):
            continue
    if len(samples) < 5:
        return GpuVramCalibration(sample_count=len(samples), source="insufficient_samples")
    # High-water is deliberately at least as conservative as the 90th percentile.
    loads = sorted(sample[0] for sample in samples)
    working_sets = sorted(sample[1] for sample in samples)
    percentile_index = min(len(samples) - 1, max(0, int(len(samples) * 0.9 + 0.999999) - 1))
    return GpuVramCalibration(
        load_delta_mb=max(loads[-1], loads[percentile_index]),
        working_set_mb=max(working_sets[-1], working_sets[percentile_index]),
        sample_count=len(samples),
        source="measured",
    )


def _database_check_payload(
    status: DatabaseStatus,
    *,
    target_url: str,
    label: str,
    required: bool,
    probe_url: str | None = None,
) -> dict[str, Any]:
    payload = {
        "ok": status.ok,
        "message": _sanitize_database_message(status.message, target_url, probe_url),
        "required": required,
        "label": label,
        "target": redact_database_url(target_url),
    }
    if probe_url is not None:
        payload["probe_target"] = redact_database_url(probe_url)
    return payload


def _check_database_for_report(url: str | None) -> DatabaseStatus:
    try:
        return check_database(url)
    except TypeError:
        return check_database()


def _sanitize_database_message(message: str, *urls: str | None) -> str:
    sanitized = str(message)
    if not redactions_enabled():
        return sanitized
    for url in urls:
        if not url:
            continue
        redacted_url = redact_database_url(url)
        if redacted_url:
            sanitized = sanitized.replace(url, redacted_url)
        try:
            password = urlsplit(url).password
        except Exception:
            password = None
        if password:
            sanitized = sanitized.replace(password, "***")
    return sanitized


def _host_database_probe_url(url: str | None) -> str | None:
    if not url:
        return None
    try:
        parts = urlsplit(url)
    except Exception:
        return url
    hostname = (parts.hostname or "").lower()
    if _running_in_container() and hostname in {"127.0.0.1", "localhost"}:
        return _replace_database_url_host(parts, "host.docker.internal")
    return url


def _running_in_container() -> bool:
    return os.environ.get("FLUX_KB_INSTALL_ROOT") == "/app" or os.environ.get("FLUX_KB_APP_ROOT") == "/app"


def _replace_database_url_host(parts, host: str) -> str:
    return _replace_database_url_auth(parts, host=host)


def _replace_database_url_auth(parts, *, host: str | None = None, password: str | None = None) -> str:
    username = parts.username
    next_password = parts.password if password is None else password
    hostname = host or parts.hostname
    if not hostname:
        return urlunsplit(parts)
    host_part = f"[{hostname}]" if ":" in hostname and not hostname.startswith("[") else hostname
    if parts.port:
        host_part = f"{host_part}:{parts.port}"
    if username is None:
        netloc = host_part
    elif next_password is None:
        netloc = f"{quote(username, safe='')}@{host_part}"
    else:
        netloc = f"{quote(username, safe='')}:{quote(next_password, safe='*')}@{host_part}"
    return urlunsplit((parts.scheme, netloc, parts.path, parts.query, parts.fragment))


def insert_episode(
    *,
    title: str,
    summary: str,
    source_kind: str = "manual",
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> str:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO episodes (title, summary, source_kind, metadata)
                VALUES (%s, %s, %s, %s::jsonb)
                RETURNING id::text
                """,
                (title, summary, source_kind, _json(metadata or {})),
            )
            episode_id = cur.fetchone()[0]
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('episode.remembered', 'episodes', %s, %s::jsonb)
                """,
                (episode_id, _json({"source_kind": source_kind})),
            )
            return episode_id


def search_episodes(
    query: str,
    *,
    limit: int = 5,
    cwd: str | None = None,
    root_path: str | None = None,
    workspace_key: str | None = None,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    limit = max(1, min(limit, 50))
    candidate_limit = max(limit * 4, 12)
    scope_sql, scope_params = _episode_scope_sql(
        "metadata->>'cwd'",
        "metadata->>'workspace_key'",
        cwd=cwd,
        root_path=root_path,
        workspace_key=workspace_key,
    )
    aliased_scope_sql, aliased_scope_params = _episode_scope_sql(
        "e.metadata->>'cwd'",
        "e.metadata->>'workspace_key'",
        cwd=cwd,
        root_path=root_path,
        workspace_key=workspace_key,
    )

    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            streams: dict[str, list[str]] = {}
            details: dict[str, dict[str, Any]] = {}

            cur.execute(
                f"""
                SELECT id::text, title, summary,
                       ts_rank(search_vector, plainto_tsquery('english', %s)) AS score,
                       jsonb_build_object('metadata', metadata) AS metadata
                FROM episodes
                WHERE search_vector @@ plainto_tsquery('english', %s)
                  {scope_sql}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM semantic_duplicate_members sm
                      JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                      WHERE sc.status = 'active'
                        AND sm.owner_table = 'episodes'
                        AND sm.owner_id = episodes.id
                        AND sm.member_role = 'duplicate'
                  )
                ORDER BY score DESC, updated_at DESC
                LIMIT %s
                """,
                (query, query, *scope_params, candidate_limit),
            )
            _add_ranked_rows("lexical", cur.fetchall(), streams, details)

            cur.execute(
                f"""
                SELECT id::text, title, summary,
                       greatest(similarity(title, %s), similarity(summary, %s)) AS score,
                       jsonb_build_object('metadata', metadata) AS metadata
                FROM episodes
                WHERE (similarity(title, %s) > 0.10 OR similarity(summary, %s) > 0.05)
                  {scope_sql}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM semantic_duplicate_members sm
                      JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                      WHERE sc.status = 'active'
                        AND sm.owner_table = 'episodes'
                        AND sm.owner_id = episodes.id
                        AND sm.member_role = 'duplicate'
                  )
                ORDER BY score DESC, updated_at DESC
                LIMIT %s
                """,
                (query, query, query, query, *scope_params, candidate_limit),
            )
            _add_ranked_rows("fuzzy", cur.fetchall(), streams, details)

            cur.execute(
                f"""
                SELECT e.id::text, e.title, e.summary,
                       max(ts_rank(c.search_vector, plainto_tsquery('english', %s))) AS score,
                       jsonb_build_object(
                         'metadata', e.metadata,
                         'lifecycle', jsonb_build_object(
                           'state', (array_agg(c.lifecycle_state ORDER BY c.updated_at DESC))[1],
                           'current', bool_or(c.lifecycle_state IN ('active', 'confirmed', 'reinforced')),
                           'audit_visible', bool_or(c.lifecycle_state IN ('superseded', 'contradicted', 'stale', 'retired'))
                         ),
                         'graph', jsonb_build_object(
                           'matched_claim_ids', jsonb_agg(DISTINCT c.id::text),
                           'entity_ids', jsonb_agg(DISTINCT c.subject_entity_id::text)
                         )
                       ) AS metadata
                FROM claims c
                JOIN episodes e ON e.id = c.episode_id
                WHERE c.search_vector @@ plainto_tsquery('english', %s)
                  {aliased_scope_sql}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM semantic_duplicate_members sm
                      JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                      WHERE sc.status = 'active'
                        AND sm.owner_table = 'episodes'
                        AND sm.owner_id = e.id
                        AND sm.member_role = 'duplicate'
                  )
                GROUP BY e.id, e.title, e.summary
                ORDER BY score DESC, max(c.updated_at) DESC
                LIMIT %s
                """,
                (query, query, *aliased_scope_params, candidate_limit),
            )
            _add_ranked_rows("claim_lifecycle", cur.fetchall(), streams, details)

            cur.execute(
                f"""
                WITH matched_entities AS (
                    SELECT DISTINCT subject_entity_id AS entity_id
                    FROM claims
                    WHERE search_vector @@ plainto_tsquery('english', %s)
                      AND subject_entity_id IS NOT NULL
                ),
                adjacent_entities AS (
                    SELECT r.to_entity_id AS entity_id
                    FROM relations r
                    JOIN matched_entities m ON m.entity_id = r.from_entity_id
                    WHERE r.lifecycle_state <> 'retired'
                    UNION
                    SELECT r.from_entity_id AS entity_id
                    FROM relations r
                    JOIN matched_entities m ON m.entity_id = r.to_entity_id
                    WHERE r.lifecycle_state <> 'retired'
                )
                SELECT e.id::text, e.title, e.summary,
                       max(c.confidence) AS score,
                       jsonb_build_object(
                         'metadata', e.metadata,
                         'graph', jsonb_build_object(
                           'matched_claim_ids', jsonb_agg(DISTINCT c.id::text),
                           'entity_ids', jsonb_agg(DISTINCT c.subject_entity_id::text)
                         )
                       ) AS metadata
                FROM adjacent_entities a
                JOIN claims c ON c.subject_entity_id = a.entity_id
                JOIN episodes e ON e.id = c.episode_id
                WHERE c.lifecycle_state IN ('active', 'confirmed', 'reinforced')
                  {aliased_scope_sql}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM semantic_duplicate_members sm
                      JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                      WHERE sc.status = 'active'
                        AND sm.owner_table = 'episodes'
                        AND sm.owner_id = e.id
                        AND sm.member_role = 'duplicate'
                  )
                GROUP BY e.id, e.title, e.summary
                ORDER BY score DESC, max(c.updated_at) DESC
                LIMIT %s
                """,
                (query, *aliased_scope_params, candidate_limit),
            )
            _add_ranked_rows("graph", cur.fetchall(), streams, details)

            if details:
                cur.execute(
                    """
                    SELECT id::text, confidence, usage_count,
                           superseded_by IS NOT NULL AS superseded,
                           lifecycle_state, contradiction_count, retention_action,
                           created_at, updated_at
                    FROM episodes
                    WHERE id = ANY(%s::uuid[])
                    """,
                    (list(details.keys()),),
                )
                _add_episode_lifecycle_rows(cur.fetchall(), details)
                _add_episode_claim_lifecycle_rows(cur, details)
                _add_semantic_duplicate_metadata(cur, owner_table="episodes", details=details)

    fused = reciprocal_rank_fusion(streams)
    results: list[dict[str, Any]] = []
    for item in fused:
        result = details[item.item_id]
        lifecycle = result.get("lifecycle") or {}
        lifecycle_score_value = float(lifecycle.get("score", 1.0) if isinstance(lifecycle, dict) else 1.0)
        adjusted_score = item.score * (0.5 + (0.5 * lifecycle_score_value))
        payload = {
            "id": item.item_id,
            "title": result["title"],
            "summary": result["summary"],
            "score": adjusted_score,
            "streams": list(item.streams),
            "raw_scores": result["raw_scores"],
        }
        if "lifecycle" in result:
            payload["lifecycle"] = result["lifecycle"]
        if "graph" in result:
            payload["graph"] = result["graph"]
        if "metadata" in result:
            payload["metadata"] = result["metadata"]
        if "semantic_duplicate_cluster" in result:
            payload["semantic_duplicate_cluster"] = result["semantic_duplicate_cluster"]
        results.append(payload)
    return sorted(results, key=lambda row: (-float(row["score"]), row["title"], row["id"]))[:limit]


def search_corpus_chunks(
    query: str,
    *,
    limit: int = 5,
    root_name: str | None = None,
    filters: dict[str, Any] | None = None,
    url: str | None = None,
    diagnostics: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    limit = max(1, min(limit, 50))
    candidate_limit = max(limit * 4, 12)
    hydration_limit = min(max(limit * 2, 20), 100)
    root_name_sql = "AND r.name = %s" if root_name else ""
    root_name_params: tuple[Any, ...] = (root_name,) if root_name else ()
    code_filter_sql, code_filter_params = _corpus_code_filter_sql(filters)

    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            streams: dict[str, list[str]] = {}
            raw_scores: dict[str, dict[str, float]] = {}
            root_id = _corpus_root_id(cur, root_name=root_name) if root_name else None

            started = time.perf_counter()
            cur.execute(
                f"""
                SELECT c.id::text,
                       ts_rank(c.search_vector, plainto_tsquery('english', %s)) AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND a.extraction_status = 'indexed'
                  AND c.search_vector @@ plainto_tsquery('english', %s)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM semantic_duplicate_members sm
                      JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                      WHERE sc.status = 'active'
                        AND sm.owner_table = 'asset_chunks'
                        AND sm.owner_id = c.id
                        AND sm.member_role = 'duplicate'
                  )
                  {root_name_sql}
                  {code_filter_sql}
                ORDER BY score DESC, c.updated_at DESC
                LIMIT %s
                """,
                (query, query, *root_name_params, *code_filter_params, candidate_limit),
            )
            rows = cur.fetchall()
            _add_ranked_corpus_candidates("corpus_lexical", rows, streams, raw_scores)
            _record_corpus_stream_diagnostics(
                diagnostics,
                "corpus_lexical",
                started,
                rows=len(rows),
                plan="tsquery_candidates",
            )

            if root_name and root_id is None:
                _record_corpus_stream_diagnostics(
                    diagnostics,
                    "postgres_metadata_diagnostic",
                    time.perf_counter(),
                    rows=0,
                    plan="root_not_found",
                )

            started = time.perf_counter()
            sidecar_rows = _search_mail_sidecar_rows(cur, query=query, root_name=root_name, filters=filters, limit=candidate_limit)
            rows = [(row[0], row[8]) for row in sidecar_rows]
            _add_ranked_corpus_candidates("mail_sidecar", rows, streams, raw_scores)
            _record_corpus_stream_diagnostics(
                diagnostics,
                "mail_sidecar",
                started,
                rows=len(rows),
                plan="sidecar_candidates",
            )

            if _filters_request_code_focus(filters):
                cur.execute("SELECT set_config('pg_trgm.similarity_threshold', %s, true)", ("0.20",))
                started = time.perf_counter()
                cur.execute(
                    f"""
                    SELECT c.id::text,
                           max(CASE
                               WHEN lower(cs.qualified_name) = lower(%s) THEN 3.0
                               WHEN lower(cs.name) = lower(%s) THEN 2.5
                               WHEN cs.qualified_name ILIKE %s THEN 2.0
                               WHEN cs.name ILIKE %s THEN 1.8
                               ELSE greatest(similarity(cs.qualified_name, %s), similarity(cs.name, %s))
                           END) AS score,
                           c.updated_at
                    FROM code_symbols cs
                    JOIN asset_chunks c ON c.id = cs.asset_chunk_id
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE r.enabled
                      AND a.deleted_at IS NULL
                      AND a.canonical_asset_id IS NULL
                      AND a.extraction_status = 'indexed'
                      AND (
                          cs.qualified_name ILIKE %s
                          OR cs.name ILIKE %s
                          OR cs.qualified_name %% %s
                          OR cs.name %% %s
                      )
                      {root_name_sql}
                      {code_filter_sql}
                    GROUP BY c.id, c.updated_at
                    ORDER BY score DESC, c.updated_at DESC
                    LIMIT %s
                    """,
                    (
                        query,
                        query,
                        f"%{query}%",
                        f"%{query}%",
                        query,
                        query,
                        f"%{query}%",
                        f"%{query}%",
                        query,
                        query,
                        *root_name_params,
                        *code_filter_params,
                        candidate_limit,
                    ),
                )
                rows = [(row[0], row[1]) for row in cur.fetchall()]
                _add_ranked_corpus_candidates("code_symbol_exact", rows, streams, raw_scores)
                _record_corpus_stream_diagnostics(
                    diagnostics,
                    "code_symbol_exact",
                    started,
                    rows=len(rows),
                    plan="trigram_symbol_candidates",
                )

            if len(raw_scores) >= limit:
                streams["corpus_fuzzy"] = []
                if diagnostics is not None:
                    diagnostics.setdefault("streams", {})["corpus_fuzzy"] = {
                        "duration_ms": 0.0,
                        "rows": 0,
                        "plan": "skipped_sufficient_candidates",
                        "candidate_limit": candidate_limit,
                        "available_candidates": len(raw_scores),
                    }
            else:
                cur.execute("SELECT set_config('pg_trgm.similarity_threshold', %s, true)", ("0.10",))
                started = time.perf_counter()
                cur.execute(
                    f"""
                    SELECT c.id::text,
                           greatest(similarity(c.title, %s), similarity(a.path, %s)) AS score,
                           c.updated_at
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE r.enabled
                      AND a.deleted_at IS NULL
                      AND a.canonical_asset_id IS NULL
                      AND a.extraction_status = 'indexed'
                      AND (c.title %% %s OR a.path %% %s)
                      AND NOT EXISTS (
                          SELECT 1
                          FROM semantic_duplicate_members sm
                          JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                          WHERE sc.status = 'active'
                            AND sm.owner_table = 'asset_chunks'
                            AND sm.owner_id = c.id
                            AND sm.member_role = 'duplicate'
                      )
                      {root_name_sql}
                      {code_filter_sql}
                    ORDER BY score DESC, c.updated_at DESC
                    LIMIT %s
                    """,
                    (query, query, query, query, *root_name_params, *code_filter_params, candidate_limit),
                )
                rows = [(row[0], row[1]) for row in cur.fetchall()]
                _add_ranked_corpus_candidates("corpus_fuzzy", rows, streams, raw_scores)
                _record_corpus_stream_diagnostics(
                    diagnostics,
                    "corpus_fuzzy",
                    started,
                    rows=len(rows),
                    plan="trigram_candidates",
                )

            details: dict[str, dict[str, Any]] = {}
            if raw_scores:
                primary_fused = reciprocal_rank_fusion(streams)
                code_symbol_backfill_limit = 10 if _has_explicit_code_focus(query, filters) else 0
                hydrated_candidate_ids = _corpus_hydration_candidate_ids(
                    primary_fused,
                    streams=streams,
                    hydration_limit=hydration_limit,
                    code_symbol_backfill_limit=code_symbol_backfill_limit,
                )
                started = time.perf_counter()
                details = _hydrate_corpus_candidate_details(
                    cur,
                    candidate_ids=hydrated_candidate_ids,
                    root_name=root_name,
                    filters=filters,
                    raw_scores=raw_scores,
                )
                _record_corpus_stream_diagnostics(
                    diagnostics,
                    "corpus_hydration",
                    started,
                    rows=len(details),
                    plan="single_detail_query",
                    candidate_limit=hydration_limit,
                )
                _add_ranked_corpus_candidates(
                    "corpus_trust",
                    sorted(
                        ((item_id, detail.get("_trust_score", 0.0)) for item_id, detail in details.items()),
                        key=lambda row: (-float(row[1] or 0.0), row[0]),
                    ),
                    streams,
                    raw_scores,
                )
                _add_ranked_corpus_candidates(
                    "corpus_freshness",
                    sorted(
                        ((item_id, detail.get("_freshness_score", 0.0)) for item_id, detail in details.items()),
                        key=lambda row: (-float(row[1] or 0.0), row[0]),
                    ),
                    streams,
                    raw_scores,
                )
                for item_id, detail in details.items():
                    detail["raw_scores"] = dict(raw_scores.get(item_id, {}))
                _add_semantic_duplicate_metadata(cur, owner_table="asset_chunks", details=details)

    fused = _rank_corpus_candidates(query, streams=streams, details=details, filters=filters)
    results: list[dict[str, Any]] = []
    for item in fused[:limit]:
        if item.item_id not in details:
            continue
        result = details[item.item_id]
        results.append(
            {
                "id": item.item_id,
                "title": result["title"],
                "summary": result["summary"],
                "score": item.score,
                "streams": list(item.streams),
                "raw_scores": result["raw_scores"],
                "asset_id": result["asset_id"],
                "source_path": result["source_path"],
                "root_name": result["root_name"],
                "duplicate_count": result["duplicate_count"],
                "trust_rank": result["trust_rank"],
                "raw_scores": result["raw_scores"],
            }
        )
        if result.get("file_kind") is not None:
            results[-1]["file_kind"] = result["file_kind"]
        if isinstance(result.get("code"), dict):
            results[-1]["code"] = result["code"]
        if "semantic_duplicate_cluster" in result:
            results[-1]["semantic_duplicate_cluster"] = result["semantic_duplicate_cluster"]
    return results


def search_corpus_chunks_postgres_diagnostic(
    query: str,
    *,
    limit: int = 5,
    root_name: str | None = None,
    filters: dict[str, Any] | None = None,
    url: str | None = None,
    diagnostics: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    """Lexical-only degraded retrieval when Vespa or model-runner is unavailable."""
    psycopg = _load_psycopg()
    limit = max(1, min(limit, 50))
    candidate_limit = max(limit * 4, 12)
    root_name_sql = "AND r.name = %s" if root_name else ""
    root_name_params: tuple[Any, ...] = (root_name,) if root_name else ()
    code_filter_sql, code_filter_params = _corpus_code_filter_sql(filters)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            streams: dict[str, list[str]] = {}
            raw_scores: dict[str, dict[str, float]] = {}
            started = time.perf_counter()
            cur.execute(
                f"""
                SELECT c.id::text,
                       ts_rank(c.search_vector, plainto_tsquery('english', %s)) AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND a.extraction_status = 'indexed'
                  AND c.search_vector @@ plainto_tsquery('english', %s)
                  AND NOT EXISTS (
                      SELECT 1
                      FROM semantic_duplicate_members sm
                      JOIN semantic_duplicate_clusters sc ON sc.id = sm.cluster_id
                      WHERE sc.status = 'active'
                        AND sm.owner_table = 'asset_chunks'
                        AND sm.owner_id = c.id
                        AND sm.member_role = 'duplicate'
                  )
                  {root_name_sql}
                  {code_filter_sql}
                ORDER BY score DESC, c.updated_at DESC
                LIMIT %s
                """,
                (query, query, *root_name_params, *code_filter_params, candidate_limit),
            )
            rows = cur.fetchall()
            _add_ranked_corpus_candidates("postgres_lexical_diagnostic", rows, streams, raw_scores)
            _record_corpus_stream_diagnostics(
                diagnostics,
                "postgres_lexical_diagnostic",
                started,
                rows=len(rows),
                plan="degraded_tsquery_only",
            )
            started = time.perf_counter()
            path_pattern = f"%{query}%"
            cur.execute(
                f"""
                SELECT c.id::text,
                       CASE
                         WHEN lower(c.title) = lower(%s) THEN 1.0
                         WHEN lower(a.path) = lower(%s) THEN 0.95
                         WHEN c.title ILIKE %s THEN 0.65
                         WHEN a.path ILIKE %s THEN 0.55
                         ELSE 0.0
                       END AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND a.extraction_status = 'indexed'
                  AND (c.title ILIKE %s OR a.path ILIKE %s)
                  {root_name_sql}
                  {code_filter_sql}
                ORDER BY score DESC, c.updated_at DESC
                LIMIT %s
                """,
                (
                    query,
                    query,
                    path_pattern,
                    path_pattern,
                    path_pattern,
                    path_pattern,
                    *root_name_params,
                    *code_filter_params,
                    candidate_limit,
                ),
            )
            rows = cur.fetchall()
            _add_ranked_corpus_candidates("postgres_metadata_diagnostic", rows, streams, raw_scores)
            _record_corpus_stream_diagnostics(
                diagnostics,
                "postgres_metadata_diagnostic",
                started,
                rows=len(rows),
                plan="degraded_title_path_only",
            )
            details: dict[str, dict[str, Any]] = {}
            if raw_scores:
                fused = reciprocal_rank_fusion(streams)
                candidate_ids = _corpus_hydration_candidate_ids(
                    fused,
                    streams=streams,
                    hydration_limit=min(max(limit * 2, 20), 100),
                    code_symbol_backfill_limit=0,
                )
                details = _hydrate_corpus_candidate_details(
                    cur,
                    candidate_ids=candidate_ids,
                    root_name=root_name,
                    filters=filters,
                    raw_scores=raw_scores,
                )
                for item_id, detail in details.items():
                    detail["raw_scores"] = dict(raw_scores.get(item_id, {}))
                _add_semantic_duplicate_metadata(cur, owner_table="asset_chunks", details=details)
    fused = _rank_corpus_candidates(query, streams=streams, details=details, filters=filters)
    return _corpus_results_from_fused(fused[:limit], details)


def _vespa_float(value: Any) -> float | None:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _vespa_match_feature(match_features: dict[str, Any], *names: str) -> float | None:
    for name in names:
        value = _vespa_float(match_features.get(name))
        if value is not None:
            return value
    return None


def _vespa_lexical_score(match_features: dict[str, Any]) -> float:
    direct = _vespa_match_feature(match_features, "lexical_score")
    if direct is not None:
        return max(0.0, direct)
    total = 0.0
    found = False
    for feature in _VESPA_BM25_FEATURES:
        value = _vespa_match_feature(match_features, feature)
        if value is None:
            continue
        found = True
        total += value
    return max(0.0, total) if found else 0.0


def _vespa_dense_score(match_features: dict[str, Any]) -> float:
    value = _vespa_match_feature(match_features, *_VESPA_DENSE_FEATURES)
    return max(0.0, value or 0.0)


def _vespa_streams_from_raw_scores(raw_scores: dict[str, Any], extra_streams: Iterable[str] | None = None) -> list[str]:
    streams: list[str] = []
    for stream in (_VESPA_RRF_STREAM, _VESPA_LEXICAL_STREAM, _VESPA_DENSE_STREAM, "vespa_hybrid"):
        if stream in raw_scores and stream not in streams:
            streams.append(stream)
    for stream in extra_streams or ():
        normalized = str(stream)
        if normalized and normalized not in streams:
            streams.append(normalized)
    for stream in sorted(str(stream) for stream in raw_scores):
        if stream not in streams:
            streams.append(stream)
    return streams


def _vespa_candidate_signals(candidate: dict[str, Any], *, query_mode: str) -> tuple[dict[str, float], list[str]]:
    if query_mode == _VESPA_LEXICAL_QUERY_MODE:
        raw_scores = {_VESPA_LEXICAL_STREAM: float(candidate.get("score") or 0.0)}
        return raw_scores, _vespa_streams_from_raw_scores(raw_scores)
    match_features = candidate.get("match_features") if isinstance(candidate.get("match_features"), dict) else {}
    raw_scores = {_VESPA_RRF_STREAM: float(candidate.get("score") or 0.0)}
    lexical_score = _vespa_lexical_score(match_features)
    dense_score = _vespa_dense_score(match_features)
    if lexical_score > 0:
        raw_scores[_VESPA_LEXICAL_STREAM] = lexical_score
    if dense_score > 0:
        raw_scores[_VESPA_DENSE_STREAM] = dense_score
    return raw_scores, _vespa_streams_from_raw_scores(raw_scores)


def _merge_vespa_rrf_candidates(
    candidates: list[dict[str, Any]],
    *,
    query_mode: str = _VESPA_RRF_QUERY_MODE,
) -> list[dict[str, Any]]:
    merged: dict[tuple[str, str], dict[str, Any]] = {}
    order: list[tuple[str, str]] = []
    for candidate in candidates:
        owner_table = str(candidate.get("owner_table") or "")
        owner_id = str(candidate.get("owner_id") or "")
        if owner_table not in _VESPA_VALID_OWNER_TABLES or not owner_id:
            continue
        raw_scores, streams = _vespa_candidate_signals(candidate, query_mode=query_mode)
        score = float(candidate.get("score") or 0.0)
        key = (owner_table, owner_id)
        if key not in merged:
            row = dict(candidate)
            row["score"] = score
            row["raw_scores"] = raw_scores
            row["streams"] = streams
            row["match_features"] = dict(candidate.get("match_features") or {})
            merged[key] = row
            order.append(key)
            continue
        row = merged[key]
        if score > float(row.get("score") or 0.0):
            for field in ("title", "root_name", "source_path"):
                if candidate.get(field):
                    row[field] = candidate[field]
            row["score"] = score
        row_features = row.setdefault("match_features", {})
        if isinstance(row_features, dict):
            row_features.update(candidate.get("match_features") or {})
        row_raw_scores = row.setdefault("raw_scores", {})
        for stream, value in raw_scores.items():
            row_raw_scores[stream] = max(float(row_raw_scores.get(stream) or 0.0), value)
        row["streams"] = _vespa_streams_from_raw_scores(row_raw_scores, [*row.get("streams", []), *streams])
    order_index = {key: index for index, key in enumerate(order)}
    return sorted(
        (merged[key] for key in order),
        key=lambda row: (
            -float(row.get("score") or 0.0),
            order_index.get((str(row.get("owner_table") or ""), str(row.get("owner_id") or "")), 0),
        ),
    )


def _vespa_stream_counts(candidates: list[dict[str, Any]]) -> dict[str, int]:
    lexical = 0
    dense = 0
    overlap = 0
    for candidate in candidates:
        raw_scores = candidate.get("raw_scores") if isinstance(candidate.get("raw_scores"), dict) else {}
        has_lexical = _VESPA_LEXICAL_STREAM in raw_scores
        has_dense = _VESPA_DENSE_STREAM in raw_scores
        lexical += int(has_lexical)
        dense += int(has_dense)
        overlap += int(has_lexical and has_dense)
    return {"lexical": lexical, "dense": dense, "overlap": overlap}


def _vespa_owner_counts(candidates: list[dict[str, Any]]) -> dict[str, int]:
    owner_counts: dict[str, int] = {}
    for candidate in candidates:
        owner_table = str(candidate.get("owner_table") or "")
        if owner_table not in _VESPA_VALID_OWNER_TABLES:
            continue
        owner_counts[owner_table] = owner_counts.get(owner_table, 0) + 1
    return owner_counts


def _vespa_grouped_ids(candidates: list[dict[str, Any]]) -> dict[str, list[str]]:
    grouped_ids: dict[str, list[str]] = {"asset_chunks": [], "episodes": [], "claims": []}
    for candidate in candidates:
        owner_table = str(candidate.get("owner_table") or "")
        owner_id = str(candidate.get("owner_id") or "")
        if owner_table in grouped_ids and owner_id:
            grouped_ids[owner_table].append(owner_id)
    return grouped_ids


def _vespa_raw_scores_by_owner(candidates: list[dict[str, Any]]) -> dict[str, dict[str, float]]:
    return {
        str(candidate.get("owner_id")): dict(candidate.get("raw_scores") or {})
        for candidate in candidates
        if candidate.get("owner_id")
    }


def _with_vespa_signal_streams(item: dict[str, Any]) -> dict[str, Any]:
    raw_scores = item.get("raw_scores") if isinstance(item.get("raw_scores"), dict) else {}
    if not any(stream in raw_scores for stream in (_VESPA_RRF_STREAM, _VESPA_LEXICAL_STREAM, _VESPA_DENSE_STREAM)):
        return item
    result = dict(item)
    result["streams"] = _vespa_streams_from_raw_scores(raw_scores, result.get("streams", []))
    return result


def search_corpus_chunks_vespa(
    query: str,
    *,
    limit: int = 5,
    root_name: str | None = None,
    filters: dict[str, Any] | None = None,
    vespa_base_url: str | None = None,
    rerank_limit: int | None = None,
    rerank_deadline: float | None = None,
    diagnostics: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    from .reranking import QwenReranker
    from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL, VespaSearchAdapter

    limit = max(1, min(limit, 50))
    file_kinds = list(filters.get("file_kinds") or []) if isinstance(filters, dict) else []
    languages = list(filters.get("languages") or []) if isinstance(filters, dict) else []
    started = time.perf_counter()
    embedding_failure = None
    try:
        embedding_result, embedding_elapsed_ms, embedding_cache_hit = _embed_query_for_retrieval(
            query,
            model=SNOWFLAKE_EMBEDDING_MODEL,
            dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
        )
    except Exception as exc:
        embedding_elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
        embedding_failure = _classify_retryable_vespa_embedding_failure(exc)
        if embedding_failure is None or not _configured_vespa_lexical_fallback_enabled():
            raise
        embedding_result = None
        embedding_cache_hit = False
    query_mode = _VESPA_LEXICAL_QUERY_MODE if embedding_result is None else _VESPA_RRF_QUERY_MODE
    rank_profile = _VESPA_LEXICAL_RANK_PROFILE if embedding_result is None else _VESPA_RRF_RANK_PROFILE
    candidate_stream = _VESPA_LEXICAL_STREAM if embedding_result is None else _VESPA_RRF_STREAM
    candidate_limit = min(max(limit * 8, 80), 200)
    query_started = time.perf_counter()
    resolved_vespa_base_url = vespa_base_url or "http://127.0.0.1:8080"
    candidates = VespaSearchAdapter(base_url=resolved_vespa_base_url).query(
        query,
        embedding=embedding_result.vector if embedding_result is not None else None,
        root_name=root_name,
        file_kinds=file_kinds,
        languages=languages,
        limit=candidate_limit,
        rank_profile=rank_profile,
    )
    query_elapsed_ms = max(0, int((time.perf_counter() - query_started) * 1000))
    fused_candidates = _merge_vespa_rrf_candidates(candidates, query_mode=query_mode)
    _record_corpus_stream_diagnostics(
        diagnostics,
        candidate_stream,
        started,
        rows=len(fused_candidates),
        plan="vespa_lexical_candidates" if embedding_result is None else "vespa_hybrid_rrf_candidates",
        candidate_limit=candidate_limit,
    )
    match_feature_keys = sorted(
        {
            str(key)
            for item in candidates
            if isinstance(item.get("match_features"), dict)
            for key in item["match_features"].keys()
        }
    )
    if diagnostics is not None:
        diagnostics["vespa"] = {
            "search_engine": "vespa",
            "base_url": resolved_vespa_base_url,
            "rank_profile": rank_profile,
            "query_mode": query_mode,
            "rrf_k": _VESPA_RRF_K if embedding_result is not None else None,
            "candidate_limit": candidate_limit,
            "candidate_count": len(candidates),
            "fused_candidate_count": len(fused_candidates),
            "stream_counts": _vespa_stream_counts(fused_candidates),
            "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
            "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
            "embedding_latency_ms": embedding_elapsed_ms,
            "embedding_cache_hit": embedding_cache_hit,
            "embedding_failure_class": embedding_failure[0] if embedding_failure else None,
            "embedding_capacity_state": embedding_failure[1] if embedding_failure else None,
            "query_latency_ms": query_elapsed_ms,
            "match_feature_keys": match_feature_keys,
        }
    candidate_ids = [
        str(item.get("owner_id") or "")
        for item in fused_candidates
        if str(item.get("owner_table") or "") == "asset_chunks"
    ]
    raw_scores = _vespa_raw_scores_by_owner(fused_candidates)
    streams = {candidate_stream: candidate_ids}
    if not candidate_ids:
        if diagnostics is not None:
            diagnostics["vespa"]["hydrated_count"] = 0
            diagnostics["vespa"]["returned_count"] = 0
        return []
    psycopg = _load_psycopg()
    hydrate_started = time.perf_counter()
    with psycopg.connect(database_url()) as conn:
        with conn.cursor() as cur:
            details = _hydrate_corpus_candidate_details(
                cur,
                candidate_ids=candidate_ids[: min(max(limit * 4, 20), 200)],
                root_name=root_name,
                filters=filters,
                raw_scores=raw_scores,
            )
            for item_id, detail in details.items():
                detail["raw_scores"] = dict(raw_scores.get(item_id, {}))
            _add_semantic_duplicate_metadata(cur, owner_table="asset_chunks", details=details)
    hydrate_elapsed_ms = max(0, int((time.perf_counter() - hydrate_started) * 1000))
    if diagnostics is not None:
        diagnostics["vespa"]["hydrated_count"] = len(details)
        diagnostics["vespa"]["hydration_latency_ms"] = hydrate_elapsed_ms
    fused = _rank_corpus_candidates(query, streams=streams, details=details, filters=filters)
    rerank_pool_limit = _rerank_pool_limit(rerank_limit=rerank_limit, result_limit=limit, available_count=len(fused))
    hydrated = [
        _with_vespa_signal_streams(item)
        for item in _corpus_results_from_fused(fused[:rerank_pool_limit], details)
    ]
    reranker = QwenReranker(top_n=rerank_pool_limit, deadline=rerank_deadline)
    rerank_started = time.perf_counter()
    try:
        reranked = reranker.rerank(query, hydrated)
    except Exception as exc:
        reason = _rerank_fallback_reason(exc)
        if reason is None:
            raise
        rerank_elapsed_ms = max(0, int((time.perf_counter() - rerank_started) * 1000))
        fallback, fallback_name, budget_metadata = _rerank_fallback_results(exc, candidates=hydrated, limit=limit)
        _record_skipped_reranker_diagnostics(
            diagnostics,
            reranker=reranker,
            candidates=hydrated,
            returned_count=len(fallback),
            latency_ms=rerank_elapsed_ms,
            reason=reason,
            fallback=fallback_name,
            extra=budget_metadata,
        )
        if diagnostics is not None:
            diagnostics["vespa"]["returned_count"] = len(fallback)
        return fallback
    rerank_elapsed_ms = max(0, int((time.perf_counter() - rerank_started) * 1000))
    if diagnostics is not None:
        diagnostics["reranker"] = _reranker_base_diagnostics(
            reranker,
            hydrated,
            returned_count=min(limit, len(reranked)),
            latency_ms=rerank_elapsed_ms,
        )
        diagnostics["vespa"]["returned_count"] = min(limit, len(reranked))
    return reranked[:limit]


def search_evidence_vespa(
    query: str,
    *,
    limit: int = 5,
    root_name: str | None = None,
    filters: dict[str, Any] | None = None,
    vespa_base_url: str | None = None,
    rerank_limit: int | None = None,
    rerank_deadline: float | None = None,
    diagnostics: dict[str, Any] | None = None,
) -> list[dict[str, Any]]:
    from .reranking import QwenReranker
    from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL, VespaSearchAdapter

    limit = max(1, min(limit, 50))
    file_kinds = list(filters.get("file_kinds") or []) if isinstance(filters, dict) else []
    languages = list(filters.get("languages") or []) if isinstance(filters, dict) else []
    started = time.perf_counter()
    embedding_failure = None
    try:
        embedding_result, embedding_elapsed_ms, embedding_cache_hit = _embed_query_for_retrieval(
            query,
            model=SNOWFLAKE_EMBEDDING_MODEL,
            dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
        )
    except Exception as exc:
        embedding_elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
        embedding_failure = _classify_retryable_vespa_embedding_failure(exc)
        if embedding_failure is None or not _configured_vespa_lexical_fallback_enabled():
            raise
        embedding_result = None
        embedding_cache_hit = False
    query_mode = _VESPA_LEXICAL_QUERY_MODE if embedding_result is None else _VESPA_RRF_QUERY_MODE
    rank_profile = _VESPA_LEXICAL_RANK_PROFILE if embedding_result is None else _VESPA_RRF_RANK_PROFILE
    candidate_stream = _VESPA_LEXICAL_STREAM if embedding_result is None else _VESPA_RRF_STREAM
    candidate_limit = min(max(limit * 8, 80), 200)
    query_started = time.perf_counter()
    resolved_vespa_base_url = vespa_base_url or "http://127.0.0.1:8080"
    candidates = VespaSearchAdapter(base_url=resolved_vespa_base_url).query(
        query,
        embedding=embedding_result.vector if embedding_result is not None else None,
        root_name=root_name,
        file_kinds=file_kinds,
        languages=languages,
        limit=candidate_limit,
        rank_profile=rank_profile,
    )
    query_elapsed_ms = max(0, int((time.perf_counter() - query_started) * 1000))
    fused_candidates = _merge_vespa_rrf_candidates(candidates, query_mode=query_mode)
    _record_corpus_stream_diagnostics(
        diagnostics,
        candidate_stream,
        started,
        rows=len(fused_candidates),
        plan="vespa_lexical_all_evidence" if embedding_result is None else "vespa_hybrid_rrf_all_evidence",
        candidate_limit=candidate_limit,
    )
    match_feature_keys = sorted(
        {
            str(key)
            for item in candidates
            if isinstance(item.get("match_features"), dict)
            for key in item["match_features"].keys()
        }
    )
    owner_counts = _vespa_owner_counts(fused_candidates)
    grouped_ids = _vespa_grouped_ids(fused_candidates)
    raw_scores = _vespa_raw_scores_by_owner(fused_candidates)
    if diagnostics is not None:
        diagnostics["vespa"] = {
            "search_engine": "vespa",
            "base_url": resolved_vespa_base_url,
            "rank_profile": rank_profile,
            "query_mode": query_mode,
            "rrf_k": _VESPA_RRF_K if embedding_result is not None else None,
            "candidate_limit": candidate_limit,
            "candidate_count": len(candidates),
            "fused_candidate_count": len(fused_candidates),
            "stream_counts": _vespa_stream_counts(fused_candidates),
            "owner_counts": owner_counts,
            "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
            "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
            "embedding_latency_ms": embedding_elapsed_ms,
            "embedding_cache_hit": embedding_cache_hit,
            "embedding_failure_class": embedding_failure[0] if embedding_failure else None,
            "embedding_capacity_state": embedding_failure[1] if embedding_failure else None,
            "query_latency_ms": query_elapsed_ms,
            "match_feature_keys": match_feature_keys,
        }
    if not any(grouped_ids.values()):
        if diagnostics is not None:
            diagnostics["vespa"]["hydrated_count"] = 0
            diagnostics["vespa"]["returned_count"] = 0
        return []

    hydrate_started = time.perf_counter()
    psycopg = _load_psycopg()
    with psycopg.connect(database_url()) as conn:
        with conn.cursor() as cur:
            corpus_details = _hydrate_corpus_candidate_details(
                cur,
                candidate_ids=grouped_ids["asset_chunks"][: min(max(limit * 4, 20), 200)],
                root_name=root_name,
                filters=filters,
                raw_scores=raw_scores,
            )
            for item_id, detail in corpus_details.items():
                detail["raw_scores"] = dict(raw_scores.get(item_id, {}))
            _add_semantic_duplicate_metadata(cur, owner_table="asset_chunks", details=corpus_details)
            episode_details = _hydrate_episode_candidate_details(
                cur,
                candidate_ids=grouped_ids["episodes"][: min(max(limit * 4, 20), 200)],
                root_name=root_name,
                raw_scores=raw_scores,
            )
            _add_semantic_duplicate_metadata(cur, owner_table="episodes", details=episode_details)
            claim_details = _hydrate_claim_candidate_details(
                cur,
                candidate_ids=grouped_ids["claims"][: min(max(limit * 4, 20), 200)],
                root_name=root_name,
                raw_scores=raw_scores,
            )
            _add_semantic_duplicate_metadata(cur, owner_table="claims", details=claim_details)
    hydrate_elapsed_ms = max(0, int((time.perf_counter() - hydrate_started) * 1000))

    results: list[dict[str, Any]] = []
    seen: set[tuple[str, str]] = set()
    if _filters_request_code_focus(filters):
        corpus_stream_ids = [item_id for item_id in grouped_ids["asset_chunks"] if item_id in corpus_details]
        corpus_ranked = _rank_corpus_candidates(
            query,
            streams={candidate_stream: corpus_stream_ids},
            details=corpus_details,
            filters=filters,
        )
        for ranked_item in corpus_ranked:
            payloads = _corpus_results_from_fused([ranked_item], corpus_details)
            if not payloads:
                continue
            results.append({"kind": "corpus_chunk", **_with_vespa_signal_streams(payloads[0])})
            seen.add(("asset_chunks", ranked_item.item_id))
    for item in fused_candidates:
        owner_table = str(item.get("owner_table") or "")
        owner_id = str(item.get("owner_id") or "")
        key = (owner_table, owner_id)
        if key in seen:
            continue
        seen.add(key)
        score = float(item.get("score") or 0.0)
        if owner_table == "asset_chunks" and owner_id in corpus_details:
            payloads = _corpus_results_from_fused(
                [RankedItem(item_id=owner_id, score=score, streams=tuple(item.get("streams") or (candidate_stream,)))],
                corpus_details,
            )
            if payloads:
                results.append({"kind": "corpus_chunk", **_with_vespa_signal_streams(payloads[0])})
        elif owner_table == "episodes" and owner_id in episode_details:
            results.append(_evidence_detail_payload("episode", episode_details[owner_id], score=score, streams=item.get("streams")))
        elif owner_table == "claims" and owner_id in claim_details:
            results.append(_evidence_detail_payload("claim", claim_details[owner_id], score=score, streams=item.get("streams")))

    if diagnostics is not None:
        diagnostics["vespa"]["hydrated_count"] = len(results)
        diagnostics["vespa"]["hydration_latency_ms"] = hydrate_elapsed_ms
    rerank_pool_limit = _rerank_pool_limit(rerank_limit=rerank_limit, result_limit=limit, available_count=len(results))
    rerank_candidates = results[:rerank_pool_limit]
    reranker = QwenReranker(top_n=rerank_pool_limit, deadline=rerank_deadline)
    rerank_started = time.perf_counter()
    try:
        reranked = _sort_code_adjusted_reranked_results(reranker.rerank(query, rerank_candidates))
    except Exception as exc:
        reason = _rerank_fallback_reason(exc)
        if reason is None:
            raise
        rerank_elapsed_ms = max(0, int((time.perf_counter() - rerank_started) * 1000))
        fallback, fallback_name, budget_metadata = _rerank_fallback_results(exc, candidates=rerank_candidates, limit=limit)
        _record_skipped_reranker_diagnostics(
            diagnostics,
            reranker=reranker,
            candidates=rerank_candidates,
            returned_count=len(fallback),
            latency_ms=rerank_elapsed_ms,
            reason=reason,
            fallback=fallback_name,
            extra=budget_metadata,
        )
        if diagnostics is not None:
            diagnostics["vespa"]["returned_count"] = len(fallback)
        return fallback
    rerank_elapsed_ms = max(0, int((time.perf_counter() - rerank_started) * 1000))
    if diagnostics is not None:
        diagnostics["reranker"] = _reranker_base_diagnostics(
            reranker,
            rerank_candidates,
            returned_count=min(limit, len(reranked)),
            latency_ms=rerank_elapsed_ms,
        )
        diagnostics["vespa"]["returned_count"] = min(limit, len(reranked))
    return reranked[:limit]


def _rerank_pool_limit(*, rerank_limit: int | None, result_limit: int, available_count: int) -> int:
    if available_count <= 0:
        return 1
    try:
        requested_limit = int(rerank_limit if rerank_limit is not None else result_limit)
    except (TypeError, ValueError):
        requested_limit = int(result_limit or 5)
    requested_limit = max(1, min(requested_limit, 50))
    target = max(requested_limit * _RERANK_POOL_LIMIT_MULTIPLIER, _MIN_RERANK_POOL_SIZE)
    return max(1, min(int(available_count), _configured_rerank_top_n(), target))


def _configured_rerank_top_n() -> int:
    try:
        from .settings import SettingsService

        value = SettingsService().resolve("retrieval.rerank_top_n").raw_value
    except Exception:
        value = _DEFAULT_RERANK_POOL_TOP_N
    try:
        return max(1, min(int(value or _DEFAULT_RERANK_POOL_TOP_N), 200))
    except (TypeError, ValueError):
        return _DEFAULT_RERANK_POOL_TOP_N


def _clear_query_embedding_cache_for_tests() -> None:
    _QUERY_EMBEDDING_CACHE.clear()


def _retrieval_int_setting(key: str, default: int, *, minimum: int, maximum: int) -> int:
    try:
        from .settings import SettingsService

        value = SettingsService().resolve(key).raw_value
    except Exception:
        value = default
    try:
        return max(minimum, min(int(value), maximum))
    except (TypeError, ValueError):
        return default


def _configured_embedding_wait_timeout_seconds() -> float:
    return float(
        _retrieval_int_setting(
            "retrieval.embedding_wait_timeout_seconds",
            _DEFAULT_EMBEDDING_WAIT_TIMEOUT_SECONDS,
            minimum=1,
            maximum=600,
        )
    )


def _configured_vespa_lexical_fallback_enabled() -> bool:
    try:
        from .settings import SettingsService

        value = SettingsService().resolve("retrieval.vespa_lexical_fallback_enabled").raw_value
    except Exception:
        return True
    if isinstance(value, str):
        return value.strip().lower() not in {"0", "false", "no", "off"}
    return bool(value)


def _configured_search_index_embedding_timeout_seconds() -> float:
    return float(
        _retrieval_int_setting(
            "retrieval.search_index_embedding_timeout_seconds",
            60,
            minimum=30,
            maximum=3600,
        )
    )


def _query_embedding_cache_limits() -> tuple[int, int]:
    ttl_seconds = _retrieval_int_setting(
        "retrieval.query_embedding_cache_ttl_seconds",
        _DEFAULT_QUERY_EMBEDDING_CACHE_TTL_SECONDS,
        minimum=0,
        maximum=3600,
    )
    max_entries = _retrieval_int_setting(
        "retrieval.query_embedding_cache_max_entries",
        _DEFAULT_QUERY_EMBEDDING_CACHE_MAX_ENTRIES,
        minimum=0,
        maximum=4096,
    )
    return ttl_seconds, max_entries


def _query_embedding_cache_key(*, query: str, model: str, dimensions: int) -> tuple[str, int, str]:
    query_hash = hashlib.sha256(str(query or "").encode("utf-8")).hexdigest()
    return (str(model), int(dimensions), query_hash)


def _clone_embedding_result(result: EmbeddingResult) -> EmbeddingResult:
    return EmbeddingResult(
        owner_table=result.owner_table,
        owner_id=result.owner_id,
        model=result.model,
        dimensions=result.dimensions,
        vector=[float(value) for value in result.vector],
        metadata=dict(result.metadata),
    )


def _embed_query_for_retrieval(query: str, *, model: str, dimensions: int) -> tuple[EmbeddingResult, int, bool]:
    from .embeddings import EmbeddingInput, SnowflakeEmbeddingProvider

    started = time.perf_counter()
    ttl_seconds, max_entries = _query_embedding_cache_limits()
    cache_key = _query_embedding_cache_key(query=query, model=model, dimensions=dimensions)
    now = time.monotonic()
    if ttl_seconds > 0 and max_entries > 0:
        cached = _QUERY_EMBEDDING_CACHE.get(cache_key)
        if cached is not None:
            cached_at, cached_result = cached
            if now - cached_at <= ttl_seconds:
                _QUERY_EMBEDDING_CACHE.move_to_end(cache_key)
                elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
                return _clone_embedding_result(cached_result), elapsed_ms, True
            _QUERY_EMBEDDING_CACHE.pop(cache_key, None)

    embedding_result = SnowflakeEmbeddingProvider(
        model=model,
        dimensions=dimensions,
        timeout_seconds=_configured_embedding_wait_timeout_seconds(),
    ).embed_batch(
        [
            EmbeddingInput(
                owner_table="query",
                owner_id="query",
                text=query,
                model=model,
                dimensions=dimensions,
            )
        ]
    )[0]
    if ttl_seconds > 0 and max_entries > 0:
        _QUERY_EMBEDDING_CACHE[cache_key] = (now, _clone_embedding_result(embedding_result))
        while len(_QUERY_EMBEDDING_CACHE) > max_entries:
            _QUERY_EMBEDDING_CACHE.popitem(last=False)
    elapsed_ms = max(0, int((time.perf_counter() - started) * 1000))
    return embedding_result, elapsed_ms, False


def _classify_retryable_vespa_embedding_failure(exc: Exception) -> tuple[str, str | None] | None:
    """Classify only temporary query-embedding failures eligible for lexical Vespa."""
    try:
        from .gpu_scheduler import GpuLeaseDeferred, GpuLeaseRejected, GpuLeaseTimeout
        from .model_runner import ModelRunnerBusy, ModelRunnerError
    except Exception:
        GpuLeaseDeferred = GpuLeaseRejected = GpuLeaseTimeout = ModelRunnerBusy = ModelRunnerError = ()  # type: ignore[assignment]
    if isinstance(exc, TimeoutError):
        return ("embedding_timeout", None)
    if isinstance(exc, ModelRunnerBusy):
        return ("model_runner_busy", None)
    if isinstance(exc, GpuLeaseTimeout):
        return ("gpu_lease_timeout", None)
    capacity_state = _search_index_embedding_capacity_state(exc)
    if isinstance(exc, (GpuLeaseDeferred, GpuLeaseRejected)) and capacity_state in {
        "inventory_incomplete",
        "reconciliation_required",
    }:
        return ("gpu_capacity", capacity_state)
    if isinstance(exc, ModelRunnerError):
        message = str(exc).lower()
        if any(marker in message for marker in ("timed out", "timeout", "503")):
            return ("model_runner_timeout", None)
        if any(marker in message for marker in ("busy", "429")):
            return ("model_runner_busy", None)
    return None


def _reranker_base_diagnostics(
    reranker: Any,
    candidates: list[dict[str, Any]],
    *,
    returned_count: int,
    latency_ms: int,
) -> dict[str, Any]:
    return {
        "model": reranker.model,
        "quantization": reranker.quantization,
        "requested_quantization": getattr(reranker, "requested_quantization", reranker.quantization),
        "quantization_backend": getattr(reranker, "quantization_backend", ""),
        "load_model": getattr(reranker, "load_model", reranker.model),
        "awq_model": getattr(reranker, "awq_model", ""),
        "input_count": len(candidates),
        "returned_count": returned_count,
        "latency_ms": latency_ms,
        "top_n": reranker.top_n,
        "max_passage_tokens": reranker.max_passage_tokens,
        "wait_timeout_seconds": getattr(reranker, "timeout_seconds", None),
        "total_budget_seconds": getattr(reranker, "total_budget_seconds", None),
        **_rerank_input_diagnostics(candidates, reranker=reranker),
    }


def _rerank_fallback_reason(exc: Exception) -> str | None:
    try:
        from .gpu_scheduler import GpuLeaseDeferred, GpuLeaseRejected, GpuLeaseTimeout
        from .model_runner import ModelRunnerBusy, ModelRunnerError
        from .reranking import RerankBudgetExceeded
    except Exception:
        GpuLeaseDeferred = GpuLeaseRejected = GpuLeaseTimeout = ModelRunnerBusy = ModelRunnerError = ()  # type: ignore[assignment]
        RerankBudgetExceeded = ()  # type: ignore[assignment]
    if isinstance(exc, RerankBudgetExceeded):
        return "budget_exceeded"
    if isinstance(exc, ModelRunnerBusy):
        return "model_runner_busy"
    if isinstance(exc, (GpuLeaseDeferred, GpuLeaseTimeout)):
        return "gpu_lease_timeout"
    if isinstance(exc, GpuLeaseRejected):
        return "gpu_lease_rejected"
    if isinstance(exc, ModelRunnerError):
        message = str(exc).lower()
        if "503" in message or "429" in message or "scheduler" in message or "busy" in message or "timeout" in message:
            return "model_runner_error"
    return None


def _raise_retryable_search_index_embedding_exception(exc: Exception) -> None:
    try:
        from .gpu_scheduler import GpuLeaseDeferred, GpuLeaseRejected, GpuLeaseTimeout
        from .model_runner import ModelRunnerBusy, ModelRunnerError
    except Exception:
        GpuLeaseDeferred = GpuLeaseRejected = GpuLeaseTimeout = ModelRunnerBusy = ModelRunnerError = ()  # type: ignore[assignment]
    if isinstance(exc, TimeoutError):
        raise ModelRunnerBusy(str(exc) or "search-index embedding request timed out", retry_after_seconds=1.0) from exc
    if isinstance(exc, ModelRunnerBusy):
        raise exc
    if isinstance(exc, (GpuLeaseDeferred, GpuLeaseRejected, GpuLeaseTimeout)):
        raise exc
    if isinstance(exc, ModelRunnerError):
        message = str(exc).lower()
        if any(marker in message for marker in ("timed out", "timeout", "scheduler", "busy", "503", "429")):
            raise ModelRunnerBusy(str(exc), retry_after_seconds=1.0) from exc


def _search_index_embedding_capacity_state(exc: Exception) -> str | None:
    state = getattr(exc, "capacity_state", None)
    if isinstance(state, str) and state.strip():
        return state.strip()
    for attribute in ("metadata", "details", "payload"):
        structured = getattr(exc, attribute, None)
        if not isinstance(structured, dict):
            continue
        state = structured.get("capacity_state")
        if isinstance(state, str) and state.strip():
            return state.strip()
    return None


def _normalised_capacity_source_content_hash(value: Any) -> str:
    return str(value or "").strip()


def _search_index_capacity_blocker_priority(
    *,
    retry_capacity_blockers: bool,
    recorded_content_hash: Any,
    current_content_hash: Any,
) -> int:
    recorded = _normalised_capacity_source_content_hash(recorded_content_hash)
    current = str(current_content_hash or "")
    return 1 if retry_capacity_blockers or (recorded and current and recorded != current) else 4


def _rerank_fallback_results(
    exc: Exception,
    *,
    candidates: list[dict[str, Any]],
    limit: int,
) -> tuple[list[dict[str, Any]], str, dict[str, Any]]:
    try:
        from .reranking import RerankBudgetExceeded
    except Exception:
        RerankBudgetExceeded = ()  # type: ignore[assignment]
    if isinstance(exc, RerankBudgetExceeded):
        results = [dict(item) for item in getattr(exc, "results", [])]
        fallback = str(getattr(exc, "fallback", "") or "vespa_ranked")
        metadata = {
            "total_budget_seconds": getattr(exc, "total_budget_seconds", None),
            "budget_elapsed_ms": getattr(exc, "budget_elapsed_ms", None),
            "scored_count": getattr(exc, "scored_count", None),
            "unscored_count": getattr(exc, "unscored_count", None),
            "completed_microbatch_count": getattr(exc, "completed_microbatch_count", None),
        }
        return results[:limit], fallback, {key: value for key, value in metadata.items() if value is not None}
    return candidates[:limit], "vespa_ranked", {}


def _record_skipped_reranker_diagnostics(
    diagnostics: dict[str, Any] | None,
    *,
    reranker: Any,
    candidates: list[dict[str, Any]],
    returned_count: int,
    latency_ms: int,
    reason: str,
    fallback: str = "vespa_ranked",
    extra: dict[str, Any] | None = None,
) -> None:
    if diagnostics is None:
        return
    payload = _reranker_base_diagnostics(reranker, candidates, returned_count=returned_count, latency_ms=latency_ms)
    payload.update(
        {
            "skipped": True,
            "reason": reason,
            "fallback": fallback,
        }
    )
    if extra:
        payload.update(extra)
    diagnostics["reranker"] = payload


def _rerank_input_diagnostics(candidates: list[dict[str, Any]], *, reranker: Any) -> dict[str, Any]:
    input_count = len(candidates)
    microbatch_size = max(1, int(getattr(reranker, "microbatch_size", 8) or 8))
    max_passage_tokens = max(1, int(getattr(reranker, "max_passage_tokens", 1536) or 1536))
    passage_chars: list[int] = []
    passage_words: list[int] = []
    for candidate in candidates:
        passage = _diagnostic_rerank_passage(candidate, max_tokens=max_passage_tokens)
        passage_chars.append(len(passage))
        passage_words.append(len(passage.split()))
    return {
        "microbatch_size": microbatch_size,
        "microbatch_count": (input_count + microbatch_size - 1) // microbatch_size if input_count else 0,
        "passage_chars": _numeric_summary(passage_chars),
        "passage_words": _numeric_summary(passage_words),
    }


def _diagnostic_rerank_passage(candidate: dict[str, Any], *, max_tokens: int) -> str:
    title = str(candidate.get("title") or "").strip()
    body = str(candidate.get("summary") or candidate.get("body") or candidate.get("excerpt") or "").strip()
    bounded_body = " ".join(body.split()[:max_tokens])
    return "\n".join(part for part in (title, bounded_body) if part)


def _numeric_summary(values: list[int]) -> dict[str, int | float]:
    if not values:
        return {"min": 0, "max": 0, "avg": 0.0}
    return {"min": min(values), "max": max(values), "avg": round(sum(values) / len(values), 1)}


def _sort_code_adjusted_reranked_results(results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    if not any(_code_rank_adjustment_score(item) > 0 for item in results):
        return results
    return [
        item
        for _index, item in sorted(
            enumerate(results),
            key=lambda row: (
                -_code_rank_adjustment_score(row[1]),
                -_reranker_score(row[1]),
                row[0],
            ),
        )
    ]


def _code_rank_adjustment_score(item: dict[str, Any]) -> float:
    raw_scores = item.get("raw_scores") if isinstance(item.get("raw_scores"), dict) else {}
    try:
        return float(raw_scores.get("code_rank_adjustment") or 0.0)
    except (TypeError, ValueError):
        return 0.0


def _reranker_score(item: dict[str, Any]) -> float:
    reranker = item.get("reranker") if isinstance(item.get("reranker"), dict) else {}
    try:
        return float(reranker.get("score") or 0.0)
    except (TypeError, ValueError):
        return 0.0


def _evidence_detail_payload(kind: str, detail: dict[str, Any], *, score: float, streams: Iterable[str] | None = None) -> dict[str, Any]:
    raw_scores = detail.get("raw_scores") if isinstance(detail.get("raw_scores"), dict) else {}
    payload = {
        "kind": kind,
        "id": detail["id"],
        "title": detail["title"],
        "summary": detail["summary"],
        "score": score,
        "streams": _vespa_streams_from_raw_scores(raw_scores, streams),
        "raw_scores": raw_scores,
    }
    for key in ("root_name", "source_path", "metadata", "lifecycle", "graph", "semantic_duplicate_cluster"):
        if key in detail:
            payload[key] = detail[key]
    return payload


def _hydrate_episode_candidate_details(
    cur: Any,
    *,
    candidate_ids: list[str],
    root_name: str | None,
    raw_scores: dict[str, dict[str, float]],
) -> dict[str, dict[str, Any]]:
    if not candidate_ids:
        return {}
    root_sql = "AND e.metadata->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...] = (candidate_ids, *((root_name,) if root_name else ()))
    cur.execute(
        f"""
        SELECT e.id::text, e.title, e.summary, e.metadata,
               e.confidence, e.usage_count, e.superseded_by IS NOT NULL AS superseded,
               e.lifecycle_state, e.contradiction_count, e.retention_action,
               e.created_at, e.updated_at
        FROM episodes e
        WHERE e.id = ANY(%s::uuid[])
          AND e.superseded_by IS NULL
          {root_sql}
        """,
        params,
    )
    details: dict[str, dict[str, Any]] = {}
    for row in cur.fetchall():
        item_id = str(row[0])
        metadata = row[3] if isinstance(row[3], dict) else {}
        lifecycle_state = str(row[7] or "active")
        details[item_id] = {
            "id": item_id,
            "title": str(row[1] or ""),
            "summary": str(row[2] or ""),
            "metadata": metadata,
            "root_name": metadata.get("root_name"),
            "source_path": metadata.get("source_path") or metadata.get("source"),
            "raw_scores": dict(raw_scores.get(item_id, {})),
            "lifecycle": {
                "confidence": float(row[4] or 0.0),
                "usage_count": int(row[5] or 0),
                "superseded": bool(row[6]),
                "state": lifecycle_state,
                "current": lifecycle_state in {"active", "confirmed", "reinforced"},
                "contradiction_count": int(row[8] or 0),
                "retention_action": row[9],
                "audit_visible": lifecycle_state in {"superseded", "contradicted", "stale", "retired"},
                "created_at": row[10].isoformat() if row[10] else None,
                "updated_at": row[11].isoformat() if row[11] else None,
            },
        }
    _add_episode_claim_lifecycle_rows(cur, details)
    return details


def _hydrate_claim_candidate_details(
    cur: Any,
    *,
    candidate_ids: list[str],
    root_name: str | None,
    raw_scores: dict[str, dict[str, float]],
) -> dict[str, dict[str, Any]]:
    if not candidate_ids:
        return {}
    root_sql = "AND c.metadata->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...] = (candidate_ids, *((root_name,) if root_name else ()))
    cur.execute(
        f"""
        SELECT c.id::text, concat_ws(' ', e.name, c.predicate) AS title,
               c.object_text, c.episode_id::text, c.lifecycle_state,
               c.confidence, c.usage_count, c.reinforcement_count,
               c.last_confirmed_at, c.retention_action, c.metadata
        FROM claims c
        LEFT JOIN entities e ON e.id = c.subject_entity_id
        WHERE c.id = ANY(%s::uuid[])
          AND c.lifecycle_state IN ('active', 'confirmed', 'reinforced')
          AND c.retention_action = 'keep'
          {root_sql}
        """,
        params,
    )
    details: dict[str, dict[str, Any]] = {}
    for row in cur.fetchall():
        item_id = str(row[0])
        metadata = row[10] if isinstance(row[10], dict) else {}
        graph = {"episode_id": row[3]} if row[3] else {}
        details[item_id] = {
            "id": item_id,
            "title": str(row[1] or "Claim"),
            "summary": str(row[2] or ""),
            "metadata": metadata,
            "root_name": metadata.get("root_name"),
            "source_path": metadata.get("source_path") or metadata.get("source"),
            "raw_scores": dict(raw_scores.get(item_id, {})),
            "graph": graph,
            "lifecycle": {
                "state": row[4],
                "confidence": float(row[5] or 0.0),
                "usage_count": int(row[6] or 0),
                "reinforcement_count": int(row[7] or 0),
                "last_confirmed_at": row[8].isoformat() if row[8] else None,
                "retention_action": row[9],
            },
        }
    return details


def _corpus_results_from_fused(fused: list[Any], details: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []
    for item in fused:
        if item.item_id not in details:
            continue
        result = details[item.item_id]
        payload = {
            "id": item.item_id,
            "title": result["title"],
            "summary": result["summary"],
            "score": item.score,
            "streams": list(item.streams),
            "raw_scores": result["raw_scores"],
            "asset_id": result["asset_id"],
            "source_path": result["source_path"],
            "root_name": result["root_name"],
            "duplicate_count": result["duplicate_count"],
            "trust_rank": result["trust_rank"],
        }
        if result.get("file_kind") is not None:
            payload["file_kind"] = result["file_kind"]
        if isinstance(result.get("code"), dict):
            payload["code"] = result["code"]
        if "semantic_duplicate_cluster" in result:
            payload["semantic_duplicate_cluster"] = result["semantic_duplicate_cluster"]
        results.append(payload)
    return results


def refresh_semantic_duplicate_clusters(
    *,
    memory_class: str = "all",
    root_name: str | None = None,
    threshold: float | None = None,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    classes = _semantic_duplicate_classes(memory_class)
    normalized_threshold = _semantic_duplicate_threshold(threshold)
    row_limit = max(2, min(int(limit or 1000), 5000))
    payload: dict[str, Any] = {
        "memory_class": memory_class,
        "root_name": root_name,
        "threshold": normalized_threshold,
        "retired_clusters": 0,
        "created_clusters": 0,
        "created_members": 0,
        "clusters": [],
    }
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            for item_class in classes:
                payload["retired_clusters"] += _retire_semantic_duplicate_clusters(
                    cur,
                    memory_class=item_class,
                    root_name=root_name,
                )
                candidates = _fetch_semantic_duplicate_candidates(
                    cur,
                    memory_class=item_class,
                    root_name=root_name,
                    limit=row_limit,
                )
                if len(candidates) < 2:
                    continue
                pairs = _semantic_duplicate_pairs_from_snowflake(
                    candidates,
                    threshold=normalized_threshold,
                )
                clusters = _build_semantic_duplicate_clusters(
                    candidates,
                    pairs,
                    memory_class=item_class,
                    threshold=normalized_threshold,
                )
                for cluster in clusters:
                    inserted = _insert_semantic_duplicate_cluster(cur, cluster)
                    payload["created_clusters"] += 1
                    payload["created_members"] += inserted["member_count"]
                    payload["clusters"].append(inserted["cluster"])
    return payload


def list_semantic_duplicate_clusters(
    *,
    memory_class: str | None = None,
    root_name: str | None = None,
    limit: int = 50,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = max(1, min(int(limit or 50), 200))
    filters = ["c.status = 'active'"]
    params: list[Any] = []
    if memory_class:
        normalized_class = _validate_semantic_duplicate_memory_class(memory_class)
        filters.append("c.memory_class = %s")
        params.append(normalized_class)
    if root_name:
        filters.append("c.root_name = %s")
        params.append(root_name)
    params.append(row_limit)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT c.id::text, c.memory_class, c.status, c.algorithm,
                       c.threshold, c.workspace_key, c.root_name,
                       c.canonical_owner_table, c.canonical_owner_id::text,
                       c.metadata, c.created_at, c.updated_at,
                       COALESCE(
                         jsonb_agg(
                           jsonb_build_object(
                             'owner_table', m.owner_table,
                             'owner_id', m.owner_id::text,
                             'member_role', m.member_role,
                             'similarity', m.similarity,
                             'label', m.evidence->>'label',
                             'source_path', m.evidence->>'source_path'
                           )
                           ORDER BY CASE WHEN m.member_role = 'canonical' THEN 0 ELSE 1 END,
                                    m.similarity DESC,
                                    m.owner_id::text
                         ) FILTER (WHERE m.id IS NOT NULL),
                         '[]'::jsonb
                       ) AS members
                FROM semantic_duplicate_clusters c
                LEFT JOIN semantic_duplicate_members m ON m.cluster_id = c.id
                WHERE {" AND ".join(filters)}
                GROUP BY c.id
                ORDER BY c.updated_at DESC, c.id
                LIMIT %s
                """,
                tuple(params),
            )
            clusters = [_semantic_duplicate_cluster_row(row) for row in cur.fetchall()]
    by_class: dict[str, int] = {}
    suppressed_count = 0
    for cluster in clusters:
        item_class = str(cluster.get("memory_class") or "unknown")
        by_class[item_class] = by_class.get(item_class, 0) + 1
        suppressed_count += int(cluster.get("suppressed_count") or 0)
    return {
        "summary": {
            "total": len(clusters),
            "by_class": by_class,
            "suppressed_count": suppressed_count,
        },
        "clusters": clusters,
    }


def list_audit_events(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, event_type, actor, target_table, target_id::text, details, created_at
                FROM audit_events
                ORDER BY created_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 200)),),
            )
            return [
                {
                    "id": row[0],
                    "event_type": row[1],
                    "actor": row[2],
                    "target_table": row[3],
                    "target_id": row[4],
                    "details": row[5],
                    "created_at": row[6].isoformat(),
                }
                for row in cur.fetchall()
            ]


def backfill_episode_workspace_scope(
    *,
    episode_ids: list[str],
    metadata_patch: dict[str, Any],
    dry_run: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    ids = [str(item).strip() for item in episode_ids if str(item).strip()]
    if not ids:
        raise ValueError("scope-backfill requires at least one episode id")
    patch = {key: value for key, value in metadata_patch.items() if value is not None}
    if not patch:
        raise ValueError("scope-backfill requires non-empty workspace metadata")
    if dry_run:
        return {"updated": 0, "dry_run": True, "episode_ids": ids, "metadata_patch": patch}

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE episodes
                   SET metadata = metadata || %s::jsonb,
                       updated_at = now()
                 WHERE id = ANY(%s::uuid[])
                """,
                (_json(patch), ids),
            )
            updated = int(cur.rowcount or 0)
    return {"updated": updated, "dry_run": False, "episode_ids": ids, "metadata_patch": patch}


def forget_episode(memory_id: str, *, reason: str = "user_request", url: str | None = None) -> bool:
    try:
        parsed_id = str(UUID(memory_id))
    except ValueError:
        return False

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("DELETE FROM episodes WHERE id = %s", (parsed_id,))
            deleted = cur.rowcount > 0
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('episode.forgotten', 'episodes', %s, %s::jsonb)
                """,
                (parsed_id, _json({"reason": reason, "deleted": deleted})),
            )
            return deleted


def list_episodes(*, limit: int = 500, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, title, summary, source_kind, metadata, created_at, updated_at
                FROM episodes
                ORDER BY updated_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 5000)),),
            )
            return [
                {
                    "id": row[0],
                    "title": row[1],
                    "summary": row[2],
                    "source_kind": row[3],
                    "metadata": row[4],
                    "created_at": row[5].isoformat(),
                    "updated_at": row[6].isoformat(),
                }
                for row in cur.fetchall()
            ]


def upsert_entity(
    *,
    entity_type: str,
    name: str,
    attributes: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO entities (type, name, attributes)
                VALUES (%s, %s, %s::jsonb)
                ON CONFLICT (type, name) DO UPDATE SET
                    attributes = entities.attributes || EXCLUDED.attributes
                RETURNING id::text, type, name, attributes, created_at
                """,
                (entity_type, name, _json(attributes or {})),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "type": row[1],
                "name": row[2],
                "attributes": row[3] or {},
                "created_at": row[4].isoformat() if row[4] else None,
            }


def upsert_claim(
    *,
    subject_type: str,
    subject_name: str,
    predicate: str,
    object_text: str,
    confidence: float = 0.5,
    episode_id: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO entities (type, name, attributes)
                VALUES (%s, %s, '{}'::jsonb)
                ON CONFLICT (type, name) DO UPDATE SET
                    attributes = entities.attributes
                RETURNING id::text, type, name, attributes, created_at
                """,
                (subject_type, subject_name),
            )
            entity = cur.fetchone()
            cur.execute(
                """
                INSERT INTO claims (
                    episode_id, subject_entity_id, predicate, object_text,
                    confidence, metadata, last_confirmed_at, lifecycle_state, updated_at
                )
                VALUES (%s, %s, %s, %s, %s, %s::jsonb, now(), 'active', now())
                ON CONFLICT (subject_entity_id, predicate, object_text) DO UPDATE SET
                    episode_id = COALESCE(EXCLUDED.episode_id, claims.episode_id),
                    confidence = greatest(claims.confidence, EXCLUDED.confidence),
                    metadata = claims.metadata || EXCLUDED.metadata,
                    last_confirmed_at = now(),
                    lifecycle_state = CASE
                        WHEN claims.lifecycle_state IN ('retired', 'deleted') THEN claims.lifecycle_state
                        ELSE 'confirmed'
                    END,
                    updated_at = now()
                RETURNING id::text, episode_id::text, subject_entity_id::text, predicate,
                          object_text, confidence, superseded_by::text, created_at,
                          last_confirmed_at, lifecycle_state, usage_count,
                          reinforcement_count, contradiction_count, last_reinforced_at,
                          retention_action, metadata, updated_at, retired_at, stale_at
                """,
                (
                    episode_id,
                    entity[0],
                    predicate,
                    object_text,
                    _clamp_float(confidence),
                    _json(metadata or {}),
                ),
            )
            claim = _claim_row(cur.fetchone(), subject=_entity_row(entity))
            cur.execute(
                """
                INSERT INTO claim_lifecycle_events (
                    claim_id, transition_type, actor, from_state, to_state, details
                )
                VALUES (%s, 'upsert', 'system', NULL, %s, %s::jsonb)
                """,
                (claim["id"], claim["lifecycle_state"], _json({"predicate": predicate})),
            )
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('claim.upserted', 'claims', %s, %s::jsonb)
                """,
                (claim["id"], _json({"subject_entity_id": entity[0]})),
            )
            return claim


def get_claim(claim_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT c.id::text, c.episode_id::text, c.subject_entity_id::text,
                       c.predicate, c.object_text, c.confidence, c.superseded_by::text,
                       c.created_at, c.last_confirmed_at, c.lifecycle_state, c.usage_count,
                       c.reinforcement_count, c.contradiction_count, c.last_reinforced_at,
                       c.retention_action, c.metadata, c.updated_at, c.retired_at, c.stale_at,
                       e.id::text, e.type, e.name, e.attributes, e.created_at
                FROM claims c
                LEFT JOIN entities e ON e.id = c.subject_entity_id
                WHERE c.id = %s
                """,
                (claim_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            subject = _entity_row(row[19:24]) if row[19] else None
            claim = _claim_row(row[:19], subject=subject)
            claim["lifecycle"]["audit_events"] = _claim_lifecycle_events(cur, claim["id"])
            claim["lifecycle"]["related_claims"] = _claim_related_claims(cur, claim["id"])
            return claim


def list_claims(
    *,
    review: str = "all",
    state: str | None = None,
    q: str | None = None,
    limit: int = 50,
    url: str | None = None,
) -> list[dict[str, Any]]:
    review_filter = (review or "all").lower()
    if review_filter not in {"all", "needs_review", "current"}:
        raise ValueError("review must be one of: all, needs_review, current")

    filters: list[str] = []
    params: list[Any] = []
    if review_filter == "needs_review":
        filters.append("(c.lifecycle_state IN ('stale', 'contradicted', 'superseded', 'retired') OR c.retention_action <> 'keep')")
    elif review_filter == "current":
        filters.append("(c.lifecycle_state IN ('active', 'confirmed', 'reinforced') AND c.retention_action = 'keep')")
    if state:
        filters.append("c.lifecycle_state = %s")
        params.append(state)
    if q:
        pattern = f"%{q}%"
        filters.append("(e.name ILIKE %s OR c.predicate ILIKE %s OR c.object_text ILIKE %s)")
        params.extend([pattern, pattern, pattern])
    params.append(max(1, min(limit, 200)))

    where = f"WHERE {' AND '.join(filters)}" if filters else ""
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT c.id::text, c.episode_id::text, c.subject_entity_id::text,
                       c.predicate, c.object_text, c.confidence, c.superseded_by::text,
                       c.created_at, c.last_confirmed_at, c.lifecycle_state, c.usage_count,
                       c.reinforcement_count, c.contradiction_count, c.last_reinforced_at,
                       c.retention_action, c.metadata, c.updated_at, c.retired_at, c.stale_at,
                       e.id::text, e.type, e.name, e.attributes, e.created_at
                FROM claims c
                LEFT JOIN entities e ON e.id = c.subject_entity_id
                {where}
                ORDER BY c.updated_at DESC, c.created_at DESC, c.id
                LIMIT %s
                """,
                tuple(params),
            )
            claims: list[dict[str, Any]] = []
            for row in cur.fetchall():
                subject = _entity_row(row[19:24]) if row[19] else None
                claim = _claim_row(row[:19], subject=subject)
                claim["review_reasons"] = _claim_review_reasons(claim)
                claims.append(claim)
            return claims


def claim_review_counts(*, url: str | None = None) -> dict[str, int]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT count(*) AS total,
                       count(*) FILTER (
                         WHERE lifecycle_state IN ('active', 'confirmed', 'reinforced')
                           AND retention_action = 'keep'
                       ) AS current,
                       count(*) FILTER (
                         WHERE lifecycle_state IN ('stale', 'contradicted', 'superseded', 'retired')
                            OR retention_action <> 'keep'
                       ) AS needs_review,
                       count(*) FILTER (WHERE lifecycle_state = 'stale') AS stale,
                       count(*) FILTER (WHERE lifecycle_state = 'contradicted') AS contradicted,
                       count(*) FILTER (WHERE lifecycle_state = 'superseded') AS superseded,
                       count(*) FILTER (WHERE lifecycle_state = 'retired') AS retired,
                       count(*) FILTER (WHERE retention_action <> 'keep') AS retention_action
                FROM claims
                """
            )
            row = cur.fetchone()
            return {
                "total": int(row[0] or 0),
                "current": int(row[1] or 0),
                "needs_review": int(row[2] or 0),
                "stale": int(row[3] or 0),
                "contradicted": int(row[4] or 0),
                "superseded": int(row[5] or 0),
                "retired": int(row[6] or 0),
                "retention_action": int(row[7] or 0),
            }


def list_retention_policies(*, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            return _fetch_retention_policies(cur)


def set_retention_policy(
    *,
    memory_class: str,
    half_life_days: int,
    min_confidence: float,
    action: str,
    actor: str = "system",
    reason: str,
    url: str | None = None,
) -> dict[str, Any]:
    memory_class = _validate_retention_policy_input(
        memory_class=memory_class,
        half_life_days=half_life_days,
        min_confidence=min_confidence,
        action=action,
        reason=reason,
    )
    metadata = {"reason": reason.strip()}
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, memory_class, half_life_days, min_confidence,
                       action, updated_by, metadata, created_at, updated_at
                FROM retention_policies
                WHERE memory_class = %s
                """,
                (memory_class,),
            )
            old_row = cur.fetchone()
            old_policy = _retention_policy_row(old_row) if old_row else None
            cur.execute(
                """
                INSERT INTO retention_policies (
                    memory_class, half_life_days, min_confidence, action,
                    updated_by, metadata, updated_at
                )
                VALUES (%s, %s, %s, %s, %s, %s::jsonb, now())
                ON CONFLICT (memory_class) DO UPDATE SET
                    half_life_days = EXCLUDED.half_life_days,
                    min_confidence = EXCLUDED.min_confidence,
                    action = EXCLUDED.action,
                    updated_by = EXCLUDED.updated_by,
                    metadata = retention_policies.metadata || EXCLUDED.metadata,
                    updated_at = now()
                RETURNING id::text, memory_class, half_life_days, min_confidence,
                          action, updated_by, metadata, created_at, updated_at
                """,
                (
                    memory_class,
                    int(half_life_days),
                    float(min_confidence),
                    action,
                    actor,
                    _json(metadata),
                ),
            )
            policy = _retention_policy_row(cur.fetchone())
            cur.execute(
                """
                INSERT INTO audit_events (event_type, actor, target_table, target_id, details)
                VALUES ('retention.policy_updated', %s, 'retention_policies', %s, %s::jsonb)
                RETURNING id::text, event_type
                """,
                (
                    actor,
                    policy["id"],
                    _json(
                        {
                            "memory_class": memory_class,
                            "old": old_policy,
                            "new": policy,
                            "reason": reason.strip(),
                        }
                    ),
                ),
            )
            audit = cur.fetchone()
            return {
                "policy": policy,
                "audit_event": {"id": audit[0], "event_type": audit[1]},
            }


def retention_quality_report(*, limit: int = 25, url: str | None = None) -> dict[str, Any]:
    row_limit = max(1, min(limit, 200))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            policies = {policy["memory_class"]: policy for policy in _fetch_retention_policies(cur)}
            candidates: list[dict[str, Any]] = []
            cur.execute(
                """
                SELECT c.id::text, e.name, c.predicate, c.object_text,
                       c.confidence, c.lifecycle_state, c.retention_action,
                       c.updated_at, c.created_at, c.contradiction_count,
                       c.superseded_by::text
                FROM claims c
                LEFT JOIN entities e ON e.id = c.subject_entity_id
                ORDER BY c.updated_at DESC, c.created_at DESC, c.id
                LIMIT %s
                """,
                (row_limit,),
            )
            candidates.extend(_claim_quality_candidate(row, policies.get("claim")) for row in cur.fetchall())
            cur.execute(
                """
                SELECT id::text, title, confidence, created_at, updated_at, superseded_by::text
                FROM episodes
                ORDER BY updated_at DESC, created_at DESC, id
                LIMIT %s
                """,
                (row_limit,),
            )
            candidates.extend(_episode_quality_candidate(row, policies.get("episode")) for row in cur.fetchall())
            cur.execute(
                """
                SELECT a.id::text, a.path,
                       least(greatest(coalesce(r.trust_rank, 0)::double precision / 10.0, 0.0), 1.0) AS confidence,
                       a.extraction_status, a.extraction_tier,
                       a.canonical_asset_id IS NULL AS is_canonical,
                       a.deleted_at, a.last_seen_at, a.updated_at
                FROM source_assets a
                LEFT JOIN monitored_roots r ON r.id = a.root_id
                ORDER BY a.updated_at DESC, a.last_seen_at DESC, a.id
                LIMIT %s
                """,
                (row_limit,),
            )
            candidates.extend(_corpus_quality_candidate(row, policies.get("corpus")) for row in cur.fetchall())
            cur.execute(
                """
                SELECT c.id::text, c.memory_class,
                       COALESCE(c.metadata->>'canonical_label', c.canonical_owner_id::text) AS label,
                       COALESCE((c.metadata->>'suppressed_count')::integer, 0) AS suppressed_count,
                       c.root_name, c.updated_at
                FROM semantic_duplicate_clusters c
                WHERE c.status = 'active'
                ORDER BY c.updated_at DESC, c.id
                LIMIT %s
                """,
                (row_limit,),
            )
            candidates.extend(_semantic_duplicate_quality_candidate(row) for row in cur.fetchall())

    bounded = candidates[:row_limit]
    summary = _retention_quality_summary(bounded)
    return {
        "summary": summary,
        "policies": list(policies.values()),
        "candidates": bounded,
    }


def upsert_entity_relation(
    *,
    from_entity_id: str,
    to_entity_id: str,
    relation_type: str,
    confidence: float = 0.5,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO relations (
                    from_entity_id, to_entity_id, relation_type, confidence, metadata,
                    lifecycle_state, updated_at
                )
                VALUES (%s, %s, %s, %s, %s::jsonb, 'active', now())
                ON CONFLICT (from_entity_id, to_entity_id, relation_type) DO UPDATE SET
                    confidence = greatest(relations.confidence, EXCLUDED.confidence),
                    metadata = relations.metadata || EXCLUDED.metadata,
                    lifecycle_state = 'active',
                    updated_at = now()
                RETURNING id::text, from_entity_id::text, to_entity_id::text,
                          relation_type, confidence, metadata, lifecycle_state, created_at, updated_at
                """,
                (
                    from_entity_id,
                    to_entity_id,
                    relation_type,
                    _clamp_float(confidence),
                    _json(metadata or {}),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "from_entity_id": row[1],
                "to_entity_id": row[2],
                "relation_type": row[3],
                "confidence": float(row[4] or 0.0),
                "metadata": row[5] or {},
                "lifecycle_state": row[6],
                "created_at": row[7].isoformat() if row[7] else None,
                "updated_at": row[8].isoformat() if row[8] else None,
            }


def transition_claim(
    *,
    claim_id: str,
    transition: str,
    related_claim_id: str | None = None,
    reason: str | None = None,
    actor: str = "system",
    confidence_delta: float = 0.0,
    url: str | None = None,
) -> dict[str, Any]:
    transition_name = transition.lower().replace("-", "_")
    if transition_name not in {
        "reinforce",
        "confirm",
        "supersede",
        "contradict",
        "stale",
        "deprioritize",
        "retire",
        "delete",
    }:
        raise ValueError(f"unsupported claim transition: {transition}")

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT lifecycle_state, confidence, contradiction_count, reinforcement_count
                FROM claims
                WHERE id = %s
                FOR UPDATE
                """,
                (claim_id,),
            )
            old = cur.fetchone()
            if old is None:
                raise LookupError(f"claim not found: {claim_id}")
            from_state = old[0]
            to_state = _claim_transition_state(transition_name)
            event_type = {
                "reinforce": "claim.reinforced",
                "confirm": "claim.confirmed",
                "supersede": "claim.superseded",
                "contradict": "claim.contradicted",
                "stale": "claim.stale",
                "deprioritize": "claim.stale",
                "retire": "claim.retired",
                "delete": "claim.retired",
            }[transition_name]
            relation_type = {
                "reinforce": "reinforces",
                "supersede": "supersedes",
                "contradict": "contradicts",
            }.get(transition_name)
            cur.execute(
                """
                UPDATE claims
                SET lifecycle_state = %s,
                    confidence = greatest(0.0, least(1.0, confidence + %s)),
                    last_confirmed_at = CASE WHEN %s IN ('confirm', 'reinforce') THEN now() ELSE last_confirmed_at END,
                    last_reinforced_at = CASE WHEN %s = 'reinforce' THEN now() ELSE last_reinforced_at END,
                    reinforcement_count = CASE WHEN %s = 'reinforce' THEN reinforcement_count + 1 ELSE reinforcement_count END,
                    contradiction_count = CASE WHEN %s = 'contradict' THEN contradiction_count + 1 ELSE contradiction_count END,
                    superseded_by = CASE WHEN %s = 'supersede' THEN %s ELSE superseded_by END,
                    stale_at = CASE WHEN %s IN ('stale', 'deprioritize') THEN now() ELSE stale_at END,
                    retired_at = CASE WHEN %s IN ('retire', 'delete') THEN now() ELSE retired_at END,
                    retention_action = CASE
                        WHEN %s = 'deprioritize' THEN 'deprioritize'
                        WHEN %s IN ('retire', 'delete') THEN 'retire'
                        ELSE retention_action
                    END,
                    updated_at = now()
                WHERE id = %s
                """,
                (
                    to_state,
                    confidence_delta,
                    transition_name,
                    transition_name,
                    transition_name,
                    transition_name,
                    transition_name,
                    related_claim_id,
                    transition_name,
                    transition_name,
                    transition_name,
                    transition_name,
                    claim_id,
                ),
            )
            if relation_type and related_claim_id:
                cur.execute(
                    """
                    INSERT INTO claim_relations (
                        from_claim_id, to_claim_id, relation_type, confidence, metadata
                    )
                    VALUES (%s, %s, %s, %s, %s::jsonb)
                    ON CONFLICT (from_claim_id, to_claim_id, relation_type) DO UPDATE SET
                        confidence = greatest(claim_relations.confidence, EXCLUDED.confidence),
                        metadata = claim_relations.metadata || EXCLUDED.metadata
                    """,
                    (
                        claim_id,
                        related_claim_id,
                        relation_type,
                        1.0 if transition_name in {"supersede", "contradict"} else 0.75,
                        _json({"transition": transition_name, "reason": reason}),
                    ),
                )
            cur.execute(
                """
                INSERT INTO claim_lifecycle_events (
                    claim_id, transition_type, actor, from_state, to_state,
                    related_claim_id, confidence_delta, details
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s::jsonb)
                """,
                (
                    claim_id,
                    transition_name,
                    actor,
                    from_state,
                    to_state,
                    related_claim_id,
                    confidence_delta,
                    _json({"reason": reason} if reason else {}),
                ),
            )
            cur.execute(
                """
                INSERT INTO audit_events (event_type, actor, target_table, target_id, details)
                VALUES (%s, %s, 'claims', %s, %s::jsonb)
                """,
                (
                    event_type,
                    actor,
                    claim_id,
                    _json(
                        {
                            "from_state": from_state,
                            "to_state": to_state,
                            "related_claim_id": related_claim_id,
                            "reason": reason,
                        }
                    ),
                ),
            )
    claim = get_claim(claim_id, url=url)
    if claim is None:
        raise LookupError(f"claim not found: {claim_id}")
    return claim


def restore_claim_lifecycle_state(
    *,
    claim_id: str,
    lifecycle_state: str,
    retention_action: str = "keep",
    actor: str = "system",
    reason: str,
    url: str | None = None,
) -> dict[str, Any]:
    restored_state = str(lifecycle_state or "active").strip().lower()
    restored_action = str(retention_action or "keep").strip().lower()
    if restored_state not in {"active", "confirmed", "reinforced", "stale", "contradicted", "superseded", "retired"}:
        raise ValueError(f"unsupported claim lifecycle state: {lifecycle_state}")
    if restored_action not in {"keep", "review", "deprioritize", "retire"}:
        raise ValueError(f"unsupported retention action: {retention_action}")
    clean_reason = str(reason or "").strip()
    if not clean_reason:
        raise ValueError("recovery reason is required")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT lifecycle_state, retention_action
                FROM claims
                WHERE id = %s
                FOR UPDATE
                """,
                (claim_id,),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"claim not found: {claim_id}")
            from_state = row[0]
            from_action = row[1]
            cur.execute(
                """
                UPDATE claims
                SET lifecycle_state = %s,
                    retention_action = %s,
                    stale_at = CASE WHEN %s = 'stale' THEN COALESCE(stale_at, now()) ELSE stale_at END,
                    retired_at = CASE WHEN %s = 'retired' THEN COALESCE(retired_at, now()) ELSE retired_at END,
                    updated_at = now()
                WHERE id = %s
                """,
                (restored_state, restored_action, restored_state, restored_state, claim_id),
            )
            cur.execute(
                """
                INSERT INTO claim_lifecycle_events (
                    claim_id, transition_type, actor, from_state, to_state, details
                )
                VALUES (%s, 'governance_recover', %s, %s, %s, %s::jsonb)
                """,
                (
                    claim_id,
                    actor,
                    from_state,
                    restored_state,
                    _json({"reason": clean_reason, "from_retention_action": from_action, "to_retention_action": restored_action}),
                ),
            )
            cur.execute(
                """
                INSERT INTO audit_events (event_type, actor, target_table, target_id, details)
                VALUES ('governance.action_recovered', %s, 'claims', %s, %s::jsonb)
                """,
                (
                    actor,
                    claim_id,
                    _json({"reason": clean_reason, "from_state": from_state, "to_state": restored_state, "settings_mutated": False}),
                ),
            )
    claim = get_claim(claim_id, url=url)
    if claim is None:
        raise LookupError(f"claim not found: {claim_id}")
    return claim


def traverse_entity_graph(
    *,
    entity_id: str,
    relation_types: list[str] | None = None,
    max_depth: int = 2,
    direction: str = "out",
    limit: int = 100,
    url: str | None = None,
) -> dict[str, Any]:
    if direction not in {"out", "in", "both"}:
        raise ValueError("direction must be one of: out, in, both")
    depth_limit = max(1, min(max_depth, 5))
    row_limit = max(1, min(limit, 500))
    relation_filter = relation_types or None
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                WITH RECURSIVE relation_edges AS (
                    SELECT id, from_entity_id, to_entity_id, relation_type, confidence, metadata
                    FROM relations
                    WHERE %s IN ('out', 'both')
                      AND lifecycle_state <> 'retired'
                    UNION ALL
                    SELECT id, to_entity_id AS from_entity_id, from_entity_id AS to_entity_id,
                           relation_type, confidence, metadata
                    FROM relations
                    WHERE %s IN ('in', 'both')
                      AND lifecycle_state <> 'retired'
                ),
                graph AS (
                    SELECT re.id AS relation_id, re.from_entity_id, re.to_entity_id,
                           re.to_entity_id AS next_entity_id, re.relation_type,
                           re.confidence, re.metadata, 1 AS depth,
                           ARRAY[%s::uuid, re.to_entity_id] AS path
                    FROM relation_edges re
                    WHERE re.from_entity_id = %s
                      AND (%s::text[] IS NULL OR re.relation_type = ANY(%s::text[]))
                    UNION ALL
                    SELECT re.id AS relation_id, re.from_entity_id, re.to_entity_id,
                           edge.next_entity_id, re.relation_type, re.confidence,
                           re.metadata, graph.depth + 1 AS depth,
                           graph.path || edge.next_entity_id AS path
                    FROM graph
                    JOIN relation_edges re ON re.from_entity_id = graph.next_entity_id
                    JOIN LATERAL (SELECT re.to_entity_id AS next_entity_id) edge ON true
                    WHERE graph.depth < %s
                      AND (%s::text[] IS NULL OR re.relation_type = ANY(%s::text[]))
                      AND NOT next_entity_id = ANY(path)
                )
                SELECT graph.relation_id::text, graph.from_entity_id::text,
                       from_entity.type, from_entity.name,
                       graph.to_entity_id::text, to_entity.type, to_entity.name,
                       graph.relation_type, graph.confidence, graph.metadata,
                       graph.depth, graph.path::text[]
                FROM graph
                JOIN entities from_entity ON from_entity.id = graph.from_entity_id
                JOIN entities to_entity ON to_entity.id = graph.to_entity_id
                ORDER BY depth ASC, relation_type ASC, from_entity.name ASC,
                         to_entity.name ASC, relation_id ASC
                LIMIT %s
                """,
                (
                    direction,
                    direction,
                    entity_id,
                    entity_id,
                    relation_filter,
                    relation_filter,
                    depth_limit,
                    relation_filter,
                    relation_filter,
                    row_limit,
                ),
            )
            edges = [
                {
                    "relation_id": row[0],
                    "from_entity_id": row[1],
                    "from_entity": {"type": row[2], "name": row[3]},
                    "to_entity_id": row[4],
                    "to_entity": {"type": row[5], "name": row[6]},
                    "relation_type": row[7],
                    "confidence": float(row[8] or 0.0),
                    "metadata": row[9] or {},
                    "depth": int(row[10] or 0),
                    "path": list(row[11] or []),
                }
                for row in cur.fetchall()
            ]
            return {
                "start_entity_id": entity_id,
                "relation_types": relation_types or [],
                "max_depth": depth_limit,
                "direction": direction,
                "edges": edges,
            }


def codex_hook_capture_exists(*, session_id: str, turn_id: str, url: str | None = None) -> bool:
    if not session_id or not turn_id:
        return False
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT 1
                FROM episodes
                WHERE metadata->>'source' = 'codex_hook_stop'
                  AND metadata->>'session_id' = %s
                  AND metadata->>'turn_id' = %s
                LIMIT 1
                """,
                (session_id, turn_id),
            )
            return cur.fetchone() is not None


def codex_hook_reference_exists(
    *, session_id: str, turn_id: str, reference: str, url: str | None = None
) -> bool:
    if not session_id or not turn_id or not reference:
        return False
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT 1
                FROM audit_events
                WHERE event_type = 'codex_hook.reference_indexed'
                  AND details->>'session_id' = %s
                  AND details->>'turn_id' = %s
                  AND details->>'reference' = %s
                LIMIT 1
                """,
                (session_id, turn_id, reference),
            )
            return cur.fetchone() is not None


def recent_codex_hook_audit_events(*, limit: int = 5, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, event_type, actor, target_table, target_id::text, details, created_at
                FROM audit_events
                WHERE event_type LIKE 'codex_hook.%'
                ORDER BY created_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 20)),),
            )
            return [
                {
                    "id": row[0],
                    "event_type": row[1],
                    "actor": row[2],
                    "target_table": row[3],
                    "target_id": row[4],
                    "details": row[5],
                    "created_at": row[6].isoformat(),
                }
                for row in cur.fetchall()
            ]


def record_audit_event(
    *,
    event_type: str,
    target_table: str | None = None,
    target_id: str | None = None,
    details: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES (%s, %s, %s, %s::jsonb)
                RETURNING id::text, event_type
                """,
                (event_type, target_table, target_id, _json(details or {})),
            )
            row = cur.fetchone()
            return {"id": row[0], "event_type": row[1]}


def record_gpu_eviction_cas_rejection(
    *,
    eviction_id: str,
    stage: str,
    worker_id: str = "",
    broker_message_id: str = "",
    url: str | None = None,
) -> dict[str, Any]:
    """Persist a CAS race without mutating the eviction's terminal history."""
    return record_audit_event(
        event_type="gpu_eviction.cas_rejected",
        target_table="gpu_evictions",
        target_id=str(eviction_id),
        details={
            "stage": _gpu_eviction_audit_stage(stage),
            "worker_id_hash": _gpu_eviction_audit_identifier_hash(worker_id),
            "broker_message_id_hash": _gpu_eviction_audit_identifier_hash(broker_message_id),
        },
        url=url,
    )


def _gpu_eviction_audit_stage(value: str) -> str:
    allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._:-"
    cleaned = "".join(char if char in allowed else "_" for char in str(value or ""))[:80]
    return cleaned or "unknown"


def _gpu_eviction_audit_identifier_hash(value: str) -> str:
    text = str(value or "")
    return hashlib.sha256(text.encode("utf-8", errors="replace")).hexdigest()[:24] if text else ""


def enqueue_message_outbox(
    *,
    message_id: str | None = None,
    exchange: str,
    routing_key: str,
    message_type: str,
    payload: dict[str, Any],
    headers: dict[str, Any] | None = None,
    aggregate_type: str | None = None,
    aggregate_id: str | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            return _enqueue_message_outbox_with_cursor(
                cur,
                message_id=message_id,
                exchange=exchange,
                routing_key=routing_key,
                message_type=message_type,
                payload=payload,
                headers=headers,
                aggregate_type=aggregate_type,
                aggregate_id=aggregate_id,
                correlation_id=correlation_id,
                causation_id=causation_id,
            )


def _enqueue_message_outbox_with_cursor(
    cur: Any,
    *,
    message_id: str | None = None,
    exchange: str,
    routing_key: str,
    message_type: str,
    payload: dict[str, Any],
    headers: dict[str, Any] | None = None,
    aggregate_type: str | None = None,
    aggregate_id: str | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
) -> dict[str, Any]:
    message = messaging.build_message(
        message_id=message_id,
        message_type=message_type,
        routing_key=routing_key,
        job_id=aggregate_id if aggregate_type == "capture_jobs" else None,
        correlation_id=correlation_id,
        causation_id=causation_id,
        payload=payload,
    )
    broker_payload = message.to_broker_payload()
    cur.execute(
        """
        INSERT INTO message_outbox (
            message_id, exchange, routing_key, message_type, schema_version,
            correlation_id, causation_id, aggregate_type, aggregate_id, payload, headers
        )
        VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb, %s::jsonb)
        ON CONFLICT (message_id) DO UPDATE SET
            updated_at = message_outbox.updated_at
        RETURNING id::text, message_id, status
        """,
        (
            message.message_id,
            exchange,
            routing_key,
            message_type,
            message.schema_version,
            message.correlation_id,
            message.causation_id,
            aggregate_type,
            aggregate_id,
            _json(broker_payload),
            _json(headers or {}),
        ),
    )
    row = cur.fetchone()
    if row is None:
        return {"id": None, "message_id": message.message_id, "status": "pending"}
    return {"id": row[0], "message_id": row[1], "status": row[2]}


def claim_pending_outbox_messages(
    *,
    limit: int = 100,
    worker_id: str = "outbox-relay",
    url: str | None = None,
) -> list[dict[str, Any]]:
    capped_limit = max(1, min(int(limit or 100), 1000))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                WITH claimable AS (
                    SELECT id
                    FROM message_outbox
                    WHERE status IN ('pending', 'failed')
                      AND next_attempt_at <= now()
                    ORDER BY created_at
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE message_outbox outbox
                SET status = 'publishing',
                    locked_by = %s,
                    locked_at = now(),
                    attempts = attempts + 1,
                    updated_at = now()
                FROM claimable
                WHERE outbox.id = claimable.id
                RETURNING outbox.id::text, outbox.message_id, outbox.exchange, outbox.routing_key,
                          outbox.message_type, outbox.payload, outbox.headers, outbox.attempts
                """,
                (capped_limit, worker_id),
            )
            return [
                {
                    "id": row[0],
                    "message_id": row[1],
                    "exchange": row[2],
                    "routing_key": row[3],
                    "message_type": row[4],
                    "payload": row[5] or {},
                    "headers": row[6] or {},
                    "attempts": int(row[7] or 0),
                }
                for row in cur.fetchall()
            ]


def mark_outbox_message_published(
    *,
    outbox_id: str,
    broker_message_id: str,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE message_outbox
                SET status = 'published',
                    broker_message_id = NULLIF(%s, ''),
                    published_at = now(),
                    locked_at = NULL,
                    locked_by = NULL,
                    last_error = NULL,
                    updated_at = now()
                WHERE id = %s
                """,
                (broker_message_id, outbox_id),
            )


def mark_outbox_message_failed(
    *,
    outbox_id: str,
    error: str,
    retry_backoff_seconds: int = OUTBOX_RETRY_BACKOFF_SECONDS,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE message_outbox
                SET status = 'failed',
                    last_error = %s,
                    next_attempt_at = now() + make_interval(secs => %s),
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                """,
                (str(error or "")[:1000], max(1, int(retry_backoff_seconds or 1)), outbox_id),
            )


def begin_message_inbox(
    *,
    consumer_name: str,
    message_id: str,
    message_type: str,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> bool:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO message_inbox (
                    consumer_name, message_id, message_type, status, metadata
                )
                VALUES (%s, %s, %s, 'processing', %s::jsonb)
                ON CONFLICT (consumer_name, message_id) DO UPDATE SET
                    attempts = message_inbox.attempts + 1,
                    last_seen_at = now(),
                    status = CASE
                        WHEN message_inbox.status = 'handled' THEN message_inbox.status
                        ELSE 'processing'
                    END,
                    metadata = message_inbox.metadata || EXCLUDED.metadata
                RETURNING status
                """,
                (consumer_name, message_id, message_type, _json(metadata or {})),
            )
            row = cur.fetchone()
            return row is not None and row[0] != "handled"


def complete_message_inbox(
    *,
    consumer_name: str,
    message_id: str,
    status: str = "handled",
    error: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    clean_status = status if status in {"handled", "failed"} else "handled"
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE message_inbox
                SET status = %s,
                    handled_at = CASE WHEN %s = 'handled' THEN now() ELSE handled_at END,
                    last_error = %s,
                    metadata = metadata || %s::jsonb,
                    last_seen_at = now()
                WHERE consumer_name = %s
                  AND message_id = %s
                """,
                (clean_status, clean_status, error, _json(metadata or {}), consumer_name, message_id),
            )


def enqueue_operator_automation_command(
    *,
    operation_id: str,
    mode: str = "guarded",
    trigger: str = "manual",
    actor: str = "api",
    limit: int = 25,
    dry_run: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    payload = {
        "operation_id": str(operation_id),
        "mode": str(mode or "guarded"),
        "trigger": str(trigger or "manual"),
        "requested_by": str(actor or "api"),
        "limit": max(1, min(int(limit or 25), 500)),
        "dry_run": bool(dry_run),
    }
    outbox = enqueue_message_outbox(
        exchange=messaging.COMMANDS_EXCHANGE,
        routing_key=messaging.AUTOMATION_ROUTING_KEY,
        message_type="flux.operator.automation.run",
        payload=payload,
        aggregate_type="operator_automation_runs",
        aggregate_id=str(operation_id),
        correlation_id=str(operation_id),
        url=url,
    )
    return {"operation_id": str(operation_id), "message_id": outbox.get("message_id"), "status": outbox.get("status"), "payload": payload}


def enqueue_governance_run_command(
    *,
    operation_id: str,
    mode: str = "shadow",
    actor: str = "api",
    limit: int = 25,
    url: str | None = None,
) -> dict[str, Any]:
    payload = {
        "operation_id": str(operation_id),
        "mode": str(mode or "shadow"),
        "requested_by": str(actor or "api"),
        "limit": max(1, min(int(limit or 25), 500)),
    }
    outbox = enqueue_message_outbox(
        exchange=messaging.COMMANDS_EXCHANGE,
        routing_key=messaging.GOVERNANCE_ROUTING_KEY,
        message_type="flux.governance.run",
        payload=payload,
        aggregate_type="memory_governance_runs",
        aggregate_id=str(operation_id),
        correlation_id=str(operation_id),
        url=url,
    )
    return {"operation_id": str(operation_id), "message_id": outbox.get("message_id"), "status": outbox.get("status"), "payload": payload}


def enqueue_runtime_control_apply_command(
    *,
    operation_id: str | None = None,
    component: str | None = None,
    actor: str = "api",
    url: str | None = None,
) -> dict[str, Any]:
    effective_operation_id = str(operation_id or _stable_message_id("runtime-control-apply", str(component or "all"), str(actor or "api"), str(time.time())))
    payload = {
        "operation_id": effective_operation_id,
        "component": str(component).strip() if component else None,
        "requested_by": str(actor or "api"),
    }
    outbox = enqueue_message_outbox(
        exchange=messaging.COMMANDS_EXCHANGE,
        routing_key=messaging.RUNTIME_CONTROL_ROUTING_KEY,
        message_type="flux.runtime_control.apply",
        payload=payload,
        aggregate_type="runtime_control_requests",
        aggregate_id=effective_operation_id,
        correlation_id=effective_operation_id,
        url=url,
    )
    return {"operation_id": effective_operation_id, "message_id": outbox.get("message_id"), "status": outbox.get("status"), "payload": payload}


def record_event_journal(
    *,
    subscriber_name: str,
    message_id: str,
    message_type: str,
    exchange: str,
    routing_key: str,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    job_id: str | None = None,
    payload: dict[str, Any] | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    clean_subscriber = str(subscriber_name or "").strip()
    if not clean_subscriber:
        raise ValueError("subscriber_name is required")
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO event_journal (
                    subscriber_name, message_id, message_type, exchange, routing_key,
                    correlation_id, causation_id, job_id, payload, metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb, %s::jsonb)
                ON CONFLICT (subscriber_name, message_id) DO UPDATE SET
                    metadata = event_journal.metadata || EXCLUDED.metadata
                RETURNING id::text, subscriber_name, message_id, routing_key, received_at
                """,
                (
                    clean_subscriber,
                    str(message_id),
                    str(message_type),
                    str(exchange),
                    str(routing_key),
                    _postgres_text_or_none(correlation_id),
                    _postgres_text_or_none(causation_id),
                    _postgres_text_or_none(job_id),
                    _json(payload or {}),
                    _json(metadata or {}),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "subscriber_name": row[1],
                "message_id": row[2],
                "routing_key": row[3],
                "received_at": row[4].isoformat() if row[4] else None,
            }


GPU_EVICTION_ACTIVE_STATUSES = ("queued", "running", "retrying")
GPU_EVICTION_TERMINAL_STATUSES = ("succeeded", "failed", "skipped", "expired")
_GPU_EVICTION_ROW_COLUMNS = """id::text, lease_id, task_type, model_id, component, status,
       estimated_freed_vram_mb, error, metadata, broker_message_id, routing_key,
       correlation_id, causation_id, broker_delivery_count, runtime_generation,
       runtime_activity_sequence, claim_token, row_version, status_changed_at,
       heartbeat_at, retry_not_before, expires_at, request_reason, terminal_reason,
       reconciliation_observation_id"""


def _gpu_eviction_deadlines(*, delivery_count: int = 0) -> dict[str, float]:
    """Return lifecycle guards from the live broker retry policy.

    The database owns the durable deadline, rather than relying on a worker's
    process-local timer.  A running eviction is bounded by the configured
    unload/verification window; retries also reserve the remaining broker
    deliveries and one final running guard.
    """
    config = messaging.RabbitMqConfig.from_env()
    retry_seconds = max(1, int(config.retry_delay_ms or 1000) // 1000)
    delivery_limit = max(1, int(config.delivery_limit or 1))
    unload_timeout_seconds = _gpu_eviction_request_timeout_seconds()
    running_seconds = max(60, 2 * (unload_timeout_seconds + unload_timeout_seconds))
    return {
        "queued_seconds": max(120, 2 * retry_seconds),
        "running_seconds": running_seconds,
        "retry_delay_seconds": retry_seconds,
        "retry_seconds": retry_seconds * (1 + max(0, delivery_limit - max(0, int(delivery_count)))) + running_seconds,
    }


def _gpu_eviction_request_timeout_seconds() -> float:
    """Resolve the same reloadable timeout used by the eviction worker.

    Importing the settings service here keeps database deadline derivation
    independent of the scheduler module, avoiding a database/scheduler import
    cycle while preserving the worker's setting precedence.
    """
    value: Any = 10
    try:
        from .settings import SettingsService

        value = SettingsService().resolve("gpu.scheduler.eviction_request_timeout_seconds").raw_value
    except Exception:
        pass
    try:
        return max(0.0, float(value or 10))
    except (TypeError, ValueError):
        return 10.0


def _gpu_eviction_cas_rejected(eviction_id: str) -> dict[str, Any]:
    return {"eviction_id": str(eviction_id), "cas_rejected": True}
GPU_EVICTION_REQUEST_MESSAGE_TYPE = "flux.gpu.eviction.request"


def enqueue_gpu_eviction_request(
    *,
    lease_id: str,
    request_profile: dict[str, Any],
    candidate: dict[str, Any],
    metadata: dict[str, Any] | None = None,
    runtime_generation: str | None = None,
    runtime_activity_sequence: int | None = None,
    request_reason: str = "demand",
    reconciliation_observation_id: str | None = None,
    url: str | None = None,
    connection: Any | None = None,
) -> dict[str, Any]:
    clean_lease_id = str(lease_id or "").strip()
    if not clean_lease_id:
        raise ValueError("lease_id is required")
    task_type = str(candidate.get("task_type") or "").strip()
    model_id = str(candidate.get("model_id") or "").strip()
    component = str(candidate.get("component") or "").strip()
    generation = str(runtime_generation if runtime_generation is not None else candidate.get("runtime_generation") or "").strip()
    activity_sequence = max(0, int(runtime_activity_sequence if runtime_activity_sequence is not None else candidate.get("runtime_activity_sequence") or 0))
    reason = "idle" if str(request_reason or "").strip().lower() == "idle" else "demand"
    observation_id = str(reconciliation_observation_id if reconciliation_observation_id is not None else candidate.get("reconciliation_observation_id") or "").strip()
    if not task_type:
        raise ValueError("GPU eviction candidate task_type is required")
    if not model_id:
        raise ValueError("GPU eviction candidate model_id is required")
    request_metadata = {
        "request_profile": dict(request_profile or {}),
        "candidate": dict(candidate or {}),
        **dict(metadata or {}),
    }
    connection_context = nullcontext(connection) if connection is not None else _load_psycopg().connect(url or database_url())
    with connection_context as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                # Row locks cannot protect a logical eviction key before its
                # first row exists. Serialise this key for the complete
                # lookup/insert/outbox transaction so concurrent admissions
                # reuse the same active eviction and publish one command.
                cur.execute(
                    """
                    SELECT pg_advisory_xact_lock(
                        hashtextextended(jsonb_build_array(%s, %s, %s)::text, 0)
                    )
                    """,
                    (task_type, model_id, component),
                )
                expired = _expire_stale_gpu_eviction_requests_with_cursor(cur)
                for request in expired:
                    _enqueue_gpu_eviction_event_with_cursor(
                        cur,
                        request=request,
                        event_status="expired",
                        correlation_id=request.get("correlation_id"),
                        causation_id=request.get("broker_message_id"),
                    )
                cur.execute(
                    """
                    SELECT id::text, status, broker_message_id, request_reason
                      FROM gpu_evictions
                     WHERE task_type = %s
                       AND model_id = %s
                       AND component = %s
                       AND runtime_generation = %s
                       AND status IN ('queued', 'running', 'retrying')
                     ORDER BY id ASC
                     LIMIT 1
                     FOR UPDATE
                    """,
                    (task_type, model_id, component, generation),
                )
                existing = cur.fetchone()
                if existing:
                    existing_reason = str(existing[3] or "") if len(existing) > 3 else "demand"
                    if reason == "demand" and existing[1] == "queued" and existing_reason == "idle":
                        cur.execute(
                            """
                            UPDATE gpu_evictions
                               SET lease_id = %s,
                                   request_reason = 'demand',
                                   reconciliation_observation_id = %s,
                                   runtime_activity_sequence = GREATEST(runtime_activity_sequence, %s),
                                   metadata = metadata || %s::jsonb,
                                   row_version = row_version + 1,
                                   status_changed_at = now()
                             WHERE id::text = %s
                               AND status = 'queued'
                            """,
                            (clean_lease_id, observation_id, activity_sequence, _json({"demand_lease_id": clean_lease_id}), existing[0]),
                        )
                    return {
                        "id": existing[0],
                        "eviction_id": existing[0],
                        "status": existing[1],
                        "message_id": existing[2] or _stable_message_id("gpu-eviction", existing[0]),
                        "deduped": True,
                    }
                cur.execute(
                    """
                    INSERT INTO gpu_evictions (
                        lease_id, task_type, model_id, component, status, estimated_freed_vram_mb,
                        created_at, queued_at, metadata, runtime_generation, runtime_activity_sequence,
                        request_reason, reconciliation_observation_id, status_changed_at, expires_at
                    )
                    VALUES (%s, %s, %s, %s, 'queued', %s, now(), now(), %s::jsonb, %s, %s, %s, %s, now(), now() + (%s * interval '1 second'))
                    RETURNING id::text
                    """,
                    (
                        clean_lease_id,
                        task_type,
                        model_id,
                        component,
                        max(0, int(candidate.get("estimated_vram_mb") or 0)),
                        _json(request_metadata),
                        generation,
                        activity_sequence,
                        reason,
                        observation_id,
                        _gpu_eviction_deadlines()["queued_seconds"],
                    ),
                )
                inserted = cur.fetchone()
                eviction_id = str(inserted[0])
                # A replacement for an expired eviction may have the same
                # lease, runtime and generation as its predecessor.  The
                # outbox identity must therefore name this durable request,
                # otherwise the unique message constraint preserves an old
                # published payload that points at the expired eviction.
                message_id = _stable_message_id("gpu-eviction", eviction_id)
                command_payload = {
                    "eviction_id": eviction_id,
                    "lease_id": clean_lease_id,
                    "task_type": task_type,
                    "model_id": model_id,
                    "component": component,
                    "estimated_vram_mb": max(0, int(candidate.get("estimated_vram_mb") or 0)),
                }
                outbox = _enqueue_message_outbox_with_cursor(
                    cur,
                    message_id=message_id,
                    exchange=messaging.COMMANDS_EXCHANGE,
                    routing_key=messaging.GPU_EVICTION_ROUTING_KEY,
                    message_type=GPU_EVICTION_REQUEST_MESSAGE_TYPE,
                    payload=command_payload,
                    aggregate_type="gpu_evictions",
                    aggregate_id=eviction_id,
                    correlation_id=message_id,
                )
                cur.execute(
                    """
                    UPDATE gpu_evictions
                       SET broker_message_id = %s,
                           correlation_id = %s,
                           routing_key = %s,
                           queued_at = COALESCE(queued_at, now())
                     WHERE id::text = %s
                    """,
                    (outbox.get("message_id"), outbox.get("message_id"), messaging.GPU_EVICTION_ROUTING_KEY, eviction_id),
                )
                return {
                    "id": eviction_id,
                    "eviction_id": eviction_id,
                    "status": "queued",
                    "message_id": outbox.get("message_id"),
                    "deduped": False,
                }


def claim_gpu_eviction_request(
    *,
    eviction_id: str,
    worker_id: str,
    broker_message_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                expired = _expire_stale_gpu_eviction_requests_with_cursor(cur)
                for request in expired:
                    _enqueue_gpu_eviction_event_with_cursor(
                        cur,
                        request=request,
                        event_status="expired",
                        correlation_id=request.get("correlation_id"),
                        causation_id=request.get("broker_message_id"),
                    )
                cur.execute(
                    """
                    SELECT """ + _GPU_EVICTION_ROW_COLUMNS + """
                      FROM gpu_evictions
                     WHERE id::text = %s
                     FOR UPDATE
                    """,
                    (str(eviction_id),),
                )
                row = cur.fetchone()
                if row is None:
                    return None
                current = _gpu_eviction_request_from_row(row)
                if current["status"] in GPU_EVICTION_TERMINAL_STATUSES:
                    return current
                if current["status"] == "running":
                    return _gpu_eviction_cas_rejected(eviction_id)
                claim_token = uuid4().hex
                deadlines = _gpu_eviction_deadlines(delivery_count=int(current.get("broker_delivery_count") or 0) + 1)
                cur.execute(
                    """
                    UPDATE gpu_evictions
                       SET status = 'running',
                           started_at = COALESCE(started_at, now()),
                           broker_message_id = COALESCE(%s, broker_message_id),
                           broker_delivery_count = broker_delivery_count + 1,
                           metadata = metadata || %s::jsonb,
                           claim_token = %s,
                           row_version = row_version + 1,
                           status_changed_at = now(),
                           heartbeat_at = now(),
                           retry_not_before = NULL,
                           expires_at = now() + (%s * interval '1 second')
                     WHERE id::text = %s
                       AND status IN ('queued', 'retrying')
                     RETURNING """ + _GPU_EVICTION_ROW_COLUMNS + """
                    """,
                    (
                        broker_message_id,
                        _json({"last_worker_id": str(worker_id or "")}),
                        claim_token,
                        deadlines["running_seconds"],
                        str(eviction_id),
                    ),
                )
                updated = cur.fetchone()
                return _gpu_eviction_request_from_row(updated) if updated else _gpu_eviction_cas_rejected(eviction_id)


def complete_gpu_eviction_request(
    *,
    eviction_id: str,
    status: str,
    error: str = "",
    metadata: dict[str, Any] | None = None,
    claim_token: str | None = None,
    row_version: int | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    residency_verification: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    clean_status = status if status in GPU_EVICTION_TERMINAL_STATUSES and status != "expired" else "failed"
    if not claim_token or row_version is None:
        return _gpu_eviction_cas_rejected(eviction_id)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                cur.execute(
                    """
                    UPDATE gpu_evictions
                       SET status = %s,
                           error = %s,
                           completed_at = now(),
                           metadata = metadata || %s::jsonb,
                           row_version = row_version + 1,
                           status_changed_at = now(),
                           heartbeat_at = now(),
                           expires_at = NULL
                     WHERE id::text = %s
                       AND status = 'running'
                       AND claim_token = %s
                       AND row_version = %s
                     RETURNING """ + _GPU_EVICTION_ROW_COLUMNS + """
                    """,
                    (clean_status, str(error or "")[:1000], _json(metadata or {}), str(eviction_id), claim_token, int(row_version)),
                )
                row = cur.fetchone()
                if row is None:
                    return _gpu_eviction_cas_rejected(eviction_id)
                request = _gpu_eviction_request_from_row(row)
                if residency_verification:
                    _update_gpu_residency_verification_with_cursor(cur, **residency_verification)
                _enqueue_gpu_eviction_event_with_cursor(
                    cur,
                    request=request,
                    event_status=clean_status,
                    correlation_id=correlation_id or request.get("correlation_id"),
                    causation_id=causation_id or request.get("broker_message_id"),
                )
                return request


def retry_gpu_eviction_request(
    *,
    eviction_id: str,
    error: str,
    metadata: dict[str, Any] | None = None,
    claim_token: str | None = None,
    row_version: int | None = None,
    broker_delivery_count: int | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    if not claim_token or row_version is None:
        return _gpu_eviction_cas_rejected(eviction_id)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                deadlines = _gpu_eviction_deadlines(delivery_count=max(0, int(broker_delivery_count or 0)))
                cur.execute(
                    """
                    UPDATE gpu_evictions
                       SET status = 'retrying',
                           error = %s,
                           completed_at = NULL,
                           metadata = metadata || %s::jsonb,
                           row_version = row_version + 1,
                           status_changed_at = now(),
                           heartbeat_at = now(),
                           retry_not_before = now() + (%s * interval '1 second'),
                           expires_at = now() + (%s * interval '1 second')
                     WHERE id::text = %s
                       AND status = 'running'
                       AND claim_token = %s
                       AND row_version = %s
                     RETURNING """ + _GPU_EVICTION_ROW_COLUMNS + """
                    """,
                    (str(error or "")[:1000], _json(metadata or {}), deadlines["retry_delay_seconds"], deadlines["retry_seconds"], str(eviction_id), claim_token, int(row_version)),
                )
                row = cur.fetchone()
                if row is None:
                    return _gpu_eviction_cas_rejected(eviction_id)
                request = _gpu_eviction_request_from_row(row)
                _enqueue_gpu_eviction_event_with_cursor(
                    cur,
                    request=request,
                    event_status="retrying",
                    correlation_id=correlation_id or request.get("correlation_id"),
                    causation_id=causation_id or request.get("broker_message_id"),
                )
                return request


def _gpu_eviction_request_from_row(row: Any) -> dict[str, Any]:
    return {
        "id": row[0],
        "eviction_id": row[0],
        "lease_id": row[1],
        "task_type": row[2],
        "model_id": row[3],
        "component": row[4],
        "status": row[5],
        "estimated_freed_vram_mb": int(row[6] or 0),
        "error": row[7] or "",
        "metadata": row[8] or {},
        "broker_message_id": row[9],
        "routing_key": row[10],
        "correlation_id": row[11],
        "causation_id": row[12],
        "broker_delivery_count": int(row[13] or 0),
        "runtime_generation": (row[14] or "") if len(row) > 14 else "",
        "runtime_activity_sequence": int(row[15] or 0) if len(row) > 15 else 0,
        "claim_token": (row[16] or "") if len(row) > 16 else "",
        "row_version": int(row[17] or 0) if len(row) > 17 else 0,
        "status_changed_at": _iso_or_none(row[18]) if len(row) > 18 else None,
        "heartbeat_at": _iso_or_none(row[19]) if len(row) > 19 else None,
        "retry_not_before": _iso_or_none(row[20]) if len(row) > 20 else None,
        "expires_at": _iso_or_none(row[21]) if len(row) > 21 else None,
        "request_reason": (row[22] or "demand") if len(row) > 22 else "demand",
        "terminal_reason": (row[23] or "") if len(row) > 23 else "",
        "reconciliation_observation_id": (row[24] or "") if len(row) > 24 else "",
    }


def heartbeat_gpu_eviction_request(
    *, eviction_id: str, claim_token: str, row_version: int, url: str | None = None
) -> dict[str, Any]:
    """Extend a running claim only when its current fence still owns the row."""
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                cur.execute(
                    """
                    UPDATE gpu_evictions
                       SET heartbeat_at = now(), status_changed_at = now(), row_version = row_version + 1,
                           expires_at = now() + (%s * interval '1 second')
                     WHERE id::text = %s AND status = 'running' AND claim_token = %s AND row_version = %s
                     RETURNING """ + _GPU_EVICTION_ROW_COLUMNS,
                    (_gpu_eviction_deadlines()["running_seconds"], str(eviction_id), claim_token, int(row_version)),
                )
                row = cur.fetchone()
                return _gpu_eviction_request_from_row(row) if row else _gpu_eviction_cas_rejected(eviction_id)


def list_active_gpu_leases(*, url: str | None = None) -> list[dict[str, Any]]:
    """Return current running GPU leases for eviction quiet-window fencing."""
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, task_type, model_id, component
                  FROM gpu_leases
                 WHERE status = 'running'
                """
            )
            return [
                {"id": row[0], "task_type": row[1], "model_id": row[2], "component": row[3]}
                for row in cur.fetchall()
            ]


def update_gpu_residency_verification(
    *,
    task_type: str,
    model_id: str,
    runtime_state: str,
    failure_reason: str = "",
    observation_id: str = "",
    owner_component: str = "",
    runtime_generation: str = "",
    runtime_activity_sequence: int | None = None,
    runtime_fingerprint: str = "",
    replace_runtime_identity: bool = False,
    capacity_state: str = "",
    url: str | None = None,
) -> None:
    """Persist a failed/unverified runtime result without clearing owner evidence."""
    allowed = {"unload_failed", "memory_release_unverified"}
    if runtime_state not in allowed:
        raise ValueError(f"unsupported GPU residency verification state: {runtime_state}")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                _update_gpu_residency_verification_with_cursor(
                    cur,
                    task_type=task_type,
                    model_id=model_id,
                    runtime_state=runtime_state,
                    failure_reason=failure_reason,
                    observation_id=observation_id,
                    owner_component=owner_component,
                    runtime_generation=runtime_generation,
                    runtime_activity_sequence=runtime_activity_sequence,
                    runtime_fingerprint=runtime_fingerprint,
                    replace_runtime_identity=replace_runtime_identity,
                    capacity_state=capacity_state,
                )


def _update_gpu_residency_verification_with_cursor(
    cur: Any,
    *,
    task_type: str,
    model_id: str,
    runtime_state: str,
    failure_reason: str = "",
    observation_id: str = "",
    owner_component: str = "",
    runtime_generation: str = "",
    runtime_activity_sequence: int | None = None,
    runtime_fingerprint: str = "",
    replace_runtime_identity: bool = False,
    capacity_state: str = "",
) -> None:
    cur.execute(
        """
        UPDATE gpu_model_residency
           SET runtime_state = %s,
               runtime_failure_reason = %s,
               owner_component = CASE WHEN %s THEN %s ELSE COALESCE(NULLIF(%s, ''), owner_component) END,
               runtime_generation = CASE WHEN %s THEN %s ELSE COALESCE(NULLIF(%s, ''), runtime_generation) END,
               runtime_activity_sequence = CASE WHEN %s THEN %s ELSE COALESCE(%s, runtime_activity_sequence) END,
               runtime_fingerprint = CASE WHEN %s THEN %s ELSE COALESCE(NULLIF(%s, ''), runtime_fingerprint) END,
               runtime_observed_at = now(),
               metadata = COALESCE(metadata, '{}'::jsonb) || jsonb_strip_nulls(jsonb_build_object(
                   'runtime_verification_observation_id', %s::text,
                   'runtime_verification_capacity_state', NULLIF(%s::text, '')
               ))
         WHERE task_type = %s AND model_id = %s
        """,
        (
            runtime_state,
            str(failure_reason or "")[:300],
            bool(replace_runtime_identity),
            str(owner_component or ""),
            str(owner_component or ""),
            bool(replace_runtime_identity),
            str(runtime_generation or ""),
            str(runtime_generation or ""),
            bool(replace_runtime_identity),
            runtime_activity_sequence,
            runtime_activity_sequence,
            bool(replace_runtime_identity),
            str(runtime_fingerprint or ""),
            str(runtime_fingerprint or ""),
            str(observation_id or ""),
            str(capacity_state or ""),
            str(task_type),
            str(model_id),
        ),
    )


def _expire_stale_gpu_eviction_requests_with_cursor(cur: Any) -> list[dict[str, Any]]:
    cur.execute(
        """
        UPDATE gpu_evictions
           SET status = 'expired', terminal_reason = 'stale_request_expired', completed_at = now(),
               status_changed_at = now(), row_version = row_version + 1, expires_at = NULL,
               metadata = metadata || '{"terminal_reason":"stale_request_expired"}'::jsonb
         WHERE status IN ('queued', 'running', 'retrying') AND expires_at IS NOT NULL AND expires_at <= now()
         RETURNING """ + _GPU_EVICTION_ROW_COLUMNS
    )
    rows = cur.fetchall() if hasattr(cur, "fetchall") else []
    return [_gpu_eviction_request_from_row(row) for row in rows]


def expire_stale_gpu_eviction_requests(
    *, url: str | None = None, connection: Any | None = None,
) -> list[dict[str, Any]]:
    """Terminally expire stale rows while retaining their audit history."""
    connection_context = nullcontext(connection) if connection is not None else _load_psycopg().connect(url or database_url())
    with connection_context as conn:
        with conn.transaction():
            with conn.cursor() as cur:
                expired = _expire_stale_gpu_eviction_requests_with_cursor(cur)
                for request in expired:
                    _enqueue_gpu_eviction_event_with_cursor(
                        cur, request=request, event_status="expired",
                        correlation_id=request.get("correlation_id"), causation_id=request.get("broker_message_id"),
                    )
                return expired


def list_idle_gpu_eviction_candidates(
    *, idle_unload_seconds: float, url: str | None = None, connection: Any | None = None,
) -> list[dict[str, Any]]:
    """Return idle, runtime-confirmed residents that have no protected GPU work.

    This is intentionally a selection-only read: the caller must enqueue each
    candidate through ``enqueue_gpu_eviction_request`` so the existing logical
    candidate lock and generation/activity fences remain authoritative.
    """
    idle_seconds = max(0.0, float(idle_unload_seconds))
    if idle_seconds <= 0:
        return []
    connection_context = nullcontext(connection) if connection is not None else _load_psycopg().connect(url or database_url())
    with connection_context as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT r.task_type, r.model_id, r.estimated_vram_mb,
                       r.owner_component, r.runtime_generation, r.runtime_activity_sequence
                  FROM gpu_model_residency r
                 WHERE r.resident = true
                   AND r.runtime_state = 'present'
                   AND r.last_operation_completed_at IS NOT NULL
                   AND r.last_operation_completed_at <= now() - (%s * interval '1 second')
                   AND r.runtime_in_flight = 0
                   AND (r.last_operation_started_at IS NULL OR r.last_operation_started_at <= r.last_operation_completed_at)
                   AND NOT EXISTS (
                       SELECT 1 FROM gpu_leases running
                        WHERE running.status = 'running'
                   )
                   AND NOT EXISTS (
                       SELECT 1 FROM gpu_leases waiting
                        WHERE waiting.status = 'waiting'
                          AND waiting.caller_attached = true
                          AND waiting.task_type = r.task_type
                          AND waiting.model_id = r.model_id
                   )
                   AND NOT EXISTS (
                       SELECT 1 FROM gpu_evictions active
                        WHERE active.task_type = r.task_type
                          AND active.model_id = r.model_id
                          AND active.component = r.owner_component
                          AND active.status IN ('queued', 'running', 'retrying')
                   )
                 ORDER BY r.last_operation_completed_at ASC, r.task_type ASC, r.model_id ASC
                """,
                (idle_seconds,),
            )
            return [
                {
                    "task_type": str(row[0] or ""),
                    "model_id": str(row[1] or ""),
                    "estimated_vram_mb": int(row[2] or 0),
                    "component": str(row[3] or ""),
                    "runtime_generation": str(row[4] or ""),
                    "runtime_activity_sequence": int(row[5] or 0),
                }
                for row in cur.fetchall()
            ]


def list_gpu_lease_jobs(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id, task_type, model_id, status, estimated_vram_mb, exclusive,
                       share_group, priority, component, request_id, created_at,
                       granted_at, heartbeat_at, expires_at, released_at, metadata,
                       broker_message_id, routing_key, broker_delivery_count
                FROM gpu_leases
                ORDER BY COALESCE(released_at, heartbeat_at, granted_at, created_at) DESC
                LIMIT %s
                """,
                (_dashboard_job_limit(limit),),
            )
            return [
                {
                    "id": row[0],
                    "task_type": row[1],
                    "model_id": row[2],
                    "status": row[3],
                    "estimated_vram_mb": int(row[4] or 0),
                    "exclusive": bool(row[5]),
                    "share_group": row[6],
                    "priority": int(row[7] or 0),
                    "component": row[8],
                    "request_id": row[9],
                    "created_at": _iso_or_none(row[10]),
                    "granted_at": _iso_or_none(row[11]),
                    "heartbeat_at": _iso_or_none(row[12]),
                    "expires_at": _iso_or_none(row[13]),
                    "released_at": _iso_or_none(row[14]),
                    "metadata": row[15] or {},
                    "broker_message_id": row[16],
                    "routing_key": row[17],
                    "broker_delivery_count": int(row[18] or 0),
                }
                for row in cur.fetchall()
            ]


def list_gpu_eviction_jobs(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, lease_id, task_type, model_id, component, status,
                       estimated_freed_vram_mb, error, created_at, completed_at,
                       metadata, broker_message_id, routing_key, correlation_id,
                       causation_id, queued_at, started_at, broker_delivery_count
                FROM gpu_evictions
                ORDER BY COALESCE(completed_at, started_at, queued_at, created_at) DESC
                LIMIT %s
                """,
                (_dashboard_job_limit(limit),),
            )
            return [
                {
                    "id": row[0],
                    "lease_id": row[1],
                    "task_type": row[2],
                    "model_id": row[3],
                    "component": row[4],
                    "status": row[5],
                    "estimated_freed_vram_mb": int(row[6] or 0),
                    "error": row[7] or "",
                    "created_at": _iso_or_none(row[8]),
                    "completed_at": _iso_or_none(row[9]),
                    "metadata": row[10] or {},
                    "broker_message_id": row[11],
                    "routing_key": row[12],
                    "correlation_id": row[13],
                    "causation_id": row[14],
                    "queued_at": _iso_or_none(row[15]),
                    "started_at": _iso_or_none(row[16]),
                    "broker_delivery_count": int(row[17] or 0),
                }
                for row in cur.fetchall()
            ]


def _enqueue_gpu_eviction_event_with_cursor(
    cur: Any,
    *,
    request: dict[str, Any],
    event_status: str,
    correlation_id: str | None = None,
    causation_id: str | None = None,
) -> dict[str, Any]:
    payload = {
        "eviction_id": str(request.get("id") or request.get("eviction_id") or ""),
        "lease_id": str(request.get("lease_id") or ""),
        "status": str(event_status or request.get("status") or ""),
        "task_type": str(request.get("task_type") or ""),
        "model_id": str(request.get("model_id") or ""),
        "component": str(request.get("component") or ""),
        "error": str(request.get("error") or ""),
    }
    return _enqueue_message_outbox_with_cursor(
        cur,
        exchange=messaging.EVENTS_EXCHANGE,
        routing_key=f"gpu.eviction.{event_status}",
        message_type=f"flux.gpu.eviction.{event_status}",
        payload=payload,
        aggregate_type="gpu_evictions",
        aggregate_id=payload["eviction_id"],
        correlation_id=correlation_id,
        causation_id=causation_id,
    )


def _stable_message_id(*parts: str) -> str:
    material = ":".join(str(part or "") for part in parts)
    return str(uuid5(NAMESPACE_URL, f"flux-llm-kb:{material}"))


def attach_callback_to_capture_jobs(
    *,
    job_ids: list[str] | tuple[str, ...],
    operation_id: str,
    callback_url: str,
    url: str | None = None,
) -> dict[str, Any]:
    clean_job_ids = [str(job_id) for job_id in job_ids if str(job_id or "").strip()]
    if not clean_job_ids:
        return {"attached": 0, "job_ids": []}
    metadata = {
        "event_callback": {
            "operation_id": str(operation_id),
            "callback_url": str(callback_url),
            "requested_at": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        }
    }
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET telemetry = telemetry || %s::jsonb,
                    updated_at = now()
                WHERE id::text = ANY(%s)
                  AND delete_requested_at IS NULL
                RETURNING id::text
                """,
                (_json(metadata), clean_job_ids),
            )
            attached = [row[0] for row in cur.fetchall()]
            return {"attached": len(attached), "job_ids": attached}


def enqueue_callback_delivery(
    *,
    event_message_id: str,
    job_id: str | None,
    callback_url: str,
    payload: dict[str, Any],
    correlation_id: str | None = None,
    causation_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    callback_message_id = _stable_message_id(
        "callback-delivery",
        str(event_message_id or ""),
        str(job_id or ""),
        str(callback_url or ""),
    )
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO callback_deliveries (
                    message_id, event_message_id, job_id, callback_url, status,
                    idempotency_key, payload
                )
                VALUES (%s, %s, NULLIF(%s, '')::uuid, %s, 'pending', %s, %s::jsonb)
                ON CONFLICT (message_id) DO UPDATE SET
                    updated_at = callback_deliveries.updated_at
                RETURNING id::text, message_id, status
                """,
                (
                    callback_message_id,
                    event_message_id,
                    str(job_id or ""),
                    callback_url,
                    callback_message_id,
                    _json(payload or {}),
                ),
            )
            delivery = cur.fetchone()
            outbox = _enqueue_message_outbox_with_cursor(
                cur,
                message_id=callback_message_id,
                exchange=messaging.CALLBACKS_EXCHANGE,
                routing_key=messaging.CALLBACK_DISPATCH_ROUTING_KEY,
                message_type="flux.callback.dispatch",
                payload={"callback_delivery_id": delivery[0], "job_id": str(job_id or "")},
                aggregate_type="callback_deliveries",
                aggregate_id=delivery[0],
                correlation_id=correlation_id,
                causation_id=causation_id or event_message_id,
            )
            return {
                "id": delivery[0],
                "message_id": delivery[1],
                "status": delivery[2],
                "outbox_message_id": outbox.get("message_id"),
            }


def claim_callback_delivery(
    *,
    delivery_id: str,
    worker_id: str,
    broker_message_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE callback_deliveries
                SET status = 'running',
                    attempts = attempts + 1,
                    headers = headers || %s::jsonb,
                    updated_at = now()
                WHERE id = %s
                  AND status IN ('pending', 'retrying')
                  AND next_attempt_at <= now()
                RETURNING id::text, message_id, event_message_id, job_id::text,
                          callback_url, status, attempts, idempotency_key, headers, payload
                """,
                (_json({"worker_id": worker_id, "broker_message_id": broker_message_id}), delivery_id),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "id": row[0],
                "message_id": row[1],
                "event_message_id": row[2],
                "job_id": row[3],
                "callback_url": row[4],
                "status": row[5],
                "attempts": int(row[6] or 0),
                "idempotency_key": row[7],
                "headers": row[8] or {},
                "payload": row[9] or {},
            }


def complete_callback_delivery(
    *,
    delivery_id: str,
    status: str,
    status_code: int | None = None,
    error: str | None = None,
    retry_backoff_seconds: int = OUTBOX_RETRY_BACKOFF_SECONDS,
    url: str | None = None,
) -> None:
    clean_status = status if status in {"delivered", "retrying", "failed", "blocked"} else "failed"
    retry_seconds = max(1, int(retry_backoff_seconds or 1))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE callback_deliveries
                SET status = %s,
                    last_status_code = %s,
                    last_error = %s,
                    next_attempt_at = CASE
                        WHEN %s = 'retrying' THEN now() + make_interval(secs => %s)
                        ELSE next_attempt_at
                    END,
                    completed_at = CASE
                        WHEN %s IN ('delivered', 'failed', 'blocked') THEN now()
                        ELSE completed_at
                    END,
                    updated_at = now()
                WHERE id = %s
                """,
                (
                    clean_status,
                    status_code,
                    str(error or "")[:1000] if error else None,
                    clean_status,
                    retry_seconds,
                    clean_status,
                    delivery_id,
                ),
            )


def message_queue_status(*, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT
                    count(*) FILTER (WHERE status = 'pending')::integer AS pending,
                    count(*) FILTER (WHERE status = 'publishing')::integer AS publishing,
                    count(*) FILTER (WHERE status = 'failed')::integer AS failed,
                    count(*) FILTER (WHERE status = 'published')::integer AS published,
                    COALESCE(EXTRACT(EPOCH FROM (now() - min(created_at)))::integer, 0) AS oldest_pending_age_seconds
                FROM message_outbox
                WHERE status IN ('pending', 'publishing', 'failed', 'published')
                """
            )
            outbox = cur.fetchone()
            cur.execute(
                """
                SELECT
                    count(*) FILTER (WHERE status = 'processing')::integer AS processing,
                    count(*) FILTER (WHERE status = 'handled')::integer AS handled,
                    count(*) FILTER (WHERE status = 'failed')::integer AS failed
                FROM message_inbox
                """
            )
            inbox = cur.fetchone()
            cur.execute(
                """
                SELECT
                    count(*) FILTER (WHERE status IN ('pending', 'retrying'))::integer AS pending,
                    count(*) FILTER (WHERE status = 'failed')::integer AS failed,
                    count(*) FILTER (WHERE status = 'blocked')::integer AS blocked,
                    count(*) FILTER (WHERE status = 'delivered')::integer AS delivered
                FROM callback_deliveries
                """
            )
            callbacks = cur.fetchone()
            cur.execute(
                """
                SELECT subscriber_name, count(*)::integer, max(received_at)
                FROM event_journal
                GROUP BY subscriber_name
                ORDER BY subscriber_name
                """
            )
            event_rows = cur.fetchall()
            event_journal = {
                "total": sum(int(row[1] or 0) for row in event_rows),
                "subscribers": [
                    {
                        "subscriber_name": str(row[0] or ""),
                        "received": int(row[1] or 0),
                        "last_received_at": row[2].isoformat() if row[2] else None,
                    }
                    for row in event_rows
                ],
            }
            return {
                "outbox": {
                    "pending": int(outbox[0] or 0),
                    "publishing": int(outbox[1] or 0),
                    "failed": int(outbox[2] or 0),
                    "published": int(outbox[3] or 0),
                    "oldest_pending_age_seconds": int(outbox[4] or 0),
                },
                "inbox": {
                    "processing": int(inbox[0] or 0),
                    "handled": int(inbox[1] or 0),
                    "failed": int(inbox[2] or 0),
                },
                "callbacks": {
                    "pending": int(callbacks[0] or 0),
                    "failed": int(callbacks[1] or 0),
                    "blocked": int(callbacks[2] or 0),
                    "delivered": int(callbacks[3] or 0),
                },
                "event_journal": event_journal,
            }


def _dashboard_job_limit(limit: int | str | None, *, default: int = 50, maximum: int = 1000) -> int:
    try:
        numeric = int(limit if limit is not None else default)
    except (TypeError, ValueError):
        numeric = default
    return max(1, min(numeric, maximum))


def _iso_or_none(value: Any) -> str | None:
    return value.isoformat() if value else None


def list_message_outbox_jobs(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, message_id, exchange, routing_key, message_type,
                       correlation_id, causation_id, aggregate_type, aggregate_id,
                       payload, headers, status, attempts, next_attempt_at,
                       locked_at, locked_by, published_at, broker_message_id,
                       last_error, created_at, updated_at
                FROM message_outbox
                WHERE status IN ('pending', 'publishing', 'failed')
                ORDER BY updated_at DESC, created_at DESC
                LIMIT %s
                """,
                (_dashboard_job_limit(limit),),
            )
            return [
                {
                    "id": row[0],
                    "message_id": row[1],
                    "exchange": row[2],
                    "routing_key": row[3],
                    "message_type": row[4],
                    "correlation_id": row[5],
                    "causation_id": row[6],
                    "aggregate_type": row[7],
                    "aggregate_id": row[8],
                    "payload": row[9] or {},
                    "headers": row[10] or {},
                    "status": row[11],
                    "attempts": int(row[12] or 0),
                    "next_attempt_at": _iso_or_none(row[13]),
                    "locked_at": _iso_or_none(row[14]),
                    "locked_by": row[15],
                    "published_at": _iso_or_none(row[16]),
                    "broker_message_id": row[17],
                    "last_error": row[18],
                    "created_at": _iso_or_none(row[19]),
                    "updated_at": _iso_or_none(row[20]),
                }
                for row in cur.fetchall()
            ]


def list_message_inbox_jobs(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT consumer_name, message_id, message_type, status, attempts,
                       first_seen_at, last_seen_at, handled_at, last_error, metadata
                FROM message_inbox
                WHERE status IN ('processing', 'failed')
                ORDER BY last_seen_at DESC, first_seen_at DESC
                LIMIT %s
                """,
                (_dashboard_job_limit(limit),),
            )
            return [
                {
                    "consumer_name": row[0],
                    "message_id": row[1],
                    "message_type": row[2],
                    "status": row[3],
                    "attempts": int(row[4] or 0),
                    "first_seen_at": _iso_or_none(row[5]),
                    "last_seen_at": _iso_or_none(row[6]),
                    "handled_at": _iso_or_none(row[7]),
                    "last_error": row[8],
                    "metadata": row[9] or {},
                }
                for row in cur.fetchall()
            ]


def list_callback_delivery_jobs(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, message_id, event_message_id, job_id::text,
                       status, attempts, next_attempt_at, last_status_code,
                       last_error, payload, created_at, updated_at, completed_at
                FROM callback_deliveries
                WHERE status IN ('pending', 'running', 'retrying', 'failed', 'blocked', 'delivered')
                ORDER BY updated_at DESC, created_at DESC
                LIMIT %s
                """,
                (_dashboard_job_limit(limit),),
            )
            return [
                {
                    "id": row[0],
                    "message_id": row[1],
                    "event_message_id": row[2],
                    "job_id": row[3],
                    "status": row[4],
                    "attempts": int(row[5] or 0),
                    "next_attempt_at": _iso_or_none(row[6]),
                    "last_status_code": row[7],
                    "last_error": row[8],
                    "payload": row[9] or {},
                    "created_at": _iso_or_none(row[10]),
                    "updated_at": _iso_or_none(row[11]),
                    "completed_at": _iso_or_none(row[12]),
                }
                for row in cur.fetchall()
            ]


def add_monitored_root(
    *,
    name: str,
    root_path: str | Path,
    recursive: bool = True,
    watch_enabled: bool = False,
    enabled: bool = True,
    trust_rank: int = 500,
    include_globs: list[str] | None = None,
    exclude_globs: list[str] | None = None,
    glob_mode: str = "extend",
    max_inline_bytes: int = 256 * 1024,
    heavy_threshold_bytes: int = 10 * 1024 * 1024,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    resolved_path = _normalize_root_path(root_path)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO monitored_roots (
                    name, root_path, enabled, recursive, watch_enabled, trust_rank,
                    include_globs, exclude_globs, glob_mode, max_inline_bytes, heavy_threshold_bytes, metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb)
                ON CONFLICT (name) DO UPDATE SET
                    root_path = EXCLUDED.root_path,
                    enabled = EXCLUDED.enabled,
                    recursive = EXCLUDED.recursive,
                    watch_enabled = EXCLUDED.watch_enabled,
                    trust_rank = EXCLUDED.trust_rank,
                    include_globs = EXCLUDED.include_globs,
                    exclude_globs = EXCLUDED.exclude_globs,
                    glob_mode = EXCLUDED.glob_mode,
                    max_inline_bytes = EXCLUDED.max_inline_bytes,
                    heavy_threshold_bytes = EXCLUDED.heavy_threshold_bytes,
                    metadata = EXCLUDED.metadata,
                    updated_at = now()
                RETURNING id::text, name, root_path, enabled, recursive, watch_enabled,
                          trust_rank, include_globs, exclude_globs, glob_mode, max_inline_bytes,
                          heavy_threshold_bytes, metadata
                """,
                (
                    name,
                    resolved_path,
                    enabled,
                    recursive,
                    watch_enabled,
                    trust_rank,
                    include_globs or [],
                    exclude_globs or [],
                    glob_mode,
                    max_inline_bytes,
                    heavy_threshold_bytes,
                    _json(metadata or {}),
                ),
            )
            row = cur.fetchone()
            if _metadata_strict_indexing_enabled(row[12]):
                _block_metadata_only_assets_for_strict_root(cur, root_id=row[0])
            _upsert_watcher_state(cur, row[0], "enabled" if watch_enabled else "disabled")
            return _root_row(row)


def list_monitored_roots(*, watch_enabled: bool | None = None, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    where = "WHERE watch_enabled = %s" if watch_enabled is not None else ""
    params: tuple[Any, ...] = (watch_enabled,) if watch_enabled is not None else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, name, root_path, enabled, recursive, watch_enabled,
                       trust_rank, include_globs, exclude_globs, glob_mode, max_inline_bytes,
                       heavy_threshold_bytes, metadata
                FROM monitored_roots
                {where}
                ORDER BY name
                """,
                params,
            )
            return [_root_row(row) for row in cur.fetchall()]


def get_monitored_root(name: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, name, root_path, enabled, recursive, watch_enabled,
                       trust_rank, include_globs, exclude_globs, glob_mode, max_inline_bytes,
                       heavy_threshold_bytes, metadata
                FROM monitored_roots
                WHERE name = %s
                """,
                (name,),
            )
            row = cur.fetchone()
            return _root_row(row) if row else None


def get_monitored_root_by_identifier(identifier: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, name, root_path, enabled, recursive, watch_enabled,
                       trust_rank, include_globs, exclude_globs, glob_mode, max_inline_bytes,
                       heavy_threshold_bytes, metadata
                FROM monitored_roots
                WHERE id::text = %s OR name = %s
                """,
                (identifier, identifier),
            )
            row = cur.fetchone()
            return _root_row(row) if row else None


def update_monitored_root(
    *,
    root_id: str,
    name: str,
    root_path: str | Path,
    enabled: bool = True,
    recursive: bool = True,
    watch_enabled: bool = False,
    trust_rank: int = 500,
    include_globs: list[str] | None = None,
    exclude_globs: list[str] | None = None,
    glob_mode: str = "extend",
    max_inline_bytes: int = 256 * 1024,
    heavy_threshold_bytes: int = 10 * 1024 * 1024,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    resolved_path = _normalize_root_path(root_path)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id::text, name FROM monitored_roots WHERE id::text = %s OR name = %s", (root_id, root_id))
            current = cur.fetchone()
            if not current:
                raise ValueError(f"monitored root not found: {root_id}")
            actual_root_id, previous_name = current
            cur.execute(
                """
                UPDATE monitored_roots
                SET name = %s,
                    root_path = %s,
                    enabled = %s,
                    recursive = %s,
                    watch_enabled = %s,
                    trust_rank = %s,
                    include_globs = %s,
                    exclude_globs = %s,
                    glob_mode = %s,
                    max_inline_bytes = %s,
                    heavy_threshold_bytes = %s,
                    metadata = metadata || %s::jsonb,
                    updated_at = now()
                WHERE id = %s
                RETURNING id::text, name, root_path, enabled, recursive, watch_enabled,
                          trust_rank, include_globs, exclude_globs, glob_mode, max_inline_bytes,
                          heavy_threshold_bytes, metadata
                """,
                (
                    name,
                    resolved_path,
                    enabled,
                    recursive,
                    watch_enabled,
                    trust_rank,
                    include_globs or [],
                    exclude_globs or [],
                    glob_mode,
                    max_inline_bytes,
                    heavy_threshold_bytes,
                    _json(metadata or {}),
                    actual_root_id,
                ),
            )
            row = cur.fetchone()
            if _metadata_strict_indexing_enabled(row[12]):
                _block_metadata_only_assets_for_strict_root(cur, root_id=actual_root_id)
            if previous_name != name:
                cur.execute(
                    """
                    UPDATE capture_jobs
                    SET payload = jsonb_set(payload, '{root_name}', to_jsonb(%s::text), true),
                        updated_at = now()
                    WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                      AND payload->>'root_name' = %s
                    """,
                    (name, previous_name),
                )
            _upsert_watcher_state(cur, actual_root_id, "enabled" if watch_enabled else "disabled")
            return _root_row(row)


def delete_monitored_root(
    *,
    root_id: str,
    purge_index: bool = True,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    if not purge_index:
        raise ValueError("deleting a monitored root requires purge_index=true")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id::text, name FROM monitored_roots WHERE id::text = %s OR name = %s", (root_id, root_id))
            row = cur.fetchone()
            if not row:
                return {"id": root_id, "name": None, "deleted": False, "purged_index": purge_index}
            actual_root_id, name = row
            root_source_hashes, root_frame_timestamps = _collect_root_cache_inputs(cur, root_id=actual_root_id)
            active_source_hashes, active_frame_timestamps = _collect_active_cache_inputs(cur, exclude_root_id=actual_root_id)
            sidecar_cleanup = delete_managed_mail_sidecars_for_root(root_name=name)
            cache_cleanup = purge_derived_cache_entries(
                source_hashes=root_source_hashes,
                active_source_hashes=active_source_hashes,
                frame_timestamps_by_hash=root_frame_timestamps,
                active_frame_timestamps_by_hash=active_frame_timestamps,
                purge_unreferenced=False,
                dry_run=False,
            )
            search_index_cleanup = purge_corpus_search_index_for_roots(root_names=[name], confirmed=True)
            cur.execute(
                """
                UPDATE monitored_roots
                SET enabled = false, watch_enabled = false, updated_at = now()
                WHERE id = %s
                """,
                (actual_root_id,),
            )
            cur.execute(
                """
                UPDATE source_assets
                SET canonical_asset_id = NULL, updated_at = now()
                WHERE canonical_asset_id IN (
                    SELECT id FROM source_assets WHERE root_id = %s
                )
                """,
                (actual_root_id,),
            )
            cur.execute(
                """
                DELETE FROM asset_chunks
                WHERE asset_id IN (SELECT id FROM source_assets WHERE root_id = %s)
                """,
                (actual_root_id,),
            )
            cur.execute("DELETE FROM source_assets WHERE root_id = %s", (actual_root_id,))
            cur.execute(
                """
                DELETE FROM capture_jobs
                WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND payload->>'root_name' = %s
                """,
                (name,),
            )
            cur.execute("DELETE FROM crawl_runs WHERE root_id = %s", (actual_root_id,))
            cur.execute("DELETE FROM watcher_state WHERE root_id = %s", (actual_root_id,))
            cur.execute("DELETE FROM monitored_roots WHERE id = %s", (actual_root_id,))
            cur.execute(
                """
                INSERT INTO audit_events (event_type, actor, target_table, target_id, details)
                VALUES ('monitored_root.deleted', %s, 'monitored_roots', %s, %s::jsonb)
                """,
                (
                    actor,
                    actual_root_id,
                    _json(
                        {
                            "name": name,
                            "purged_index": purge_index,
                            "sidecar_cleanup": sidecar_cleanup,
                            "cache_cleanup": cache_cleanup,
                            "search_index_cleanup": search_index_cleanup,
                        }
                    ),
                ),
            )
            return {
                "id": actual_root_id,
                "name": name,
                "deleted": True,
                "purged_index": purge_index,
            }


def set_watch_enabled(
    *, root_name: str | None = None, enabled: bool, url: str | None = None
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            if root_name:
                cur.execute(
                    """
                    UPDATE monitored_roots
                    SET watch_enabled = %s, updated_at = now()
                    WHERE name = %s
                    RETURNING id::text
                    """,
                    (enabled, root_name),
                )
            else:
                cur.execute(
                    """
                    UPDATE monitored_roots
                    SET watch_enabled = %s, updated_at = now()
                    RETURNING id::text
                    """,
                    (enabled,),
                )
            root_ids = [row[0] for row in cur.fetchall()]
            for root_id in root_ids:
                _upsert_watcher_state(cur, root_id, "enabled" if enabled else "disabled")
            return {"updated": len(root_ids), "root_name": root_name, "watch_enabled": enabled}


def record_watcher_heartbeat(
    *,
    root_name: str,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    _update_watcher_state(root_name=root_name, status="running", heartbeat=True, metadata=metadata, url=url)


def record_watch_event(
    *,
    root_name: str,
    action: str = "changed",
    path_hash: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    _update_watcher_state(
        root_name=root_name,
        status="running",
        event=True,
        action=action,
        path_hash=path_hash,
        metadata=metadata,
        url=url,
    )


def record_watch_error(
    *,
    root_name: str,
    error: str,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    _update_watcher_state(root_name=root_name, status="error", error=error, metadata=metadata, url=url)


def list_watch_events(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT e.id::text, r.name, e.action, e.path_hash, e.metadata, e.created_at
                FROM watcher_events e
                JOIN monitored_roots r ON r.id = e.root_id
                ORDER BY e.created_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 200)),),
            )
            return [
                {
                    "id": row[0],
                    "root_name": row[1],
                    "action": row[2],
                    "path_hash": row[3],
                    "metadata": _sanitize_operational_metadata(row[4] or {}),
                    "created_at": row[5].isoformat() if row[5] else None,
                }
                for row in cur.fetchall()
            ]


def persist_crawl_plan(
    *,
    root_name: str,
    plan: CrawlPlan,
    dry_run: bool = False,
    reason: str = "manual_sync",
    unseen_purge_grace_seconds: int = DEFAULT_UNSEEN_ASSET_PURGE_GRACE_SECONDS,
    progress_callback: Callable[[dict[str, Any]], None] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    if dry_run:
        _emit_persist_progress(progress_callback, stage="persisted", files_done=len(plan.assets), files_total=len(plan.assets))
        return {
            "root_name": root_name,
            "root_path": str(plan.root_path),
            "dry_run": True,
            "reason": reason,
            "files_seen": len(plan.assets),
            "jobs_queued": len(plan.deferred_jobs),
            "chunks_indexed": sum(len(asset.chunks) for asset in plan.assets),
            "manifest_skipped_unchanged": sum(1 for asset in plan.assets if asset.metadata.get("manifest_skipped_unchanged")),
        }

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id::text FROM monitored_roots WHERE name = %s", (root_name,))
            root_row = cur.fetchone()
            if not root_row:
                raise ValueError(f"monitored root not found: {root_name}")
            root_id = root_row[0]
            cur.execute(
                """
                INSERT INTO crawl_runs (root_id, status, reason)
                VALUES (%s, 'running', %s)
                RETURNING id::text
                """,
                (root_id, reason),
            )
            run_id = cur.fetchone()[0]
            seen_paths: set[str] = set()
            chunks_indexed = 0
            jobs_queued = 0
            changed = 0
            manifest_skipped_unchanged = 0
            files_total = len(plan.assets)
            _emit_persist_progress(progress_callback, stage="persisting", files_done=0, files_total=files_total)
            for index, asset in enumerate(plan.assets, start=1):
                seen_paths.add(asset.relative_path)
                if asset.metadata.get("manifest_skipped_unchanged"):
                    manifest_skipped_unchanged += 1
                cur.execute(
                    """
                    SELECT id::text, quick_hash, content_hash, extraction_status, extension
                    FROM source_assets
                    WHERE root_id = %s AND path = %s
                    """,
                    (root_id, asset.relative_path),
                )
                previous = cur.fetchone()
                content_hash_matches = bool(
                    previous is not None
                    and previous[2]
                    and asset.content_hash
                    and str(previous[2]) == str(asset.content_hash)
                )
                changed_asset = previous is None or (
                    previous[1] != asset.quick_hash and not content_hash_matches
                )
                if changed_asset:
                    changed += 1
                status = {
                    "inline": "indexed",
                    "deferred": "queued",
                    "metadata_only": "metadata_only",
                }[asset.extraction_tier]
                status = asset.extraction_status or status
                canonical_id = _find_canonical_asset_id(cur, asset.content_hash, previous[0] if previous else None)
                if canonical_id:
                    status = "duplicate_suppressed"
                legacy_metadata_requeue = (
                    previous is not None
                    and not changed_asset
                    and previous[3] == "metadata_only"
                    and asset.extension in REQUEUE_DOCUMENT_EXTENSIONS
                    and asset.extraction_tier == "deferred"
                    and canonical_id is None
                )
                recovered_indexed_asset = (
                    previous is not None
                    and not changed_asset
                    and (previous[3] == "metadata_only" or str(previous[3]).startswith("blocked_"))
                    and status == "indexed"
                    and canonical_id is None
                )
                recovered_deferred_asset = (
                    previous is not None
                    and not changed_asset
                    and (previous[3] == "metadata_only" or str(previous[3]).startswith("blocked_"))
                    and status == "queued"
                    and canonical_id is None
                )
                existing_chunk_count: int | None = None
                if previous is not None and not changed_asset and status == "indexed" and canonical_id is None:
                    cur.execute("SELECT count(*) FROM asset_chunks WHERE asset_id = %s", (previous[0],))
                    count_row = cur.fetchone()
                    existing_chunk_count = int(count_row[0] if count_row else 0)
                repaired_missing_chunks = (
                    previous is not None
                    and not changed_asset
                    and status == "indexed"
                    and canonical_id is None
                    and tuple(asset.chunks)
                    and (
                        bool(asset.metadata.get("manifest_repaired_missing_chunks"))
                        or (existing_chunk_count is not None and existing_chunk_count <= 0)
                    )
                )
                cur.execute(
                    """
                    INSERT INTO source_assets (
                        root_id, path, uri, file_kind, mime_type, extension, size_bytes,
                        mtime_ns, quick_hash, content_hash, canonical_asset_id,
                        extraction_status, extraction_tier, last_seen_at, indexed_at, deleted_at, metadata
                    )
                    VALUES (
                        %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s,
                        now(), CASE WHEN %s = 'indexed' THEN now() ELSE NULL END, NULL, %s::jsonb
                    )
                    ON CONFLICT (root_id, path) DO UPDATE SET
                        uri = EXCLUDED.uri,
                        file_kind = EXCLUDED.file_kind,
                        mime_type = EXCLUDED.mime_type,
                        extension = EXCLUDED.extension,
                        size_bytes = EXCLUDED.size_bytes,
                        mtime_ns = EXCLUDED.mtime_ns,
                        quick_hash = EXCLUDED.quick_hash,
                        content_hash = EXCLUDED.content_hash,
                        canonical_asset_id = EXCLUDED.canonical_asset_id,
                        extraction_status = CASE
                            WHEN EXCLUDED.extraction_status = 'duplicate_suppressed'
                                THEN EXCLUDED.extraction_status
                            WHEN EXCLUDED.extraction_status LIKE 'blocked_%%'
                                 AND EXCLUDED.metadata ? 'metadata_only_blocked'
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash
                                 AND NOT (
                                     source_assets.content_hash IS NOT NULL
                                     AND source_assets.content_hash = EXCLUDED.content_hash
                                 )
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.extraction_status = 'metadata_only'
                                 AND source_assets.extension = ANY(%s)
                                 AND EXCLUDED.extraction_status = 'queued'
                                THEN EXCLUDED.extraction_status
                            WHEN (source_assets.extraction_status = 'metadata_only' OR source_assets.extraction_status LIKE 'blocked_%%')
                                 AND EXCLUDED.extraction_status IN ('indexed', 'queued')
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.extraction_status = 'indexed'
                                 OR source_assets.extraction_status = 'metadata_only'
                                 OR source_assets.extraction_status LIKE 'blocked_%%'
                                THEN source_assets.extraction_status
                            ELSE EXCLUDED.extraction_status
                        END,
                        extraction_tier = EXCLUDED.extraction_tier,
                        last_seen_at = now(),
                        indexed_at = CASE
                            WHEN source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash
                                 AND NOT (
                                     source_assets.content_hash IS NOT NULL
                                     AND source_assets.content_hash = EXCLUDED.content_hash
                                 )
                                THEN EXCLUDED.indexed_at
                            WHEN (source_assets.extraction_status = 'metadata_only' OR source_assets.extraction_status LIKE 'blocked_%%')
                                 AND EXCLUDED.extraction_status = 'indexed'
                                THEN EXCLUDED.indexed_at
                            ELSE source_assets.indexed_at
                        END,
                        deleted_at = NULL,
                        metadata = CASE
                            WHEN (source_assets.extraction_status = 'metadata_only' OR source_assets.extraction_status LIKE 'blocked_%%')
                                 AND EXCLUDED.extraction_status IN ('indexed', 'queued')
                                THEN (
                                    source_assets.metadata
                                    - 'metadata_only_blocked'
                                    - 'readiness_status'
                                    - 'readiness_reason'
                                    - 'original_status'
                                ) || EXCLUDED.metadata
                            ELSE source_assets.metadata || EXCLUDED.metadata
                        END,
                        updated_at = now()
                    RETURNING id::text
                    """,
                    (
                        root_id,
                        asset.relative_path,
                        asset.path.as_uri(),
                        asset.file_kind,
                        asset.mime_type,
                        asset.extension,
                        asset.size_bytes,
                        asset.mtime_ns,
                        asset.quick_hash,
                        asset.content_hash,
                        canonical_id,
                        status,
                        asset.extraction_tier,
                        status,
                        _json({"source": "corpus_crawler", **asset.metadata}),
                        sorted(REQUEUE_DOCUMENT_EXTENSIONS),
                    ),
                )
                asset_id = cur.fetchone()[0]
                cur.execute(
                    """
                    INSERT INTO crawl_path_manifests (
                        root_id, path, size_bytes, mtime_ns, quick_hash, content_hash, metadata, updated_at
                    )
                    VALUES (%s, %s, %s, %s, %s, %s, %s::jsonb, now())
                    ON CONFLICT (root_id, path) DO UPDATE SET
                        size_bytes = EXCLUDED.size_bytes,
                        mtime_ns = EXCLUDED.mtime_ns,
                        quick_hash = EXCLUDED.quick_hash,
                        content_hash = EXCLUDED.content_hash,
                        metadata = crawl_path_manifests.metadata || EXCLUDED.metadata,
                        updated_at = now()
                    """,
                    (
                        root_id,
                        asset.relative_path,
                        asset.size_bytes,
                        asset.mtime_ns,
                        asset.quick_hash,
                        asset.content_hash,
                        _json({"source": "corpus_crawler", **_sanitize_operational_metadata(dict(asset.metadata))}),
                    ),
                )
                if changed_asset or legacy_metadata_requeue or recovered_indexed_asset or recovered_deferred_asset or repaired_missing_chunks:
                    chunks_indexed += _replace_asset_chunks(cur, asset_id, () if canonical_id else asset.chunks)
                    if asset.extraction_tier == "deferred" and not canonical_id:
                        job_type = f"corpus_extract_{asset.file_kind}"
                        schedule = _job_schedule_metadata(job_type)
                        queued = _enqueue_unique_capture_job_with_cursor(
                            cur,
                            job_type=job_type,
                            payload={"root_name": root_name, "path": asset.relative_path},
                            job_family=schedule["job_family"],
                            resource_class=schedule["resource_class"],
                            priority=schedule["priority"],
                            time_budget_seconds=schedule["time_budget_seconds"],
                            telemetry={"stage": "queued", "root_name": root_name, "path": asset.relative_path},
                            audit_details={"job_type": job_type, "root_name": root_name, "path": asset.relative_path},
                        )
                        jobs_queued += 0 if queued["deduped"] else 1
                    elif asset.extraction_status == "retrying_locked" and not canonical_id:
                        job_type = f"corpus_extract_{asset.file_kind}"
                        schedule = _job_schedule_metadata(job_type)
                        queued = _enqueue_unique_capture_job_with_cursor(
                            cur,
                            job_type=job_type,
                            payload={"root_name": root_name, "path": asset.relative_path},
                            status="retrying_locked",
                            last_error=str(asset.metadata.get("error") or "file locked"),
                            next_attempt_delay_seconds=300,
                            job_family=schedule["job_family"],
                            resource_class=schedule["resource_class"],
                            priority=schedule["priority"],
                            time_budget_seconds=schedule["time_budget_seconds"],
                            telemetry={"stage": "queued", "root_name": root_name, "path": asset.relative_path},
                            audit_details={"job_type": job_type, "root_name": root_name, "path": asset.relative_path},
                        )
                        jobs_queued += 0 if queued["deduped"] else 1
                    elif asset.extraction_tier == "deferred" and canonical_id:
                        _cancel_duplicate_corpus_job_for_asset(cur, root_name=root_name, relative_path=asset.relative_path)
                if index == files_total or index % 100 == 0:
                    _emit_persist_progress(
                        progress_callback,
                        stage="persisting",
                        files_done=index,
                        files_total=files_total,
                        current_path=asset.relative_path,
                    )

            unseen_paths = _mark_deleted_assets(
                cur,
                root_id,
                seen_paths,
                plan,
                reason="sync_unseen",
                grace_seconds=unseen_purge_grace_seconds,
            )
            deleted = len(unseen_paths)
            _cancel_unseen_corpus_jobs_for_paths(
                cur,
                root_name=root_name,
                paths=unseen_paths,
                reason="sync_unseen",
            )
            cur.execute(
                """
                UPDATE crawl_runs
                SET status = 'completed',
                    finished_at = now(),
                    files_seen = %s,
                    files_changed = %s,
                    files_deleted = %s,
                    chunks_indexed = %s,
                    jobs_queued = %s,
                    errors = %s::jsonb
                WHERE id = %s
                """,
                (len(plan.assets), changed, deleted, chunks_indexed, jobs_queued, _json_list(plan.errors), run_id),
            )
            _emit_persist_progress(progress_callback, stage="persisted", files_done=files_total, files_total=files_total)
            return {
                "root_name": root_name,
                "root_path": str(plan.root_path),
                "dry_run": False,
                "reason": reason,
                "files_seen": len(plan.assets),
                "files_changed": changed,
                "files_deleted": deleted,
                "jobs_queued": jobs_queued,
                "chunks_indexed": chunks_indexed,
                "manifest_skipped_unchanged": manifest_skipped_unchanged,
            }


def _emit_persist_progress(
    callback: Callable[[dict[str, Any]], None] | None,
    *,
    stage: str,
    files_done: int,
    files_total: int,
    current_path: str | None = None,
) -> None:
    if callback is None:
        return
    total = max(0, int(files_total or 0))
    done = max(0, min(int(files_done or 0), total)) if total else 0
    if stage == "persisted":
        percent = 100
        label = f"Persisted {done}/{total} files"
    else:
        stage_fraction = done / total if total else 0.0
        percent = min(99, int(((5 + stage_fraction) / 6) * 100))
        label = f"Persisting {done}/{total} files"
    payload: dict[str, Any] = {
        "stage": stage,
        "stage_index": 6,
        "stage_total": 6,
        "files_done": done,
        "files_total": total,
        "progress_percent": percent,
        "progress_label": label,
    }
    if current_path:
        payload["current_path"] = current_path
    try:
        callback(payload)
    except Exception:
        return


def crawl_status(*, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT count(*) FROM monitored_roots WHERE enabled AND watch_enabled")
            active_watch_roots = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM monitored_roots WHERE enabled AND NOT watch_enabled")
            disabled_watch_roots = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM capture_jobs WHERE job_type LIKE 'corpus_%%' AND status = 'pending'")
            pending_jobs = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM capture_jobs WHERE job_type LIKE 'corpus_%%' AND status = 'failed'")
            failed_jobs = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM capture_jobs WHERE job_type LIKE 'corpus_%%' AND status LIKE 'blocked_%%'")
            blocked_jobs = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM capture_jobs WHERE job_type LIKE 'corpus_%%' AND status = 'cancelled_duplicate'")
            duplicate_jobs = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM source_assets WHERE canonical_asset_id IS NOT NULL AND deleted_at IS NULL")
            duplicate_assets = cur.fetchone()[0]
            cur.execute(
                """
                SELECT last_error
                FROM watcher_state
                WHERE last_error IS NOT NULL
                ORDER BY updated_at DESC
                LIMIT 5
                """
            )
            watcher_errors = [row[0] for row in cur.fetchall()]
            cur.execute(
                """
                SELECT last_error
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%' AND last_error IS NOT NULL
                ORDER BY updated_at DESC
                LIMIT 5
                """
            )
            job_errors = [row[0] for row in cur.fetchall()]
            cur.execute(
                """
                SELECT r.name, s.status, s.heartbeat_at, s.last_event_at, s.last_error,
                       CASE
                         WHEN s.heartbeat_at IS NULL THEN NULL
                         ELSE EXTRACT(EPOCH FROM (now() - s.heartbeat_at))::integer
                       END AS heartbeat_age_seconds
                FROM monitored_roots r
                LEFT JOIN watcher_state s ON s.root_id = r.id
                ORDER BY r.name
                """
            )
            watchers = [
                {
                    "root_name": row[0],
                    "status": row[1] or "stopped",
                    "heartbeat_at": row[2].isoformat() if row[2] else None,
                    "last_event_at": row[3].isoformat() if row[3] else None,
                    "last_error": row[4],
                    "heartbeat_age_seconds": row[5],
                }
                for row in cur.fetchall()
            ]
            return {
                "active_watch_roots": active_watch_roots,
                "disabled_watch_roots": disabled_watch_roots,
                "pending_jobs": pending_jobs,
                "failed_jobs": failed_jobs,
                "blocked_jobs": blocked_jobs,
                "duplicate_jobs": duplicate_jobs,
                "duplicate_assets": duplicate_assets,
                "recent_errors": watcher_errors + job_errors,
                "watchers": watchers,
            }


def crawl_root_summaries(
    *, limit_assets: int = 12, limit_jobs: int = 12, url: str | None = None
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT r.id::text, r.name, r.root_path, r.enabled, r.recursive, r.watch_enabled,
                       r.trust_rank, r.include_globs, r.exclude_globs, r.glob_mode, r.max_inline_bytes,
                       r.heavy_threshold_bytes, r.metadata,
                       s.status, s.heartbeat_at, s.last_event_at, s.last_error,
                       CASE
                         WHEN s.heartbeat_at IS NULL THEN NULL
                         ELSE EXTRACT(EPOCH FROM (now() - s.heartbeat_at))::integer
                       END AS heartbeat_age_seconds
                FROM monitored_roots r
                LEFT JOIN watcher_state s ON s.root_id = r.id
                ORDER BY r.name
                """
            )
            roots = cur.fetchall()
            summaries: list[dict[str, Any]] = []
            for row in roots:
                root = _root_row(row[:13])
                root_id = root["id"]
                watcher = {
                    "status": row[13] or ("enabled" if root["watch_enabled"] else "disabled"),
                    "heartbeat_at": row[14].isoformat() if row[14] else None,
                    "last_event_at": row[15].isoformat() if row[15] else None,
                    "last_error": row[16],
                    "heartbeat_age_seconds": row[17],
                }
                latest_crawl = _latest_crawl_run(cur, root_id)
                asset_counts = _root_asset_counts(cur, root_id)
                job_counts = _root_job_counts(cur, root["name"])
                recent_assets = _recent_root_assets(cur, root_id, limit=limit_assets)
                recent_jobs = _recent_root_jobs(cur, root["name"], limit=limit_jobs)
                recent_errors = [
                    error
                    for error in [watcher["last_error"], *(job.get("last_error") for job in recent_jobs)]
                    if error
                ][:5]
                summaries.append(
                    {
                        **root,
                        "state": _root_state(root, watcher, latest_crawl, job_counts),
                        "watcher": watcher,
                        "latest_crawl": latest_crawl,
                        "asset_counts": asset_counts,
                        "job_counts": job_counts,
                        "recent_assets": recent_assets,
                        "recent_jobs": recent_jobs,
                        "recent_errors": recent_errors,
                    }
                )
            return summaries


def _capture_job_limit(value: int | str | None, *, default: int = 50) -> int:
    try:
        numeric = int(value if value is not None else default)
    except (TypeError, ValueError):
        numeric = default
    return max(1, min(numeric, 200))


def _capture_job_offset(value: int | str | None) -> int:
    try:
        numeric = int(value if value is not None else 0)
    except (TypeError, ValueError):
        numeric = 0
    return max(0, numeric)


def _capture_job_filter_values(value: str | list[str] | tuple[str, ...] | None) -> list[str]:
    if value is None:
        return []
    raw_values = [value] if isinstance(value, str) else list(value)
    values: list[str] = []
    seen: set[str] = set()
    for raw in raw_values:
        clean = str(raw or "").strip()
        if clean and clean not in seen:
            values.append(clean)
            seen.add(clean)
    return values


def _capture_job_filter_sql(
    *,
    status: str | list[str] | None = None,
    root_name: str | list[str] | None = None,
    job_type: str | list[str] | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
) -> tuple[str, list[Any]]:
    clauses = ["job_type LIKE 'corpus_%%'"]
    params: list[Any] = []
    statuses = _capture_job_filter_values(status)
    roots = _capture_job_filter_values(root_name)
    job_types = _capture_job_filter_values(job_type)
    clean_updated_from = str(updated_from or "").strip()
    clean_updated_to = str(updated_to or "").strip()
    if statuses:
        clauses.append("status = ANY(%s::text[])")
        params.append(statuses)
    if roots:
        clauses.append("payload->>'root_name' = ANY(%s::text[])")
        params.append(roots)
    if job_types:
        clauses.append("job_type = ANY(%s::text[])")
        params.append(job_types)
    if clean_updated_from:
        clauses.append("updated_at >= %s")
        params.append(clean_updated_from)
    if clean_updated_to:
        clauses.append("updated_at <= %s")
        params.append(clean_updated_to)
    return " AND ".join(clauses), params


def _capture_job_delete_markable(status: str) -> bool:
    return status in {"completed", "failed"} or status.startswith("blocked_") or status.startswith("cancelled_")


def _capture_job_terminal_progress_label(telemetry: dict[str, Any]) -> str:
    result_status = str(telemetry.get("result_status") or "").strip().lower()
    if result_status == "indexed":
        return "Indexed"
    if result_status == "metadata_only":
        return "Metadata only"
    if result_status == "staged":
        return "Staged"
    if result_status == "obsolete":
        return "Obsolete"
    if result_status:
        return result_status.replace("_", " ").capitalize()
    return "Completed"


def _terminal_capture_job_telemetry(telemetry: dict[str, Any] | None) -> dict[str, Any]:
    normalized = dict(telemetry or {})
    if not str(normalized.get("progress_label") or "").strip():
        normalized["progress_label"] = _capture_job_terminal_progress_label(normalized)
    return normalized


_CAPTURE_JOB_SORT_SQL = {
    "status": "status",
    "job_type": "job_type",
    "target": "COALESCE(payload->>'path', payload->>'canonical_path', payload->>'file_path', payload->>'profile_name', '')",
    "root": "COALESCE(payload->>'root_name', '')",
    "attempts": "attempts",
    "updated": "updated_at",
    "progress": """CASE
        WHEN status = 'completed' THEN COALESCE(telemetry->>'progress_label', telemetry->>'result_status', status)
        WHEN status = 'obsolete' THEN COALESCE(telemetry->>'progress_label', telemetry->>'obsolete_reason', telemetry->>'result_status', status)
        ELSE COALESCE(telemetry->>'progress_label', telemetry->>'stage', telemetry->>'progress_percent', '')
    END""",
    "last_error": "COALESCE(last_error, '')",
}


def _capture_job_order_sql(sort_by: str | None, sort_dir: str | None) -> str:
    expression = _CAPTURE_JOB_SORT_SQL.get(str(sort_by or "").strip(), _CAPTURE_JOB_SORT_SQL["updated"])
    direction = "ASC" if str(sort_dir or "").strip().lower() == "asc" else "DESC"
    return f"ORDER BY {expression} {direction}, id {direction}"


def list_capture_jobs(
    *,
    limit: int = 50,
    offset: int = 0,
    status: str | list[str] | None = None,
    root_name: str | list[str] | None = None,
    job_type: str | list[str] | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
    sort_by: str | None = "updated",
    sort_dir: str | None = "desc",
    url: str | None = None,
) -> list[dict[str, Any]]:
    where_sql, params = _capture_job_filter_sql(
        status=status,
        root_name=root_name,
        job_type=job_type,
        updated_from=updated_from,
        updated_to=updated_to,
    )
    order_sql = _capture_job_order_sql(sort_by, sort_dir)
    params.extend([_capture_job_limit(limit), _capture_job_offset(offset)])
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, job_type, job_family, resource_class, priority, time_budget_seconds,
                       status, payload, attempts, last_error, created_at, updated_at,
                       started_at, completed_at, last_duration_ms, telemetry,
                       locked_at, locked_by, progress_heartbeat_at,
                       delete_requested_at, delete_requested_by, delete_reason,
                       broker_message_id, correlation_id, causation_id, routing_key,
                       queued_at, broker_delivery_count
                FROM capture_jobs
                WHERE {where_sql}
                {order_sql}
                LIMIT %s
                OFFSET %s
                """,
                tuple(params),
            )
            return [
                {
                    "id": row[0],
                    "job_type": row[1],
                    "job_family": row[2],
                    "resource_class": row[3],
                    "priority": row[4],
                    "time_budget_seconds": row[5],
                    "status": row[6],
                    "payload": row[7],
                    "attempts": row[8],
                    "last_error": row[9],
                    "created_at": row[10].isoformat(),
                    "updated_at": row[11].isoformat(),
                    "started_at": row[12].isoformat() if row[12] else None,
                    "completed_at": row[13].isoformat() if row[13] else None,
                    "last_duration_ms": row[14],
                    "telemetry": row[15] or {},
                    "locked_at": row[16].isoformat() if row[16] else None,
                    "locked_by": row[17],
                    "progress_heartbeat_at": row[18].isoformat() if row[18] else None,
                    "delete_requested_at": row[19].isoformat() if row[19] else None,
                    "delete_requested_by": row[20],
                    "delete_reason": row[21],
                    "broker_message_id": row[22] if len(row) > 22 else None,
                    "correlation_id": row[23] if len(row) > 23 else None,
                    "causation_id": row[24] if len(row) > 24 else None,
                    "routing_key": row[25] if len(row) > 25 else None,
                    "queued_at": row[26].isoformat() if len(row) > 26 and row[26] else None,
                    "broker_delivery_count": int(row[27] or 0) if len(row) > 27 else 0,
                }
                for row in cur.fetchall()
            ]


def count_capture_jobs(
    *,
    status: str | list[str] | None = None,
    root_name: str | list[str] | None = None,
    job_type: str | list[str] | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
    url: str | None = None,
) -> int:
    where_sql, params = _capture_job_filter_sql(
        status=status,
        root_name=root_name,
        job_type=job_type,
        updated_from=updated_from,
        updated_to=updated_to,
    )
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT count(*)
                FROM capture_jobs
                WHERE {where_sql}
                """,
                tuple(params),
            )
            row = cur.fetchone()
            return int(row[0] or 0) if row else 0


def capture_job_filter_options(*, url: str | None = None) -> dict[str, list[str]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT DISTINCT status
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%'
                  AND status IS NOT NULL
                ORDER BY status
                """
            )
            statuses = [str(row[0]) for row in cur.fetchall() if row[0]]
            cur.execute(
                """
                SELECT DISTINCT payload->>'root_name'
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%'
                  AND COALESCE(payload->>'root_name', '') <> ''
                ORDER BY payload->>'root_name'
                """
            )
            roots = [str(row[0]) for row in cur.fetchall() if row[0]]
            cur.execute(
                """
                SELECT DISTINCT job_type
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%'
                ORDER BY job_type
                """
            )
            job_types = [str(row[0]) for row in cur.fetchall() if row[0]]
            return {"statuses": statuses, "roots": roots, "job_types": job_types}


def _capture_job_tool_invocation_limit(limit: int | None) -> int:
    try:
        numeric = int(limit or 100)
    except (TypeError, ValueError):
        numeric = 100
    return max(1, min(numeric, 500))


def _capture_job_tool_command(command: Any) -> list[str]:
    if isinstance(command, (list, tuple)):
        return [str(part) for part in command]
    return [str(command)]


def start_capture_job_tool_invocation(
    *,
    job_id: str,
    command: Any,
    cwd: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO capture_job_tool_invocations (
                    job_id, command, cwd, status
                )
                VALUES (%s, %s::jsonb, %s, 'running')
                RETURNING id::text, started_at
                """,
                (job_id, _json(_capture_job_tool_command(command)), _postgres_text_or_none(cwd)),
            )
            row = cur.fetchone()
            if not row:
                return None
            return {"id": row[0], "started_at": row[1].isoformat() if row[1] else None}


def update_capture_job_tool_invocation_output(
    *,
    invocation_id: str,
    stdout: str,
    stderr: str,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_job_tool_invocations
                SET stdout = %s,
                    stderr = %s,
                    updated_at = now()
                WHERE id = %s
                """,
                (_postgres_text_or_none(stdout) or "", _postgres_text_or_none(stderr) or "", invocation_id),
            )


def complete_capture_job_tool_invocation(
    *,
    invocation_id: str,
    status: str,
    return_code: int | None = None,
    stdout: str = "",
    stderr: str = "",
    duration_ms: int | None = None,
    exception_type: str | None = None,
    exception_message: str | None = None,
    url: str | None = None,
) -> None:
    normalized_status = status if status in {"completed", "failed", "timeout", "exception"} else "exception"
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_job_tool_invocations
                SET status = %s,
                    return_code = %s,
                    stdout = %s,
                    stderr = %s,
                    exception_type = %s,
                    exception_message = %s,
                    duration_ms = %s,
                    completed_at = now(),
                    updated_at = now()
                WHERE id = %s
                """,
                (
                    normalized_status,
                    return_code,
                    _postgres_text_or_none(stdout) or "",
                    _postgres_text_or_none(stderr) or "",
                    _postgres_text_or_none(exception_type),
                    _postgres_text_or_none(exception_message),
                    duration_ms,
                    invocation_id,
                ),
            )


def list_capture_job_tool_invocations(
    *,
    job_id: str,
    limit: int = 100,
    url: str | None = None,
) -> list[dict[str, Any]]:
    safe_limit = _capture_job_tool_invocation_limit(limit)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, job_id::text, command, cwd, status, return_code,
                       stdout, stderr, exception_type, exception_message,
                       started_at, completed_at, duration_ms, updated_at
                FROM capture_job_tool_invocations
                WHERE job_id = %s
                ORDER BY started_at, id
                LIMIT %s
                """,
                (job_id, safe_limit),
            )
            return [
                {
                    "id": row[0],
                    "job_id": row[1],
                    "command": row[2] or [],
                    "cwd": row[3],
                    "status": row[4],
                    "return_code": row[5],
                    "stdout": row[6] or "",
                    "stderr": row[7] or "",
                    "exception_type": row[8],
                    "exception_message": row[9],
                    "started_at": row[10].isoformat() if row[10] else None,
                    "completed_at": row[11].isoformat() if row[11] else None,
                    "duration_ms": row[12],
                    "updated_at": row[13].isoformat() if row[13] else None,
                }
                for row in cur.fetchall()
            ]


def purge_expired_capture_job_tool_invocations(
    *,
    retention_hours: int = 24,
    url: str | None = None,
) -> dict[str, Any]:
    safe_hours = max(1, int(retention_hours or 24))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                DELETE FROM capture_job_tool_invocations inv
                USING capture_jobs job
                WHERE job.id = inv.job_id
                  AND job.status = 'completed'
                  AND job.completed_at IS NOT NULL
                  AND job.completed_at < now() - make_interval(hours => %s)
                """,
                (safe_hours,),
            )
            return {"purged": int(cur.rowcount or 0), "retention_hours": safe_hours}


def mark_capture_job_for_deletion(
    *,
    job_id: str,
    actor: str = "operator",
    reason: str = "operator_cleanup",
    url: str | None = None,
) -> dict[str, Any]:
    clean_actor = str(actor or "operator").strip() or "operator"
    clean_reason = str(reason or "operator_cleanup").strip() or "operator_cleanup"
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status, delete_requested_at, delete_requested_by, delete_reason
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (job_id,),
            )
            row = cur.fetchone()
            if row is None:
                return {
                    "job_id": job_id,
                    "status": "not_found",
                    "delete_requested": False,
                    "error": f"corpus job not found: {job_id}",
                }
            status = str(row[1] or "unknown")
            if status == "obsolete" and row[2] is not None:
                requested_at = row[2].isoformat() if hasattr(row[2], "isoformat") else str(row[2])
                return {
                    "job_id": row[0],
                    "status": status,
                    "delete_requested": True,
                    "delete_requested_at": requested_at,
                    "delete_requested_by": row[3],
                    "delete_reason": row[4],
                }
            if not _capture_job_delete_markable(status):
                return {
                    "job_id": row[0],
                    "status": status,
                    "delete_requested": False,
                    "error": f"Corpus job status {status} cannot be marked for deletion.",
                }
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'obsolete',
                    delete_requested_at = COALESCE(delete_requested_at, now()),
                    delete_requested_by = COALESCE(delete_requested_by, %s),
                    delete_reason = COALESCE(delete_reason, %s),
                    telemetry = jsonb_strip_nulls(
                        COALESCE(telemetry, '{}'::jsonb) || jsonb_build_object(
                            'obsolete_previous_status', status,
                            'obsolete_previous_result_status', telemetry->>'result_status',
                            'result_status', 'obsolete'
                        )
                    ),
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND (
                      status = 'completed'
                      OR status = 'failed'
                      OR status LIKE 'blocked_%%'
                      OR status LIKE 'cancelled_%%'
                  )
                RETURNING id::text, status, delete_requested_at, delete_requested_by, delete_reason
                """,
                (clean_actor, clean_reason, row[0]),
            )
            updated = cur.fetchone()
            if updated is None:
                return {
                    "job_id": row[0],
                    "status": status,
                    "delete_requested": False,
                    "error": "Corpus job could not be marked for deletion because its status changed.",
                }
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('capture_job.deletion_requested', 'capture_jobs', %s, %s::jsonb)
                """,
                (
                    row[0],
                    _json(
                        {
                            "actor": clean_actor,
                            "reason": clean_reason,
                            "previous_status": status,
                            "status": "obsolete",
                        }
                    ),
                ),
            )
            requested_at = updated[2].isoformat() if hasattr(updated[2], "isoformat") else str(updated[2])
            return {
                "job_id": updated[0],
                "status": updated[1],
                "delete_requested": True,
                "delete_requested_at": requested_at,
                "delete_requested_by": updated[3],
                "delete_reason": updated[4],
            }


def restore_capture_job_deletion_request(
    *,
    job_id: str,
    actor: str = "operator",
    url: str | None = None,
) -> dict[str, Any]:
    clean_actor = str(actor or "operator").strip() or "operator"
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status, delete_requested_at, delete_requested_by, delete_reason, telemetry
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (job_id,),
            )
            row = cur.fetchone()
            if row is None:
                return {
                    "job_id": job_id,
                    "status": "not_found",
                    "delete_requested": False,
                    "error": f"corpus job not found: {job_id}",
                }
            status = str(row[1] or "unknown")
            if status != "obsolete" or row[2] is None:
                return {
                    "job_id": row[0],
                    "status": status,
                    "delete_requested": row[2] is not None,
                    "error": f"Corpus job status {status} cannot be restored from deletion.",
                }
            telemetry = row[5] if isinstance(row[5], dict) else {}
            restored_status = str(telemetry.get("obsolete_previous_status") or "").strip()
            restored_result_status = telemetry.get("obsolete_previous_result_status")
            if not restored_status or restored_status == "obsolete":
                return {
                    "job_id": row[0],
                    "status": status,
                    "delete_requested": True,
                    "delete_requested_at": row[2].isoformat() if hasattr(row[2], "isoformat") else str(row[2]),
                    "delete_requested_by": row[3],
                    "delete_reason": row[4],
                    "error": "Corpus job cannot be restored because its previous status was not recorded.",
                }
            if restored_result_status is not None:
                restored_result_status = str(restored_result_status)
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = %s,
                    delete_requested_at = NULL,
                    delete_requested_by = NULL,
                    delete_reason = NULL,
                    telemetry = CASE
                        WHEN %s::text IS NULL THEN
                            COALESCE(telemetry, '{}'::jsonb)
                            - 'obsolete_previous_status'
                            - 'obsolete_previous_result_status'
                            - 'result_status'
                        ELSE
                            (
                                COALESCE(telemetry, '{}'::jsonb)
                                - 'obsolete_previous_status'
                                - 'obsolete_previous_result_status'
                                - 'result_status'
                            ) || jsonb_build_object('result_status', %s::text)
                        END,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'obsolete'
                  AND delete_requested_at IS NOT NULL
                RETURNING id::text, status, delete_requested_at, delete_requested_by, delete_reason
                """,
                (restored_status, restored_result_status, restored_result_status, row[0]),
            )
            updated = cur.fetchone()
            if updated is None:
                return {
                    "job_id": row[0],
                    "status": status,
                    "delete_requested": True,
                    "error": "Corpus job could not be restored because its status changed.",
                }
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('capture_job.deletion_restored', 'capture_jobs', %s, %s::jsonb)
                """,
                (
                    row[0],
                    _json(
                        {
                            "actor": clean_actor,
                            "previous_status": status,
                            "restored_status": updated[1],
                        }
                    ),
                ),
            )
            return {
                "job_id": updated[0],
                "status": updated[1],
                "delete_requested": False,
                "delete_requested_at": updated[2].isoformat() if hasattr(updated[2], "isoformat") else updated[2],
                "delete_requested_by": updated[3],
                "delete_reason": updated[4],
            }


def purge_expired_capture_jobs(
    *,
    retention_days: int = 7,
    url: str | None = None,
) -> dict[str, Any]:
    safe_days = max(1, int(retention_days or 7))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                WITH deleted AS (
                    DELETE FROM capture_jobs job
                    WHERE job.job_type LIKE 'corpus_%%'
                      AND COALESCE(job.completed_at, job.updated_at) < now() - make_interval(days => %s)
                      AND (
                          job.status = 'completed'
                          OR (
                              job.delete_requested_at IS NOT NULL
                              AND job.status = 'obsolete'
                          )
                      )
                    RETURNING 1
                ),
                audit AS (
                    INSERT INTO audit_events (event_type, target_table, details)
                    SELECT 'capture_job.retention_purged',
                           'capture_jobs',
                           jsonb_build_object(
                               'purged', (SELECT count(*) FROM deleted),
                               'retention_days', %s::integer
                           )
                    WHERE EXISTS (SELECT 1 FROM deleted)
                    RETURNING 1
                )
                SELECT count(*) FROM deleted
                """,
                (safe_days, safe_days),
            )
            row = cur.fetchone()
            return {"purged": int(row[0] or 0) if row else 0, "retention_days": safe_days}


_CAPTURE_REVIEW_STATUSES = {
    "pending_review",
    "approved",
    "rejected",
    "completed",
    "failed",
    "blocked_missing_dependency",
    "all",
}


def list_capture_review_jobs(
    *,
    status: str = "pending_review",
    limit: int = 50,
    url: str | None = None,
) -> list[dict[str, Any]]:
    normalized_status = _normalize_capture_review_status(status)
    where_sql = "TRUE"
    params: list[Any] = []
    if normalized_status == "pending_review":
        where_sql = "status = 'pending_review' OR payload->>'status' = 'pending_review'"
    elif normalized_status != "all":
        where_sql = "status = %s OR payload->>'status' = %s"
        params.extend([normalized_status, normalized_status])
    params.append(max(1, min(limit, 200)))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, job_type, status, payload, attempts, last_error, created_at, updated_at
                FROM capture_jobs
                WHERE {where_sql}
                ORDER BY updated_at DESC, created_at DESC
                LIMIT %s
                """,
                tuple(params),
            )
            return [_capture_job_row(row, metadata_only=True) for row in cur.fetchall()]


def list_capture_ingestion_jobs(
    *,
    job_id: str | None = None,
    limit: int = 25,
    url: str | None = None,
) -> list[dict[str, Any]]:
    clauses = ["job_type = 'codex_backfill'", "(status = 'approved' OR payload->>'status' = 'approved')"]
    params: list[Any] = []
    if job_id:
        clauses.append("id::text = %s")
        params.append(job_id)
    params.append(max(1, min(limit, 100)))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, job_type, status, payload, attempts, last_error, created_at, updated_at
                FROM capture_jobs
                WHERE {' AND '.join(clauses)}
                ORDER BY updated_at DESC, created_at DESC
                LIMIT %s
                """,
                tuple(params),
            )
            return [_capture_job_row(row, metadata_only=False) for row in cur.fetchall()]


def codex_backfill_source_hash_exists(*, source_hash: str, url: str | None = None) -> bool:
    safe_hash = (source_hash or "").strip()
    if not safe_hash:
        return False
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT 1
                FROM episodes
                WHERE metadata->>'source' = 'codex_backfill'
                  AND metadata->>'source_hash' = %s
                LIMIT 1
                """,
                (safe_hash,),
            )
            return cur.fetchone() is not None


def update_capture_job_ingestion(
    *,
    job_id: str,
    status: str,
    ingestion: dict[str, Any],
    url: str | None = None,
) -> dict[str, Any]:
    normalized_status = _normalize_capture_review_status(status)
    if normalized_status == "all":
        raise ValueError("capture job status cannot be all")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = %s,
                    payload = jsonb_set(
                        jsonb_set(COALESCE(payload, '{}'::jsonb), '{ingestion}', %s::jsonb, true),
                        '{status}',
                        to_jsonb(%s::text),
                        true
                    ),
                    last_error = CASE WHEN %s IN ('failed', 'blocked_missing_dependency') THEN COALESCE(%s, last_error) ELSE NULL END,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id::text = %s
                RETURNING id::text, job_type, status, payload, attempts, last_error, created_at, updated_at
                """,
                (
                    normalized_status,
                    _json(ingestion),
                    normalized_status,
                    normalized_status,
                    ingestion.get("error") if isinstance(ingestion, dict) else None,
                    job_id,
                ),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"capture review job not found: {job_id}")
            return _capture_job_row(row, metadata_only=True)


def review_capture_job(
    *,
    job_id: str,
    decision: str,
    rationale: str,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    normalized_decision = (decision or "").strip().lower()
    if normalized_decision not in {"approve", "reject"}:
        raise ValueError("decision must be one of: approve, reject")

    normalized_rationale = (rationale or "").strip()
    if not normalized_rationale:
        raise ValueError("rationale is required")
    capped_rationale = normalized_rationale[:1000]
    normalized_actor = (actor or "system").strip() or "system"
    reviewed_at = datetime.now(UTC).isoformat()
    target_status = "approved" if normalized_decision == "approve" else "rejected"
    event_type = f"capture.review_{target_status}"
    review_payload = {
        "decision": normalized_decision,
        "rationale": capped_rationale,
        "actor": normalized_actor,
        "reviewed_at": reviewed_at,
    }

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, job_type, status, payload, attempts, last_error, created_at, updated_at
                FROM capture_jobs
                WHERE id::text = %s
                FOR UPDATE
                """,
                (job_id,),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"capture review job not found: {job_id}")
            current_payload = row[3] or {}
            current_payload_status = current_payload.get("status") if isinstance(current_payload, dict) else None
            if row[2] != "pending_review" and current_payload_status != "pending_review":
                raise RuntimeError(f"capture review job already decided: {job_id}")

            cur.execute(
                """
                INSERT INTO audit_events (event_type, actor, target_table, target_id, details)
                VALUES (%s, %s, 'capture_jobs', %s::uuid, %s::jsonb)
                RETURNING id::text
                """,
                (
                    event_type,
                    normalized_actor,
                    row[0],
                    _json(
                        {
                            "decision": normalized_decision,
                            "rationale": capped_rationale,
                            "status": target_status,
                            "job_type": row[1],
                        }
                    ),
                ),
            )
            audit_id = cur.fetchone()[0]
            review_payload["audit_event_id"] = audit_id
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = %s,
                    payload = jsonb_set(
                        jsonb_set(COALESCE(payload, '{}'::jsonb), '{review}', %s::jsonb, true),
                        '{status}',
                        to_jsonb(%s::text),
                        true
                    ),
                    last_error = NULL,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id::text = %s
                RETURNING id::text, job_type, status, payload, attempts, last_error, created_at, updated_at
                """,
                (target_status, _json(review_payload), target_status, job_id),
            )
            updated = cur.fetchone()
            if updated is None:
                raise LookupError(f"capture review job not found: {job_id}")
            return {
                "job": _capture_job_row(updated, metadata_only=True),
                "review": review_payload,
                "audit_event_id": audit_id,
                "audit_event": {"id": audit_id, "event_type": event_type},
                "links": [{"label": "Audit event", "href": "/api/audit?limit=50", "audit_event_id": audit_id}],
            }


def decide_capture_review_job(
    *,
    job_id: str,
    decision: str,
    reason: str | None = None,
    rationale: str | None = None,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    return review_capture_job(
        job_id=job_id,
        decision=decision,
        rationale=rationale if rationale is not None else reason or "",
        actor=actor,
        url=url,
    )


def cancel_duplicate_corpus_jobs(*, root_name: str | None = None, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_filter = "AND payload->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...] = (root_name,) if root_name else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH cancelled AS (
                    UPDATE capture_jobs job
                    SET status = 'cancelled_duplicate',
                        last_error = 'duplicate_suppressed',
                        locked_at = NULL,
                        locked_by = NULL,
                        updated_at = now()
                    WHERE job.job_type LIKE 'corpus_%%'
                      AND job.status IN ('pending', 'running')
                      {root_filter}
                      AND EXISTS (
                          SELECT 1
                          FROM source_assets asset
                          JOIN monitored_roots root ON root.id = asset.root_id
                          WHERE root.name = job.payload->>'root_name'
                            AND asset.path = job.payload->>'path'
                            AND asset.canonical_asset_id IS NOT NULL
                            AND asset.deleted_at IS NULL
                            AND asset.extraction_status = 'duplicate_suppressed'
                      )
                    RETURNING 1
                )
                SELECT count(*) FROM cancelled
                """,
                params,
            )
            return {"root_name": root_name, "cancelled": int(cur.fetchone()[0] or 0)}


def list_active_source_asset_paths(*, root_name: str, url: str | None = None) -> list[str]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT a.path
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.name = %s
                  AND a.deleted_at IS NULL
                ORDER BY a.path
                """,
                (root_name,),
            )
            return [str(row[0]) for row in cur.fetchall()]


def mark_unseen_source_assets(
    *,
    root_name: str,
    paths: list[str] | tuple[str, ...],
    reason: str = "root_policy_update",
    grace_seconds: int = DEFAULT_UNSEEN_ASSET_PURGE_GRACE_SECONDS,
    url: str | None = None,
) -> dict[str, Any]:
    normalized_paths = _normalized_relative_paths(paths)
    if not normalized_paths:
        return {"root_name": root_name, "reason": reason, "assets_marked": 0, "jobs_cancelled": 0}
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id::text FROM monitored_roots WHERE name = %s", (root_name,))
            row = cur.fetchone()
            if not row:
                raise ValueError(f"monitored root not found: {root_name}")
            marked_paths = _mark_unseen_assets_by_paths(
                cur,
                root_id=row[0],
                paths=normalized_paths,
                reason=reason,
                grace_seconds=grace_seconds,
            )
            jobs_cancelled = _cancel_unseen_corpus_jobs_for_paths(
                cur,
                root_name=root_name,
                paths=marked_paths,
                reason=reason,
            )
            return {
                "root_name": root_name,
                "reason": reason,
                "assets_marked": len(marked_paths),
                "jobs_cancelled": jobs_cancelled,
            }


def purge_unseen_corpus_assets(
    *,
    root_name: str | None = None,
    grace_seconds: int = DEFAULT_UNSEEN_ASSET_PURGE_GRACE_SECONDS,
    batch_size: int = DEFAULT_UNSEEN_ASSET_PURGE_BATCH_SIZE,
    url: str | None = None,
) -> dict[str, Any]:
    bounded_grace = max(0, int(grace_seconds or 0))
    bounded_batch = max(1, min(int(batch_size or DEFAULT_UNSEEN_ASSET_PURGE_BATCH_SIZE), 5000))
    root_filter = "AND r.name = %s" if root_name else ""
    params: list[Any] = [bounded_grace]
    if root_name:
        params.append(root_name)
    params.append(bounded_batch)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH eligible AS MATERIALIZED (
                    SELECT a.id, a.root_id, a.path
                    FROM source_assets a
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE a.deleted_at IS NOT NULL
                      AND a.metadata ? 'unseen_reason'
                      AND COALESCE((a.metadata->>'purge_after')::timestamptz, a.deleted_at + make_interval(secs => %s)) <= now()
                      {root_filter}
                    ORDER BY a.deleted_at, a.path
                    LIMIT %s
                    FOR UPDATE OF a SKIP LOCKED
                ),
                canonical_links_cleared AS (
                    UPDATE source_assets duplicate
                    SET canonical_asset_id = NULL,
                        updated_at = now()
                    WHERE duplicate.canonical_asset_id IN (SELECT id FROM eligible)
                    RETURNING 1
                ),
                code_references_deleted AS (
                    DELETE FROM code_references
                    WHERE source_asset_id IN (SELECT id FROM eligible)
                    RETURNING 1
                ),
                code_symbols_deleted AS (
                    DELETE FROM code_symbols
                    WHERE source_asset_id IN (SELECT id FROM eligible)
                    RETURNING 1
                ),
                chunks_deleted AS (
                    DELETE FROM asset_chunks
                    WHERE asset_id IN (SELECT id FROM eligible)
                    RETURNING 1
                ),
                manifests_deleted AS (
                    DELETE FROM crawl_path_manifests manifest
                    USING eligible
                    WHERE manifest.root_id = eligible.root_id
                      AND manifest.path = eligible.path
                    RETURNING 1
                ),
                assets_deleted AS (
                    DELETE FROM source_assets
                    WHERE id IN (SELECT id FROM eligible)
                    RETURNING id::text
                ),
                audit AS (
                    INSERT INTO audit_events (event_type, target_table, details)
                    SELECT 'corpus.unseen_assets_purged',
                           'source_assets',
                           jsonb_build_object(
                               'root_name', %s::text,
                               'assets_purged', (SELECT count(*) FROM assets_deleted),
                               'grace_seconds', %s::integer,
                               'batch_size', %s::integer
                           )
                    WHERE EXISTS (SELECT 1 FROM assets_deleted)
                    RETURNING 1
                )
                SELECT count(*) FROM assets_deleted
                """,
                tuple([*params, root_name, bounded_grace, bounded_batch]),
            )
            return {"root_name": root_name, "assets_purged": int(cur.fetchone()[0] or 0)}


def claim_corpus_jobs(
    *,
    limit: int = 1,
    worker_id: str = "flux-kb-worker",
    root_name: str | None = None,
    job_families: list[str] | tuple[str, ...] | None = None,
    family_caps: dict[str, int] | None = None,
    host_agent_roots: bool | None = None,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    root_filter = "AND payload->>'root_name' = %s" if root_name else ""
    family_filter = "AND job_family = ANY(%s)" if job_families else ""
    host_root_filter = ""
    if host_agent_roots is True:
        host_root_filter = """
                      AND EXISTS (
                          SELECT 1
                          FROM monitored_roots r
                          WHERE r.name = capture_jobs.payload->>'root_name'
                            AND r.metadata->>'host_access' = 'host_agent'
                      )
        """
    elif host_agent_roots is False:
        host_root_filter = """
                      AND NOT EXISTS (
                          SELECT 1
                          FROM monitored_roots r
                          WHERE r.name = capture_jobs.payload->>'root_name'
                            AND r.metadata->>'host_access' = 'host_agent'
                      )
        """
    same_root_sync_filter = """
                      AND (
                          capture_jobs.job_type <> 'corpus_sync_root'
                          OR capture_jobs.id = (
                              SELECT first_sync.id
                              FROM capture_jobs first_sync
                              WHERE first_sync.job_type = 'corpus_sync_root'
                                AND first_sync.status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                                AND first_sync.delete_requested_at IS NULL
                                AND first_sync.next_attempt_at <= now()
                                AND first_sync.payload->>'root_name' = capture_jobs.payload->>'root_name'
                              ORDER BY first_sync.priority DESC, first_sync.created_at
                              LIMIT 1
                          )
                      )
                      AND NOT (
                          capture_jobs.job_type = 'corpus_sync_root'
                          AND EXISTS (
                              SELECT 1
                              FROM capture_jobs other
                              WHERE other.job_type = 'corpus_sync_root'
                                AND other.status = 'running'
                                AND other.delete_requested_at IS NULL
                                AND other.payload->>'root_name' = capture_jobs.payload->>'root_name'
                          )
                      )
        """
    caps_cte = ""
    claim_selection = f"""
                    SELECT id
                    FROM capture_jobs
                    WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                      AND status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                      -- legacy active statuses: status IN ('pending', 'retrying_locked', 'retrying_vss_failed')
                      AND delete_requested_at IS NULL
                      AND next_attempt_at <= now()
                      {family_filter}
                      {root_filter}
                      {host_root_filter}
                      {same_root_sync_filter}
                    ORDER BY priority DESC, created_at
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
    """
    if family_caps:
        caps_cte = f"""
                WITH family_caps AS (SELECT %s::jsonb AS caps),
                     running_family_counts AS (
                         SELECT job_family AS family, count(*)::integer AS running_count
                         FROM capture_jobs
                         WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                           AND status = 'running'
                         GROUP BY job_family
                     ),
                     claimable_jobs AS (
                         SELECT capture_jobs.id,
                                capture_jobs.job_family,
                                capture_jobs.priority,
                                capture_jobs.created_at,
                                COALESCE(
                                    (SELECT running_count FROM running_family_counts WHERE family = capture_jobs.job_family),
                                    0
                                ) AS running_count,
                                COALESCE(NULLIF(family_caps.caps ->> capture_jobs.job_family, '')::integer, 2147483647) AS family_cap
                         FROM capture_jobs
                         CROSS JOIN family_caps
                         WHERE (capture_jobs.job_type LIKE 'corpus_%%' OR capture_jobs.job_type = 'search_index_sync')
                           AND capture_jobs.status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                           AND capture_jobs.delete_requested_at IS NULL
                           AND capture_jobs.next_attempt_at <= now()
                           {family_filter}
                           {root_filter}
                           {host_root_filter}
                           {same_root_sync_filter}
                         ORDER BY capture_jobs.priority DESC, capture_jobs.created_at
                     ),
                     ranked_claimable_jobs AS (
                         SELECT id,
                                priority,
                                created_at,
                                row_number() OVER (PARTITION BY job_family ORDER BY priority DESC, created_at) AS family_rank,
                                GREATEST(0, family_cap - running_count) AS family_capacity_available
                         FROM claimable_jobs
                     )
        """
        claim_selection = """
                    SELECT capture_jobs.id
                    FROM capture_jobs
                    JOIN ranked_claimable_jobs ranked
                      ON ranked.id = capture_jobs.id
                    WHERE ranked.family_rank <= ranked.family_capacity_available
                    ORDER BY ranked.priority DESC, ranked.created_at
                    LIMIT %s
                    FOR UPDATE OF capture_jobs SKIP LOCKED
        """
    params_list: list[Any] = []
    if family_caps:
        params_list.append(_json({key: max(0, int(value)) for key, value in sorted(family_caps.items())}))
        if job_families:
            params_list.append(list(job_families))
        if root_name:
            params_list.append(root_name)
        params_list.append(worker_id)
    else:
        params_list.append(worker_id)
        if job_families:
            params_list.append(list(job_families))
        if root_name:
            params_list.append(root_name)
    params_list.append(max(1, min(limit, 100)))
    params = tuple(params_list)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                {caps_cte}
                UPDATE capture_jobs
                SET status = 'running',
                    attempts = attempts + 1,
                    started_at = now(),
                    completed_at = NULL,
                    locked_at = now(),
                    locked_by = %s,
                    progress_heartbeat_at = now(),
                    updated_at = now()
                WHERE id IN (
                    {claim_selection}
                )
                RETURNING id::text, job_type, job_family, resource_class, priority, time_budget_seconds,
                          status, payload, attempts, last_error, telemetry
                """,
                params,
            )
            return [
                {
                    "id": row[0],
                    "job_type": row[1],
                    "job_family": row[2],
                    "resource_class": row[3],
                    "priority": row[4],
                    "time_budget_seconds": row[5],
                    "status": row[6],
                    "payload": row[7],
                    "attempts": row[8],
                    "last_error": row[9],
                    "telemetry": row[10] or {},
                }
                for row in cur.fetchall()
            ]


def enqueue_pending_corpus_job_commands(
    *,
    limit: int = 100,
    root_name: str | None = None,
    job_families: list[str] | tuple[str, ...] | None = None,
    host_agent_roots: bool | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    capped_limit = max(1, min(int(limit or 100), 1000))
    family_filter = "AND job_family = ANY(%s)" if job_families else ""
    root_filter = "AND payload->>'root_name' = %s" if root_name else ""
    host_root_filter = ""
    if host_agent_roots is True:
        host_root_filter = """
                      AND EXISTS (
                          SELECT 1
                          FROM monitored_roots r
                          WHERE r.name = capture_jobs.payload->>'root_name'
                            AND r.metadata->>'host_access' = 'host_agent'
                      )
        """
    elif host_agent_roots is False:
        host_root_filter = """
                      AND NOT EXISTS (
                          SELECT 1
                          FROM monitored_roots r
                          WHERE r.name = capture_jobs.payload->>'root_name'
                            AND r.metadata->>'host_access' = 'host_agent'
                      )
        """
    params: list[Any] = []
    if job_families:
        params.append(list(job_families))
    if root_name:
        params.append(root_name)
    params.append(capped_limit)
    psycopg = _load_psycopg()
    jobs: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT capture_jobs.id::text,
                       capture_jobs.job_type,
                       capture_jobs.payload,
                       capture_jobs.job_family,
                       capture_jobs.resource_class,
                       capture_jobs.status,
                       EXISTS (
                           SELECT 1
                           FROM monitored_roots r
                           WHERE r.name = capture_jobs.payload->>'root_name'
                             AND r.metadata->>'host_access' = 'host_agent'
                       ) AS host_agent_route
                FROM capture_jobs
                WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                  AND delete_requested_at IS NULL
                  AND next_attempt_at <= now()
                  {family_filter}
                  {root_filter}
                  {host_root_filter}
                ORDER BY priority DESC, created_at
                LIMIT %s
                """,
                tuple(params),
            )
            rows = cur.fetchall()
            for row in rows:
                outbox = _enqueue_capture_job_command_with_cursor(
                    cur,
                    job_id=row[0],
                    job_type=row[1],
                    payload=row[2] or {},
                    job_family=row[3],
                    resource_class=row[4],
                    host_agent_route=bool(row[6]),
                )
                inserted = bool(outbox and not outbox.get("deduped"))
                jobs.append(
                    {
                        "job_id": row[0],
                        "job_type": row[1],
                        "job_family": row[3],
                        "resource_class": row[4],
                        "status": row[5],
                        "message_id": (outbox or {}).get("message_id"),
                        "deduped": bool(outbox and outbox.get("deduped")),
                        "queued": inserted,
                    }
                )
    return {
        "queued": sum(1 for job in jobs if job.get("queued")),
        "jobs": jobs,
        "root_name": root_name,
        "job_families": list(job_families or []),
    }


def enqueue_capture_job_command_by_id(
    *,
    job_id: str,
    force_new_message: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT capture_jobs.id::text,
                       capture_jobs.job_type,
                       capture_jobs.payload,
                       capture_jobs.job_family,
                       capture_jobs.resource_class,
                       capture_jobs.status,
                       EXISTS (
                           SELECT 1
                           FROM monitored_roots r
                           WHERE r.name = capture_jobs.payload->>'root_name'
                             AND r.metadata->>'host_access' = 'host_agent'
                       ) AS host_agent_route
                FROM capture_jobs
                WHERE capture_jobs.id = %s
                  AND (capture_jobs.job_type LIKE 'corpus_%%' OR capture_jobs.job_type = 'search_index_sync')
                  AND capture_jobs.status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                  AND capture_jobs.delete_requested_at IS NULL
                  AND capture_jobs.next_attempt_at <= now()
                FOR UPDATE
                """,
                (job_id,),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"claimable corpus job not found: {job_id}")
            outbox = _enqueue_capture_job_command_with_cursor(
                cur,
                job_id=row[0],
                job_type=row[1],
                payload=row[2] or {},
                job_family=row[3],
                resource_class=row[4],
                host_agent_route=bool(row[6]),
                force_new_message=force_new_message,
            )
            return {
                "job_id": row[0],
                "status": row[5],
                "message_id": (outbox or {}).get("message_id"),
                "routing_key": (outbox or {}).get("routing_key"),
                "queued": bool(outbox and not outbox.get("deduped")),
                "deduped": bool(outbox and outbox.get("deduped")),
            }


def get_capture_job(*, job_id: str, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, job_type, job_family, resource_class, priority, time_budget_seconds,
                       status, payload, attempts, last_error, telemetry, broker_message_id
                FROM capture_jobs
                WHERE id = %s
                """,
                (job_id,),
            )
            row = cur.fetchone()
            return _capture_job_from_row(row) if row else None


def claim_corpus_job_by_id(
    *,
    job_id: str,
    worker_id: str,
    broker_message_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'running',
                    attempts = CASE WHEN status = 'running' THEN attempts ELSE attempts + 1 END,
                    started_at = COALESCE(started_at, now()),
                    completed_at = NULL,
                    locked_at = now(),
                    locked_by = %s,
                    progress_heartbeat_at = now(),
                    broker_message_id = COALESCE(%s::text, broker_message_id),
                    broker_delivery_count = broker_delivery_count + 1,
                    updated_at = now()
                WHERE id = %s
                  AND (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND delete_requested_at IS NULL
                  AND (
                      status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                      OR (status = 'running' AND (%s::text IS NULL OR broker_message_id = %s::text))
                  )
                RETURNING id::text, job_type, job_family, resource_class, priority, time_budget_seconds,
                          status, payload, attempts, last_error, telemetry, broker_message_id
                """,
                (worker_id, broker_message_id, job_id, broker_message_id, broker_message_id),
            )
            row = cur.fetchone()
            return _capture_job_from_row(row) if row else None


def _capture_job_from_row(row: Any) -> dict[str, Any]:
    return {
        "id": row[0],
        "job_type": row[1],
        "job_family": row[2],
        "resource_class": row[3],
        "priority": row[4],
        "time_budget_seconds": row[5],
        "status": row[6],
        "payload": row[7] or {},
        "attempts": row[8],
        "last_error": row[9],
        "telemetry": row[10] or {},
        "broker_message_id": row[11] if len(row) > 11 else None,
    }


def enqueue_capture_job_event(
    *,
    job_id: str,
    event_type: str,
    status: str,
    details: dict[str, Any] | None = None,
    correlation_id: str | None = None,
    causation_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    payload = {
        "job_id": str(job_id),
        "status": str(status),
        "details": details or {},
    }
    event_message_id = _stable_message_id("capture-job-event", str(job_id), str(event_type), str(status))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            return _enqueue_message_outbox_with_cursor(
                cur,
                message_id=event_message_id,
                exchange=messaging.EVENTS_EXCHANGE,
                routing_key=event_type,
                message_type=event_type,
                payload=payload,
                aggregate_type="capture_jobs",
                aggregate_id=str(job_id),
                correlation_id=correlation_id,
                causation_id=causation_id,
            )


def record_runtime_component_heartbeat(
    *,
    name: str,
    status: str = "running",
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO runtime_components (name, status, heartbeat_at, metadata)
                VALUES (%s, %s, now(), %s::jsonb)
                ON CONFLICT (name) DO UPDATE SET
                    status = EXCLUDED.status,
                    heartbeat_at = now(),
                    metadata = runtime_components.metadata || EXCLUDED.metadata,
                    updated_at = now()
                RETURNING name, status, heartbeat_at, metadata, updated_at
                """,
                (name, status, _json(metadata or {})),
            )
            row = cur.fetchone()
            return {
                "name": row[0],
                "status": row[1],
                "heartbeat_at": row[2].isoformat() if row[2] else None,
                "metadata": row[3] or {},
                "updated_at": row[4].isoformat() if row[4] else None,
            }


def list_runtime_components(*, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT name, status, heartbeat_at,
                       CASE
                         WHEN heartbeat_at IS NULL THEN NULL
                         ELSE EXTRACT(EPOCH FROM (now() - heartbeat_at))::integer
                       END AS heartbeat_age_seconds,
                       metadata, updated_at
                FROM runtime_components
                ORDER BY name
                """
            )
            return [
                {
                    "name": row[0],
                    "status": row[1],
                    "heartbeat_at": row[2].isoformat() if row[2] else None,
                    "heartbeat_age_seconds": row[3],
                    "metadata": row[4] or {},
                    "updated_at": row[5].isoformat() if row[5] else None,
                }
                for row in cur.fetchall()
            ]


def complete_corpus_job(
    *,
    job_id: str,
    duration_ms: int | None = None,
    telemetry: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    terminal_telemetry = _terminal_capture_job_telemetry(telemetry)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'completed',
                    last_error = NULL,
                    completed_at = now(),
                    last_duration_ms = %s,
                    telemetry = CASE
                        WHEN job_type = 'search_index_sync' THEN telemetry
                            - 'search_index_errors'
                            - 'error_type'
                            - 'error'
                            - 'last_error'
                            - 'failed_stage'
                            - 'failure_stage'
                            - 'failed_error'
                            - 'failure_error'
                            - 'failed_reason'
                            - 'failure_reason'
                        ELSE telemetry
                    END || %s::jsonb,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND status = 'running'
                """,
                (duration_ms, _json(terminal_telemetry), job_id),
            )


def cancel_orphaned_corpus_job(
    *,
    job_id: str,
    error: str,
    duration_ms: int | None = None,
    telemetry: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'cancelled_orphaned_root',
                    last_error = %s,
                    completed_at = now(),
                    last_duration_ms = %s,
                    telemetry = telemetry || %s::jsonb,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                """,
                (error, duration_ms, _json(telemetry or {}), job_id),
            )


def cancel_missing_source_corpus_job(
    *,
    job_id: str,
    root_name: str,
    relative_path: str,
    error: str,
    duration_ms: int | None = None,
    telemetry: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'cancelled_missing_source',
                    last_error = %s,
                    completed_at = now(),
                    last_duration_ms = %s,
                    telemetry = telemetry || %s::jsonb,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                """,
                (error, duration_ms, _json(telemetry or {}), job_id),
            )
            cur.execute(
                """
                UPDATE source_assets a
                SET extraction_status = 'deleted',
                    deleted_at = COALESCE(a.deleted_at, now()),
                    metadata = a.metadata || %s::jsonb,
                    updated_at = now()
                FROM monitored_roots r
                WHERE r.id = a.root_id
                  AND r.name = %s
                  AND a.path = %s
                  AND a.deleted_at IS NULL
                """,
                (
                    _json(
                        {
                            "missing_source_deleted": True,
                            "readiness_status": "deleted",
                            "readiness_reason": error,
                        }
                    ),
                    root_name,
                    relative_path,
                ),
            )


def clear_completed_corpus_job_errors(
    *, root_name: str | None = None, url: str | None = None
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_filter = "AND payload->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...] = (root_name,) if root_name else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH cleared AS (
                    UPDATE capture_jobs
                    SET last_error = NULL,
                        updated_at = now()
                    WHERE job_type LIKE 'corpus_%%'
                      AND status = 'completed'
                      AND last_error IS NOT NULL
                      {root_filter}
                    RETURNING 1
                )
                SELECT count(*) FROM cleared
                """,
                params,
            )
            return {"root_name": root_name, "cleared": int(cur.fetchone()[0] or 0)}


def recover_stale_running_corpus_jobs(
    *,
    root_name: str | None = None,
    stale_after_seconds: int = 300,
    active_worker_grace_seconds: int | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_filter = "AND job.payload->>'root_name' = %s" if root_name else ""
    active_grace = max(1, int(active_worker_grace_seconds or stale_after_seconds or 300))
    stale_after = max(1, int(stale_after_seconds or 300))
    params: list[Any] = [stale_after, active_grace]
    if root_name:
        params.append(root_name)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH recovered AS (
                    UPDATE capture_jobs job
                    SET status = 'pending',
                        next_attempt_at = now(),
                        last_error = COALESCE(last_error, 'stale_running_recovered'),
                        completed_at = NULL,
                        locked_at = NULL,
                        locked_by = NULL,
                        telemetry = telemetry || jsonb_build_object('stale_running_recovered', true),
                        updated_at = now()
                    WHERE (job.job_type LIKE 'corpus_%%' OR job.job_type = 'search_index_sync')
                      AND job.status = 'running'
                      AND COALESCE(job.progress_heartbeat_at, job.locked_at, job.started_at, job.updated_at)
                          < now() - make_interval(secs => GREATEST(%s, COALESCE(job.time_budget_seconds, 0)))
                      AND NOT EXISTS (
                          SELECT 1
                          FROM runtime_components component
                          WHERE component.name = job.locked_by
                            AND component.metadata->>'worker_instance' = 'true'
                            AND component.status = 'running'
                            AND component.heartbeat_at >= now() - make_interval(secs => %s)
                      )
                      {root_filter}
                    RETURNING 1
                )
                SELECT count(*) FROM recovered
                """,
                tuple(params),
            )
            return {"root_name": root_name, "recovered": int(cur.fetchone()[0] or 0)}


def retry_corpus_job(
    *,
    job_id: str,
    error: str,
    cooldown_seconds: int = 300,
    status: str = "pending",
    duration_ms: int | None = None,
    telemetry: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = %s,
                    last_error = %s,
                    next_attempt_at = now() + make_interval(secs => %s),
                    last_duration_ms = %s,
                    telemetry = telemetry || %s::jsonb,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND status = 'running'
                  AND delete_requested_at IS NULL
                """,
                (status, error, max(1, cooldown_seconds), duration_ms, _json(telemetry or {}), job_id),
            )


def requeue_corpus_job(
    *,
    job_id: str,
    reason: str = "operator diagnostic remediation",
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'pending',
                    attempts = 0,
                    last_error = NULL,
                    started_at = NULL,
                    completed_at = NULL,
                    next_attempt_at = now(),
                    locked_at = NULL,
                    locked_by = NULL,
                    broker_message_id = NULL,
                    routing_key = NULL,
                    correlation_id = NULL,
                    causation_id = NULL,
                    queued_at = NULL,
                    broker_delivery_count = 0,
                    telemetry = (
                        COALESCE(telemetry, '{}'::jsonb)
                        - 'gpu_busy_first_seen_at'
                        - 'gpu_busy_retry_count'
                        - 'gpu_busy_next_cooldown_seconds'
                        - 'gpu_busy_block_after_seconds'
                        - 'gpu_busy_blocked_at'
                    ) || jsonb_build_object('remediation_reason', %s::text),
                    updated_at = now()
                WHERE id = %s
                  AND (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND delete_requested_at IS NULL
                  AND (
                      status = 'failed'
                      OR status LIKE 'blocked_%%'
                      OR status = 'retrying_locked'
                      OR status = 'retrying_vss_failed'
                      OR status = 'retrying_gpu_busy'
                      OR status LIKE 'cancelled_%%'
                  )
                RETURNING id::text, status, attempts
                """,
                (reason, job_id),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"retryable corpus job not found: {job_id}")
            return {"job_id": row[0], "status": row[1], "attempts": int(row[2] or 0)}


def cancel_corpus_job(
    *,
    job_id: str,
    actor: str = "operator",
    url: str | None = None,
) -> dict[str, Any]:
    clean_actor = str(actor or "operator").strip() or "operator"
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (job_id,),
            )
            row = cur.fetchone()
            if row is None:
                return {"job_id": job_id, "status": "not_found", "cancelled": False, "error": f"corpus job not found: {job_id}"}
            status = str(row[1] or "unknown")
            if status == "running":
                return {
                    "job_id": row[0],
                    "status": status,
                    "cancelled": False,
                    "error": "Corpus job is running and cannot be cancelled mid-execution.",
                }
            if status not in {"pending", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy"}:
                return {
                    "job_id": row[0],
                    "status": status,
                    "cancelled": False,
                    "error": f"Corpus job status {status} cannot be cancelled.",
                }
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'cancelled_operator',
                    last_error = %s,
                    completed_at = now(),
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                  -- legacy cancellable statuses: status IN ('pending', 'retrying_locked', 'retrying_vss_failed')
                """,
                (f"cancelled by {clean_actor}", row[0]),
            )
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('capture_job.cancelled', 'capture_jobs', %s, %s::jsonb)
                """,
                (row[0], _json({"actor": clean_actor, "previous_status": status})),
            )
            return {"job_id": row[0], "status": "cancelled_operator", "cancelled": True}


def block_corpus_job(
    *,
    job_id: str,
    error: str,
    status: str = "blocked_missing_dependency",
    duration_ms: int | None = None,
    telemetry: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = %s,
                    last_error = %s,
                    completed_at = now(),
                    last_duration_ms = %s,
                    telemetry = telemetry || %s::jsonb,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND status = 'running'
                """,
                (status, error, duration_ms, _json(telemetry or {}), job_id),
            )


def cancel_unseen_corpus_job(
    *,
    job_id: str,
    error: str,
    duration_ms: int | None = None,
    telemetry: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH cancelled AS (
                    UPDATE capture_jobs
                    SET status = '{UNSEEN_ASSET_CANCELLED_STATUS}',
                        last_error = %s,
                        completed_at = now(),
                        last_duration_ms = %s,
                        telemetry = telemetry || %s::jsonb,
                        locked_at = NULL,
                        locked_by = NULL,
                        updated_at = now()
                    WHERE id = %s
                      AND job_type LIKE 'corpus_%%'
                      AND status = 'running'
                    RETURNING id
                )
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                SELECT 'capture_job.cancelled_unseen_asset',
                       'capture_jobs',
                       id,
                       jsonb_build_object('reason', %s::text)
                FROM cancelled
                """,
                (error, duration_ms, _json(telemetry or {}), job_id, error),
            )


def repair_extracted_corpus_asset_statuses(
    *, root_name: str | None = None, url: str | None = None
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_filter = "AND r.name = %s" if root_name else ""
    params: tuple[Any, ...] = (root_name,) if root_name else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH repaired AS (
                    UPDATE source_assets a
                    SET extraction_status = 'indexed',
                        indexed_at = COALESCE(a.indexed_at, now()),
                        updated_at = now()
                    FROM monitored_roots r
                    WHERE r.id = a.root_id
                      {root_filter}
                      AND a.deleted_at IS NULL
                      AND a.extraction_status = 'queued'
                      AND EXISTS (
                          SELECT 1
                          FROM asset_chunks c
                          WHERE c.asset_id = a.id
                      )
                    RETURNING 1
                )
                SELECT count(*) FROM repaired
                """,
                params,
            )
            repaired = int(cur.fetchone()[0] or 0)
            cur.execute(
                f"""
                WITH marked AS (
                    UPDATE source_assets a
                    SET extraction_status = 'deleted',
                        deleted_at = COALESCE(a.deleted_at, now()),
                        metadata = a.metadata || '{{"internal_mail_artifact": true, "readiness_status": "skipped_internal"}}'::jsonb,
                        updated_at = now()
                    FROM monitored_roots r
                    WHERE r.id = a.root_id
                      {root_filter}
                      AND a.deleted_at IS NULL
                      AND array_length(string_to_array(a.path, '/'), 1) = 2
                      AND lower(split_part(a.path, '/', 2)) IN ('message.eml', 'message.msg', 'body.html')
                    RETURNING 1
                )
                SELECT count(*) FROM marked
                """,
                params,
            )
            internal_deleted = int(cur.fetchone()[0] or 0)
            cur.execute(
                f"""
                WITH stale_chunks AS (
                    SELECT c.id
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE (
                        a.deleted_at IS NOT NULL
                        OR a.extraction_status NOT IN ('indexed', 'processing_staged')
                        OR (
                            array_length(string_to_array(a.path, '/'), 1) = 2
                            AND lower(split_part(a.path, '/', 2)) IN ('message.eml', 'message.msg', 'body.html')
                        )
                    )
                      {root_filter}
                ),
                deleted AS (
                    DELETE FROM asset_chunks c
                    USING stale_chunks s
                    WHERE c.id = s.id
                    RETURNING 1
                )
                SELECT count(*) FROM deleted
                """,
                params,
            )
            chunks_purged = int(cur.fetchone()[0] or 0)
            cur.execute(
                f"""
                SELECT c.id::text, c.chunk_index, c.title, c.body, c.metadata,
                       a.path, r.name
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND r.metadata ? 'mail_profile'
                  AND a.deleted_at IS NULL
                  AND a.extraction_status = 'indexed'
                  AND c.body <> ''
                  AND (
                      (
                          array_length(string_to_array(a.path, '/'), 1) = 2
                          AND lower(split_part(a.path, '/', 2)) = 'body.txt'
                      )
                      OR (
                          array_length(string_to_array(a.path, '/'), 1) >= 3
                          AND lower(split_part(a.path, '/', 2)) = 'attachments'
                      )
                  )
                  {root_filter}
                ORDER BY c.updated_at DESC
                LIMIT 1000
                """,
                params,
            )
            mail_plaintext_chunks_repaired = 0
            for chunk_id, chunk_index, title, body, _metadata, asset_path, row_root_name in cur.fetchall():
                kind = mail_content_store.managed_mail_content_kind(str(asset_path or ""))
                if not kind:
                    continue
                content_ref = mail_content_store.write_mail_content(
                    root_name=str(row_root_name or ""),
                    asset_path=str(asset_path or ""),
                    chunk_index=int(chunk_index or 0),
                    title=str(title or ""),
                    text=str(body or ""),
                    kind=kind,
                )
                cur.execute(
                    """
                    UPDATE asset_chunks
                    SET body = '',
                        metadata = metadata || %s::jsonb,
                        updated_at = now()
                    WHERE id = %s
                    """,
                    (
                        _json(
                            _sanitize_operational_metadata(
                                {"sidecar_ref": {"source": "managed_mail", **content_ref}}
                            )
                        ),
                        chunk_id,
                    ),
                )
                mail_plaintext_chunks_repaired += 1
            return {
                "root_name": root_name,
                "repaired": repaired,
                "internal_mail_artifacts_deleted": internal_deleted,
                "chunks_purged": chunks_purged,
                "mail_plaintext_chunks_repaired": mail_plaintext_chunks_repaired,
            }


def worker_family_stats(*, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT job_family,
                       resource_class,
                       count(*) FILTER (WHERE status = 'pending')::integer AS pending,
                       count(*) FILTER (WHERE status = 'running')::integer AS running,
                       count(*) FILTER (WHERE status LIKE 'blocked_%')::integer AS blocked,
                       count(*) FILTER (WHERE status = 'failed')::integer AS failed,
                       avg(last_duration_ms)::integer AS avg_duration_ms,
                       percentile_disc(0.95) WITHIN GROUP (ORDER BY last_duration_ms)::integer AS p95_duration_ms,
                       max(last_duration_ms)::integer AS max_duration_ms,
                       COALESCE(sum((telemetry->>'ocr_cache_hits')::integer), 0)::integer AS ocr_cache_hits,
                       COALESCE(sum((telemetry->>'ocr_cache_misses')::integer), 0)::integer AS ocr_cache_misses,
                       COALESCE(sum((telemetry->>'asr_cache_hits')::integer), 0)::integer AS asr_cache_hits,
                       COALESCE(sum((telemetry->>'asr_cache_misses')::integer), 0)::integer AS asr_cache_misses,
                       COALESCE(sum((telemetry->>'asr_segments')::integer), 0)::integer AS asr_segments,
                       COALESCE(sum((telemetry->>'container_member_count')::integer), 0)::integer AS container_member_count,
                       COALESCE(sum((telemetry->>'container_parsed_child_count')::integer), 0)::integer AS container_parsed_child_count,
                       COALESCE(sum((telemetry->>'container_skipped_child_count')::integer), 0)::integer AS container_skipped_child_count,
                       COALESCE(sum((telemetry->>'container_blocked_dependency_count')::integer), 0)::integer AS container_blocked_dependency_count,
                       COALESCE(sum((telemetry->>'vision_cache_hits')::integer), 0)::integer AS vision_cache_hits,
                       COALESCE(sum((telemetry->>'vision_cache_misses')::integer), 0)::integer AS vision_cache_misses,
                       COALESCE(sum((telemetry->>'vision_descriptions')::integer), 0)::integer AS vision_descriptions,
                       COALESCE(sum((telemetry->>'vision_blocked_dependency_count')::integer), 0)::integer AS vision_blocked_dependency_count,
                       COALESCE(sum((telemetry->>'decorative_image_skips')::integer), 0)::integer AS decorative_image_skips,
                       COALESCE(sum((telemetry->>'frame_sample_count')::integer), 0)::integer AS frame_sample_count,
                       COALESCE(sum((telemetry->>'thumbnail_cache_hits')::integer), 0)::integer AS thumbnail_cache_hits,
                       COALESCE(sum((telemetry->>'thumbnail_cache_misses')::integer), 0)::integer AS thumbnail_cache_misses,
                       COALESCE(sum((telemetry->>'embedding_vectors')::integer), 0)::integer AS embedding_vectors,
                       COALESCE(sum((telemetry->>'embedding_skipped_unchanged')::integer), 0)::integer AS embedding_skipped_unchanged,
                       COALESCE(sum((telemetry->>'embedding_batches')::integer), 0)::integer AS embedding_batches,
                       COALESCE(sum((telemetry->>'embedding_cache_hits')::integer), 0)::integer AS embedding_cache_hits,
                       COALESCE(sum((telemetry->>'embedding_cache_misses')::integer), 0)::integer AS embedding_cache_misses,
                       COALESCE(sum((telemetry->>'parser_cache_hits')::integer), 0)::integer AS parser_cache_hits,
                       COALESCE(sum((telemetry->>'parser_cache_misses')::integer), 0)::integer AS parser_cache_misses,
                       COALESCE(sum((telemetry->>'manifest_skipped_unchanged')::integer), 0)::integer AS manifest_skipped_unchanged,
                       EXTRACT(EPOCH FROM (now() - min(created_at) FILTER (WHERE status = 'pending')))::integer AS oldest_pending_age_seconds,
                       count(*) FILTER (WHERE status = 'retrying_locked')::integer AS retrying_locked,
                       count(*) FILTER (WHERE status = 'retrying_gpu_busy')::integer AS retrying_gpu_busy,
                       count(*) FILTER (WHERE status LIKE 'blocked_%%' AND locked_at IS NOT NULL)::integer AS blocked_locked,
                       (
                           SELECT COALESCE(
                               jsonb_agg(
                                   jsonb_build_object(
                                       'id', recent.id::text,
                                       'path', recent.payload->>'path',
                                       'duration_ms', recent.last_duration_ms
                                   )
                                   ORDER BY recent.last_duration_ms DESC
                               ),
                               '[]'::jsonb
                           )
                           FROM (
                               SELECT id, payload, last_duration_ms
                               FROM capture_jobs recent
                               WHERE recent.job_type LIKE 'corpus_%%'
                                 AND recent.job_family = capture_jobs.job_family
                                 AND recent.last_duration_ms IS NOT NULL
                               ORDER BY recent.last_duration_ms DESC NULLS LAST
                               LIMIT 5
                           ) recent
                       ) AS slowest_recent_jobs
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%'
                GROUP BY job_family, resource_class
                ORDER BY job_family
                """
            )
            return [
                {
                    "family": row[0],
                    "resource_class": row[1],
                    "pending": row[2],
                    "running": row[3],
                    "blocked": row[4],
                    "failed": row[5],
                    "avg_duration_ms": row[6],
                    "p95_duration_ms": row[7],
                    "max_duration_ms": row[8],
                    "ocr_cache_hits": row[9],
                    "ocr_cache_misses": row[10],
                    "asr_cache_hits": row[11],
                    "asr_cache_misses": row[12],
                    "asr_segments": row[13],
                    "container_member_count": row[14],
                    "container_parsed_child_count": row[15],
                    "container_skipped_child_count": row[16],
                    "container_blocked_dependency_count": row[17],
                    "vision_cache_hits": row[18],
                    "vision_cache_misses": row[19],
                    "vision_descriptions": row[20],
                    "vision_blocked_dependency_count": row[21],
                    "decorative_image_skips": row[22],
                    "frame_sample_count": row[23],
                    "thumbnail_cache_hits": row[24],
                    "thumbnail_cache_misses": row[25],
                    "embedding_vectors": row[26],
                    "embedding_skipped_unchanged": row[27],
                    "embedding_batches": row[28],
                    "embedding_cache_hits": row[29],
                    "embedding_cache_misses": row[30],
                    "parser_cache_hits": row[31],
                    "parser_cache_misses": row[32],
                    "manifest_skipped_unchanged": row[33],
                    "oldest_pending_age_seconds": row[34],
                    "retrying_locked": row[35],
                    **({"retrying_gpu_busy": row[36]} if len(row) > 38 else {}),
                    "blocked_locked": row[37] if len(row) > 38 else row[36],
                    "slowest_recent_jobs": _slow_jobs_from_db((row[38] if len(row) > 38 else row[37]) or []),
                }
                for row in cur.fetchall()
            ]


def code_index_status(*, root_name: str | None = None, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_filter = "WHERE r.name = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT r.name,
                       COUNT(a.id)::integer AS asset_count,
                       COUNT(a.id) FILTER (
                         WHERE a.metadata->'code'->>'parser_status' = 'fallback'
                            OR EXISTS (
                                SELECT 1
                                FROM asset_chunks c_fallback
                                WHERE c_fallback.asset_id = a.id
                                  AND c_fallback.metadata->'code'->>'parser_status' = 'fallback'
                            )
                            OR EXISTS (
                                SELECT 1
                                FROM code_symbols cs_fallback
                                WHERE cs_fallback.source_asset_id = a.id
                                  AND cs_fallback.parser_status = 'fallback'
                            )
                       )::integer AS fallback_count,
                       COUNT(a.id) FILTER (
                         WHERE a.metadata->'code'->>'generated' = 'true'
                            OR EXISTS (
                                SELECT 1
                                FROM asset_chunks c_generated
                                WHERE c_generated.asset_id = a.id
                                  AND (
                                      c_generated.metadata->>'generated' = 'true'
                                      OR c_generated.metadata->'code'->>'generated' = 'true'
                                  )
                            )
                            OR EXISTS (
                                SELECT 1
                                FROM code_symbols cs_generated
                                WHERE cs_generated.source_asset_id = a.id
                                  AND cs_generated.metadata->>'generated' = 'true'
                            )
                       )::integer AS generated_count
                FROM monitored_roots r
                LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL
                {root_filter}
                GROUP BY r.name
                ORDER BY r.name
                """,
                root_params,
            )
            roots_by_name: dict[str, dict[str, Any]] = {
                str(row[0]): {
                    "root_name": row[0],
                    "asset_count": int(row[1] or 0),
                    "chunk_count": 0,
                    "symbol_count": 0,
                    "reference_count": 0,
                    "fallback_count": int(row[2] or 0),
                    "generated_count": int(row[3] or 0),
                }
                for row in cur.fetchall()
            }

            count_queries = [
                (
                    "chunk_count",
                    f"""
                    SELECT r.name, COUNT(c.id)::integer
                    FROM monitored_roots r
                    LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL
                    LEFT JOIN asset_chunks c ON c.asset_id = a.id
                    {root_filter}
                    GROUP BY r.name
                    """,
                ),
                (
                    "symbol_count",
                    f"""
                    SELECT r.name, COUNT(cs.id)::integer
                    FROM monitored_roots r
                    LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL
                    LEFT JOIN code_symbols cs ON cs.source_asset_id = a.id
                    {root_filter}
                    GROUP BY r.name
                    """,
                ),
                (
                    "reference_count",
                    f"""
                    SELECT r.name, COUNT(cr.id)::integer
                    FROM monitored_roots r
                    LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL
                    LEFT JOIN code_references cr ON cr.source_asset_id = a.id
                    {root_filter}
                    GROUP BY r.name
                    """,
                ),
            ]
            for field, sql in count_queries:
                cur.execute(sql, root_params)
                for row in cur.fetchall():
                    root = roots_by_name.setdefault(
                        str(row[0]),
                        {
                            "root_name": row[0],
                            "asset_count": 0,
                            "chunk_count": 0,
                            "symbol_count": 0,
                            "reference_count": 0,
                            "fallback_count": 0,
                            "generated_count": 0,
                        },
                    )
                    root[field] = int(row[1] or 0)

            roots = []
            for root in sorted(roots_by_name.values(), key=lambda item: str(item["root_name"])):
                root_name_value = str(root["root_name"])
                roots.append(
                    {
                        **root,
                        "languages": _code_language_counts(cur, root_name_value),
                        "parser_statuses": _code_parser_status_counts(cur, root_name_value),
                        "slow_files": _code_slow_files(cur, root_name_value),
                    }
                )
            return {
                "roots": roots,
                "totals": {
                    "asset_count": sum(row["asset_count"] for row in roots),
                    "chunk_count": sum(row["chunk_count"] for row in roots),
                    "symbol_count": sum(row["symbol_count"] for row in roots),
                    "reference_count": sum(row["reference_count"] for row in roots),
                    "fallback_count": sum(row["fallback_count"] for row in roots),
                    "generated_count": sum(row["generated_count"] for row in roots),
                },
            }


def search_code_symbols(
    *,
    query: str,
    root_name: str | None = None,
    language: str | None = None,
    symbol_kind: str | None = None,
    relationship: str | None = None,
    path_glob: str | list[str] | None = None,
    include_generated: bool = False,
    limit: int = 20,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    filters = ["a.deleted_at IS NULL", "(cs.name ILIKE %s OR cs.qualified_name ILIKE %s OR cs.path ILIKE %s)"]
    needle = f"%{query}%"
    params: list[Any] = [needle, needle, needle]
    if root_name:
        filters.append("r.name = %s")
        params.append(root_name)
    if language:
        filters.append("cs.language = %s")
        params.append(language)
    if symbol_kind:
        filters.append("cs.symbol_kind = %s")
        params.append(symbol_kind)
    if relationship:
        filters.append(
            """
            EXISTS (
                SELECT 1 FROM code_references cr_filter
                WHERE cr_filter.source_asset_id = cs.source_asset_id
                  AND cr_filter.relationship_kind = %s
            )
            """
        )
        params.append(relationship)
    path_globs = _normalize_path_globs(path_glob)
    if path_globs:
        path_clauses = []
        for pattern in path_globs:
            path_clauses.append("a.path LIKE %s ESCAPE '\\'")
            params.append(_glob_to_sql_like(pattern))
        filters.append(f"({' OR '.join(path_clauses)})")
    if not include_generated:
        filters.append("COALESCE(cs.metadata->>'generated', 'false') <> 'true'")
    capped_limit = max(1, min(int(limit or 20), 100))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT cs.qualified_name, cs.name, cs.symbol_kind, cs.language, cs.path,
                       cs.line_start, cs.line_end, cs.parser_status, cs.confidence, r.name,
                       cs.metadata
                FROM code_symbols cs
                JOIN source_assets a ON a.id = cs.source_asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE {' AND '.join(filters)}
                ORDER BY
                  CASE WHEN lower(cs.qualified_name) = lower(%s) OR lower(cs.name) = lower(%s) THEN 0 ELSE 1 END,
                  cs.confidence DESC,
                  cs.qualified_name
                LIMIT %s
                """,
                tuple([*params, query, query, capped_limit]),
            )
            return [_code_symbol_row(row) for row in cur.fetchall()]


def lookup_code_symbol(
    *,
    symbol: str,
    root_name: str | None = None,
    language: str | None = None,
    include_references: bool = True,
    limit: int = 20,
    url: str | None = None,
) -> dict[str, Any]:
    matches = search_code_symbols(query=symbol, root_name=root_name, language=language, limit=limit, url=url)
    references = (
        _lookup_code_references(symbol=symbol, root_name=root_name, language=language, limit=limit, url=url)
        if include_references
        else []
    )
    return {"query": symbol, "matches": matches, "references": references}


def recent_retrieval_explain_diagnostics(*, limit: int = 25, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT query_count, metrics, case_results, created_at
                FROM retrieval_benchmark_runs
                ORDER BY created_at DESC
                LIMIT %s
                """,
                (max(1, min(int(limit or 25), 100)),),
            )
            rows = []
            for row in cur.fetchall():
                query_count = int(row[0] or 0)
                metrics = row[1] if isinstance(row[1], dict) else {}
                cases = row[2] if isinstance(row[2], list) else []
                hashes = [case.get("query_hash") for case in cases if isinstance(case, dict) and case.get("query_hash")]
                failures = [
                    case
                    for case in cases
                    if isinstance(case, dict)
                    and (
                        case.get("passed") is False
                        or str(case.get("status") or "").lower() in {"failed", "failure", "error"}
                    )
                ]
                rows.append(
                    {
                        "query_hash": hashes[0] if hashes else None,
                        "result_count": query_count or int(metrics.get("query_count") or metrics.get("case_count") or 0),
                        "confidence": metrics.get("confidence_band") or "unknown",
                        "failed_case_count": len(failures) or int(metrics.get("failed_count") or 0),
                        "created_at": row[3].isoformat() if row[3] else None,
                    }
                )
            return rows


def _code_language_counts(cur: Any, root_name: str) -> dict[str, int]:
    cur.execute(
        """
        SELECT language, count(*)::integer
        FROM code_symbols cs
        JOIN source_assets a ON a.id = cs.source_asset_id
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE r.name = %s
          AND a.deleted_at IS NULL
        GROUP BY language
        ORDER BY language
        """,
        (root_name,),
    )
    return {str(row[0] or "unknown"): int(row[1] or 0) for row in cur.fetchall()}


def _code_parser_status_counts(cur: Any, root_name: str) -> dict[str, int]:
    cur.execute(
        """
        SELECT parser_status, count(*)::integer
        FROM (
            SELECT cs.parser_status
            FROM code_symbols cs
            JOIN source_assets a ON a.id = cs.source_asset_id
            JOIN monitored_roots r ON r.id = a.root_id
            WHERE r.name = %s
              AND a.deleted_at IS NULL
            UNION ALL
            SELECT COALESCE(c.metadata->'code'->>'parser_status', c.metadata->>'parser_status')
            FROM asset_chunks c
            JOIN source_assets a ON a.id = c.asset_id
            JOIN monitored_roots r ON r.id = a.root_id
            WHERE r.name = %s
              AND a.deleted_at IS NULL
        ) statuses
        WHERE parser_status IS NOT NULL
        GROUP BY parser_status
        ORDER BY parser_status
        """,
        (root_name, root_name),
    )
    return {str(row[0] or "unknown"): int(row[1] or 0) for row in cur.fetchall()}


def _code_slow_files(cur: Any, root_name: str) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT payload->>'path', last_duration_ms
        FROM capture_jobs
        WHERE payload->>'root_name' = %s
          AND job_type LIKE 'corpus_%%'
          AND last_duration_ms IS NOT NULL
        ORDER BY last_duration_ms DESC NULLS LAST
        LIMIT 5
        """,
        (root_name,),
    )
    return [{"path": row[0], "duration_ms": row[1]} for row in cur.fetchall()]


def _lookup_code_references(
    *,
    symbol: str,
    root_name: str | None,
    language: str | None,
    limit: int,
    url: str | None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    filters = ["a.deleted_at IS NULL", "(cr.target ILIKE %s OR cr.source_symbol ILIKE %s)"]
    needle = f"%{symbol}%"
    params: list[Any] = [needle, needle]
    if root_name:
        filters.append("r.name = %s")
        params.append(root_name)
    if language:
        filters.append("cr.language = %s")
        params.append(language)
    params.append(max(1, min(int(limit or 20), 100)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT cr.target, cr.relationship_kind, cr.source_symbol, cr.language, cr.path,
                       cr.line_start, cr.line_end, cr.parser_status, cr.confidence, r.name
                FROM code_references cr
                JOIN source_assets a ON a.id = cr.source_asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE {' AND '.join(filters)}
                ORDER BY cr.confidence DESC, cr.target
                LIMIT %s
                """,
                tuple(params),
            )
            return [_code_reference_row(row) for row in cur.fetchall()]


def _code_symbol_row(row: tuple[Any, ...]) -> dict[str, Any]:
    metadata = row[10] if len(row) > 10 and isinstance(row[10], dict) else {}
    routes = metadata.get("routes") if isinstance(metadata.get("routes"), list) else []
    payload = {
        "symbol": row[0],
        "name": row[1],
        "symbol_kind": row[2],
        "language": row[3],
        "path": row[4],
        "line_start": row[5],
        "line_end": row[6],
        "parser_status": row[7],
        "confidence": row[8],
        "root_name": row[9],
        "relationship": "definition",
        "is_generated": bool(metadata.get("generated")),
        "target_symbol": row[0],
    }
    if routes:
        payload["route"] = routes[0]
    return payload


def _code_reference_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "target": row[0],
        "relationship": row[1],
        "source_symbol": row[2],
        "language": row[3],
        "path": row[4],
        "line_start": row[5],
        "line_end": row[6],
        "parser_status": row[7],
        "confidence": row[8],
        "root_name": row[9],
    }


def benchmark_fixture_stats(*, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT telemetry->>'benchmark_fixture' AS name,
                       COALESCE(sum((telemetry->>'benchmark_file_count')::integer), 0)::integer AS file_count,
                       COALESCE(sum(last_duration_ms), 0)::integer AS elapsed_ms,
                       count(*)::integer AS jobs_queued,
                       count(*) FILTER (WHERE status = 'completed')::integer AS jobs_completed,
                       count(*) FILTER (WHERE status LIKE 'blocked_%')::integer AS jobs_blocked,
                       COALESCE(sum((telemetry->>'benchmark_cache_hits')::integer), 0)::integer AS cache_hits,
                       COALESCE(sum((telemetry->>'benchmark_cache_misses')::integer), 0)::integer AS cache_misses
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%'
                  AND telemetry ? 'benchmark_fixture'
                GROUP BY telemetry->>'benchmark_fixture'
                ORDER BY telemetry->>'benchmark_fixture'
                """
            )
            return [
                {
                    "name": row[0],
                    "file_count": row[1],
                    "elapsed_ms": row[2],
                    "jobs_queued": row[3],
                    "jobs_completed": row[4],
                    "jobs_blocked": row[5],
                    "cache_hits": row[6],
                    "cache_misses": row[7],
                }
                for row in cur.fetchall()
            ]


def create_benchmark_soak_jobs(
    *,
    tag: str,
    fixture: str,
    file_count: int,
    family: str = "all",
    label: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    normalized_family = str(family or "all").lower()
    if normalized_family != "all" and normalized_family not in JOB_FAMILIES:
        raise ValueError(f"unknown benchmark worker family: {normalized_family}")
    families = list(JOB_FAMILIES) if normalized_family == "all" else [normalized_family]
    row_count = max(1, min(int(file_count or 1), 500))
    psycopg = _load_psycopg()
    inserted = 0
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            for index in range(row_count):
                job_family = families[index % len(families)]
                job_type = "search_index_sync" if job_family == "embedding" else f"corpus_extract_{job_family}"
                payload = {
                    "benchmark": True,
                    "benchmark_tag": tag,
                    "benchmark_fixture": fixture,
                    "benchmark_label": label,
                    "benchmark_outcome": "blocked" if index % 7 == 6 else "completed",
                    "path": f"synthetic/{fixture}/{job_family}-{index:04d}",
                    "root_name": "__benchmark__",
                }
                result = _enqueue_unique_capture_job_with_cursor(
                    cur,
                    job_type=job_type,
                    payload=payload,
                    job_family=job_family,
                    resource_class=resource_class_for_family(job_family),
                    priority=default_priority_for_family(job_family),
                    time_budget_seconds=time_budget_for_family(job_family),
                    telemetry={"benchmark_tag": tag, "benchmark_fixture": fixture, "benchmark_file_count": row_count},
                    audit_details={"job_type": job_type, "benchmark_tag": tag, "benchmark_fixture": fixture},
                )
                inserted += 0 if result["deduped"] else 1
    return {"tag": tag, "created": inserted, "family": normalized_family}


def purge_benchmark_soak_jobs(*, tag: str, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                DELETE FROM capture_jobs
                WHERE payload->>'benchmark_tag' = %s
                   OR telemetry->>'benchmark_tag' = %s
                """,
                (tag, tag),
            )
            return {"tag": tag, "purged": int(cur.rowcount or 0)}


def record_benchmark_run(
    *,
    fixture: str,
    file_count: int,
    elapsed_ms: int,
    timings_ms: list[int] | tuple[int, ...] | None = None,
    mode: str = "scan",
    label: str | None = None,
    compare_label: str | None = None,
    status: str = "completed",
    warm_state: str = "cold",
    pass_index: int = 1,
    hash_parallelism: int = 1,
    worker_count: int = 1,
    manifest_skipped_unchanged: int = 0,
    cache_hits: int = 0,
    cache_misses: int = 0,
    jobs_queued: int | None = None,
    jobs_completed: int | None = None,
    jobs_blocked: int = 0,
    worker_family_breakdown: dict[str, Any] | None = None,
    metadata: dict[str, Any] | None = None,
    scope_type: str = "synthetic",
    scope_hash: str | None = None,
    deployment_label: str | None = None,
    build_metadata: dict[str, Any] | None = None,
    settings_snapshot: dict[str, Any] | None = None,
    model_telemetry: dict[str, Any] | None = None,
    recommendation_metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    normalized_timings = sorted(int(value) for value in (timings_ms or []) if value is not None)
    p50_ms = _percentile_disc(normalized_timings, 0.50)
    p95_ms = _percentile_disc(normalized_timings, 0.95)
    max_ms = max(normalized_timings) if normalized_timings else None
    safe_file_count = max(0, int(file_count))
    safe_elapsed_ms = max(0, int(elapsed_ms))
    throughput = (safe_file_count / (safe_elapsed_ms / 1000.0)) if safe_elapsed_ms > 0 else 0.0
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO acceleration_benchmark_runs (
                    fixture, mode, label, compare_label, status, file_count, elapsed_ms, throughput_files_per_second,
                    p50_ms, p95_ms, max_ms, warm_state, cache_hits, cache_misses,
                    jobs_queued, jobs_completed, jobs_blocked, pass_index, hash_parallelism,
                    worker_count, manifest_skipped_unchanged, worker_family_breakdown, metadata,
                    scope_type, scope_hash, deployment_label, build_metadata, settings_snapshot,
                    model_telemetry, recommendation_metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb, %s::jsonb, %s, %s, %s, %s::jsonb, %s::jsonb, %s::jsonb, %s::jsonb)
                RETURNING id::text, created_at
                """,
                (
                    fixture,
                    _normalize_benchmark_mode(mode),
                    _blank_to_none(label),
                    _blank_to_none(compare_label),
                    status,
                    safe_file_count,
                    safe_elapsed_ms,
                    throughput,
                    p50_ms,
                    p95_ms,
                    max_ms,
                    warm_state,
                    max(0, int(cache_hits)),
                    max(0, int(cache_misses)),
                    max(0, int(jobs_queued if jobs_queued is not None else safe_file_count)),
                    max(0, int(jobs_completed if jobs_completed is not None else safe_file_count)),
                    max(0, int(jobs_blocked)),
                    max(1, int(pass_index or 1)),
                    max(1, int(hash_parallelism or 1)),
                    max(1, int(worker_count or 1)),
                    max(0, int(manifest_skipped_unchanged or 0)),
                    _json(worker_family_breakdown or {}),
                    _json(_sanitize_operational_metadata(metadata or {})),
                    _normalize_benchmark_scope_type(scope_type),
                    _blank_to_none(scope_hash),
                    _blank_to_none(deployment_label),
                    _json(_sanitize_operational_metadata(build_metadata or {})),
                    _json(_sanitize_operational_metadata(settings_snapshot or {})),
                    _json(_sanitize_operational_metadata(model_telemetry or {})),
                    _json(_sanitize_operational_metadata(recommendation_metadata or {})),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "fixture": fixture,
                "mode": _normalize_benchmark_mode(mode),
                "label": _blank_to_none(label),
                "compare_label": _blank_to_none(compare_label),
                "status": status,
                "scope_type": _normalize_benchmark_scope_type(scope_type),
                "scope_hash": _blank_to_none(scope_hash),
                "deployment_label": _blank_to_none(deployment_label),
                "file_count": safe_file_count,
                "elapsed_ms": safe_elapsed_ms,
                "throughput_files_per_second": throughput,
                "created_at": row[1].isoformat() if row[1] else None,
            }


def latest_benchmark_runs(*, limit: int = 10, url: str | None = None) -> list[dict[str, Any]]:
    return list_benchmark_runs(limit=limit, url=url)


def list_benchmark_runs(
    *,
    fixture: str | None = None,
    mode: str | None = None,
    label: str | None = None,
    compare_label: str | None = None,
    warm_state: str | None = None,
    scope_type: str | None = None,
    scope_hash: str | None = None,
    deployment_label: str | None = None,
    scenario: str | None = None,
    freshness_hours: int | None = None,
    limit: int = 20,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    filters: list[str] = []
    params: list[Any] = []
    if fixture:
        filters.append("fixture = %s")
        params.append(fixture)
    if mode:
        filters.append("mode = %s")
        params.append(_normalize_benchmark_mode(mode))
    if label:
        filters.append("label = %s")
        params.append(label)
    if compare_label:
        filters.append("compare_label = %s")
        params.append(compare_label)
    if warm_state:
        filters.append("warm_state = %s")
        params.append(warm_state)
    if scope_type:
        filters.append("scope_type = %s")
        params.append(_normalize_benchmark_scope_type(scope_type))
    if scope_hash:
        filters.append("scope_hash = %s")
        params.append(scope_hash)
    if deployment_label:
        filters.append("deployment_label = %s")
        params.append(deployment_label)
    if scenario:
        filters.append("COALESCE(recommendation_metadata->>'scenario', metadata->>'scenario', 'standard') = %s")
        params.append(str(scenario).strip().lower().replace("-", "_"))
    if freshness_hours is not None:
        filters.append("created_at >= now() - (%s::int * interval '1 hour')")
        params.append(max(1, int(freshness_hours)))
    where_clause = f"WHERE {' AND '.join(filters)}" if filters else ""
    params.append(max(1, min(limit, 200)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH ordered AS (
                    SELECT current_run.id::text, current_run.fixture, current_run.mode, current_run.label,
                           current_run.compare_label, current_run.status, current_run.file_count,
                           current_run.elapsed_ms, current_run.throughput_files_per_second,
                           current_run.p50_ms, current_run.p95_ms, current_run.max_ms, current_run.warm_state,
                           current_run.cache_hits, current_run.cache_misses, current_run.jobs_queued,
                           current_run.jobs_completed, current_run.jobs_blocked, current_run.pass_index,
                           current_run.hash_parallelism, current_run.worker_count,
                           current_run.manifest_skipped_unchanged, current_run.worker_family_breakdown,
                           current_run.metadata, current_run.scope_type, current_run.scope_hash,
                           current_run.deployment_label, current_run.build_metadata,
                           current_run.settings_snapshot, current_run.model_telemetry,
                           current_run.recommendation_metadata, current_run.created_at,
                           COALESCE(
                               label_baseline.elapsed_ms,
                               lead(current_run.elapsed_ms) OVER (
                                   PARTITION BY current_run.fixture, current_run.mode, current_run.file_count, current_run.warm_state
                                   ORDER BY current_run.created_at DESC
                               )
                           ) AS previous_elapsed_ms,
                           COALESCE(
                               label_baseline.throughput_files_per_second,
                               lead(current_run.throughput_files_per_second) OVER (
                                   PARTITION BY current_run.fixture, current_run.mode, current_run.file_count, current_run.warm_state
                                   ORDER BY current_run.created_at DESC
                               )
                           ) AS previous_throughput_files_per_second
                    FROM acceleration_benchmark_runs current_run
                    LEFT JOIN LATERAL (
                        SELECT prior.elapsed_ms, prior.throughput_files_per_second
                        FROM acceleration_benchmark_runs prior
                        WHERE current_run.compare_label IS NOT NULL
                          AND prior.fixture = current_run.fixture
                          AND prior.mode = current_run.mode
                          AND prior.file_count = current_run.file_count
                          AND prior.warm_state = current_run.warm_state
                          AND prior.label = current_run.compare_label
                          AND prior.created_at < current_run.created_at
                        ORDER BY prior.created_at DESC
                        LIMIT 1
                    ) label_baseline ON TRUE
                )
                SELECT id, fixture, mode, label, compare_label, status,
                       file_count, elapsed_ms, throughput_files_per_second,
                       p50_ms, p95_ms, max_ms, warm_state, cache_hits, cache_misses,
                       jobs_queued, jobs_completed, jobs_blocked, pass_index,
                       hash_parallelism, worker_count, manifest_skipped_unchanged, worker_family_breakdown,
                       metadata, scope_type, scope_hash, deployment_label, build_metadata,
                       settings_snapshot, model_telemetry, recommendation_metadata,
                       created_at, previous_elapsed_ms, previous_throughput_files_per_second
                FROM ordered
                {where_clause}
                ORDER BY created_at DESC
                LIMIT %s
                """,
                tuple(params),
            )
            return [_benchmark_run_row(row) for row in cur.fetchall()]


def record_retrieval_benchmark_run(
    *,
    suite: str,
    status: str = "completed",
    label: str | None = None,
    compare_label: str | None = None,
    query_count: int = 0,
    passed_count: int = 0,
    failed_count: int = 0,
    metrics: dict[str, Any] | None = None,
    case_results: list[dict[str, Any]] | None = None,
    metadata: dict[str, Any] | None = None,
    recommendation_metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    safe_suite = _normalize_retrieval_benchmark_suite(suite)
    safe_query_count = max(0, int(query_count or 0))
    safe_passed = max(0, int(passed_count or 0))
    safe_failed = max(0, int(failed_count or 0))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO retrieval_benchmark_runs (
                    suite, label, compare_label, status, query_count,
                    passed_count, failed_count, metrics, case_results,
                    metadata, recommendation_metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s::jsonb, %s::jsonb, %s::jsonb, %s::jsonb)
                RETURNING id::text, created_at
                """,
                (
                    safe_suite,
                    _blank_to_none(label),
                    _blank_to_none(compare_label),
                    status,
                    safe_query_count,
                    safe_passed,
                    safe_failed,
                    _json(_sanitize_operational_metadata(metrics or {})),
                    _json_list(_sanitize_retrieval_case_results(case_results or [])),
                    _json(_sanitize_operational_metadata(metadata or {})),
                    _json(_sanitize_operational_metadata(recommendation_metadata or {})),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "suite": safe_suite,
                "label": _blank_to_none(label),
                "compare_label": _blank_to_none(compare_label),
                "status": status,
                "query_count": safe_query_count,
                "passed_count": safe_passed,
                "failed_count": safe_failed,
                "created_at": row[1].isoformat() if row[1] else None,
            }


def list_retrieval_benchmark_runs(
    *,
    suite: str | None = None,
    label: str | None = None,
    limit: int = 20,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    filters: list[str] = []
    params: list[Any] = []
    if suite:
        filters.append("suite = %s")
        params.append(_normalize_retrieval_benchmark_suite(suite))
    if label:
        filters.append("label = %s")
        params.append(label)
    where_clause = f"WHERE {' AND '.join(filters)}" if filters else ""
    params.append(max(1, min(limit, 200)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                WITH ordered AS (
                    SELECT current_run.id::text, current_run.suite, current_run.label,
                           current_run.compare_label, current_run.status,
                           current_run.query_count, current_run.passed_count,
                           current_run.failed_count, current_run.metrics,
                           current_run.case_results, current_run.metadata,
                           current_run.recommendation_metadata, current_run.created_at,
                           COALESCE(
                               label_baseline.metrics,
                               lead(current_run.metrics) OVER (
                                   PARTITION BY current_run.suite
                                   ORDER BY current_run.created_at DESC
                               )
                           ) AS previous_metrics
                    FROM retrieval_benchmark_runs current_run
                    LEFT JOIN LATERAL (
                        SELECT prior.metrics
                        FROM retrieval_benchmark_runs prior
                        WHERE current_run.compare_label IS NOT NULL
                          AND prior.suite = current_run.suite
                          AND prior.label = current_run.compare_label
                          AND prior.created_at < current_run.created_at
                        ORDER BY prior.created_at DESC
                        LIMIT 1
                    ) label_baseline ON TRUE
                )
                SELECT id, suite, label, compare_label, status, query_count,
                       passed_count, failed_count, metrics, case_results,
                       metadata, recommendation_metadata, created_at,
                       previous_metrics
                FROM ordered
                {where_clause}
                ORDER BY created_at DESC
                LIMIT %s
                """,
                params,
            )
            return [_retrieval_benchmark_run_row(row) for row in cur.fetchall()]


def record_memory_governance_run(
    *,
    mode: str = "shadow",
    trigger: str = "manual",
    status: str = "completed",
    policy_snapshot: dict[str, Any] | None = None,
    gate: dict[str, Any] | None = None,
    summary: dict[str, Any] | None = None,
    actor: str = "system",
    memory_mutated: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    safe_policy = _sanitize_governance_metadata(policy_snapshot or {})
    safe_gate = _sanitize_governance_metadata(gate or {})
    safe_summary = _sanitize_governance_metadata(summary or {})
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO memory_governance_runs (
                    mode, trigger, status, actor, policy_snapshot, gate,
                    summary, settings_mutated, memory_mutated
                )
                VALUES (%s, %s, %s, %s, %s::jsonb, %s::jsonb, %s::jsonb, false, %s)
                RETURNING id::text, created_at
                """,
                (
                    _normalize_governance_mode(mode),
                    str(trigger or "manual")[:80],
                    _normalize_governance_run_status(status),
                    str(actor or "system")[:80],
                    _json(safe_policy),
                    _json(safe_gate),
                    _json(safe_summary),
                    bool(memory_mutated),
                ),
            )
            row = cur.fetchone()
            run_id = row[0]
            cur.execute(
                """
                INSERT INTO memory_governance_policy_snapshots (
                    run_id, policy, settings_mutated
                )
                VALUES (%s, %s::jsonb, false)
                """,
                (run_id, _json(safe_policy)),
            )
            return {
                "id": run_id,
                "mode": _normalize_governance_mode(mode),
                "trigger": str(trigger or "manual")[:80],
                "status": _normalize_governance_run_status(status),
                "actor": str(actor or "system")[:80],
                "policy_snapshot": safe_policy,
                "gate": safe_gate,
                "summary": safe_summary,
                "settings_mutated": False,
                "memory_mutated": bool(memory_mutated),
                "created_at": row[1].isoformat() if row[1] else None,
            }


def list_memory_governance_runs(*, limit: int = 20, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, mode, trigger, status, actor, policy_snapshot,
                       gate, summary, settings_mutated, memory_mutated, created_at, updated_at
                FROM memory_governance_runs
                ORDER BY created_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 200)),),
            )
            return [_memory_governance_run_row(row) for row in cur.fetchall()]


def update_memory_governance_run(
    *,
    run_id: str,
    status: str | None = None,
    summary: dict[str, Any] | None = None,
    memory_mutated: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    safe_summary = _sanitize_governance_metadata(summary or {})
    normalized_status = _normalize_governance_run_status(status) if status else None
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE memory_governance_runs
                SET status = COALESCE(%s, status),
                    summary = CASE WHEN %s THEN summary ELSE %s::jsonb END,
                    memory_mutated = %s,
                    updated_at = now()
                WHERE id = %s
                RETURNING id::text, mode, trigger, status, actor, policy_snapshot,
                          gate, summary, settings_mutated, memory_mutated, created_at, updated_at
                """,
                (
                    normalized_status,
                    summary is None,
                    _json(safe_summary),
                    bool(memory_mutated),
                    run_id,
                ),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"governance run not found: {run_id}")
            return _memory_governance_run_row(row)


def record_memory_governance_action(
    *,
    run_id: str | None,
    action: str,
    target_type: str,
    target_id: str,
    memory_class: str | None = None,
    risk: str = "medium",
    status: str = "proposed",
    source: str = "governance",
    rationale: dict[str, Any] | None = None,
    evidence: dict[str, Any] | None = None,
    before_state: dict[str, Any] | None = None,
    after_state: dict[str, Any] | None = None,
    actor: str = "system",
    memory_mutated: bool = False,
    audit_event_id: str | None = None,
    error: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    safe_rationale = _sanitize_governance_metadata(rationale or {})
    safe_evidence = _sanitize_governance_metadata(evidence or {})
    safe_before = _sanitize_governance_metadata(before_state or {})
    safe_after = _sanitize_governance_metadata(after_state or {})
    normalized_status = _normalize_governance_action_status(status)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO memory_governance_actions (
                    run_id, action, target_type, target_id, memory_class, risk,
                    status, source, actor, rationale, evidence, before_state,
                    after_state, settings_mutated, memory_mutated, audit_event_id, error
                )
                VALUES (
                    %s, %s, %s, %s, %s, %s, %s, %s, %s,
                    %s::jsonb, %s::jsonb, %s::jsonb, %s::jsonb,
                    false, %s, %s, %s
                )
                RETURNING id::text, created_at
                """,
                (
                    run_id,
                    _normalize_governance_action(action),
                    str(target_type or "memory")[:80],
                    str(target_id or "")[:160],
                    _normalize_governance_memory_class(memory_class),
                    _normalize_governance_risk(risk),
                    normalized_status,
                    str(source or "governance")[:80],
                    str(actor or "system")[:80],
                    _json(safe_rationale),
                    _json(safe_evidence),
                    _json(safe_before),
                    _json(safe_after),
                    bool(memory_mutated),
                    audit_event_id,
                    str(error)[:300] if error else None,
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "run_id": run_id,
                "action": _normalize_governance_action(action),
                "target_type": str(target_type or "memory")[:80],
                "target_id": str(target_id or "")[:160],
                "memory_class": _normalize_governance_memory_class(memory_class),
                "risk": _normalize_governance_risk(risk),
                "status": normalized_status,
                "source": str(source or "governance")[:80],
                "actor": str(actor or "system")[:80],
                "rationale": safe_rationale,
                "evidence": safe_evidence,
                "before_state": safe_before,
                "after_state": safe_after,
                "settings_mutated": False,
                "memory_mutated": bool(memory_mutated),
                "audit_event_id": audit_event_id,
                "error": str(error)[:300] if error else None,
                "created_at": row[1].isoformat() if row[1] else None,
            }


def list_memory_governance_actions(
    *,
    status: str | None = None,
    limit: int = 50,
    url: str | None = None,
) -> list[dict[str, Any]]:
    filters: list[str] = []
    params: list[Any] = []
    if status and status != "all":
        filters.append("status = %s")
        params.append(_normalize_governance_action_status(status))
    where_clause = f"WHERE {' AND '.join(filters)}" if filters else ""
    params.append(max(1, min(limit, 200)))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, run_id::text, action, target_type, target_id,
                       memory_class, risk, status, source, actor, rationale,
                       evidence, before_state, after_state, settings_mutated,
                       memory_mutated, audit_event_id::text, error, created_at,
                       updated_at, applied_at, recovered_at
                FROM memory_governance_actions
                {where_clause}
                ORDER BY created_at DESC
                LIMIT %s
                """,
                params,
            )
            return [_memory_governance_action_row(row) for row in cur.fetchall()]


def get_memory_governance_action(action_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, run_id::text, action, target_type, target_id,
                       memory_class, risk, status, source, actor, rationale,
                       evidence, before_state, after_state, settings_mutated,
                       memory_mutated, audit_event_id::text, error, created_at,
                       updated_at, applied_at, recovered_at
                FROM memory_governance_actions
                WHERE id = %s
                """,
                (action_id,),
            )
            row = cur.fetchone()
            return _memory_governance_action_row(row) if row else None


def update_memory_governance_action(
    *,
    action_id: str,
    status: str,
    after_state: dict[str, Any] | None = None,
    memory_mutated: bool = False,
    audit_event_id: str | None = None,
    error: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    normalized_status = _normalize_governance_action_status(status)
    safe_after = _sanitize_governance_metadata(after_state or {})
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE memory_governance_actions
                SET status = %s,
                    after_state = memory_governance_actions.after_state || %s::jsonb,
                    memory_mutated = %s,
                    audit_event_id = COALESCE(%s::uuid, audit_event_id),
                    error = %s,
                    applied_at = CASE WHEN %s = 'applied' THEN now() ELSE applied_at END,
                    recovered_at = CASE WHEN %s = 'recovered' THEN now() ELSE recovered_at END,
                    updated_at = now()
                WHERE id = %s
                RETURNING id::text, run_id::text, action, target_type, target_id,
                          memory_class, risk, status, source, actor, rationale,
                          evidence, before_state, after_state, settings_mutated,
                          memory_mutated, audit_event_id::text, error, created_at,
                          updated_at, applied_at, recovered_at
                """,
                (
                    normalized_status,
                    _json(safe_after),
                    bool(memory_mutated),
                    audit_event_id,
                    str(error)[:300] if error else None,
                    normalized_status,
                    normalized_status,
                    action_id,
                ),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"governance action not found: {action_id}")
            return _memory_governance_action_row(row)


def record_operator_automation_run(
    *,
    mode: str = "guarded",
    trigger: str = "manual",
    status: str = "running",
    policy_snapshot: dict[str, Any] | None = None,
    summary: dict[str, Any] | None = None,
    actor: str = "system",
    memory_mutated: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    safe_policy = _sanitize_governance_metadata(policy_snapshot or {})
    safe_summary = _sanitize_governance_metadata(summary or {})
    normalized_mode = _normalize_operator_automation_mode(mode)
    normalized_status = _normalize_operator_automation_run_status(status)
    clean_actor = str(actor or "system")[:80]
    clean_trigger = str(trigger or "manual")[:80]
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO operator_automation_runs (
                    mode, trigger, status, actor, policy_snapshot, summary,
                    settings_mutated, memory_mutated
                )
                VALUES (%s, %s, %s, %s, %s::jsonb, %s::jsonb, false, %s)
                RETURNING id::text, started_at
                """,
                (
                    normalized_mode,
                    clean_trigger,
                    normalized_status,
                    clean_actor,
                    _json(safe_policy),
                    _json(safe_summary),
                    bool(memory_mutated),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "mode": normalized_mode,
                "trigger": clean_trigger,
                "status": normalized_status,
                "actor": clean_actor,
                "policy_snapshot": safe_policy,
                "summary": safe_summary,
                "settings_mutated": False,
                "memory_mutated": bool(memory_mutated),
                "started_at": row[1].isoformat() if row[1] else None,
                "completed_at": None,
                "created_at": row[1].isoformat() if row[1] else None,
            }


def update_operator_automation_run(
    *,
    run_id: str,
    status: str | None = None,
    summary: dict[str, Any] | None = None,
    memory_mutated: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    safe_summary = _sanitize_governance_metadata(summary or {})
    normalized_status = _normalize_operator_automation_run_status(status) if status else None
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE operator_automation_runs
                SET status = COALESCE(%s, status),
                    summary = CASE WHEN %s THEN summary ELSE %s::jsonb END,
                    memory_mutated = %s,
                    completed_at = now(),
                    updated_at = now()
                WHERE id = %s
                RETURNING id::text, mode, trigger, status, actor, policy_snapshot,
                          summary, settings_mutated, memory_mutated, started_at,
                          completed_at, created_at, updated_at
                """,
                (
                    normalized_status,
                    summary is None,
                    _json(safe_summary),
                    bool(memory_mutated),
                    run_id,
                ),
            )
            row = cur.fetchone()
            if row is None:
                raise LookupError(f"operator automation run not found: {run_id}")
            return _operator_automation_run_row(row)


def list_operator_automation_runs(*, limit: int = 20, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, mode, trigger, status, actor, policy_snapshot,
                       summary, settings_mutated, memory_mutated, started_at,
                       completed_at, created_at, updated_at
                FROM operator_automation_runs
                ORDER BY created_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 200)),),
            )
            return [_operator_automation_run_row(row) for row in cur.fetchall()]


def record_operator_automation_action(
    *,
    run_id: str | None,
    action: str,
    target_type: str | None = None,
    target_id: str | None = None,
    risk: str = "low",
    status: str = "proposed",
    source: str = "automation",
    rationale: dict[str, Any] | None = None,
    evidence: dict[str, Any] | None = None,
    result: dict[str, Any] | None = None,
    actor: str = "system",
    memory_mutated: bool = False,
    audit_event_id: str | None = None,
    error: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    normalized_action = _normalize_operator_automation_action(action)
    normalized_status = _normalize_operator_automation_action_status(status)
    safe_rationale = _sanitize_governance_metadata(rationale or {})
    safe_evidence = _sanitize_governance_metadata(evidence or {})
    safe_result = _sanitize_governance_metadata(result or {})
    clean_actor = str(actor or "system")[:80]
    clean_source = str(source or "automation")[:80]
    clean_target_type = str(target_type or "")[:80] or None
    clean_target_id = str(target_id or "")[:160] or None
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO operator_automation_actions (
                    run_id, action, target_type, target_id, risk, status, source,
                    actor, rationale, evidence, result, settings_mutated,
                    memory_mutated, audit_event_id, error
                )
                VALUES (
                    %s, %s, %s, %s, %s, %s, %s, %s,
                    %s::jsonb, %s::jsonb, %s::jsonb, false, %s, %s, %s
                )
                RETURNING id::text, created_at
                """,
                (
                    run_id,
                    normalized_action,
                    clean_target_type,
                    clean_target_id,
                    _normalize_governance_risk(risk),
                    normalized_status,
                    clean_source,
                    clean_actor,
                    _json(safe_rationale),
                    _json(safe_evidence),
                    _json(safe_result),
                    bool(memory_mutated),
                    audit_event_id,
                    str(error)[:300] if error else None,
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "run_id": run_id,
                "action": normalized_action,
                "target_type": clean_target_type,
                "target_id": clean_target_id,
                "risk": _normalize_governance_risk(risk),
                "status": normalized_status,
                "source": clean_source,
                "actor": clean_actor,
                "rationale": safe_rationale,
                "evidence": safe_evidence,
                "result": safe_result,
                "settings_mutated": False,
                "memory_mutated": bool(memory_mutated),
                "audit_event_id": audit_event_id,
                "error": str(error)[:300] if error else None,
                "created_at": row[1].isoformat() if row[1] else None,
                "updated_at": row[1].isoformat() if row[1] else None,
            }


def list_operator_automation_actions(
    *,
    status: str | None = None,
    run_id: str | None = None,
    action: str | None = None,
    limit: int = 50,
    url: str | None = None,
) -> list[dict[str, Any]]:
    filters: list[str] = []
    params: list[Any] = []
    if status and status != "all":
        filters.append("status = %s")
        params.append(_normalize_operator_automation_action_status(status))
    if run_id:
        filters.append("run_id = %s")
        params.append(run_id)
    if action:
        filters.append("action = %s")
        params.append(_normalize_operator_automation_action(action))
    where_clause = f"WHERE {' AND '.join(filters)}" if filters else ""
    params.append(max(1, min(limit, 200)))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, run_id::text, action, target_type, target_id,
                       risk, status, source, actor, rationale, evidence, result,
                       settings_mutated, memory_mutated, audit_event_id::text,
                       error, created_at, updated_at
                FROM operator_automation_actions
                {where_clause}
                ORDER BY created_at DESC
                LIMIT %s
                """,
                params,
            )
            return [_operator_automation_action_row(row) for row in cur.fetchall()]


def record_memory_governance_digest(
    *,
    run_id: str | None,
    summary: dict[str, Any] | None = None,
    recommendations: list[dict[str, Any]] | None = None,
    actor: str = "system",
    memory_mutated: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    safe_summary = _sanitize_governance_metadata(summary or {})
    safe_recommendations = [_sanitize_governance_metadata(item) for item in (recommendations or [])[:20] if isinstance(item, dict)]
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO memory_governance_digests (
                    run_id, actor, summary, recommendations, settings_mutated, memory_mutated
                )
                VALUES (%s, %s, %s::jsonb, %s::jsonb, false, %s)
                RETURNING id::text, created_at
                """,
                (
                    run_id,
                    str(actor or "system")[:80],
                    _json(safe_summary),
                    _json_any(safe_recommendations),
                    bool(memory_mutated),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "run_id": run_id,
                "actor": str(actor or "system")[:80],
                "summary": safe_summary,
                "recommendations": safe_recommendations,
                "settings_mutated": False,
                "memory_mutated": bool(memory_mutated),
                "created_at": row[1].isoformat() if row[1] else None,
            }


def latest_memory_governance_digest(*, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, run_id::text, actor, summary, recommendations,
                       settings_mutated, memory_mutated, created_at
                FROM memory_governance_digests
                ORDER BY created_at DESC
                LIMIT 1
                """
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "id": row[0],
                "run_id": row[1],
                "actor": row[2],
                "summary": _sanitize_governance_metadata(row[3] or {}),
                "recommendations": [_sanitize_governance_metadata(item) for item in row[4] or [] if isinstance(item, dict)],
                "settings_mutated": bool(row[5]),
                "memory_mutated": bool(row[6]),
                "created_at": row[7].isoformat() if row[7] else None,
            }


CODE_FEEDBACK_CATEGORIES = {
    "missing_symbol",
    "wrong_root",
    "wrong_relationship",
    "parser_fallback",
    "ranking_order",
    "stale_generated",
    "other",
}


def record_code_feedback_event(
    *,
    query: str,
    root_name: str | None = None,
    result_count: int = 0,
    surface: str = "unknown",
    miss_category: str = "other",
    expected_symbol: str | None = None,
    path: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    safe_category = _normalize_code_feedback_category(miss_category)
    safe_root = _blank_to_none(root_name)
    scope = f"sha256:{_stable_digest(safe_root or 'global')}"
    query_hash = f"sha256:{_stable_digest(query)}"
    expected_hash = f"sha256:{_stable_digest(expected_symbol)}" if expected_symbol else None
    path_leaf = _safe_path_leaf(path or "") if path else None
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO code_retrieval_feedback_events (
                    root_name, scope_hash, query_hash, result_count, surface,
                    miss_category, expected_symbol_hash, path_leaf, metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb)
                RETURNING id::text, created_at
                """,
                (
                    safe_root,
                    scope,
                    query_hash,
                    max(0, int(result_count or 0)),
                    str(surface or "unknown")[:64],
                    safe_category,
                    expected_hash,
                    path_leaf,
                    _json(_sanitize_operational_metadata(metadata or {})),
                ),
            )
            row = cur.fetchone()
            return {
                "id": row[0],
                "root_name": safe_root,
                "scope_hash": scope,
                "query_hash": query_hash,
                "result_count": max(0, int(result_count or 0)),
                "surface": str(surface or "unknown")[:64],
                "miss_category": safe_category,
                "expected_symbol_hash": expected_hash,
                "path_leaf": path_leaf,
                "settings_mutated": False,
                "created_at": row[1].isoformat() if row[1] else None,
            }


def code_feedback_summary(
    *,
    root_name: str | None = None,
    limit: int = 20,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    filters: list[str] = []
    params: list[Any] = []
    if root_name:
        filters.append("root_name = %s")
        params.append(root_name)
    where_clause = f"WHERE {' AND '.join(filters)}" if filters else ""
    params.append(max(1, min(int(limit or 20), 100)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT miss_category, root_name, count(*)::integer AS event_count,
                       COALESCE(min(result_count), 0)::integer AS min_result_count,
                       COALESCE(max(result_count), 0)::integer AS max_result_count,
                       max(created_at) AS latest_at
                FROM code_retrieval_feedback_events
                {where_clause}
                GROUP BY miss_category, root_name
                ORDER BY event_count DESC, latest_at DESC
                LIMIT %s
                """,
                params,
            )
            rows = [
                {
                    "miss_category": row[0],
                    "root_name": row[1],
                    "event_count": int(row[2] or 0),
                    "min_result_count": int(row[3] or 0),
                    "max_result_count": int(row[4] or 0),
                    "latest_at": row[5].isoformat() if row[5] else None,
                }
                for row in cur.fetchall()
            ]
    return {
        "settings_mutated": False,
        "root_name": root_name,
        "totals": {"event_count": sum(int(row["event_count"]) for row in rows), "category_count": len(rows)},
        "rows": rows,
    }


def upsert_scan_manifest(
    *,
    root_name: str,
    path: str,
    size_bytes: int,
    mtime_ns: int,
    quick_hash: str | None,
    content_hash: str | None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            root_id = _root_id_for_name(cur, root_name)
            cur.execute(
                """
                INSERT INTO crawl_path_manifests (
                    root_id, path, size_bytes, mtime_ns, quick_hash, content_hash, metadata, updated_at
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s::jsonb, now())
                ON CONFLICT (root_id, path) DO UPDATE SET
                    size_bytes = EXCLUDED.size_bytes,
                    mtime_ns = EXCLUDED.mtime_ns,
                    quick_hash = EXCLUDED.quick_hash,
                    content_hash = EXCLUDED.content_hash,
                    metadata = crawl_path_manifests.metadata || EXCLUDED.metadata,
                    updated_at = now()
                RETURNING path
                """,
                (
                    root_id,
                    path,
                    max(0, int(size_bytes)),
                    max(0, int(mtime_ns)),
                    quick_hash,
                    content_hash,
                    _json(_sanitize_operational_metadata(metadata or {})),
                ),
            )
            row = cur.fetchone()
            return {"root_name": root_name, "path": row[0]}


def lookup_scan_manifest(*, root_name: str, path: str, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            root_id = _root_id_for_name(cur, root_name)
            cur.execute(
                """
                SELECT m.path, m.size_bytes, m.mtime_ns, m.quick_hash, m.content_hash,
                       m.metadata, a.extraction_status, (a.deleted_at IS NOT NULL) AS source_asset_deleted,
                       count(c.id)::integer AS chunk_count
                FROM crawl_path_manifests m
                LEFT JOIN source_assets a
                  ON a.root_id = m.root_id
                 AND a.path = m.path
                LEFT JOIN asset_chunks c ON c.asset_id = a.id
                WHERE m.root_id = %s
                  AND m.path = %s
                GROUP BY m.path, m.size_bytes, m.mtime_ns, m.quick_hash, m.content_hash,
                         m.metadata, a.extraction_status, a.deleted_at
                """,
                (root_id, path),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return _scan_manifest_row(row)


def load_scan_manifest(*, root_name: str, url: str | None = None) -> dict[str, dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            root_id = _root_id_for_name(cur, root_name)
            cur.execute(
                """
                SELECT m.path, m.size_bytes, m.mtime_ns, m.quick_hash, m.content_hash,
                       m.metadata, a.extraction_status, (a.deleted_at IS NOT NULL) AS source_asset_deleted,
                       count(c.id)::integer AS chunk_count
                FROM crawl_path_manifests m
                LEFT JOIN source_assets a
                  ON a.root_id = m.root_id
                 AND a.path = m.path
                LEFT JOIN asset_chunks c ON c.asset_id = a.id
                WHERE m.root_id = %s
                GROUP BY m.path, m.size_bytes, m.mtime_ns, m.quick_hash, m.content_hash,
                         m.metadata, a.extraction_status, a.deleted_at
                """,
                (root_id,),
            )
            return {str(row[0]): _scan_manifest_row(row) for row in cur.fetchall()}


def _scan_manifest_row(row: Any) -> dict[str, Any]:
    return {
        "path": row[0],
        "size_bytes": row[1],
        "mtime_ns": row[2],
        "quick_hash": row[3],
        "content_hash": row[4],
        "metadata": _sanitize_operational_metadata(row[5] or {}),
        "source_asset_status": row[6],
        "source_asset_deleted": bool(row[7]),
        "chunk_count": int(row[8] or 0),
    }


def apply_extraction_result(
    *, root_name: str, relative_path: str, result: Any, url: str | None = None
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            _apply_extraction_result_with_cursor(
                cur,
                root_name=root_name,
                relative_path=relative_path,
                result=result,
            )


def corpus_job_is_running(job_id: str, *, url: str | None = None) -> bool:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT 1
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                """,
                (job_id,),
            )
            return cur.fetchone() is not None


def apply_extraction_result_for_job(
    *,
    job_id: str,
    root_name: str,
    relative_path: str,
    result: Any,
    url: str | None = None,
) -> bool:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                FOR UPDATE
                """,
                (job_id,),
            )
            if cur.fetchone() is None:
                return False
            try:
                _apply_extraction_result_with_cursor(
                    cur,
                    root_name=root_name,
                    relative_path=relative_path,
                    result=result,
                )
            except ValueError:
                return False
            return True


def apply_staged_extraction_plan_for_job(
    *,
    job_id: str,
    root_name: str,
    relative_path: str,
    result: Any,
    url: str | None = None,
) -> bool:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                FOR UPDATE
                """,
                (job_id,),
            )
            if cur.fetchone() is None:
                return False
            try:
                asset_id, canonical_asset_id = _staged_asset_for_update(
                    cur,
                    root_name=root_name,
                    relative_path=relative_path,
                )
            except ValueError:
                return False
            status = "duplicate_suppressed" if canonical_asset_id else "processing_staged"
            metadata_json = _json(result.metadata or {})
            cur.execute(
                """
                UPDATE source_assets
                SET extraction_status = %s,
                    metadata = metadata || %s::jsonb,
                    updated_at = now()
                WHERE id = %s
                  AND deleted_at IS NULL
                RETURNING id::text
                """,
                (status, metadata_json, asset_id),
            )
            if cur.fetchone() is None:
                return False
            if not canonical_asset_id:
                _replace_asset_chunks(cur, asset_id, ())
                _append_or_upsert_asset_chunks(cur, asset_id, tuple(getattr(result, "chunks", ()) or ()))
                _enqueue_staged_jobs_with_cursor(
                    cur,
                    root_name=root_name,
                    relative_path=relative_path,
                    jobs=_staged_jobs_from_result(result, plan=True),
                )
            return True


def apply_staged_extraction_piece_for_job(
    *,
    job_id: str,
    root_name: str,
    relative_path: str,
    result: Any,
    url: str | None = None,
) -> bool:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status
                FROM capture_jobs
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                FOR UPDATE
                """,
                (job_id,),
            )
            if cur.fetchone() is None:
                return False
            try:
                asset_id, canonical_asset_id = _staged_asset_for_update(
                    cur,
                    root_name=root_name,
                    relative_path=relative_path,
                )
            except ValueError:
                return False
            result_status = str(getattr(result, "status", "") or "metadata_only")
            status = "processing_staged" if result_status == "staged" else result_status
            if canonical_asset_id and status == "indexed":
                status = "duplicate_suppressed"
            metadata_json = _json(getattr(result, "metadata", {}) or {})
            cur.execute(
                """
                UPDATE source_assets
                SET extraction_status = %s,
                    metadata = CASE
                        WHEN %s::jsonb ? 'strict_indexing'
                             AND COALESCE(%s::jsonb->>'readiness_status', '') NOT LIKE 'blocked_%%'
                        THEN (metadata - 'metadata_only_blocked' - 'readiness_reason') || %s::jsonb
                        ELSE metadata || %s::jsonb
                    END,
                    indexed_at = CASE WHEN %s = 'indexed' THEN now() ELSE indexed_at END,
                    updated_at = now()
                WHERE id = %s
                  AND deleted_at IS NULL
                RETURNING id::text
                """,
                (status, metadata_json, metadata_json, metadata_json, metadata_json, status, asset_id),
            )
            if cur.fetchone() is None:
                return False
            if not canonical_asset_id:
                _append_or_upsert_asset_chunks(cur, asset_id, tuple(getattr(result, "chunks", ()) or ()))
                _enqueue_staged_jobs_with_cursor(
                    cur,
                    root_name=root_name,
                    relative_path=relative_path,
                    jobs=_staged_jobs_from_result(result, plan=False),
                )
            return True


def _staged_asset_for_update(cur: Any, *, root_name: str, relative_path: str) -> tuple[str, str | None]:
    cur.execute(
        """
        SELECT a.id::text, a.canonical_asset_id::text
        FROM source_assets a
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE r.name = %s
          AND a.path = %s
          AND a.deleted_at IS NULL
        FOR UPDATE
        """,
        (root_name, relative_path),
    )
    row = cur.fetchone()
    if not row:
        raise ValueError(f"source asset not found: {root_name}:{relative_path}")
    return row[0], row[1]


def _staged_jobs_from_result(result: Any, *, plan: bool) -> list[dict[str, Any]]:
    metadata = getattr(result, "metadata", None)
    if not isinstance(metadata, dict):
        return []
    jobs: list[dict[str, Any]] = []
    if plan and isinstance(metadata.get("staged_jobs"), list):
        raw_jobs = metadata.get("staged_jobs") or []
        jobs.extend(item for item in raw_jobs if isinstance(item, dict))
    staged = metadata.get("staged_extraction")
    if isinstance(staged, dict) and isinstance(staged.get("next_job"), dict):
        jobs.append(staged["next_job"])
    return jobs


def _enqueue_staged_jobs_with_cursor(
    cur: Any,
    *,
    root_name: str,
    relative_path: str,
    jobs: list[dict[str, Any]],
) -> list[str]:
    job_ids: list[str] = []
    for job in jobs:
        job_type = str(job.get("job_type") or "").strip()
        if not job_type:
            continue
        payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
        job_payload = {
            **payload,
            "root_name": root_name,
            "path": relative_path,
        }
        schedule = _job_schedule_metadata(job_type)
        queued = _enqueue_unique_capture_job_with_cursor(
            cur,
            job_type=job_type,
            payload=job_payload,
            job_family=schedule["job_family"],
            resource_class=schedule["resource_class"],
            priority=schedule["priority"],
            time_budget_seconds=schedule["time_budget_seconds"],
            telemetry={"stage": "queued", "root_name": root_name, "path": relative_path, "staged": True},
            audit_details={"job_type": job_type, "root_name": root_name, "path": relative_path, "staged": True},
        )
        if queued:
            job_ids.append(queued["job_id"])
    return job_ids


def _apply_extraction_result_with_cursor(
    cur: Any,
    *,
    root_name: str,
    relative_path: str,
    result: Any,
) -> None:
    cur.execute(
        """
        SELECT a.id::text, a.canonical_asset_id::text, a.root_id::text, a.uri, a.mtime_ns
        FROM source_assets a
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE r.name = %s
          AND a.path = %s
          AND a.deleted_at IS NULL
        """,
        (root_name, relative_path),
    )
    row = cur.fetchone()
    if not row:
        raise ValueError(f"source asset not found: {root_name}:{relative_path}")
    asset_id, canonical_asset_id, root_id, parent_uri, parent_mtime_ns = row
    status = result.status
    if canonical_asset_id and status == "indexed":
        status = "duplicate_suppressed"
    metadata_json = _json(result.metadata or {})
    cur.execute(
        """
        UPDATE source_assets
        SET extraction_status = %s,
            metadata = CASE
                WHEN %s::jsonb ? 'strict_indexing'
                     AND COALESCE(%s::jsonb->>'readiness_status', '') NOT LIKE 'blocked_%%'
                THEN (metadata - 'metadata_only_blocked' - 'readiness_reason') || %s::jsonb
                ELSE metadata || %s::jsonb
            END,
            indexed_at = CASE WHEN %s = 'indexed' THEN now() ELSE indexed_at END,
            updated_at = now()
        WHERE id = %s
          AND deleted_at IS NULL
        RETURNING id::text
        """,
        (status, metadata_json, metadata_json, metadata_json, metadata_json, status, asset_id),
    )
    if cur.fetchone() is None:
        raise ValueError(f"source asset not active: {root_name}:{relative_path}")
    _replace_asset_chunks(cur, asset_id, () if canonical_asset_id else result.chunks)
    child_assets = tuple(getattr(result, "child_assets", ()) or ())
    if not canonical_asset_id and (child_assets or (result.metadata or {}).get("extractor") == "container"):
        _replace_container_child_assets(
            cur,
            root_id=root_id,
            parent_asset_id=asset_id,
            parent_relative_path=relative_path,
            parent_uri=parent_uri,
            parent_mtime_ns=int(parent_mtime_ns or 0),
            child_assets=child_assets,
        )


def retrieval_stats(*, url: str | None = None) -> dict[str, int]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            counts: dict[str, int] = {}
            for key, table in {
                "episodes": "episodes",
                "sources": "sources",
                "source_assets": "source_assets",
                "asset_chunks": "asset_chunks",
                "search_index_records": "search_index_records",
            }.items():
                cur.execute(f"SELECT count(*) FROM {table}")
                counts[key] = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM source_assets WHERE canonical_asset_id IS NOT NULL")
            counts["duplicate_assets"] = cur.fetchone()[0]
            return counts


def list_source_assets(
    *,
    root_name: str | None = None,
    path: str | None = None,
    limit: int = 50,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    filters = ["a.deleted_at IS NULL"]
    params: list[Any] = []
    if root_name:
        filters.append("r.name = %s")
        params.append(root_name)
    if path:
        filters.append("a.path ILIKE %s")
        params.append(f"%{path}%")
    params.append(max(1, min(limit, 200)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT a.id::text, r.name, a.path, a.uri, a.file_kind, a.mime_type,
                       a.extension, a.size_bytes, a.extraction_status,
                       a.canonical_asset_id::text,
                       (
                           SELECT count(*)
                           FROM source_assets duplicate
                           WHERE duplicate.canonical_asset_id = a.id
                             AND duplicate.deleted_at IS NULL
                       ) AS duplicate_count,
                       a.last_seen_at, a.indexed_at, a.metadata
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE {" AND ".join(filters)}
                ORDER BY a.updated_at DESC
                LIMIT %s
                """,
                tuple(params),
            )
            return [_source_asset_lookup_row(row) for row in cur.fetchall()]


def get_source_asset(asset_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT a.id::text, r.name, a.path, a.uri, a.file_kind, a.mime_type,
                       a.extension, a.size_bytes, a.extraction_status,
                       a.canonical_asset_id::text,
                       (
                           SELECT count(*)
                           FROM source_assets duplicate
                           WHERE duplicate.canonical_asset_id = a.id
                             AND duplicate.deleted_at IS NULL
                       ) AS duplicate_count,
                       a.last_seen_at, a.indexed_at, a.metadata
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE a.id = %s
                """,
                (asset_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            asset = _source_asset_lookup_row(row)
            cur.execute(
                """
                SELECT id::text, chunk_index, title, modality, locator, token_estimate
                FROM asset_chunks
                WHERE asset_id = %s
                ORDER BY chunk_index
                LIMIT 100
                """,
                (asset_id,),
            )
            asset["chunks"] = [
                {
                    "id": chunk[0],
                    "chunk_index": chunk[1],
                    "title": chunk[2],
                    "modality": chunk[3],
                    "locator": chunk[4],
                    "token_estimate": chunk[5],
                }
                for chunk in cur.fetchall()
            ]
            return asset


def get_asset_chunk(chunk_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT c.id::text, c.asset_id::text, c.chunk_index, c.title, c.body,
                       c.modality, c.locator, c.token_estimate, c.metadata,
                       a.path, r.name
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE c.id = %s
                """,
                (chunk_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "id": row[0],
                "asset_id": row[1],
                "chunk_index": row[2],
                "title": row[3],
                "body": row[4],
                "modality": row[5],
                "locator": row[6],
                "token_estimate": row[7],
                "metadata": row[8] or {},
                "asset_path": row[9],
                "root_name": row[10],
            }


def get_asset_chunk_detail(chunk_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT c.id::text, c.asset_id::text, c.chunk_index, c.title, c.body,
                       c.modality, c.locator, c.token_estimate, c.metadata,
                       a.path, a.extraction_status, a.deleted_at, r.name, r.root_path
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE c.id = %s
                """,
                (chunk_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "id": row[0],
                "asset_id": row[1],
                "chunk_index": row[2],
                "title": row[3],
                "body": row[4],
                "modality": row[5],
                "locator": row[6],
                "token_estimate": row[7],
                "metadata": row[8] or {},
                "asset_path": row[9],
                "asset_status": "deleted" if row[11] else row[10],
                "deleted_at": row[11].isoformat() if row[11] else None,
                "root_name": row[12],
                "root_path": row[13],
            }


def get_source_asset_detail(asset_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT a.id::text, r.name, r.root_path, a.path, a.uri, a.file_kind,
                       a.mime_type, a.extension, a.size_bytes, a.extraction_status,
                       a.canonical_asset_id::text,
                       (
                           SELECT count(*)
                           FROM source_assets duplicate
                           WHERE duplicate.canonical_asset_id = a.id
                             AND duplicate.deleted_at IS NULL
                       ) AS duplicate_count,
                       a.last_seen_at, a.indexed_at, a.deleted_at, a.metadata, r.metadata
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE a.id = %s
                """,
                (asset_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            asset = _source_asset_detail_row(row)
            asset["chunks"] = _asset_chunk_details(cur, asset_id)
            return asset


def get_source_asset_for_file_action(asset_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT a.id::text, r.root_path, a.path, a.extraction_status,
                       a.deleted_at, a.metadata, r.metadata
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE a.id = %s
                """,
                (asset_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "id": row[0],
                "root_path": row[1],
                "path": row[2],
                "status": "deleted" if row[4] else row[3],
                "deleted_at": row[4].isoformat() if row[4] else None,
                "metadata": row[5] or {},
                "root_metadata": row[6] or {},
            }


def get_capture_job_for_file_action(job_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT job.id::text, job.job_type, job.status, job.payload,
                       root.name, root.root_path
                FROM capture_jobs job
                LEFT JOIN monitored_roots root
                  ON root.name = job.payload->>'root_name'
                WHERE job.id = %s
                  AND job.job_type LIKE 'corpus_%%'
                """,
                (job_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            payload = row[3] or {}
            relative_path = str(payload.get("path") or "").strip() if isinstance(payload, dict) else ""
            if not relative_path or not row[5]:
                return None
            return {
                "id": row[0],
                "job_type": row[1],
                "status": row[2],
                "payload": payload,
                "root_name": row[4],
                "root_path": row[5],
                "path": relative_path,
            }


def list_related_source_assets(asset_id: str, *, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT a.id::text, r.name, r.root_path, a.path, a.uri, a.file_kind,
                       a.mime_type, a.extension, a.size_bytes, a.extraction_status,
                       a.canonical_asset_id::text, 0 AS duplicate_count,
                       a.last_seen_at, a.indexed_at, a.deleted_at, a.metadata, r.metadata
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE a.id <> %s
                  AND (
                      a.metadata->>'parent_asset_id' = %s
                      OR a.metadata->>'container_asset_id' = %s
                      OR a.metadata->>'logical_parent_asset_id' = %s
                  )
                ORDER BY a.path
                LIMIT 100
                """,
                (asset_id, asset_id, asset_id, asset_id),
            )
            return [_source_asset_detail_row(row) for row in cur.fetchall()]


def list_mail_export_assets(export_id: str, *, root_name: str | None = None, url: str | None = None) -> list[dict[str, Any]]:
    assets = list_source_assets(root_name=root_name, path=f"{export_id}/", limit=200, url=url)
    related = [
        asset
        for asset in assets
        if str(asset.get("path") or "").replace("\\", "/").startswith(f"{export_id}/")
    ]
    return [
        detail
        for asset in related
        if (detail := get_source_asset_detail(asset["id"], url=url)) is not None
    ]


def get_mail_message(mail_message_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT m.id::text, p.name, p.source_type, m.source_message_id,
                       m.source_folder, m.internet_message_id, m.content_hash,
                       m.export_id, m.export_state, m.error, m.received_at,
                       m.exported_at, m.metadata
                FROM mail_messages m
                JOIN mail_profiles p ON p.id = m.profile_id
                WHERE m.id = %s
                """,
                (mail_message_id,),
            )
            row = cur.fetchone()
            return _mail_message_detail_row(row) if row else None


def get_mail_message_by_export_id(
    export_id: str,
    *,
    profile_name: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    filters = ["m.export_id = %s"]
    params: list[Any] = [export_id]
    if profile_name:
        filters.append("p.name = %s")
        params.append(profile_name)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT m.id::text, p.name, p.source_type, m.source_message_id,
                       m.source_folder, m.internet_message_id, m.content_hash,
                       m.export_id, m.export_state, m.error, m.received_at,
                       m.exported_at, m.metadata
                FROM mail_messages m
                JOIN mail_profiles p ON p.id = m.profile_id
                WHERE {" AND ".join(filters)}
                ORDER BY m.updated_at DESC
                LIMIT 1
                """,
                tuple(params),
            )
            row = cur.fetchone()
            return _mail_message_detail_row(row) if row else None


def get_episode_detail(episode_id: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, title, summary, source_kind, metadata, created_at, updated_at
                FROM episodes
                WHERE id = %s
                """,
                (episode_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "id": row[0],
                "title": row[1],
                "summary": row[2],
                "source_kind": row[3],
                "metadata": row[4] or {},
                "created_at": row[5].isoformat() if row[5] else None,
                "updated_at": row[6].isoformat() if row[6] else None,
            }


def get_runtime_setting(key: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT value, updated_at
                FROM runtime_settings
                WHERE key = %s
                """,
                (key,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {"key": key, "value": row[0], "updated_at": row[1].isoformat()}


def set_runtime_setting(
    *,
    key: str,
    value: Any,
    actor: str = "system",
    reason: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT value FROM runtime_settings WHERE key = %s", (key,))
            old_row = cur.fetchone()
            old_value = old_row[0] if old_row else None
            cur.execute(
                """
                INSERT INTO runtime_settings (key, value, updated_by, reason)
                VALUES (%s, %s::jsonb, %s, %s)
                ON CONFLICT (key) DO UPDATE SET
                    value = EXCLUDED.value,
                    updated_by = EXCLUDED.updated_by,
                    reason = EXCLUDED.reason,
                    updated_at = now()
                RETURNING key, value, updated_by, reason, updated_at
                """,
                (key, _json_any(value), actor, reason),
            )
            row = cur.fetchone()
            cur.execute(
                """
                INSERT INTO runtime_setting_events (setting_key, old_value, new_value, actor, reason)
                VALUES (%s, %s::jsonb, %s::jsonb, %s, %s)
                """,
                (key, _json_any(old_value), _json_any(value), actor, reason),
            )
            return {
                "key": row[0],
                "value": row[1],
                "updated_by": row[2],
                "reason": row[3],
                "updated_at": row[4].isoformat(),
            }


def delete_runtime_setting(*, key: str, actor: str = "system", url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("DELETE FROM runtime_settings WHERE key = %s RETURNING value", (key,))
            row = cur.fetchone()
            deleted = row is not None
            cur.execute(
                """
                INSERT INTO runtime_setting_events (setting_key, old_value, new_value, actor, reason)
                VALUES (%s, %s::jsonb, NULL, %s, 'reset')
                """,
                (key, _json_any(row[0] if row else None), actor),
            )
            return {"key": key, "deleted": deleted}


def enqueue_runtime_control_request(
    *,
    setting_key: str,
    action: str,
    affected_components: list[str],
    actor: str = "system",
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO runtime_control_requests (
                    setting_key, action, affected_components, actor, metadata
                )
                VALUES (%s, %s, %s, %s, %s::jsonb)
                RETURNING id::text, setting_key, action, affected_components, status
                """,
                (setting_key, action, affected_components, actor, _json(metadata or {})),
            )
            row = cur.fetchone()
            outbox = _enqueue_message_outbox_with_cursor(
                cur,
                exchange=messaging.COMMANDS_EXCHANGE,
                routing_key=messaging.RUNTIME_CONTROL_ROUTING_KEY,
                message_type="flux.runtime_control.apply",
                payload={
                    "request_id": row[0],
                    "setting_key": row[1],
                    "action": row[2],
                    "affected_components": list(row[3] or []),
                    "actor": actor,
                },
                aggregate_type="runtime_control_requests",
                aggregate_id=row[0],
            )
            cur.execute(
                """
                UPDATE runtime_control_requests
                SET broker_message_id = %s,
                    correlation_id = %s,
                    routing_key = %s,
                    queued_at = COALESCE(queued_at, now()),
                    updated_at = now()
                WHERE id = %s
                """,
                (outbox.get("message_id"), outbox.get("message_id"), messaging.RUNTIME_CONTROL_ROUTING_KEY, row[0]),
            )
            return {
                "id": row[0],
                "setting_key": row[1],
                "action": row[2],
                "affected_components": list(row[3] or []),
                "status": row[4],
                "message_id": outbox.get("message_id"),
            }


def ack_runtime_control_requests(
    *,
    component: str | None = None,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            if component:
                cur.execute(
                    """
                    UPDATE runtime_control_requests
                    SET status = 'acknowledged',
                        acknowledged_at = now(),
                        metadata = metadata || %s::jsonb
                    WHERE status = 'pending'
                      AND %s = ANY(affected_components)
                    RETURNING id::text
                    """,
                    (_json({"acknowledged_by": actor}), component),
                )
            else:
                cur.execute(
                    """
                    UPDATE runtime_control_requests
                    SET status = 'acknowledged',
                        acknowledged_at = now(),
                        metadata = metadata || %s::jsonb
                    WHERE status = 'pending'
                    RETURNING id::text
                    """,
                    (_json({"acknowledged_by": actor}),),
                )
            return {"acknowledged": len(cur.fetchall()), "component": component}


def list_runtime_control_requests(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, setting_key, action, affected_components, status,
                       actor, requested_at, acknowledged_at, metadata, updated_at,
                       broker_message_id, correlation_id, routing_key, queued_at,
                       broker_delivery_count
                FROM runtime_control_requests
                ORDER BY updated_at DESC, requested_at DESC
                LIMIT %s
                """,
                (_dashboard_job_limit(limit),),
            )
            return [
                {
                    "id": row[0],
                    "setting_key": row[1],
                    "action": row[2],
                    "affected_components": list(row[3] or []),
                    "status": row[4],
                    "actor": row[5],
                    "requested_at": _iso_or_none(row[6]),
                    "acknowledged_at": _iso_or_none(row[7]),
                    "metadata": row[8] or {},
                    "updated_at": _iso_or_none(row[9]),
                    "broker_message_id": row[10],
                    "correlation_id": row[11],
                    "routing_key": row[12],
                    "queued_at": _iso_or_none(row[13]),
                    "broker_delivery_count": int(row[14] or 0),
                }
                for row in cur.fetchall()
            ]


def insert_mail_profile(
    *,
    name: str,
    source_type: str,
    account: str | None,
    server: str | None,
    folder_paths: list[str],
    spool_path: str,
    post_process_policy: str,
    trust_rank: int,
    metadata: dict[str, Any] | None = None,
    enabled: bool = True,
    sync_enabled: bool = False,
    sync_interval_seconds: int = 900,
    sync_window_days: int = 30,
    max_messages_per_run: int = 200,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO mail_profiles (
                    name, source_type, account, server, folder_paths, spool_path,
                    post_process_policy, enabled, trust_rank, metadata,
                    sync_enabled, sync_interval_seconds, sync_window_days,
                    max_messages_per_run, next_sync_at
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb,
                        %s, %s, %s, %s,
                        CASE WHEN %s THEN now() ELSE NULL END)
                ON CONFLICT (name) DO UPDATE SET
                    source_type = EXCLUDED.source_type,
                    account = EXCLUDED.account,
                    server = EXCLUDED.server,
                    folder_paths = EXCLUDED.folder_paths,
                    spool_path = EXCLUDED.spool_path,
                    post_process_policy = EXCLUDED.post_process_policy,
                    enabled = EXCLUDED.enabled,
                    trust_rank = EXCLUDED.trust_rank,
                    metadata = EXCLUDED.metadata,
                    sync_enabled = EXCLUDED.sync_enabled,
                    sync_interval_seconds = EXCLUDED.sync_interval_seconds,
                    sync_window_days = EXCLUDED.sync_window_days,
                    max_messages_per_run = EXCLUDED.max_messages_per_run,
                    next_sync_at = CASE WHEN EXCLUDED.sync_enabled THEN COALESCE(mail_profiles.next_sync_at, now()) ELSE NULL END,
                    updated_at = now()
                RETURNING id::text, name, source_type, account, server, folder_paths,
                          spool_path, post_process_policy, enabled, trust_rank, metadata,
                          sync_enabled, sync_interval_seconds, sync_window_days,
                          max_messages_per_run, last_sync_at, next_sync_at
                """,
                (
                    name,
                    source_type,
                    account,
                    server,
                    folder_paths,
                    spool_path,
                    post_process_policy,
                    enabled,
                    trust_rank,
                    _json(metadata or {}),
                    sync_enabled,
                    sync_interval_seconds,
                    sync_window_days,
                    max_messages_per_run,
                    sync_enabled,
                ),
            )
            return _mail_profile_row(cur.fetchone())


def list_mail_profiles(*, name: str | None = None, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            if name:
                cur.execute(
                    """
                    SELECT id::text, name, source_type, account, server, folder_paths,
                           spool_path, post_process_policy, enabled, trust_rank, metadata,
                           sync_enabled, sync_interval_seconds, sync_window_days,
                           max_messages_per_run, last_sync_at, next_sync_at
                    FROM mail_profiles
                    WHERE name = %s
                    ORDER BY name
                    """,
                    (name,),
                )
            else:
                cur.execute(
                    """
                    SELECT id::text, name, source_type, account, server, folder_paths,
                           spool_path, post_process_policy, enabled, trust_rank, metadata,
                           sync_enabled, sync_interval_seconds, sync_window_days,
                           max_messages_per_run, last_sync_at, next_sync_at
                    FROM mail_profiles
                    ORDER BY name
                    """
                )
            return [_mail_profile_row(row) for row in cur.fetchall()]


def delete_mail_profile(*, name: str, actor: str = "system", url: str | None = None) -> dict[str, Any]:
    normalized_name = str(name or "").strip()
    if not normalized_name:
        raise ValueError("mail profile name is required")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id::text, name FROM mail_profiles WHERE name = %s", (normalized_name,))
            row = cur.fetchone()
            if not row:
                raise ValueError(f"mail profile not found: {normalized_name}")
            profile_id, profile_name = row
            cur.execute(
                """
                DELETE FROM mail_profiles
                WHERE id = %s
                RETURNING id::text, name
                """,
                (profile_id,),
            )
            deleted = cur.fetchone()
            cur.execute(
                """
                INSERT INTO audit_events (event_type, actor, target_table, target_id, details)
                VALUES ('mail_profile.deleted', %s, 'mail_profiles', %s, %s::jsonb)
                """,
                (
                    actor,
                    profile_id,
                    _json({"name": profile_name}),
                ),
            )
            return {
                "id": deleted[0] if deleted else profile_id,
                "name": deleted[1] if deleted else profile_name,
                "deleted": deleted is not None,
            }


def list_due_imap_mail_profiles(*, limit: int = 10, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, name, source_type, account, server, folder_paths,
                       spool_path, post_process_policy, enabled, trust_rank, metadata,
                       sync_enabled, sync_interval_seconds, sync_window_days,
                       max_messages_per_run, last_sync_at, next_sync_at
                FROM mail_profiles
                WHERE source_type = 'imap'
                  AND enabled
                  AND sync_enabled
                  AND (next_sync_at IS NULL OR next_sync_at <= now())
                ORDER BY COALESCE(next_sync_at, now()), name
                LIMIT %s
                """,
                (max(1, min(limit, 100)),),
            )
            return [_mail_profile_row(row) for row in cur.fetchall()]


def create_imap_sync_run(
    *,
    profile_name: str,
    trigger: str = "manual",
    requested_by: str = "dashboard",
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            _expire_stale_imap_sync_runs(cur)
            cur.execute(
                """
                WITH profile AS (
                    SELECT id, name
                    FROM mail_profiles
                    WHERE name = %s
                      AND source_type = 'imap'
                      AND enabled
                    FOR UPDATE
                ),
                active AS (
                    SELECT r.id::text, p.name, r.status, r.trigger, r.requested_by,
                           r.claimed_by, r.claimed_at, r.worker_id, r.attempt_count,
                           r.last_error, r.next_attempt_at, r.drift_seconds,
                           r.missed_runs, r.started_at, r.finished_at,
                           r.messages_seen, r.messages_exported, r.last_cursor, r.errors
                    FROM mail_sync_runs r
                    JOIN profile p ON p.id = r.profile_id
                    WHERE r.status IN ('queued', 'claimed', 'running', 'backoff')
                    ORDER BY r.started_at DESC
                    LIMIT 1
                ),
                inserted AS (
                    INSERT INTO mail_sync_runs (
                        profile_id, status, trigger, requested_by, drift_seconds, missed_runs
                    )
                    SELECT p.id, 'queued', %s, %s, 0, 0
                    FROM profile p
                    WHERE NOT EXISTS (SELECT 1 FROM active)
                    RETURNING id::text, status, trigger, requested_by, claimed_by,
                              claimed_at, worker_id, attempt_count, last_error,
                              next_attempt_at, drift_seconds, missed_runs, started_at,
                              finished_at, messages_seen, messages_exported, last_cursor,
                              errors
                )
                SELECT i.id, p.name, i.status, i.trigger, i.requested_by,
                       i.claimed_by, i.claimed_at, i.worker_id, i.attempt_count,
                       i.last_error, i.next_attempt_at, i.drift_seconds,
                       i.missed_runs, i.started_at, i.finished_at, i.messages_seen,
                       i.messages_exported, i.last_cursor, i.errors
                FROM inserted i
                CROSS JOIN profile p
                UNION ALL
                SELECT id, name, status, trigger, requested_by, claimed_by, claimed_at,
                       worker_id, attempt_count, last_error, next_attempt_at,
                       drift_seconds, missed_runs, started_at, finished_at,
                       messages_seen, messages_exported, last_cursor, errors
                FROM active
                LIMIT 1
                """,
                (profile_name, trigger, requested_by),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"IMAP profile not found or disabled: {profile_name}")
            return _mail_sync_run_row(row)


def enqueue_imap_sync_command(
    *,
    profile_name: str | None = None,
    requested_by: str = "dashboard",
    url: str | None = None,
) -> dict[str, Any]:
    if profile_name is None or not str(profile_name).strip():
        return enqueue_due_imap_sync_commands(limit=10, requested_by=requested_by, url=url) | {
            "accepted": True,
            "operation_id": _stable_message_id("imap-sync-due", str(requested_by), str(time.time())),
            "operation_type": "mail_imap_sync",
            "status_url": "/api/mail/status",
            "event_topics": ["mail.imap.completed", "mail.imap.failed", "mail.imap.retrying"],
        }
    clean_profile = str(profile_name).strip()
    psycopg = _load_psycopg()
    runs: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            _expire_stale_imap_sync_runs(cur)
            cur.execute(
                """
                WITH profile AS (
                    SELECT id, name
                    FROM mail_profiles
                    WHERE name = %s
                      AND source_type = 'imap'
                      AND enabled
                    FOR UPDATE
                ),
                active AS (
                    SELECT r.id::text, r.profile_id::text, p.name, r.status, r.trigger,
                           r.requested_by, r.broker_message_id, r.correlation_id
                    FROM mail_sync_runs r
                    JOIN profile p ON p.id = r.profile_id
                    WHERE r.status IN ('queued', 'claimed', 'running', 'backoff')
                    ORDER BY r.started_at DESC
                    LIMIT 1
                ),
                inserted AS (
                    INSERT INTO mail_sync_runs (
                        profile_id, status, trigger, requested_by, drift_seconds, missed_runs
                    )
                    SELECT p.id, 'queued', 'manual', %s, 0, 0
                    FROM profile p
                    WHERE NOT EXISTS (SELECT 1 FROM active)
                    RETURNING id::text, profile_id::text, status, trigger, requested_by,
                              broker_message_id, correlation_id
                )
                SELECT i.id, i.profile_id, p.name, i.status, i.trigger,
                       i.requested_by, i.broker_message_id, i.correlation_id
                FROM inserted i
                CROSS JOIN profile p
                UNION ALL
                SELECT id, profile_id, name, status, trigger, requested_by,
                       broker_message_id, correlation_id
                FROM active
                LIMIT 1
                """,
                (clean_profile, requested_by),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"IMAP profile not found or disabled: {clean_profile}")
            run_id = str(row[0])
            profile_id = str(row[1])
            broker_message_id = str(row[6] or "")
            correlation_id = str(row[7] or broker_message_id or "")
            if not broker_message_id:
                outbox = _enqueue_message_outbox_with_cursor(
                    cur,
                    exchange=messaging.COMMANDS_EXCHANGE,
                    routing_key=messaging.MAIL_IMAP_SYNC_ROUTING_KEY,
                    message_type="flux.mail.imap.sync",
                    payload={
                        "run_id": run_id,
                        "profile_id": profile_id,
                        "trigger": row[4],
                        "requested_by": row[5],
                    },
                    aggregate_type="mail_sync_runs",
                    aggregate_id=run_id,
                )
                broker_message_id = str(outbox.get("message_id") or "")
                correlation_id = broker_message_id
                cur.execute(
                    """
                    UPDATE mail_sync_runs
                    SET broker_message_id = %s,
                        correlation_id = %s,
                        routing_key = %s,
                        queued_at = COALESCE(queued_at, now()),
                        updated_at = now()
                    WHERE id = %s
                    """,
                    (broker_message_id, correlation_id, messaging.MAIL_IMAP_SYNC_ROUTING_KEY, run_id),
                )
            runs.append(
                {
                    "run_id": run_id,
                    "profile_id": profile_id,
                    "profile_name": str(row[2]),
                    "status": str(row[3]),
                    "message_id": broker_message_id,
                }
            )
    operation_id = _stable_message_id("imap-sync", clean_profile, runs[0]["run_id"])
    return {
        "accepted": True,
        "operation_id": operation_id,
        "operation_type": "mail_imap_sync",
        "queued": len(runs),
        "runs": runs,
        "status_url": "/api/mail/status",
        "event_topics": ["mail.imap.completed", "mail.imap.failed", "mail.imap.retrying"],
        "settings_mutated": False,
    }


def claim_due_imap_sync_runs(
    *,
    limit: int = 10,
    worker_id: str = "flux-kb-mail-worker",
    url: str | None = None,
) -> list[dict[str, Any]]:
    capped_limit = max(1, min(limit, 100))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            _expire_stale_imap_sync_runs(cur)
            cur.execute(
                """
                WITH due_profiles AS (
                    SELECT p.id,
                           GREATEST(EXTRACT(EPOCH FROM (now() - COALESCE(p.next_sync_at, now())))::integer, 0) AS drift_seconds,
                           CASE
                               WHEN p.next_sync_at IS NULL OR p.sync_interval_seconds <= 0 THEN 0
                               ELSE GREATEST(FLOOR(EXTRACT(EPOCH FROM (now() - p.next_sync_at)) / p.sync_interval_seconds)::integer, 0)
                           END AS missed_runs
                    FROM mail_profiles p
                    WHERE p.source_type = 'imap'
                      AND p.enabled
                      AND p.sync_enabled
                      AND (p.next_sync_at IS NULL OR p.next_sync_at <= now())
                      AND NOT EXISTS (
                          SELECT 1
                          FROM mail_sync_runs active
                          WHERE active.profile_id = p.id
                            AND active.status IN ('queued', 'claimed', 'running', 'backoff')
                      )
                    ORDER BY COALESCE(p.next_sync_at, now()), p.name
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
                )
                INSERT INTO mail_sync_runs (
                    profile_id, status, trigger, requested_by, drift_seconds, missed_runs
                )
                SELECT id, 'queued', 'schedule', 'scheduler', drift_seconds, missed_runs
                FROM due_profiles
                ON CONFLICT DO NOTHING
                """,
                (capped_limit,),
            )
            cur.execute(
                """
                WITH claimable AS (
                    SELECT r.id, r.profile_id
                    FROM mail_sync_runs r
                    JOIN mail_profiles p ON p.id = r.profile_id
                    WHERE p.source_type = 'imap'
                      AND p.enabled
                      AND r.status IN ('queued', 'backoff')
                      AND (r.next_attempt_at IS NULL OR r.next_attempt_at <= now())
                    ORDER BY CASE WHEN r.trigger = 'manual' THEN 0 ELSE 1 END,
                             COALESCE(r.next_attempt_at, r.started_at),
                             r.started_at
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE mail_sync_runs r
                SET status = 'claimed',
                    claimed_by = %s,
                    claimed_at = now(),
                    worker_id = %s,
                    attempt_count = r.attempt_count + 1,
                    updated_at = now()
                FROM claimable
                JOIN mail_profiles p ON p.id = claimable.profile_id
                WHERE r.id = claimable.id
                RETURNING r.id::text, p.name, r.status, r.trigger, r.requested_by,
                          r.claimed_by, r.claimed_at, r.worker_id, r.attempt_count,
                          r.last_error, r.next_attempt_at, r.drift_seconds,
                          r.missed_runs, r.started_at, r.finished_at, r.messages_seen,
                          r.messages_exported, r.last_cursor, r.errors,
                          p.id::text, p.source_type, p.account, p.server,
                          p.folder_paths, p.spool_path, p.post_process_policy,
                          p.enabled, p.trust_rank, p.metadata, p.sync_enabled,
                          p.sync_interval_seconds, p.sync_window_days,
                          p.max_messages_per_run, p.last_sync_at, p.next_sync_at
                """,
                (capped_limit, worker_id, worker_id),
            )
            return [_mail_sync_run_row(row, include_profile=True) for row in cur.fetchall()]


def enqueue_due_imap_sync_commands(
    *,
    limit: int = 10,
    requested_by: str = "scheduler",
    url: str | None = None,
) -> dict[str, Any]:
    capped_limit = max(1, min(limit, 100))
    psycopg = _load_psycopg()
    runs: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            _expire_stale_imap_sync_runs(cur)
            cur.execute(
                """
                WITH due_profiles AS (
                    SELECT p.id,
                           p.name,
                           GREATEST(EXTRACT(EPOCH FROM (now() - COALESCE(p.next_sync_at, now())))::integer, 0) AS drift_seconds,
                           CASE
                               WHEN p.next_sync_at IS NULL OR p.sync_interval_seconds <= 0 THEN 0
                               ELSE GREATEST(FLOOR(EXTRACT(EPOCH FROM (now() - p.next_sync_at)) / p.sync_interval_seconds)::integer, 0)
                           END AS missed_runs
                    FROM mail_profiles p
                    WHERE p.source_type = 'imap'
                      AND p.enabled
                      AND p.sync_enabled
                      AND (p.next_sync_at IS NULL OR p.next_sync_at <= now())
                      AND NOT EXISTS (
                          SELECT 1
                          FROM mail_sync_runs active
                          WHERE active.profile_id = p.id
                            AND active.status IN ('queued', 'claimed', 'running', 'backoff')
                      )
                    ORDER BY COALESCE(p.next_sync_at, now()), p.name
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
                )
                INSERT INTO mail_sync_runs (
                    profile_id, status, trigger, requested_by, drift_seconds, missed_runs
                )
                SELECT id, 'queued', 'schedule', %s, drift_seconds, missed_runs
                FROM due_profiles
                RETURNING id::text, profile_id::text, status, trigger, requested_by, drift_seconds, missed_runs
                """,
                (capped_limit, requested_by),
            )
            rows = cur.fetchall()
            for row in rows:
                outbox = _enqueue_message_outbox_with_cursor(
                    cur,
                    exchange=messaging.COMMANDS_EXCHANGE,
                    routing_key=messaging.MAIL_IMAP_SYNC_ROUTING_KEY,
                    message_type="flux.mail.imap.sync",
                    payload={
                        "run_id": row[0],
                        "profile_id": row[1],
                        "trigger": row[3],
                        "requested_by": row[4],
                    },
                    aggregate_type="mail_sync_runs",
                    aggregate_id=row[0],
                )
                cur.execute(
                    """
                    UPDATE mail_sync_runs
                    SET broker_message_id = %s,
                        correlation_id = %s,
                        routing_key = %s,
                        queued_at = COALESCE(queued_at, now()),
                        updated_at = now()
                    WHERE id = %s
                    """,
                    (outbox.get("message_id"), outbox.get("message_id"), messaging.MAIL_IMAP_SYNC_ROUTING_KEY, row[0]),
                )
                runs.append(
                    {
                        "run_id": row[0],
                        "profile_id": row[1],
                        "status": row[2],
                        "message_id": outbox.get("message_id"),
                    }
                )
    return {"queued": len(runs), "runs": runs}


def claim_imap_sync_run_by_id(
    *,
    run_id: str,
    worker_id: str,
    broker_message_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_sync_runs r
                SET status = 'claimed',
                    claimed_by = %s,
                    claimed_at = now(),
                    worker_id = %s,
                    attempt_count = CASE WHEN r.status = 'claimed' THEN r.attempt_count ELSE r.attempt_count + 1 END,
                    broker_message_id = COALESCE(%s::text, r.broker_message_id),
                    broker_delivery_count = r.broker_delivery_count + 1,
                    updated_at = now()
                FROM mail_profiles p
                WHERE p.id = r.profile_id
                  AND r.id = %s
                  AND p.source_type = 'imap'
                  AND p.enabled
                  AND (
                      r.status IN ('queued', 'backoff')
                      OR (r.status = 'claimed' AND (%s::text IS NULL OR r.broker_message_id = %s::text))
                  )
                  AND (r.next_attempt_at IS NULL OR r.next_attempt_at <= now())
                RETURNING r.id::text, p.name, r.status, r.trigger, r.requested_by,
                          r.claimed_by, r.claimed_at, r.worker_id, r.attempt_count,
                          r.last_error, r.next_attempt_at, r.drift_seconds,
                          r.missed_runs, r.started_at, r.finished_at, r.messages_seen,
                          r.messages_exported, r.last_cursor, r.errors,
                          p.id::text, p.source_type, p.account, p.server,
                          p.folder_paths, p.spool_path, p.post_process_policy,
                          p.enabled, p.trust_rank, p.metadata, p.sync_enabled,
                          p.sync_interval_seconds, p.sync_window_days,
                          p.max_messages_per_run, p.last_sync_at, p.next_sync_at
                """,
                (worker_id, worker_id, broker_message_id, run_id, broker_message_id, broker_message_id),
            )
            row = cur.fetchone()
            return _mail_sync_run_row(row, include_profile=True) if row else None


def mark_mail_sync_run_running(
    *,
    run_id: str,
    worker_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_sync_runs
                SET status = 'running',
                    worker_id = COALESCE(%s, worker_id),
                    attempt_count = CASE WHEN attempt_count = 0 THEN 1 ELSE attempt_count END,
                    started_at = now(),
                    updated_at = now()
                WHERE id = %s
                RETURNING id::text, status
                """,
                (worker_id, run_id),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"mail sync run not found: {run_id}")
            return {"id": row[0], "status": row[1]}


def complete_mail_sync_run(
    *,
    run_id: str,
    profile_name: str,
    status: str,
    messages_seen: int = 0,
    messages_exported: int = 0,
    last_cursor: dict[str, Any] | None = None,
    errors: list[dict[str, Any]] | None = None,
    backoff_seconds: int | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    error_payload = errors or []
    last_error = _mail_last_error(error_payload)
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_sync_runs r
                SET status = %s,
                    finished_at = now(),
                    messages_seen = %s,
                    messages_exported = %s,
                    last_cursor = %s::jsonb,
                    errors = %s::jsonb,
                    last_error = %s,
                    next_attempt_at = CASE
                        WHEN %s::integer IS NOT NULL THEN now() + make_interval(secs => %s::integer)
                        ELSE NULL
                    END,
                    updated_at = now()
                FROM mail_profiles p
                WHERE r.id = %s
                  AND p.id = r.profile_id
                  AND p.name = %s
                RETURNING r.id::text, p.id, r.status
                """,
                (
                    status,
                    messages_seen,
                    messages_exported,
                    _json(last_cursor or {}),
                    _json_list(error_payload),
                    last_error,
                    backoff_seconds,
                    backoff_seconds,
                    run_id,
                    profile_name,
                ),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"mail sync run not found: {run_id}")
            cur.execute(
                """
                UPDATE mail_profiles
                SET last_sync_at = now(),
                    next_sync_at = CASE
                        WHEN NOT sync_enabled THEN NULL
                        WHEN %s::integer IS NOT NULL THEN now() + make_interval(secs => %s::integer)
                        ELSE now() + make_interval(secs => sync_interval_seconds)
                    END,
                    updated_at = now()
                WHERE id = %s
                """,
                (backoff_seconds, backoff_seconds, row[1]),
            )
            return {"id": row[0], "profile_name": profile_name, "status": row[2]}


def list_mail_sync_runs(
    *,
    profile_name: str | None = None,
    limit: int = 20,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    capped_limit = max(1, min(limit, 100))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            if profile_name:
                cur.execute(
                    """
                    SELECT r.id::text, p.name, r.status, r.trigger, r.requested_by,
                           r.claimed_by, r.claimed_at, r.worker_id, r.attempt_count,
                           r.last_error, r.next_attempt_at, r.drift_seconds,
                           r.missed_runs, r.started_at, r.finished_at,
                           r.messages_seen, r.messages_exported, r.last_cursor, r.errors
                    FROM mail_sync_runs r
                    JOIN mail_profiles p ON p.id = r.profile_id
                    WHERE p.name = %s
                    ORDER BY r.updated_at DESC, r.started_at DESC
                    LIMIT %s
                    """,
                    (profile_name, capped_limit),
                )
            else:
                cur.execute(
                    """
                    SELECT r.id::text, p.name, r.status, r.trigger, r.requested_by,
                           r.claimed_by, r.claimed_at, r.worker_id, r.attempt_count,
                           r.last_error, r.next_attempt_at, r.drift_seconds,
                           r.missed_runs, r.started_at, r.finished_at,
                           r.messages_seen, r.messages_exported, r.last_cursor, r.errors
                    FROM mail_sync_runs r
                    JOIN mail_profiles p ON p.id = r.profile_id
                    WHERE p.source_type = 'imap'
                    ORDER BY r.updated_at DESC, r.started_at DESC
                    LIMIT %s
                    """,
                    (capped_limit,),
                )
            return [_mail_sync_run_row(row) for row in cur.fetchall()]


def mail_scheduler_status(*, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    counts = {
        "due": 0,
        "queued": 0,
        "claimed": 0,
        "running": 0,
        "failed": 0,
        "blocked_auth": 0,
        "backoff": 0,
    }
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT count(*)
                FROM mail_profiles p
                WHERE p.source_type = 'imap'
                  AND p.enabled
                  AND p.sync_enabled
                  AND (p.next_sync_at IS NULL OR p.next_sync_at <= now())
                  AND NOT EXISTS (
                      SELECT 1
                      FROM mail_sync_runs active
                      WHERE active.profile_id = p.id
                        AND active.status IN ('queued', 'claimed', 'running', 'backoff')
                  )
                """
            )
            counts["due"] = cur.fetchone()[0]
            cur.execute(
                """
                SELECT
                    count(*) FILTER (WHERE r.status = 'queued') AS queued,
                    count(*) FILTER (WHERE r.status = 'claimed') AS claimed,
                    count(*) FILTER (WHERE r.status = 'running') AS running,
                    count(*) FILTER (WHERE r.status = 'failed') AS failed,
                    count(*) FILTER (WHERE r.status IN ('blocked_auth_required', 'auth_expired', 'auth_failed')) AS blocked_auth,
                    count(*) FILTER (WHERE r.status = 'backoff') AS backoff
                FROM mail_sync_runs r
                JOIN mail_profiles p ON p.id = r.profile_id
                WHERE p.source_type = 'imap'
                """
            )
            row = cur.fetchone()
            counts.update(
                {
                    "queued": int(row[0] or 0),
                    "claimed": int(row[1] or 0),
                    "running": int(row[2] or 0),
                    "failed": int(row[3] or 0),
                    "blocked_auth": int(row[4] or 0),
                    "backoff": int(row[5] or 0),
                }
            )
    recent_runs = list_mail_sync_runs(limit=20, url=url)
    return {
        "counts": counts,
        "recent_runs": recent_runs,
        "diagnostics": [_mail_scheduler_diagnostic(run) for run in recent_runs if _mail_run_needs_action(run)],
    }


def update_mail_profile_metadata(
    *,
    name: str,
    metadata: dict[str, Any],
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_profiles
                SET metadata = %s::jsonb,
                    updated_at = now()
                WHERE name = %s
                RETURNING id::text, name, source_type, account, server, folder_paths,
                          spool_path, post_process_policy, enabled, trust_rank, metadata,
                          sync_enabled, sync_interval_seconds, sync_window_days,
                          max_messages_per_run, last_sync_at, next_sync_at
                """,
                (_json(metadata), name),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"mail profile not found: {name}")
            return _mail_profile_row(row)


def create_mail_oauth_state(
    *,
    profile_name: str,
    provider: str,
    state: str,
    code_verifier: str,
    redirect_uri: str,
    client_config: dict[str, Any],
    client_config_path: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id FROM mail_profiles WHERE name = %s", (profile_name,))
            profile = cur.fetchone()
            if profile is None:
                raise ValueError(f"mail profile not found: {profile_name}")
            cur.execute(
                """
                INSERT INTO mail_oauth_states (
                    profile_id, provider, state, code_verifier, redirect_uri,
                    client_config, client_config_path, metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s::jsonb, %s, %s::jsonb)
                RETURNING id::text, provider, state, code_verifier, redirect_uri,
                          client_config, client_config_path, metadata, created_at,
                          expires_at, consumed_at
                """,
                (
                    profile[0],
                    provider,
                    state,
                    code_verifier,
                    redirect_uri,
                    _json(client_config),
                    client_config_path,
                    _json(metadata or {}),
                ),
            )
            return _mail_oauth_state_row(cur.fetchone(), profile_name=profile_name)


def get_mail_oauth_state(state: str, *, url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT s.id::text, p.name, s.provider, s.state, s.code_verifier,
                       s.redirect_uri, s.client_config, s.client_config_path,
                       s.metadata, s.created_at, s.expires_at, s.consumed_at
                FROM mail_oauth_states s
                JOIN mail_profiles p ON p.id = s.profile_id
                WHERE s.state = %s
                """,
                (state,),
            )
            row = cur.fetchone()
            return _mail_oauth_state_lookup_row(row) if row else None


def consume_mail_oauth_state(*, state: str, url: str | None = None) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_oauth_states
                SET consumed_at = now()
                WHERE state = %s
                """,
                (state,),
            )


def upsert_mail_oauth_token(
    *,
    profile_name: str,
    provider: str,
    refresh_token: str,
    scope: str,
    token_type: str,
    status: str,
    client_config: dict[str, Any],
    expires_at: str | None = None,
    last_error: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id FROM mail_profiles WHERE name = %s", (profile_name,))
            profile = cur.fetchone()
            if profile is None:
                raise ValueError(f"mail profile not found: {profile_name}")
            cur.execute(
                """
                INSERT INTO mail_oauth_tokens (
                    profile_id, provider, refresh_token, scope, token_type, status,
                    last_error, client_config, metadata, refreshed_at, expires_at
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s::jsonb, %s::jsonb, now(), %s)
                ON CONFLICT (profile_id, provider) DO UPDATE SET
                    refresh_token = EXCLUDED.refresh_token,
                    scope = EXCLUDED.scope,
                    token_type = EXCLUDED.token_type,
                    status = EXCLUDED.status,
                    last_error = EXCLUDED.last_error,
                    client_config = EXCLUDED.client_config,
                    metadata = EXCLUDED.metadata,
                    refreshed_at = now(),
                    expires_at = EXCLUDED.expires_at,
                    updated_at = now()
                RETURNING id::text, provider, refresh_token, scope, token_type,
                          status, last_error, client_config, metadata,
                          refreshed_at, expires_at, updated_at
                """,
                (
                    profile[0],
                    provider,
                    refresh_token,
                    scope,
                    token_type,
                    status,
                    last_error,
                    _json(client_config),
                    _json(metadata or {}),
                    expires_at,
                ),
            )
            return _mail_oauth_token_row(cur.fetchone(), profile_name=profile_name)


def get_mail_oauth_token(
    profile_name: str,
    *,
    provider: str = "gmail",
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT t.id::text, t.provider, t.refresh_token, t.scope, t.token_type,
                       t.status, t.last_error, t.client_config, t.metadata,
                       t.refreshed_at, t.expires_at, t.updated_at
                FROM mail_oauth_tokens t
                JOIN mail_profiles p ON p.id = t.profile_id
                WHERE p.name = %s AND t.provider = %s
                """,
                (profile_name, provider),
            )
            row = cur.fetchone()
            return _mail_oauth_token_row(row, profile_name=profile_name) if row else None


def update_mail_oauth_token_status(
    *,
    profile_name: str,
    provider: str = "gmail",
    status: str,
    last_error: str | None = None,
    expires_at: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_oauth_tokens t
                SET status = %s,
                    last_error = %s,
                    refreshed_at = CASE WHEN %s = 'configured' THEN now() ELSE refreshed_at END,
                    expires_at = COALESCE(%s, expires_at),
                    updated_at = now()
                FROM mail_profiles p
                WHERE p.id = t.profile_id
                  AND p.name = %s
                  AND t.provider = %s
                RETURNING t.id::text, t.provider, t.refresh_token, t.scope, t.token_type,
                          t.status, t.last_error, t.client_config, t.metadata,
                          t.refreshed_at, t.expires_at, t.updated_at
                """,
                (status, last_error, status, expires_at, profile_name, provider),
            )
            row = cur.fetchone()
            return _mail_oauth_token_row(row, profile_name=profile_name) if row else None


def mail_oauth_status(
    *,
    profile_name: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    where = "WHERE p.name = %s" if profile_name else ""
    params: tuple[Any, ...] = (profile_name,) if profile_name else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT p.name, p.source_type, p.account, p.enabled,
                       t.provider, t.status, t.scope, t.token_type, t.last_error,
                       t.refreshed_at, t.expires_at, t.updated_at,
                       t.refresh_token IS NOT NULL AS has_refresh_token
                FROM mail_profiles p
                LEFT JOIN mail_oauth_tokens t ON t.profile_id = p.id
                {where}
                ORDER BY p.name
                """,
                params,
            )
            profiles = []
            for row in cur.fetchall():
                status = row[5] or ("blocked_auth_required" if row[1] == "imap" else "not_required")
                profiles.append(
                    {
                        "profile_name": row[0],
                        "source_type": row[1],
                        "account": row[2],
                        "enabled": row[3],
                        "provider": row[4] or ("gmail" if row[1] == "imap" else None),
                        "status": status,
                        "scope": row[6],
                        "token_type": row[7],
                        "last_error": row[8],
                        "refreshed_at": row[9].isoformat() if row[9] else None,
                        "expires_at": row[10].isoformat() if row[10] else None,
                        "updated_at": row[11].isoformat() if row[11] else None,
                        "has_refresh_token": bool(row[12]),
                    }
                )
            return {"profiles": profiles, "count": len(profiles)}


def record_mail_sync_run(
    *,
    profile_name: str,
    status: str,
    messages_seen: int = 0,
    messages_exported: int = 0,
    last_cursor: dict[str, Any] | None = None,
    errors: list[dict[str, Any]] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id FROM mail_profiles WHERE name = %s", (profile_name,))
            profile = cur.fetchone()
            if profile is None:
                raise ValueError(f"mail profile not found: {profile_name}")
            cur.execute(
                """
                INSERT INTO mail_sync_runs (
                    profile_id, status, finished_at, messages_seen, messages_exported,
                    last_cursor, errors
                )
                VALUES (%s, %s, now(), %s, %s, %s::jsonb, %s::jsonb)
                RETURNING id::text, status
                """,
                (
                    profile[0],
                    status,
                    messages_seen,
                    messages_exported,
                    _json(last_cursor or {}),
                    _json_list(errors or []),
                ),
            )
            row = cur.fetchone()
            cur.execute(
                """
                UPDATE mail_profiles
                SET last_sync_at = now(),
                    next_sync_at = CASE
                        WHEN sync_enabled THEN now() + make_interval(secs => sync_interval_seconds)
                        ELSE NULL
                    END,
                    updated_at = now()
                WHERE id = %s
                """,
                (profile[0],),
            )
            return {"id": row[0], "status": row[1]}


def record_mail_message(
    *,
    profile_name: str,
    source_message_id: str,
    source_folder: str,
    export_state: str,
    export_id: str | None = None,
    content_hash: str | None = None,
    internet_message_id: str | None = None,
    metadata: dict[str, Any] | None = None,
    error: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id FROM mail_profiles WHERE name = %s", (profile_name,))
            profile = cur.fetchone()
            if profile is None:
                raise ValueError(f"mail profile not found: {profile_name}")
            cur.execute(
                """
                INSERT INTO mail_messages (
                    profile_id, source_message_id, source_folder, internet_message_id,
                    content_hash, export_id, export_state, error, metadata, exported_at
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb,
                        CASE WHEN %s = 'exported' THEN now() ELSE NULL END)
                ON CONFLICT (profile_id, source_folder, source_message_id) DO UPDATE SET
                    internet_message_id = EXCLUDED.internet_message_id,
                    content_hash = EXCLUDED.content_hash,
                    export_id = EXCLUDED.export_id,
                    export_state = EXCLUDED.export_state,
                    error = EXCLUDED.error,
                    metadata = EXCLUDED.metadata,
                    exported_at = CASE WHEN EXCLUDED.export_state = 'exported' THEN now() ELSE mail_messages.exported_at END,
                    updated_at = now()
                RETURNING id::text, export_state
                """,
                (
                    profile[0],
                    source_message_id,
                    source_folder,
                    internet_message_id,
                    content_hash,
                    export_id,
                    export_state,
                    error,
                    _json(metadata or {}),
                    export_state,
                ),
            )
            row = cur.fetchone()
            return {"id": row[0], "export_state": row[1]}


def mail_message_exists(
    *,
    profile_name: str,
    source_folder: str,
    source_message_id: str,
    url: str | None = None,
) -> bool:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM mail_messages m
                    JOIN mail_profiles p ON p.id = m.profile_id
                    WHERE p.name = %s
                      AND m.source_folder = %s
                      AND m.source_message_id = %s
                )
                """,
                (profile_name, source_folder, source_message_id),
            )
            row = cur.fetchone()
            return bool(row and row[0])


def record_mail_post_process_event(
    *,
    profile_name: str,
    provider: str,
    policy: str,
    action: str,
    status: str,
    dry_run: bool = False,
    sync_run_id: str | None = None,
    mail_message_id: str | None = None,
    commands: list[dict[str, Any]] | None = None,
    error: str | None = None,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id FROM mail_profiles WHERE name = %s", (profile_name,))
            profile = cur.fetchone()
            if profile is None:
                raise ValueError(f"mail profile not found: {profile_name}")
            profile_id = profile[0]
            cur.execute(
                """
                INSERT INTO mail_post_process_events (
                    profile_id, sync_run_id, mail_message_id, provider, policy,
                    action, status, dry_run, commands, error, metadata
                )
                VALUES (
                    %s,
                    %s::uuid,
                    %s::uuid,
                    %s,
                    %s,
                    %s,
                    %s,
                    %s,
                    %s::jsonb,
                    %s,
                    %s::jsonb
                )
                RETURNING id::text,
                          (SELECT name FROM mail_profiles WHERE id = %s),
                          sync_run_id::text, mail_message_id::text, provider,
                          policy, action, status, dry_run, commands, error,
                          metadata, created_at
                """,
                (
                    profile_id,
                    sync_run_id,
                    mail_message_id,
                    provider,
                    policy,
                    action,
                    status,
                    dry_run,
                    _json_list(commands or []),
                    error,
                    _json(metadata or {}),
                    profile_id,
                ),
            )
            row = cur.fetchone()
            if mail_message_id:
                cur.execute(
                    """
                    UPDATE mail_messages
                    SET post_process_policy = %s,
                        post_process_status = %s,
                        post_process_action = %s,
                        post_process_error = %s,
                        post_process_dry_run = %s,
                        post_processed_at = CASE WHEN NOT %s THEN now() ELSE post_processed_at END,
                        post_process_metadata = post_process_metadata || %s::jsonb,
                        updated_at = now()
                    WHERE id = %s
                    """,
                    (
                        policy,
                        status,
                        action,
                        error,
                        dry_run,
                        dry_run,
                        _json(metadata or {}),
                        mail_message_id,
                    ),
                )
            return _mail_post_process_event_row(row)


def list_mail_post_process_events(
    *,
    profile_name: str | None = None,
    limit: int = 20,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    filters: list[str] = []
    params: list[Any] = []
    if profile_name:
        filters.append("p.name = %s")
        params.append(profile_name)
    where = f"WHERE {' AND '.join(filters)}" if filters else ""
    params.append(max(1, min(limit, 200)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT e.id::text, p.name, e.sync_run_id::text,
                       e.mail_message_id::text, e.provider, e.policy, e.action,
                       e.status, e.dry_run, e.commands, e.error, e.metadata,
                       e.created_at
                FROM mail_post_process_events e
                LEFT JOIN mail_profiles p ON p.id = e.profile_id
                {where}
                ORDER BY e.created_at DESC
                LIMIT %s
                """,
                tuple(params),
            )
            return [_mail_post_process_event_row(row) for row in cur.fetchall()]


def mail_status(*, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT count(*) FROM mail_profiles WHERE enabled")
            enabled_profiles = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM mail_messages WHERE export_state = 'exported'")
            exported_messages = cur.fetchone()[0]
            cur.execute("SELECT count(*) FROM mail_messages WHERE export_state = 'error'")
            errored_messages = cur.fetchone()[0]
            cur.execute(
                """
                SELECT
                    count(*) FILTER (WHERE status = 'planned') AS planned,
                    count(*) FILTER (WHERE status = 'applied') AS applied,
                    count(*) FILTER (WHERE status = 'failed') AS failed,
                    count(*) FILTER (WHERE status = 'blocked_config') AS blocked_config
                FROM mail_post_process_events
                """
            )
            post_process_counts = cur.fetchone()
            profiles = list_mail_profiles(url=url)
            return {
                "enabled_profiles": enabled_profiles,
                "exported_messages": exported_messages,
                "errored_messages": errored_messages,
                "profiles": profiles,
                "scheduler": mail_scheduler_status(url=url),
                "post_process": {
                    "counts": {
                        "planned": int(post_process_counts[0] or 0),
                        "applied": int(post_process_counts[1] or 0),
                        "failed": int(post_process_counts[2] or 0),
                        "blocked_config": int(post_process_counts[3] or 0),
                    },
                    "recent_events": list_mail_post_process_events(limit=10, url=url),
                },
            }


def update_mail_profile_sync(
    *,
    name: str,
    sync_enabled: bool,
    sync_interval_seconds: int | None = None,
    sync_window_days: int | None = None,
    max_messages_per_run: int | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_profiles
                SET sync_enabled = %s,
                    sync_interval_seconds = COALESCE(%s, sync_interval_seconds),
                    sync_window_days = COALESCE(%s, sync_window_days),
                    max_messages_per_run = COALESCE(%s, max_messages_per_run),
                    next_sync_at = CASE WHEN %s THEN COALESCE(next_sync_at, now()) ELSE NULL END,
                    updated_at = now()
                WHERE name = %s
                RETURNING id::text, name, source_type, account, server, folder_paths,
                          spool_path, post_process_policy, enabled, trust_rank, metadata,
                          sync_enabled, sync_interval_seconds, sync_window_days,
                          max_messages_per_run, last_sync_at, next_sync_at
                """,
                (
                    sync_enabled,
                    sync_interval_seconds,
                    sync_window_days,
                    max_messages_per_run,
                    sync_enabled,
                    name,
                ),
            )
            row = cur.fetchone()
            if row is None:
                raise ValueError(f"mail profile not found: {name}")
            return _mail_profile_row(row)


def create_outlook_sync_request(
    *,
    profile_name: str,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id FROM mail_profiles WHERE name = %s AND source_type = 'outlook_com'", (profile_name,))
            profile = cur.fetchone()
            if profile is None:
                raise ValueError(f"Outlook COM profile not found: {profile_name}")
            cur.execute(
                """
                INSERT INTO outlook_sync_requests (profile_id, requested_by)
                VALUES (%s, %s)
                RETURNING id::text, status, created_at
                """,
                (profile[0], actor),
            )
            row = cur.fetchone()
            outbox = _enqueue_message_outbox_with_cursor(
                cur,
                exchange=messaging.COMMANDS_EXCHANGE,
                routing_key=messaging.OUTLOOK_SYNC_ROUTING_KEY,
                message_type="flux.mail.outlook.sync",
                payload={"request_id": row[0], "profile_name": profile_name, "requested_by": actor},
                aggregate_type="outlook_sync_requests",
                aggregate_id=row[0],
            )
            cur.execute(
                """
                UPDATE outlook_sync_requests
                SET broker_message_id = %s,
                    correlation_id = %s,
                    routing_key = %s,
                    queued_at = COALESCE(queued_at, now()),
                    updated_at = now()
                WHERE id = %s
                """,
                (outbox.get("message_id"), outbox.get("message_id"), messaging.OUTLOOK_SYNC_ROUTING_KEY, row[0]),
            )
            return {
                "id": row[0],
                "profile_name": profile_name,
                "status": row[1],
                "created_at": row[2].isoformat(),
                "message_id": outbox.get("message_id"),
            }


def list_outlook_sync_requests(*, limit: int = 20, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT r.id::text, p.name, r.status, r.requested_by, r.claimed_by,
                       r.error, r.result, r.created_at, r.updated_at,
                       r.broker_message_id, r.correlation_id, r.routing_key,
                       r.queued_at, r.broker_delivery_count
                FROM outlook_sync_requests r
                JOIN mail_profiles p ON p.id = r.profile_id
                ORDER BY r.created_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 100)),),
            )
            return [
                {
                    "id": row[0],
                    "profile_name": row[1],
                    "status": row[2],
                    "requested_by": row[3],
                    "claimed_by": row[4],
                    "error": row[5],
                    "result": row[6],
                    "created_at": row[7].isoformat(),
                    "updated_at": row[8].isoformat(),
                    "broker_message_id": row[9],
                    "correlation_id": row[10],
                    "routing_key": row[11],
                    "queued_at": row[12].isoformat() if row[12] else None,
                    "broker_delivery_count": int(row[13] or 0),
                }
                for row in cur.fetchall()
            ]


def claim_outlook_sync_request(*, host_id: str = "default", url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                WITH stale_claimed AS (
                    SELECT r.id
                    FROM outlook_sync_requests r
                    JOIN mail_profiles p ON p.id = r.profile_id
                    WHERE r.status = 'claimed'
                      AND p.enabled
                      AND p.source_type = 'outlook_com'
                      AND (
                          r.claimed_by = %s
                          OR NOT EXISTS (
                              SELECT 1
                              FROM outlook_host_state h
                              WHERE h.host_id = r.claimed_by
                                AND h.status = 'running'
                                AND h.heartbeat_at >= now() - interval '120 seconds'
                          )
                      )
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE outlook_sync_requests r
                SET status = 'pending',
                    claimed_by = NULL,
                    claimed_at = NULL,
                    updated_at = now(),
                    result = COALESCE(r.result, '{}'::jsonb) || jsonb_build_object('requeued_from_stale_claim', true)
                FROM stale_claimed
                WHERE r.id = stale_claimed.id
                """,
                (host_id,),
            )
            cur.execute(
                """
                WITH request AS (
                    SELECT r.id, p.name
                    FROM outlook_sync_requests r
                    JOIN mail_profiles p ON p.id = r.profile_id
                    WHERE r.status = 'pending'
                      AND p.enabled
                      AND p.source_type = 'outlook_com'
                    ORDER BY r.created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                UPDATE outlook_sync_requests r
                SET status = 'claimed',
                    claimed_by = %s,
                    claimed_at = now(),
                    updated_at = now()
                FROM request
                WHERE r.id = request.id
                RETURNING r.id::text, request.name, r.status
                """,
                (host_id,),
            )
            row = cur.fetchone()
            if row:
                return {"id": row[0], "profile_name": row[1], "status": row[2]}

            cur.execute(
                """
                SELECT id, name
                FROM mail_profiles
                WHERE source_type = 'outlook_com'
                  AND enabled
                  AND sync_enabled
                  AND (next_sync_at IS NULL OR next_sync_at <= now())
                ORDER BY COALESCE(next_sync_at, now())
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """
            )
            due = cur.fetchone()
            if due is None:
                return None
            cur.execute(
                """
                INSERT INTO outlook_sync_requests (profile_id, requested_by, status, claimed_by, claimed_at)
                VALUES (%s, 'schedule', 'claimed', %s, now())
                RETURNING id::text, status
                """,
                (due[0], host_id),
            )
            row = cur.fetchone()
            return {"id": row[0], "profile_name": due[1], "status": row[1]}


def enqueue_due_outlook_sync_commands(
    *,
    limit: int = 10,
    requested_by: str = "scheduler",
    url: str | None = None,
) -> dict[str, Any]:
    capped_limit = max(1, min(limit, 100))
    psycopg = _load_psycopg()
    requests: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id, name
                FROM mail_profiles
                WHERE source_type = 'outlook_com'
                  AND enabled
                  AND sync_enabled
                  AND (next_sync_at IS NULL OR next_sync_at <= now())
                  AND NOT EXISTS (
                      SELECT 1
                      FROM outlook_sync_requests active
                      WHERE active.profile_id = mail_profiles.id
                        AND active.status IN ('pending', 'claimed', 'running')
                  )
                ORDER BY COALESCE(next_sync_at, now())
                LIMIT %s
                FOR UPDATE SKIP LOCKED
                """,
                (capped_limit,),
            )
            due_rows = cur.fetchall()
            for profile_id, profile_name in due_rows:
                cur.execute(
                    """
                    INSERT INTO outlook_sync_requests (profile_id, requested_by, status)
                    VALUES (%s, %s, 'pending')
                    RETURNING id::text, status
                    """,
                    (profile_id, requested_by),
                )
                row = cur.fetchone()
                outbox = _enqueue_message_outbox_with_cursor(
                    cur,
                    exchange=messaging.COMMANDS_EXCHANGE,
                    routing_key=messaging.OUTLOOK_SYNC_ROUTING_KEY,
                    message_type="flux.mail.outlook.sync",
                    payload={"request_id": row[0], "profile_name": profile_name, "requested_by": requested_by},
                    aggregate_type="outlook_sync_requests",
                    aggregate_id=row[0],
                )
                cur.execute(
                    """
                    UPDATE outlook_sync_requests
                    SET broker_message_id = %s,
                        correlation_id = %s,
                        routing_key = %s,
                        queued_at = COALESCE(queued_at, now()),
                        updated_at = now()
                    WHERE id = %s
                    """,
                    (outbox.get("message_id"), outbox.get("message_id"), messaging.OUTLOOK_SYNC_ROUTING_KEY, row[0]),
                )
                requests.append({"id": row[0], "profile_name": profile_name, "status": row[1], "message_id": outbox.get("message_id")})
    return {"queued": len(requests), "requests": requests}


def requeue_stale_pending_outlook_sync_requests(
    *,
    min_age_seconds: int = 300,
    limit: int = 20,
    requested_by: str = "outlook-host-repair",
    url: str | None = None,
) -> dict[str, Any]:
    capped_limit = max(1, min(limit, 100))
    min_age = max(1, int(min_age_seconds or 1))
    psycopg = _load_psycopg()
    requests: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT r.id::text, p.name, r.requested_by
                FROM outlook_sync_requests r
                JOIN mail_profiles p ON p.id = r.profile_id
                WHERE r.status = 'pending'
                  AND p.enabled
                  AND p.source_type = 'outlook_com'
                  AND COALESCE(r.broker_delivery_count, 0) = 0
                  AND COALESCE(r.queued_at, r.created_at) <= now() - make_interval(secs => %s)
                ORDER BY COALESCE(r.queued_at, r.created_at), r.created_at
                LIMIT %s
                FOR UPDATE SKIP LOCKED
                """,
                (min_age, capped_limit),
            )
            stale_rows = cur.fetchall()
            for request_id, profile_name, original_requested_by in stale_rows:
                outbox = _enqueue_message_outbox_with_cursor(
                    cur,
                    exchange=messaging.COMMANDS_EXCHANGE,
                    routing_key=messaging.OUTLOOK_SYNC_ROUTING_KEY,
                    message_type="flux.mail.outlook.sync",
                    payload={
                        "request_id": request_id,
                        "profile_name": profile_name,
                        "requested_by": requested_by,
                        "requeued_from_stale_pending": True,
                    },
                    aggregate_type="outlook_sync_requests",
                    aggregate_id=request_id,
                )
                message_id = outbox.get("message_id")
                cur.execute(
                    """
                    UPDATE outlook_sync_requests
                    SET broker_message_id = %s,
                        correlation_id = %s,
                        routing_key = %s,
                        queued_at = now(),
                        updated_at = now(),
                        result = COALESCE(result, '{}'::jsonb)
                            || jsonb_build_object(
                                'requeued_from_stale_pending', true,
                                'previous_requested_by', %s::text,
                                'requeued_by', %s::text
                            )
                    WHERE id = %s
                    """,
                    (
                        message_id,
                        message_id,
                        messaging.OUTLOOK_SYNC_ROUTING_KEY,
                        original_requested_by,
                        requested_by,
                        request_id,
                    ),
                )
                requests.append({"id": request_id, "profile_name": profile_name, "message_id": message_id})
    return {"requeued": len(requests), "requests": requests}


def claim_outlook_sync_request_by_id(
    *,
    request_id: str,
    host_id: str = "default",
    broker_message_id: str | None = None,
    url: str | None = None,
) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                WITH request AS (
                    SELECT r.id, p.name
                    FROM outlook_sync_requests r
                    JOIN mail_profiles p ON p.id = r.profile_id
                    WHERE r.id = %s
                      AND r.status IN ('pending', 'claimed')
                      AND p.enabled
                      AND p.source_type = 'outlook_com'
                      AND (%s::text IS NULL OR r.broker_message_id IS NULL OR r.broker_message_id = %s::text)
                    LIMIT 1
                    FOR UPDATE
                )
                UPDATE outlook_sync_requests r
                SET status = 'claimed',
                    claimed_by = %s,
                    claimed_at = now(),
                    broker_message_id = COALESCE(%s::text, r.broker_message_id),
                    broker_delivery_count = r.broker_delivery_count + 1,
                    updated_at = now()
                FROM request
                WHERE r.id = request.id
                RETURNING r.id::text, request.name, r.status
                """,
                (request_id, broker_message_id, broker_message_id, host_id, broker_message_id),
            )
            row = cur.fetchone()
            if row:
                return {"id": row[0], "profile_name": row[1], "status": row[2]}
            return None


def cancel_outlook_sync_request(
    *,
    request_id: str,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT r.id::text, p.name, r.status
                FROM outlook_sync_requests r
                JOIN mail_profiles p ON p.id = r.profile_id
                WHERE r.id = %s
                FOR UPDATE
                """,
                (request_id,),
            )
            row = cur.fetchone()
            if row is None:
                return {
                    "id": request_id,
                    "status": "not_found",
                    "cancelled": False,
                    "error": "Outlook sync request not found.",
                }

            request_id_text, profile_name, status = row
            if status in {"claimed", "running"}:
                return {
                    "id": request_id_text,
                    "profile_name": profile_name,
                    "status": status,
                    "cancelled": False,
                    "error": "Outlook sync request is already claimed and cannot be cancelled mid-execution.",
                }
            if status != "pending":
                return {
                    "id": request_id_text,
                    "profile_name": profile_name,
                    "status": status,
                    "cancelled": False,
                    "error": f"Outlook sync request is already {status}.",
                }

            cur.execute(
                """
                UPDATE outlook_sync_requests r
                SET status = 'cancelled',
                    cancelled_by = %s,
                    cancelled_at = now(),
                    completed_at = now(),
                    result = %s::jsonb,
                    updated_at = now()
                FROM mail_profiles p
                WHERE r.id = %s
                  AND p.id = r.profile_id
                RETURNING r.id::text, p.name, r.status
                """,
                (actor, _json({"cancelled_by": actor}), request_id),
            )
            cancelled = cur.fetchone()
            return {
                "id": cancelled[0],
                "profile_name": cancelled[1],
                "status": cancelled[2],
                "cancelled": True,
            }


def complete_outlook_sync_request(
    *,
    request_id: str,
    profile_name: str,
    status: str,
    result: dict[str, Any] | None = None,
    error: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE outlook_sync_requests
                SET status = %s,
                    completed_at = now(),
                    error = %s,
                    result = %s::jsonb,
                    updated_at = now()
                WHERE id = %s
                RETURNING id::text, status
                """,
                (status, error, _json(result or {}), request_id),
            )
            row = cur.fetchone()
            cur.execute(
                """
                UPDATE mail_profiles
                SET last_sync_at = now(),
                    next_sync_at = CASE
                        WHEN sync_enabled THEN now() + make_interval(secs => sync_interval_seconds)
                        ELSE NULL
                    END,
                    updated_at = now()
                WHERE name = %s
                """,
                (profile_name,),
            )
            return {"id": row[0], "profile_name": profile_name, "status": row[1]}


def record_outlook_host_heartbeat(
    *,
    host_id: str = "default",
    status: str,
    metadata: dict[str, Any] | None = None,
    process_id: int | None = None,
    last_error: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO outlook_host_state (host_id, status, process_id, last_error, metadata)
                VALUES (%s, %s, %s, %s, %s::jsonb)
                ON CONFLICT (host_id) DO UPDATE SET
                    status = EXCLUDED.status,
                    process_id = EXCLUDED.process_id,
                    last_error = EXCLUDED.last_error,
                    metadata = EXCLUDED.metadata,
                    heartbeat_at = now(),
                    updated_at = now()
                RETURNING host_id, status, process_id, heartbeat_at, last_error, metadata
                """,
                (host_id, status, process_id, last_error, _json(metadata or {})),
            )
            row = cur.fetchone()
            return {
                "host_id": row[0],
                "status": row[1],
                "process_id": row[2],
                "heartbeat_at": row[3].isoformat(),
                "last_error": row[4],
                "metadata": row[5],
            }


def get_outlook_host_state(*, host_id: str = "default", url: str | None = None) -> dict[str, Any] | None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT host_id, status, process_id, heartbeat_at, last_error, metadata, updated_at
                FROM outlook_host_state
                WHERE host_id = %s
                """,
                (host_id,),
            )
            row = cur.fetchone()
            if row is None:
                return None
            return {
                "host_id": row[0],
                "status": row[1],
                "process_id": row[2],
                "heartbeat_at": row[3].isoformat(),
                "last_error": row[4],
                "metadata": row[5],
                "updated_at": row[6].isoformat(),
            }


MAX_CORPUS_SYNC_BATCH_PATHS = 5000


def _corpus_sync_payload_paths(payload: dict[str, Any]) -> list[str]:
    paths: list[str] = []
    raw_paths = payload.get("paths")
    if isinstance(raw_paths, list):
        paths.extend(str(item).strip() for item in raw_paths if str(item).strip())
    raw_path = str(payload.get("path") or "").strip()
    if raw_path:
        paths.append(raw_path)
    return _dedupe_preserving_order(paths)


def _merge_corpus_sync_payload(existing: dict[str, Any], incoming: dict[str, Any]) -> dict[str, Any]:
    merged = {key: value for key, value in {**existing, **incoming}.items() if key not in {"path", "paths"}}
    merged["root_name"] = str(incoming.get("root_name") or existing.get("root_name") or "").strip()
    merged["reason"] = str(incoming.get("reason") or existing.get("reason") or "manual_sync")
    existing_paths = _corpus_sync_payload_paths(existing)
    incoming_paths = _corpus_sync_payload_paths(incoming)
    if not incoming_paths:
        return merged
    if not existing_paths and ("path" not in existing and "paths" not in existing):
        return merged
    paths = _dedupe_preserving_order([*existing_paths, *incoming_paths])
    if len(paths) > MAX_CORPUS_SYNC_BATCH_PATHS:
        merged["paths_truncated_to_root_sync"] = True
        return merged
    if len(paths) == 1:
        merged["path"] = paths[0]
    else:
        merged["paths"] = paths
    return merged


def enqueue_corpus_sync_job(
    *,
    root_name: str,
    reason: str = "manual_sync",
    payload: dict[str, Any] | None = None,
    path: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    root = str(root_name or "").strip()
    if not root:
        raise ValueError("root_name is required")
    job_payload = {"root_name": root, "reason": str(reason or "manual_sync")}
    job_payload.update(payload or {})
    clean_path = str(path).strip() if path is not None else ""
    if clean_path:
        job_payload["path"] = clean_path
    schedule = _job_schedule_metadata("corpus_sync_root")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, status, payload
                FROM capture_jobs
                WHERE job_type = 'corpus_sync_root'
                  AND payload->>'root_name' = %s
                  AND status = 'running'
                ORDER BY updated_at DESC
                LIMIT 1
                """,
                (root,),
            )
            running = cur.fetchone()
            cur.execute(
                """
                SELECT id::text, status, payload
                FROM capture_jobs
                WHERE job_type = 'corpus_sync_root'
                  AND payload->>'root_name' = %s
                  AND status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy')
                  -- legacy cancellable statuses: status IN ('pending', 'retrying_locked', 'retrying_vss_failed')
                ORDER BY updated_at DESC
                LIMIT 1
                """,
                (root,),
            )
            pending = cur.fetchone()
            if pending:
                existing_payload = pending[2] if len(pending) > 2 and isinstance(pending[2], dict) else {}
                update_payload = _merge_corpus_sync_payload(existing_payload, job_payload)
                cur.execute(
                    """
                    UPDATE capture_jobs
                    SET payload = %s::jsonb,
                        priority = GREATEST(priority, %s),
                        time_budget_seconds = GREATEST(time_budget_seconds, %s),
                        telemetry = telemetry || %s::jsonb,
                        updated_at = now()
                    WHERE id = %s
                    RETURNING id::text, status
                    """,
                    (
                        _json(update_payload),
                        schedule["priority"],
                        schedule["time_budget_seconds"],
                        _json({"stage": "queued", "root_name": root, "paths_queued": len(_corpus_sync_payload_paths(update_payload))}),
                        pending[0],
                    ),
                )
                row = cur.fetchone() or pending
                outbox = _enqueue_capture_job_command_with_cursor(
                    cur,
                    job_id=row[0],
                    job_type="corpus_sync_root",
                    payload=update_payload,
                    job_family=schedule["job_family"],
                    resource_class=schedule["resource_class"],
                )
                return {
                    "job_id": row[0],
                    "status": row[1],
                    "root_name": root,
                    "deduped": True,
                    "reused": False,
                    "message_id": (outbox or {}).get("message_id"),
                    "routing_key": (outbox or {}).get("routing_key"),
                    "queued": bool(outbox and not outbox.get("deduped")),
                }
            queued = _enqueue_unique_capture_job_with_cursor(
                cur,
                job_type="corpus_sync_root",
                payload=job_payload,
                job_family=schedule["job_family"],
                resource_class=schedule["resource_class"],
                priority=schedule["priority"],
                time_budget_seconds=schedule["time_budget_seconds"],
                telemetry={"stage": "queued", "root_name": root},
                audit_details={"job_type": "corpus_sync_root", "root_name": root, "reason": job_payload["reason"]},
            )
            result = {
                "job_id": queued["job_id"],
                "status": queued["status"],
                "root_name": root,
                "deduped": queued["deduped"],
                "reused": queued["reused"],
            }
            if running and not queued["deduped"]:
                result["followup"] = True
            return result


def enqueue_corpus_sync_path_batch_jobs(
    *,
    root_name: str,
    paths: Iterable[str],
    reason: str = "manual_sync",
    payload: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    root = str(root_name or "").strip()
    if not root:
        raise ValueError("root_name is required")
    clean_paths = _dedupe_preserving_order([str(path).strip() for path in paths if str(path).strip()])
    if not clean_paths:
        return {"root_name": root, "jobs": [], "count": 0, "path_count": 0}
    schedule = _job_schedule_metadata("corpus_sync_root")
    batch_size = MAX_CORPUS_SYNC_BATCH_PATHS
    batches = [clean_paths[index : index + batch_size] for index in range(0, len(clean_paths), batch_size)]
    psycopg = _load_psycopg()
    jobs: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            for batch_index, batch_paths in enumerate(batches, start=1):
                job_payload = {
                    "root_name": root,
                    "reason": str(reason or "manual_sync"),
                    **(payload or {}),
                    "paths": batch_paths,
                    "paths_total": len(clean_paths),
                    "path_batch_index": batch_index,
                    "path_batch_total": len(batches),
                }
                queued = _enqueue_unique_capture_job_with_cursor(
                    cur,
                    job_type="corpus_sync_root",
                    payload=job_payload,
                    job_family=schedule["job_family"],
                    resource_class=schedule["resource_class"],
                    priority=schedule["priority"],
                    time_budget_seconds=schedule["time_budget_seconds"],
                    telemetry={
                        "stage": "queued",
                        "root_name": root,
                        "paths_total": len(clean_paths),
                        "paths_queued": len(batch_paths),
                        "path_batch_index": batch_index,
                        "path_batch_total": len(batches),
                    },
                    audit_details={
                        "job_type": "corpus_sync_root",
                        "root_name": root,
                        "reason": job_payload["reason"],
                        "paths_queued": len(batch_paths),
                        "path_batch_index": batch_index,
                        "path_batch_total": len(batches),
                    },
                )
                jobs.append(
                    {
                        "job_id": queued["job_id"],
                        "status": queued["status"],
                        "root_name": root,
                        "deduped": queued["deduped"],
                        "reused": queued["reused"],
                        "path_count": len(batch_paths),
                        "path_batch_index": batch_index,
                        "path_batch_total": len(batches),
                    }
                )
    return {"root_name": root, "jobs": jobs, "count": len(jobs), "path_count": len(clean_paths)}


def update_corpus_job_progress(
    *,
    job_id: str,
    telemetry: dict[str, Any],
    last_error: str | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET telemetry = telemetry || %s::jsonb,
                    last_error = COALESCE(%s, last_error),
                    progress_heartbeat_at = now(),
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (_json(telemetry or {}), _postgres_text_or_none(last_error), job_id),
            )


def heartbeat_corpus_job(
    *,
    job_id: str,
    telemetry: dict[str, Any],
    last_error: str | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET telemetry = telemetry || %s::jsonb,
                    last_error = COALESCE(%s, last_error),
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (_json(telemetry or {}), _postgres_text_or_none(last_error), job_id),
            )


_ACTIVE_CAPTURE_JOB_STATUSES = ("pending", "running", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy")
_ACTIVE_COMMAND_OUTBOX_STATUSES = ("pending", "publishing", "failed")


def _capture_job_has_active_command_outbox_with_cursor(
    cur: Any,
    *,
    job_id: str,
    message_id: str,
    routing_key: str,
) -> bool:
    cur.execute(
        """
        SELECT EXISTS (
            SELECT 1
            FROM message_outbox active_outbox
            WHERE active_outbox.aggregate_type = 'capture_jobs'
              AND active_outbox.aggregate_id = %s
              AND active_outbox.exchange = %s
              AND active_outbox.routing_key = %s
              AND active_outbox.message_id = %s
              AND active_outbox.status = ANY(%s)
        )
        """,
        (
            str(job_id),
            messaging.COMMANDS_EXCHANGE,
            str(routing_key),
            str(message_id),
            list(_ACTIVE_COMMAND_OUTBOX_STATUSES),
        ),
    )
    row = cur.fetchone()
    return bool(row and row[0])


def _enqueue_unique_capture_job_with_cursor(
    cur: Any,
    *,
    job_type: str,
    payload: dict[str, Any],
    status: str = "pending",
    last_error: str | None = None,
    next_attempt_delay_seconds: int = 0,
    job_family: str | None = None,
    resource_class: str | None = None,
    priority: int | None = None,
    time_budget_seconds: int | None = None,
    telemetry: dict[str, Any] | None = None,
    audit_details: dict[str, Any] | None = None,
) -> dict[str, Any]:
    clean_job_type = str(job_type or "").strip()
    if not clean_job_type:
        raise ValueError("job_type is required")
    clean_payload = dict(payload or {})
    schedule = _job_schedule_metadata(clean_job_type)
    resolved_job_family = str(job_family or schedule["job_family"])
    resolved_resource_class = str(resource_class or schedule["resource_class"])
    resolved_priority = int(priority if priority is not None else schedule["priority"])
    resolved_time_budget = time_budget_seconds if time_budget_seconds is not None else schedule["time_budget_seconds"]
    clean_status = str(status or "pending").strip() or "pending"
    delay_seconds = max(0, int(next_attempt_delay_seconds or 0))
    payload_json = _json(clean_payload)
    telemetry_json = _json(telemetry or {})
    details = {"job_type": clean_job_type, **(audit_details or {})}
    cur.execute(
        """
        SELECT pg_advisory_xact_lock(hashtextextended(capture_job_identity(%s, %s::jsonb), 0))
        """,
        (clean_job_type, payload_json),
    )
    cur.execute(
        """
        SELECT id::text, status
        FROM capture_jobs
        WHERE identity_key = capture_job_identity(%s, %s::jsonb)
        FOR UPDATE
        """,
        (clean_job_type, payload_json),
    )
    existing = cur.fetchone()
    if existing is not None:
        existing_status = str(existing[1] or "unknown")
        if existing_status in _ACTIVE_CAPTURE_JOB_STATUSES:
            _enqueue_capture_job_command_with_cursor(
                cur,
                job_id=existing[0],
                job_type=clean_job_type,
                payload=clean_payload,
                job_family=resolved_job_family,
                resource_class=resolved_resource_class,
            )
            return {
                "job_id": existing[0],
                "status": existing_status,
                "created": False,
                "deduped": True,
                "reused": False,
            }
        cur.execute("DELETE FROM capture_job_tool_invocations WHERE job_id = %s", (existing[0],))
        cur.execute(
            """
            UPDATE capture_jobs
            SET job_type = %s,
                payload = %s::jsonb,
                status = %s,
                attempts = 0,
                last_error = %s,
                next_attempt_at = now() + make_interval(secs => %s),
                locked_at = NULL,
                locked_by = NULL,
                started_at = NULL,
                completed_at = NULL,
                last_duration_ms = NULL,
                progress_heartbeat_at = NULL,
                broker_message_id = NULL,
                routing_key = NULL,
                correlation_id = NULL,
                causation_id = NULL,
                queued_at = NULL,
                broker_delivery_count = 0,
                delete_requested_at = NULL,
                delete_requested_by = NULL,
                delete_reason = NULL,
                job_family = %s,
                resource_class = %s,
                priority = %s,
                time_budget_seconds = %s,
                telemetry = %s::jsonb,
                created_at = now(),
                updated_at = now()
            WHERE id = %s
            RETURNING id::text, status
            """,
            (
                clean_job_type,
                payload_json,
                clean_status,
                _postgres_text_or_none(last_error),
                delay_seconds,
                resolved_job_family,
                resolved_resource_class,
                resolved_priority,
                resolved_time_budget,
                telemetry_json,
                existing[0],
            ),
        )
        updated = cur.fetchone()
        outbox = _enqueue_capture_job_command_with_cursor(
            cur,
            job_id=updated[0],
            job_type=clean_job_type,
            payload=clean_payload,
            job_family=resolved_job_family,
            resource_class=resolved_resource_class,
            force_new_message=True,
        )
        reuse_details = {**details, "previous_status": existing_status, "status": updated[1]}
        if outbox:
            reuse_details.update(
                {
                    "message_id": outbox.get("message_id"),
                    "routing_key": outbox.get("routing_key"),
                    "command_deduped": bool(outbox.get("deduped")),
                }
            )
        cur.execute(
            """
            INSERT INTO audit_events (event_type, target_table, target_id, details)
            VALUES ('capture_job.identity_reused', 'capture_jobs', %s, %s::jsonb)
            """,
            (
                updated[0],
                _json(reuse_details),
            ),
        )
        return {
            "job_id": updated[0],
            "status": updated[1],
            "created": False,
            "deduped": False,
            "reused": True,
        }
    cur.execute(
        """
        INSERT INTO capture_jobs (
            job_type, payload, status, last_error, next_attempt_at,
            job_family, resource_class, priority, time_budget_seconds, telemetry
        )
        VALUES (%s, %s::jsonb, %s, %s, now() + make_interval(secs => %s), %s, %s, %s, %s, %s::jsonb)
        RETURNING id::text, status
        """,
        (
            clean_job_type,
            payload_json,
            clean_status,
            _postgres_text_or_none(last_error),
            delay_seconds,
            resolved_job_family,
            resolved_resource_class,
            resolved_priority,
            resolved_time_budget,
            telemetry_json,
        ),
    )
    inserted = cur.fetchone()
    cur.execute(
        """
        INSERT INTO audit_events (event_type, target_table, target_id, details)
        VALUES ('capture_job.queued', 'capture_jobs', %s, %s::jsonb)
        """,
        (inserted[0], _json(details)),
    )
    _enqueue_capture_job_command_with_cursor(
        cur,
        job_id=inserted[0],
        job_type=clean_job_type,
        payload=clean_payload,
        job_family=resolved_job_family,
        resource_class=resolved_resource_class,
    )
    return {
        "job_id": inserted[0],
        "status": inserted[1],
        "created": True,
        "deduped": False,
        "reused": False,
    }


def _enqueue_capture_job_command_with_cursor(
    cur: Any,
    *,
    job_id: str,
    job_type: str,
    payload: dict[str, Any],
    job_family: str,
    resource_class: str,
    host_agent_route: bool | None = None,
    force_new_message: bool = False,
) -> dict[str, Any] | None:
    effective_host_agent_route = (
        _capture_job_payload_targets_host_agent_root(cur, payload=payload)
        if host_agent_route is None
        else bool(host_agent_route)
    )
    routing_key, message_type = _capture_job_command_contract(job_type, host_agent_route=effective_host_agent_route)
    if not routing_key:
        return None
    cur.execute(
        """
        SELECT id::text, status, broker_message_id, routing_key
        FROM capture_jobs
        WHERE id = %s
        FOR UPDATE
        """,
        (job_id,),
    )
    existing = cur.fetchone()
    if existing is not None:
        existing_status = str(existing[1] or "")
        existing_message_id = str(existing[2] or "").strip() if len(existing) > 2 else ""
        existing_routing_key = str(existing[3] or "").strip() if len(existing) > 3 else ""
        if (
            not force_new_message
            and existing_status in _ACTIVE_CAPTURE_JOB_STATUSES
            and existing_message_id
            and existing_routing_key == routing_key
            and _capture_job_has_active_command_outbox_with_cursor(
                cur,
                job_id=str(job_id),
                message_id=existing_message_id,
                routing_key=routing_key,
            )
        ):
            return {
                "id": None,
                "message_id": existing_message_id,
                "status": "deduped",
                "deduped": True,
                "routing_key": routing_key,
            }
    command_payload: dict[str, Any] = {
        "job_id": str(job_id),
        "job_type": str(job_type),
        "job_family": str(job_family),
        "resource_class": str(resource_class),
    }
    for key in ("root_name", "path", "reason", "path_batch_index", "path_batch_total"):
        if key in payload:
            command_payload[key] = payload[key]
    if isinstance(payload.get("paths"), list):
        command_payload["paths_count"] = len(payload["paths"])
    outbox = _enqueue_message_outbox_with_cursor(
        cur,
        exchange=messaging.COMMANDS_EXCHANGE,
        routing_key=routing_key,
        message_type=message_type,
        payload=command_payload,
        aggregate_type="capture_jobs",
        aggregate_id=str(job_id),
    )
    cur.execute(
        """
        UPDATE capture_jobs
        SET routing_key = %s,
            queued_at = now(),
            correlation_id = COALESCE(correlation_id, %s),
            broker_message_id = %s,
            updated_at = now()
        WHERE id = %s
        """,
        (routing_key, outbox.get("message_id"), outbox.get("message_id"), job_id),
    )
    outbox["deduped"] = False
    outbox["routing_key"] = routing_key
    return outbox


def _capture_job_payload_targets_host_agent_root(cur: Any, *, payload: dict[str, Any]) -> bool:
    root_name = str((payload or {}).get("root_name") or "").strip()
    if not root_name:
        return False
    cur.execute(
        """
        SELECT EXISTS (
            SELECT 1
            FROM monitored_roots r
            WHERE r.name = %s
              AND r.metadata->>'host_access' = 'host_agent'
        )
        """,
        (root_name,),
    )
    row = cur.fetchone()
    return bool(row and row[0])


def _capture_job_command_contract(job_type: str, *, host_agent_route: bool = False) -> tuple[str | None, str | None]:
    clean = str(job_type or "").strip()
    if clean == "search_index_sync":
        return messaging.SEARCH_INDEX_PROCESS_ROUTING_KEY, "flux.search_index.process"
    if clean.startswith("corpus_"):
        if host_agent_route:
            return messaging.CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY, "flux.corpus.host_agent.process"
        return messaging.CORPUS_PROCESS_ROUTING_KEY, "flux.corpus.process"
    return None, None


def repair_capture_command_storm(
    *,
    apply: bool = False,
    confirm: str | None = None,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    if apply and confirm != "broker-claim-storm":
        raise ValueError("applying this repair requires --confirm broker-claim-storm")
    capped_limit = max(1, min(int(limit or 1000), 10000))
    command_routes = [
        messaging.CORPUS_PROCESS_ROUTING_KEY,
        messaging.CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY,
        messaging.SEARCH_INDEX_PROCESS_ROUTING_KEY,
    ]
    repair_statuses = ["pending", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy"]
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            jobs = _capture_command_storm_jobs_with_cursor(
                cur,
                command_routes=command_routes,
                repair_statuses=repair_statuses,
                limit=capped_limit,
            )
            result: dict[str, Any] = {
                "applied": bool(apply),
                "affected_jobs": len(jobs),
                "deleted_unpublished_outbox": 0,
                "reset_jobs": 0,
                "enqueued": 0,
                "jobs": jobs,
            }
            if not apply or not jobs:
                return result
            job_ids = [job["job_id"] for job in jobs]
            cur.execute(
                """
                DELETE FROM message_outbox
                WHERE aggregate_type = 'capture_jobs'
                  AND aggregate_id = ANY(%s)
                  AND exchange = %s
                  AND routing_key = ANY(%s)
                  AND status IN ('pending', 'publishing', 'failed')
                """,
                (job_ids, messaging.COMMANDS_EXCHANGE, command_routes),
            )
            result["deleted_unpublished_outbox"] = int(cur.rowcount or 0)
            cur.execute(
                """
                UPDATE capture_jobs
                SET broker_message_id = NULL,
                    correlation_id = NULL,
                    routing_key = NULL,
                    queued_at = NULL,
                    updated_at = now()
                WHERE id::text = ANY(%s)
                  AND status = ANY(%s)
                """,
                (job_ids, repair_statuses),
            )
            result["reset_jobs"] = int(cur.rowcount or 0)
            enqueued = 0
            repaired_jobs: list[dict[str, Any]] = []
            for job in jobs:
                outbox = _enqueue_capture_job_command_with_cursor(
                    cur,
                    job_id=job["job_id"],
                    job_type=job["job_type"],
                    payload=job["payload"],
                    job_family=job["job_family"],
                    resource_class=job["resource_class"],
                    host_agent_route=bool(job["host_agent_route"]),
                )
                if outbox and not outbox.get("deduped"):
                    enqueued += 1
                repaired_jobs.append(
                    {
                        **job,
                        "message_id": (outbox or {}).get("message_id"),
                        "deduped": bool(outbox and outbox.get("deduped")),
                    }
                )
            result["enqueued"] = enqueued
            result["jobs"] = repaired_jobs
            return result


def repair_stranded_capture_commands(
    *,
    apply: bool = False,
    confirm: str | None = None,
    job_id: str | None = None,
    root_name: str | None = None,
    family: str | None = None,
    min_age_seconds: int = 300,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    if apply and confirm != "stranded-capture-commands":
        raise ValueError("applying this repair requires --confirm stranded-capture-commands")
    capped_limit = max(1, min(int(limit or 1000), 10000))
    bounded_min_age = max(0, int(min_age_seconds or 0))
    command_routes = [
        messaging.CORPUS_PROCESS_ROUTING_KEY,
        messaging.CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY,
        messaging.SEARCH_INDEX_PROCESS_ROUTING_KEY,
    ]
    repair_statuses = ["pending", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy"]
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            jobs = _stranded_capture_command_jobs_with_cursor(
                cur,
                command_routes=command_routes,
                repair_statuses=repair_statuses,
                min_age_seconds=bounded_min_age,
                limit=capped_limit,
                job_id=job_id,
                root_name=root_name,
                family=family,
            )
            result: dict[str, Any] = {
                "applied": bool(apply),
                "affected_jobs": len(jobs),
                "reset_jobs": 0,
                "enqueued": 0,
                "jobs": jobs,
            }
            if not apply or not jobs:
                return result
            job_ids = [job["job_id"] for job in jobs]
            cur.execute(
                """
                UPDATE capture_jobs
                SET broker_message_id = NULL,
                    correlation_id = NULL,
                    causation_id = NULL,
                    routing_key = NULL,
                    queued_at = NULL,
                    broker_delivery_count = 0,
                    updated_at = now()
                WHERE id::text = ANY(%s)
                  AND status = ANY(%s)
                """,
                (job_ids, repair_statuses),
            )
            result["reset_jobs"] = int(cur.rowcount or 0)
            enqueued = 0
            repaired_jobs: list[dict[str, Any]] = []
            for job in jobs:
                outbox = _enqueue_capture_job_command_with_cursor(
                    cur,
                    job_id=job["job_id"],
                    job_type=job["job_type"],
                    payload=job["payload"],
                    job_family=job["job_family"],
                    resource_class=job["resource_class"],
                    host_agent_route=bool(job["host_agent_route"]),
                    force_new_message=True,
                )
                if outbox and not outbox.get("deduped"):
                    enqueued += 1
                repaired_jobs.append(
                    {
                        **job,
                        "message_id": (outbox or {}).get("message_id"),
                        "deduped": bool(outbox and outbox.get("deduped")),
                    }
                )
            result["enqueued"] = enqueued
            result["jobs"] = repaired_jobs
            return result


def list_stranded_capture_commands(
    *,
    job_id: str | None = None,
    root_name: str | None = None,
    family: str | None = None,
    min_age_seconds: int = 300,
    limit: int = 1000,
    url: str | None = None,
) -> list[dict[str, Any]]:
    capped_limit = max(1, min(int(limit or 1000), 10000))
    bounded_min_age = max(0, int(min_age_seconds or 0))
    command_routes = [
        messaging.CORPUS_PROCESS_ROUTING_KEY,
        messaging.CORPUS_HOST_AGENT_PROCESS_ROUTING_KEY,
        messaging.SEARCH_INDEX_PROCESS_ROUTING_KEY,
    ]
    repair_statuses = ["pending", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy"]
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            jobs = _stranded_capture_command_jobs_with_cursor(
                cur,
                command_routes=command_routes,
                repair_statuses=repair_statuses,
                min_age_seconds=bounded_min_age,
                limit=capped_limit,
                job_id=job_id,
                root_name=root_name,
                family=family,
                for_update=False,
            )
    rows: list[dict[str, Any]] = []
    for job in jobs:
        payload = job.get("payload") if isinstance(job.get("payload"), dict) else {}
        paths = payload.get("paths") if isinstance(payload.get("paths"), list) else []
        rows.append(
            {
                "id": job.get("job_id"),
                "job_id": job.get("job_id"),
                "job_type": job.get("job_type"),
                "job_family": job.get("job_family"),
                "resource_class": job.get("resource_class"),
                "status": "stranded_command",
                "job_status": job.get("status"),
                "root_name": payload.get("root_name"),
                "paths_count": len(paths),
                "age_seconds": job.get("age_seconds"),
                "broker_message_id": job.get("broker_message_id"),
                "routing_key": job.get("routing_key"),
            }
        )
    return rows


def _stranded_capture_command_jobs_with_cursor(
    cur: Any,
    *,
    command_routes: list[str],
    repair_statuses: list[str],
    min_age_seconds: int,
    limit: int,
    job_id: str | None = None,
    root_name: str | None = None,
    family: str | None = None,
    for_update: bool = True,
) -> list[dict[str, Any]]:
    filters = ""
    params: list[Any] = [
        repair_statuses,
        command_routes,
        messaging.COMMANDS_EXCHANGE,
        list(_ACTIVE_COMMAND_OUTBOX_STATUSES),
        max(0, int(min_age_seconds or 0)),
    ]
    if job_id:
        filters += " AND j.id::text = %s"
        params.append(str(job_id))
    if root_name:
        filters += " AND j.payload->>'root_name' = %s"
        params.append(str(root_name))
    if family:
        filters += " AND j.job_family = %s"
        params.append(str(family))
    params.append(max(1, min(int(limit or 1000), 10000)))
    lock_clause = "FOR UPDATE OF j" if for_update else ""
    cur.execute(
        f"""
        SELECT j.id::text,
               j.job_type,
               j.payload,
               j.job_family,
               j.resource_class,
               j.status,
               EXISTS (
                   SELECT 1
                   FROM monitored_roots r
                   WHERE r.name = j.payload->>'root_name'
                     AND r.metadata->>'host_access' = 'host_agent'
               ) AS host_agent_route,
               j.broker_message_id,
               j.routing_key,
               EXTRACT(EPOCH FROM (now() - j.updated_at))::integer AS age_seconds
        FROM capture_jobs j
        WHERE (j.job_type LIKE 'corpus_%%' OR j.job_type = 'search_index_sync')
          AND j.status = ANY(%s)
          AND j.delete_requested_at IS NULL
          AND j.next_attempt_at <= now()
          AND j.broker_message_id IS NOT NULL
          AND j.routing_key = ANY(%s)
          AND NOT EXISTS (
              SELECT 1
              FROM message_outbox active_outbox
              WHERE active_outbox.aggregate_type = 'capture_jobs'
                AND active_outbox.aggregate_id = j.id::text
                AND active_outbox.exchange = %s
                AND active_outbox.routing_key = j.routing_key
                AND active_outbox.message_id = j.broker_message_id
                AND active_outbox.status = ANY(%s)
          )
          AND j.updated_at <= now() - make_interval(secs => %s)
          {filters}
        ORDER BY j.priority DESC, j.updated_at
        LIMIT %s
        {lock_clause}
        """,
        tuple(params),
    )
    return [
        {
            "job_id": row[0],
            "job_type": row[1],
            "payload": row[2] or {},
            "job_family": row[3],
            "resource_class": row[4],
            "status": row[5],
            "host_agent_route": bool(row[6]),
            "broker_message_id": row[7],
            "routing_key": row[8],
            "age_seconds": int(row[9] or 0) if len(row) > 9 and row[9] is not None else None,
        }
        for row in cur.fetchall()
    ]


def _capture_command_storm_jobs_with_cursor(
    cur: Any,
    *,
    command_routes: list[str],
    repair_statuses: list[str],
    limit: int,
) -> list[dict[str, Any]]:
    cur.execute(
        """
        WITH duplicate_commands AS (
            SELECT o.aggregate_id,
                   count(*)::integer AS duplicate_outbox_rows,
                   count(*) FILTER (WHERE o.status IN ('pending', 'publishing', 'failed'))::integer AS unpublished_outbox_rows,
                   max(o.created_at) AS newest_outbox_at
            FROM message_outbox o
            JOIN capture_jobs j ON j.id::text = o.aggregate_id
            WHERE o.aggregate_type = 'capture_jobs'
              AND o.exchange = %s
              AND o.routing_key = ANY(%s)
              AND j.status = ANY(%s)
              AND (j.job_type LIKE 'corpus_%%' OR j.job_type = 'search_index_sync')
              AND j.delete_requested_at IS NULL
            GROUP BY o.aggregate_id
            HAVING count(*) > 1
            ORDER BY max(o.created_at) DESC
            LIMIT %s
        )
        SELECT j.id::text,
               j.job_type,
               j.payload,
               j.job_family,
               j.resource_class,
               j.status,
               EXISTS (
                   SELECT 1
                   FROM monitored_roots r
                   WHERE r.name = j.payload->>'root_name'
                     AND r.metadata->>'host_access' = 'host_agent'
               ) AS host_agent_route,
               d.duplicate_outbox_rows,
               d.unpublished_outbox_rows
        FROM duplicate_commands d
        JOIN capture_jobs j ON j.id::text = d.aggregate_id
        ORDER BY d.newest_outbox_at DESC
        FOR UPDATE OF j
        """,
        (messaging.COMMANDS_EXCHANGE, command_routes, repair_statuses, limit),
    )
    return [
        {
            "job_id": row[0],
            "job_type": row[1],
            "payload": row[2] or {},
            "job_family": row[3],
            "resource_class": row[4],
            "status": row[5],
            "host_agent_route": bool(row[6]),
            "duplicate_outbox_rows": int(row[7] or 0),
            "unpublished_outbox_rows": int(row[8] or 0),
        }
        for row in cur.fetchall()
    ]


def enqueue_capture_job(
    *, job_type: str, payload: dict[str, Any], url: str | None = None
) -> str:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            result = _enqueue_unique_capture_job_with_cursor(
                cur,
                job_type=job_type,
                payload=payload,
                telemetry={"stage": "queued"},
                audit_details={"job_type": job_type},
            )
            return result["job_id"]


def _normalize_metadata_only_requeue_extensions(extensions: Iterable[str] | None) -> list[str]:
    normalized: list[str] = []
    seen: set[str] = set()
    for extension in extensions or ():
        value = str(extension or "").strip().lower()
        if not value:
            raise ValueError("metadata-only requeue extensions must be non-empty")
        if not value.startswith("."):
            value = f".{value}"
        if value == "." or "/" in value or "\\" in value:
            raise ValueError(f"invalid metadata-only requeue extension: {extension}")
        if value not in seen:
            seen.add(value)
            normalized.append(value)
    return normalized


def _normalize_metadata_only_requeue_paths(paths: Iterable[str] | None) -> list[str]:
    normalized: list[str] = []
    seen: set[str] = set()
    for path in paths or ():
        raw_path = str(path or "").strip()
        normalized_path = raw_path.replace("\\", "/")
        posix_path = PurePosixPath(normalized_path)
        windows_path = PureWindowsPath(raw_path)
        if (
            not normalized_path
            or posix_path.is_absolute()
            or windows_path.is_absolute()
            or windows_path.drive
            or ".." in posix_path.parts
        ):
            raise ValueError(f"metadata-only requeue paths must be root-relative: {path}")
        parts = [part for part in posix_path.parts if part not in {"", "."}]
        if not parts:
            raise ValueError(f"metadata-only requeue paths must be root-relative: {path}")
        value = "/".join(parts)
        if value not in seen:
            seen.add(value)
            normalized.append(value)
    return normalized


def requeue_metadata_only_source_assets(
    *,
    root_name: str | None = None,
    extensions: Iterable[str] | None = None,
    paths: Iterable[str] | None = None,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = max(1, min(int(limit or 1000), 10000))
    clean_root_name = str(root_name or "").strip() or None
    normalized_extensions = _normalize_metadata_only_requeue_extensions(extensions)
    normalized_paths = _normalize_metadata_only_requeue_paths(paths)
    if normalized_paths and clean_root_name is None:
        raise ValueError("root_name is required when filtering metadata-only requeue paths")
    filters = [
        "a.deleted_at IS NULL",
        "a.extraction_status = 'metadata_only'",
        "NOT (a.metadata ? 'container_asset_id')",
    ]
    params: list[Any] = []
    if clean_root_name:
        filters.append("r.name = %s")
        params.append(clean_root_name)
    if normalized_extensions:
        filters.append("lower(a.extension) = ANY(%s)")
        params.append(normalized_extensions)
    if normalized_paths:
        filters.append("a.path = ANY(%s)")
        params.append(normalized_paths)
    params.append(row_limit)
    psycopg = _load_psycopg()
    jobs: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT a.id::text, r.name, a.path, a.file_kind
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE {" AND ".join(filters)}
                ORDER BY a.updated_at ASC, a.id ASC
                LIMIT %s
                """,
                tuple(params),
            )
            rows = cur.fetchall()
            for asset_id, row_root_name, path, file_kind in rows:
                job_type = _corpus_extract_job_type_for_file_kind(str(file_kind or ""))
                schedule = _job_schedule_metadata(job_type)
                payload = {
                    "root_name": row_root_name,
                    "path": path,
                    "reason": "metadata_only_requeue",
                }
                queued = _enqueue_unique_capture_job_with_cursor(
                    cur,
                    job_type=job_type,
                    payload=payload,
                    job_family=schedule["job_family"],
                    resource_class=schedule["resource_class"],
                    priority=schedule["priority"],
                    time_budget_seconds=schedule["time_budget_seconds"],
                    telemetry={"stage": "queued", "root_name": row_root_name, "path": path, "reason": "metadata_only_requeue"},
                    audit_details={"job_type": job_type, "root_name": row_root_name, "path": path, "reason": "metadata_only_requeue"},
                )
                job_id = queued["job_id"]
                cur.execute(
                    """
                    UPDATE source_assets
                    SET extraction_status = 'queued',
                        metadata = metadata || %s::jsonb,
                        updated_at = now()
                    WHERE id = %s
                      AND extraction_status = 'metadata_only'
                    """,
                    (
                        _json({"metadata_only_requeued": True, "metadata_only_requeue_job_id": job_id}),
                        asset_id,
                    ),
                )
                jobs.append({"job_id": job_id, "root_name": row_root_name, "path": path, "job_type": job_type, "deduped": queued["deduped"], "reused": queued["reused"]})
    return {
        "queued": len(jobs),
        "jobs": jobs,
        "limit": row_limit,
        "root_name": clean_root_name,
        "extensions": normalized_extensions,
        "paths": normalized_paths,
        "container_members_excluded": True,
    }


_REPROCESS_METADATA_DROP_KEYS = (
    "ocr",
    "asr",
    "vision",
    "vision_escalation",
    "frame_sampling",
    "video_frames",
    "thumbnail",
    "thumbnails",
    "staged_extraction",
    "staged_jobs",
    "svg",
    "svg_parse",
    "svg_raster",
    "decorative",
    "metadata_only_requeued",
    "metadata_only_requeue_job_id",
    "svg_requeued",
    "svg_requeue_reason",
    "svg_requeue_job_id",
)


def inventory_reprocess_derived_state(
    *,
    root_name: str | None = None,
    all_roots: bool = False,
    force: bool = False,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = max(1, min(int(limit or 1000), 10000))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            roots = _reprocess_scope_roots(cur, root_name=root_name, all_roots=all_roots)
            root_ids = [row["id"] for row in roots]
            root_names = [row["name"] for row in roots]
            if not roots:
                return {
                    "scope": {"all_roots": bool(all_roots), "root_name": root_name, "root_names": []},
                    "counts": {
                        "roots": 0,
                        "candidate_assets": 0,
                        "source_assets": 0,
                        "asset_chunks": 0,
                        "running_jobs": 0,
                        "pending_jobs": 0,
                        "search_index_records": 0,
                    },
                    "running_jobs": [],
                    "limit": row_limit,
                    "force": bool(force),
                }

            candidate_where = _reprocess_asset_candidate_where(force=force)
            cur.execute(
                f"""
                SELECT count(*)::integer
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.id = ANY(%s::uuid[])
                  AND {candidate_where}
                """,
                (root_ids,),
            )
            candidate_assets = int((cur.fetchone() or (0,))[0] or 0)
            cur.execute(
                """
                SELECT count(*)::integer
                FROM source_assets a
                WHERE a.root_id = ANY(%s::uuid[])
                  AND a.deleted_at IS NULL
                """,
                (root_ids,),
            )
            source_assets = int((cur.fetchone() or (0,))[0] or 0)
            cur.execute(
                """
                SELECT count(*)::integer
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                WHERE a.root_id = ANY(%s::uuid[])
                """,
                (root_ids,),
            )
            asset_chunks = int((cur.fetchone() or (0,))[0] or 0)
            cur.execute(
                """
                SELECT count(*)::integer
                FROM capture_jobs
                WHERE status IN ('pending', 'retrying', 'retrying_locked', 'retrying_vss_failed', 'failed')
                  AND (
                      (job_type LIKE 'corpus_%%' AND payload->>'root_name' = ANY(%s::text[]))
                      OR (
                          job_type = 'search_index_sync'
                          AND (payload->>'root_name' = ANY(%s::text[]) OR COALESCE(payload->>'root_name', '') = '')
                      )
                  )
                """,
                (root_names, root_names),
            )
            pending_jobs = int((cur.fetchone() or (0,))[0] or 0)
            cur.execute(
                """
                SELECT id::text, job_type, status, payload, locked_by, updated_at
                FROM capture_jobs
                WHERE status = 'running'
                  AND (
                      (job_type LIKE 'corpus_%%' AND payload->>'root_name' = ANY(%s::text[]))
                      OR (
                          job_type = 'search_index_sync'
                          AND (payload->>'root_name' = ANY(%s::text[]) OR COALESCE(payload->>'root_name', '') = '')
                      )
                  )
                ORDER BY updated_at DESC
                LIMIT 20
                """,
                (root_names, root_names),
            )
            running_jobs = [
                {
                    "id": row[0],
                    "job_type": row[1],
                    "status": row[2],
                    "payload": row[3] if isinstance(row[3], dict) else {},
                    "locked_by": row[4],
                    "updated_at": row[5].isoformat() if hasattr(row[5], "isoformat") else row[5],
                }
                for row in cur.fetchall()
            ]
            search_where, search_params = _reprocess_search_index_scope(root_names=root_names, all_roots=all_roots)
            cur.execute(
                f"""
                SELECT count(*)::integer
                FROM search_index_records
                WHERE {search_where}
                """,
                search_params,
            )
            search_index_records = int((cur.fetchone() or (0,))[0] or 0)
    return {
        "scope": {"all_roots": bool(all_roots), "root_name": root_name, "root_names": root_names},
        "counts": {
            "roots": len(roots),
            "candidate_assets": candidate_assets,
            "source_assets": source_assets,
            "asset_chunks": asset_chunks,
            "running_jobs": len(running_jobs),
            "pending_jobs": pending_jobs,
            "search_index_records": search_index_records,
        },
        "running_jobs": running_jobs,
        "limit": row_limit,
        "force": bool(force),
    }


def invalidate_reprocess_derived_state(
    *,
    root_name: str | None = None,
    all_roots: bool = False,
    force: bool = False,
    limit: int = 1000,
    actor: str = "system",
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = max(1, min(int(limit or 1000), 10000))
    psycopg = _load_psycopg()
    jobs: list[dict[str, Any]] = []
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            roots = _reprocess_scope_roots(cur, root_name=root_name, all_roots=all_roots)
            root_ids = [row["id"] for row in roots]
            root_names = [row["name"] for row in roots]
            if not roots:
                return _empty_reprocess_invalidation(root_name=root_name, all_roots=all_roots, limit=row_limit)

            candidate_where = _reprocess_asset_candidate_where(force=force)
            cur.execute(
                f"""
                SELECT a.id::text, r.name, a.path, a.file_kind
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.id = ANY(%s::uuid[])
                  AND {candidate_where}
                ORDER BY a.updated_at ASC, a.id ASC
                LIMIT %s
                """,
                (root_ids, row_limit),
            )
            candidates = [
                {"asset_id": row[0], "root_name": row[1], "path": row[2], "file_kind": row[3]}
                for row in cur.fetchall()
            ]
            asset_ids = [row["asset_id"] for row in candidates]

            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'obsolete',
                    telemetry = jsonb_strip_nulls(
                        COALESCE(telemetry, '{}'::jsonb) || jsonb_build_object(
                            'obsolete_previous_status', status,
                            'obsolete_previous_result_status', telemetry->>'result_status',
                            'result_status', 'obsolete',
                            'obsolete_reason', 'maintenance_reprocess_derived_state',
                            'actor', %s::text
                        )
                    ),
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND status IN ('pending', 'retrying', 'retrying_locked', 'retrying_vss_failed', 'failed')
                  AND (
                      (job_type LIKE 'corpus_%%' AND payload->>'root_name' = ANY(%s::text[]))
                      OR (
                          job_type = 'search_index_sync'
                          AND (payload->>'root_name' = ANY(%s::text[]) OR COALESCE(payload->>'root_name', '') = '')
                      )
                  )
                """,
                (actor, root_names, root_names),
            )
            jobs_obsoleted = int(cur.rowcount or 0)

            container_children_deleted = 0
            chunks_deleted = 0
            assets_requeued = 0
            if asset_ids:
                cur.execute(
                    """
                    UPDATE source_assets
                    SET extraction_status = 'deleted',
                        deleted_at = COALESCE(deleted_at, now()),
                        metadata = COALESCE(metadata, '{}'::jsonb) || %s::jsonb,
                        updated_at = now()
                    WHERE root_id = ANY(%s::uuid[])
                      AND metadata->>'container_asset_id' = ANY(%s::text[])
                      AND deleted_at IS NULL
                    """,
                    (
                        _json({"deleted_reason": "maintenance_reprocess_derived_state", "actor": actor}),
                        root_ids,
                        asset_ids,
                    ),
                )
                container_children_deleted = int(cur.rowcount or 0)
                cur.execute("DELETE FROM code_references WHERE source_asset_id = ANY(%s::uuid[])", (asset_ids,))
                cur.execute("DELETE FROM code_symbols WHERE source_asset_id = ANY(%s::uuid[])", (asset_ids,))
                cur.execute("DELETE FROM asset_chunks WHERE asset_id = ANY(%s::uuid[])", (asset_ids,))
                chunks_deleted = int(cur.rowcount or 0)
                metadata_reset_expr = _metadata_drop_expression(
                    "COALESCE(metadata, '{}'::jsonb)",
                    _REPROCESS_METADATA_DROP_KEYS,
                )
                cur.execute(
                    f"""
                    UPDATE source_assets
                    SET extraction_status = 'queued',
                        indexed_at = NULL,
                        metadata = {metadata_reset_expr}
                                   || %s::jsonb,
                        updated_at = now()
                    WHERE id = ANY(%s::uuid[])
                      AND deleted_at IS NULL
                    """,
                    (
                        _json(
                            {
                                "maintenance_reprocess": True,
                                "maintenance_reprocess_force": bool(force),
                                "maintenance_reprocess_actor": actor,
                            }
                        ),
                        asset_ids,
                    ),
                )
                assets_requeued = int(cur.rowcount or 0)

            search_where, search_params = _reprocess_search_index_scope(root_names=root_names, all_roots=all_roots)
            cur.execute(
                f"""
                UPDATE search_index_records
                SET index_status = 'pending',
                    source_hash = NULL,
                    last_error = NULL,
                    sync_started_at = NULL,
                    sync_completed_at = NULL,
                    metadata = COALESCE(metadata, '{{}}'::jsonb) || %s::jsonb,
                    updated_at = now()
                WHERE index_status <> 'deleted'
                  AND {search_where}
                """,
                (_json({"sync_reason": "maintenance_reprocess_derived_state", "actor": actor}), *search_params),
            )
            search_records_marked = int(cur.rowcount or 0)

            for item in candidates:
                job_type = _corpus_extract_job_type_for_file_kind(str(item["file_kind"] or ""))
                schedule = _job_schedule_metadata(job_type)
                payload = {
                    "root_name": item["root_name"],
                    "path": item["path"],
                    "reason": "maintenance_reprocess_derived_state",
                }
                queued = _enqueue_unique_capture_job_with_cursor(
                    cur,
                    job_type=job_type,
                    payload=payload,
                    job_family=schedule["job_family"],
                    resource_class=schedule["resource_class"],
                    priority=schedule["priority"],
                    time_budget_seconds=schedule["time_budget_seconds"],
                    telemetry={
                        "stage": "queued",
                        "root_name": item["root_name"],
                        "path": item["path"],
                        "reason": "maintenance_reprocess_derived_state",
                    },
                    audit_details={
                        "job_type": job_type,
                        "root_name": item["root_name"],
                        "path": item["path"],
                        "reason": "maintenance_reprocess_derived_state",
                    },
                )
                jobs.append(
                    {
                        "job_id": queued["job_id"],
                        "root_name": item["root_name"],
                        "path": item["path"],
                        "job_type": job_type,
                        "deduped": queued["deduped"],
                        "reused": queued["reused"],
                    }
                )
    return {
        "scope": {"all_roots": bool(all_roots), "root_name": root_name, "root_names": root_names},
        "limit": row_limit,
        "force": bool(force),
        "jobs_obsoleted": jobs_obsoleted,
        "assets_requeued": assets_requeued,
        "container_children_deleted": container_children_deleted,
        "chunks_deleted": chunks_deleted,
        "search_records_marked": search_records_marked,
        "jobs": jobs,
    }


def _empty_reprocess_invalidation(*, root_name: str | None, all_roots: bool, limit: int) -> dict[str, Any]:
    return {
        "scope": {"all_roots": bool(all_roots), "root_name": root_name, "root_names": []},
        "limit": limit,
        "force": False,
        "jobs_obsoleted": 0,
        "assets_requeued": 0,
        "container_children_deleted": 0,
        "chunks_deleted": 0,
        "search_records_marked": 0,
        "jobs": [],
    }


def _reprocess_scope_roots(cur: Any, *, root_name: str | None, all_roots: bool) -> list[dict[str, str]]:
    if all_roots and root_name:
        raise ValueError("use either all_roots or root_name, not both")
    if not all_roots and not root_name:
        raise ValueError("reprocess scope requires all_roots or root_name")
    if all_roots:
        cur.execute(
            """
            SELECT id::text, name
            FROM monitored_roots
            WHERE enabled
            ORDER BY name
            """
        )
    else:
        cur.execute(
            """
            SELECT id::text, name
            FROM monitored_roots
            WHERE enabled
              AND name = %s
            ORDER BY name
            """,
            (root_name,),
        )
    return [{"id": row[0], "name": row[1]} for row in cur.fetchall()]


def _reprocess_asset_candidate_where(*, force: bool) -> str:
    force_clause = "" if force else "AND (a.extraction_status <> 'indexed' OR NOT EXISTS (SELECT 1 FROM asset_chunks c WHERE c.asset_id = a.id))"
    return f"""
        a.deleted_at IS NULL
        AND a.canonical_asset_id IS NULL
        AND NOT (a.metadata ? 'container_asset_id')
        AND NOT (
            array_length(string_to_array(a.path, '/'), 1) = 2
            AND lower(split_part(a.path, '/', 2)) IN ('message.eml', 'message.msg', 'body.html')
        )
        {force_clause}
    """


def _reprocess_search_index_scope(*, root_names: list[str], all_roots: bool) -> tuple[str, tuple[Any, ...]]:
    if all_roots:
        return "(owner_table IN ('episodes', 'claims') OR root_name = ANY(%s::text[]))", (root_names,)
    return "root_name = ANY(%s::text[])", (root_names,)


def _metadata_drop_expression(column: str, keys: tuple[str, ...]) -> str:
    expression = column
    for key in keys:
        expression = f"({expression} - '{key}')"
    return expression


def requeue_svg_source_assets(
    *,
    root_name: str | None = None,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = max(1, min(int(limit or 1000), 10000))
    filters = [
        "a.deleted_at IS NULL",
        "LOWER(a.extension) = '.svg'",
        "COALESCE(a.metadata->>'svg_requeue_reason', '') <> 'svg_renderer_reparse'",
    ]
    params: list[Any] = []
    if root_name:
        filters.append("r.name = %s")
        params.append(root_name)
    params.append(row_limit)
    psycopg = _load_psycopg()
    jobs: list[dict[str, Any]] = []
    schedule = _job_schedule_metadata("corpus_extract_image")
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT a.id::text, r.name, a.path
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE {" AND ".join(filters)}
                ORDER BY a.updated_at ASC, a.id ASC
                LIMIT %s
                """,
                tuple(params),
            )
            rows = cur.fetchall()
            for asset_id, row_root_name, path in rows:
                payload = {
                    "root_name": row_root_name,
                    "path": path,
                    "reason": "svg_renderer_reparse",
                }
                queued = _enqueue_unique_capture_job_with_cursor(
                    cur,
                    job_type="corpus_extract_image",
                    payload=payload,
                    job_family=schedule["job_family"],
                    resource_class=schedule["resource_class"],
                    priority=schedule["priority"],
                    time_budget_seconds=schedule["time_budget_seconds"],
                    telemetry={"stage": "queued", "root_name": row_root_name, "path": path, "reason": "svg_renderer_reparse"},
                    audit_details={"job_type": "corpus_extract_image", "root_name": row_root_name, "path": path, "reason": "svg_renderer_reparse"},
                )
                job_id = queued["job_id"]
                cur.execute("DELETE FROM asset_chunks WHERE asset_id = %s", (asset_id,))
                cur.execute(
                    """
                    UPDATE source_assets
                    SET extraction_status = 'queued',
                        indexed_at = NULL,
                        metadata = (
                            metadata - 'svg' - 'svg_parse' - 'svg_raster' - 'ocr' - 'vision' - 'vision_escalation'
                                     - 'decorative'
                        ) || %s::jsonb,
                        updated_at = now()
                    WHERE id = %s
                      AND deleted_at IS NULL
                    """,
                    (
                        _json({"svg_requeued": True, "svg_requeue_reason": "svg_renderer_reparse", "svg_requeue_job_id": job_id}),
                        asset_id,
                    ),
                )
                jobs.append({"job_id": job_id, "root_name": row_root_name, "path": path, "job_type": "corpus_extract_image", "deduped": queued["deduped"], "reused": queued["reused"]})
    return {"queued": len(jobs), "jobs": jobs, "limit": row_limit, "root_name": root_name}


def _corpus_extract_job_type_for_file_kind(file_kind: str) -> str:
    normalized = str(file_kind or "").strip().lower()
    if normalized == "pdf":
        return "corpus_extract_pdf"
    return f"corpus_extract_{normalized or 'binary'}"


def _path_scope_sql(
    column_sql: str,
    *,
    cwd: str | None = None,
    root_path: str | None = None,
) -> tuple[str, tuple[Any, ...]]:
    scope_path = str(root_path or cwd or "").strip()
    if not scope_path:
        return "", ()

    exact_values: list[str] = []
    prefix_values: list[str] = []
    for variant in _path_scope_variants(scope_path):
        exact_values.append(variant)
        escaped = _like_escape(variant)
        prefix_values.append(f"{escaped}\\\\%")
        prefix_values.append(f"{escaped}/%")

    clauses: list[str] = []
    params: list[Any] = []
    for value in _dedupe_preserving_order(exact_values):
        clauses.append(f"{column_sql} = %s")
        params.append(value)
    for value in _dedupe_preserving_order(prefix_values):
        clauses.append(f"{column_sql} LIKE %s")
        params.append(value)
    return f"AND ({' OR '.join(clauses)})", tuple(params)


def _episode_scope_sql(
    cwd_column_sql: str,
    workspace_key_column_sql: str,
    *,
    cwd: str | None = None,
    root_path: str | None = None,
    workspace_key: str | None = None,
) -> tuple[str, tuple[Any, ...]]:
    clauses: list[str] = []
    params: list[Any] = []
    cleaned_workspace_key = str(workspace_key or "").strip()
    if cleaned_workspace_key:
        clauses.append(f"{workspace_key_column_sql} = %s")
        params.append(cleaned_workspace_key)

    path_sql, path_params = _path_scope_sql(cwd_column_sql, cwd=cwd, root_path=root_path)
    if path_sql:
        clauses.append(path_sql.removeprefix("AND ").strip())
        params.extend(path_params)

    if not clauses:
        return "", ()
    return f"AND ({' OR '.join(clauses)})", tuple(params)


def _path_scope_variants(path: str) -> list[str]:
    cleaned = path.strip().rstrip("\\/")
    if not cleaned:
        return []
    return _dedupe_preserving_order([cleaned, cleaned.replace("\\", "/"), cleaned.replace("/", "\\")])


def _like_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")


def _dedupe_preserving_order(values: list[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        if value and value not in seen:
            seen.add(value)
            result.append(value)
    return result


def _add_ranked_rows(
    stream: str,
    rows: list[tuple[Any, ...]],
    streams: dict[str, list[str]],
    details: dict[str, dict[str, Any]],
) -> None:
    streams[stream] = []
    for row in rows:
        item_id = row[0]
        streams[stream].append(item_id)
        detail = details.setdefault(
            item_id,
            {"title": row[1], "summary": row[2], "raw_scores": {}},
        )
        detail["raw_scores"][stream] = float(row[3] or 0.0)
        if len(row) > 4 and isinstance(row[4], dict):
            for key, value in row[4].items():
                if key == "graph" and isinstance(value, dict) and isinstance(detail.get("graph"), dict):
                    detail["graph"] = _merge_graph_metadata(detail["graph"], value)
                elif key == "lifecycle" and isinstance(value, dict) and isinstance(detail.get("lifecycle"), dict):
                    detail["lifecycle"] = {**detail["lifecycle"], **value}
                else:
                    detail[key] = value


def _add_episode_lifecycle_rows(
    rows: list[tuple[Any, ...]],
    details: dict[str, dict[str, Any]],
) -> None:
    for row in rows:
        item_id = row[0]
        detail = details.get(item_id)
        if detail is None:
            continue
        score = lifecycle_score(
            LifecycleScoreInput(
                confidence=float(row[1] or 0.0),
                usage_count=int(row[2] or 0),
                superseded=bool(row[3]),
                lifecycle_state=row[4] or "active",
                contradiction_count=int(row[5] or 0),
                retention_action=row[6] or "keep",
                age_days=_age_days(row[7]),
                confirmation_age_days=_age_days(row[8]),
            )
        )
        lifecycle = detail.setdefault("lifecycle", {})
        if isinstance(lifecycle, dict):
            lifecycle.update(
                {
                    "state": lifecycle.get("state") or row[4] or "active",
                    "score": score.score,
                    "current": lifecycle.get("current", row[4] in {"active", "confirmed", "reinforced"}),
                    "audit_visible": lifecycle.get(
                        "audit_visible",
                        row[4] in {"superseded", "contradicted", "stale", "retired"},
                    ),
                    "explanation": score.explanation,
                }
            )


def _add_episode_claim_lifecycle_rows(cur: Any, details: dict[str, dict[str, Any]]) -> None:
    if not details:
        return
    cur.execute(
        """
        SELECT episode_id::text,
               bool_or(lifecycle_state IN ('active', 'confirmed', 'reinforced')
                       AND COALESCE(retention_action, 'keep') = 'keep') AS has_current_claim,
               bool_or(lifecycle_state IN ('stale', 'contradicted', 'superseded', 'retired')
                       OR COALESCE(retention_action, 'keep') <> 'keep') AS has_non_current_claim,
               array_remove(array_agg(DISTINCT lifecycle_state ORDER BY lifecycle_state), NULL) AS claim_states,
               array_remove(array_agg(DISTINCT COALESCE(retention_action, 'keep')
                                      ORDER BY COALESCE(retention_action, 'keep')), NULL) AS retention_actions
        FROM claims
        WHERE episode_id = ANY(%s::uuid[])
        GROUP BY episode_id
        """,
        (list(details.keys()),),
    )
    for row in cur.fetchall():
        detail = details.get(str(row[0]))
        if detail is None:
            continue
        has_current_claim = bool(row[1])
        has_non_current_claim = bool(row[2])
        lifecycle = detail.setdefault("lifecycle", {})
        if not isinstance(lifecycle, dict):
            continue
        lifecycle["claim_current"] = has_current_claim
        lifecycle["claim_states"] = [str(value) for value in (row[3] or []) if value is not None]
        lifecycle["claim_retention_actions"] = [str(value) for value in (row[4] or []) if value is not None]
        if has_non_current_claim and not has_current_claim:
            lifecycle["current"] = False
            lifecycle["audit_visible"] = True


def _merge_graph_metadata(left: dict[str, Any], right: dict[str, Any]) -> dict[str, Any]:
    merged = dict(left)
    for key in {"matched_claim_ids", "entity_ids"}:
        values = [str(value) for value in merged.get(key, []) if value is not None]
        values.extend(str(value) for value in right.get(key, []) if value is not None)
        merged[key] = sorted(set(values))
    return merged


def _add_ranked_corpus_rows(
    stream: str,
    rows: list[tuple[Any, ...]],
    streams: dict[str, list[str]],
    details: dict[str, dict[str, Any]],
) -> None:
    streams[stream] = []
    for row in rows:
        item_id = row[0]
        streams[stream].append(item_id)
        detail = details.setdefault(
            item_id,
            {
                "asset_id": row[1],
                "title": row[2],
                "summary": row[3],
                "source_path": row[4],
                "duplicate_count": int(row[5] or 0),
                "trust_rank": int(row[6] or 500),
                "root_name": row[7],
                "raw_scores": {},
            },
        )
        detail["raw_scores"][stream] = float(row[8] or 0.0)
        if len(row) > 9 and row[9] is not None:
            detail["file_kind"] = row[9]
        if len(row) > 10 and row[10]:
            detail["code"] = row[10]


def _corpus_hydration_candidate_ids(
    primary_fused: list[RankedItem],
    *,
    streams: dict[str, list[str]],
    hydration_limit: int,
    code_symbol_backfill_limit: int = 10,
) -> list[str]:
    selected: list[str] = []
    seen: set[str] = set()
    for item in primary_fused[:hydration_limit]:
        if item.item_id in seen:
            continue
        selected.append(item.item_id)
        seen.add(item.item_id)

    code_symbol_ids = streams.get("code_symbol_exact") or []
    for item_id in code_symbol_ids[: min(max(0, code_symbol_backfill_limit), hydration_limit)]:
        if item_id in seen:
            continue
        if len(selected) >= hydration_limit and selected:
            removed = selected.pop()
            seen.discard(removed)
        selected.append(item_id)
        seen.add(item_id)
    return selected


def _rank_corpus_candidates(
    query: str,
    *,
    streams: dict[str, list[str]],
    details: dict[str, dict[str, Any]],
    filters: dict[str, Any] | None,
) -> list[RankedItem]:
    fused = reciprocal_rank_fusion(streams)
    if not details:
        return fused

    query_symbols = _code_query_symbols(query)
    explicit_code_focus = _has_explicit_code_focus(query, filters)
    implementation_intent_allowed = (
        explicit_code_focus
        and not _query_requests_non_implementation_code(query)
        and not _filters_request_non_implementation_relationship(filters)
    )
    adjusted: list[RankedItem] = []
    for item in fused:
        detail = details.get(item.item_id)
        if not detail:
            adjusted.append(item)
            continue
        boost = _corpus_code_rank_adjustment(
            detail,
            query_symbols=query_symbols,
            exact_symbol_allowed=explicit_code_focus,
            implementation_intent_allowed=implementation_intent_allowed,
        )
        if boost <= 0:
            adjusted.append(item)
            continue
        raw_scores = detail.setdefault("raw_scores", {})
        raw_scores["code_rank_adjustment"] = round(boost, 6)
        adjusted.append(
            RankedItem(
                item_id=item.item_id,
                score=item.score + boost,
                streams=tuple(sorted(set(item.streams) | {"code_rank_adjustment"})),
            )
        )
    ranked = sorted(adjusted, key=lambda item: (-item.score, item.item_id))
    if _should_balance_generic_corpus_query(query, filters):
        return _balance_generic_corpus_ranking(ranked, details)
    return ranked


def _corpus_code_rank_adjustment(
    detail: dict[str, Any],
    *,
    query_symbols: set[str],
    exact_symbol_allowed: bool,
    implementation_intent_allowed: bool,
) -> float:
    code = detail.get("code") if isinstance(detail.get("code"), dict) else {}
    relationship = str(code.get("relationship") or "").strip().lower().replace("-", "_")
    if relationship not in _CODE_IMPLEMENTATION_RELATIONSHIPS:
        return 0.0
    candidate_symbols = _candidate_code_symbol_aliases(detail)
    if exact_symbol_allowed and query_symbols and candidate_symbols.intersection(query_symbols):
        return _CODE_EXACT_SYMBOL_BOOST
    if implementation_intent_allowed:
        return _CODE_IMPLEMENTATION_INTENT_BOOST
    return 0.0


def _balance_generic_corpus_ranking(ranked: list[RankedItem], details: dict[str, dict[str, Any]]) -> list[RankedItem]:
    if not ranked:
        return ranked
    top_detail = details.get(ranked[0].item_id, {})
    if not _is_code_corpus_detail(top_detail):
        return ranked

    best_index: int | None = None
    best_quality = 0.0
    for index, item in enumerate(ranked):
        detail = details.get(item.item_id, {})
        if _is_code_corpus_detail(detail):
            continue
        quality = _non_code_text_quality(detail)
        if quality < 0.70:
            continue
        if best_index is None or quality > best_quality:
            best_index = index
            best_quality = quality

    if best_index is None or best_index == 0:
        return ranked
    selected = ranked[best_index]
    top_score = ranked[0].score
    if selected.score <= top_score:
        detail = details.get(selected.item_id)
        if detail is not None:
            detail.setdefault("raw_scores", {})["balanced_non_code_guardrail"] = round(best_quality, 6)
        selected = RankedItem(
            item_id=selected.item_id,
            score=top_score + 0.000001,
            streams=tuple(sorted(set(selected.streams) | {"balanced_non_code_guardrail"})),
        )
    return [selected, *ranked[:best_index], *ranked[best_index + 1 :]]


def _is_code_corpus_detail(detail: dict[str, Any]) -> bool:
    file_kind = str(detail.get("file_kind") or "").strip().lower().replace("-", "_")
    return file_kind == "code" or isinstance(detail.get("code"), dict)


def _non_code_text_quality(detail: dict[str, Any]) -> float:
    raw_scores = detail.get("raw_scores") if isinstance(detail.get("raw_scores"), dict) else {}
    quality = 0.0
    for stream in ("corpus_lexical", "corpus_fuzzy", "mail_sidecar", _VESPA_RRF_STREAM, _VESPA_LEXICAL_STREAM, "vespa_hybrid"):
        try:
            quality = max(quality, float(raw_scores.get(stream) or 0.0))
        except (TypeError, ValueError):
            continue
    return quality


def _candidate_code_symbol_aliases(detail: dict[str, Any]) -> set[str]:
    aliases: set[str] = set()
    code = detail.get("code") if isinstance(detail.get("code"), dict) else {}
    primary_symbol = code.get("primary_symbol")
    if primary_symbol:
        aliases.update(_code_symbol_aliases(str(primary_symbol)))
    title = str(detail.get("title") or "")
    if "::" in title:
        aliases.update(_code_symbol_aliases(title.rsplit("::", 1)[-1]))
    return aliases


def _code_query_symbols(query: str) -> set[str]:
    symbols: set[str] = set()
    for token in _CODE_SYMBOL_TOKEN_RE.findall(query or ""):
        symbols.update(_code_symbol_aliases(token))
    return symbols


def _code_symbol_aliases(value: str) -> set[str]:
    normalized = _normalize_code_symbol(value)
    if not normalized:
        return set()
    aliases = {normalized}
    for separator in ("::", ".", "/", "\\"):
        if separator in value:
            tail = value.rsplit(separator, 1)[-1]
            tail_normalized = _normalize_code_symbol(tail)
            if tail_normalized:
                aliases.add(tail_normalized)
    if "." in normalized:
        tail = normalized.rsplit(".", 1)[-1]
        if tail:
            aliases.add(tail)
    return aliases


def _normalize_code_symbol(value: str) -> str:
    return value.strip().strip("`'\"()[]{}:,;.").replace("::", ".").lower()


def _has_code_implementation_intent(query: str) -> bool:
    normalized = f" {str(query or '').lower()} "
    clear_terms = _CODE_IMPLEMENTATION_INTENT_TERMS - _AMBIGUOUS_CODE_IMPLEMENTATION_INTENT_TERMS
    if any(f" {term} " in normalized for term in clear_terms):
        return True
    has_code_marker = _query_has_code_marker(query)
    if has_code_marker:
        return True
    return False


def _query_has_code_marker(query: str) -> bool:
    text = str(query or "")
    normalized = f" {text.lower()} "
    if re.search(r"[A-Za-z0-9_.-]+[/\\][A-Za-z0-9_.\\/-]+\.[A-Za-z0-9]{1,8}", text):
        return True
    if any(token in normalized for token in ("::", " .py ", " .ts ", " .tsx ", " .js ", " .jsx ", " .cs ", " .sql ")):
        return True
    if re.search(r"\b[A-Za-z_][A-Za-z0-9_.]*\s*\(", text):
        return True
    return any("." in symbol and not _looks_like_filename_token(symbol) for symbol in _code_query_symbols(query))


def _looks_like_filename_token(value: str) -> bool:
    if "." not in value:
        return False
    extension = value.rsplit(".", 1)[-1].strip().lower()
    return extension in _FILENAME_EXTENSION_TOKENS


def _has_explicit_code_focus(query: str, filters: dict[str, Any] | None) -> bool:
    return _filters_request_code_focus(filters)


def _should_balance_generic_corpus_query(query: str, filters: dict[str, Any] | None) -> bool:
    return not _filters_request_code_focus(filters) and not _has_code_implementation_intent(query) and not _query_requests_non_implementation_code(query)


def _query_requests_non_implementation_code(query: str) -> bool:
    tokens = {token.lower() for token in re.findall(r"[A-Za-z_]+", query or "")}
    return bool(tokens.intersection(_CODE_NON_IMPLEMENTATION_INTENT_TERMS))


def _filters_request_non_implementation_relationship(filters: dict[str, Any] | None) -> bool:
    if not isinstance(filters, dict):
        return False
    relationships = {str(value).lower().replace("-", "_") for value in filters.get("relationships") or []}
    if not relationships:
        return False
    return not relationships.issubset(_CODE_IMPLEMENTATION_RELATIONSHIPS)


def _filters_request_code_focus(filters: dict[str, Any] | None) -> bool:
    if not isinstance(filters, dict):
        return False
    file_kinds = set(_filter_text_values(filters, "file_kinds", "file_kind"))
    return "code" in file_kinds


def _filter_text_values(filters: dict[str, Any], plural_key: str, singular_key: str | None = None) -> list[str]:
    raw_values = filters.get(plural_key)
    if raw_values is None and singular_key:
        raw_values = filters.get(singular_key)
    if raw_values is None:
        return []
    values = raw_values if isinstance(raw_values, (list, tuple, set)) else [raw_values]
    result: list[str] = []
    seen: set[str] = set()
    for value in values:
        text = str(value or "").strip().lower().replace("-", "_")
        if not text or text in seen:
            continue
        seen.add(text)
        result.append(text)
    return result


def _add_ranked_corpus_candidates(
    stream: str,
    rows: list[tuple[Any, ...]],
    streams: dict[str, list[str]],
    raw_scores: dict[str, dict[str, float]],
) -> None:
    streams[stream] = []
    seen: set[str] = set()
    for row in rows:
        item_id = str(row[0])
        if item_id in seen:
            continue
        seen.add(item_id)
        streams[stream].append(item_id)
        raw_scores.setdefault(item_id, {})[stream] = float(row[1] or 0.0)


def _record_corpus_stream_diagnostics(
    diagnostics: dict[str, Any] | None,
    stream: str,
    started: float,
    *,
    rows: int,
    plan: str,
    candidate_limit: int | None = None,
) -> None:
    if diagnostics is None:
        return
    stream_payload: dict[str, Any] = {
        "duration_ms": round((time.perf_counter() - started) * 1000, 3),
        "rows": int(rows),
        "plan": plan,
    }
    if candidate_limit is not None:
        stream_payload["candidate_limit"] = int(candidate_limit)
    diagnostics.setdefault("streams", {})[stream] = stream_payload


def _corpus_root_id(cur: Any, *, root_name: str | None) -> str | None:
    if not root_name:
        return None
    cur.execute(
        """
        SELECT id::text FROM monitored_roots
        WHERE name = %s
          AND enabled
        """,
        (root_name,),
    )
    row = cur.fetchone()
    return str(row[0]) if row else None


def _hydrate_corpus_candidate_details(
    cur: Any,
    *,
    candidate_ids: list[str],
    root_name: str | None,
    filters: dict[str, Any] | None,
    raw_scores: dict[str, dict[str, float]],
) -> dict[str, dict[str, Any]]:
    if not candidate_ids:
        return {}
    root_name_sql = "AND r.name = %s" if root_name else ""
    root_name_params: tuple[Any, ...] = (root_name,) if root_name else ()
    code_filter_sql, code_filter_params = _corpus_code_filter_sql(filters)
    cur.execute(
        f"""
        SELECT c.id::text, c.asset_id::text, c.title, c.body, a.path,
               (
                   SELECT count(*)
                   FROM source_assets duplicate
                   WHERE duplicate.canonical_asset_id = a.id
               ) AS duplicate_count,
               r.trust_rank,
               r.name AS root_name,
               1.0 / greatest(r.trust_rank, 1) AS trust_score,
               1.0 / (
                   1.0 + (
                       GREATEST(EXTRACT(EPOCH FROM (now() - c.updated_at)), 0)
                       / 86400.0 / 30.0
                   )
               ) AS freshness_score,
               a.file_kind,
               c.metadata -> 'code' AS code,
               c.metadata
        FROM asset_chunks c
        JOIN source_assets a ON a.id = c.asset_id
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE c.id = ANY(%s::uuid[])
          AND r.enabled
          AND a.deleted_at IS NULL
          AND a.canonical_asset_id IS NULL
          AND a.extraction_status = 'indexed'
          {root_name_sql}
          {code_filter_sql}
        """,
        (candidate_ids, *root_name_params, *code_filter_params),
    )
    details: dict[str, dict[str, Any]] = {}
    for row in cur.fetchall():
        item_id = str(row[0])
        metadata = row[12] if isinstance(row[12], dict) else {}
        body = mail_content_store.hydrate_chunk_body({"body": row[3], "metadata": metadata})
        detail = {
            "asset_id": row[1],
            "title": row[2],
            "summary": body,
            "source_path": row[4],
            "duplicate_count": int(row[5] or 0),
            "trust_rank": int(row[6] or 500),
            "root_name": row[7],
            "raw_scores": dict(raw_scores.get(item_id, {})),
            "_trust_score": float(row[8] or 0.0),
            "_freshness_score": float(row[9] or 0.0),
        }
        if row[10] is not None:
            detail["file_kind"] = row[10]
        if row[11]:
            detail["code"] = row[11]
        details[item_id] = detail
    return details


def _search_mail_sidecar_rows(
    cur: Any,
    *,
    query: str,
    root_name: str | None,
    filters: dict[str, Any] | None,
    limit: int,
) -> list[tuple[Any, ...]]:
    if _filters_request_code_focus(filters):
        return []
    root_name_sql = "AND r.name = %s" if root_name else ""
    root_name_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT c.id::text, c.asset_id::text, c.title, c.body, a.path,
               (
                   SELECT count(*)
                   FROM source_assets duplicate
                   WHERE duplicate.canonical_asset_id = a.id
               ) AS duplicate_count,
               r.trust_rank,
               r.name AS root_name,
               a.file_kind,
               c.metadata
        FROM asset_chunks c
        JOIN source_assets a ON a.id = c.asset_id
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE r.enabled
          AND r.metadata ? 'mail_profile'
          AND a.deleted_at IS NULL
          AND a.canonical_asset_id IS NULL
          AND a.extraction_status = 'indexed'
          AND c.metadata ? 'sidecar_ref'
          {root_name_sql}
        ORDER BY c.updated_at DESC
        LIMIT %s
        """,
        (*root_name_params, max(1, min(int(limit or 12), 200))),
    )
    rows: list[tuple[Any, ...]] = []
    for row in cur.fetchall():
        metadata = row[9] if isinstance(row[9], dict) else {}
        body = mail_content_store.hydrate_chunk_body({"body": row[3], "metadata": metadata})
        score = mail_content_store.score_mail_text(query, str(row[2] or ""), body)
        if score <= 0:
            continue
        rows.append(
            (
                row[0],
                row[1],
                row[2],
                body,
                row[4],
                row[5],
                row[6],
                row[7],
                score,
                row[8],
                None,
            )
        )
    return sorted(rows, key=lambda item: (-float(item[8] or 0.0), str(item[4] or ""), str(item[0] or "")))[:limit]


def _has_code_filters(filters: dict[str, Any] | None) -> bool:
    return _filters_request_code_focus(filters)


def _corpus_code_filter_sql(filters: dict[str, Any] | None) -> tuple[str, list[Any]]:
    if not isinstance(filters, dict):
        return "", []
    clauses: list[str] = []
    params: list[Any] = []
    file_kinds = _filter_text_values(filters, "file_kinds", "file_kind")
    if file_kinds:
        clauses.append("AND a.file_kind = ANY(%s::text[])")
        params.append(file_kinds)
    excluded_file_kinds = _filter_text_values(filters, "_exclude_file_kinds") or _filter_text_values(
        filters,
        "exclude_file_kinds",
        "exclude_file_kind",
    )
    if excluded_file_kinds:
        clauses.append("AND a.file_kind <> ALL(%s::text[])")
        params.append(excluded_file_kinds)
    languages = _filter_text_values(filters, "languages", "language")
    if languages:
        clauses.append("AND (c.metadata -> 'code' ->> 'language') = ANY(%s::text[])")
        params.append(languages)
    symbol_kinds = _filter_text_values(filters, "symbol_kinds", "symbol_kind")
    if symbol_kinds:
        clauses.append("AND (c.metadata -> 'code' ->> 'symbol_kind') = ANY(%s::text[])")
        params.append(symbol_kinds)
    relationships = _filter_text_values(filters, "relationships", "relationship")
    if relationships:
        clauses.append("AND (c.metadata -> 'code' ->> 'relationship') = ANY(%s::text[])")
        params.append(relationships)
    path_globs = _normalize_path_globs(filters.get("path_globs") if filters.get("path_globs") is not None else filters.get("path_glob"))
    if path_globs:
        glob_clauses = []
        for pattern in path_globs:
            glob_clauses.append("a.path LIKE %s ESCAPE '\\'")
            params.append(_glob_to_sql_like(str(pattern)))
        clauses.append(f"AND ({' OR '.join(glob_clauses)})")
    if filters.get("include_generated") is False:
        clauses.append("AND COALESCE(c.metadata->'code'->>'generated', c.metadata->>'generated', a.metadata->'code'->>'generated', 'false') <> 'true'")
    return ("\n                  " + "\n                  ".join(clauses)) if clauses else "", params


def _normalize_path_globs(value: str | list[str] | None) -> list[str]:
    if value is None:
        return []
    raw_values = value if isinstance(value, list) else [value]
    result: list[str] = []
    seen: set[str] = set()
    for item in raw_values:
        text = str(item or "").strip().replace("\\", "/")
        if not text or text in seen:
            continue
        seen.add(text)
        result.append(text)
    return result


def _glob_to_sql_like(pattern: str) -> str:
    escaped = []
    for char in pattern.replace("\\", "/"):
        if char == "*":
            escaped.append("%")
        elif char == "?":
            escaped.append("_")
        elif char in {"%", "_", "\\"}:
            escaped.append(f"\\{char}")
        else:
            escaped.append(char)
    return "".join(escaped)


def _validate_semantic_duplicate_memory_class(memory_class: str) -> str:
    normalized = str(memory_class or "").strip().lower()
    if normalized not in _SEMANTIC_DUPLICATE_MEMORY_CLASSES:
        raise ValueError("memory_class must be one of: corpus, episode, claim")
    return normalized


def _semantic_duplicate_classes(memory_class: str) -> list[str]:
    normalized = str(memory_class or "all").strip().lower()
    if normalized == "all":
        return ["corpus", "episode", "claim"]
    return [_validate_semantic_duplicate_memory_class(normalized)]


def _semantic_duplicate_threshold(threshold: float | None) -> float:
    value = _SEMANTIC_DUPLICATE_DEFAULT_THRESHOLD if threshold is None else float(threshold)
    if value < 0.0 or value > 1.0:
        raise ValueError("threshold must be between 0.0 and 1.0")
    return value


def _retire_semantic_duplicate_clusters(cur: Any, *, memory_class: str, root_name: str | None) -> int:
    filters = ["memory_class = %s", "status = 'active'"]
    params: list[Any] = [memory_class]
    if root_name:
        filters.append("root_name = %s")
        params.append(root_name)
    cur.execute(
        f"""
        UPDATE semantic_duplicate_clusters
        SET status = 'retired', updated_at = now()
        WHERE {" AND ".join(filters)}
        """,
        tuple(params),
    )
    return int(getattr(cur, "rowcount", 0) or 0)


def _delete_semantic_duplicate_clusters_for_root(*, root_name: str, url: str | None = None) -> int:
    normalized_root = str(root_name or "").strip()
    if not normalized_root:
        raise ValueError("root_name is required")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("DELETE FROM semantic_duplicate_clusters WHERE root_name = %s", (normalized_root,))
            return int(cur.rowcount or 0)


def delete_managed_mail_sidecars_for_root(*, root_name: str, url: str | None = None) -> dict[str, Any]:
    normalized_root = str(root_name or "").strip()
    if not normalized_root:
        raise ValueError("root_name is required")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT DISTINCT ref
                FROM (
                    SELECT c.metadata->'sidecar_ref' AS ref, r.name AS root_name
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE c.metadata ? 'sidecar_ref'
                    UNION
                    SELECT c.metadata->'mail_content' AS ref, r.name AS root_name
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE c.metadata ? 'mail_content'
                ) refs
                WHERE root_name = %s
                  AND ref IS NOT NULL
                  AND ref->>'source' = 'managed_mail'
                  AND ref->>'storage' = 'disk_sidecar'
                """,
                (normalized_root,),
            )
            try:
                rows = cur.fetchall()
            except AttributeError:
                rows = []

    summary: dict[str, Any] = {
        "root_name": normalized_root,
        "deleted": 0,
        "missing": 0,
        "blocked": 0,
        "failed": 0,
        "errors": [],
        "count": 0,
    }
    seen: set[str] = set()
    for (ref,) in rows:
        if not isinstance(ref, dict):
            summary["blocked"] += 1
            summary["errors"].append({"status": "blocked", "blocked_reason": "invalid mail sidecar reference"})
            continue
        key = str(ref.get("relative_path") or "").replace("\\", "/").strip("/")
        if not key:
            key = str(sorted(ref.items()))
        if key in seen:
            continue
        seen.add(key)
        summary["count"] += 1
        try:
            result = mail_content_store.delete_mail_content(ref)
        except OSError as exc:
            summary["failed"] += 1
            summary["errors"].append({"status": "failed", "relative_path": key, "error": str(exc)})
            continue
        status = str(result.get("status") or "unknown")
        if status in {"deleted", "missing", "blocked"}:
            summary[status] += 1
        elif status == "skipped":
            summary["missing"] += 1
        else:
            summary["failed"] += 1
        if status in {"blocked", "failed"}:
            summary["errors"].append(
                {
                    "status": status,
                    "relative_path": result.get("relative_path") or key,
                    "blocked_reason": result.get("blocked_reason"),
                    "error": result.get("error"),
                }
            )
    return summary


_DERIVED_CACHE_JSON_DIRECTORIES = ("ocr", "asr", "vision", "parser", "embeddings")
_DERIVED_CACHE_DIRECTORIES = (*_DERIVED_CACHE_JSON_DIRECTORIES, "thumbnails")


def purge_derived_cache_entries(
    *,
    source_hashes: Iterable[str] | None = None,
    active_source_hashes: Iterable[str] | None = None,
    frame_timestamps_by_hash: dict[str, Iterable[Any]] | None = None,
    active_frame_timestamps_by_hash: dict[str, Iterable[Any]] | None = None,
    purge_unreferenced: bool = False,
    dry_run: bool = True,
) -> dict[str, Any]:
    """Purge root-owned derived extraction caches without removing shared active cache entries."""

    layout = acceleration.resolve_cache_layout()
    cache_root = Path(str(layout.get("root") or "")).expanduser().resolve()
    directories = layout.get("directories") if isinstance(layout.get("directories"), dict) else {}
    target_hashes = _normalised_cache_hashes(source_hashes or [])
    active_hashes = _normalised_cache_hashes(active_source_hashes or [])
    removable_hashes = {value for value in target_hashes if value not in active_hashes}
    root_frame_timestamps = _normalised_frame_timestamp_map(frame_timestamps_by_hash or {})
    active_frame_timestamps = _normalised_frame_timestamp_map(active_frame_timestamps_by_hash or {})
    expected_active_thumbnails = _thumbnail_cache_paths_for_hashes(
        cache_root=cache_root,
        directories=directories,
        timestamps_by_hash=active_frame_timestamps,
    )
    active_thumbnail_hashes = _file_sha256_values(expected_active_thumbnails)
    removable_thumbnail_paths = _thumbnail_cache_paths_for_hashes(
        cache_root=cache_root,
        directories=directories,
        timestamps_by_hash={key: value for key, value in root_frame_timestamps.items() if key in removable_hashes},
    )
    removable_thumbnail_hashes = _file_sha256_values(removable_thumbnail_paths)
    removable_json_hashes = removable_hashes | removable_thumbnail_hashes
    preserved_json_hashes = active_hashes | active_thumbnail_hashes

    result: dict[str, Any] = {
        "dry_run": bool(dry_run),
        "source": layout.get("source"),
        "root": str(cache_root),
        "requested_hashes": len(target_hashes),
        "active_hashes": len(active_hashes),
        "purge_unreferenced": bool(purge_unreferenced),
        "deleted": {name: 0 for name in _DERIVED_CACHE_DIRECTORIES},
        "missing": {name: 0 for name in _DERIVED_CACHE_DIRECTORIES},
        "skipped_shared": {name: 0 for name in _DERIVED_CACHE_DIRECTORIES},
        "errors": [],
    }

    for name in _DERIVED_CACHE_JSON_DIRECTORIES:
        directory = _cache_layout_directory(cache_root=cache_root, directories=directories, name=name)
        if not directory.exists():
            continue
        for path in sorted(directory.glob("*.json")):
            try:
                payload = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as exc:
                result["errors"].append({"cache": name, "path": str(path), "error": str(exc)})
                continue
            source_hash = _normalise_cache_hash(payload.get("source_hash"))
            if not source_hash:
                continue
            if source_hash in preserved_json_hashes:
                if source_hash in target_hashes:
                    result["skipped_shared"][name] += 1
                continue
            should_delete = source_hash in removable_json_hashes or (purge_unreferenced and source_hash not in preserved_json_hashes)
            if not should_delete:
                continue
            _delete_cache_file(path, name=name, result=result, dry_run=dry_run)

    thumbnail_dir = _cache_layout_directory(cache_root=cache_root, directories=directories, name="thumbnails")
    if thumbnail_dir.exists():
        expected_active = {path.resolve() for path in expected_active_thumbnails}
        explicit_thumbnail_targets = {path.resolve() for path in removable_thumbnail_paths}
        if purge_unreferenced:
            thumbnail_targets = {
                path.resolve()
                for path in thumbnail_dir.glob("*")
                if path.is_file() and path.resolve() not in expected_active
            }
        else:
            thumbnail_targets = explicit_thumbnail_targets
        for path in sorted(thumbnail_targets):
            if path in expected_active:
                result["skipped_shared"]["thumbnails"] += 1
                continue
            _delete_cache_file(path, name="thumbnails", result=result, dry_run=dry_run)

    return result


def _collect_root_cache_inputs(cur: Any, *, root_id: str) -> tuple[set[str], dict[str, list[float]]]:
    cur.execute(
        """
        SELECT DISTINCT a.content_hash, a.metadata
        FROM source_assets a
        LEFT JOIN asset_chunks c ON c.asset_id = a.id
        WHERE a.root_id = %s
          AND a.content_hash IS NOT NULL
        """,
        (root_id,),
    )
    try:
        rows = cur.fetchall()
    except AttributeError:
        rows = []
    return _cache_inputs_from_asset_rows(rows)


def _collect_active_cache_inputs(cur: Any, *, exclude_root_id: str | None = None) -> tuple[set[str], dict[str, list[float]]]:
    params: tuple[Any, ...] = ()
    root_filter = ""
    if exclude_root_id:
        root_filter = "AND a.root_id <> %s"
        params = (exclude_root_id,)
    cur.execute(
        f"""
        SELECT DISTINCT a.content_hash, a.metadata
        FROM source_assets a
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE a.deleted_at IS NULL
          AND a.content_hash IS NOT NULL
          {root_filter}
        """,
        params,
    )
    try:
        rows = cur.fetchall()
    except AttributeError:
        rows = []
    return _cache_inputs_from_asset_rows(rows)


def _cache_inputs_from_asset_rows(rows: Iterable[tuple[Any, Any]]) -> tuple[set[str], dict[str, list[float]]]:
    hashes: set[str] = set()
    timestamps: dict[str, list[float]] = {}
    for raw_hash, metadata in rows:
        source_hash = _normalise_cache_hash(raw_hash)
        if not source_hash:
            continue
        hashes.add(source_hash)
        extracted = _frame_timestamps_from_metadata(metadata if isinstance(metadata, dict) else {})
        if extracted:
            timestamps[source_hash] = extracted
    return hashes, timestamps


def _frame_timestamps_from_metadata(metadata: dict[str, Any]) -> list[float]:
    timestamps: list[float] = []

    def visit(value: Any) -> None:
        if isinstance(value, dict):
            raw = value.get("timestamps")
            if isinstance(raw, list):
                for item in raw:
                    try:
                        timestamps.append(round(float(item), 3))
                    except (TypeError, ValueError):
                        continue
            for key in ("frame_sampling", "embedded_frame_sampling"):
                nested = value.get(key)
                if isinstance(nested, dict):
                    visit(nested)
            for key in ("staged_jobs", "followup_jobs"):
                jobs = value.get(key)
                if isinstance(jobs, list):
                    for job in jobs:
                        visit(job)
            payload = value.get("payload")
            if isinstance(payload, dict):
                visit(payload)
        elif isinstance(value, list):
            for item in value:
                visit(item)

    visit(metadata)
    return sorted(set(timestamps))


def _normalise_cache_hash(value: Any) -> str:
    text = str(value or "").strip().lower()
    if text.startswith("sha256:"):
        text = text.split(":", 1)[1]
    return text if re.fullmatch(r"[0-9a-f]{64}", text) else ""


def _normalised_cache_hashes(values: Iterable[Any]) -> set[str]:
    return {item for item in (_normalise_cache_hash(value) for value in values) if item}


def _normalised_frame_timestamp_map(value: dict[str, Iterable[Any]]) -> dict[str, list[float]]:
    result: dict[str, list[float]] = {}
    for raw_hash, raw_timestamps in value.items():
        source_hash = _normalise_cache_hash(raw_hash)
        if not source_hash:
            continue
        timestamps: list[float] = []
        for item in raw_timestamps or []:
            try:
                timestamps.append(round(float(item), 3))
            except (TypeError, ValueError):
                continue
        if timestamps:
            result[source_hash] = sorted(set(timestamps))
    return result


def _cache_layout_directory(*, cache_root: Path, directories: dict[str, Any], name: str) -> Path:
    path = Path(str(directories.get(name) or cache_root / name)).expanduser().resolve()
    expected = (cache_root / name).resolve()
    if path != expected and (path == cache_root or cache_root not in path.parents):
        raise ValueError(f"refusing to clear cache path outside cache root: {path}")
    return path


def _thumbnail_cache_paths_for_hashes(
    *,
    cache_root: Path,
    directories: dict[str, Any],
    timestamps_by_hash: dict[str, list[float]],
) -> set[Path]:
    thumbnail_dir = _cache_layout_directory(cache_root=cache_root, directories=directories, name="thumbnails")
    paths: set[Path] = set()
    for source_hash, timestamps in timestamps_by_hash.items():
        for timestamp in timestamps:
            timestamp_key = f"{float(timestamp):.3f}"
            key = hashlib.sha256(f"flux-thumbnail-cache-v1:{source_hash}:{timestamp_key}".encode("utf-8")).hexdigest()
            paths.add((thumbnail_dir / f"{key}.png").resolve())
    return paths


def _file_sha256_values(paths: Iterable[Path]) -> set[str]:
    values: set[str] = set()
    for path in paths:
        try:
            if not path.exists() or not path.is_file():
                continue
            digest = hashlib.sha256()
            with path.open("rb") as handle:
                for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                    digest.update(chunk)
            values.add(digest.hexdigest())
        except OSError:
            continue
    return values


def _delete_cache_file(path: Path, *, name: str, result: dict[str, Any], dry_run: bool) -> None:
    try:
        if not path.exists():
            result["missing"][name] += 1
            return
        if not path.is_file():
            result["errors"].append({"cache": name, "path": str(path), "error": "cache entry is not a file"})
            return
        result["deleted"][name] += 1
        if not dry_run:
            path.unlink()
    except OSError as exc:
        result["errors"].append({"cache": name, "path": str(path), "error": str(exc)})


def _fetch_semantic_duplicate_candidates(
    cur: Any,
    *,
    memory_class: str,
    root_name: str | None,
    limit: int,
) -> dict[str, dict[str, Any]]:
    if memory_class == "corpus":
        root_sql = "AND r.name = %s" if root_name else ""
        params: tuple[Any, ...] = ((root_name,) if root_name else ()) + (limit,)
        cur.execute(
            f"""
            SELECT c.id::text, 'asset_chunks' AS owner_table, c.title AS label,
                   a.path AS source_path, r.name AS root_name,
                   'root:' || r.name AS workspace_key,
                   r.trust_rank, length(c.body) AS text_length,
                   NULL::double precision AS confidence,
                   0 AS usage_count, 0 AS reinforcement_count,
                   NULL::timestamptz AS last_confirmed_at,
                   c.updated_at,
                   concat_ws(E'\n', c.title, a.path, c.body) AS semantic_text
            FROM asset_chunks c
            JOIN source_assets a ON a.id = c.asset_id
            JOIN monitored_roots r ON r.id = a.root_id
            WHERE r.enabled
              AND a.deleted_at IS NULL
              AND a.canonical_asset_id IS NULL
              AND a.extraction_status = 'indexed'
              {root_sql}
            ORDER BY r.name, c.updated_at DESC, c.id
            LIMIT %s
            """,
            params,
        )
    elif memory_class == "episode":
        root_sql = "AND e.metadata->>'root_name' = %s" if root_name else ""
        params = ((root_name,) if root_name else ()) + (limit,)
        cur.execute(
            f"""
            SELECT e.id::text, 'episodes' AS owner_table, e.title AS label,
                   NULL::text AS source_path,
                   NULLIF(e.metadata->>'root_name', '') AS root_name,
                   COALESCE(
                     NULLIF(e.metadata->>'workspace_key', ''),
                     CASE
                       WHEN NULLIF(e.metadata->>'root_name', '') IS NOT NULL
                       THEN 'root:' || (e.metadata->>'root_name')
                       ELSE ''
                     END
                   ) AS workspace_key,
                   NULL::integer AS trust_rank, length(e.summary) AS text_length,
                   e.confidence, e.usage_count, 0 AS reinforcement_count,
                   NULL::timestamptz AS last_confirmed_at,
                   e.updated_at,
                   concat_ws(E'\n', e.title, e.summary) AS semantic_text
            FROM episodes e
            WHERE e.superseded_by IS NULL
              {root_sql}
            ORDER BY workspace_key, e.updated_at DESC, e.id
            LIMIT %s
            """,
            params,
        )
    else:
        root_sql = "AND c.metadata->>'root_name' = %s" if root_name else ""
        params = ((root_name,) if root_name else ()) + (limit,)
        cur.execute(
            f"""
            SELECT c.id::text, 'claims' AS owner_table,
                   concat_ws(' ', e.name, c.predicate, c.object_text) AS label,
                   NULL::text AS source_path,
                   NULLIF(c.metadata->>'root_name', '') AS root_name,
                   COALESCE(
                     NULLIF(c.metadata->>'workspace_key', ''),
                     CASE
                       WHEN NULLIF(c.metadata->>'root_name', '') IS NOT NULL
                       THEN 'root:' || (c.metadata->>'root_name')
                       ELSE ''
                     END
                   ) AS workspace_key,
                   NULL::integer AS trust_rank,
                   length(concat_ws(' ', e.name, c.predicate, c.object_text)) AS text_length,
                   c.confidence, c.usage_count, c.reinforcement_count,
                   c.last_confirmed_at,
                   c.updated_at,
                   concat_ws(' ', e.name, c.predicate, c.object_text) AS semantic_text
            FROM claims c
            LEFT JOIN entities e ON e.id = c.subject_entity_id
            WHERE c.lifecycle_state IN ('active', 'confirmed', 'reinforced')
              AND c.retention_action = 'keep'
              {root_sql}
            ORDER BY workspace_key, c.updated_at DESC, c.id
            LIMIT %s
            """,
            params,
        )
    rows = cur.fetchall()
    return {
        str(row[0]): {
            "owner_id": str(row[0]),
            "owner_table": row[1],
            "memory_class": memory_class,
            "label": row[2],
            "source_path": row[3],
            "root_name": row[4],
            "workspace_key": row[5] or "",
            "trust_rank": row[6],
            "text_length": row[7],
            "confidence": row[8],
            "usage_count": row[9],
            "reinforcement_count": row[10],
            "last_confirmed_at": row[11],
            "updated_at": row[12],
            "semantic_text": row[13],
        }
        for row in rows
    }


def _semantic_duplicate_pairs_from_snowflake(
    candidates: dict[str, dict[str, Any]],
    *,
    threshold: float,
) -> list[tuple[str, str, float]]:
    if len(candidates) < 2:
        return []
    from .embeddings import SnowflakeEmbeddingProvider
    from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL

    ordered = sorted(candidates.values(), key=lambda item: (str(item.get("workspace_key") or ""), str(item["owner_id"])))
    inputs = [
        EmbeddingInput(
            owner_table=str(item["owner_table"]),
            owner_id=str(item["owner_id"]),
            text=str(item.get("semantic_text") or item.get("label") or ""),
            model=SNOWFLAKE_EMBEDDING_MODEL,
            dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
        )
        for item in ordered
    ]
    vectors = {
        result.owner_id: result.vector
        for result in SnowflakeEmbeddingProvider(
            model=SNOWFLAKE_EMBEDDING_MODEL,
            dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
        ).embed_batch(inputs)
    }
    pairs: list[tuple[str, str, float]] = []
    by_workspace: dict[str, list[dict[str, Any]]] = {}
    for item in ordered:
        by_workspace.setdefault(str(item.get("workspace_key") or ""), []).append(item)
    for workspace_items in by_workspace.values():
        for left_index, left in enumerate(workspace_items):
            left_id = str(left["owner_id"])
            left_vector = vectors.get(left_id)
            if left_vector is None:
                continue
            for right in workspace_items[left_index + 1 :]:
                right_id = str(right["owner_id"])
                right_vector = vectors.get(right_id)
                if right_vector is None:
                    continue
                similarity = _cosine_similarity(left_vector, right_vector)
                if similarity >= threshold:
                    pairs.append((left_id, right_id, similarity))
    return sorted(pairs, key=lambda item: (-item[2], item[0], item[1]))


def _cosine_similarity(left: list[float], right: list[float]) -> float:
    if not left or not right or len(left) != len(right):
        return 0.0
    dot = sum(float(a) * float(b) for a, b in zip(left, right))
    left_norm = sum(float(value) * float(value) for value in left) ** 0.5
    right_norm = sum(float(value) * float(value) for value in right) ** 0.5
    if left_norm == 0.0 or right_norm == 0.0:
        return 0.0
    return float(dot / (left_norm * right_norm))


def _build_semantic_duplicate_clusters(
    candidates: dict[str, dict[str, Any]],
    pairs: list[tuple[str, str, float]],
    *,
    memory_class: str,
    threshold: float,
) -> list[dict[str, Any]]:
    parent: dict[str, str] = {}

    def find(item_id: str) -> str:
        parent.setdefault(item_id, item_id)
        while parent[item_id] != item_id:
            parent[item_id] = parent[parent[item_id]]
            item_id = parent[item_id]
        return item_id

    def union(left: str, right: str) -> None:
        left_root = find(left)
        right_root = find(right)
        if left_root != right_root:
            parent[right_root] = left_root

    pair_similarity: dict[tuple[str, str], float] = {}
    for left, right, similarity in pairs:
        if left not in candidates or right not in candidates:
            continue
        union(left, right)
        pair_similarity[tuple(sorted((left, right)))] = float(similarity)

    grouped: dict[str, list[str]] = {}
    for item_id in parent:
        grouped.setdefault(find(item_id), []).append(item_id)

    clusters: list[dict[str, Any]] = []
    for member_ids in grouped.values():
        if len(member_ids) < 2:
            continue
        member_id_set = set(member_ids)
        cluster_similarities = [
            similarity
            for (left, right), similarity in pair_similarity.items()
            if left in member_id_set and right in member_id_set
        ]
        cluster_max_similarity = max(cluster_similarities, default=1.0)
        members = [candidates[item_id] for item_id in member_ids]
        canonical = sorted(members, key=_semantic_canonical_sort_key)[0]
        cluster_members = []
        for member in sorted(members, key=lambda item: (item["owner_id"] != canonical["owner_id"], str(item.get("label") or ""), item["owner_id"])):
            member_id = member["owner_id"]
            similarity = (
                1.0
                if member_id == canonical["owner_id"]
                else _semantic_member_similarity(
                    member_id=member_id,
                    canonical_id=canonical["owner_id"],
                    member_ids=member_id_set,
                    pair_similarity=pair_similarity,
                    fallback=cluster_max_similarity,
                )
            )
            cluster_members.append(
                {
                    "owner_table": member["owner_table"],
                    "owner_id": member_id,
                    "member_role": "canonical" if member_id == canonical["owner_id"] else "duplicate",
                    "similarity": float(similarity or 0.0),
                    "evidence": _semantic_member_evidence(member),
                }
            )
        clusters.append(
            {
                "memory_class": memory_class,
                "algorithm": _SEMANTIC_DUPLICATE_ALGORITHM,
                "threshold": threshold,
                "workspace_key": str(canonical.get("workspace_key") or ""),
                "root_name": canonical.get("root_name"),
                "canonical_owner_table": canonical["owner_table"],
                "canonical_owner_id": canonical["owner_id"],
                "suppressed_count": len(cluster_members) - 1,
                "max_similarity": float(cluster_max_similarity or 0.0),
                "metadata": {
                    "canonical_label": canonical.get("label"),
                    "canonical_source_path": canonical.get("source_path"),
                    "suppressed_count": len(cluster_members) - 1,
                    "max_similarity": float(cluster_max_similarity or 0.0),
                },
                "members": cluster_members,
            }
        )
    return sorted(clusters, key=lambda item: (item["memory_class"], item["workspace_key"], str(item["canonical_owner_id"])))


def _semantic_member_similarity(
    *,
    member_id: str,
    canonical_id: str,
    member_ids: set[str],
    pair_similarity: dict[tuple[str, str], float],
    fallback: float,
) -> float:
    direct = pair_similarity.get(tuple(sorted((canonical_id, member_id))))
    if direct is not None:
        return float(direct)
    incident = [
        similarity
        for (left, right), similarity in pair_similarity.items()
        if member_id in {left, right} and left in member_ids and right in member_ids
    ]
    return float(max(incident, default=fallback))


def _semantic_canonical_sort_key(item: dict[str, Any]) -> tuple[float, float, float, float, str]:
    trust_rank = float(item.get("trust_rank") or 0.0)
    confidence = float(item.get("confidence") or 0.0)
    reinforcement = float(item.get("reinforcement_count") or item.get("usage_count") or 0.0)
    text_length = float(item.get("text_length") or 0.0)
    updated = item.get("last_confirmed_at") or item.get("updated_at")
    updated_score = updated.timestamp() if hasattr(updated, "timestamp") else 0.0
    stable = str(item.get("source_path") or item.get("label") or item.get("owner_id") or "")
    return (-trust_rank, -confidence, -reinforcement, -text_length, -updated_score, stable.lower())


def _semantic_member_evidence(member: dict[str, Any]) -> dict[str, Any]:
    evidence: dict[str, Any] = {}
    for key in ("label", "source_path", "root_name", "workspace_key"):
        value = member.get(key)
        if value:
            evidence[key] = value
    return evidence


def _positive_int(value: Any) -> int:
    try:
        number = int(value or 0)
    except (TypeError, ValueError):
        return 0
    return max(0, number)


def _insert_semantic_duplicate_cluster(cur: Any, cluster: dict[str, Any]) -> dict[str, Any]:
    cur.execute(
        """
        INSERT INTO semantic_duplicate_clusters (
            memory_class, status, algorithm, threshold, workspace_key, root_name,
            canonical_owner_table, canonical_owner_id, metadata
        )
        VALUES (%s, 'active', %s, %s, %s, %s, %s, %s, %s::jsonb)
        RETURNING id::text, created_at, updated_at
        """,
        (
            cluster["memory_class"],
            cluster["algorithm"],
            cluster["threshold"],
            cluster["workspace_key"],
            cluster.get("root_name"),
            cluster["canonical_owner_table"],
            cluster["canonical_owner_id"],
            _json(cluster.get("metadata") or {}),
        ),
    )
    cluster_id, created_at, updated_at = cur.fetchone()
    member_count = 0
    for member in cluster["members"]:
        cur.execute(
            """
            INSERT INTO semantic_duplicate_members (
                cluster_id, memory_class, owner_table, owner_id, member_role,
                similarity, evidence
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s::jsonb)
            """,
            (
                cluster_id,
                cluster["memory_class"],
                member["owner_table"],
                member["owner_id"],
                member["member_role"],
                member["similarity"],
                _json(member.get("evidence") or {}),
            ),
        )
        member_count += 1
    inserted = {
        "id": cluster_id,
        "memory_class": cluster["memory_class"],
        "status": "active",
        "algorithm": cluster["algorithm"],
        "threshold": cluster["threshold"],
        "workspace_key": cluster["workspace_key"],
        "root_name": cluster.get("root_name"),
        "canonical_owner_table": cluster["canonical_owner_table"],
        "canonical_owner_id": cluster["canonical_owner_id"],
        "suppressed_count": cluster["suppressed_count"],
        "created_at": created_at.isoformat() if created_at else None,
        "updated_at": updated_at.isoformat() if updated_at else None,
    }
    return {"cluster": inserted, "member_count": member_count}


def _semantic_duplicate_cluster_row(row: tuple[Any, ...]) -> dict[str, Any]:
    members = [_semantic_member_row(item) for item in (row[12] or [])]
    canonical = next((member for member in members if member.get("member_role") == "canonical"), None) or {
        "owner_table": row[7],
        "owner_id": row[8],
    }
    metadata = row[9] or {}
    suppressed_count = _positive_int(metadata.get("suppressed_count")) or sum(1 for member in members if member.get("member_role") == "duplicate")
    return {
        "id": row[0],
        "memory_class": row[1],
        "status": row[2],
        "algorithm": row[3],
        "threshold": float(row[4] or 0.0),
        "workspace_key": row[5],
        "root_name": row[6],
        "canonical_owner_table": row[7],
        "canonical_owner_id": row[8],
        "canonical": canonical,
        "suppressed_count": suppressed_count,
        "members": members,
        "created_at": row[10].isoformat() if row[10] else None,
        "updated_at": row[11].isoformat() if row[11] else None,
    }


def _semantic_member_row(value: Any) -> dict[str, Any]:
    item = value if isinstance(value, dict) else {}
    result: dict[str, Any] = {
        "owner_table": item.get("owner_table"),
        "owner_id": item.get("owner_id"),
        "member_role": item.get("member_role"),
        "similarity": float(item.get("similarity") or 0.0),
    }
    for key in ("label", "source_path"):
        if item.get(key):
            result[key] = item.get(key)
    return result


def _add_semantic_duplicate_metadata(
    cur: Any,
    *,
    owner_table: str,
    details: dict[str, dict[str, Any]],
) -> None:
    if not details:
        return
    cur.execute(
        """
        SELECT c.id::text, c.canonical_owner_id::text, c.threshold, c.metadata,
               COALESCE(
                 jsonb_agg(
                   jsonb_build_object(
                     'owner_table', m.owner_table,
                     'owner_id', m.owner_id::text,
                     'similarity', m.similarity,
                     'label', m.evidence->>'label',
                     'source_path', m.evidence->>'source_path'
                   )
                   ORDER BY m.similarity DESC, m.owner_id::text
                 ) FILTER (WHERE m.member_role = 'duplicate'),
                 '[]'::jsonb
               ) AS suppressed
        FROM semantic_duplicate_clusters c
        LEFT JOIN semantic_duplicate_members m ON m.cluster_id = c.id
        WHERE c.status = 'active'
          AND c.canonical_owner_table = %s
          AND c.canonical_owner_id = ANY(%s::uuid[])
        GROUP BY c.id
        """,
        (owner_table, list(details.keys())),
    )
    for row in cur.fetchall():
        cluster_id, canonical_owner_id, threshold, metadata, suppressed = row
        metadata = metadata or {}
        suppressed_items = [_semantic_suppressed_item(item) for item in (suppressed or [])]
        details[str(canonical_owner_id)]["semantic_duplicate_cluster"] = {
            "cluster_id": cluster_id,
            "canonical_owner_id": str(canonical_owner_id),
            "suppressed_count": _positive_int(metadata.get("suppressed_count")) or len(suppressed_items),
            "reason": "semantic_near_duplicate",
            "threshold": float(threshold or 0.0),
            "max_similarity": float(metadata.get("max_similarity") or 0.0),
            "suppressed": suppressed_items,
        }


def _semantic_suppressed_item(item: Any) -> dict[str, Any]:
    value = item if isinstance(item, dict) else {}
    result: dict[str, Any] = {
        "owner_id": value.get("owner_id"),
        "owner_table": value.get("owner_table"),
        "similarity": float(value.get("similarity") or 0.0),
    }
    for key in ("label", "source_path"):
        if value.get(key):
            result[key] = value.get(key)
    return result


def _entity_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "type": row[1],
        "name": row[2],
        "attributes": row[3] or {},
        "created_at": row[4].isoformat() if row[4] else None,
    }


def _fetch_retention_policies(cur: Any) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT id::text, memory_class, half_life_days, min_confidence,
               action, updated_by, metadata, created_at, updated_at
        FROM retention_policies
        ORDER BY CASE memory_class
                   WHEN 'claim' THEN 1
                   WHEN 'episode' THEN 2
                   WHEN 'corpus' THEN 3
                   ELSE 4
                 END,
                 memory_class
        """
    )
    return [_retention_policy_row(row) for row in cur.fetchall()]


def _retention_policy_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "memory_class": row[1],
        "half_life_days": int(row[2] or 0),
        "min_confidence": float(row[3] or 0.0),
        "action": row[4],
        "updated_by": row[5],
        "metadata": row[6] or {},
        "created_at": row[7].isoformat() if row[7] else None,
        "updated_at": row[8].isoformat() if row[8] else None,
    }


def _validate_retention_policy_input(
    *,
    memory_class: str,
    half_life_days: int,
    min_confidence: float,
    action: str,
    reason: str,
) -> str:
    normalized_class = str(memory_class or "").strip().lower()
    if normalized_class not in _RETENTION_MEMORY_CLASSES:
        raise ValueError("memory_class must be one of: claim, corpus, episode")
    if int(half_life_days) <= 0:
        raise ValueError("half_life_days must be greater than 0")
    if not 0.0 <= float(min_confidence) <= 1.0:
        raise ValueError("min_confidence must be between 0 and 1")
    if action not in _RETENTION_ACTIONS:
        raise ValueError("action must be one of: review, deprioritize, retire")
    if not str(reason or "").strip():
        raise ValueError("reason is required")
    return normalized_class


def _claim_quality_candidate(row: tuple[Any, ...], policy: dict[str, Any] | None) -> dict[str, Any]:
    confidence = float(row[4] or 0.0)
    state = row[5] or "active"
    retention_action = row[6] or "keep"
    contradiction_count = int(row[9] or 0)
    superseded = bool(row[10])
    policy_action = _policy_action(policy)
    reason = "current"
    bucket = "healthy"
    if retention_action != "keep":
        reason = f"retention:{retention_action}"
        bucket = retention_action
    elif state in {"stale", "contradicted", "superseded", "retired"}:
        reason = state
        bucket = policy_action
    elif confidence < _policy_min_confidence(policy):
        reason = "low_confidence"
        bucket = policy_action
    elif contradiction_count > 0:
        reason = "contradiction"
        bucket = policy_action
    elif superseded:
        reason = "superseded"
        bucket = policy_action
    elif _older_than_policy(row[8], policy):
        reason = "age_exceeds_policy"
        bucket = policy_action
    return {
        "id": row[0],
        "memory_class": "claim",
        "label": _safe_label(" ".join(str(part or "") for part in (row[1], row[2], row[3]))),
        "reason": reason,
        "quality_bucket": bucket,
        "confidence": confidence,
        "lifecycle_state": state,
        "retention_action": retention_action,
        "updated_at": row[7].isoformat() if row[7] else None,
    }


def _episode_quality_candidate(row: tuple[Any, ...], policy: dict[str, Any] | None) -> dict[str, Any]:
    confidence = float(row[2] or 0.0)
    policy_action = _policy_action(policy)
    reason = "current"
    bucket = "healthy"
    if bool(row[5]):
        reason = "superseded"
        bucket = policy_action
    elif confidence < _policy_min_confidence(policy):
        reason = "low_confidence"
        bucket = policy_action
    elif _older_than_policy(row[3], policy):
        reason = "age_exceeds_policy"
        bucket = policy_action
    return {
        "id": row[0],
        "memory_class": "episode",
        "label": _safe_label(row[1]),
        "reason": reason,
        "quality_bucket": bucket,
        "confidence": confidence,
        "updated_at": row[4].isoformat() if row[4] else None,
    }


def _corpus_quality_candidate(row: tuple[Any, ...], policy: dict[str, Any] | None) -> dict[str, Any]:
    confidence = float(row[2] or 0.0)
    status = row[3] or "unknown"
    tier = row[4] or "metadata_only"
    is_canonical = bool(row[5])
    deleted = bool(row[6])
    policy_action = _policy_action(policy)
    reason = "current"
    bucket = "healthy"
    if deleted:
        reason = "deleted"
        bucket = "retire"
    elif not is_canonical:
        reason = "duplicate"
        bucket = "deprioritize"
    elif str(status).startswith("blocked") or status in {"failed", "retrying_locked", "retrying_vss_failed", "retrying_gpu_busy"}:
        reason = status
        bucket = "review"
    elif status == "metadata_only" or tier == "metadata_only":
        reason = "metadata_only"
        bucket = policy_action
    elif confidence < _policy_min_confidence(policy):
        reason = "low_trust"
        bucket = policy_action
    return {
        "id": row[0],
        "memory_class": "corpus",
        "label": _safe_label(Path(str(row[1] or "")).name or row[1]),
        "reason": reason,
        "quality_bucket": bucket,
        "confidence": confidence,
        "extraction_status": status,
        "retention_action": None,
        "updated_at": row[8].isoformat() if row[8] else None,
    }


def _semantic_duplicate_quality_candidate(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "memory_class": row[1],
        "label": f"Semantic duplicates: {_safe_label(row[2])}",
        "reason": "semantic_near_duplicate",
        "quality_bucket": "deprioritize",
        "confidence": 1.0,
        "updated_at": row[5].isoformat() if row[5] else None,
        "metadata": {
            "root_name": row[4],
            "suppressed_count": int(row[3] or 0),
        },
    }


def _retention_quality_summary(candidates: list[dict[str, Any]]) -> dict[str, Any]:
    by_class = {"claim": 0, "episode": 0, "corpus": 0}
    by_bucket = {"healthy": 0, "review": 0, "deprioritize": 0, "retire": 0}
    needs_review = 0
    for candidate in candidates:
        memory_class = str(candidate.get("memory_class") or "unknown")
        bucket = str(candidate.get("quality_bucket") or "healthy")
        by_class[memory_class] = by_class.get(memory_class, 0) + 1
        by_bucket[bucket] = by_bucket.get(bucket, 0) + 1
        if bucket != "healthy":
            needs_review += 1
    return {
        "total": len(candidates),
        "needs_review": needs_review,
        "by_class": by_class,
        "by_bucket": by_bucket,
    }


def _policy_action(policy: dict[str, Any] | None) -> str:
    action = str((policy or {}).get("action") or "review")
    return action if action in _RETENTION_ACTIONS else "review"


def _policy_min_confidence(policy: dict[str, Any] | None) -> float:
    return float((policy or {}).get("min_confidence") or 0.0)


def _older_than_policy(created_at: Any, policy: dict[str, Any] | None) -> bool:
    age_days = _age_days(created_at)
    half_life_days = int((policy or {}).get("half_life_days") or 0)
    return age_days is not None and half_life_days > 0 and age_days > half_life_days


def _safe_label(value: Any, *, max_length: int = 120) -> str:
    label = " ".join(str(value or "").split())
    if not label:
        return "-"
    return label if len(label) <= max_length else f"{label[: max_length - 1]}..."


def _claim_review_reasons(claim: dict[str, Any]) -> list[str]:
    reasons: list[str] = []
    state = str(claim.get("lifecycle_state") or "")
    if state in {"stale", "contradicted", "superseded", "retired"}:
        reasons.append(state)
    retention_action = str(claim.get("retention_action") or "keep")
    if retention_action != "keep":
        reasons.append(f"retention:{retention_action}")
    return reasons


def _claim_row(row: tuple[Any, ...], *, subject: dict[str, Any] | None = None) -> dict[str, Any]:
    score = lifecycle_score(
        LifecycleScoreInput(
            confidence=float(row[5] or 0.0),
            usage_count=int(row[10] or 0),
            superseded=bool(row[6]),
            lifecycle_state=row[9] or "active",
            reinforcement_count=int(row[11] or 0),
            reinforcement_age_days=_age_days(row[13]),
            contradiction_count=int(row[12] or 0),
            retention_action=row[14] or "keep",
            age_days=_age_days(row[7]),
            confirmation_age_days=_age_days(row[8]),
        )
    )
    return {
        "id": row[0],
        "episode_id": row[1],
        "subject_entity_id": row[2],
        "subject": subject,
        "predicate": row[3],
        "object_text": row[4],
        "confidence": float(row[5] or 0.0),
        "superseded_by": row[6],
        "created_at": row[7].isoformat() if row[7] else None,
        "last_confirmed_at": row[8].isoformat() if row[8] else None,
        "lifecycle_state": row[9],
        "usage_count": int(row[10] or 0),
        "reinforcement_count": int(row[11] or 0),
        "contradiction_count": int(row[12] or 0),
        "last_reinforced_at": row[13].isoformat() if row[13] else None,
        "retention_action": row[14],
        "metadata": row[15] or {},
        "updated_at": row[16].isoformat() if row[16] else None,
        "retired_at": row[17].isoformat() if row[17] else None,
        "stale_at": row[18].isoformat() if row[18] else None,
        "lifecycle": {
            "state": row[9],
            "score": score.score,
            "current": row[9] in {"active", "confirmed", "reinforced"},
            "audit_visible": row[9] in {"superseded", "contradicted", "stale", "retired"},
            "explanation": score.explanation,
            "audit_events": [],
            "related_claims": [],
        },
    }


_RAW_CAPTURE_PAYLOAD_KEYS = {
    "body",
    "content",
    "html",
    "message",
    "raw",
    "raw_text",
    "summary",
    "text",
    "transcript",
}

_CAPTURE_PAYLOAD_PATH_KEYS = {"file", "path", "source", "source_dir"}


def _normalize_capture_review_status(value: str | None) -> str:
    normalized = str(value or "pending_review").strip().lower().replace("-", "_")
    if normalized not in _CAPTURE_REVIEW_STATUSES:
        raise ValueError(
            "capture review status must be one of: all, approved, blocked_missing_dependency, completed, failed, pending_review, rejected"
        )
    return normalized


def _metadata_only_payload(value: Any, *, key: str | None = None) -> Any:
    if isinstance(value, dict):
        return {
            item_key: _metadata_only_payload(item, key=item_key)
            for item_key, item in value.items()
            if item_key.lower() not in _RAW_CAPTURE_PAYLOAD_KEYS
        }
    if isinstance(value, list):
        return [_metadata_only_payload(item) for item in value[:50]]
    if key and key.lower() in _CAPTURE_PAYLOAD_PATH_KEYS and isinstance(value, str):
        return _path_leaf(value)
    return value


def _capture_job_row(row: tuple[Any, ...], *, metadata_only: bool = False) -> dict[str, Any]:
    payload = row[3] or {}
    if metadata_only:
        payload = _metadata_only_payload(payload)
    return {
        "id": row[0],
        "job_type": row[1],
        "status": row[2],
        "payload": payload,
        "attempts": row[4],
        "last_error": row[5],
        "created_at": row[6].isoformat(),
        "updated_at": row[7].isoformat(),
    }


def _path_leaf(value: str) -> str:
    cleaned = value.strip()
    if not cleaned:
        return cleaned
    normalized = cleaned.replace("\\", "/").rstrip("/")
    return normalized.rsplit("/", 1)[-1] or normalized


def _claim_lifecycle_events(cur: Any, claim_id: str) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT id::text, transition_type, actor, from_state, to_state,
               related_claim_id::text, confidence_delta, details, created_at
        FROM claim_lifecycle_events
        WHERE claim_id = %s
        ORDER BY created_at DESC, id DESC
        LIMIT 50
        """,
        (claim_id,),
    )
    return [
        {
            "id": row[0],
            "transition_type": row[1],
            "actor": row[2],
            "from_state": row[3],
            "to_state": row[4],
            "related_claim_id": row[5],
            "confidence_delta": float(row[6] or 0.0),
            "details": row[7] or {},
            "created_at": row[8].isoformat() if row[8] else None,
        }
        for row in cur.fetchall()
    ]


def _claim_related_claims(cur: Any, claim_id: str) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT id::text, from_claim_id::text, to_claim_id::text,
               relation_type, confidence, metadata, created_at
        FROM claim_relations
        WHERE from_claim_id = %s OR to_claim_id = %s
        ORDER BY relation_type ASC, created_at DESC, id DESC
        LIMIT 50
        """,
        (claim_id, claim_id),
    )
    return [
        {
            "id": row[0],
            "from_claim_id": row[1],
            "to_claim_id": row[2],
            "relation_type": row[3],
            "confidence": float(row[4] or 0.0),
            "metadata": row[5] or {},
            "created_at": row[6].isoformat() if row[6] else None,
        }
        for row in cur.fetchall()
    ]


def _claim_transition_state(transition: str) -> str:
    return {
        "reinforce": "reinforced",
        "confirm": "confirmed",
        "supersede": "superseded",
        "contradict": "contradicted",
        "stale": "stale",
        "deprioritize": "stale",
        "retire": "retired",
        "delete": "retired",
    }[transition]


def _age_days(value: Any) -> float | None:
    if value is None:
        return None
    if isinstance(value, datetime):
        timestamp = value if value.tzinfo else value.replace(tzinfo=UTC)
        return max((datetime.now(UTC) - timestamp.astimezone(UTC)).total_seconds() / 86400.0, 0.0)
    return None


def _clamp_float(value: float) -> float:
    return max(0.0, min(1.0, float(value)))


def _root_row(row: tuple[Any, ...]) -> dict[str, Any]:
    if len(row) >= 13:
        glob_mode = row[9]
        max_inline_bytes = row[10]
        heavy_threshold_bytes = row[11]
        metadata = row[12]
    else:
        metadata = row[11]
        glob_mode = (metadata or {}).get("glob_mode", "extend")
        max_inline_bytes = row[9]
        heavy_threshold_bytes = row[10]
    return {
        "id": row[0],
        "name": row[1],
        "root_path": row[2],
        "enabled": row[3],
        "recursive": row[4],
        "watch_enabled": row[5],
        "trust_rank": row[6],
        "include_globs": list(row[7] or []),
        "exclude_globs": list(row[8] or []),
        "glob_mode": glob_mode or "extend",
        "max_inline_bytes": max_inline_bytes,
        "heavy_threshold_bytes": heavy_threshold_bytes,
        "metadata": metadata,
    }


def _source_asset_lookup_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "root_name": row[1],
        "path": row[2],
        "uri": row[3],
        "file_kind": row[4],
        "mime_type": row[5],
        "extension": row[6],
        "size_bytes": row[7],
        "status": row[8],
        "canonical_asset_id": row[9],
        "is_duplicate": row[9] is not None,
        "duplicate_count": int(row[10] or 0),
        "last_seen_at": row[11].isoformat() if row[11] else None,
        "indexed_at": row[12].isoformat() if row[12] else None,
        "metadata": row[13] or {},
    }


def _source_asset_detail_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "root_name": row[1],
        "root_path": row[2],
        "path": row[3],
        "uri": row[4],
        "file_kind": row[5],
        "mime_type": row[6],
        "extension": row[7],
        "size_bytes": row[8],
        "status": "deleted" if row[14] else row[9],
        "canonical_asset_id": row[10],
        "is_duplicate": row[10] is not None,
        "duplicate_count": int(row[11] or 0),
        "last_seen_at": row[12].isoformat() if row[12] else None,
        "indexed_at": row[13].isoformat() if row[13] else None,
        "deleted_at": row[14].isoformat() if row[14] else None,
        "metadata": row[15] or {},
        "root_metadata": row[16] or {},
    }


def _asset_chunk_details(cur: Any, asset_id: str) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT id::text, chunk_index, title, body, modality, locator, token_estimate, metadata
        FROM asset_chunks
        WHERE asset_id = %s
        ORDER BY chunk_index
        LIMIT 200
        """,
        (asset_id,),
    )
    return [
        {
            "id": row[0],
            "chunk_index": row[1],
            "title": row[2],
            "body": row[3],
            "modality": row[4],
            "locator": row[5],
            "token_estimate": row[6],
            "metadata": row[7] or {},
        }
        for row in cur.fetchall()
    ]


def _mail_message_detail_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "profile_name": row[1],
        "source_type": row[2],
        "source_message_id": row[3],
        "source_folder": row[4],
        "internet_message_id": row[5],
        "content_hash": row[6],
        "export_id": row[7],
        "export_state": row[8],
        "error": row[9],
        "received_at": row[10].isoformat() if row[10] else None,
        "exported_at": row[11].isoformat() if row[11] else None,
        "metadata": row[12] or {},
    }


def _mail_post_process_event_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "profile_name": row[1],
        "sync_run_id": row[2],
        "mail_message_id": row[3],
        "provider": row[4],
        "policy": row[5],
        "action": row[6],
        "status": row[7],
        "dry_run": bool(row[8]),
        "commands": row[9] or [],
        "error": row[10],
        "metadata": row[11] or {},
        "created_at": row[12].isoformat() if row[12] else None,
    }


def _latest_crawl_run(cur: Any, root_id: str) -> dict[str, Any] | None:
    cur.execute(
        """
        SELECT id::text, status, started_at, finished_at, files_seen, files_changed,
               files_deleted, chunks_indexed, jobs_queued, errors, reason
        FROM crawl_runs
        WHERE root_id = %s
        ORDER BY started_at DESC
        LIMIT 1
        """,
        (root_id,),
    )
    row = cur.fetchone()
    if not row:
        return None
    return {
        "id": row[0],
        "status": row[1],
        "started_at": row[2].isoformat() if row[2] else None,
        "finished_at": row[3].isoformat() if row[3] else None,
        "files_seen": row[4],
        "files_changed": row[5],
        "files_deleted": row[6],
        "chunks_indexed": row[7],
        "jobs_queued": row[8],
        "errors": row[9] or [],
        "reason": row[10],
    }


def _root_asset_counts(cur: Any, root_id: str) -> dict[str, int]:
    cur.execute(
        """
        SELECT
            count(*) FILTER (WHERE deleted_at IS NULL) AS total,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'indexed') AS indexed,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'queued') AS queued,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'pending_stable') AS pending_stable,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'retrying_locked') AS retrying_locked,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'metadata_only') AS metadata_only,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'duplicate_suppressed') AS duplicate_suppressed,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status LIKE 'blocked%%') AS blocked,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'blocked_locked') AS blocked_locked,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'failed') AS failed,
            count(*) FILTER (WHERE deleted_at IS NOT NULL) AS deleted
        FROM source_assets
        WHERE root_id = %s
        """,
        (root_id,),
    )
    row = cur.fetchone()
    keys = [
        "total",
        "indexed",
        "queued",
        "pending_stable",
        "retrying_locked",
        "metadata_only",
        "duplicate_suppressed",
        "blocked",
        "blocked_locked",
        "failed",
        "deleted",
    ]
    return {key: int(row[index] or 0) for index, key in enumerate(keys)}


def _root_job_counts(cur: Any, root_name: str) -> dict[str, int]:
    cur.execute(
        """
        SELECT
            count(*) FILTER (WHERE status = 'pending') AS pending,
            count(*) FILTER (WHERE status = 'retrying_locked') AS retrying_locked,
            count(*) FILTER (WHERE status = 'retrying_vss_failed') AS retrying_vss_failed,
            count(*) FILTER (WHERE status = 'retrying_gpu_busy') AS retrying_gpu_busy,
            count(*) FILTER (WHERE status = 'running') AS running,
            count(*) FILTER (WHERE status LIKE 'blocked%%') AS blocked,
            count(*) FILTER (WHERE status = 'blocked_locked') AS blocked_locked,
            count(*) FILTER (WHERE status = 'blocked_vss_failed') AS blocked_vss_failed,
            count(*) FILTER (WHERE status = 'failed') AS failed,
            count(*) FILTER (WHERE status = 'completed') AS completed,
            count(*) FILTER (WHERE status = 'cancelled_duplicate') AS duplicate_suppressed
        FROM capture_jobs
        WHERE job_type LIKE 'corpus_%%'
          AND payload->>'root_name' = %s
        """,
        (root_name,),
    )
    row = cur.fetchone()
    keys = [
        "pending",
        "retrying_locked",
        "retrying_vss_failed",
        "retrying_gpu_busy",
        "running",
        "blocked",
        "blocked_locked",
        "blocked_vss_failed",
        "failed",
        "completed",
        "duplicate_suppressed",
    ]
    return {key: int(row[index] or 0) for index, key in enumerate(keys)}


def _recent_root_assets(cur: Any, root_id: str, *, limit: int) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT path, file_kind, extraction_status, extraction_tier, size_bytes,
               canonical_asset_id IS NOT NULL AS is_duplicate, deleted_at,
               last_seen_at, indexed_at, updated_at
        FROM source_assets
        WHERE root_id = %s
        ORDER BY updated_at DESC
        LIMIT %s
        """,
        (root_id, max(1, min(limit, 100))),
    )
    rows = []
    for row in cur.fetchall():
        status = row[2]
        if row[6]:
            status = "deleted"
        elif row[5]:
            status = "duplicate_suppressed"
        rows.append(
            {
                "path": row[0],
                "file_kind": row[1],
                "status": status,
                "extraction_tier": row[3],
                "size_bytes": row[4],
                "duplicate": row[5],
                "deleted_at": row[6].isoformat() if row[6] else None,
                "last_seen_at": row[7].isoformat() if row[7] else None,
                "indexed_at": row[8].isoformat() if row[8] else None,
                "updated_at": row[9].isoformat() if row[9] else None,
            }
        )
    return rows


def _recent_root_jobs(cur: Any, root_name: str, *, limit: int) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT id::text, job_type, status, payload, attempts, last_error, updated_at
        FROM capture_jobs
        WHERE job_type LIKE 'corpus_%%'
          AND payload->>'root_name' = %s
        ORDER BY updated_at DESC
        LIMIT %s
        """,
        (root_name, max(1, min(limit, 100))),
    )
    rows = []
    for row in cur.fetchall():
        payload = row[3] or {}
        rows.append(
            {
                "id": row[0],
                "job_type": row[1],
                "status": row[2],
                "path": payload.get("path"),
                "attempts": row[4],
                "last_error": row[5],
                "updated_at": row[6].isoformat() if row[6] else None,
            }
        )
    return rows


def _root_state(
    root: dict[str, Any],
    watcher: dict[str, Any],
    latest_crawl: dict[str, Any] | None,
    job_counts: dict[str, int],
) -> str:
    if not root["enabled"]:
        return "disabled"
    if latest_crawl and latest_crawl.get("status") == "running":
        return "crawling"
    if job_counts.get("running", 0) > 0:
        return "processing"
    if job_counts.get("pending", 0) > 0:
        return "queued"
    if job_counts.get("blocked", 0) > 0:
        return "blocked"
    if watcher.get("status") == "error":
        return "failed"
    if root["watch_enabled"]:
        age = watcher.get("heartbeat_age_seconds")
        if age is not None and age > 120:
            return "stale"
        if watcher.get("status") == "running":
            return "watching"
        return "watch_enabled"
    return "watch_off"


def _mail_profile_row(row: tuple[Any, ...]) -> dict[str, Any]:
    payload = {
        "id": row[0],
        "name": row[1],
        "source_type": row[2],
        "account": row[3],
        "server": row[4],
        "folder_paths": list(row[5] or []),
        "spool_path": row[6],
        "post_process_policy": row[7],
        "enabled": row[8],
        "trust_rank": row[9],
        "metadata": row[10],
    }
    if len(row) > 11:
        payload.update(
            {
                "sync_enabled": row[11],
                "sync_interval_seconds": row[12],
                "sync_window_days": row[13],
                "max_messages_per_run": row[14],
                "last_sync_at": row[15].isoformat() if row[15] else None,
                "next_sync_at": row[16].isoformat() if row[16] else None,
            }
        )
    else:
        payload.update(
            {
                "sync_enabled": False,
                "sync_interval_seconds": 900,
                "sync_window_days": 30,
                "max_messages_per_run": 200,
                "last_sync_at": None,
                "next_sync_at": None,
            }
        )
    return payload


def _mail_sync_run_row(row: tuple[Any, ...], *, include_profile: bool = False) -> dict[str, Any]:
    payload = {
        "id": row[0],
        "profile_name": row[1],
        "status": row[2],
        "trigger": row[3],
        "requested_by": row[4],
        "claimed_by": row[5],
        "claimed_at": row[6].isoformat() if row[6] else None,
        "worker_id": row[7],
        "attempt_count": int(row[8] or 0),
        "last_error": row[9],
        "next_attempt_at": row[10].isoformat() if row[10] else None,
        "drift_seconds": int(row[11] or 0),
        "missed_runs": int(row[12] or 0),
        "started_at": row[13].isoformat() if row[13] else None,
        "completed_at": row[14].isoformat() if row[14] else None,
        "messages_seen": int(row[15] or 0),
        "messages_exported": int(row[16] or 0),
        "last_cursor": row[17] or {},
        "errors": row[18] or [],
    }
    if include_profile and len(row) > 34:
        run_id = payload["id"]
        profile_row = (
            row[19],
            row[1],
            row[20],
            row[21],
            row[22],
            row[23],
            row[24],
            row[25],
            row[26],
            row[27],
            row[28],
            row[29],
            row[30],
            row[31],
            row[32],
            row[33],
            row[34],
        )
        profile_payload = _mail_profile_row(profile_row)
        payload.update(profile_payload)
        payload["id"] = run_id
        payload["run_id"] = run_id
        payload["profile_id"] = profile_payload["id"]
        payload["profile_name"] = profile_payload["name"]
    return payload


def _mail_last_error(errors: list[dict[str, Any]]) -> str | None:
    for item in errors:
        if isinstance(item, dict):
            message = item.get("error") or item.get("message")
            if message:
                return str(message)
    return None


def recover_interrupted_imap_sync_runs(
    *,
    worker_id: str,
    worker_started_at: datetime,
    url: str | None = None,
) -> dict[str, Any]:
    error = "interrupted_imap_sync: previous worker instance stopped before completing IMAP sync"
    event = [{"stage": "imap_scheduler", "error": error}]
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE mail_sync_runs
                SET status = 'backoff',
                    finished_at = COALESCE(finished_at, now()),
                    last_error = COALESCE(last_error, %s),
                    errors = CASE
                        WHEN errors IS NULL OR jsonb_typeof(errors) <> 'array' THEN %s::jsonb
                        ELSE errors || %s::jsonb
                    END,
                    next_attempt_at = now(),
                    updated_at = now()
                WHERE status IN ('claimed', 'running')
                  AND worker_id = %s
                  AND COALESCE(updated_at, started_at, claimed_at) < %s
                RETURNING id::text
                """,
                (error, _json_list(event), _json_list(event), worker_id, worker_started_at),
            )
            run_ids = [row[0] for row in cur.fetchall()]
    return {"recovered": len(run_ids), "run_ids": run_ids, "worker_id": worker_id}


def _expire_stale_imap_sync_runs(cur: Any, *, stale_after_seconds: int | None = None) -> int:
    seconds = _imap_sync_stale_after_seconds(stale_after_seconds)
    error = f"stale_imap_sync: IMAP sync exceeded {seconds} seconds without completion"
    event = [{"stage": "imap_scheduler", "error": error}]
    cur.execute(
        """
        UPDATE mail_sync_runs
        SET status = 'backoff',
            finished_at = COALESCE(finished_at, now()),
            last_error = COALESCE(last_error, %s),
            errors = CASE
                WHEN errors IS NULL OR jsonb_typeof(errors) <> 'array' THEN %s::jsonb
                ELSE errors || %s::jsonb
            END,
            next_attempt_at = now(),
            updated_at = now()
        WHERE status IN ('claimed', 'running')
          AND COALESCE(updated_at, started_at, claimed_at) < now() - make_interval(secs => %s::integer)
        """,
        (error, _json_list(event), _json_list(event), seconds),
    )
    return int(getattr(cur, "rowcount", 0) or 0)


def _imap_sync_stale_after_seconds(value: int | None = None) -> int:
    if value is not None:
        return max(300, min(int(value), 86_400))
    raw = os.getenv("FLUX_MAIL_SYNC_STALE_AFTER_SECONDS")
    if raw:
        try:
            return max(300, min(int(raw), 86_400))
        except ValueError:
            pass
    return DEFAULT_IMAP_SYNC_STALE_AFTER_SECONDS


def _mail_run_is_stale(run: dict[str, Any], *, stale_after_seconds: int | None = None) -> bool:
    if run.get("status") not in {"claimed", "running"}:
        return False
    timestamp = _mail_run_timestamp(run.get("started_at") or run.get("claimed_at"))
    if timestamp is None:
        return False
    return (datetime.now(UTC) - timestamp.astimezone(UTC)).total_seconds() > _imap_sync_stale_after_seconds(stale_after_seconds)


def _mail_run_timestamp(value: Any) -> datetime | None:
    if isinstance(value, datetime):
        return value if value.tzinfo else value.replace(tzinfo=UTC)
    if isinstance(value, str) and value.strip():
        text = value.strip()
        if text.endswith("Z"):
            text = f"{text[:-1]}+00:00"
        try:
            parsed = datetime.fromisoformat(text)
        except ValueError:
            return None
        return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)
    return None


def _mail_run_needs_action(run: dict[str, Any]) -> bool:
    return run.get("status") in {"failed", "backoff", "blocked_auth_required", "auth_expired", "auth_failed"} or _mail_run_is_stale(run)


def _mail_scheduler_diagnostic(run: dict[str, Any]) -> dict[str, Any]:
    status = str(run.get("status") or "unknown")
    profile_name = str(run.get("profile_name") or "unknown")
    stale = _mail_run_is_stale(run)
    message = str(
        run.get("last_error")
        or (
            f"stale_imap_sync: IMAP sync has been {status} longer than {_imap_sync_stale_after_seconds()} seconds"
            if stale
            else status
        )
    )
    auth_blocked = status in {"blocked_auth_required", "auth_expired", "auth_failed"}
    return {
        "code": "mail.imap_sync_stale" if stale else "mail.imap_auth_blocked" if auth_blocked else f"mail.imap_sync_{status}",
        "message": message,
        "severity": "warning" if auth_blocked or status == "backoff" else "error",
        "component": "mail",
        "stage": "imap_scheduler",
        "retryable": not auth_blocked,
        "user_action": (
            "Open Mail and reconnect Gmail OAuth for this profile."
            if auth_blocked
            else "Wait for the scheduler to retry, or run the mail sync after confirming no worker is still processing this profile."
            if stale
            else "Open Mail, inspect the run history, and retry after the backoff window or fixing the IMAP error."
        ),
        "technical_detail": f"IMAP scheduler run {run.get('id')} for {profile_name} ended with {status}: {message}",
        "target": {"type": "mail_profile", "id": profile_name},
        "links": [{"label": "Mail", "tab": "mail", "profile": profile_name}],
    }


def _mail_oauth_state_row(row: tuple[Any, ...], *, profile_name: str) -> dict[str, Any]:
    return {
        "id": row[0],
        "profile_name": profile_name,
        "provider": row[1],
        "state": row[2],
        "code_verifier": row[3],
        "redirect_uri": row[4],
        "client_config": row[5],
        "client_config_path": row[6],
        "metadata": row[7],
        "created_at": row[8].isoformat() if row[8] else None,
        "expires_at": row[9].isoformat() if row[9] else None,
        "consumed_at": row[10].isoformat() if row[10] else None,
    }


def _mail_oauth_state_lookup_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "profile_name": row[1],
        "provider": row[2],
        "state": row[3],
        "code_verifier": row[4],
        "redirect_uri": row[5],
        "client_config": row[6],
        "client_config_path": row[7],
        "metadata": row[8],
        "created_at": row[9].isoformat() if row[9] else None,
        "expires_at": row[10].isoformat() if row[10] else None,
        "consumed_at": row[11].isoformat() if row[11] else None,
    }


def _mail_oauth_token_row(row: tuple[Any, ...], *, profile_name: str) -> dict[str, Any]:
    return {
        "id": row[0],
        "profile_name": profile_name,
        "provider": row[1],
        "refresh_token": row[2],
        "scope": row[3],
        "token_type": row[4],
        "status": row[5],
        "last_error": row[6],
        "client_config": row[7],
        "metadata": row[8],
        "refreshed_at": row[9].isoformat() if row[9] else None,
        "expires_at": row[10].isoformat() if row[10] else None,
        "updated_at": row[11].isoformat() if row[11] else None,
    }


def _normalize_root_path(root_path: str | Path) -> str:
    value = str(root_path)
    if _looks_like_absolute_posix_path(value):
        return value
    try:
        from .host_agent import path_requires_host_agent

        if path_requires_host_agent(value):
            return value
    except Exception:
        pass
    return str(Path(value).expanduser().resolve())


def _looks_like_absolute_posix_path(value: str) -> bool:
    return value.startswith("/") and not value.startswith("//")


def _metadata_strict_indexing_enabled(metadata: dict[str, Any] | None) -> bool:
    if not isinstance(metadata, dict):
        return False
    value = metadata.get("strict_indexing")
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value)
    if isinstance(value, str):
        return value.strip().lower() in {"1", "true", "yes", "on", "strict"}
    return False


def _block_metadata_only_assets_for_strict_root(cur: Any, *, root_id: str) -> int:
    cur.execute(
        """
        SELECT id::text
        FROM source_assets
        WHERE root_id = %s
          AND extraction_status = 'metadata_only'
          AND deleted_at IS NULL
        """,
        (root_id,),
    )
    asset_ids = [row[0] for row in cur.fetchall()]
    if not asset_ids:
        return 0
    cur.execute("DELETE FROM code_references WHERE source_asset_id = ANY(%s::uuid[])", (asset_ids,))
    cur.execute("DELETE FROM code_symbols WHERE source_asset_id = ANY(%s::uuid[])", (asset_ids,))
    cur.execute("DELETE FROM asset_chunks WHERE asset_id = ANY(%s::uuid[])", (asset_ids,))
    cur.execute(
        """
        UPDATE source_assets
        SET extraction_status = 'blocked_by_policy',
            metadata = metadata || %s::jsonb,
            updated_at = now()
        WHERE id = ANY(%s::uuid[])
        """,
        (
            _json(
                {
                    "strict_indexing": True,
                    "metadata_only_blocked": True,
                    "readiness_status": "blocked_by_policy",
                    "readiness_reason": "Strict indexing root does not allow metadata-only assets.",
                    "original_status": "metadata_only",
                }
            ),
            asset_ids,
        ),
    )
    return len(asset_ids)


def _replace_container_child_assets(
    cur: Any,
    *,
    root_id: str,
    parent_asset_id: str,
    parent_relative_path: str,
    parent_uri: str,
    parent_mtime_ns: int,
    child_assets: tuple[Any, ...],
) -> None:
    cur.execute(
        """
        UPDATE source_assets
        SET deleted_at = now(),
            extraction_status = 'deleted',
            updated_at = now()
        WHERE root_id = %s
          AND metadata->>'container_asset_id' = %s
          AND deleted_at IS NULL
        """,
        (root_id, parent_asset_id),
    )
    for child in child_assets:
        member_path = str(getattr(child, "member_path"))
        child_path = f"{parent_relative_path}/{member_path}"
        cur.execute(
            """
            SELECT id::text
            FROM source_assets
            WHERE root_id = %s
              AND path = %s
            """,
            (root_id, child_path),
        )
        previous = cur.fetchone()
        previous_id = previous[0] if previous else None
        canonical_id = _find_canonical_asset_id(cur, getattr(child, "content_hash", None), previous_id)
        status = str(getattr(child, "extraction_status", "metadata_only") or "metadata_only")
        if canonical_id and status == "indexed":
            status = "duplicate_suppressed"
        metadata = {
            "source": "container_extractor",
            "container_asset_id": parent_asset_id,
            "parent_asset_id": parent_asset_id,
            "container_member_path": member_path,
            **(getattr(child, "metadata", {}) or {}),
        }
        cur.execute(
            """
            INSERT INTO source_assets (
                root_id, path, uri, file_kind, mime_type, extension, size_bytes,
                mtime_ns, quick_hash, content_hash, canonical_asset_id,
                extraction_status, extraction_tier, last_seen_at, indexed_at, deleted_at, metadata
            )
            VALUES (
                %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s,
                now(), CASE WHEN %s = 'indexed' THEN now() ELSE NULL END, NULL, %s::jsonb
            )
            ON CONFLICT (root_id, path) DO UPDATE SET
                uri = EXCLUDED.uri,
                file_kind = EXCLUDED.file_kind,
                mime_type = EXCLUDED.mime_type,
                extension = EXCLUDED.extension,
                size_bytes = EXCLUDED.size_bytes,
                mtime_ns = EXCLUDED.mtime_ns,
                quick_hash = EXCLUDED.quick_hash,
                content_hash = EXCLUDED.content_hash,
                canonical_asset_id = EXCLUDED.canonical_asset_id,
                extraction_status = EXCLUDED.extraction_status,
                extraction_tier = EXCLUDED.extraction_tier,
                last_seen_at = now(),
                indexed_at = CASE WHEN EXCLUDED.extraction_status = 'indexed' THEN now() ELSE source_assets.indexed_at END,
                deleted_at = NULL,
                metadata = source_assets.metadata || EXCLUDED.metadata,
                updated_at = now()
            RETURNING id::text
            """,
            (
                root_id,
                child_path,
                f"{parent_uri}#member={quote(member_path, safe='')}",
                getattr(child, "file_kind", "binary"),
                getattr(child, "mime_type", None),
                getattr(child, "extension", ""),
                int(getattr(child, "size_bytes", 0) or 0),
                parent_mtime_ns,
                getattr(child, "quick_hash", None),
                getattr(child, "content_hash", None),
                canonical_id,
                status,
                getattr(child, "extraction_tier", "metadata_only"),
                status,
                _json(metadata),
            ),
        )
        child_id = cur.fetchone()[0]
        _replace_asset_chunks(cur, child_id, () if canonical_id else tuple(getattr(child, "chunks", ()) or ()))


def _replace_asset_chunks(cur: Any, asset_id: str, chunks: tuple[Any, ...]) -> int:
    mail_context = _managed_mail_asset_context(cur, asset_id)
    cur.execute("DELETE FROM code_references WHERE source_asset_id = %s", (asset_id,))
    cur.execute("DELETE FROM code_symbols WHERE source_asset_id = %s", (asset_id,))
    cur.execute("DELETE FROM asset_chunks WHERE asset_id = %s", (asset_id,))
    inserted = 0
    for chunk in chunks:
        chunk_title = strip_postgres_nul(str(chunk.title))
        chunk_body = strip_postgres_nul(str(chunk.body))
        chunk_modality = strip_postgres_nul(str(chunk.modality))
        chunk_locator = _postgres_text_or_none(chunk.locator)
        chunk_metadata = sanitize_postgres_text_value(dict(getattr(chunk, "metadata", {}) or {}))
        db_body = chunk_body
        if mail_context is not None:
            content_ref = mail_content_store.write_mail_content(
                root_name=mail_context["root_name"],
                asset_path=mail_context["asset_path"],
                chunk_index=int(chunk.chunk_index),
                title=chunk_title,
                text=chunk_body,
                kind=mail_context["kind"],
            )
            chunk_metadata["sidecar_ref"] = {"source": "managed_mail", **content_ref}
            db_body = ""
        cur.execute(
            """
            INSERT INTO asset_chunks (
                asset_id, chunk_index, title, body, modality, locator, token_estimate, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s::jsonb)
            RETURNING id::text
            """,
            (
                asset_id,
                chunk.chunk_index,
                chunk_title,
                db_body,
                chunk_modality,
                chunk_locator,
                chunk.token_estimate,
                _json(_sanitize_operational_metadata(chunk_metadata)),
            ),
        )
        chunk_id = cur.fetchone()[0]
        _insert_code_metadata_for_chunk(cur, asset_id=asset_id, chunk_id=chunk_id, chunk=chunk)
        inserted += 1
    if inserted:
        _enqueue_corpus_search_index_sync_for_asset(cur, asset_id=asset_id)
    return inserted


def _append_or_upsert_asset_chunks(cur: Any, asset_id: str, chunks: tuple[Any, ...]) -> int:
    if not chunks:
        return 0
    mail_context = _managed_mail_asset_context(cur, asset_id)
    inserted = 0
    for chunk in chunks:
        chunk_index = int(chunk.chunk_index)
        cur.execute(
            """
            SELECT id::text FROM asset_chunks
            WHERE asset_id = %s
              AND chunk_index = %s
            """,
            (asset_id, chunk_index),
        )
        previous = cur.fetchone()
        if previous:
            cur.execute("DELETE FROM asset_chunks WHERE asset_id = %s AND chunk_index = %s", (asset_id, chunk_index))
        chunk_title = strip_postgres_nul(str(chunk.title))
        chunk_body = strip_postgres_nul(str(chunk.body))
        chunk_modality = strip_postgres_nul(str(chunk.modality))
        chunk_locator = _postgres_text_or_none(chunk.locator)
        chunk_metadata = sanitize_postgres_text_value(dict(getattr(chunk, "metadata", {}) or {}))
        db_body = chunk_body
        if mail_context is not None:
            content_ref = mail_content_store.write_mail_content(
                root_name=mail_context["root_name"],
                asset_path=mail_context["asset_path"],
                chunk_index=chunk_index,
                title=chunk_title,
                text=chunk_body,
                kind=mail_context["kind"],
            )
            chunk_metadata["sidecar_ref"] = {"source": "managed_mail", **content_ref}
            db_body = ""
        cur.execute(
            """
            INSERT INTO asset_chunks (
                asset_id, chunk_index, title, body, modality, locator, token_estimate, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s::jsonb)
            RETURNING id::text
            """,
            (
                asset_id,
                chunk_index,
                chunk_title,
                db_body,
                chunk_modality,
                chunk_locator,
                chunk.token_estimate,
                _json(_sanitize_operational_metadata(chunk_metadata)),
            ),
        )
        chunk_id = cur.fetchone()[0]
        _insert_code_metadata_for_chunk(cur, asset_id=asset_id, chunk_id=chunk_id, chunk=chunk)
        inserted += 1
    if inserted:
        _enqueue_corpus_search_index_sync_for_asset(cur, asset_id=asset_id)
    return inserted


def _managed_mail_asset_context(cur: Any, asset_id: str) -> dict[str, str] | None:
    cur.execute(
        """
        SELECT a.path, r.name, r.root_path, r.metadata
        FROM source_assets a
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE a.id = %s
        """,
        (asset_id,),
    )
    row = cur.fetchone()
    if not row:
        return None
    asset_path = str(row[0] or "")
    root_metadata = row[3] if isinstance(row[3], dict) else {}
    kind = mail_content_store.managed_mail_content_kind(asset_path)
    if not kind or not mail_content_store.is_managed_mail_content(asset_path, root_metadata):
        return None
    return {
        "asset_path": asset_path,
        "root_name": str(row[1] or ""),
        "root_path": str(row[2] or ""),
        "kind": kind,
    }


def _insert_code_metadata_for_chunk(cur: Any, *, asset_id: str, chunk_id: str, chunk: Any) -> None:
    metadata = dict(getattr(chunk, "metadata", {}) or {})
    symbols = metadata.get("code_symbols") if isinstance(metadata.get("code_symbols"), list) else []
    references = metadata.get("code_references") if isinstance(metadata.get("code_references"), list) else []
    for symbol in symbols:
        if not isinstance(symbol, dict):
            continue
        path = str(symbol.get("path") or _chunk_code_path(metadata))
        cur.execute(
            """
            INSERT INTO code_symbols (
                source_asset_id, asset_chunk_id, language, symbol_kind, name, qualified_name,
                path, line_start, line_end, byte_start, byte_end, parent_symbol, exported,
                signature, parser_status, confidence, scope_hash, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb)
            ON CONFLICT (source_asset_id, qualified_name, symbol_kind, line_start) DO UPDATE SET
                asset_chunk_id = EXCLUDED.asset_chunk_id,
                line_end = EXCLUDED.line_end,
                signature = EXCLUDED.signature,
                parser_status = EXCLUDED.parser_status,
                confidence = EXCLUDED.confidence,
                metadata = EXCLUDED.metadata,
                updated_at = now()
            """,
            (
                asset_id,
                chunk_id,
                str(symbol.get("language") or metadata.get("language") or "text"),
                str(symbol.get("symbol_kind") or metadata.get("symbol_kind") or "unknown"),
                str(symbol.get("name") or symbol.get("qualified_name") or ""),
                str(symbol.get("qualified_name") or symbol.get("name") or ""),
                path,
                int(symbol.get("line_start") or 1),
                int(symbol.get("line_end") or symbol.get("line_start") or 1),
                _optional_int(symbol.get("byte_start")),
                _optional_int(symbol.get("byte_end")),
                symbol.get("parent_symbol"),
                symbol.get("exported"),
                symbol.get("signature"),
                str(symbol.get("parser_status") or metadata.get("parser_status") or "parsed"),
                float(symbol.get("confidence") or 1.0),
                str(symbol.get("scope_hash") or code_scope_hash(None, path)),
                _json(_sanitize_operational_metadata(dict(symbol.get("metadata") or {}))),
            ),
        )
    for reference in references:
        if not isinstance(reference, dict):
            continue
        path = str(reference.get("path") or _chunk_code_path(metadata))
        cur.execute(
            """
            INSERT INTO code_references (
                source_asset_id, asset_chunk_id, language, relationship_kind, source_symbol,
                target, path, line_start, line_end, parser_status, confidence, scope_hash, metadata
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb)
            """,
            (
                asset_id,
                chunk_id,
                str(reference.get("language") or metadata.get("language") or "text"),
                str(reference.get("relationship_kind") or metadata.get("relationship") or "reference"),
                reference.get("source_symbol"),
                str(reference.get("target") or ""),
                path,
                int(reference.get("line_start") or 1),
                int(reference.get("line_end") or reference.get("line_start") or 1),
                str(reference.get("parser_status") or metadata.get("parser_status") or "parsed"),
                float(reference.get("confidence") or 0.8),
                str(reference.get("scope_hash") or code_scope_hash(None, path)),
                _json(_sanitize_operational_metadata(dict(reference.get("metadata") or {}))),
            ),
        )


def _chunk_code_path(metadata: dict[str, Any]) -> str:
    code = metadata.get("code")
    if isinstance(code, dict):
        return str(code.get("source_path") or "")
    return ""


def _optional_int(value: Any) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


JOB_TYPE_SCHEDULE_OVERRIDES: dict[str, dict[str, int]] = {
    "corpus_sync_root": {"priority": 95, "time_budget_seconds": 1800},
}


def _job_schedule_metadata(job_type: str) -> dict[str, Any]:
    family = job_family_for_type(job_type)
    schedule = {
        "job_family": family,
        "resource_class": resource_class_for_family(family),
        "priority": default_priority_for_family(family),
        "time_budget_seconds": time_budget_for_family(family),
    }
    schedule.update(JOB_TYPE_SCHEDULE_OVERRIDES.get(str(job_type or "").lower(), {}))
    return schedule


def _normalize_search_owner_class(owner_class: str | None) -> str:
    normalized = str(owner_class or "all").strip().lower()
    aliases = {
        "asset_chunks": "corpus",
        "chunk": "corpus",
        "chunks": "corpus",
        "episode": "episodes",
        "claim": "claims",
    }
    normalized = aliases.get(normalized, normalized)
    if normalized not in {"all", "corpus", "episodes", "claims"}:
        raise ValueError("owner_class must be all, corpus, episodes, or claims")
    return normalized


def search_index_status(*, root_name: str | None = None, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_sql = "WHERE root_name = %s" if root_name else ""
    params: tuple[Any, ...] = (root_name,) if root_name else ()
    corpus_root_sql = "AND r.name = %s" if root_name else ""
    corpus_root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT index_status, count(*)::integer
                FROM search_index_records
                {root_sql}
                GROUP BY index_status
                ORDER BY index_status
                """,
                params,
            )
            by_status = {str(row[0]): int(row[1] or 0) for row in cur.fetchall()}
            cur.execute(
                f"""
                SELECT vespa_document_id, owner_table, owner_id::text, root_name,
                       source_hash, embedding_model, embedding_dimensions,
                       index_status, last_error, sync_started_at, sync_completed_at, updated_at
                FROM search_index_records
                {root_sql}
                ORDER BY updated_at DESC
                LIMIT 20
                """,
                params,
            )
            recent = [
                {
                    "vespa_document_id": row[0],
                    "owner_table": row[1],
                    "owner_id": row[2],
                    "root_name": row[3],
                    "source_hash": row[4],
                    "embedding_model": row[5],
                    "embedding_dimensions": row[6],
                    "index_status": row[7],
                    "last_error": row[8],
                    "sync_started_at": row[9].isoformat() if row[9] else None,
                    "sync_completed_at": row[10].isoformat() if row[10] else None,
                    "updated_at": row[11].isoformat() if row[11] else None,
                }
                for row in cur.fetchall()
            ]
            cur.execute(
                f"""
                SELECT count(*)::integer
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                LEFT JOIN search_index_records rec ON rec.owner_table = 'asset_chunks'
                                                  AND rec.owner_id = c.id
                                                  AND rec.embedding_model = %s
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND a.extraction_status = 'indexed'
                  AND rec.owner_id IS NULL
                  {corpus_root_sql}
                """,
                ("Snowflake/snowflake-arctic-embed-l-v2.0", *corpus_root_params),
            )
            missing_row = cur.fetchall()
            missing_corpus_asset_chunks = int((missing_row[0][0] if missing_row else 0) or 0)
    existing_pending_work = (
        int(by_status.get("pending") or 0)
        + int(by_status.get("failed") or 0)
        + int(by_status.get("syncing") or 0)
    )
    return {
        "root_name": root_name,
        "search_engine": "vespa",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
        "summary": {
            "total": sum(by_status.values()),
            "by_status": by_status,
            "missing": missing_corpus_asset_chunks,
            "pending_work": existing_pending_work + missing_corpus_asset_chunks,
        },
        "missing": {
            "corpus": {
                "asset_chunks": missing_corpus_asset_chunks,
            },
        },
        "recent": recent,
    }


def enqueue_search_index_sync(
    *,
    owner_class: str = "all",
    root_name: str | None = None,
    limit: int = DEFAULT_SEARCH_INDEX_JOB_LIMIT,
    retry_capacity_blockers: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = _search_index_job_limit(limit)
    page_size = min(_search_index_page_size(), row_limit)
    payload = {
        "owner_class": str(owner_class or "all"),
        "root_name": root_name,
        "limit": row_limit,
        "page_size": page_size,
        "page_sequence": 0,
        "search_engine": "vespa",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
    }
    if retry_capacity_blockers:
        payload["retry_capacity_blockers"] = True
    schedule = _job_schedule_metadata("search_index_sync")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            queued = _enqueue_unique_capture_job_with_cursor(
                cur,
                job_type="search_index_sync",
                payload=payload,
                job_family=schedule["job_family"],
                resource_class=schedule["resource_class"],
                priority=schedule["priority"],
                time_budget_seconds=schedule["time_budget_seconds"],
                telemetry={"stage": "queued", "owner_class": payload["owner_class"], "root_name": root_name},
                audit_details={"job_type": "search_index_sync", "owner_class": payload["owner_class"], "root_name": root_name},
            )
    return {"queued": 0 if queued["deduped"] else 1, "job_id": queued["job_id"], "deduped": queued["deduped"], "reused": queued["reused"], **payload}


def enqueue_search_index_sync_continuation(
    *,
    owner_class: str,
    root_name: str | None,
    limit: int,
    page_size: int,
    continuation_of: str,
    page_sequence: int,
    retry_capacity_blockers: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = _search_index_job_limit(limit)
    resolved_page_size = min(_search_index_page_size(page_size), row_limit)
    payload = {
        "owner_class": str(owner_class or "all"),
        "root_name": root_name,
        "limit": row_limit,
        "page_size": resolved_page_size,
        "page_sequence": max(1, int(page_sequence or 1)),
        "continuation_of": str(continuation_of or ""),
        "search_engine": "vespa",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
    }
    if retry_capacity_blockers:
        payload["retry_capacity_blockers"] = True
    schedule = _job_schedule_metadata("search_index_sync")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            queued = _enqueue_unique_capture_job_with_cursor(
                cur,
                job_type="search_index_sync",
                payload=payload,
                job_family=schedule["job_family"],
                resource_class=schedule["resource_class"],
                priority=schedule["priority"],
                time_budget_seconds=schedule["time_budget_seconds"],
                telemetry={
                    "stage": "queued",
                    "owner_class": payload["owner_class"],
                    "root_name": root_name,
                    "continuation_of": payload["continuation_of"],
                    "page_sequence": payload["page_sequence"],
                },
                audit_details={
                    "job_type": "search_index_sync",
                    "owner_class": payload["owner_class"],
                    "root_name": root_name,
                    "continuation_of": payload["continuation_of"],
                    "page_sequence": payload["page_sequence"],
                },
            )
    return {"queued": 0 if queued["deduped"] else 1, "job_id": queued["job_id"], "deduped": queued["deduped"], "reused": queued["reused"], **payload}


def _enqueue_corpus_search_index_sync_for_asset(cur: Any, *, asset_id: str) -> dict[str, Any] | None:
    cur.execute(
        """
        SELECT r.name
        FROM source_assets a
        JOIN monitored_roots r ON r.id = a.root_id
        WHERE a.id = %s
          AND r.enabled
          AND a.deleted_at IS NULL
          AND a.canonical_asset_id IS NULL
          AND a.extraction_status = 'indexed'
        """,
        (asset_id,),
    )
    row = cur.fetchone()
    if not row:
        return None
    root_name = str(row[0] or "").strip()
    if not root_name:
        return None
    row_limit = _search_index_job_limit()
    page_size = min(_search_index_page_size(), row_limit)
    payload = {
        "owner_class": "corpus",
        "root_name": root_name,
        "limit": row_limit,
        "page_size": page_size,
        "page_sequence": 0,
        "search_engine": "vespa",
        "embedding_model": "Snowflake/snowflake-arctic-embed-l-v2.0",
        "embedding_dimensions": 1024,
    }
    schedule = _job_schedule_metadata("search_index_sync")
    return _enqueue_unique_capture_job_with_cursor(
        cur,
        job_type="search_index_sync",
        payload=payload,
        job_family=schedule["job_family"],
        resource_class=schedule["resource_class"],
        priority=schedule["priority"],
        time_budget_seconds=schedule["time_budget_seconds"],
        telemetry={"stage": "queued", "owner_class": "corpus", "root_name": root_name, "reason": "corpus_chunks_changed"},
        audit_details={
            "job_type": "search_index_sync",
            "owner_class": "corpus",
            "root_name": root_name,
            "reason": "corpus_chunks_changed",
        },
    )


def mark_search_index_rebuild(
    *,
    root_name: str | None = None,
    confirmed: bool = False,
    url: str | None = None,
) -> dict[str, Any]:
    if not confirmed:
        raise ValueError("search index rebuild requires --confirm")
    root_sql = "WHERE root_name = %s" if root_name else ""
    params: tuple[Any, ...] = (root_name,) if root_name else ()
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                UPDATE search_index_records
                   SET index_status = 'pending',
                       last_error = NULL,
                       sync_started_at = NULL,
                       sync_completed_at = NULL,
                       updated_at = now()
                {root_sql}
                """,
                params,
            )
            marked = int(cur.rowcount or 0)
    return {"marked_pending": marked, "root_name": root_name, "confirmed": True}


def purge_deleted_corpus_residue(
    *,
    confirmed: bool = False,
    adapter: Any | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            records = _fetch_deleted_corpus_search_index_records(cur)
            residue_roots = _fetch_deleted_corpus_residue_roots(cur)
            active_hashes, active_frame_timestamps = _collect_active_cache_inputs(cur)
    roots = sorted(
        {
            str(record.get("root_name") or "").strip()
            for record in records
            if str(record.get("root_name") or "").strip()
        }
        | residue_roots
    )
    cache_result = purge_derived_cache_entries(
        active_source_hashes=active_hashes,
        active_frame_timestamps_by_hash=active_frame_timestamps,
        purge_unreferenced=True,
        dry_run=not confirmed,
    )
    if roots:
        purge_result = purge_corpus_search_index_for_roots(
            root_names=roots,
            confirmed=confirmed,
            adapter=adapter,
            url=url,
        )
    else:
        purge_result = {
            "dry_run": not confirmed,
            "roots": [],
            "vespa_documents_planned": 0,
            "vespa_documents_deleted": 0,
            "search_index_records_deleted": 0,
            "semantic_duplicate_clusters_deleted": 0,
            "jobs_deleted": 0,
            "vespa_remaining_by_root": {},
            "errors": [],
        }
    return {
        **purge_result,
        "roots": roots,
        "cache": cache_result,
    }


def purge_corpus_search_index_for_roots(
    *,
    root_names: Iterable[str],
    confirmed: bool = False,
    adapter: Any | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    roots = sorted({str(root_name or "").strip() for root_name in root_names if str(root_name or "").strip()})
    result: dict[str, Any] = {
        "dry_run": not confirmed,
        "roots": roots,
        "vespa_documents_planned": 0,
        "vespa_documents_deleted": 0,
        "search_index_records_deleted": 0,
        "semantic_duplicate_clusters_deleted": 0,
        "jobs_deleted": 0,
        "vespa_remaining_by_root": {},
        "errors": [],
    }
    if not roots:
        return result

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            records = _fetch_corpus_search_index_records_for_roots(cur, roots=roots)
            result["vespa_documents_planned"] = len(records)
            if not confirmed:
                return result

            vespa = adapter or _vespa_search_adapter()
            successful_document_ids: list[str] = []
            for record in records:
                document_id = str(record.get("vespa_document_id") or "")
                if not document_id:
                    continue
                try:
                    vespa.delete(document_id)
                except Exception as exc:
                    result["errors"].append(
                        {
                            "stage": "vespa_delete",
                            "root_name": record.get("root_name"),
                            "vespa_document_id": document_id,
                            "error": str(exc)[:1000],
                        }
                    )
                    continue
                successful_document_ids.append(document_id)
                result["vespa_documents_deleted"] += 1

            if successful_document_ids:
                cur.execute(
                    """
                    DELETE FROM search_index_records
                    WHERE owner_table = 'asset_chunks'
                      AND vespa_document_id = ANY(%s::text[])
                    """,
                    (successful_document_ids,),
                )
                result["search_index_records_deleted"] = int(cur.rowcount or 0)

            cur.execute(
                """
                DELETE FROM semantic_duplicate_clusters
                WHERE memory_class = 'corpus'
                  AND root_name = ANY(%s::text[])
                """,
                (roots,),
            )
            result["semantic_duplicate_clusters_deleted"] = int(cur.rowcount or 0)

            cur.execute(
                """
                DELETE FROM capture_jobs
                WHERE (job_type LIKE 'corpus_%%' OR job_type = 'search_index_sync')
                  AND payload->>'root_name' = ANY(%s::text[])
                """,
                (roots,),
            )
            result["jobs_deleted"] = int(cur.rowcount or 0)

            for root_name in roots:
                if hasattr(vespa, "count_by_root_name"):
                    try:
                        result["vespa_remaining_by_root"][root_name] = int(
                            vespa.count_by_root_name(root_name, owner_table="asset_chunks")
                        )
                    except Exception as exc:
                        result["vespa_remaining_by_root"][root_name] = None
                        result["errors"].append(
                            {
                                "stage": "vespa_verify",
                                "root_name": root_name,
                                "error": str(exc)[:1000],
                            }
                        )

    return result


def _fetch_deleted_corpus_search_index_records(cur: Any) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT rec.vespa_document_id, rec.root_name, rec.index_status
        FROM search_index_records rec
        LEFT JOIN monitored_roots r ON r.name = rec.root_name
        WHERE rec.owner_table = 'asset_chunks'
          AND NULLIF(rec.root_name, '') IS NOT NULL
          AND r.id IS NULL
        ORDER BY rec.root_name, rec.updated_at
        """
    )
    try:
        rows = cur.fetchall()
    except AttributeError:
        rows = []
    return [_corpus_search_index_record_row(row) for row in rows]


def _fetch_deleted_corpus_residue_roots(cur: Any) -> set[str]:
    roots: set[str] = set()
    cur.execute(
        """
        SELECT DISTINCT sc.root_name
        FROM semantic_duplicate_clusters sc
        LEFT JOIN monitored_roots r ON r.name = sc.root_name
        WHERE sc.memory_class = 'corpus'
          AND NULLIF(sc.root_name, '') IS NOT NULL
          AND r.id IS NULL
        ORDER BY sc.root_name
        """
    )
    try:
        semantic_rows = cur.fetchall()
    except AttributeError:
        semantic_rows = []
    roots.update(str(row[0] or "").strip() for row in semantic_rows if str(row[0] or "").strip())

    cur.execute(
        """
        SELECT DISTINCT j.payload->>'root_name'
        FROM capture_jobs j
        LEFT JOIN monitored_roots r ON r.name = j.payload->>'root_name'
        WHERE (j.job_type LIKE 'corpus_%%' OR j.job_type = 'search_index_sync')
          AND NULLIF(j.payload->>'root_name', '') IS NOT NULL
          AND r.id IS NULL
        ORDER BY j.payload->>'root_name'
        """
    )
    try:
        job_rows = cur.fetchall()
    except AttributeError:
        job_rows = []
    roots.update(str(row[0] or "").strip() for row in job_rows if str(row[0] or "").strip())
    return roots


def _fetch_corpus_search_index_records_for_roots(cur: Any, *, roots: list[str]) -> list[dict[str, Any]]:
    cur.execute(
        """
        SELECT rec.vespa_document_id, rec.root_name, rec.index_status
        FROM search_index_records rec
        LEFT JOIN monitored_roots r ON r.name = rec.root_name
        WHERE rec.owner_table = 'asset_chunks'
          AND rec.root_name = ANY(%s::text[])
        ORDER BY rec.root_name, rec.updated_at
        """,
        (roots,),
    )
    try:
        rows = cur.fetchall()
    except AttributeError:
        rows = []
    return [_corpus_search_index_record_row(row) for row in rows]


def _corpus_search_index_record_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "vespa_document_id": str(row[0] or ""),
        "root_name": str(row[1] or ""),
        "index_status": str(row[2] or ""),
    }


def _vespa_search_adapter() -> Any:
    from .search_index import VespaSearchAdapter

    return VespaSearchAdapter(base_url=os.environ.get("FLUX_KB_RETRIEVAL_VESPA_BASE_URL") or "http://127.0.0.1:8080")


def _bounded_int_env(name: str, *, default: int, minimum: int, maximum: int) -> int:
    raw_value = os.environ.get(name)
    try:
        value = int(raw_value) if raw_value is not None else default
    except (TypeError, ValueError):
        value = default
    return max(minimum, min(value, maximum))


def _search_index_job_limit(limit: int | None = None) -> int:
    configured = _bounded_int_env(
        "FLUX_KB_SEARCH_INDEX_JOB_LIMIT",
        default=DEFAULT_SEARCH_INDEX_JOB_LIMIT,
        minimum=1,
        maximum=MAX_SEARCH_INDEX_JOB_LIMIT,
    )
    try:
        requested = int(limit) if limit is not None else configured
    except (TypeError, ValueError):
        requested = configured
    return max(1, min(requested, configured))


def _search_index_page_size(page_size: int | None = None) -> int:
    configured = _bounded_int_env(
        "FLUX_KB_SEARCH_INDEX_PAGE_SIZE",
        default=DEFAULT_SEARCH_INDEX_PAGE_SIZE,
        minimum=1,
        maximum=MAX_SEARCH_INDEX_PAGE_SIZE,
    )
    try:
        requested = int(page_size) if page_size is not None else configured
    except (TypeError, ValueError):
        requested = configured
    return max(1, min(requested, configured))


def _search_index_fetch_limit(limit: int | None = None) -> int:
    try:
        requested = int(limit) if limit is not None else DEFAULT_SEARCH_INDEX_PAGE_SIZE
    except (TypeError, ValueError):
        requested = DEFAULT_SEARCH_INDEX_PAGE_SIZE
    return max(1, min(requested, MAX_SEARCH_INDEX_PAGE_SIZE))


def _search_index_embedding_batch_size() -> int:
    return _bounded_int_env(
        "FLUX_KB_SEARCH_INDEX_EMBEDDING_BATCH_SIZE",
        default=DEFAULT_SEARCH_INDEX_EMBEDDING_BATCH_SIZE,
        minimum=1,
        maximum=512,
    )


def _search_index_text_max_chars() -> int:
    return _bounded_int_env(
        "FLUX_KB_SEARCH_INDEX_TEXT_MAX_CHARS",
        default=DEFAULT_SEARCH_INDEX_TEXT_MAX_CHARS,
        minimum=1,
        maximum=MAX_SEARCH_INDEX_TEXT_MAX_CHARS,
    )


def _bounded_search_index_body(value: Any) -> tuple[str, int, int]:
    text = str(value or "")
    hydrated_chars = len(text)
    max_chars = _search_index_text_max_chars()
    if hydrated_chars <= max_chars:
        return text, hydrated_chars, 0
    return text[:max_chars], hydrated_chars, hydrated_chars - max_chars


def _current_process_rss_bytes() -> int | None:
    try:
        with open("/proc/self/statm", "r", encoding="utf-8") as handle:
            parts = handle.read().split()
        if len(parts) < 2:
            return None
        return int(parts[1]) * int(os.sysconf("SC_PAGE_SIZE"))
    except Exception:
        return None


def sync_search_index(
    *,
    owner_class: str = "all",
    root_name: str | None = None,
    limit: int = DEFAULT_SEARCH_INDEX_JOB_LIMIT,
    page_size: int | None = None,
    page_sequence: int = 0,
    retry_capacity_blockers: bool = False,
    embedding_provider: Any | None = None,
    adapter: Any | None = None,
    vespa_base_url: str | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    from .embeddings import SnowflakeEmbeddingProvider
    from .search_index import (
        SNOWFLAKE_EMBEDDING_DIMENSIONS,
        SNOWFLAKE_EMBEDDING_MODEL,
        VespaSearchAdapter,
        build_vespa_document,
    )

    normalized_class = _normalize_search_owner_class(owner_class)
    row_limit = _search_index_job_limit(limit)
    resolved_page_size = min(_search_index_page_size(page_size), row_limit)
    provider = embedding_provider or SnowflakeEmbeddingProvider(
        model=SNOWFLAKE_EMBEDDING_MODEL,
        dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
        timeout_seconds=_configured_search_index_embedding_timeout_seconds(),
    )
    resolved_vespa_base_url = vespa_base_url or os.environ.get("FLUX_KB_RETRIEVAL_VESPA_BASE_URL") or "http://127.0.0.1:8080"
    vespa = adapter or VespaSearchAdapter(base_url=resolved_vespa_base_url)
    result: dict[str, Any] = {
        "owner_class": normalized_class,
        "root_name": root_name,
        "limit": row_limit,
        "page_size": resolved_page_size,
        "page_sequence": max(0, int(page_sequence or 0)),
        "search_engine": "vespa",
        "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
        "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
        "model_generation": "snowflake-qwen-paddleocr-v1",
        "requested": 0,
        "rows_loaded": 0,
        "indexed": 0,
        "deleted": 0,
        "skipped_unchanged": 0,
        "failed": 0,
        "errors": [],
        "embedding_batch_size": _search_index_embedding_batch_size(),
        "embedding_batches": 0,
        "embedding_attempted_size_histogram": {},
        "embedding_split_count": 0,
        "embedding_smallest_attempted_size": None,
        "embedding_capacity_state": None,
        "non_retryable": False,
        "non_retryable_blocker": None,
        "skipped_capacity_blockers": 0,
        "hydrated_body_chars": 0,
        "truncated_body_chars": 0,
        "vespa_feed_count": 0,
        "vespa_feed_latency_ms_total": 0.0,
        "vespa_feed_latency_ms_max": 0.0,
        "rss_bytes_before": _current_process_rss_bytes(),
        "rss_bytes_after": None,
        "more_pending": False,
        "continuation_remaining": 0,
    }

    def finish_result() -> dict[str, Any]:
        loaded = int(result.get("rows_loaded") or 0)
        remaining = max(0, row_limit - loaded)
        result["continuation_remaining"] = remaining if loaded >= resolved_page_size else 0
        result["more_pending"] = bool(result["continuation_remaining"] and int(result.get("failed") or 0) == 0)
        result["rss_bytes_after"] = _current_process_rss_bytes()
        result["errors"] = list(dict.fromkeys(result["errors"]))[:20]
        return result

    psycopg = _load_psycopg()
    db_url = url or database_url()

    def with_cursor(callback):
        with psycopg.connect(db_url) as conn:
            with conn.cursor() as cur:
                return callback(cur)

    with psycopg.connect(db_url) as conn:
        with conn.cursor() as cur:
            stale_records = _fetch_stale_search_index_records(
                cur,
                owner_class=normalized_class,
                root_name=root_name,
                limit=resolved_page_size,
            )
            result["rows_loaded"] += len(stale_records)
            for record in stale_records:
                try:
                    vespa.delete(str(record["vespa_document_id"]))
                except Exception as exc:
                    result["failed"] += 1
                    result["errors"].append(str(exc)[:300])
                    _mark_search_index_record_failed(cur, record=record, error=str(exc))
                    continue
                _mark_search_index_record_deleted(cur, record=record)
                result["deleted"] += 1

            remaining = max(0, resolved_page_size - len(stale_records))
            if remaining == 0:
                return finish_result()
            rows = _fetch_search_index_rows(
                cur,
                owner_class=normalized_class,
                root_name=root_name,
                limit=remaining,
                embedding_model=SNOWFLAKE_EMBEDDING_MODEL,
                retry_capacity_blockers=retry_capacity_blockers,
            )
            result["requested"] = len(rows)
            result["rows_loaded"] += len(rows)
            result["hydrated_body_chars"] += sum(int(row.get("search_index_hydrated_body_chars") or 0) for row in rows)
            result["truncated_body_chars"] += sum(int(row.get("search_index_truncated_chars") or 0) for row in rows)
            pending = []
            for row in rows:
                unchanged_capacity_blocker = (
                    not retry_capacity_blockers
                    and row.get("existing_index_status") == "blocked_embedding_capacity"
                    and row.get("existing_source_hash") == row.get("source_hash")
                    and row.get("existing_embedding_model") == SNOWFLAKE_EMBEDDING_MODEL
                )
                if unchanged_capacity_blocker:
                    current_content_hash = str(row.get("owner_content_hash") or "")
                    recorded_content_hash = _normalised_capacity_source_content_hash(row.get("existing_capacity_source_content_hash"))
                    should_record_current_content_hash = bool(current_content_hash) and (
                        not recorded_content_hash
                        or _search_index_capacity_blocker_priority(
                            retry_capacity_blockers=False,
                            recorded_content_hash=recorded_content_hash,
                            current_content_hash=current_content_hash,
                        ) == 1
                    )
                    if should_record_current_content_hash:
                        _upsert_search_index_record(
                            cur,
                            row=row,
                            status="blocked_embedding_capacity",
                            last_error=str(row.get("existing_last_error") or "") or None,
                            metadata={"capacity_source_content_hash": current_content_hash},
                        )
                    result["skipped_capacity_blockers"] += 1
                    continue
                if (
                    row.get("existing_source_hash") != row.get("source_hash")
                    or row.get("existing_index_status") != "indexed"
                    or row.get("existing_embedding_model") != SNOWFLAKE_EMBEDDING_MODEL
                ):
                    pending.append(row)
            result["skipped_unchanged"] = len(rows) - len(pending)
            for row in pending:
                _upsert_search_index_record(
                    cur,
                    row=row,
                    status="syncing",
                    last_error=None,
                    metadata={
                        "sync_reason": "search_index_sync",
                        "search_index_text_max_chars": _search_index_text_max_chars(),
                        "body_truncated_chars": int(row.get("search_index_truncated_chars") or 0),
                    },
                )
            if not pending:
                return finish_result()

    batch_size = int(result["embedding_batch_size"])
    batches = deque((offset, pending[offset : offset + batch_size]) for offset in range(0, len(pending), batch_size))
    while batches:
        offset, batch_rows = batches.popleft()
        result["embedding_batches"] += 1
        attempted_size = len(batch_rows)
        histogram = result["embedding_attempted_size_histogram"]
        histogram[str(attempted_size)] = int(histogram.get(str(attempted_size)) or 0) + 1
        smallest = result.get("embedding_smallest_attempted_size")
        result["embedding_smallest_attempted_size"] = attempted_size if smallest is None else min(int(smallest), attempted_size)
        inputs = [
            EmbeddingInput(
                owner_table=str(row["owner_table"]),
                owner_id=str(row["owner_id"]),
                text=str(row["index_text"]),
                model=SNOWFLAKE_EMBEDDING_MODEL,
                dimensions=SNOWFLAKE_EMBEDDING_DIMENSIONS,
            )
            for row in batch_rows
        ]
        try:
            embeddings = provider.embed_batch(inputs)
            if len(embeddings) != len(batch_rows):
                raise ValueError("embedding provider returned a different number of embeddings than requested")
        except Exception as exc:
            capacity_state = _search_index_embedding_capacity_state(exc)
            if capacity_state:
                result["embedding_capacity_state"] = capacity_state
            if capacity_state == "unschedulable" and len(batch_rows) > 1:
                split_at = len(batch_rows) // 2
                batches.appendleft((offset + split_at, batch_rows[split_at:]))
                batches.appendleft((offset, batch_rows[:split_at]))
                result["embedding_split_count"] += 1
                continue
            if capacity_state == "unschedulable":
                failed_row = batch_rows[0]

                def mark_capacity_blocker(cur):
                    _upsert_search_index_record(
                        cur,
                        row=failed_row,
                        status="blocked_embedding_capacity",
                        last_error=str(exc),
                        metadata={
                            "failed_stage": "embedding_capacity",
                            "capacity_state": "unschedulable",
                            "capacity_source_content_hash": str(failed_row.get("owner_content_hash") or ""),
                            "retryable": False,
                        },
                    )

                with_cursor(mark_capacity_blocker)
                result["failed"] += 1
                result["non_retryable"] = True
                result["non_retryable_blocker"] = "embedding_capacity"
                result["errors"].append(str(exc)[:300])
                return finish_result()
            _raise_retryable_search_index_embedding_exception(exc)
            remaining_rows = list(batch_rows)
            for _queued_offset, queued_rows in batches:
                remaining_rows.extend(queued_rows)

            def mark_embedding_failures(cur):
                for row in remaining_rows:
                    _upsert_search_index_record(
                        cur,
                        row=row,
                        status="failed",
                        last_error=str(exc),
                        metadata={"failed_stage": "embedding"},
                    )

            with_cursor(mark_embedding_failures)
            result["failed"] += len(remaining_rows)
            result["errors"].append(str(exc)[:300])
            return finish_result()

        for row, embedding in zip(batch_rows, embeddings):
            row = {
                **row,
                "embedding": embedding.vector,
                "embedding_model": embedding.model,
                "embedding_dimensions": embedding.dimensions,
                "source_hash": str(embedding.metadata.get("source_hash") or row["source_hash"]),
            }
            try:
                feed_started = time.perf_counter()
                vespa.feed(build_vespa_document(row))
            except Exception as exc:
                result["failed"] += 1
                result["errors"].append(str(exc)[:300])
                failed_row = row

                def mark_feed_failure(cur):
                    _upsert_search_index_record(
                        cur,
                        row=failed_row,
                        status="failed",
                        last_error=str(exc),
                        metadata={"failed_stage": "feed"},
                    )

                with_cursor(mark_feed_failure)
                continue
            latency_ms = (time.perf_counter() - feed_started) * 1000
            result["vespa_feed_count"] += 1
            result["vespa_feed_latency_ms_total"] = round(float(result["vespa_feed_latency_ms_total"]) + latency_ms, 3)
            result["vespa_feed_latency_ms_max"] = round(max(float(result["vespa_feed_latency_ms_max"]), latency_ms), 3)

            def mark_indexed(cur):
                _upsert_search_index_record(
                    cur,
                    row=row,
                    status="indexed",
                    last_error=None,
                    metadata={
                        "rank_profile": "hybrid",
                        "source": "search_index_sync",
                        "search_index_text_max_chars": _search_index_text_max_chars(),
                        "body_truncated_chars": int(row.get("search_index_truncated_chars") or 0),
                    },
                )

            with_cursor(mark_indexed)
            result["indexed"] += 1
    return finish_result()


def _fetch_search_index_rows(
    cur: Any,
    *,
    owner_class: str,
    root_name: str | None,
    limit: int,
    embedding_model: str,
    retry_capacity_blockers: bool = False,
) -> list[dict[str, Any]]:
    row_limit = _search_index_fetch_limit(limit)
    classes = ["corpus", "episodes", "claims"] if owner_class == "all" else [owner_class]
    rows: list[dict[str, Any]] = []
    class_limits = _fair_class_limits(classes, row_limit) if owner_class == "all" else {classes[0]: row_limit}
    for item_class in classes:
        class_limit = class_limits.get(item_class, row_limit)
        if class_limit <= 0:
            continue
        if item_class == "corpus":
            batch = _fetch_corpus_search_index_rows(
                cur,
                root_name=root_name,
                limit=class_limit,
                embedding_model=embedding_model,
                retry_capacity_blockers=retry_capacity_blockers,
            )
        elif item_class == "episodes":
            batch = _fetch_episode_search_index_rows(
                cur,
                root_name=root_name,
                limit=class_limit,
                embedding_model=embedding_model,
                retry_capacity_blockers=retry_capacity_blockers,
            )
        else:
            batch = _fetch_claim_search_index_rows(
                cur,
                root_name=root_name,
                limit=class_limit,
                embedding_model=embedding_model,
                retry_capacity_blockers=retry_capacity_blockers,
            )
        rows.extend(batch)
    return rows[:row_limit]


def _fair_class_limits(classes: list[str], row_limit: int) -> dict[str, int]:
    if not classes:
        return {}
    base = max(1, row_limit // len(classes))
    remainder = max(0, row_limit - (base * len(classes)))
    return {item_class: base + (1 if index < remainder else 0) for index, item_class in enumerate(classes)}


def _fetch_corpus_search_index_rows(
    cur: Any,
    *,
    root_name: str | None,
    limit: int,
    embedding_model: str,
    retry_capacity_blockers: bool = False,
) -> list[dict[str, Any]]:
    from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL, vespa_document_id

    root_sql = "AND r.name = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT c.id::text, a.id::text, r.id::text, r.name,
               c.title, c.body, a.path, a.file_kind, c.metadata, a.content_hash,
               rec.source_hash, rec.index_status, rec.embedding_model,
               rec.metadata->>'capacity_source_content_hash', rec.last_error
        FROM asset_chunks c
        JOIN source_assets a ON a.id = c.asset_id
        JOIN monitored_roots r ON r.id = a.root_id
        LEFT JOIN search_index_records rec ON rec.owner_table = 'asset_chunks'
                                          AND rec.owner_id = c.id
                                          AND rec.embedding_model = %s
        WHERE r.enabled
          AND a.deleted_at IS NULL
          AND a.canonical_asset_id IS NULL
          AND a.extraction_status = 'indexed'
          {root_sql}
        ORDER BY CASE
                    WHEN rec.index_status = 'failed' THEN 0
                    WHEN rec.index_status IS NULL THEN 1
                    WHEN rec.index_status = 'blocked_embedding_capacity' AND (%s OR (NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL AND NULLIF(a.content_hash, '') IS NOT NULL AND NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS DISTINCT FROM a.content_hash)) THEN 1
                    WHEN rec.index_status = 'blocked_embedding_capacity' THEN 4
                    WHEN rec.index_status IS DISTINCT FROM 'indexed' THEN 2
                    ELSE 3
                 END,
                 c.updated_at DESC,
                 c.id
        LIMIT %s
        """,
        (embedding_model, *root_params, bool(retry_capacity_blockers), _search_index_fetch_limit(limit)),
    )
    items: list[dict[str, Any]] = []
    for owner_id, asset_id, row_root_id, row_root_name, title, body, source_path, file_kind, metadata, owner_content_hash, existing_hash, existing_status, existing_model, existing_capacity_source_content_hash, existing_last_error in cur.fetchall():
        item_metadata = metadata if isinstance(metadata, dict) else {}
        hydrated_body = mail_content_store.hydrate_chunk_body({"body": body, "metadata": item_metadata})
        bounded_body, hydrated_body_chars, truncated_body_chars = _bounded_search_index_body(hydrated_body)
        symbols = _search_index_symbols(item_metadata)
        text = _search_index_text(title=title, body=bounded_body, source_path=source_path, symbols=symbols)
        items.append(
            {
                "vespa_document_id": vespa_document_id("asset_chunks", str(owner_id)),
                "owner_table": "asset_chunks",
                "owner_id": str(owner_id),
                "asset_id": str(asset_id or ""),
                "root_id": str(row_root_id or ""),
                "root_name": str(row_root_name or ""),
                "title": str(title or ""),
                "body": bounded_body,
                "source_path": str(source_path or ""),
                "symbols": symbols,
                "language": _search_index_language(item_metadata),
                "file_kind": str(file_kind or ""),
                "lifecycle_state": "active",
                "deleted": False,
                "canonical": True,
                "source_hash": embedding_source_hash(text),
                "owner_content_hash": str(owner_content_hash or ""),
                "index_text": text,
                "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
                "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
                "existing_source_hash": existing_hash,
                "existing_index_status": existing_status,
                "existing_embedding_model": existing_model,
                "existing_capacity_source_content_hash": str(existing_capacity_source_content_hash or ""),
                "existing_last_error": str(existing_last_error or "") or None,
                "search_index_hydrated_body_chars": hydrated_body_chars,
                "search_index_truncated_chars": truncated_body_chars,
            }
        )
    return items


def _fetch_episode_search_index_rows(
    cur: Any,
    *,
    root_name: str | None,
    limit: int,
    embedding_model: str,
    retry_capacity_blockers: bool = False,
) -> list[dict[str, Any]]:
    from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL, vespa_document_id

    root_sql = "AND e.metadata->>'root_name' = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT e.id::text, e.title, e.summary, e.metadata, s.content_hash,
               rec.source_hash, rec.index_status, rec.embedding_model,
               rec.metadata->>'capacity_source_content_hash', rec.last_error
        FROM episodes e
        LEFT JOIN sources s ON s.id = e.source_id
        LEFT JOIN search_index_records rec ON rec.owner_table = 'episodes'
                                          AND rec.owner_id = e.id
                                          AND rec.embedding_model = %s
        WHERE e.superseded_by IS NULL
          {root_sql}
        ORDER BY CASE
                    WHEN rec.index_status = 'failed' THEN 0
                    WHEN rec.index_status IS NULL THEN 1
                    WHEN rec.index_status = 'blocked_embedding_capacity' AND (%s OR (NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL AND NULLIF(s.content_hash, '') IS NOT NULL AND NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS DISTINCT FROM s.content_hash)) THEN 1
                    WHEN rec.index_status = 'blocked_embedding_capacity' THEN 4
                    WHEN rec.index_status IS DISTINCT FROM 'indexed' THEN 2
                    ELSE 3
                 END,
                 e.updated_at DESC,
                 e.id
        LIMIT %s
        """,
        (embedding_model, *root_params, bool(retry_capacity_blockers), _search_index_fetch_limit(limit)),
    )
    items: list[dict[str, Any]] = []
    for owner_id, title, summary, metadata, owner_content_hash, existing_hash, existing_status, existing_model, existing_capacity_source_content_hash, existing_last_error in cur.fetchall():
        item_metadata = metadata if isinstance(metadata, dict) else {}
        symbols = _search_index_symbols(item_metadata)
        source_path = str(item_metadata.get("source_path") or item_metadata.get("source") or "")
        bounded_body, hydrated_body_chars, truncated_body_chars = _bounded_search_index_body(summary)
        text = _search_index_text(title=title, body=bounded_body, source_path=source_path, symbols=symbols)
        items.append(
            {
                "vespa_document_id": vespa_document_id("episodes", str(owner_id)),
                "owner_table": "episodes",
                "owner_id": str(owner_id),
                "root_id": str(item_metadata.get("root_id") or ""),
                "root_name": str(item_metadata.get("root_name") or ""),
                "title": str(title or ""),
                "body": bounded_body,
                "source_path": source_path,
                "symbols": symbols,
                "language": _search_index_language(item_metadata),
                "file_kind": "episode",
                "lifecycle_state": "active",
                "deleted": False,
                "canonical": True,
                "source_hash": embedding_source_hash(text),
                "owner_content_hash": str(owner_content_hash or ""),
                "index_text": text,
                "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
                "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
                "existing_source_hash": existing_hash,
                "existing_index_status": existing_status,
                "existing_embedding_model": existing_model,
                "existing_capacity_source_content_hash": str(existing_capacity_source_content_hash or ""),
                "existing_last_error": str(existing_last_error or "") or None,
                "search_index_hydrated_body_chars": hydrated_body_chars,
                "search_index_truncated_chars": truncated_body_chars,
            }
        )
    return items


def _fetch_claim_search_index_rows(
    cur: Any,
    *,
    root_name: str | None,
    limit: int,
    embedding_model: str,
    retry_capacity_blockers: bool = False,
) -> list[dict[str, Any]]:
    from .search_index import SNOWFLAKE_EMBEDDING_DIMENSIONS, SNOWFLAKE_EMBEDDING_MODEL, vespa_document_id

    root_sql = "AND c.metadata->>'root_name' = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT c.id::text, concat_ws(' ', e.name, c.predicate) AS title,
               c.object_text, c.lifecycle_state, c.metadata, source.content_hash,
               rec.source_hash, rec.index_status, rec.embedding_model,
               rec.metadata->>'capacity_source_content_hash', rec.last_error
        FROM claims c
        LEFT JOIN entities e ON e.id = c.subject_entity_id
        LEFT JOIN episodes source_episode ON source_episode.id = c.episode_id
        LEFT JOIN sources source ON source.id = source_episode.source_id
        LEFT JOIN search_index_records rec ON rec.owner_table = 'claims'
                                          AND rec.owner_id = c.id
                                          AND rec.embedding_model = %s
        WHERE c.lifecycle_state IN ('active', 'confirmed', 'reinforced')
          AND c.retention_action = 'keep'
          {root_sql}
        ORDER BY CASE
                    WHEN rec.index_status = 'failed' THEN 0
                    WHEN rec.index_status IS NULL THEN 1
                    WHEN rec.index_status = 'blocked_embedding_capacity' AND (%s OR (NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS NOT NULL AND NULLIF(source.content_hash, '') IS NOT NULL AND NULLIF(regexp_replace(rec.metadata->>'capacity_source_content_hash', '^[[:space:]]+|[[:space:]]+$', '', 'g'), '') IS DISTINCT FROM source.content_hash)) THEN 1
                    WHEN rec.index_status = 'blocked_embedding_capacity' THEN 4
                    WHEN rec.index_status IS DISTINCT FROM 'indexed' THEN 2
                    ELSE 3
                 END,
                 c.updated_at DESC,
                 c.id
        LIMIT %s
        """,
        (embedding_model, *root_params, bool(retry_capacity_blockers), _search_index_fetch_limit(limit)),
    )
    items: list[dict[str, Any]] = []
    for owner_id, title, object_text, lifecycle_state, metadata, owner_content_hash, existing_hash, existing_status, existing_model, existing_capacity_source_content_hash, existing_last_error in cur.fetchall():
        item_metadata = metadata if isinstance(metadata, dict) else {}
        symbols = _search_index_symbols(item_metadata)
        source_path = str(item_metadata.get("source_path") or item_metadata.get("source") or "")
        bounded_body, hydrated_body_chars, truncated_body_chars = _bounded_search_index_body(object_text)
        text = _search_index_text(title=title, body=bounded_body, source_path=source_path, symbols=symbols)
        items.append(
            {
                "vespa_document_id": vespa_document_id("claims", str(owner_id)),
                "owner_table": "claims",
                "owner_id": str(owner_id),
                "root_id": str(item_metadata.get("root_id") or ""),
                "root_name": str(item_metadata.get("root_name") or ""),
                "title": str(title or ""),
                "body": bounded_body,
                "source_path": source_path,
                "symbols": symbols,
                "language": _search_index_language(item_metadata),
                "file_kind": "claim",
                "lifecycle_state": str(lifecycle_state or "active"),
                "deleted": False,
                "canonical": True,
                "source_hash": embedding_source_hash(text),
                "owner_content_hash": str(owner_content_hash or ""),
                "index_text": text,
                "embedding_model": SNOWFLAKE_EMBEDDING_MODEL,
                "embedding_dimensions": SNOWFLAKE_EMBEDDING_DIMENSIONS,
                "existing_source_hash": existing_hash,
                "existing_index_status": existing_status,
                "existing_embedding_model": existing_model,
                "existing_capacity_source_content_hash": str(existing_capacity_source_content_hash or ""),
                "existing_last_error": str(existing_last_error or "") or None,
                "search_index_hydrated_body_chars": hydrated_body_chars,
                "search_index_truncated_chars": truncated_body_chars,
            }
        )
    return items


def _fetch_stale_search_index_records(
    cur: Any,
    *,
    owner_class: str,
    root_name: str | None,
    limit: int,
) -> list[dict[str, Any]]:
    row_limit = _search_index_fetch_limit(limit)
    classes = ["corpus", "episodes", "claims"] if owner_class == "all" else [owner_class]
    rows: list[dict[str, Any]] = []
    class_limits = _fair_class_limits(classes, row_limit) if owner_class == "all" else {classes[0]: row_limit}
    for item_class in classes:
        class_limit = class_limits.get(item_class, row_limit)
        if class_limit <= 0:
            continue
        if item_class == "corpus":
            batch = _fetch_stale_corpus_search_index_records(cur, root_name=root_name, limit=class_limit)
        elif item_class == "episodes":
            batch = _fetch_stale_episode_search_index_records(cur, root_name=root_name, limit=class_limit)
        else:
            batch = _fetch_stale_claim_search_index_records(cur, root_name=root_name, limit=class_limit)
        rows.extend(batch)
    return rows[:row_limit]


def _fetch_stale_corpus_search_index_records(cur: Any, *, root_name: str | None, limit: int) -> list[dict[str, Any]]:
    root_sql = "AND rec.root_name = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT rec.vespa_document_id, rec.owner_table, rec.owner_id::text, rec.root_name
        FROM search_index_records rec
        LEFT JOIN asset_chunks c ON c.id = rec.owner_id
        LEFT JOIN source_assets a ON a.id = c.asset_id
        WHERE rec.owner_table = 'asset_chunks'
          AND rec.index_status <> 'deleted'
          AND (
              c.id IS NULL
              OR a.id IS NULL
              OR a.deleted_at IS NOT NULL
              OR a.canonical_asset_id IS NOT NULL
              OR a.extraction_status <> 'indexed'
          )
          {root_sql}
        ORDER BY rec.updated_at
        LIMIT %s
        """,
        (*root_params, _search_index_fetch_limit(limit)),
    )
    return [_search_index_record_row(row) for row in cur.fetchall()]


def _fetch_stale_episode_search_index_records(cur: Any, *, root_name: str | None, limit: int) -> list[dict[str, Any]]:
    root_sql = "AND rec.root_name = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT rec.vespa_document_id, rec.owner_table, rec.owner_id::text, rec.root_name
        FROM search_index_records rec
        LEFT JOIN episodes e ON e.id = rec.owner_id
        WHERE rec.owner_table = 'episodes'
          AND rec.index_status <> 'deleted'
          AND (e.id IS NULL OR e.superseded_by IS NOT NULL)
          {root_sql}
        ORDER BY rec.updated_at
        LIMIT %s
        """,
        (*root_params, _search_index_fetch_limit(limit)),
    )
    return [_search_index_record_row(row) for row in cur.fetchall()]


def _fetch_stale_claim_search_index_records(cur: Any, *, root_name: str | None, limit: int) -> list[dict[str, Any]]:
    root_sql = "AND rec.root_name = %s" if root_name else ""
    root_params: tuple[Any, ...] = (root_name,) if root_name else ()
    cur.execute(
        f"""
        SELECT rec.vespa_document_id, rec.owner_table, rec.owner_id::text, rec.root_name
        FROM search_index_records rec
        LEFT JOIN claims c ON c.id = rec.owner_id
        WHERE rec.owner_table = 'claims'
          AND rec.index_status <> 'deleted'
          AND (
              c.id IS NULL
              OR c.lifecycle_state NOT IN ('active', 'confirmed', 'reinforced')
              OR c.retention_action <> 'keep'
          )
          {root_sql}
        ORDER BY rec.updated_at
        LIMIT %s
        """,
        (*root_params, _search_index_fetch_limit(limit)),
    )
    return [_search_index_record_row(row) for row in cur.fetchall()]


def _search_index_record_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "vespa_document_id": str(row[0] or ""),
        "owner_table": str(row[1] or ""),
        "owner_id": str(row[2] or ""),
        "root_name": row[3],
    }


def _search_index_text(*, title: Any, body: Any, source_path: Any, symbols: list[str]) -> str:
    return "\n".join(
        part
        for part in (
            str(title or "").strip(),
            str(source_path or "").strip(),
            " ".join(symbols).strip(),
            str(body or "").strip(),
        )
        if part
    )


def _search_index_language(metadata: dict[str, Any]) -> str:
    code = metadata.get("code")
    if isinstance(code, dict) and code.get("language"):
        return str(code.get("language") or "")
    return str(metadata.get("language") or metadata.get("mime_language") or "")


def _search_index_symbols(metadata: dict[str, Any]) -> list[str]:
    raw_symbols: list[Any] = []
    for key in ("symbols", "symbol_names"):
        value = metadata.get(key)
        if isinstance(value, list):
            raw_symbols.extend(value)
    code = metadata.get("code")
    if isinstance(code, dict):
        for key in ("symbol", "symbol_name", "qualified_name", "name"):
            if code.get(key):
                raw_symbols.append(code.get(key))
    symbols: list[str] = []
    seen: set[str] = set()
    for value in raw_symbols:
        text = str(value or "").strip()
        if not text or text in seen:
            continue
        seen.add(text)
        symbols.append(text[:240])
    return symbols[:32]


def _upsert_search_index_record(
    cur: Any,
    *,
    row: dict[str, Any],
    status: str,
    last_error: str | None,
    metadata: dict[str, Any],
) -> None:
    completed = status in {"indexed", "deleted", "failed", "skipped", "blocked_embedding_capacity"}
    cur.execute(
        """
        INSERT INTO search_index_records (
            vespa_document_id, owner_table, owner_id, root_id, root_name,
            source_hash, embedding_model, embedding_dimensions, model_generation,
            index_status, last_error, sync_started_at, sync_completed_at, metadata
        )
        VALUES (
            %s, %s, %s, NULLIF(%s, '')::uuid, NULLIF(%s, ''),
            NULLIF(%s, ''), %s, %s, %s,
            %s, %s, now(), CASE WHEN %s THEN now() ELSE NULL END, %s::jsonb
        )
        ON CONFLICT (vespa_document_id) DO UPDATE SET
            owner_table = EXCLUDED.owner_table,
            owner_id = EXCLUDED.owner_id,
            root_id = EXCLUDED.root_id,
            root_name = EXCLUDED.root_name,
            source_hash = EXCLUDED.source_hash,
            embedding_model = EXCLUDED.embedding_model,
            embedding_dimensions = EXCLUDED.embedding_dimensions,
            model_generation = EXCLUDED.model_generation,
            index_status = EXCLUDED.index_status,
            last_error = EXCLUDED.last_error,
            sync_started_at = EXCLUDED.sync_started_at,
            sync_completed_at = EXCLUDED.sync_completed_at,
            metadata = CASE
                WHEN EXCLUDED.index_status IN ('indexed', 'deleted', 'skipped') THEN search_index_records.metadata
                    - 'failed_stage'
                    - 'failed_stage_last_error'
                    - 'failed_stage_error'
                    - 'failed_last_error'
                    - 'failed_error'
                    - 'failure_error'
                    - 'error_type'
                    - 'last_error'
                ELSE search_index_records.metadata
            END || EXCLUDED.metadata,
            updated_at = now()
        """,
        (
            row["vespa_document_id"],
            row["owner_table"],
            row["owner_id"],
            str(row.get("root_id") or ""),
            str(row.get("root_name") or ""),
            str(row.get("source_hash") or ""),
            row.get("embedding_model") or "Snowflake/snowflake-arctic-embed-l-v2.0",
            int(row.get("embedding_dimensions") or 1024),
            str(row.get("model_generation") or "snowflake-qwen-paddleocr-v1"),
            status,
            last_error[:1000] if last_error else None,
            completed,
            _json(metadata),
        ),
    )


def _mark_search_index_record_deleted(cur: Any, *, record: dict[str, Any]) -> None:
    cur.execute(
        """
        UPDATE search_index_records
        SET index_status = 'deleted',
            last_error = NULL,
            sync_completed_at = now(),
            updated_at = now(),
            metadata = (
                metadata
                    - 'failed_stage'
                    - 'failed_stage_last_error'
                    - 'failed_stage_error'
                    - 'failed_last_error'
                    - 'failed_error'
                    - 'failure_error'
                    - 'error_type'
                    - 'last_error'
            ) || jsonb_build_object('deleted_from_vespa', true)
        WHERE vespa_document_id = %s
        """,
        (record["vespa_document_id"],),
    )


def _mark_search_index_record_failed(cur: Any, *, record: dict[str, Any], error: str) -> None:
    cur.execute(
        """
        UPDATE search_index_records
        SET index_status = 'failed',
            last_error = %s,
            sync_completed_at = now(),
            updated_at = now(),
            metadata = metadata || jsonb_build_object('failed_stage', 'delete')
        WHERE vespa_document_id = %s
        """,
        (str(error or "")[:1000], record["vespa_document_id"]),
    )


def _delete_search_index_records_for_root(
    *,
    root_name: str,
    statuses: list[str] | tuple[str, ...] | None = None,
    url: str | None = None,
) -> int:
    normalized_root = str(root_name or "").strip()
    if not normalized_root:
        raise ValueError("root_name is required")
    clauses = ["root_name = %s"]
    params: list[Any] = [normalized_root]
    normalized_statuses = [str(status or "").strip().lower() for status in statuses or [] if str(status or "").strip()]
    if normalized_statuses:
        clauses.append("index_status = ANY(%s::text[])")
        params.append(normalized_statuses)

    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(f"DELETE FROM search_index_records WHERE {' AND '.join(clauses)}", tuple(params))
            return int(cur.rowcount or 0)


def _cancel_duplicate_corpus_job_for_asset(cur: Any, *, root_name: str, relative_path: str) -> None:
    cur.execute(
        """
        UPDATE capture_jobs
        SET status = 'cancelled_duplicate',
            last_error = 'duplicate_suppressed',
            locked_at = NULL,
            locked_by = NULL,
            updated_at = now()
        WHERE job_type LIKE 'corpus_%%'
          AND status IN ('pending', 'running')
          AND payload->>'root_name' = %s
          AND payload->>'path' = %s
        """,
        (root_name, relative_path),
    )


def _normalized_relative_paths(paths: Iterable[str]) -> list[str]:
    normalized: list[str] = []
    for raw_path in paths:
        path = str(raw_path or "").replace("\\", "/").strip().strip("/")
        if path:
            normalized.append(path)
    return list(dict.fromkeys(normalized))


def _mark_unseen_assets_by_paths(
    cur: Any,
    *,
    root_id: str,
    paths: list[str],
    reason: str,
    grace_seconds: int,
) -> list[str]:
    if not paths:
        return []
    cur.execute(
        """
        UPDATE source_assets
        SET deleted_at = COALESCE(deleted_at, now()),
            extraction_status = 'deleted',
            metadata = metadata || jsonb_build_object(
                'unseen_reason', %s::text,
                'unseen_since', COALESCE(metadata->>'unseen_since', now()::text),
                'purge_after', COALESCE(metadata->>'purge_after', (now() + make_interval(secs => %s))::text),
                'readiness_status', 'deleted',
                'readiness_reason', %s::text
            ),
            updated_at = now()
        WHERE root_id = %s
          AND path = ANY(%s)
          AND (deleted_at IS NULL OR NOT (metadata ? 'unseen_reason'))
        RETURNING path
        """,
        (reason, max(0, int(grace_seconds or 0)), reason, root_id, paths),
    )
    try:
        return [str(row[0]) for row in cur.fetchall()]
    except AttributeError:
        return []


def _mark_deleted_assets(
    cur: Any,
    root_id: str,
    seen_paths: set[str],
    plan: CrawlPlan,
    *,
    reason: str,
    grace_seconds: int,
) -> list[str]:
    seen_path_list = list(seen_paths) or [""]
    if plan.scope_relative_path is None:
        cur.execute(
            """
            UPDATE source_assets
            SET deleted_at = COALESCE(deleted_at, now()),
                extraction_status = 'deleted',
                metadata = metadata || jsonb_build_object(
                    'unseen_reason', %s::text,
                    'unseen_since', COALESCE(metadata->>'unseen_since', now()::text),
                    'purge_after', COALESCE(metadata->>'purge_after', (now() + make_interval(secs => %s))::text),
                    'readiness_status', 'deleted',
                    'readiness_reason', %s::text
                ),
                updated_at = now()
            WHERE root_id = %s
              AND (deleted_at IS NULL OR NOT (metadata ? 'unseen_reason'))
              AND NOT (path = ANY(%s))
            RETURNING path
            """,
            (reason, max(0, int(grace_seconds or 0)), reason, root_id, seen_path_list),
        )
        try:
            return [str(row[0]) for row in cur.fetchall()]
        except AttributeError:
            return []

    if plan.scope_is_file:
        cur.execute(
            """
            UPDATE source_assets
            SET deleted_at = COALESCE(deleted_at, now()),
                extraction_status = 'deleted',
                metadata = metadata || jsonb_build_object(
                    'unseen_reason', %s::text,
                    'unseen_since', COALESCE(metadata->>'unseen_since', now()::text),
                    'purge_after', COALESCE(metadata->>'purge_after', (now() + make_interval(secs => %s))::text),
                    'readiness_status', 'deleted',
                    'readiness_reason', %s::text
                ),
                updated_at = now()
            WHERE root_id = %s
              AND (deleted_at IS NULL OR NOT (metadata ? 'unseen_reason'))
              AND path = %s
              AND NOT (path = ANY(%s))
            RETURNING path
            """,
            (reason, max(0, int(grace_seconds or 0)), reason, root_id, plan.scope_relative_path, seen_path_list),
        )
        try:
            return [str(row[0]) for row in cur.fetchall()]
        except AttributeError:
            return []

    prefix = f"{plan.scope_relative_path}/"
    cur.execute(
        """
        UPDATE source_assets
        SET deleted_at = COALESCE(deleted_at, now()),
            extraction_status = 'deleted',
            metadata = metadata || jsonb_build_object(
                'unseen_reason', %s::text,
                'unseen_since', COALESCE(metadata->>'unseen_since', now()::text),
                'purge_after', COALESCE(metadata->>'purge_after', (now() + make_interval(secs => %s))::text),
                'readiness_status', 'deleted',
                'readiness_reason', %s::text
            ),
            updated_at = now()
        WHERE root_id = %s
          AND (deleted_at IS NULL OR NOT (metadata ? 'unseen_reason'))
          AND (path = %s OR path LIKE %s)
          AND NOT (path = ANY(%s))
        RETURNING path
        """,
        (reason, max(0, int(grace_seconds or 0)), reason, root_id, plan.scope_relative_path, f"{prefix}%", seen_path_list),
    )
    try:
        return [str(row[0]) for row in cur.fetchall()]
    except AttributeError:
        return []


def _cancel_unseen_corpus_jobs_for_paths(
    cur: Any,
    *,
    root_name: str,
    paths: list[str] | tuple[str, ...],
    reason: str,
) -> int:
    normalized_paths = _normalized_relative_paths(paths)
    if not normalized_paths:
        return 0
    cur.execute(
        f"""
        WITH candidates AS (
            SELECT id, status
            FROM capture_jobs
            WHERE job_type LIKE 'corpus_%%'
              AND status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'retrying_gpu_busy', 'running')
              -- legacy active statuses: status IN ('pending', 'retrying_locked', 'retrying_vss_failed', 'running')
              AND payload->>'root_name' = %s
              AND payload->>'path' = ANY(%s)
            FOR UPDATE
        ),
        cancelled AS (
            UPDATE capture_jobs job
            SET status = '{UNSEEN_ASSET_CANCELLED_STATUS}',
                last_error = %s,
                completed_at = now(),
                locked_at = NULL,
                locked_by = NULL,
                telemetry = telemetry || jsonb_build_object(
                    'unseen_reason', %s::text,
                    'previous_status', candidates.status
                ),
                updated_at = now()
            FROM candidates
            WHERE job.id = candidates.id
            RETURNING job.id, candidates.status AS previous_status
        ),
        audit AS (
            INSERT INTO audit_events (event_type, target_table, target_id, details)
            SELECT 'capture_job.cancelled_unseen_asset',
                   'capture_jobs',
                   id,
                   jsonb_build_object('reason', %s::text, 'previous_status', previous_status)
            FROM cancelled
            RETURNING 1
        )
        SELECT count(*) FROM cancelled
        """,
        (
            root_name,
            normalized_paths,
            f"cancelled_unseen_asset: {reason}",
            reason,
            reason,
        ),
    )
    row = cur.fetchone()
    return int(row[0] or 0) if row else 0


def _upsert_watcher_state(cur: Any, root_id: str, status: str) -> None:
    cur.execute(
        """
        INSERT INTO watcher_state (root_id, status, updated_at)
        VALUES (%s, %s, now())
        ON CONFLICT (root_id) DO UPDATE SET
            status = EXCLUDED.status,
            updated_at = now()
        """,
        (root_id, status),
    )


def _update_watcher_state(
    *,
    root_name: str,
    status: str,
    heartbeat: bool = False,
    event: bool = False,
    action: str = "changed",
    path_hash: str | None = None,
    metadata: dict[str, Any] | None = None,
    error: str | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute("SELECT id::text FROM monitored_roots WHERE name = %s", (root_name,))
            row = cur.fetchone()
            if not row:
                raise ValueError(f"monitored root not found: {root_name}")
            root_id = row[0]
            sanitized_metadata = _sanitize_operational_metadata(metadata or {})
            cur.execute(
                """
                INSERT INTO watcher_state (
                    root_id, status, heartbeat_at, last_event_at, last_error, process_id,
                    event_count, metadata, updated_at
                )
                VALUES (
                    %s, %s,
                    CASE WHEN %s THEN now() ELSE NULL END,
                    CASE WHEN %s THEN now() ELSE NULL END,
                    %s, %s,
                    CASE WHEN %s THEN 1 ELSE 0 END,
                    %s::jsonb,
                    now()
                )
                ON CONFLICT (root_id) DO UPDATE SET
                    status = EXCLUDED.status,
                    heartbeat_at = CASE WHEN %s THEN now() ELSE watcher_state.heartbeat_at END,
                    last_event_at = CASE WHEN %s THEN now() ELSE watcher_state.last_event_at END,
                    last_error = EXCLUDED.last_error,
                    process_id = EXCLUDED.process_id,
                    event_count = watcher_state.event_count + CASE WHEN %s THEN 1 ELSE 0 END,
                    metadata = watcher_state.metadata || EXCLUDED.metadata,
                    updated_at = now()
                """,
                (
                    root_id,
                    status,
                    heartbeat,
                    event,
                    error,
                    os.getpid(),
                    event,
                    _json(sanitized_metadata),
                    heartbeat,
                    event,
                    event,
                ),
            )
            if event:
                cur.execute(
                    """
                    INSERT INTO watcher_events (root_id, action, path_hash, metadata)
                    VALUES (%s, %s, %s, %s::jsonb)
                    RETURNING id::text
                    """,
                    (root_id, action or "changed", path_hash, _json(sanitized_metadata)),
                )
                cur.fetchone()


def _root_id_for_name(cur: Any, root_name: str) -> str:
    cur.execute("SELECT id::text FROM monitored_roots WHERE name = %s", (root_name,))
    row = cur.fetchone()
    if not row:
        raise ValueError(f"monitored root not found: {root_name}")
    return row[0]


def _benchmark_run_row(row: tuple[Any, ...]) -> dict[str, Any]:
    previous_elapsed_ms = row[32]
    previous_throughput = row[33]
    elapsed_ms = int(row[7] or 0)
    throughput = float(row[8] or 0.0)
    previous_delta = None if previous_elapsed_ms is None else elapsed_ms - int(previous_elapsed_ms or 0)
    throughput_delta = None if previous_throughput is None else throughput - float(previous_throughput or 0.0)
    return {
        "id": row[0],
        "fixture": row[1],
        "mode": row[2],
        "label": row[3],
        "compare_label": row[4],
        "status": row[5],
        "file_count": row[6],
        "elapsed_ms": elapsed_ms,
        "throughput_files_per_second": throughput,
        "p50_ms": row[9],
        "p95_ms": row[10],
        "max_ms": row[11],
        "warm_state": row[12],
        "cache_hits": row[13],
        "cache_misses": row[14],
        "jobs_queued": row[15],
        "jobs_completed": row[16],
        "jobs_blocked": row[17],
        "pass_index": row[18],
        "hash_parallelism": row[19],
        "worker_count": row[20],
        "manifest_skipped_unchanged": row[21],
        "worker_family_breakdown": row[22] or {},
        "metadata": _sanitize_operational_metadata(row[23] or {}),
        "scope_type": _normalize_benchmark_scope_type(row[24] or "synthetic"),
        "scope_hash": row[25],
        "deployment_label": row[26],
        "build_metadata": _sanitize_operational_metadata(row[27] or {}),
        "settings_snapshot": _sanitize_operational_metadata(row[28] or {}),
        "model_telemetry": _sanitize_operational_metadata(row[29] or {}),
        "recommendation_metadata": _sanitize_operational_metadata(row[30] or {}),
        "scenario": str((row[30] or {}).get("scenario") or (row[23] or {}).get("scenario") or "standard"),
        "created_at": row[31].isoformat() if row[31] else None,
        "previous_elapsed_delta_ms": previous_delta,
        "previous_throughput_delta": throughput_delta,
    }


def _retrieval_benchmark_run_row(row: tuple[Any, ...]) -> dict[str, Any]:
    metrics = _sanitize_operational_metadata(row[8] or {})
    metadata = _sanitize_operational_metadata(row[10] or {})
    previous_metrics = _sanitize_operational_metadata(row[13] or {})
    return {
        "id": row[0],
        "suite": _normalize_retrieval_benchmark_suite(row[1]),
        "label": row[2],
        "compare_label": row[3],
        "status": row[4],
        "query_count": int(row[5] or 0),
        "passed_count": int(row[6] or 0),
        "failed_count": int(row[7] or 0),
        "metrics": metrics,
        "case_results": _sanitize_retrieval_case_results(row[9] or []),
        "metadata": metadata,
        "recommendation_metadata": _sanitize_operational_metadata(row[11] or {}),
        "created_at": row[12].isoformat() if row[12] else None,
        "previous_metrics": previous_metrics,
        "metric_deltas": _metric_deltas(metrics, previous_metrics),
        "calibration_summary": metadata.get("calibration_summary") if isinstance(metadata.get("calibration_summary"), dict) else {},
    }


def _memory_governance_run_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "mode": _normalize_governance_mode(row[1]),
        "trigger": row[2],
        "status": _normalize_governance_run_status(row[3]),
        "actor": row[4],
        "policy_snapshot": _sanitize_governance_metadata(row[5] or {}),
        "gate": _sanitize_governance_metadata(row[6] or {}),
        "summary": _sanitize_governance_metadata(row[7] or {}),
        "settings_mutated": bool(row[8]),
        "memory_mutated": bool(row[9]),
        "created_at": row[10].isoformat() if row[10] else None,
        "updated_at": row[11].isoformat() if row[11] else None,
    }


def _memory_governance_action_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "run_id": row[1],
        "action": _normalize_governance_action(row[2]),
        "target_type": row[3],
        "target_id": row[4],
        "memory_class": _normalize_governance_memory_class(row[5]),
        "risk": _normalize_governance_risk(row[6]),
        "status": _normalize_governance_action_status(row[7]),
        "source": row[8],
        "actor": row[9],
        "rationale": _sanitize_governance_metadata(row[10] or {}),
        "evidence": _sanitize_governance_metadata(row[11] or {}),
        "before_state": _sanitize_governance_metadata(row[12] or {}),
        "after_state": _sanitize_governance_metadata(row[13] or {}),
        "settings_mutated": bool(row[14]),
        "memory_mutated": bool(row[15]),
        "audit_event_id": row[16],
        "error": row[17],
        "created_at": row[18].isoformat() if row[18] else None,
        "updated_at": row[19].isoformat() if row[19] else None,
        "applied_at": row[20].isoformat() if row[20] else None,
        "recovered_at": row[21].isoformat() if row[21] else None,
    }


def _operator_automation_run_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "mode": _normalize_operator_automation_mode(row[1]),
        "trigger": row[2],
        "status": _normalize_operator_automation_run_status(row[3]),
        "actor": row[4],
        "policy_snapshot": _sanitize_governance_metadata(row[5] or {}),
        "summary": _sanitize_governance_metadata(row[6] or {}),
        "settings_mutated": bool(row[7]),
        "memory_mutated": bool(row[8]),
        "started_at": row[9].isoformat() if row[9] else None,
        "completed_at": row[10].isoformat() if row[10] else None,
        "created_at": row[11].isoformat() if row[11] else None,
        "updated_at": row[12].isoformat() if row[12] else None,
    }


def _operator_automation_action_row(row: tuple[Any, ...]) -> dict[str, Any]:
    return {
        "id": row[0],
        "run_id": row[1],
        "action": _normalize_operator_automation_action(row[2]),
        "target_type": row[3],
        "target_id": row[4],
        "risk": _normalize_governance_risk(row[5]),
        "status": _normalize_operator_automation_action_status(row[6]),
        "source": row[7],
        "actor": row[8],
        "rationale": _sanitize_governance_metadata(row[9] or {}),
        "evidence": _sanitize_governance_metadata(row[10] or {}),
        "result": _sanitize_governance_metadata(row[11] or {}),
        "settings_mutated": bool(row[12]),
        "memory_mutated": bool(row[13]),
        "audit_event_id": row[14],
        "error": row[15],
        "created_at": row[16].isoformat() if row[16] else None,
        "updated_at": row[17].isoformat() if row[17] else None,
    }


def _metric_deltas(current: dict[str, Any], previous: dict[str, Any]) -> dict[str, float]:
    deltas: dict[str, float] = {}
    for key, value in current.items():
        if isinstance(value, (int, float)) and isinstance(previous.get(key), (int, float)):
            deltas[key] = round(float(value) - float(previous[key]), 6)
    return deltas


def _percentile_disc(values: list[int], percentile: float) -> int | None:
    if not values:
        return None
    index = max(0, min(len(values) - 1, int((len(values) - 1) * percentile)))
    return values[index]


def _blank_to_none(value: str | None) -> str | None:
    text = str(value).strip() if value is not None else ""
    return text or None


def _normalize_benchmark_mode(value: str | None) -> str:
    normalized = str(value or "scan").strip().lower()
    if normalized not in {"scan", "soak", "watcher", "model"}:
        raise ValueError("benchmark mode must be scan, soak, watcher, or model")
    return normalized


def _normalize_benchmark_scope_type(value: str | None) -> str:
    normalized = str(value or "synthetic").strip().lower().replace("-", "_")
    if normalized == "root":
        normalized = "monitored_root"
    if normalized not in {"synthetic", "monitored_root", "path"}:
        raise ValueError("benchmark scope_type must be synthetic, monitored_root, or path")
    return normalized


def _normalize_retrieval_benchmark_suite(value: str | None) -> str:
    normalized = str(value or "standard").strip().lower().replace("_", "-")
    if normalized not in {"standard", "governance-shadow"}:
        raise ValueError("retrieval benchmark suite must be standard or governance-shadow")
    return normalized


def _normalize_governance_mode(value: str | None) -> str:
    normalized = str(value or "shadow").strip().lower().replace("_", "-")
    if normalized not in {"shadow", "manual", "auto"}:
        raise ValueError("governance mode must be shadow, manual, or auto")
    return normalized


def _normalize_governance_run_status(value: str | None) -> str:
    normalized = str(value or "completed").strip().lower().replace("-", "_")
    if normalized not in {"completed", "blocked", "failed"}:
        raise ValueError("governance run status must be completed, blocked, or failed")
    return normalized


def _normalize_governance_action(value: str | None) -> str:
    normalized = str(value or "").strip().lower().replace("-", "_")
    allowed = {
        "mark_review",
        "stale_tag",
        "deprioritize",
        "retire",
        "semantic_cluster_apply",
        "canonical_cluster_promote",
        "capture_ingestion_recheck",
        "feedback_gap_escalate",
        "recover",
    }
    if normalized not in allowed:
        raise ValueError(f"unsupported governance action: {value}")
    return normalized


def _normalize_governance_action_status(value: str | None) -> str:
    normalized = str(value or "proposed").strip().lower().replace("-", "_")
    allowed = {
        "proposed",
        "blocked",
        "skipped_duplicate",
        "skipped_conflict",
        "applied",
        "recovered",
        "failed",
    }
    if normalized not in allowed:
        raise ValueError(f"unsupported governance action status: {value}")
    return normalized


def _normalize_governance_risk(value: str | None) -> str:
    normalized = str(value or "medium").strip().lower()
    if normalized not in {"low", "medium", "high"}:
        return "medium"
    return normalized


def _normalize_governance_memory_class(value: str | None) -> str | None:
    if value is None:
        return None
    normalized = str(value or "").strip().lower()
    return normalized if normalized in {"claim", "episode", "corpus"} else None


def _normalize_operator_automation_mode(value: str | None) -> str:
    normalized = str(value or "guarded").strip().lower().replace("-", "_")
    if normalized not in {"guarded", "suggest_only"}:
        raise ValueError("operator automation mode must be guarded or suggest_only")
    return normalized


def _normalize_operator_automation_run_status(value: str | None) -> str:
    normalized = str(value or "completed").strip().lower().replace("-", "_")
    if normalized not in {"running", "completed", "blocked", "failed"}:
        raise ValueError("operator automation run status must be running, completed, blocked, or failed")
    return normalized


def _normalize_operator_automation_action(value: str | None) -> str:
    normalized = str(value or "").strip().lower().replace("-", "_")
    allowed = {
        "refresh_retrieval_evidence",
        "ingest_approved_capture",
        "safe_diagnostic_recovery",
        "sync_search_index",
        "run_governance_shadow",
    }
    if normalized not in allowed:
        raise ValueError(f"unsupported operator automation action: {value}")
    return normalized


def _normalize_operator_automation_action_status(value: str | None) -> str:
    normalized = str(value or "proposed").strip().lower().replace("-", "_")
    if normalized not in {"proposed", "applied", "skipped", "blocked", "failed"}:
        raise ValueError(f"unsupported operator automation action status: {value}")
    return normalized


def _normalize_code_feedback_category(value: str | None) -> str:
    normalized = str(value or "other").strip().lower().replace("-", "_")
    return normalized if normalized in CODE_FEEDBACK_CATEGORIES else "other"


def _stable_digest(value: Any) -> str:
    return hashlib.sha256(str(value or "").encode("utf-8", errors="ignore")).hexdigest()


def _safe_path_leaf(value: str) -> str:
    if not value:
        return ""
    normalized = value.replace("\\", "/")
    leaf = Path(normalized).name
    return leaf or Path(value).name or "<path>"


def _slow_jobs_from_db(value: Any) -> list[dict[str, Any]]:
    if not isinstance(value, list):
        return []
    rows: list[dict[str, Any]] = []
    for item in value[:5]:
        if not isinstance(item, dict):
            continue
        path = str(item.get("path") or "")
        rows.append(
            {
                "id": str(item.get("id") or ""),
                "path": Path(path).name,
                "duration_ms": item.get("duration_ms"),
            }
        )
    return rows


_SENSITIVE_METADATA_FRAGMENTS = (
    "body",
    "content",
    "credential",
    "embedding",
    "mail",
    "password",
    "private_path",
    "raw",
    "root_path",
    "secret",
    "token",
    "uri",
)


def _sanitize_operational_metadata(value: dict[str, Any]) -> dict[str, Any]:
    sanitized: dict[str, Any] = {}
    for key, item in value.items():
        key_text = str(key)
        lowered = key_text.lower()
        if any(fragment in lowered for fragment in _SENSITIVE_METADATA_FRAGMENTS):
            continue
        sanitized_item = _sanitize_operational_value(item)
        if sanitized_item is not None:
            sanitized[key_text] = sanitized_item
    return sanitized


def _sanitize_operational_value(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return value[:200]
    if isinstance(value, dict):
        return _sanitize_operational_metadata(value)
    if isinstance(value, list):
        sanitized = [_sanitize_operational_value(item) for item in value[:20]]
        return [item for item in sanitized if item is not None]
    return str(value)[:200]


def _sanitize_governance_metadata(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return value[:200]
    if isinstance(value, list):
        sanitized = [_sanitize_governance_metadata(item) for item in value[:50]]
        return [item for item in sanitized if item is not None]
    if not isinstance(value, dict):
        return str(value)[:200]
    sanitized: dict[str, Any] = {}
    for key, item in value.items():
        key_text = str(key)
        lowered = key_text.lower()
        if lowered in {"raw", "body", "content", "text", "prompt", "query", "snippet", "error"}:
            continue
        if any(fragment in lowered for fragment in ("private_path", "embedding", "secret", "token", "credential", "password")):
            continue
        if lowered in {"path", "source_path", "file_path"}:
            leaf = _safe_path_leaf(str(item or ""))
            if leaf:
                sanitized["source_leaf"] = leaf
            continue
        sanitized_item = _sanitize_governance_metadata(item)
        if sanitized_item is not None:
            sanitized[key_text] = sanitized_item
    return sanitized


def _sanitize_retrieval_case_results(value: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for item in value[:100]:
        if not isinstance(item, dict):
            continue
        sanitized = _sanitize_retrieval_case_result(item)
        if sanitized:
            rows.append(sanitized)
    return rows


def _sanitize_retrieval_case_result(value: dict[str, Any]) -> dict[str, Any]:
    blocked_keys = {
        "query",
        "raw_query",
        "snippet",
        "summary",
        "excerpt",
        "text",
        "body",
        "content",
    }
    sanitized: dict[str, Any] = {}
    for key, item in value.items():
        key_text = str(key)
        lowered = key_text.lower()
        if lowered in blocked_keys or any(fragment in lowered for fragment in _SENSITIVE_METADATA_FRAGMENTS):
            continue
        sanitized_item = _sanitize_operational_value(item)
        if sanitized_item is not None:
            sanitized[key_text] = sanitized_item
    return sanitized


def _find_canonical_asset_id(cur: Any, content_hash: str | None, current_id: str | None) -> str | None:
    if not content_hash:
        return None
    if current_id:
        cur.execute(
            """
            SELECT id::text
            FROM source_assets
            WHERE content_hash = %s
              AND id <> %s
              AND canonical_asset_id IS NULL
            ORDER BY created_at
            LIMIT 1
            """,
            (content_hash, current_id),
        )
    else:
        cur.execute(
            """
            SELECT id::text
            FROM source_assets
            WHERE content_hash = %s
              AND canonical_asset_id IS NULL
            ORDER BY created_at
            LIMIT 1
            """,
            (content_hash,),
        )
    row = cur.fetchone()
    return row[0] if row else None


_MODEL_ACTIVITY_STATUSES = {"running", "completed", "failed", "busy", "stale_running", "blocked_missing_dependency"}
_MODEL_ACTIVITY_CLASSES = {"retrieval", "vision_ocr", "sidecar", "health", "control_plane", "model_loading"}
_MODEL_ACTIVITY_METADATA_KEYS = {
    "batch_size",
    "component",
    "dimensions",
    "document",
    "duration_hint_ms",
    "input_count",
    "keep_alive",
    "passage_count",
    "quantization",
    "resident",
    "route",
    "stale_running_recovered",
    "task_type",
}
MODEL_ACTIVITY_TEST_DATABASE_URL_ENV = "FLUX_KB_TEST_DATABASE_URL"
MODEL_ACTIVITY_TEST_WRITE_OPT_IN_ENV = "FLUX_KB_ALLOW_MODEL_ACTIVITY_TEST_WRITES"


def _model_activity_test_write_opted_in() -> bool:
    return str(os.environ.get(MODEL_ACTIVITY_TEST_WRITE_OPT_IN_ENV) or "").strip().lower() in {"1", "true", "yes", "on"}


def _model_activity_write_url(url: str | None) -> str:
    if url:
        return str(url)
    test_url = os.environ.get(MODEL_ACTIVITY_TEST_DATABASE_URL_ENV)
    if os.environ.get("PYTEST_CURRENT_TEST") and _model_activity_test_write_opted_in() and test_url:
        return test_url
    return database_url()


def _guard_model_activity_test_write(url: str | None) -> None:
    if not os.environ.get("PYTEST_CURRENT_TEST"):
        return
    target_url = str(url or database_url())
    test_url = os.environ.get(MODEL_ACTIVITY_TEST_DATABASE_URL_ENV)
    opted_in = _model_activity_test_write_opted_in()
    if opted_in and test_url and target_url == test_url:
        return
    if opted_in and not test_url:
        raise RuntimeError(
            "model activity writes are disabled under pytest because "
            f"{MODEL_ACTIVITY_TEST_DATABASE_URL_ENV} is not set"
        )
    if opted_in and test_url and target_url != test_url:
        raise RuntimeError(
            "model activity writes are disabled under pytest unless the target URL matches "
            f"{MODEL_ACTIVITY_TEST_DATABASE_URL_ENV}"
        )
    raise RuntimeError(
        "model activity writes are disabled under pytest unless "
        f"{MODEL_ACTIVITY_TEST_WRITE_OPT_IN_ENV}=1 and {MODEL_ACTIVITY_TEST_DATABASE_URL_ENV} is used"
    )


def start_model_activity_event(
    *,
    service: str,
    endpoint: str = "",
    action: str = "",
    activity_class: str = "sidecar",
    caller_surface: str = "",
    model: str = "",
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> str:
    target_url = _model_activity_write_url(url)
    _guard_model_activity_test_write(target_url)
    psycopg = _load_psycopg()
    try:
        from psycopg.types.json import Jsonb
    except ImportError:  # pragma: no cover - depends on optional psycopg extras
        Jsonb = lambda value: _json(value)  # type: ignore[assignment]
    with psycopg.connect(target_url) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO model_activity_events (
                    service, endpoint, action, activity_class, caller_surface,
                    model, status, metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, 'running', %s)
                RETURNING id::text
                """,
                (
                    _model_activity_text(service, fallback="unknown", max_length=80),
                    _model_activity_text(endpoint, max_length=120),
                    _model_activity_text(action, max_length=80),
                    _model_activity_class(activity_class),
                    _model_activity_text(caller_surface, max_length=40),
                    _model_activity_text(model, max_length=200),
                    Jsonb(_sanitize_model_activity_metadata(metadata or {})),
                ),
            )
            row = cur.fetchone()
            return str(row[0])


def finish_model_activity_event(
    *,
    event_id: str,
    status: str,
    duration_ms: int | None = None,
    error_class: str | None = None,
    error_message: str | None = None,
    url: str | None = None,
) -> None:
    target_url = _model_activity_write_url(url)
    _guard_model_activity_test_write(target_url)
    psycopg = _load_psycopg()
    with psycopg.connect(target_url) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE model_activity_events
                   SET status = %s,
                       completed_at = CASE WHEN %s = 'running' THEN completed_at ELSE now() END,
                       duration_ms = %s,
                       error_class = %s,
                       error_message = %s
                 WHERE id = %s
                """,
                (
                    _model_activity_status(status),
                    _model_activity_status(status),
                    max(0, int(duration_ms)) if duration_ms is not None else None,
                    _model_activity_text(error_class, max_length=120) or None,
                    _model_activity_text(error_message, max_length=300) or None,
                    event_id,
                ),
            )


def list_model_activity_events(
    *,
    window_minutes: int = 60,
    limit: int = 50,
    offset: int = 0,
    include_control_plane: bool = True,
    url: str | None = None,
) -> list[dict[str, Any]]:
    safe_window = max(5, min(int(window_minutes or 60), 360))
    safe_limit = max(1, min(int(limit or 50), 200))
    safe_offset = max(0, int(offset or 0))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, service, endpoint, action, activity_class, caller_surface,
                       model, status, started_at, completed_at, duration_ms,
                       error_class, error_message, metadata
                  FROM model_activity_events
                 WHERE (started_at >= now() - (%s * interval '1 minute')
                    OR status = 'running')
                   AND (%s OR activity_class NOT IN ('health', 'control_plane'))
                 ORDER BY COALESCE(completed_at, started_at) DESC NULLS LAST,
                          started_at DESC NULLS LAST,
                          id DESC
                 LIMIT %s
                OFFSET %s
                """,
                (safe_window, bool(include_control_plane), safe_limit, safe_offset),
            )
            return [
                {
                    "id": row[0],
                    "service": row[1],
                    "endpoint": row[2],
                    "action": row[3],
                    "activity_class": row[4],
                    "caller_surface": row[5],
                    "model": row[6],
                    "status": row[7],
                    "started_at": row[8],
                    "completed_at": row[9],
                    "duration_ms": row[10],
                    "error_class": row[11],
                    "error_message": row[12],
                    "metadata": _sanitize_model_activity_metadata(row[13] or {}),
                }
                for row in cur.fetchall()
            ]


def count_model_activity_events(
    *,
    window_minutes: int = 60,
    include_control_plane: bool = True,
    url: str | None = None,
) -> int:
    safe_window = max(5, min(int(window_minutes or 60), 360))
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT count(*)
                  FROM model_activity_events
                 WHERE (started_at >= now() - (%s * interval '1 minute')
                    OR status = 'running')
                   AND (%s OR activity_class NOT IN ('health', 'control_plane'))
                """,
                (safe_window, bool(include_control_plane)),
            )
            row = cur.fetchone()
            return int(row[0] or 0) if row else 0


def recover_stale_model_activity_events(*, stale_after_seconds: int | float, url: str | None = None) -> dict[str, int]:
    safe_stale_after = max(1, int(float(stale_after_seconds or 0)))
    target_url = _model_activity_write_url(url)
    _guard_model_activity_test_write(target_url)
    psycopg = _load_psycopg()
    message = _model_activity_text(
        "Model activity exceeded the stale threshold without a finish update.",
        max_length=300,
    )
    with psycopg.connect(target_url) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                WITH recovered AS (
                    UPDATE model_activity_events
                       SET status = 'stale_running',
                           completed_at = COALESCE(completed_at, now()),
                           duration_ms = COALESCE(
                               duration_ms,
                               GREATEST(0, FLOOR(EXTRACT(EPOCH FROM (now() - started_at)) * 1000))::integer
                           ),
                           error_class = COALESCE(error_class, 'ModelActivityStale'),
                           error_message = COALESCE(error_message, %s),
                           metadata = COALESCE(metadata, '{}'::jsonb)
                               || jsonb_build_object('stale_running_recovered', true)
                     WHERE status = 'running'
                       AND completed_at IS NULL
                       AND started_at < now() - (%s * interval '1 second')
                     RETURNING 1
                )
                SELECT count(*) FROM recovered
                """,
                (message, safe_stale_after),
            )
            row = cur.fetchone()
    return {"recovered": int(row[0] or 0) if row else 0}


def prune_model_activity_events(*, retention_hours: int = 24, url: str | None = None) -> None:
    safe_retention = max(1, min(int(retention_hours or 24), 168))
    target_url = _model_activity_write_url(url)
    _guard_model_activity_test_write(target_url)
    psycopg = _load_psycopg()
    with psycopg.connect(target_url) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                DELETE FROM model_activity_events
                 WHERE started_at < now() - (%s * interval '1 hour')
                """,
                (safe_retention,),
            )


def _model_activity_status(value: str | None) -> str:
    normalized = str(value or "running").strip().lower()
    return normalized if normalized in _MODEL_ACTIVITY_STATUSES else "failed"


def _model_activity_class(value: str | None) -> str:
    normalized = str(value or "sidecar").strip().lower()
    return normalized if normalized in _MODEL_ACTIVITY_CLASSES else "sidecar"


def _model_activity_text(value: Any, *, fallback: str = "", max_length: int) -> str:
    text = str(value or fallback).strip()
    return text[:max_length]


def _sanitize_model_activity_metadata(value: dict[str, Any]) -> dict[str, Any]:
    sanitized: dict[str, Any] = {}
    for key, item in dict(value or {}).items():
        key_text = str(key)
        if key_text not in _MODEL_ACTIVITY_METADATA_KEYS:
            continue
        safe = _sanitize_model_activity_value(item)
        if safe is not None:
            sanitized[key_text] = safe
    return sanitized


def _sanitize_model_activity_value(value: Any) -> Any:
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return round(value, 6)
    if isinstance(value, str):
        return value[:120]
    return None


def _load_psycopg():
    try:
        import psycopg
    except ImportError as exc:  # pragma: no cover - depends on environment
        raise RuntimeError("Install PostgreSQL dependencies with `pip install -e .`") from exc
    return psycopg


def _postgres_text_or_none(value: Any) -> str | None:
    if value is None:
        return None
    return strip_postgres_nul(str(value))


def _json(value: dict[str, Any]) -> str:
    import json

    return json.dumps(sanitize_postgres_text_value(value), sort_keys=True)


def _json_any(value: Any) -> str:
    import json

    return json.dumps(sanitize_postgres_text_value(value), sort_keys=True)


def _json_list(value: list[dict[str, Any]]) -> str:
    import json

    return json.dumps(sanitize_postgres_text_value(value), sort_keys=True)
