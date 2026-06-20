# Architecture

Flux-LLM-KB stores agent interaction knowledge as a lifecycle-managed knowledge
system rather than a large prompt-injected memory file.

## Core Model

- `episodes`: session-level summaries and completed work records.
- `sources`: provenance records for files, prompts, external sources, and tool outputs.
- `claims`: atomic facts with confidence, timestamps, and supersession links.
- `entities`: typed people, projects, files, concepts, systems, and decisions.
- `relations`: typed graph edges such as uses, depends_on, supersedes, contradicts,
  caused, fixed, and mentions.
- `embeddings`: vector representations for semantic retrieval.
- `audit_events`: append-only record of memory writes, deletes, redactions, and queries.
- `capture_jobs`: asynchronous ingestion and consolidation jobs.
- `workspace_scopes`: workspace/project identity and visibility boundaries.
- `retention_policies`: decay and forgetting configuration by memory class.
- `monitored_roots`: opt-in local paths for recursive corpus crawling and watch mode.
- `source_assets`: file-level corpus records with metadata, hashes, extraction state,
  and duplicate/canonical tracking.
- `asset_chunks`: extracted text/code/document snippets for retrieval without turning
  every file into an interaction episode.
- `crawl_runs` and `watcher_state`: crawler statistics, watcher heartbeat, event, and
  error state for dashboard monitoring.

## Retrieval

Queries combine four signals:

- lexical retrieval from PostgreSQL full-text search
- semantic retrieval from pgvector
- graph traversal through typed relations
- lifecycle scoring from confidence, recency, reinforcement, and supersession

The merged result uses reciprocal rank fusion and then packs a compact task brief
within a strict token budget.

Corpus chunks use the same `embeddings` table as episodes with
`owner_table = 'asset_chunks'`. Corpus retrieval fuses PostgreSQL full-text,
trigram fuzzy matching, pgvector similarity, source trust rank, and freshness.
Deleted assets and non-canonical duplicate assets are suppressed from retrieval.

## Corpus Monitoring

Configured roots are crawled recursively according to root policy, `.gitignore`,
`.fluxignore`, `.fluxkbignore`, and `.exclude.codex` markers. Metadata is recorded
for every supported file type. Sync can target a full root, a subtree, or a
single file. Small text-like files are extracted and chunked locally; heavy
documents, images, audio, and video are queued for local deferred processing.
Images are dimensioned locally, media uses sidecar transcripts and `ffprobe`
when available, and archives or unknown binaries remain metadata-only unless
explicitly enabled later.

Deferred workers claim jobs with `FOR UPDATE SKIP LOCKED`, use retry/cooldown
state in `capture_jobs`, and do not call cloud providers by default. Jobs move to
explicit terminal states such as `completed`, `metadata_only`, or
`blocked_missing_dependency`; they are not completed merely because they were
claimed. Duplicate content is suppressed by content hash while preserving every
observed path and source asset record.

The watcher runtime reloads enabled roots while running, so `watch enable` and
`watch disable` take effect without a restart. It applies debounce, a bounded
event queue, heartbeat recording, and stale-state reporting.

The dashboard is the single UI surface for health, watcher status, crawler stats,
backlog, errors, retrieval/index stats, and future graph/review workflows.

## Integration Surfaces

- MCP exposes memory tools to Codex and other MCP-capable agents.
- CLI supports local automation, diagnostics, migration, and export.
- REST mirrors the MCP operations for clients that do not support MCP.
- Codex hooks enforce preflight retrieval and post-turn capture across workspaces.
