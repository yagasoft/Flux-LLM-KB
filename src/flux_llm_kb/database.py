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
    query_vector = to_pgvector_literal(embed_text(query))

    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            streams: dict[str, list[str]] = {}
            details: dict[str, dict[str, Any]] = {}

            cur.execute(
                """
                SELECT c.id::text, c.title, c.body, a.path,
                       (
                           SELECT count(*)
                           FROM source_assets duplicate
                           WHERE duplicate.canonical_asset_id = a.id
                       ) AS duplicate_count,
                       r.trust_rank,
                       ts_rank(c.search_vector, plainto_tsquery('english', %s)) AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
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
                       (
                           SELECT count(*)
                           FROM source_assets duplicate
                           WHERE duplicate.canonical_asset_id = a.id
                       ) AS duplicate_count,
                       r.trust_rank,
                       greatest(similarity(c.title, %s), similarity(c.body, %s)) AS score
                FROM asset_chunks c
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND (similarity(c.title, %s) > 0.10 OR similarity(c.body, %s) > 0.15)
                ORDER BY score DESC, c.updated_at DESC
                LIMIT %s
                """,
                (query, query, query, query, candidate_limit),
            )
            _add_ranked_corpus_rows("corpus_fuzzy", cur.fetchall(), streams, details)

            cur.execute(
                """
                SELECT c.id::text, c.title, c.body, a.path,
                       (
                           SELECT count(*)
                           FROM source_assets duplicate
                           WHERE duplicate.canonical_asset_id = a.id
                       ) AS duplicate_count,
                       r.trust_rank,
                       1 - (emb.embedding <=> %s::vector) AS score
                FROM embeddings emb
                JOIN asset_chunks c
                  ON emb.owner_table = 'asset_chunks'
                 AND emb.owner_id = c.id
                JOIN source_assets a ON a.id = c.asset_id
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.enabled
                  AND a.deleted_at IS NULL
                  AND a.canonical_asset_id IS NULL
                  AND emb.model = %s
                  AND 1 - (emb.embedding <=> %s::vector) > 0.25
                ORDER BY emb.embedding <=> %s::vector
                LIMIT %s
                """,
                (query_vector, DEFAULT_EMBEDDING_MODEL, query_vector, query_vector, candidate_limit),
            )
            _add_ranked_corpus_rows("corpus_vector", cur.fetchall(), streams, details)

            if details:
                cur.execute(
                    """
                    SELECT c.id::text, c.title, c.body, a.path,
                           (
                               SELECT count(*)
                               FROM source_assets duplicate
                               WHERE duplicate.canonical_asset_id = a.id
                           ) AS duplicate_count,
                           r.trust_rank,
                           1.0 / greatest(r.trust_rank, 1) AS score
                    FROM asset_chunks c
                    JOIN source_assets a ON a.id = c.asset_id
                    JOIN monitored_roots r ON r.id = a.root_id
                    WHERE c.id = ANY(%s::uuid[])
                    ORDER BY r.trust_rank ASC, c.updated_at DESC
                    """,
                    (list(details.keys()),),
                )
                _add_ranked_corpus_rows("corpus_trust", cur.fetchall(), streams, details)

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
                "duplicate_count": result["duplicate_count"],
                "trust_rank": result["trust_rank"],
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
                if canonical_id:
                    status = "duplicate_suppressed"
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
                            WHEN source_assets.quick_hash IS DISTINCT FROM EXCLUDED.quick_hash
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
                            ELSE source_assets.indexed_at
                        END,
                        deleted_at = NULL,
                        metadata = source_assets.metadata || EXCLUDED.metadata,
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
                    ),
                )
                asset_id = cur.fetchone()[0]
                if changed_asset:
                    chunks_indexed += _replace_asset_chunks(cur, asset_id, () if canonical_id else asset.chunks)
                    if asset.extraction_tier == "deferred" and not canonical_id:
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
                    elif asset.extraction_tier == "deferred" and canonical_id:
                        _cancel_duplicate_corpus_job_for_asset(cur, root_name=root_name, relative_path=asset.relative_path)

            _mark_deleted_assets(cur, root_id, seen_paths, plan)
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
                    jobs_queued = %s,
                    errors = %s::jsonb
                WHERE id = %s
                """,
                (len(plan.assets), changed, deleted, chunks_indexed, jobs_queued, _json_list(plan.errors), run_id),
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


def claim_corpus_jobs(
    *,
    limit: int = 1,
    worker_id: str = "flux-kb-worker",
    root_name: str | None = None,
    url: str | None = None,
) -> list[dict[str, Any]]:
    psycopg = _load_psycopg()
    root_filter = "AND payload->>'root_name' = %s" if root_name else ""
    params: tuple[Any, ...]
    if root_name:
        params = (worker_id, root_name, max(1, min(limit, 100)))
    else:
        params = (worker_id, max(1, min(limit, 100)))
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                f"""
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
                      {root_filter}
                    ORDER BY created_at
                    LIMIT %s
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING id::text, job_type, status, payload, attempts, last_error
                """,
                params,
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
                    last_error = NULL,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (job_id,),
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


def block_corpus_job(
    *, job_id: str, error: str, status: str = "blocked_missing_dependency", url: str | None = None
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE capture_jobs
                SET status = %s,
                    last_error = %s,
                    locked_at = NULL,
                    locked_by = NULL,
                    updated_at = now()
                WHERE id = %s
                  AND job_type LIKE 'corpus_%%'
                """,
                (status, error, job_id),
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
            return {"root_name": root_name, "repaired": int(cur.fetchone()[0] or 0)}


