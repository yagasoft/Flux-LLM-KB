from __future__ import annotations

from dataclasses import dataclass
from datetime import UTC, datetime
import hashlib
import os
from pathlib import Path
import re
import time
from typing import Any, Callable, Iterable
from urllib.parse import quote, urlsplit, urlunsplit
from uuid import UUID

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
    DEFAULT_EMBEDDING_DIMENSIONS,
    DEFAULT_EMBEDDING_MODEL,
    EmbeddingInput,
    EmbeddingResult,
    HashEmbeddingProvider,
    embed_text,
    embedding_source_hash,
    to_pgvector_literal,
)
from . import mail_content_store
from .migrations import load_migrations
from .scoring import LifecycleScoreInput, RankedItem, lifecycle_score, reciprocal_rank_fusion
from .text_safety import sanitize_postgres_text_value, strip_postgres_nul


DEFAULT_DATABASE_URL = "postgresql://flux:flux@127.0.0.1:5432/flux_llm_kb"
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
_SEMANTIC_DUPLICATE_ALGORITHM = f"{DEFAULT_EMBEDDING_MODEL}:cosine"
UNSEEN_ASSET_CANCELLED_STATUS = "cancelled_unseen_asset"
DEFAULT_UNSEEN_ASSET_PURGE_GRACE_SECONDS = 86400
DEFAULT_UNSEEN_ASSET_PURGE_BATCH_SIZE = 500
_CODE_EXACT_SYMBOL_BOOST = 0.10
_CODE_IMPLEMENTATION_INTENT_BOOST = 0.025
_CODE_IMPLEMENTATION_RELATIONSHIPS = {"definition", "route", "config", "migration"}
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


def _embedding_result_for_text(*, owner_table: str, owner_id: str, text: str) -> EmbeddingResult:
    return HashEmbeddingProvider().embed_batch(
        [
            EmbeddingInput(
                owner_table=owner_table,
                owner_id=owner_id,
                text=text,
                model=DEFAULT_EMBEDDING_MODEL,
                dimensions=DEFAULT_EMBEDDING_DIMENSIONS,
            )
        ]
    )[0]


