# Roadmap

Progress against this roadmap is tracked in [progress.md](progress.md).

## V0: Foundation

- Public GitHub repo with safety docs and architecture records.
- PostgreSQL + pgvector schema migrations.
- Docker Compose runtime profile with explicit Docker prerequisite checks.
- Synthetic fixture corpus for repeatable tests.
- Initial MCP, CLI, and REST skeletons.

## V1: Working Knowledge Kernel

- Hybrid retrieval with lexical, vector, graph, and lifecycle scoring.
- Codex hooks for automatic preflight retrieval and session capture.
- Configurable Codex hook policy that invokes `kb.brief` automatically before
  non-trivial prompts or tasks, with relevance gating, context-budget limits,
  opt-out controls, audit records, dashboard health/status, and suppression for
  trivial prompts where memory lookup would add noise.
- Redaction and audit trail before any persistence.
- Markdown wiki export for human auditability.
- Disposable PostgreSQL integration tests.

## V2: Review And Visualization

- Unified dashboard for health, monitoring, watcher status, crawler stats, search,
  graph browsing, stale claims, contradictions, capture review, runtime settings,
  and mail ingestion status.
- Retention policy tuning and memory quality reports.
- Manual approval flows for sensitive or low-confidence captures.

## V2.5: Autonomous Corpus Expansion

- Configurable recursive path monitoring with persistent watch enable/disable state.
- Local host-agent bridge for arbitrary host filesystem paths when the normal
  API/dashboard run in Docker, including native folder browse, host-path
  validation, and host-side sync/watch execution.
- Global include/exclude glob defaults with per-root inherit, extend, or
  override behavior visible in the dashboard.
- Live watcher control with reloadable enabled roots, debounce, bounded queues,
  heartbeat, and stale-state reporting.
- Non-invasive watch semantics: filesystem monitors should subscribe to OS
  notifications or polling snapshots without holding exclusive handles on files
  or directories, so watched roots can coexist with OneDrive, Dropbox,
  SharePoint sync, editors, build tools, and backup software.
- Debounce and file-stability policy for watcher events: coalesce write/rename/
  create bursts per path, wait for size and mtime to remain stable, use adaptive
  longer quiet windows for cloud-synced or large files, and record suppressed
  duplicate events for diagnostics.
- Startup and periodic reconciliation for enabled watched roots so files added,
  modified, or deleted while Flux was offline are detected without manual
  backfill.
- Targeted file/subtree sync for efficient watcher-triggered updates.
- Broad file-type coverage through explicit support tiers: inline text extraction,
  local parser extraction, optional local external-tool extraction, local media
  enrichment, archive/container expansion when enabled, and metadata-only fallback.
- Dedicated file-type coverage matrix in [file-type-coverage.md](file-type-coverage.md),
  with common formats such as `doc`, `xls`, `ppt`, `drawio`, and `vsdx` treated
  as explicit roadmap targets rather than incidental binaries.
- File-type aware extraction roadmap:
  - plain text and notes: txt, md, markdown, rst, org, asciidoc, tex, log,
    changelog, license, readme, todo, ini, env examples, and other UTF text
  - code and developer artifacts: py, js, ts, tsx, jsx, java, cs, fs, cpp, c,
    h, hpp, go, rs, rb, php, swift, kt, scala, sql, sh, ps1, bat, cmd, yaml,
    yml, toml, xml, html, css, scss, dockerfile, makefile, gradle, lockfiles,
    package manifests, OpenAPI/Swagger specs, GraphQL schemas, protobuf, thrift,
    notebooks, diffs, and patches
  - structured data: json, jsonl, ndjson, csv, tsv, psv, parquet, avro, orc,
    feather, arrow, sqlite/db snapshots, xml, yaml, xlsx tables, ods, and common
    BI/report exports; large tabular files use schema/profile/sample-first
    indexing before optional chunk backfill
  - documents and publications: pdf, docx, doc, rtf, odt, ott, epub, mobi/azw
    where locally parseable, html/mhtml, xps, and scanned PDFs; text layers are
    preferred before local OCR
  - spreadsheets and presentations: xlsx, xls, xlsm, ods, csv bundles, pptx,
    ppt, odp, speaker notes, slide text, tables, and embedded media metadata
  - mail and collaboration exports: eml, msg, mbox, maildir, ics, vcf, teams/
    slack/discord exports, meeting transcripts, chat logs, and attachment
    relationships; PST/OST remain optional Windows-host extraction targets
  - images, diagrams, and vector assets: png, jpg/jpeg, webp, gif, tiff, bmp,
    heic/heif where local codecs exist, svg, drawio, mermaid, plantuml, graphviz,
    excalidraw, visio/vsdx where local tooling exists, and design exports such
    as fig/sketch metadata where available
  - audio, video, and subtitles: mp3, wav, m4a, flac, ogg, aac, mp4, mov, mkv,
    avi, webm, wmv, mpeg, ts, vtt, srt, ass, and sidecar transcripts; metadata
    and sidecars are first-class, transcription/frame sampling is deferred
  - archives and containers: zip, 7z, tar, gz, bz2, xz, rar where locally
    supported, wheel/jar/war/ear, npm/tgz, container image metadata, ISO/VHD
    metadata, and nested archive expansion with depth, size, and file-count caps
  - binary/proprietary engineering assets: cad/bim/gis/media project files such
    as dwg/dxf/ifc/rvt/skp/qgz/shp/kml/kmz/psd/ai/indd/blend/fbx/obj/usd/usdz
    are metadata-first, with optional local adapters for text layers, manifests,
    thumbnails, and sidecar exports
  - security and operations artifacts: pcap summaries, sarif, junit, coverage,
    sbom/cyclonedx/spdx, vulnerability scans, terraform plans/state metadata,
    kube manifests, logs, traces, and metrics exports
  - unknown or unsafe binaries: hash, size, timestamps, mime, signature, and
    provenance only unless a trusted local extractor is explicitly enabled
