# Roadmap Progress

Last reviewed: 2026-06-23

This tracker records public project progress against [roadmap.md](roadmap.md).
It is intentionally separate from live runtime state. Do not add private paths,
mail contents, OAuth tokens, live database values, raw indexed content, or local
deployment-only details here.

Live operational state belongs in the local dashboard and production status
scripts:

- Dashboard: `http://127.0.0.1:8765/dashboard`
- Production status: `scripts/deploy/status-flux.ps1`
- CLI health: `flux-kb doctor --json`

## Status Labels

| Label | Meaning |
| --- | --- |
| `shipped` | Implemented, documented enough to use, and covered by verification. |
| `in progress` | Usable slice exists, but planned scope remains. |
| `planned` | Roadmap intent exists; implementation has not started. |
| `blocked` | Cannot proceed without external input, dependency, or design decision. |
| `deferred` | Intentionally postponed. |

## Version Summary

| Version | Status | Summary |
| --- | --- | --- |
| V0 Foundation | shipped | Public repo, safety model, ADRs, PostgreSQL/pgvector migrations, Docker Compose, fixtures, and initial interfaces exist. |
| V1 Working Knowledge Kernel | in progress | Core storage, CLI/REST/MCP surfaces, redaction/audit, wiki export, hybrid retrieval, automatic Codex hook preflight/capture, direct Codex MCP tool configuration, and safe reference capture exist; graph/lifecycle depth still needs hardening. |
| V2 Review And Visualization | in progress | React dashboard is the unified operational UI; review workflows for graph browsing, stale claims, contradictions, capture approval, and retention tuning remain planned. |
| V2.5 Autonomous Corpus Expansion | in progress | Watch roots, host agent, reconciliation, worker processing, duplicate/version suppression, and broad file-type roadmap exist; deeper extractors/media/archive stages remain planned. |
| V2.6 Mail Capture And Runtime Configuration | in progress | Settings catalog, production deployment, Gmail OAuth, IMAP capture, Outlook host split, dashboard controls, and consumer access exist; provider-specific mail semantics and scheduler state need hardening. |
| V2.7 Mail And Retrieval Production Hardening | in progress | Search result content actions, in-app mail/file detail views, host-agent file actions, logical mail grouping, and structured actionable error diagnostics are implemented; lock-tolerant indexing, mail post-processing, retrieval explainability, and scheduler reliability remain. |
| V2.8 Indexer Acceleration And Local Inference Optimization | planned | Dedicated acceleration lane for GPU/local inference routing, caches, bounded workers, OCR/ASR/vision batching, native watchers, vectorization throughput, and indexing benchmarks. |
| V3 Scale And Evaluation | planned | Historical backfill, retrieval benchmarks, optional ParadeDB/BM25, and local librarian workers. |
| V4 Collaboration And Transfer | planned | Shared vault mode, sync/export policy, optional Apache AGE, and synthetic-data/fine-tuning pipeline. |

## Current Shipped Capabilities

- PostgreSQL/pgvector primary store with migrations.
- Local deterministic `flux-hash-v1` embeddings.
- CLI, REST, and MCP-facing service layer.
- React/Vite dashboard served by FastAPI.
- Production deployment scripts and separated runtime layout under a configurable
  install root.
- Host agent for Windows/host filesystem paths, folder browse, watch, sync,
  reconciliation, and host-side worker processing.
- Corpus root add/edit/delete/sync/watch controls in the dashboard.
- IMAP Gmail OAuth setup and profile-scoped mail capture through a private spool.
- Separate Windows Outlook COM host process model for selected-folder catch-up.
- Automatic Docker worker processing for Docker-visible corpus and mail jobs.
- Automatic host-agent worker processing for host-only roots.
- Exact duplicate suppression and conservative version-family suppression.
- REST/CLI/MCP consumer search and brief access.
- Dashboard search result content actions with sanitized in-app mail viewing,
  file text previews, host-agent open/reveal actions, copy path, action states,
  and related-evidence grouping for mail spool siblings and known child assets.
- Codex hook policy for automatic non-trivial prompt briefs, final-turn capture,
  opt-out runtime settings, dashboard-visible status, and audit records.
- Codex MCP server configuration through `flux-kb codex install-plugin`, making
  Flux tools such as `kb.brief` directly callable in Codex sessions when the
  optional MCP dependency is installed.
- Codex Stop hook reference indexing for bounded public web references and
  existing monitored-root file references, with duplicate checks and audit
  records.
- One-shot feature closeout script with fail-fast validation, structured local
  logs, squash merge, push, deploy, probes, and safe cleanup sequencing.
