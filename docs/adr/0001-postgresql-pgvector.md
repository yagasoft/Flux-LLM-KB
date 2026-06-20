# ADR 0001: PostgreSQL And pgvector As Primary Store

## Status

Accepted.

## Context

The project needs a free, reputable, local-first store that supports semantic
retrieval, structured metadata, lifecycle state, and graph-like traversal.

## Decision

Use PostgreSQL with pgvector as the primary persistence backend.

## Consequences

- Semantic retrieval can run beside relational state.
- JSONB, GIN, full-text search, and `pg_trgm` support hybrid search.
- The project remains portable to other machines and agent systems.
- Docker Compose is the preferred runtime profile, but setup must detect missing
  Docker and fail clearly.