- MoHESR-inspired local media stages, generalized for Flux: draw.io/SVG
  structural extraction, image hash cache reuse, decorative-image skips,
  PaddleOCR/Tesseract local OCR, optional local Ollama/ONNX vision
  descriptions, faster-whisper audio/video transcription, stale lock recovery,
  sidecar transcript indexing, and a separate semantic media backfill phase.
- Background processing with low-priority bounded workers, `FOR UPDATE SKIP LOCKED`
  job claiming, retry/cooldown tracking, and no cloud/provider calls by default.
- Always-on worker runtime: Docker workers process Docker-visible corpus and mail
  spool jobs, while the local host agent owns Windows/host filesystem roots and
  drains host-only extraction jobs automatically after watch-triggered sync.
- Duplicate suppression by content hash while preserving all observed paths and
  source metadata.
- Conservative same-document/version-family suppression in retrieval, so common
  `v1`/`v2`/`final`/dated/copy variants preserve provenance but surface as one
  canonical result by default.
- Corpus retrieval combines full-text, fuzzy, pgvector chunk embeddings, trust rank,
  freshness, deletion state, and canonical duplicate filtering.

## V2.6: Mail Capture And Runtime Configuration

- Settings catalog-backed runtime settings with dashboard editing, environment override
  visibility, masked secrets, audit events, and confirmation-gated reload,
  restart, or reindex requests.
- Terminology cleanup so public docs and dashboard wording do not imply Windows
  Registry usage; settings are cross-platform catalog definitions plus local
  PostgreSQL overrides.
- Dashboard forms for settings edits, mail profile creation, Gmail OAuth setup,
  confirmation-gated apply actions, validation errors, and mail/token status.
- React/Vite operations console served by FastAPI at `/dashboard`, with raw JSON
  available only through a developer/debug drawer instead of primary monitoring
  panels.
- Production PC deployment under `D:\FluxLLMKB` with repo-independent app,
  private, data, logs, runtime, and backup directories plus install/update/start/
  stop/status scripts.
- Docker-hosted Flux control plane for PostgreSQL/pgvector, FastAPI, REST APIs,
  dashboard assets, IMAP worker, corpus crawler, and normal extraction workers.
- Gmail OAuth setup for installed desktop clients, token refresh before IMAP
  XOAUTH2 login, token health reporting, and clean `blocked_auth_required` or
  `auth_expired` states when authorization is missing or revoked.
- IMAP mailbox/label monitor for Gmail or standards-compliant IMAP servers,
  using TLS, XOAUTH2-first authentication, UID/UIDVALIDITY cursors, optional
  IDLE, and periodic reconciliation after restarts.
- Safe mail post-processing defaults: move/remove from capture label or move to
  a processed folder; permanent trash/delete is opt-in and confirmation-gated.
- Classic Outlook COM catch-up for selected mailbox folder paths, intended for
  historical or missed message pulls rather than all-folder live monitoring.
- Separate Windows Outlook COM host process for classic Outlook catch-up, with
  heartbeat, blocked-state reporting, and sync-request claiming from the Docker
  control plane.
- Dashboard controls for IMAP worker state, Outlook COM host state, per-profile
  schedule fields, manual sync requests, last sync, next sync, backlog, and errors.
