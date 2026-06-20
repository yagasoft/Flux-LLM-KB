# Roadmap

## V0: Foundation

- Public GitHub repo with safety docs and architecture records.
- PostgreSQL + pgvector schema migrations.
- Docker Compose runtime profile with explicit Docker prerequisite checks.
- Synthetic fixture corpus for repeatable tests.
- Initial MCP, CLI, and REST skeletons.

## V1: Working Knowledge Kernel

- Hybrid retrieval with lexical, vector, graph, and lifecycle scoring.
- Codex hooks for automatic preflight retrieval and session capture.
- Redaction and audit trail before any persistence.
- Markdown wiki export for human auditability.
- Disposable PostgreSQL integration tests.

## V2: Review And Visualization

- Dashboard for search, graph browsing, stale claims, contradictions, and capture review.
- Retention policy tuning and memory quality reports.
- Manual approval flows for sensitive or low-confidence captures.

## V3: Scale And Evaluation

- Historical Codex backfill with redaction.
- Retrieval benchmark suite.
- Optional ParadeDB/BM25 path.
- Local-LLM librarian workers for consolidation and linting.

## V4: Collaboration And Transfer

- Team/shared vault mode.
- Sync and export policies.
- Optional Apache AGE graph backend.
- Synthetic-data and fine-tuning pipeline.