def _insert_embedding_result(cur: Any, result: EmbeddingResult) -> None:
    cur.execute(
        """
        INSERT INTO embeddings (owner_table, owner_id, model, dimensions, embedding, metadata, root_id, updated_at)
        VALUES (
            %s,
            %s,
            %s,
            %s,
            %s::vector,
            %s::jsonb,
            CASE WHEN %s = 'asset_chunks' THEN (
                SELECT a.root_id
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                WHERE c.id = %s::uuid
            ) ELSE NULL END,
            now()
        )
        """,
        (
            result.owner_table,
            result.owner_id,
            result.model,
            result.dimensions,
            to_pgvector_literal(result.vector),
            _json(result.metadata),
            result.owner_table,
            result.owner_id,
        ),
    )


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
            _insert_embedding_result(
                cur,
                _embedding_result_for_text(
                    owner_table="episodes",
                    owner_id=episode_id,
                    text=f"{title}\n{summary}",
                ),
            )
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
    query_vector = to_pgvector_literal(embed_text(query))
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
                       1 - (emb.embedding <=> %s::vector) AS score,
                       jsonb_build_object('metadata', e.metadata) AS metadata
                FROM embeddings emb
                JOIN episodes e
                  ON emb.owner_table = 'episodes'
                 AND emb.owner_id = e.id
                WHERE emb.model = %s
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
                ORDER BY emb.embedding <=> %s::vector
                LIMIT %s
                """,
                (query_vector, DEFAULT_EMBEDDING_MODEL, *aliased_scope_params, query_vector, candidate_limit),
            )
            _add_ranked_rows("vector", cur.fetchall(), streams, details)

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
    max_vector_candidate_limit = min(max(candidate_limit * 2, 240), 400)
    query_vector = to_pgvector_literal(embed_text(query))
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

            cur.execute("SELECT set_config('pg_trgm.similarity_threshold', %s, true)", ("0.10",))
            started = time.perf_counter()
            cur.execute(
                f"""
                SELECT c.id::text,
                       greatest(similarity(c.title, %s), similarity(c.body, %s)) AS score,
                       c.updated_at
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND a.extraction_status = 'indexed'
                  AND (c.title %% %s OR c.body %% %s)
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

            if root_name and root_id is None:
                streams["corpus_vector"] = []
                _record_corpus_stream_diagnostics(
                    diagnostics,
                    "corpus_vector",
                    time.perf_counter(),
                    rows=0,
                    plan="root_not_found",
                )
            else:
                vector_root_sql = "AND (nearest.root_id = %s::uuid OR nearest.root_id IS NULL)" if root_id else ""
                vector_root_params: tuple[Any, ...] = (root_id,) if root_id else ()
                non_vector_candidate_count = len(raw_scores)
                vector_candidate_limit = 80 if non_vector_candidate_count >= candidate_limit else max_vector_candidate_limit
                cur.execute("SELECT set_config('hnsw.ef_search', %s, true)", (str(vector_candidate_limit),))
                started = time.perf_counter()
                cur.execute(
                    f"""
                    WITH nearest_embeddings AS MATERIALIZED (
                        SELECT emb.owner_id,
                               emb.root_id,
                               1 - (emb.embedding <=> %s::vector) AS score,
                               emb.embedding <=> %s::vector AS distance
                        FROM embeddings emb
                        WHERE emb.owner_table = 'asset_chunks'
                          AND emb.model = %s
                        ORDER BY emb.embedding <=> %s::vector
                        LIMIT %s
                    )
                    SELECT c.id::text, nearest.score
                    FROM nearest_embeddings nearest
                    JOIN asset_chunks c ON c.id = nearest.owner_id
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE r.enabled
                      AND a.deleted_at IS NULL
                      AND a.canonical_asset_id IS NULL
                      AND a.extraction_status = 'indexed'
                      AND nearest.score > 0.25
                      {vector_root_sql}
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
                    ORDER BY nearest.distance
                    LIMIT %s
                    """,
                    (
                        query_vector,
                        query_vector,
                        DEFAULT_EMBEDDING_MODEL,
                        query_vector,
                        vector_candidate_limit,
                        *vector_root_params,
                        *root_name_params,
                        *code_filter_params,
                        candidate_limit,
                    ),
                )
                rows = cur.fetchall()
                _add_ranked_corpus_candidates("corpus_vector", rows, streams, raw_scores)
                _record_corpus_stream_diagnostics(
                    diagnostics,
                    "corpus_vector",
                    started,
                    rows=len(rows),
                    plan="root_scoped_hnsw_candidates" if root_id else "global_hnsw_candidates",
                    candidate_limit=vector_candidate_limit,
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
                if item_class == "claim":
                    payload["claim_embeddings_backfilled"] = payload.get("claim_embeddings_backfilled", 0) + _backfill_claim_embeddings(
                        cur,
                        limit=row_limit,
                    )
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
                pairs = _fetch_semantic_duplicate_pairs(
                    cur,
                    memory_class=item_class,
                    root_name=root_name,
                    threshold=normalized_threshold,
                    limit=row_limit,
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
            cur.execute("DELETE FROM embeddings WHERE owner_table = 'episodes' AND owner_id = %s", (parsed_id,))
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
                DELETE FROM embeddings
                WHERE owner_table = 'claims'
                  AND owner_id = %s
                  AND model = %s
                """,
                (claim["id"], DEFAULT_EMBEDDING_MODEL),
            )
            _insert_embedding_result(
                cur,
                _embedding_result_for_text(
                    owner_table="claims",
                    owner_id=claim["id"],
                    text=f"{subject_name}\n{predicate}\n{object_text}",
                ),
            )
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
                      AND (%s::text[] IS NULL OR relation_type = ANY(%s::text[]))
                    UNION ALL
                    SELECT re.id AS relation_id, re.from_entity_id, re.to_entity_id,
                           edge.next_entity_id, re.relation_type, re.confidence,
                           re.metadata, graph.depth + 1 AS depth,
                           graph.path || edge.next_entity_id AS path
                    FROM graph
                    JOIN relation_edges re ON re.from_entity_id = graph.next_entity_id
                    JOIN LATERAL (SELECT re.to_entity_id AS next_entity_id) edge ON true
                    WHERE graph.depth < %s
                      AND (%s::text[] IS NULL OR relation_type = ANY(%s::text[]))
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
                    WHERE job_type LIKE 'corpus_%%'
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
                DELETE FROM embeddings
                WHERE owner_table = 'asset_chunks'
                  AND owner_id IN (
                    SELECT c.id
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    WHERE a.root_id = %s
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
                WHERE job_type LIKE 'corpus_%%'
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
                (actor, actual_root_id, _json({"name": name, "purged_index": purge_index})),
            )
            return {"id": actual_root_id, "name": name, "deleted": True, "purged_index": purge_index}


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
                    SELECT id::text, quick_hash, extraction_status, extension
                    FROM source_assets
                    WHERE root_id = %s AND path = %s
                    """,
                    (root_id, asset.relative_path),
                )
                previous = cur.fetchone()
                changed_asset = previous is None or previous[1] != asset.quick_hash
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
                    and previous[2] == "metadata_only"
                    and asset.extension in REQUEUE_DOCUMENT_EXTENSIONS
                    and asset.extraction_tier == "deferred"
                    and canonical_id is None
                )
                recovered_indexed_asset = (
                    previous is not None
                    and not changed_asset
                    and previous[2] in {"metadata_only", "blocked_missing_dependency"}
                    and status == "indexed"
                    and canonical_id is None
                )
                recovered_deferred_asset = (
                    previous is not None
                    and not changed_asset
                    and previous[2] in {"metadata_only", "blocked_missing_dependency"}
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
                            WHEN EXCLUDED.extraction_status = 'blocked_missing_dependency'
                                 AND EXCLUDED.metadata ? 'metadata_only_blocked'
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.extraction_status = 'metadata_only'
                                 AND source_assets.extension = ANY(%s)
                                 AND EXCLUDED.extraction_status = 'queued'
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.extraction_status IN ('metadata_only', 'blocked_missing_dependency')
                                 AND EXCLUDED.extraction_status IN ('indexed', 'queued')
                                THEN EXCLUDED.extraction_status
                            WHEN source_assets.extraction_status IN ('indexed', 'metadata_only', 'blocked_missing_dependency')
                                THEN source_assets.extraction_status
                            ELSE EXCLUDED.extraction_status
                        END,
                        extraction_tier = EXCLUDED.extraction_tier,
                        last_seen_at = now(),
                        indexed_at = CASE
                            WHEN source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash
                                THEN EXCLUDED.indexed_at
                            WHEN source_assets.extraction_status IN ('metadata_only', 'blocked_missing_dependency')
                                 AND EXCLUDED.extraction_status = 'indexed'
                                THEN EXCLUDED.indexed_at
                            ELSE source_assets.indexed_at
                        END,
                        deleted_at = NULL,
                        metadata = CASE
                            WHEN source_assets.extraction_status IN ('metadata_only', 'blocked_missing_dependency')
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
                        cur.execute(
                            """
                            INSERT INTO capture_jobs (
                                job_type, payload, job_family, resource_class, priority, time_budget_seconds
                            )
                            VALUES (%s, %s::jsonb, %s, %s, %s, %s)
                            """,
                            (
                                job_type,
                                _json({"root_name": root_name, "path": asset.relative_path}),
                                schedule["job_family"],
                                schedule["resource_class"],
                                schedule["priority"],
                                schedule["time_budget_seconds"],
                            ),
                        )
                        jobs_queued += 1
                    elif asset.extraction_status == "retrying_locked" and not canonical_id:
                        job_type = f"corpus_extract_{asset.file_kind}"
                        schedule = _job_schedule_metadata(job_type)
                        cur.execute(
                            """
                            INSERT INTO capture_jobs (
                                job_type, payload, status, last_error, next_attempt_at,
                                job_family, resource_class, priority, time_budget_seconds
                            )
                            VALUES (%s, %s::jsonb, 'retrying_locked', %s, now() + make_interval(secs => 300), %s, %s, %s, %s)
                            """,
                            (
                                job_type,
                                _json({"root_name": root_name, "path": asset.relative_path}),
                                str(asset.metadata.get("error") or "file locked"),
                                schedule["job_family"],
                                schedule["resource_class"],
                                schedule["priority"],
                                schedule["time_budget_seconds"],
                            ),
                        )
                        jobs_queued += 1
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
            cur.execute("SELECT count(*) FROM capture_jobs WHERE job_type LIKE 'corpus_%%' AND status = 'blocked_missing_dependency'")
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


def list_capture_jobs(
    *,
    limit: int = 50,
    offset: int = 0,
    status: str | list[str] | None = None,
    root_name: str | list[str] | None = None,
    job_type: str | list[str] | None = None,
    updated_from: str | None = None,
    updated_to: str | None = None,
    url: str | None = None,
) -> list[dict[str, Any]]:
    where_sql, params = _capture_job_filter_sql(
        status=status,
        root_name=root_name,
        job_type=job_type,
        updated_from=updated_from,
        updated_to=updated_to,
    )
    params.extend([_capture_job_limit(limit), _capture_job_offset(offset)])
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT id::text, job_type, job_family, resource_class, priority, time_budget_seconds,
                       status, payload, attempts, last_error, created_at, updated_at,
                       started_at, completed_at, last_duration_ms, telemetry,
                       locked_at, locked_by, progress_heartbeat_at
                FROM capture_jobs
                WHERE {where_sql}
                ORDER BY updated_at DESC, id DESC
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
                embeddings_deleted AS (
                    DELETE FROM embeddings
                    WHERE owner_table = 'asset_chunks'
                      AND owner_id IN (
                          SELECT c.id
                          FROM asset_chunks c
                          JOIN eligible e ON e.id = c.asset_id
                      )
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
                                AND first_sync.status IN ('pending', 'retrying_locked')
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
                                AND other.payload->>'root_name' = capture_jobs.payload->>'root_name'
                          )
                      )
        """
    caps_cte = ""
    claim_selection = f"""
                    SELECT id
                    FROM capture_jobs
                    WHERE job_type LIKE 'corpus_%%'
                      AND status IN ('pending', 'retrying_locked')
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
                         WHERE job_type LIKE 'corpus_%%'
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
                         WHERE capture_jobs.job_type LIKE 'corpus_%%'
                           AND capture_jobs.status IN ('pending', 'retrying_locked')
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
                    telemetry = telemetry || %s::jsonb,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
                """,
                (duration_ms, _json(telemetry or {}), job_id),
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
                    WHERE job.job_type LIKE 'corpus_%%'
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
                  AND job_type LIKE 'corpus_%%'
                  AND status = 'running'
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
                    telemetry = telemetry || jsonb_build_object('remediation_reason', %s::text),
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                  AND (
                      status = 'failed'
                      OR status LIKE 'blocked_%%'
                      OR status = 'retrying_locked'
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
            if status not in {"pending", "retrying_locked"}:
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
                  AND status IN ('pending', 'retrying_locked')
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
                  AND job_type LIKE 'corpus_%%'
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
                    RETURNING id::text
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
                        OR a.extraction_status <> 'indexed'
                        OR (
                            array_length(string_to_array(a.path, '/'), 1) = 2
                            AND lower(split_part(a.path, '/', 2)) IN ('message.eml', 'message.msg', 'body.html')
                        )
                    )
                      {root_filter}
                ),
                deleted AS (
                    DELETE FROM embeddings e
                    USING stale_chunks s
                    WHERE e.owner_table = 'asset_chunks'
                      AND e.owner_id = s.id
                    RETURNING 1
                )
                SELECT count(*) FROM deleted
                """,
                params,
            )
            embeddings_purged = int(cur.fetchone()[0] or 0)
            cur.execute(
                f"""
                WITH stale_chunks AS (
                    SELECT c.id
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE (
                        a.deleted_at IS NOT NULL
                        OR a.extraction_status <> 'indexed'
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
            repaired_mail_chunk_ids: list[str] = []
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
                repaired_mail_chunk_ids.append(str(chunk_id))
                mail_plaintext_chunks_repaired += 1
            if repaired_mail_chunk_ids:
                cur.execute(
                    """
                    DELETE FROM embeddings
                    WHERE owner_table = 'asset_chunks'
                      AND owner_id = ANY(%s::uuid[])
                    """,
                    (repaired_mail_chunk_ids,),
                )
                embeddings_purged += int(getattr(cur, "rowcount", 0) or 0)
            return {
                "root_name": root_name,
                "repaired": repaired,
                "internal_mail_artifacts_deleted": internal_deleted,
                "chunks_purged": chunks_purged,
                "embeddings_purged": embeddings_purged,
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
                    "blocked_locked": row[36],
                    "slowest_recent_jobs": _slow_jobs_from_db(row[37] or []),
                }
                for row in cur.fetchall()
            ]


def code_index_status(*, root_name: str | None = None, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    root_filter = "WHERE r.name = %s" if root_name else ""
    params: list[Any] = []
    if root_name:
        params.append(root_name)
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
                SELECT r.name,
                       count(DISTINCT a.id)::integer AS asset_count,
                       count(DISTINCT c.id)::integer AS chunk_count,
                       count(DISTINCT cs.id)::integer AS symbol_count,
                       count(DISTINCT cr.id)::integer AS reference_count,
                       count(DISTINCT a.id) FILTER (
                         WHERE a.metadata->'code'->>'parser_status' = 'fallback'
                            OR c.metadata->'code'->>'parser_status' = 'fallback'
                            OR cs.parser_status = 'fallback'
                       )::integer AS fallback_count,
                       count(DISTINCT a.id) FILTER (
                         WHERE a.metadata->'code'->>'generated' = 'true'
                            OR c.metadata->>'generated' = 'true'
                            OR c.metadata->'code'->>'generated' = 'true'
                            OR cs.metadata->>'generated' = 'true'
                       )::integer AS generated_count
                FROM monitored_roots r
                LEFT JOIN source_assets a ON a.root_id = r.id AND a.deleted_at IS NULL
                LEFT JOIN asset_chunks c ON c.asset_id = a.id
                LEFT JOIN code_symbols cs ON cs.source_asset_id = a.id
                LEFT JOIN code_references cr ON cr.source_asset_id = a.id
                {root_filter}
                GROUP BY r.name
                ORDER BY r.name
                """,
                tuple(params),
            )
            roots = []
            for row in cur.fetchall():
                roots.append(
                    {
                        "root_name": row[0],
                        "asset_count": row[1],
                        "chunk_count": row[2],
                        "symbol_count": row[3],
                        "reference_count": row[4],
                        "fallback_count": row[5],
                        "generated_count": row[6],
                        "languages": _code_language_counts(cur, row[0]),
                        "parser_statuses": _code_parser_status_counts(cur, row[0]),
                        "slow_files": _code_slow_files(cur, row[0]),
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
                job_type = "corpus_embed" if job_family == "embedding" else f"corpus_extract_{job_family}"
                payload = {
                    "benchmark": True,
                    "benchmark_tag": tag,
                    "benchmark_fixture": fixture,
                    "benchmark_label": label,
                    "benchmark_outcome": "blocked" if index % 7 == 6 else "completed",
                    "path": f"synthetic/{fixture}/{job_family}-{index:04d}",
                    "root_name": "__benchmark__",
                }
                cur.execute(
                    """
                    INSERT INTO capture_jobs (
                        job_type, payload, job_family, resource_class, priority, time_budget_seconds, telemetry
                    )
                    VALUES (%s, %s::jsonb, %s, %s, %s, %s, %s::jsonb)
                    """,
                    (
                        job_type,
                        _json(payload),
                        job_family,
                        resource_class_for_family(job_family),
                        default_priority_for_family(job_family),
                        time_budget_for_family(job_family),
                        _json({"benchmark_tag": tag, "benchmark_fixture": fixture, "benchmark_file_count": row_count}),
                    ),
                )
                inserted += 1
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
                             AND COALESCE(%s::jsonb->>'readiness_status', '') <> 'blocked_missing_dependency'
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
        cur.execute(
            """
            INSERT INTO capture_jobs (
                job_type, payload, job_family, resource_class, priority, time_budget_seconds, telemetry
            )
            VALUES (%s, %s::jsonb, %s, %s, %s, %s, %s::jsonb)
            RETURNING id::text, status
            """,
            (
                job_type,
                _json(job_payload),
                schedule["job_family"],
                schedule["resource_class"],
                schedule["priority"],
                schedule["time_budget_seconds"],
                _json({"stage": "queued", "root_name": root_name, "path": relative_path, "staged": True}),
            ),
        )
        row = cur.fetchone()
        if row:
            job_id = row[0]
            job_ids.append(job_id)
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('capture_job.queued', 'capture_jobs', %s, %s::jsonb)
                """,
                (job_id, _json({"job_type": job_type, "root_name": root_name, "path": relative_path, "staged": True})),
            )
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
                     AND COALESCE(%s::jsonb->>'readiness_status', '') <> 'blocked_missing_dependency'
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
                "embeddings": "embeddings",
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
            return {
                "id": row[0],
                "setting_key": row[1],
                "action": row[2],
                "affected_components": list(row[3] or []),
                "status": row[4],
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
            return {
                "id": row[0],
                "profile_name": profile_name,
                "status": row[1],
                "created_at": row[2].isoformat(),
            }


def list_outlook_sync_requests(*, limit: int = 20, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT r.id::text, p.name, r.status, r.requested_by, r.claimed_by,
                       r.error, r.result, r.created_at, r.updated_at
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
                  AND status IN ('pending', 'retrying_locked')
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
                return {"job_id": row[0], "status": row[1], "root_name": root, "deduped": True}
            cur.execute(
                """
                INSERT INTO capture_jobs (
                    job_type, payload, job_family, resource_class, priority, time_budget_seconds, telemetry
                )
                VALUES (%s, %s::jsonb, %s, %s, %s, %s, %s::jsonb)
                RETURNING id::text, status
                """,
                (
                    "corpus_sync_root",
                    _json(job_payload),
                    schedule["job_family"],
                    schedule["resource_class"],
                    schedule["priority"],
                    schedule["time_budget_seconds"],
                    _json({"stage": "queued", "root_name": root}),
                ),
            )
            row = cur.fetchone()
            job_id = row[0]
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('capture_job.queued', 'capture_jobs', %s, %s::jsonb)
                """,
                (job_id, _json({"job_type": "corpus_sync_root", "root_name": root, "reason": job_payload["reason"]})),
            )
            result = {"job_id": job_id, "status": row[1], "root_name": root, "deduped": False}
            if running:
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
                cur.execute(
                    """
                    INSERT INTO capture_jobs (
                        job_type, payload, job_family, resource_class, priority, time_budget_seconds, telemetry
                    )
                    VALUES (%s, %s::jsonb, %s, %s, %s, %s, %s::jsonb)
                    RETURNING id::text, status
                    """,
                    (
                        "corpus_sync_root",
                        _json(job_payload),
                        schedule["job_family"],
                        schedule["resource_class"],
                        schedule["priority"],
                        schedule["time_budget_seconds"],
                        _json(
                            {
                                "stage": "queued",
                                "root_name": root,
                                "paths_total": len(clean_paths),
                                "paths_queued": len(batch_paths),
                                "path_batch_index": batch_index,
                                "path_batch_total": len(batches),
                            }
                        ),
                    ),
                )
                row = cur.fetchone()
                job_id = row[0]
                cur.execute(
                    """
                    INSERT INTO audit_events (event_type, target_table, target_id, details)
                    VALUES ('capture_job.queued', 'capture_jobs', %s, %s::jsonb)
                    """,
                    (
                        job_id,
                        _json(
                            {
                                "job_type": "corpus_sync_root",
                                "root_name": root,
                                "reason": job_payload["reason"],
                                "paths_queued": len(batch_paths),
                                "path_batch_index": batch_index,
                                "path_batch_total": len(batches),
                            }
                        ),
                    ),
                )
                jobs.append(
                    {
                        "job_id": job_id,
                        "status": row[1],
                        "root_name": root,
                        "deduped": False,
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


def enqueue_capture_job(
    *, job_type: str, payload: dict[str, Any], url: str | None = None
) -> str:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO capture_jobs (job_type, payload)
                VALUES (%s, %s::jsonb)
                RETURNING id::text
                """,
                (job_type, _json(payload)),
            )
            job_id = cur.fetchone()[0]
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES ('capture_job.queued', 'capture_jobs', %s, %s::jsonb)
                """,
                (job_id, _json({"job_type": job_type})),
            )
            return job_id


def requeue_metadata_only_source_assets(
    *,
    root_name: str | None = None,
    limit: int = 1000,
    url: str | None = None,
) -> dict[str, Any]:
    row_limit = max(1, min(int(limit or 1000), 10000))
    filters = ["a.deleted_at IS NULL", "a.extraction_status = 'metadata_only'"]
    params: list[Any] = []
    if root_name:
        filters.append("r.name = %s")
        params.append(root_name)
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
                cur.execute(
                    """
                    INSERT INTO capture_jobs (
                        job_type, payload, job_family, resource_class, priority, time_budget_seconds, telemetry
                    )
                    VALUES (%s, %s::jsonb, %s, %s, %s, %s, %s::jsonb)
                    RETURNING id::text
                    """,
                    (
                        job_type,
                        _json(payload),
                        schedule["job_family"],
                        schedule["resource_class"],
                        schedule["priority"],
                        schedule["time_budget_seconds"],
                        _json({"stage": "queued", "root_name": row_root_name, "path": path, "reason": "metadata_only_requeue"}),
                    ),
                )
                job_id = cur.fetchone()[0]
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
                cur.execute(
                    """
                    INSERT INTO audit_events (event_type, target_table, target_id, details)
                    VALUES ('capture_job.queued', 'capture_jobs', %s, %s::jsonb)
                    """,
                    (
                        job_id,
                        _json({"job_type": job_type, "root_name": row_root_name, "path": path, "reason": "metadata_only_requeue"}),
                    ),
                )
                jobs.append({"job_id": job_id, "root_name": row_root_name, "path": path, "job_type": job_type})
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
    for stream in ("corpus_lexical", "corpus_fuzzy", "mail_sidecar", "corpus_vector"):
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
                   c.updated_at
            FROM asset_chunks c
            JOIN source_assets a ON a.id = c.asset_id
            JOIN monitored_roots r ON r.id = a.root_id
            JOIN embeddings emb ON emb.owner_table = 'asset_chunks'
                               AND emb.owner_id = c.id
                               AND emb.model = %s
            WHERE r.enabled
              AND a.deleted_at IS NULL
              AND a.canonical_asset_id IS NULL
              AND a.extraction_status = 'indexed'
              {root_sql}
            ORDER BY r.name, c.updated_at DESC, c.id
            LIMIT %s
            """,
            (DEFAULT_EMBEDDING_MODEL, *params),
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
                   e.updated_at
            FROM episodes e
            JOIN embeddings emb ON emb.owner_table = 'episodes'
                               AND emb.owner_id = e.id
                               AND emb.model = %s
            WHERE e.superseded_by IS NULL
              {root_sql}
            ORDER BY workspace_key, e.updated_at DESC, e.id
            LIMIT %s
            """,
            (DEFAULT_EMBEDDING_MODEL, *params),
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
                   c.updated_at
            FROM claims c
            LEFT JOIN entities e ON e.id = c.subject_entity_id
            JOIN embeddings emb ON emb.owner_table = 'claims'
                               AND emb.owner_id = c.id
                               AND emb.model = %s
            WHERE c.lifecycle_state IN ('active', 'confirmed', 'reinforced')
              AND c.retention_action = 'keep'
              {root_sql}
            ORDER BY workspace_key, c.updated_at DESC, c.id
            LIMIT %s
            """,
            (DEFAULT_EMBEDDING_MODEL, *params),
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
        }
        for row in rows
    }


def _fetch_semantic_duplicate_pairs(
    cur: Any,
    *,
    memory_class: str,
    root_name: str | None,
    threshold: float,
    limit: int,
) -> list[tuple[str, str, float]]:
    cte_sql, params = _semantic_duplicate_candidate_cte(memory_class=memory_class, root_name=root_name, limit=limit)
    cur.execute(
        f"""
        WITH candidates AS (
            {cte_sql}
        )
        SELECT a.owner_id::text, b.owner_id::text,
               1 - (a.embedding <=> b.embedding) AS similarity
        FROM candidates a
        JOIN candidates b
          ON a.workspace_key = b.workspace_key
         AND a.owner_id < b.owner_id
        WHERE 1 - (a.embedding <=> b.embedding) >= %s
        ORDER BY similarity DESC, a.owner_id::text, b.owner_id::text
        """,
        (*params, threshold),
    )
    return [(str(row[0]), str(row[1]), float(row[2] or 0.0)) for row in cur.fetchall()]


def _semantic_duplicate_candidate_cte(
    *,
    memory_class: str,
    root_name: str | None,
    limit: int,
) -> tuple[str, tuple[Any, ...]]:
    if memory_class == "corpus":
        root_sql = "AND r.name = %s" if root_name else ""
        params: tuple[Any, ...] = ((root_name,) if root_name else ()) + (limit,)
        return (
            f"""
            SELECT c.id AS owner_id, 'root:' || r.name AS workspace_key, emb.embedding
            FROM asset_chunks c
            JOIN source_assets a ON a.id = c.asset_id
            JOIN monitored_roots r ON r.id = a.root_id
            JOIN embeddings emb ON emb.owner_table = 'asset_chunks'
                               AND emb.owner_id = c.id
                               AND emb.model = %s
            WHERE r.enabled
              AND a.deleted_at IS NULL
              AND a.canonical_asset_id IS NULL
              {root_sql}
            ORDER BY r.name, c.updated_at DESC, c.id
            LIMIT %s
            """,
            (DEFAULT_EMBEDDING_MODEL, *params),
        )
    if memory_class == "episode":
        root_sql = "AND e.metadata->>'root_name' = %s" if root_name else ""
        params = ((root_name,) if root_name else ()) + (limit,)
        return (
            f"""
            SELECT e.id AS owner_id,
                   COALESCE(
                     NULLIF(e.metadata->>'workspace_key', ''),
                     CASE
                       WHEN NULLIF(e.metadata->>'root_name', '') IS NOT NULL
                       THEN 'root:' || (e.metadata->>'root_name')
                       ELSE ''
                     END
                   ) AS workspace_key,
                   emb.embedding
            FROM episodes e
            JOIN embeddings emb ON emb.owner_table = 'episodes'
                               AND emb.owner_id = e.id
                               AND emb.model = %s
            WHERE e.superseded_by IS NULL
              {root_sql}
            ORDER BY workspace_key, e.updated_at DESC, e.id
            LIMIT %s
            """,
            (DEFAULT_EMBEDDING_MODEL, *params),
        )
    root_sql = "AND c.metadata->>'root_name' = %s" if root_name else ""
    params = ((root_name,) if root_name else ()) + (limit,)
    return (
        f"""
        SELECT c.id AS owner_id,
               COALESCE(
                 NULLIF(c.metadata->>'workspace_key', ''),
                 CASE
                   WHEN NULLIF(c.metadata->>'root_name', '') IS NOT NULL
                   THEN 'root:' || (c.metadata->>'root_name')
                   ELSE ''
                 END
               ) AS workspace_key,
               emb.embedding
        FROM claims c
        JOIN embeddings emb ON emb.owner_table = 'claims'
                           AND emb.owner_id = c.id
                           AND emb.model = %s
        WHERE c.lifecycle_state IN ('active', 'confirmed', 'reinforced')
          AND c.retention_action = 'keep'
          {root_sql}
        ORDER BY workspace_key, c.updated_at DESC, c.id
        LIMIT %s
        """,
        (DEFAULT_EMBEDDING_MODEL, *params),
    )


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


def _backfill_claim_embeddings(cur: Any, *, limit: int) -> int:
    cur.execute(
        """
        SELECT c.id::text, e.name, c.predicate, c.object_text
        FROM claims c
        LEFT JOIN entities e ON e.id = c.subject_entity_id
        WHERE NOT EXISTS (
            SELECT 1
            FROM embeddings emb
            WHERE emb.owner_table = 'claims'
              AND emb.owner_id = c.id
              AND emb.model = %s
        )
        ORDER BY c.updated_at DESC, c.id
        LIMIT %s
        """,
        (DEFAULT_EMBEDDING_MODEL, limit),
    )
    rows = cur.fetchall()
    for claim_id, subject_name, predicate, object_text in rows:
        text = f"{subject_name or ''}\n{predicate or ''}\n{object_text or ''}"
        _insert_embedding_result(
            cur,
            _embedding_result_for_text(
                owner_table="claims",
                owner_id=claim_id,
                text=text,
            ),
        )
    return len(rows)


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
    elif str(status).startswith("blocked") or status in {"failed", "retrying_locked"}:
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
            count(*) FILTER (WHERE status = 'running') AS running,
            count(*) FILTER (WHERE status LIKE 'blocked%%') AS blocked,
            count(*) FILTER (WHERE status = 'blocked_locked') AS blocked_locked,
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
    keys = ["pending", "retrying_locked", "running", "blocked", "blocked_locked", "failed", "completed", "duplicate_suppressed"]
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
    cur.execute(
        """
        DELETE FROM embeddings
        WHERE owner_table = 'asset_chunks'
          AND owner_id IN (
              SELECT id
              FROM asset_chunks
              WHERE asset_id = ANY(%s::uuid[])
          )
        """,
        (asset_ids,),
    )
    cur.execute("DELETE FROM asset_chunks WHERE asset_id = ANY(%s::uuid[])", (asset_ids,))
    cur.execute(
        """
        UPDATE source_assets
        SET extraction_status = 'blocked_missing_dependency',
            metadata = metadata || %s::jsonb,
            updated_at = now()
        WHERE id = ANY(%s::uuid[])
        """,
        (
            _json(
                {
                    "strict_indexing": True,
                    "metadata_only_blocked": True,
                    "readiness_status": "blocked_missing_dependency",
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
    cur.execute(
        """
        DELETE FROM embeddings
        WHERE owner_table = 'asset_chunks'
          AND owner_id IN (SELECT id FROM asset_chunks WHERE asset_id = %s)
        """,
        (asset_id,),
    )
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
        _insert_embedding_result(
            cur,
            _embedding_result_for_text(
                owner_table="asset_chunks",
                owner_id=chunk_id,
                text=f"{chunk_title}\n{chunk_body}",
            ),
        )
        inserted += 1
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
            cur.execute(
                """
                DELETE FROM embeddings
                WHERE owner_table = 'asset_chunks'
                  AND owner_id = %s
                """,
                (previous[0],),
            )
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
        _insert_embedding_result(
            cur,
            _embedding_result_for_text(
                owner_table="asset_chunks",
                owner_id=chunk_id,
                text=f"{chunk_title}\n{chunk_body}",
            ),
        )
        inserted += 1
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


def _normalize_embedding_owner_class(owner_class: str | None) -> str:
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


def enqueue_embedding_jobs(
    *,
    owner_class: str = "all",
    root_name: str | None = None,
    stale_only: bool = True,
    limit: int = 100,
    url: str | None = None,
) -> dict[str, Any]:
    normalized_class = _normalize_embedding_owner_class(owner_class)
    row_limit = max(1, min(int(limit or 100), 5000))
    payload = {
        "owner_class": normalized_class,
        "root_name": root_name,
        "stale_only": bool(stale_only),
        "limit": row_limit,
    }
    schedule = _job_schedule_metadata("corpus_embed")
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO capture_jobs (
                    job_type, payload, job_family, resource_class, priority, time_budget_seconds
                )
                VALUES (%s, %s::jsonb, %s, %s, %s, %s)
                RETURNING id::text
                """,
                (
                    "corpus_embed",
                    _json(payload),
                    schedule["job_family"],
                    schedule["resource_class"],
                    schedule["priority"],
                    schedule["time_budget_seconds"],
                ),
            )
            job_id = cur.fetchone()[0]
    return {
        "queued": 1,
        "job_id": job_id,
        **payload,
    }


def refresh_embeddings(
    *,
    owner_class: str = "all",
    root_name: str | None = None,
    stale_only: bool = True,
    limit: int = 100,
    provider: HashEmbeddingProvider | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    normalized_class = _normalize_embedding_owner_class(owner_class)
    row_limit = max(1, min(int(limit or 100), 5000))
    embedding_provider = provider or HashEmbeddingProvider()
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            inputs = _fetch_embedding_inputs(
                cur,
                owner_class=normalized_class,
                root_name=root_name,
                stale_only=bool(stale_only),
                limit=row_limit,
            )
            results = embedding_provider.embed_batch(inputs)
            replaced = _replace_embeddings(cur, results)
    cache_keys = [str(item.metadata.get("cache_key") or "") for item in results]
    unique_cache_keys = {item for item in cache_keys if item}
    cache_hits = max(0, len(cache_keys) - len(unique_cache_keys))
    cache_misses = len(unique_cache_keys)
    dimensions = results[0].dimensions if results else DEFAULT_EMBEDDING_DIMENSIONS
    model = results[0].model if results else DEFAULT_EMBEDDING_MODEL
    return {
        "owner_class": normalized_class,
        "root_name": root_name,
        "stale_only": bool(stale_only),
        "limit": row_limit,
        "requested": len(inputs),
        "vectors": replaced,
        "skipped_unchanged": 0,
        "batches": 1 if inputs else 0,
        "cache_hits": cache_hits,
        "cache_misses": cache_misses,
        "provider": embedding_provider.name,
        "model": model,
        "dimensions": dimensions,
    }


def embedding_status(*, root_name: str | None = None, url: str | None = None) -> dict[str, Any]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            corpus_filter = "AND r.name = %s" if root_name else ""
            corpus_params: tuple[Any, ...] = (root_name,) if root_name else ()
            cur.execute(
                f"""
                SELECT count(*)::integer,
                       count(*) FILTER (WHERE emb.id IS NULL)::integer,
                       count(*) FILTER (WHERE emb.id IS NOT NULL AND NOT (emb.metadata ? 'source_hash'))::integer
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                LEFT JOIN embeddings emb ON emb.owner_table = 'asset_chunks'
                                        AND emb.owner_id = c.id
                                        AND emb.model = %s
                WHERE a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND a.extraction_status = 'indexed'
                  {corpus_filter}
                """,
                (DEFAULT_EMBEDDING_MODEL, *corpus_params),
            )
            corpus = cur.fetchone()
            memory_root_filter = "AND item.metadata->>'root_name' = %s" if root_name else ""
            memory_params: tuple[Any, ...] = (root_name,) if root_name else ()
            cur.execute(
                f"""
                SELECT count(*)::integer,
                       count(*) FILTER (WHERE emb.id IS NULL)::integer,
                       count(*) FILTER (WHERE emb.id IS NOT NULL AND NOT (emb.metadata ? 'source_hash'))::integer
                FROM episodes item
                LEFT JOIN embeddings emb ON emb.owner_table = 'episodes'
                                        AND emb.owner_id = item.id
                                        AND emb.model = %s
                WHERE item.superseded_by IS NULL
                  {memory_root_filter}
                """,
                (DEFAULT_EMBEDDING_MODEL, *memory_params),
            )
            episodes = cur.fetchone()
            cur.execute(
                f"""
                SELECT count(*)::integer,
                       count(*) FILTER (WHERE emb.id IS NULL)::integer,
                       count(*) FILTER (WHERE emb.id IS NOT NULL AND NOT (emb.metadata ? 'source_hash'))::integer
                FROM claims item
                LEFT JOIN embeddings emb ON emb.owner_table = 'claims'
                                        AND emb.owner_id = item.id
                                        AND emb.model = %s
                WHERE item.retention_action = 'keep'
                  {memory_root_filter}
                """,
                (DEFAULT_EMBEDDING_MODEL, *memory_params),
            )
            claims = cur.fetchone()
    rows = {
        "corpus": {"total": int(corpus[0] or 0), "missing": int(corpus[1] or 0), "metadata_missing": int(corpus[2] or 0)},
        "episodes": {"total": int(episodes[0] or 0), "missing": int(episodes[1] or 0), "metadata_missing": int(episodes[2] or 0)},
        "claims": {"total": int(claims[0] or 0), "missing": int(claims[1] or 0), "metadata_missing": int(claims[2] or 0)},
    }
    return {
        "root_name": root_name,
        "model": DEFAULT_EMBEDDING_MODEL,
        "dimensions": DEFAULT_EMBEDDING_DIMENSIONS,
        "owners": rows,
        "totals": {
            "total": sum(item["total"] for item in rows.values()),
            "missing": sum(item["missing"] for item in rows.values()),
            "metadata_missing": sum(item["metadata_missing"] for item in rows.values()),
        },
    }


def _fetch_embedding_inputs(
    cur: Any,
    *,
    owner_class: str,
    root_name: str | None,
    stale_only: bool,
    limit: int,
) -> list[EmbeddingInput]:
    remaining = max(1, min(int(limit or 100), 5000))
    classes = ["corpus", "episodes", "claims"] if owner_class == "all" else [owner_class]
    items: list[EmbeddingInput] = []
    for item_class in classes:
        if remaining <= 0:
            break
        if item_class == "corpus":
            rows = _fetch_corpus_embedding_rows(cur, root_name=root_name, limit=remaining)
            owner_table = "asset_chunks"
        elif item_class == "episodes":
            rows = _fetch_episode_embedding_rows(cur, root_name=root_name, limit=remaining)
            owner_table = "episodes"
        else:
            rows = _fetch_claim_embedding_rows(cur, root_name=root_name, limit=remaining)
            owner_table = "claims"
        for owner_id, text, existing_source_hash in rows:
            source_hash = embedding_source_hash(str(text or ""))
            if stale_only and existing_source_hash == source_hash:
                continue
            items.append(
                EmbeddingInput(
                    owner_table=owner_table,
                    owner_id=str(owner_id),
                    text=str(text or ""),
                    model=DEFAULT_EMBEDDING_MODEL,
                    dimensions=DEFAULT_EMBEDDING_DIMENSIONS,
                    existing_source_hash=existing_source_hash,
                )
            )
            remaining -= 1
            if remaining <= 0:
                break
    return items


def _fetch_corpus_embedding_rows(cur: Any, *, root_name: str | None, limit: int) -> list[tuple[Any, Any, Any]]:
    root_sql = "AND r.name = %s" if root_name else ""
    params: tuple[Any, ...] = ((root_name,) if root_name else ()) + (limit,)
    cur.execute(
        f"""
        SELECT c.id::text, c.title, c.body, c.metadata,
               emb.metadata->>'source_hash' AS existing_source_hash
        FROM asset_chunks c
        JOIN source_assets a ON a.id = c.asset_id
        JOIN monitored_roots r ON r.id = a.root_id
        LEFT JOIN embeddings emb ON emb.owner_table = 'asset_chunks'
                                AND emb.owner_id = c.id
                                AND emb.model = %s
        WHERE a.deleted_at IS NULL
          AND a.canonical_asset_id IS NULL
          AND a.extraction_status = 'indexed'
          {root_sql}
        ORDER BY c.updated_at DESC, c.id
        LIMIT %s
        """,
        (DEFAULT_EMBEDDING_MODEL, *params),
    )
    rows: list[tuple[Any, Any, Any]] = []
    for owner_id, title, body, metadata, existing_source_hash in cur.fetchall():
        chunk = {"title": title, "body": body, "metadata": metadata or {}}
        hydrated_body = mail_content_store.hydrate_chunk_body(chunk)
        text = "\n".join(part for part in (str(title or ""), hydrated_body) if part).strip()
        rows.append((owner_id, text, existing_source_hash))
    return rows


def _fetch_episode_embedding_rows(cur: Any, *, root_name: str | None, limit: int) -> list[tuple[Any, Any, Any]]:
    root_sql = "AND e.metadata->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...] = ((root_name,) if root_name else ()) + (limit,)
    cur.execute(
        f"""
        SELECT e.id::text, concat_ws(E'\n', e.title, e.summary) AS text,
               emb.metadata->>'source_hash' AS existing_source_hash
        FROM episodes e
        LEFT JOIN embeddings emb ON emb.owner_table = 'episodes'
                                AND emb.owner_id = e.id
                                AND emb.model = %s
        WHERE e.superseded_by IS NULL
          {root_sql}
        ORDER BY e.updated_at DESC, e.id
        LIMIT %s
        """,
        (DEFAULT_EMBEDDING_MODEL, *params),
    )
    return list(cur.fetchall())


def _fetch_claim_embedding_rows(cur: Any, *, root_name: str | None, limit: int) -> list[tuple[Any, Any, Any]]:
    root_sql = "AND c.metadata->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...] = ((root_name,) if root_name else ()) + (limit,)
    cur.execute(
        f"""
        SELECT c.id::text, concat_ws(' ', e.name, c.predicate, c.object_text) AS text,
               emb.metadata->>'source_hash' AS existing_source_hash
        FROM claims c
        LEFT JOIN entities e ON e.id = c.subject_entity_id
        LEFT JOIN embeddings emb ON emb.owner_table = 'claims'
                                AND emb.owner_id = c.id
                                AND emb.model = %s
        WHERE c.retention_action = 'keep'
          {root_sql}
        ORDER BY c.updated_at DESC, c.id
        LIMIT %s
        """,
        (DEFAULT_EMBEDDING_MODEL, *params),
    )
    return list(cur.fetchall())


def _replace_embeddings(cur: Any, results: list[EmbeddingResult] | tuple[EmbeddingResult, ...]) -> int:
    replaced = 0
    for result in results:
        cur.execute(
            """
            DELETE FROM embeddings
            WHERE owner_table = %s
              AND owner_id = %s
              AND model = %s
            """,
            (result.owner_table, result.owner_id, result.model),
        )
        _insert_embedding_result(cur, result)
        replaced += 1
    return replaced


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
              AND status IN ('pending', 'retrying_locked', 'running')
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
            RETURNING job.id::text, candidates.status AS previous_status
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
        "enqueue_embedding_refresh",
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
