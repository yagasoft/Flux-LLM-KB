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
- `runtime_settings`, `runtime_setting_events`, `runtime_components`, and
  `runtime_control_requests`: settings catalog-backed configuration, audit trail, and
  reload/restart/reindex coordination.
- `mail_profiles`, `mail_messages`, and `mail_sync_runs`: IMAP and Outlook COM
  capture profiles, per-message export state, cursors, errors, and sync runs.
- `outlook_host_state` and `outlook_sync_requests`: Windows host heartbeat and
  pull-request coordination for Outlook COM catch-up profiles.

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

File coverage is intentionally broad but tiered. Flux should first record stable
metadata for every encountered file: path, size, timestamps, hashes, MIME/signature,
source root, trust rank, and provenance. Extraction then escalates only when a
safe local path exists: inline UTF/code parsing; local document/data libraries;
optional local tools such as LibreOffice, Tesseract, ffprobe/ffmpeg, or
faster-whisper; bounded archive/container expansion; and finally metadata-only
terminal states for unsafe, encrypted, proprietary, or unsupported binaries.
The detailed target matrix lives in [file-type-coverage.md](file-type-coverage.md)
and explicitly includes common legacy and diagram formats such as `doc`, `xls`,
`ppt`, Draw.io, and Visio `vsdx`. This lets Flux cover common text, code,
office, PDF, spreadsheet, presentation, mail, calendar/contact, image, diagram,
audio, video, subtitle, archive, database/export, notebook, CAD/BIM/GIS/design,
security scan, operations log, and unknown-binary families without requiring
cloud services or blocking normal watch/crawl loops.

When the API/dashboard is Docker-hosted, arbitrary Windows/macOS/Linux host
paths are accessed through a separate local host agent (`flux-kb host-agent run`).
The dashboard can ask that host process to open a native folder picker, validate
the selected path on the host OS, and execute host-side sync/watch work against
the same PostgreSQL database. Docker never interprets Windows drive paths such
as `E:\Projects` with Linux path rules.

Global crawler include/exclude globs live in the settings catalog. Each
monitored root chooses `inherit`, `extend`, or `override`; the dashboard shows
both root-local globs and the effective policy used by crawl/watch.

The media backfill path is deliberately local and staged. Flux should prefer
cheap structural signals first: file hash caches, dimensions, SVG/draw.io
structure, sidecar transcripts, and decorative-image skips. Optional richer
stages can then run as bounded jobs: Tesseract or PaddleOCR OCR, local
Ollama/ONNX image descriptions, frame sampling, and faster-whisper audio/video
transcription. Semantic media embeddings are a separate backfill phase so large
media files do not slow normal crawl/watch loops.

Deferred workers claim jobs with `FOR UPDATE SKIP LOCKED`, use retry/cooldown
state in `capture_jobs`, and do not call cloud providers by default. Jobs move to
explicit terminal states such as `completed`, `metadata_only`, or
`blocked_missing_dependency`; they are not completed merely because they were
claimed. Duplicate content is suppressed by content hash while preserving every
observed path and source asset record. Retrieval also applies a conservative
same-document/version-family collapse for common filename variants such as
`v1`, `v2`, `final`, dated copies, and copy suffixes. It suppresses sibling
versions only in result presentation and exposes the canonical path plus
suppressed sibling count.

The watcher runtime reloads enabled roots while running, so `watch enable` and
`watch disable` take effect without a restart. It applies debounce, a bounded
event queue, heartbeat recording, and stale-state reporting. Live filesystem
events are not the only correctness mechanism: watcher services run startup reconciliation
and periodic reconciliation for enabled watched roots. A
reconciliation is a full-root sync recorded in `crawl_runs.reason` as
`startup_reconcile` or `periodic_reconcile`; it compares the current filesystem
snapshot with persisted `source_assets` hashes, marks deleted files as deleted,
queues changed deferred files, and treats empty folders as a clean no-op. Watch
events continue to use targeted sync with reason `watch_event`.

Production deployments are intentionally not repo-coupled. The default Windows
PC install root is `D:\FluxLLMKB`, with deployed app files under `app`, private
runtime/config/spool data under `private`, PostgreSQL bind-mounted data under
`data`, and logs under `logs`. Docker runs PostgreSQL/API/dashboard/worker from
prebuilt local images and bind-mounts only deployed runtime paths. Host-agent and
Outlook-host run as Windows Scheduled Tasks in the logged-in user session.

The dashboard is the single UI surface for health, watcher status, crawler stats,
backlog, errors, retrieval/index stats, runtime settings, mail ingestion status,
and future graph/review workflows. The UI is a React/Vite operations console
bundled into the Python package and served by FastAPI at `/dashboard`; raw JSON
payloads are diagnostic-only, not the primary monitoring surface.

## Runtime Configuration

Settings are defined in a typed settings catalog with defaults, optional environment
overrides, sensitivity flags, apply modes, and affected components. Resolution is
`environment override > database override > catalog default`. Bootstrap settings
such as database URL and API bind address are visible but read-only in the
dashboard because changing them requires restarting the process that serves the
dashboard. Sensitive settings are masked in API, CLI, and dashboard responses.
The settings catalog is an application catalog stored in code and PostgreSQL; it
does not use the Windows Registry.

Settings that affect live behavior are picked up on the next service call.
Settings that require reload, component restart, or embedding reindex create
runtime control requests and require confirmation before mutation.

## Mail Ingestion

Mail capture uses a private filesystem spool so IMAP and Outlook exports enter
the same corpus path. Exporters write into `_inflight/<export_id>` and atomically
move completed exports to `ready/<export_id>`. Flux monitors only `ready`, so
partial messages and attachments are not indexed. Each export contains a
manifest, message body files, the original `.eml` or `.msg` where available, and
attachments.

IMAP profiles are the preferred ongoing capture mechanism. They connect over
TLS, use Gmail installed-app OAuth plus XOAUTH2 when configured, refresh access
tokens before login, track UID/UIDVALIDITY cursors per folder or label, and
always run reconciliation so restarts and missed events are recovered.
Post-processing defaults to moving/removing the capture label or moving the
message to a processed folder; permanent delete is not the default.

Classic Outlook COM catch-up profiles pull selected folder paths from local
Outlook for historical or missed messages. They are intentionally scoped
catch-up jobs, not broad live mailbox monitors. COM access runs only in a
separate Windows host process (`flux-kb outlook-host run`) under the logged-in
user session. Docker-hosted Flux services never attempt COM directly; they
record sync requests and read host heartbeat/status through PostgreSQL and REST.

After the split, Outlook COM crawls when a sync request is queued or when a
scheduled Outlook profile becomes due while the Windows host is running. If
`sync_enabled=false`, it crawls only on manual requests such as dashboard
“Sync Now” or `flux-kb outlook-host sync --profile <name>`. If
`sync_enabled=true`, the host reconciles due profiles on startup and then at the
configured interval. Missing host/Outlook states are explicit:
`host_offline`, `blocked_not_windows`, `blocked_missing_dependency`, or
`blocked_outlook_unavailable`.

## Integration Surfaces

- MCP exposes memory tools to Codex and other MCP-capable agents.
- CLI supports local automation, diagnostics, migration, and export.
- REST mirrors the MCP operations for clients that do not support MCP.
- Codex hooks enforce preflight retrieval and post-turn capture across workspaces.
- Docker hosts the normal Flux API/dashboard/worker processes. The Outlook COM
  bridge is deliberately outside Docker because COM requires the logged-in
  Windows user session and classic Outlook.
- The generic host-agent bridge is also outside Docker when direct access to
  arbitrary host filesystem paths or native folder browsing is required.
