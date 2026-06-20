from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
from typing import Any
from uuid import UUID

from .crawler import CrawlPlan
from .embeddings import (
    DEFAULT_EMBEDDING_DIMENSIONS,
    DEFAULT_EMBEDDING_MODEL,
    embed_text,
    to_pgvector_literal,
)
from .migrations import load_migrations
from .scoring import reciprocal_rank_fusion


DEFAULT_DATABASE_URL = "postgresql://flux:flux@localhost:5432/flux_llm_kb"


@dataclass(frozen=True)
class DatabaseStatus:
    ok: bool
    message: str


def database_url() -> str:
    return os.environ.get("FLUX_KB_DATABASE_URL", DEFAULT_DATABASE_URL)


def run_migrations(url: str | None = None) -> list[str]:
    psycopg = _load_psycopg()
    applied: list[str] = []
    with psycopg.connect(url or database_url(), autocommit=True) as conn:
        with conn.cursor() as cur:
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
                    "INSERT INTO schema_migrations (version, name) VALUES (%s, %s)",
                    (migration.version, migration.name),
                )
                applied.append(migration.name)
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


def insert_episode(
    *,
    title: str,
    summary: str,
    source_kind: str = "manual",
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> str:
    psycopg = _load_psycopg()
    vector = to_pgvector_literal(embed_text(f"{title}\n{summary}"))
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
                INSERT INTO embeddings (owner_table, owner_id, model, dimensions, embedding)
                VALUES ('episodes', %s, %s, %s, %s::vector)
                """,
                (
                    episode_id,
                    DEFAULT_EMBEDDING_MODEL,
                    DEFAULT_EMBEDDING_DIMENSIONS,
                    vector,
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


def search_episodes(query: str, *, limit: int = 5, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    limit = max(1, min(limit, 50))
    candidate_limit = max(limit * 4, 12)
    query_vector = to_pgvector_literal(embed_text(query))

    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            streams: dict[str, list[str]] = {}
            details: dict[str, dict[str, Any]] = {}

            cur.execute(
                """
                SELECT id::text, title, summary,
                       ts_rank(search_vector, plainto_tsquery('english', %s)) AS score
                FROM episodes
                WHERE search_vector @@ plainto_tsquery('english', %s)
                ORDER BY score DESC, updated_at DESC
                LIMIT %s
                """,
                (query, query, candidate_limit),
            )
            _add_ranked_rows("lexical", cur.fetchall(), streams, details)

            cur.execute(
                """
                SELECT id::text, title, summary,
                       greatest(similarity(title, %s), similarity(summary, %s)) AS score
                FROM episodes
                WHERE similarity(title, %s) > 0.10 OR similarity(summary, %s) > 0.05
                ORDER BY score DESC, updated_at DESC
                LIMIT %s
                """,
                (query, query, query, query, candidate_limit),
            )
            _add_ranked_rows("fuzzy", cur.fetchall(), streams, details)

            cur.execute(
                """
                SELECT e.id::text, e.title, e.summary,
                       1 - (emb.embedding <=> %s::vector) AS score
                FROM embeddings emb
                JOIN episodes e
                  ON emb.owner_table = 'episodes'
                 AND emb.owner_id = e.id
                WHERE emb.model = %s
                ORDER BY emb.embedding <=> %s::vector
                LIMIT %s
                """,
                (query_vector, DEFAULT_EMBEDDING_MODEL, query_vector, candidate_limit),
            )
            _add_ranked_rows("vector", cur.fetchall(), streams, details)

    fused = reciprocal_rank_fusion(streams)
    results: list[dict[str, Any]] = []
    for item in fused[:limit]:
        result = details[item.item_id]
        results.append(
            {
                "id": item.item_id,
                "title": result["title"],
                "summary": result["summary"],
                "score": item.score,
                "streams": list(item.streams),
                "raw_scores": result["raw_scores"],
            }
        )
    return results


def search_corpus_chunks(query: str, *, limit: int = 5, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    limit = max(1, min(limit, 50))
    candidate_limit = max(limit * 4, 12)

    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            streams: dict[str, list[str]] = {}
            details: dict[str, dict[str, Any]] = {}

            cur.execute(
                """
                SELECT c.id::text, c.title, c.body, a.path,
                       ts_rank(c.search_vector, plainto_tsquery('english', %s)) AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND c.search_vector @@ plainto_tsquery('english', %s)
                ORDER BY score DESC, c.updated_at DESC
                LIMIT %s
                """,
                (query, query, candidate_limit),
            )
            _add_ranked_corpus_rows("corpus_lexical", cur.fetchall(), streams, details)

            cur.execute(
                """
                SELECT c.id::text, c.title, c.body, a.path,
                       greatest(similarity(c.title, %s), similarity(c.body, %s)) AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND (similarity(c.title, %s) > 0.10 OR similarity(c.body, %s) > 0.05)
                ORDER BY score DESC, c.updated_at DESC
                LIMIT %s
                """,
                (query, query, query, query, candidate_limit),
            )
            _add_ranked_corpus_rows("corpus_fuzzy", cur.fetchall(), streams, details)

    fused = reciprocal_rank_fusion(streams)
    results: list[dict[str, Any]] = []
    for item in fused[:limit]:
        result = details[item.item_id]
        results.append(
            {
                "id": item.item_id,
                "title": result["title"],
                "summary": result["summary"],
                "score": item.score,
                "streams": list(item.streams),
                "raw_scores": result["raw_scores"],
                "source_path": result["source_path"],
            }
        )
    return results


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


def record_audit_event(
    *,
    event_type: str,
    target_table: str | None = None,
    target_id: str | None = None,
    details: dict[str, Any] | None = None,
    url: str | None = None,
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO audit_events (event_type, target_table, target_id, details)
                VALUES (%s, %s, %s, %s::jsonb)
                """,
                (event_type, target_table, target_id, _json(details or {})),
            )


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
    max_inline_bytes: int = 256 * 1024,
    heavy_threshold_bytes: int = 10 * 1024 * 1024,
    metadata: dict[str, Any] | None = None,
    url: str | None = None,
) -> dict[str, Any]:
    psycopg = _load_psycopg()
    resolved_path = str(Path(root_path).expanduser().resolve())
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO monitored_roots (
                    name, root_path, enabled, recursive, watch_enabled, trust_rank,
                    include_globs, exclude_globs, max_inline_bytes, heavy_threshold_bytes, metadata
                )
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s::jsonb)
                ON CONFLICT (name) DO UPDATE SET
                    root_path = EXCLUDED.root_path,
                    enabled = EXCLUDED.enabled,
                    recursive = EXCLUDED.recursive,
                    watch_enabled = EXCLUDED.watch_enabled,
                    trust_rank = EXCLUDED.trust_rank,
                    include_globs = EXCLUDED.include_globs,
                    exclude_globs = EXCLUDED.exclude_globs,
                    max_inline_bytes = EXCLUDED.max_inline_bytes,
                    heavy_threshold_bytes = EXCLUDED.heavy_threshold_bytes,
                    metadata = EXCLUDED.metadata,
                    updated_at = now()
                RETURNING id::text, name, root_path, enabled, recursive, watch_enabled,
                          trust_rank, include_globs, exclude_globs, max_inline_bytes,
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
                    max_inline_bytes,
                    heavy_threshold_bytes,
                    _json(metadata or {}),
                ),
            )
            row = cur.fetchone()
            _upsert_watcher_state(cur, row[0], "enabled" if watch_enabled else "stopped")
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
                       trust_rank, include_globs, exclude_globs, max_inline_bytes,
                       heavy_threshold_bytes, metadata
                FROM monitored_roots
                {where}
                ORDER BY name
                """,
                params,
            )
            return [_root_row(row) for row in cur.fetchall()]


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
                _upsert_watcher_state(cur, root_id, "enabled" if enabled else "stopped")
            return {"updated": len(root_ids), "root_name": root_name, "watch_enabled": enabled}


def record_watcher_heartbeat(*, root_name: str, url: str | None = None) -> None:
    _update_watcher_state(root_name=root_name, status="running", heartbeat=True, url=url)


def record_watch_event(*, root_name: str, url: str | None = None) -> None:
    _update_watcher_state(root_name=root_name, status="running", event=True, url=url)


def record_watch_error(*, root_name: str, error: str, url: str | None = None) -> None:
    _update_watcher_state(root_name=root_name, status="error", error=error, url=url)


def persist_crawl_plan(
    *, root_name: str, plan: CrawlPlan, dry_run: bool = False, url: str | None = None
) -> dict[str, Any]:
    if dry_run:
        return {
            "root_name": root_name,
            "root_path": str(plan.root_path),
            "dry_run": True,
            "files_seen": len(plan.assets),
            "jobs_queued": len(plan.deferred_jobs),
            "chunks_indexed": sum(len(asset.chunks) for asset in plan.assets),
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
                "INSERT INTO crawl_runs (root_id, status) VALUES (%s, 'running') RETURNING id::text",
                (root_id,),
            )
            run_id = cur.fetchone()[0]
            seen_paths: set[str] = set()
            chunks_indexed = 0
            jobs_queued = 0
            changed = 0
            for asset in plan.assets:
                seen_paths.add(asset.relative_path)
                cur.execute(
                    "SELECT id::text, quick_hash FROM source_assets WHERE root_id = %s AND path = %s",
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
                canonical_id = _find_canonical_asset_id(cur, asset.content_hash, previous[0] if previous else None)
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
                        indexed_at = EXCLUDED.indexed_at,
                        deleted_at = NULL,
                        metadata = EXCLUDED.metadata,
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
                        _json({"source": "corpus_crawler"}),
                    ),
                )
                asset_id = cur.fetchone()[0]
                if changed_asset:
                    cur.execute("DELETE FROM asset_chunks WHERE asset_id = %s", (asset_id,))
                    for chunk in asset.chunks:
                        cur.execute(
                            """
                            INSERT INTO asset_chunks (
                                asset_id, chunk_index, title, body, modality, locator, token_estimate
                            )
                            VALUES (%s, %s, %s, %s, %s, %s, %s)
                            """,
                            (
                                asset_id,
                                chunk.chunk_index,
                                chunk.title,
                                chunk.body,
                                chunk.modality,
                                chunk.locator,
                                chunk.token_estimate,
                            ),
                        )
                        chunks_indexed += 1
                    if asset.extraction_tier == "deferred":
                        cur.execute(
                            """
                            INSERT INTO capture_jobs (job_type, payload)
                            VALUES (%s, %s::jsonb)
                            """,
                            (
                                f"corpus_extract_{asset.file_kind}",
                                _json({"root_name": root_name, "path": asset.relative_path}),
                            ),
                        )
                        jobs_queued += 1

            cur.execute(
                """
                UPDATE source_assets
                SET deleted_at = now(), updated_at = now()
                WHERE root_id = %s
                  AND deleted_at IS NULL
                  AND NOT (path = ANY(%s))
                """,
                (root_id, list(seen_paths) or [""]),
            )
            deleted = cur.rowcount
            cur.execute(
                """
                UPDATE crawl_runs
                SET status = 'completed',
                    finished_at = now(),
                    files_seen = %s,
                    files_changed = %s,
                    files_deleted = %s,
                    chunks_indexed = %s,
                    jobs_queued = %s
                WHERE id = %s
                """,
                (len(plan.assets), changed, deleted, chunks_indexed, jobs_queued, run_id),
            )
            return {
                "root_name": root_name,
                "root_path": str(plan.root_path),
                "dry_run": False,
                "files_seen": len(plan.assets),
                "files_changed": changed,
                "files_deleted": deleted,
                "jobs_queued": jobs_queued,
                "chunks_indexed": chunks_indexed,
            }


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
                SELECT r.name, s.status, s.heartbeat_at, s.last_event_at, s.last_error
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
                }
                for row in cur.fetchall()
            ]
            return {
                "active_watch_roots": active_watch_roots,
                "disabled_watch_roots": disabled_watch_roots,
                "pending_jobs": pending_jobs,
                "failed_jobs": failed_jobs,
                "recent_errors": watcher_errors + job_errors,
                "watchers": watchers,
            }


def list_capture_jobs(*, limit: int = 50, url: str | None = None) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT id::text, job_type, status, payload, attempts, last_error, created_at, updated_at
                FROM capture_jobs
                WHERE job_type LIKE 'corpus_%%'
                ORDER BY updated_at DESC
                LIMIT %s
                """,
                (max(1, min(limit, 200)),),
            )
            return [
                {
                    "id": row[0],
                    "job_type": row[1],
                    "status": row[2],
                    "payload": row[3],
                    "attempts": row[4],
                    "last_error": row[5],
                    "created_at": row[6].isoformat(),
                    "updated_at": row[7].isoformat(),
                }
                for row in cur.fetchall()
            ]


