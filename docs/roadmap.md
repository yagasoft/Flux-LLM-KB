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
