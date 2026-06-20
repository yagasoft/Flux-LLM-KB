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

- Unified dashboard for health, monitoring, watcher status, crawler stats, search,
  graph browsing, stale claims, contradictions, and capture review.
- Retention policy tuning and memory quality reports.
- Manual approval flows for sensitive or low-confidence captures.

## V2.5: Autonomous Corpus Expansion

- Configurable recursive path monitoring with persistent watch enable/disable state.
- Live watcher control with reloadable enabled roots, debounce, bounded queues,
  heartbeat, and stale-state reporting.
- Targeted file/subtree sync for efficient watcher-triggered updates.
- File-type aware extraction:
  - text/code/markdown/json/csv: fast local extraction and chunking
  - office/PDF/spreadsheets/slides: local library extraction where practical; large
    tabular files can be metadata-first
  - images: metadata, dimensions, hash, optional local OCR in deferred jobs
  - audio/video: metadata via local probing, sidecar transcript reuse, optional
    local transcription/frame sampling in deferred jobs
  - archives/binaries: metadata-only unless explicitly enabled later
- Background processing with low-priority bounded workers, `FOR UPDATE SKIP LOCKED`
  job claiming, retry/cooldown tracking, and no cloud/provider calls by default.
- Duplicate suppression by content hash while preserving all observed paths and
  source metadata.
- Corpus retrieval combines full-text, fuzzy, pgvector chunk embeddings, trust rank,
  freshness, deletion state, and canonical duplicate filtering.

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