def claim_corpus_jobs(
    *, limit: int = 1, worker_id: str = "flux-kb-worker", url: str | None = None
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'running',
                    attempts = attempts + 1,
                    locked_at = now(),
                    locked_by = %s,
                    updated_at = now()
                WHERE id IN (
                    SELECT id
                    FROM capture_jobs
                    WHERE job_type LIKE 'corpus_%%'
                      AND status = 'pending'
                      AND next_attempt_at <= now()
                    ORDER BY created_at
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id::text, job_type, status, payload, attempts, last_error
                """,
                (worker_id, max(1, min(limit, 100))),
            )
            return [
                {
                    "id": row[0],
                    "job_type": row[1],
                    "status": row[2],
                    "payload": row[3],
                    "attempts": row[4],
                    "last_error": row[5],
                }
                for row in cur.fetchall()
            ]


def complete_corpus_job(*, job_id: str, url: str | None = None) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'completed',
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (job_id,),
            )


def retry_corpus_job(
    *, job_id: str, error: str, cooldown_seconds: int = 300, url: str | None = None
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = 'pending',
                    last_error = %s,
                    next_attempt_at = now() + make_interval(secs => %s),
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (error, max(1, cooldown_seconds), job_id),
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
            return counts


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
            {"title": row[1], "summary": row[2], "source_path": row[3], "raw_scores": {}},
        )
        detail["raw_scores"][stream] = float(row[4] or 0.0)


def _root_row(row: tuple[Any, ...]) -> dict[str, Any]:
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
        "max_inline_bytes": row[9],
        "heavy_threshold_bytes": row[10],
        "metadata": row[11],
    }


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
            cur.execute(
                """
                INSERT INTO watcher_state (
                    root_id, status, heartbeat_at, last_event_at, last_error, process_id, updated_at
                )
                VALUES (
                    %s, %s,
                    CASE WHEN %s THEN now() ELSE NULL END,
                    CASE WHEN %s THEN now() ELSE NULL END,
                    %s, %s, now()
                )
                ON CONFLICT (root_id) DO UPDATE SET
                    status = EXCLUDED.status,
                    heartbeat_at = CASE WHEN %s THEN now() ELSE watcher_state.heartbeat_at END,
                    last_event_at = CASE WHEN %s THEN now() ELSE watcher_state.last_event_at END,
                    last_error = EXCLUDED.last_error,
                    process_id = EXCLUDED.process_id,
                    updated_at = now()
                """,
                (root_id, status, heartbeat, event, error, os.getpid(), heartbeat, event),
            )


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


def _json(value: dict[str, Any]) -> str:
    import json

    return json.dumps(value, sort_keys=True)