def apply_extraction_result(
    *, root_name: str, relative_path: str, result: Any, url: str | None = None
) -> None:
    psycopg = _load_psycopg()
    with psycopg.connect(url or database_url()) as conn:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT a.id::text, a.canonical_asset_id::text
                FROM source_assets a
                JOIN monitored_roots r ON r.id = a.root_id
                WHERE r.name = %s
                  AND a.path = %s
                """,
                (root_name, relative_path),
            )
            row = cur.fetchone()
            if not row:
                raise ValueError(f"source asset not found: {root_name}:{relative_path}")
            asset_id, canonical_asset_id = row
            status = result.status
            if canonical_asset_id and status == "indexed":
                status = "duplicate_suppressed"
            cur.execute(
                """
                UPDATE source_assets
                SET extraction_status = %s,
                    metadata = metadata || %s::jsonb,
                    indexed_at = CASE WHEN %s = 'indexed' THEN now() ELSE indexed_at END,
                    updated_at = now()
                WHERE id = %s
                """,
                (status, _json(result.metadata or {}), status, asset_id),
            )
            _replace_asset_chunks(cur, asset_id, () if canonical_asset_id else result.chunks)


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
            profiles = list_mail_profiles(url=url)
            return {
                "enabled_profiles": enabled_profiles,
                "exported_messages": exported_messages,
                "errored_messages": errored_messages,
                "profiles": profiles,
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
                WITH request AS (
                    SELECT r.id
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
                JOIN mail_profiles p ON p.id = r.profile_id
                WHERE r.id = request.id
                RETURNING r.id::text, p.name, r.status
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
            {
                "title": row[1],
                "summary": row[2],
                "source_path": row[3],
                "duplicate_count": int(row[4] or 0),
                "trust_rank": int(row[5] or 500),
                "raw_scores": {},
            },
        )
        detail["raw_scores"][stream] = float(row[6] or 0.0)


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


def _latest_crawl_run(cur: Any, root_id: str) -> dict[str, Any] | None:
    cur.execute(
        """
        SELECT id::text, status, started_at, finished_at, files_seen, files_changed,
               files_deleted, chunks_indexed, jobs_queued, errors
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
    }


def _root_asset_counts(cur: Any, root_id: str) -> dict[str, int]:
    cur.execute(
        """
        SELECT
            count(*) FILTER (WHERE deleted_at IS NULL) AS total,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'indexed') AS indexed,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'queued') AS queued,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'metadata_only') AS metadata_only,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status = 'duplicate_suppressed') AS duplicate_suppressed,
            count(*) FILTER (WHERE deleted_at IS NULL AND extraction_status LIKE 'blocked%%') AS blocked,
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
        "metadata_only",
        "duplicate_suppressed",
        "blocked",
        "failed",
        "deleted",
    ]
    return {key: int(row[index] or 0) for index, key in enumerate(keys)}


def _root_job_counts(cur: Any, root_name: str) -> dict[str, int]:
    cur.execute(
        """
        SELECT
            count(*) FILTER (WHERE status = 'pending') AS pending,
            count(*) FILTER (WHERE status = 'running') AS running,
            count(*) FILTER (WHERE status = 'blocked_missing_dependency') AS blocked,
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
    keys = ["pending", "running", "blocked", "failed", "completed", "duplicate_suppressed"]
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
    try:
        from .host_agent import path_requires_host_agent

        if path_requires_host_agent(value):
            return value
    except Exception:
        pass
    return str(Path(value).expanduser().resolve())


def _replace_asset_chunks(cur: Any, asset_id: str, chunks: tuple[Any, ...]) -> int:
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
        cur.execute(
            """
            INSERT INTO asset_chunks (
                asset_id, chunk_index, title, body, modality, locator, token_estimate
            )
            VALUES (%s, %s, %s, %s, %s, %s, %s)
            RETURNING id::text
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
        chunk_id = cur.fetchone()[0]
        cur.execute(
            """
            INSERT INTO embeddings (owner_table, owner_id, model, dimensions, embedding)
            VALUES ('asset_chunks', %s, %s, %s, %s::vector)
            """,
            (
                chunk_id,
                DEFAULT_EMBEDDING_MODEL,
                DEFAULT_EMBEDDING_DIMENSIONS,
                to_pgvector_literal(embed_text(f"{chunk.title}\n{chunk.body}")),
            ),
        )
        inserted += 1
    return inserted


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


def _mark_deleted_assets(cur: Any, root_id: str, seen_paths: set[str], plan: CrawlPlan) -> None:
    if plan.scope_relative_path is None:
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
        return

    if plan.scope_is_file:
        cur.execute(
            """
            UPDATE source_assets
            SET deleted_at = now(), updated_at = now()
            WHERE root_id = %s
              AND deleted_at IS NULL
              AND path = %s
              AND NOT (path = ANY(%s))
            """,
            (root_id, plan.scope_relative_path, list(seen_paths) or [""]),
        )
        return

    prefix = f"{plan.scope_relative_path}/"
    cur.execute(
        """
        UPDATE source_assets
        SET deleted_at = now(), updated_at = now()
        WHERE root_id = %s
          AND deleted_at IS NULL
          AND (path = %s OR path LIKE %s)
          AND NOT (path = ANY(%s))
        """,
        (root_id, plan.scope_relative_path, f"{prefix}%", list(seen_paths) or [""]),
    )


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


def _json_any(value: Any) -> str:
    import json

    return json.dumps(value, sort_keys=True)


def _json_list(value: list[dict[str, Any]]) -> str:
    import json

    return json.dumps(value, sort_keys=True)