- Profile-scoped Gmail OAuth actions and status, so multiple IMAP/Gmail accounts
  can be configured independently without floating global OAuth controls.
- Unified private mail spool for IMAP and Outlook exports; Flux indexes only the
  `ready` spool and ignores `_inflight` partial exports.
- Consumer access panel and read-only lookup endpoints for REST, MCP, and CLI
  consumers, including `GET /api/search`, `GET /api/brief`, corpus asset lookup,
  and chunk lookup.

## V2.7: Mail And Retrieval Production Hardening

- Search result content actions and previews:
  - Clicking a search result should open the most useful representation for that
    result instead of leaving it as a passive row.
  - Mail results open inside the dashboard as a logical email view with subject,
    sender, recipients, dates, sanitized HTML/text body, attachment list,
    source profile, mailbox folder, post-process state, and provenance links.
  - File results expose `Preview extracted text`, `Open with default app`,
    `Reveal in folder`, and `Copy path` actions where appropriate. Opening or
    revealing host files is routed through the local host agent so Docker never
    tries to launch Windows desktop apps directly.
  - Dashboard file-open actions are local-only, explicit user actions. The host
    agent must validate that the target is a known indexed asset, normalize the
    path, reject arbitrary browser-supplied paths, audit the action, and return
    clear states such as `opened`, `missing`, `deleted`, `locked`, or
    `host_agent_offline`.
  - Archive members, mail attachments, embedded document objects, and generated
    sidecars should display as related evidence under their parent logical
    result rather than as unexplained standalone implementation files.
- Lock-tolerant indexing and cloud-sync coexistence:
  - Indexers should open files read-only with shared-read semantics where the
    platform supports it, copy stable inputs to a temporary extraction workspace
    before heavy parsers run, and release file handles quickly.
  - Indexing must not require Flux to own or exclusively lock a user file. A
    locked file should produce `pending_stable`, `blocked_locked`, or
    `retrying_locked` state with cooldown, not a failed root crawl.
  - Detect common cloud-sync edge cases such as OneDrive Files On-Demand
    placeholders, partially hydrated files, transient `.tmp`/sync artifacts, and
    rename bursts. Default behavior is to wait or skip with an actionable state
    rather than forcing hydration or conflicting with the sync client.
  - Add optional Windows Volume Shadow Copy support in the host agent for local
    NTFS roots when normal shared reads cannot access a locked but important
    file. VSS use is opt-in, settings-controlled, permission-aware, capped by
    size/time/depth, audited, and falls back to retry/cooldown when unavailable.
  - Add explicit compatibility tests for OneDrive/SharePoint-synced folders,
    Office files open during indexing, large files still being written, and
    repeated save/rename patterns from common editors.
- Mail post-process semantics hardening:
  - Make mailbox-side actions explicit per provider/profile: remove label, move
    to processed folder, IMAP delete plus expunge, Gmail trash, or no action.
  - Add dashboard copy that explains where messages go for the selected policy,
    including Gmail-specific behavior and warnings for destructive policies.
  - Add dry-run and audit records for post-process actions, with per-message
    source UID, folder, policy, command sequence, result, and provider response.
  - Add retry-safe post-process handling so an exported message is not processed
    twice and a failed delete/move does not hide successful local export.
- Retrieval result quality and explainability:
  - Generate query-aware snippets with highlighted terms instead of showing only
    raw chunk prefixes or generic summaries.
  - Treat mail spool implementation files (`manifest.json`, `body.txt`,
    `body.html`, `.eml`, `.msg`) as a single logical mail result by default,
    with attachments linked as related evidence rather than noisy siblings.
  - Separate score display from confidence: expose retrieval streams, raw ranks,
    source trust, freshness, duplicate/version-family suppression, and why each
    result matched.
  - Add configurable filters for source type, root, mail profile, date range,
    document family, and minimum lexical/vector evidence.
- Scheduled sync and worker reliability:
  - Promote due-profile IMAP sync from implicit worker behavior to a first-class
    scheduler state machine with claimed/running/completed/failed runs.
  - Show schedule drift, next-due time, last attempt, last success, retry
    cooldown, auth-blocked state, and worker ownership in the dashboard.
  - Add missed-run reconciliation after API/worker restarts, with bounded catch-up
    and explicit backoff for repeated provider/auth failures.
  - Add tests and health checks proving tight intervals trigger automatically
    without manual `Sync now` or `backfill`.
