# ADR 0001: PostgreSQL As Primary Durable Store

## Status

Accepted.

## Context

The project needs a free, reputable, local-first durable store for structured
metadata, lifecycle state, auditability, job state, and graph-like traversal.
Searchable evidence can use local sidecars when they are better suited to
ranking and model-backed retrieval.

## Decision

Use PostgreSQL as the primary durable persistence backend. Active searchable
evidence is synchronised into local Vespa sidecars through search-index records,
Snowflake embeddings, and Qwen reranking. PostgreSQL remains responsible for
hydration, permissions, lifecycle, graph, retention, audit, and degraded
lexical/title/path lookup.

## Consequences

- Relational state, graph metadata, capture jobs, and audit records stay in one
  dependable local database.
- JSONB, GIN, full-text search, and `pg_trgm` support bounded degraded lookup
  and diagnostics.
- Vespa/model-runner sidecars can evolve independently of durable memory
  storage.
- The project remains portable to other machines and agent systems.
- Docker Compose is the preferred runtime profile, but setup must detect missing
  Docker and fail clearly.