- Public file-type coverage matrix and roadmap targets.

## Known Gaps

### V1

- Codex plugin discovery, MCP tool availability, automatic hook policy, and
  reference capture exist locally. Broader real-session proof should continue
  across Codex surfaces, especially around long-running turns, user opt-out
  habits, and duplicate capture review.
- Graph traversal, claim lifecycle, confidence decay, contradiction handling, and
  lifecycle scoring need more complete implementation and tests.

### V2

- Dashboard is operational, but graph browsing, stale claim review,
  contradiction review, capture approval, and retention tuning are not complete.
- Dashboard needs deeper drill-down views for retrieval explanations, mail sync
  runs, watcher events, and worker history.

### V2.5

- File-type coverage is broad in roadmap form, but many advanced extractors are
  still metadata-first or optional-tool dependent.
- Archive/container expansion with depth, size, and file-count caps is planned
  but not production-ready.
- Local OCR, visual descriptions, media frame sampling, and transcription need
  bounded worker stages and dashboard controls before default use.
- Same-document/version-family suppression is conservative and path/title based;
  semantic near-duplicate grouping remains a future enhancement.

### V2.6

- IMAP scheduled sync works through the worker loop, but needs first-class run
  records, claiming, drift reporting, backoff, and dashboard history.
- Mail post-processing needs provider-specific semantics, dry-run/audit views,
  and clearer UI around destructive actions.
- Outlook COM host model exists, but broader real-world Outlook catch-up
  verification remains a Windows-host validation task.
- Error reporting has a standard API envelope and dashboard actionable
  diagnostics; remaining mail hardening should add deeper per-run history and
  provider-specific post-process detail.

### V2.7

- Watcher debounce exists as a roadmap/runtime concern, but needs stronger
  documented guarantees and tests for burst coalescing, stable-size/mtime
  windows, cloud-sync rename bursts, and large file writes.
- Lock-tolerant indexing is planned: shared read handles, temporary extraction
  copies, locked-file retry/cooldown states, and optional Windows VSS fallback
  for local NTFS roots when normal reads cannot access an important file.
- OneDrive/SharePoint/Dropbox coexistence needs explicit verification so
  monitoring remains non-invasive and indexing reports actionable states instead
  of fighting sync clients.
- Mail post-processing, retrieval explainability, and scheduler reliability
  remain planned hardening items. See
  [roadmap.md](roadmap.md#v27-mail-and-retrieval-production-hardening).

### V2.8

- GPU/local inference acceleration is planned but not implemented as a cohesive
  Flux runtime yet.
- Current extractors detect some optional tools, but Flux still needs provider
  routing by hardware capability, CPU/GPU policy settings, model warmup/unload,
  and persistent model/cache layout visibility.
- OCR, vision, ASR, thumbnails/previews, archive expansion, and embedding
  vectorization need bounded worker queues, cache-hit tracking, and per-stage
  throughput telemetry.
- High-volume watcher/indexer performance needs native watcher evaluation,
  incremental scan manifests, content-hash caches, and benchmarks across text,
  Office/PDF, image, and audio/video-heavy roots. See
  [roadmap.md](roadmap.md#v28-indexer-acceleration-and-local-inference-optimization).

### V3

- Historical Codex backfill is not yet production-ready.
- Benchmark suite and retrieval strategy comparisons are planned.
- Optional ParadeDB/BM25 and local-LLM librarian workers are not started.

### V4

- Team/shared vault mode, sync/export governance, Apache AGE backend option, and
  synthetic-data/fine-tuning pipeline are planned only.

## Immediate Next Queue

1. Add lock-tolerant indexing states, debounce/stability tests, and optional
   Windows VSS design/controls for locked files.
2. Promote IMAP scheduled sync into a claimable scheduler state machine.
3. Improve retrieval snippets with query-aware highlights and explainability.
4. Add provider-specific mail post-process policies with dry-run and audit views.
5. Design V2.8 indexer acceleration: hardware detection, local inference
   provider routing, permanent caches, bounded media/OCR/ASR workers, vector
   batching, and throughput telemetry.
6. Continue extractor expansion from [file-type-coverage.md](file-type-coverage.md),
   prioritizing common Office legacy files, diagrams, archives, and embedded
   media.

## Update Rules

- Update this file in the same commit as any roadmap-significant feature.
- Keep `roadmap.md` strategic and `progress.md` factual.
- Prefer `in progress` over `shipped` unless the feature has working code,
  documentation or UI, and verification.
- Link to docs, tests, or commits when a status changes materially.
- Never record private runtime state, private file paths, mail contents, tokens,
  raw memories, embeddings from private content, or database dumps.