- Detailed error diagnostics and operator UX:
  - Standardize API error envelopes with code, severity, component, profile/root,
    stage, retryability, user action, and sanitized technical detail.
  - Render errors in the dashboard as red actionable alerts with expandable
    details, copyable diagnostics, and links to the relevant profile/root/job.
  - Preserve recent error history per component while distinguishing optional
    dependency warnings from core health failures.
  - Add operator-facing debug views for mail sync runs, retrieval explanations,
    watcher events, worker heartbeats, and post-process command outcomes.

## V2.8: Indexer Acceleration And Local Inference Optimization

- Add a dedicated acceleration lane for high-volume file indexing before V3
  benchmarks. The goal is to make Flux fast and predictable on a single PC
  without making GPU or heavyweight media tooling mandatory.
- Hardware capability detection and routing:
  - Detect CPU cores, memory pressure, disk throughput hints, NVIDIA/CUDA via
    `nvidia-smi` and local runtimes, ONNX Runtime providers, DirectML/OpenVINO
    where available, and local model servers such as Ollama.
  - Expose capabilities in dashboard Health and Settings, including current
    provider, fallback provider, model cache paths, and blocked-missing-runtime
    reasons.
  - Add settings for `auto`, `cpu_only`, `gpu_preferred`, and `gpu_only` per
    extractor family so operators can avoid saturating the workstation.
- Permanent cache and model layout:
  - Keep dependency, model, OCR, ASR, vision, thumbnail, and parser caches under
    the production install root rather than source worktrees.
  - Reuse pip/package caches, Hugging Face/model caches, Paddle/ONNX/Ollama
    model caches, and generated sidecars across deploy updates.
  - Add cold-start avoidance: model warmup, lazy load with reuse, explicit unload
    for large local models, and dashboard visibility into cache hit/miss rates.
- Resource-aware worker scheduling:
  - Split queues by job family and locality: text/parser, Office/PDF, OCR,
    vision, audio/video transcription, embeddings, archive expansion, and
    preview generation.
  - Add concurrency caps, priority bands, rate limits, backpressure, cooldowns,
    and time budgets per queue so normal watch/index work stays responsive while
    large media backfills run in the background.
  - Prevent repeated expensive work by checking content hash, extracted-sidecar
    hash, model version, provider, source mtime/size, and extraction settings
    before queuing a new job.
- OCR, image, diagram, and vision acceleration:
  - Prefer structural extraction before OCR for formats such as `drawio`, SVG,
    Mermaid/PlantUML/Graphviz exports, `vsdx`, Office embedded drawings, and
    embedded document images.
  - Add image hash caches, decorative-image skips, thumbnail/preview caches,
    page/image batching, confidence thresholds, language routing, and retry-safe
    OCR job metadata.
  - Support a local provider chain such as PaddleOCR/PaddleX with GPU when
    available, Tesseract fallback, and optional local ONNX/Ollama vision
    descriptions. Cloud OCR or cloud vision remains off by default.
- Audio/video transcription acceleration:
  - Reuse sidecar transcripts first, then run local deferred transcription with
    `ffmpeg`/`ffprobe` or bundled equivalents, faster-whisper/CTranslate2, and
    GPU-first CPU-fallback model candidates.
  - Store transcript metadata with source hash, model, device, compute type,
    language, duration, and transcript version so unchanged media is not
    reprocessed.
  - Add stale lock recovery, progress reporting, segment-level diagnostics, and
    bounded temp audio extraction.
- Embedding and vectorization acceleration:
  - Batch embedding generation by model/provider and hardware target instead of
    embedding chunks one by one.
  - Support optional local accelerated embedding providers while preserving the
    deterministic lightweight embedding path for tests and offline bootstrap.
  - Bulk upsert vectors into PostgreSQL/pgvector and record embedding model,
    dimensions, source version, and chunk hash to avoid stale vectors.
- Native and incremental filesystem performance:
  - Evaluate optional `watchfiles`/native watcher backends for high-volume roots,
    keeping polling and watchdog fallbacks.
  - Use incremental scan manifests, mtime/size prefilters, content-hash caches,
    and bounded parallel hashing so reconciliation does not rescan unchanged
    trees expensively.
  - Stage heavy parser input through temporary snapshots where useful, then
    release source file handles quickly.
- Observability and benchmarks:
  - Add dashboard panels for per-stage throughput, queue latency, cache hit rate,
    model warm/cold state, CPU/GPU mode, blocked dependencies, and top slow files.
  - Add benchmark fixtures for small text-heavy roots, Office/PDF-heavy roots,
    image-heavy roots, and audio/video-heavy roots, with before/after p50/p95
    indexing times and resource usage.
  - Keep the implementation generic and local-first, using MoHESR-inspired
    operational lessons without copying private workspace code or data.

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
