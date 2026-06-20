from __future__ import annotations

from dataclasses import dataclass
import os
from typing import Any
from uuid import UUID

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


def _load_psycopg():
    try:
        import psycopg
    except ImportError as exc:  # pragma: no cover - depends on environment
        raise RuntimeError("Install PostgreSQL dependencies with `pip install -e .`") from exc
    return psycopg


def _json(value: dict[str, Any]) -> str:
    import json

    return json.dumps(value, sort_keys=True)
