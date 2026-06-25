# Roadmap

Last reviewed: 2026-06-25

This file is the canonical public roadmap and implementation-status tracker for
Flux-LLM-KB. It is intentionally separate from live runtime state. Do not add
private paths, mail contents, OAuth tokens, live database values, raw indexed
content, local deployment-only details, or private memory exports here.

Live operational state belongs in the local dashboard and production status
scripts:

- Dashboard: `http://127.0.0.1:8765/dashboard`
- Production status: `scripts/deploy/status-flux.ps1`
- CLI health: `flux-kb doctor --json`

## Status Model

| Label | Meaning |
| --- | --- |
| `shipped` | Implemented, documented enough to use, and covered by verification. |
| `in progress` | Usable slice exists, but planned scope remains. |
| `planned` | Roadmap intent exists; implementation has not started. |
| `blocked` | Cannot proceed without external input, dependency, or design decision. |
| `deferred` | Intentionally postponed. |

## Priority Model

The detailed roadmap tables and queued work use the same priority labels:

| Label | Meaning |
| --- | --- |
| `P0` | Core-value or correctness work that should come before broader automation. |
| `P1` | Basic feature completion and reliability hardening that should follow the P0 foundation. |
| `P2` | Automation, enrichment, and optimization work that should wait for evaluation signals. |
| `P3` | Deferred exploration that should not distract from single-user reliability. |

## Version Summary

| Version | Plain-English Purpose | Status | Summary |
| --- | --- | --- | --- |
| V0 Foundation | Put the project on safe, repeatable footing before adding memory features. | shipped | Public repo, safety model, ADRs, PostgreSQL/pgvector migrations, Docker Compose, fixtures, and initial interfaces exist. |
| V1 Working Knowledge Kernel | Store useful agent knowledge in a searchable system instead of relying on one giant memory file. | in progress | Core storage, CLI/REST/MCP surfaces, redaction/audit, wiki export, hybrid retrieval, Codex hooks, Codex MCP setup, safe reference capture, and V1 graph/lifecycle backend hardening exist; review UI depth remains future work. |
| V2 Review And Visualization | Give the operator a dashboard to inspect, correct, and trust what Flux remembers. | in progress | React dashboard is the unified operational UI; graph browsing, claim lifecycle review, capture-review visibility, approval/rejection decisions, audit-visible rationales, retention policy tuning, and memory quality reporting exist. |
| V2.5 Autonomous Corpus Expansion | Let Flux safely notice and index useful local files without constant manual imports. | in progress | Watch roots, host agent, reconciliation, worker processing, duplicate/version suppression, broad file-type roadmap, structured diagram extraction, business document extractor expansion, and bounded recursive archive/container extraction exist; deeper media parsing remains planned. |
| V2.6 Mail Capture And Runtime Configuration | Make mail capture and runtime settings manageable from the local app instead of hidden scripts. | in progress | Settings catalog, production deployment, Gmail OAuth, IMAP capture, claimable IMAP scheduler state, Outlook host split, dashboard controls, provider-specific post-processing, and consumer access exist; broader live-provider validation should continue. |
| V2.7 Mail And Retrieval Production Hardening | Make search, mail handling, and file indexing reliable enough for daily use. | in progress | Search result actions, in-app mail/file detail views, host-agent file actions, logical mail grouping, structured diagnostics, claimable IMAP sync runs, provider-specific mail post-processing, lock-tolerant indexing/watch states, query-aware snippets, retrieval/brief explainability, configurable retrieval filters, and suppression/lifecycle diagnostics exist; automated-action rationale remains planned. |
| V2.8 Indexer Acceleration And Local Inference Optimization | Make large local indexing faster, observable, and easier to tune on one PC. | in progress | Dedicated acceleration lane foundations exist for local capability status, explicit watcher backend policy/probe, cache layout visibility, worker-family queues and caps, backpressure/debug status, bounded crawler hash parallelism, incremental scan manifest skips, throughput telemetry, cache-backed local OCR for image/image-only PDF jobs, cache-backed local ASR for audio/video jobs, recursive container telemetry, local vision cache telemetry, scene-transition video frame sampling, thumbnail cache reuse, embedding vector refresh jobs, and durable deterministic scan/soak/watcher benchmark history with labeled comparisons. |
| V3 Scale And Evaluation | Prove Flux improves retrieval and memory quality before trusting more automation. | planned | Code-aware corpus indexing, historical backfill, retrieval benchmarks, automated memory governance, optional ParadeDB/BM25, and local-only librarian workers. |
| V4 Collaboration And Transfer | Explore sharing and transfer only after the personal local system is safe and stable. | planned | Shared vault mode, sync/export policy, optional Apache AGE, and synthetic-data/fine-tuning pipeline. |

## V0: Foundation

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Public project baseline | P0 | Keep the public repo safe to share while the private runtime stays private. | Public GitHub repo with safety docs, architecture records, and public/private data boundaries. | shipped | Safety docs, architecture docs, and repository guidance exist. | Maintain boundaries during every roadmap-significant update. |
| Durable store | P0 | Give Flux a dependable database so memories survive restarts and upgrades. | PostgreSQL plus pgvector schema migrations as the primary persistence backend. | shipped | PostgreSQL/pgvector migrations are present and remain the preferred backend. | Keep migrations backward-compatible as later features expand tables. |
| Runtime bootstrap | P1 | Make startup failures obvious instead of leaving operators guessing. | Docker Compose runtime profile with explicit Docker prerequisite checks. | shipped | Docker setup and doctor/status scripts fail clearly when prerequisites are missing. | Keep deployment scripts aligned with production layout changes. |
| Test fixtures | P1 | Let tests cover indexing behavior without using private files. | Synthetic fixture corpus for repeatable tests. | shipped | Fixtures support repeatable local tests without private content. | Expand fixtures only with synthetic data. |
| Initial interfaces | P1 | Give people and tools stable ways to talk to Flux from the start. | Initial MCP, CLI, and REST skeletons. | shipped | CLI, REST, and MCP-facing service layer exist. | Preserve surface compatibility while adding features. |

## V1: Working Knowledge Kernel

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Storage and retrieval core | P0 | Help Flux find relevant facts by combining exact words, meaning, relationships, and freshness. | Hybrid retrieval with lexical, vector, graph, and lifecycle scoring. | in progress | PostgreSQL full-text, pgvector, local deterministic `flux-hash-v1` embeddings, JSONB/GIN, trigram fuzzy matching, hybrid RRF ranking, durable claim lifecycle events, claim relations, bounded typed graph traversal, lifecycle scoring decay, and CLI/REST/MCP graph/claim primitives exist. V2 review UX and deeper retrieval explainability remain planned. | Add V2 graph browsing, stale claim review, contradiction review, capture approval, and retention tuning after this backend contract. |
| Codex integration | P0 | Bring relevant Flux context into Codex work automatically and save useful outcomes afterward. | Codex hooks for automatic preflight retrieval and session capture. | shipped | Automatic non-trivial prompt briefs, final-turn capture, opt-out settings, dashboard-visible status, and audit records exist. | Continue real-session proof across Codex surfaces, long-running turns, opt-out habits, and duplicate capture review. |
| Hook policy | P1 | Let users control when Codex memory is used so small or sensitive prompts are not over-handled. | Configurable Codex hook policy with relevance gating, context-budget limits, opt-out controls, audit records, dashboard health/status, and suppression for trivial prompts. | shipped | Hook policy controls and status are available through settings, dashboard, and audit flow. | Keep policy tuning tied to real-session feedback. |
| Codex MCP/plugin setup | P1 | Make Flux tools available inside Codex without hand-editing every integration file. | Codex personal plugin and MCP configuration for callable Flux tools. | shipped | `flux-kb codex install-plugin` configures MCP; Codex may expose raw names such as `kb.brief` or wrappers such as `mcp__flux_llm_kb.kb_brief`. | Keep installer diagnostics current when Codex plugin/MCP discovery changes. |
| Redaction and audit | P0 | Reduce privacy risk and leave a record of what changed. | Redaction and audit trail before any persistence. | shipped | Public docs and implementation require redaction and audit records before persistence; manual memory and claim write paths redact user-supplied text before database writes. | Extend audit coverage as new write paths are added. |
| Human audit export | P2 | Let a person review remembered knowledge outside the app. | Markdown wiki export for human auditability. | shipped | Wiki export exists as a CLI/runtime capability. | Keep exports free of private generated artifacts in the public repo. |
| Integration tests | P1 | Catch database and contract breakage before changes reach the running system. | Disposable PostgreSQL integration tests. | shipped | Disposable database tests exist for core flows. | Add targeted integration coverage when graph/lifecycle work lands. |

## V2: Review And Visualization

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Unified operations dashboard | P1 | Put health, review, search, and controls in one place so operators do not need raw logs. | Unified dashboard for health, monitoring, watcher status, crawler stats, search, graph browsing, stale claims, contradictions, capture review, runtime settings, and mail ingestion status. | in progress | React/Vite dashboard served by FastAPI is the unified operations UI for health, corpus monitoring, runtime settings, mail capture, worker state, Outlook COM host status, claim lifecycle review, selected-entity graph browsing, retention tuning, memory quality reporting, capture-review queue decisions, and recent decision audit visibility. | Keep retention review exception-oriented for rare escalations, policy tuning, audits, and recovery once automatic retention workers exist. |
| Retention and quality | P2 | Help decide which memories stay useful, fade out, or need review. | Retention policy tuning and memory quality reports. | shipped | Fixed retention classes for claim, episode, and corpus are tunable through REST, CLI, MCP, and the dashboard Review tab; updates are audited, and quality reports aggregate sanitized claim, episode, and corpus candidates without raw content or automatic mutation. | Calibrate policy defaults from live review feedback, then use them as guardrails for future automatic low-risk lifecycle tagging, deprioritization, and duplicate suppression while keeping public docs free of private runtime values. |
| Sensitive capture review | P1 | Put questionable captures in front of a person before they become trusted memory. | Manual approval flows for sensitive or low-confidence captures. | in progress | Capture and audit paths exist, and the dashboard now exposes a pending capture-review queue with approval/rejection actions, required rationale capture, sanitized responses, and recent `capture.review_*` audit visibility. Approved Codex backfill ingestion remains future work. | Continue live usage feedback and design the later ingestion worker before processing approved historical backfill content. |
| Drill-down diagnostics | P1 | Let operators investigate why Flux behaved a certain way instead of guessing. | Dashboard drill-down views for retrieval explanations, watcher events, and worker history. | in progress | Dashboard has operational panels and structured diagnostics; deeper drill-down remains uneven. | Add retrieval explanation, watcher event, and worker history drill-downs in roadmap order after core review workflows. |

## V2.5: Autonomous Corpus Expansion

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Watch roots and host bridge | P0 | Let Flux safely watch folders on the real machine even when the app runs in Docker. | Configurable recursive path monitoring plus local host-agent bridge for arbitrary host filesystem paths when API/dashboard run in Docker. | shipped | Host agent supports Windows/host paths, folder browse, validation, watch, sync, reconciliation, and host-side worker processing. | Continue compatibility validation across host platforms and sync clients. |
| Include/exclude policy | P1 | Let users decide which files matter and which should be ignored. | Global include/exclude glob defaults with per-root inherit, extend, or override behavior visible in the dashboard. | shipped | Settings catalog and dashboard expose effective root policy. | Keep policy explanations clear as file-type support expands. |
| Live watcher runtime | P0 | Notice file changes without taking ownership of files or interrupting other apps. | Reloadable enabled roots, debounce, bounded queues, heartbeat, stale-state reporting, and non-invasive watch semantics. | shipped | Watcher reload, heartbeat, stale-state reporting, stable-candidate gating, and non-exclusive semantics exist. | Broaden real-world validation against OneDrive, SharePoint, Dropbox, editors, build tools, and backup software. |
| Reconciliation and targeted sync | P0 | Recover from missed file events and update only the changed parts when possible. | Startup and periodic reconciliation plus targeted file/subtree sync for watcher-triggered updates. | shipped | Startup/periodic reconciliation and targeted sync are implemented for enabled watched roots. | Use the V2.8 benchmark harness when validating high-volume roots and watcher-triggered updates. |
| File-type coverage matrix | P1 | Make clear what Flux can read now, what it only tracks as metadata, and what still needs work. | Broad file-type coverage through explicit support tiers and a dedicated coverage matrix. | in progress | Public `file-type-coverage.md` and broad roadmap targets exist; structured Draw.io, modern VSDX extraction, local business document expansion for Office variants, OpenDocument files, legacy Excel/PowerPoint adapters, EPUB/FB2 extraction, Calibre `ebook-convert` eBook fallback, comic archive container handling, bounded recursive archive/container extraction, embedded media sidecar transcripts, cache-backed local OCR for image/image-only PDF jobs, and cache-backed local ASR for audio/video jobs exist; many advanced formats remain metadata-first or optional-tool dependent. | Continue extractor expansion, prioritizing richer vision, embedded-media diagnostics, and remaining specialized local-tool stages. |
| Text/code/data extraction | P1 | Read common plain-text, code, and data files quickly without expensive tools. | Inline text extraction and local parser extraction for text, code, developer artifacts, structured data, and common exports. | in progress | Small text-like files and many local parser paths are supported; large tabular files need schema/profile/sample-first indexing before optional chunk backfill. | Add sample-first indexing and parser coverage where local libraries are reliable. |
| Documents, spreadsheets, and presentations | P1 | Extract useful text from normal business files people already have. | Local extraction for PDF, Office, OpenDocument, ebooks, scans, speaker notes, tables, and embedded media metadata. | in progress | Common PDF/Office extraction exists; Office macro/template variants, OpenDocument text/spreadsheet/presentation files, legacy Excel/PowerPoint local adapters, bounded OCR fallback for image-only PDFs, EPUB/FB2 local publication parsing, Calibre `ebook-convert` fallback for MOBI/AZW/LIT, comic archive container extraction, and embedded document parsing from bounded containers now have extraction paths. Broader document families still need staged local-tool handling. | Prioritize remaining local-tool support with metadata-first fallback. |
| Mail and collaboration exports | P2 | Bring exported conversations and attachments into search without treating them as loose files. | Treat EML, MSG, mbox, maildir, calendar/contact, chat exports, transcripts, and attachments as first-class indexed relationships. | in progress | IMAP/Outlook mail enters a private spool and related-evidence grouping exists for mail results. PST/OST and broader collaboration exports remain optional/future. | Keep mail spool logical grouping while adding provider-specific mail hardening in V2.7. |
| Images, diagrams, and vector assets | P2 | Make visual files searchable through local structure, OCR, or optional local captions. | Structural extraction, image hash cache reuse, local OCR, optional local vision descriptions, and metadata-first handling for design/vector assets. | in progress | Draw.io, embedded Draw.io SVG/PNG payloads, and modern VSDX/VSDM/VSSX/VSSM/VSTX/VSTM containers are parsed locally into diagram chunks. Deferred image jobs can run local Tesseract OCR with redacted cache reuse and dashboard-visible cache hit/miss telemetry. Decorative-image spacers are skipped before OCR or vision. Optional local vision descriptions run through configured loopback local inference when `acceleration.vision.enabled` and `acceleration.vision.model` are configured; the first implemented runtime path is an Ollama-compatible API and Gemma-class local vision models are valid configurable model choices when installed locally. Redacted vision cache telemetry is exposed. Deeper vector/design extractors remain planned. | Add deeper vector/design extractors and calibrate local vision quality after retrieval benchmarks exist. |
| Audio, video, and subtitles | P2 | Let Flux use transcripts, local speech tools, and frame previews without sending media to the cloud. | Sidecar transcript indexing, optional local ASR, media metadata, stale lock recovery, and semantic media backfill. | in progress | Media sidecars and metadata are first-class targets, including embedded media sidecar transcripts from archives; local faster-whisper ASR can transcribe bounded audio/video jobs through `ffmpeg` when `acceleration.asr.model_path` points at an existing local model. Redacted ASR cache entries record cache hits, misses, and segment counts without cloud transcription or remote model download. Video jobs can use scene-transition frame sampling with thumbnail cache reuse and midpoint fallback only when no transition is detected. Richer media diagnostics and semantic media backfill remain deferred. | Add stale lock recovery proof, richer segment/frame diagnostics, and semantic media backfill. |
| Archives and containers | P1 | Safely look inside zip-like packages without letting huge or unsafe archives overwhelm the system. | Bounded archive/container expansion with depth, size, and file-count caps. | in progress | ZIP-family, TAR-family, gzip/bzip2/xz streams, supported package containers, and comic archive formats are enumerated through bounded local adapters; optional-tool formats report explicit dependency states when local tools are missing; inline-safe text/code members, nested containers, embedded documents, diagrams, images, and media become related child assets with sanitized recursion telemetry. Embedded media sidecar transcript files are used before probing or ASR while remaining visible as child assets. | Add broader optional-tool validation and deeper specialized local-tool stages after worker-family scheduling and observability mature. |
| Worker claiming and retry | P0 | Process heavy files in the background without two workers fighting over the same job. | Low-priority bounded workers with `FOR UPDATE SKIP LOCKED`, retry/cooldown tracking, and no cloud/provider calls by default. | shipped | Deferred workers claim jobs, track retries/cooldowns, and use explicit terminal states. | Extend queue families for heavy extractor stages in V2.8. |
| Duplicate and version suppression | P0 | Avoid showing the same document repeatedly while keeping all evidence recoverable. | Duplicate suppression by content hash, conservative same-document/version-family suppression, and semantic near-duplicate grouping in retrieval. | in progress | Exact duplicate suppression, conservative path/title version-family suppression, and advisory semantic duplicate clusters for corpus chunks, episodes, and claims exist. Semantic clusters choose canonical members, suppress noncanonical retrieval siblings, and expose sanitized counts without automatic deletion. | Calibrate semantic thresholds from live review feedback, then feed clusters into V3 evaluation and automation-first librarian workers. |
| Corpus retrieval | P0 | Let file contents participate in search alongside session memories. | Full-text, fuzzy, pgvector chunk embeddings, trust rank, freshness, deletion state, and canonical duplicate filtering. | shipped | Corpus retrieval combines these signals, including freshness reranking, and suppresses deleted/non-canonical duplicate assets. | Improve query-aware snippets and retrieval explainability in V2.7. |

## V2.6: Mail Capture And Runtime Configuration

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Runtime settings catalog | P1 | Let operators see and change settings safely from the app. | Settings catalog-backed runtime settings with dashboard editing, environment override visibility, masked secrets, audit events, and confirmation-gated apply actions. | shipped | Settings catalog, dashboard editing, masking, audit events, and apply coordination exist. | Keep new settings catalog-backed and avoid implying Windows Registry usage. |
| Terminology cleanup | P3 | Avoid confusing users into thinking settings are stored in the Windows Registry. | Public docs and dashboard wording should not imply Windows Registry usage. | shipped | Settings are documented as cross-platform catalog definitions plus local PostgreSQL overrides. | Preserve this wording in future settings docs. |
| Dashboard forms | P1 | Make setup and changes guided instead of requiring manual API calls. | Dashboard forms for settings edits, mail profile creation, Gmail OAuth setup, confirmation-gated apply actions, validation errors, and mail/token status. | shipped | Dashboard forms exist for settings, mail profiles, OAuth, validation, and status. | Improve action copy as provider-specific mail policies are added. |
| Dashboard delivery | P1 | Make the dashboard the normal operator experience, with raw JSON kept for debugging. | React/Vite operations console served by FastAPI at `/dashboard`, with raw JSON only in developer/debug contexts. | shipped | FastAPI serves the bundled dashboard; raw JSON is diagnostic rather than primary monitoring UI. | Keep primary operator flows in the dashboard, not raw JSON panels. |
| Production deployment | P1 | Keep installed runtime data separate from the repo so updates are safer. | Production PC deployment under `D:\FluxLLMKB` with repo-independent app, private, data, logs, runtime, and backup directories plus install/update/start/stop/status scripts. | shipped | Production scripts and separated runtime layout exist under a configurable install root. | Keep closeout/deploy scripts aligned with layout changes. |
| Docker control plane | P1 | Run the main services predictably while host-only actions stay outside Docker. | Docker-hosted PostgreSQL/pgvector, FastAPI, REST APIs, dashboard assets, IMAP worker, crawler, and normal extraction workers. | shipped | Docker hosts the normal Flux API/dashboard/worker runtime. | Keep host-only actions routed through host agent. |
| Gmail OAuth and IMAP capture | P1 | Pull Gmail/IMAP messages into Flux with visible auth and sync state. | Gmail installed-client OAuth, token refresh before IMAP XOAUTH2 login, token health reporting, blocked auth states, TLS IMAP capture, UID cursors, optional IDLE, and reconciliation. | in progress | Gmail OAuth, profile-scoped IMAP capture, token health, blocked auth states, and scheduler reconciliation exist. Broader live-provider validation should continue across Gmail and standards-compliant IMAP servers. | Broaden live-provider validation and keep auth-required/expired states actionable. |
| Mail post-processing defaults | P1 | Keep captured mail organized without deleting anything unless explicitly allowed. | Safe defaults to move/remove capture label or move to processed folder; permanent trash/delete opt-in and confirmation-gated. | shipped | Provider-specific policies exist for Gmail labels/trash and generic IMAP copy/delete/expunge, with dry-run, audit events, dashboard visibility, and destructive-action confirmation. | Continue live-provider validation across Gmail and standards-compliant IMAP servers. |
| Outlook COM catch-up | P2 | Let Windows Outlook users import selected folders through a separate host process. | Classic Outlook COM catch-up for selected mailbox folder paths, with a separate Windows host process, heartbeat, blocked-state reporting, and request claiming. | in progress | Separate Outlook host process model and dashboard status exist. Broader real-world Outlook catch-up verification remains a Windows-host validation task. | Validate Outlook catch-up across selected-folder scenarios and blocked dependency states. |
| Mail dashboard controls | P1 | Let operators start, inspect, and troubleshoot mail sync without leaving the dashboard. | Dashboard controls for IMAP worker state, Outlook COM host state, per-profile schedule fields, manual sync requests, last sync, next sync, backlog, and errors. | shipped | Dashboard exposes mail worker state, scheduler counts, profile sync controls, Outlook state, backlogs, errors, post-process policy fields, dry-run, and recent post-process outcomes. | Add deeper per-run debug detail only when operator workflows need it. |
| Profile-scoped OAuth | P1 | Keep each mail account's login status separate and understandable. | Profile-scoped Gmail OAuth actions and status for multiple IMAP/Gmail accounts. | shipped | Multiple profile-scoped OAuth controls and status exist without floating global OAuth controls. | Keep OAuth diagnostics profile-scoped. |
| Unified private mail spool | P0 | Prevent partially exported mail from being indexed before it is complete. | Unified private spool for IMAP and Outlook exports; Flux indexes only `ready` and ignores `_inflight`. | shipped | IMAP and Outlook exports use a private spool with `_inflight` and `ready` states. | Keep public docs free of raw mail, spool data, and local runtime values. |
| Consumer access | P0 | Let other tools read Flux knowledge without giving them write access. | Read-only lookup endpoints for REST, MCP, and CLI consumers, including search, brief, asset lookup, and chunk lookup. | shipped | REST/CLI/MCP consumer search and brief access plus corpus asset/chunk lookup exist. | Maintain compatibility while adding explainability fields. |

## V2.7: Mail And Retrieval Production Hardening

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Search result actions and previews | P1 | Make search results immediately useful by opening the right file, mail, or preview. | Search results should open useful logical representations, including sanitized mail views and host-agent file actions. | shipped | Dashboard search result actions, sanitized in-app mail viewing, file text previews, host-agent open/reveal actions, copy path, action states, and related-evidence grouping exist. | Keep host-agent validation/audit strict as more file actions are added. |
| Logical related evidence | P1 | Show attachments, archive contents, and sidecars together so evidence stays understandable. | Archive members, mail attachments, embedded objects, and sidecars should appear under parent logical results. | in progress | Mail spool siblings, archive/container children, and known child assets are grouped as related evidence. Broader embedded media extraction still needs validation. | Extend related-evidence grouping as embedded media extraction lands. |
| Lock-tolerant indexing and cloud-sync coexistence | P0 | Keep indexing from breaking when files are open, syncing, or still being written. | Read-only/shared access, temporary extraction snapshots, no exclusive file ownership, cloud-sync edge handling, optional VSS, and compatibility tests. | in progress | Watcher stability gating, `pending_stable`, `retrying_locked`, `blocked_locked`, retry/cooldown, VSS settings/capability reporting, and fallback to retry/cooldown exist. Actual VSS snapshot extraction and broad cloud-sync compatibility proof remain. | Validate OneDrive/SharePoint/Dropbox, open Office files, large writes, and editor save/rename patterns; then implement opt-in VSS extraction. |
| Mail post-process hardening | P1 | Make mailbox cleanup predictable and retry-safe after messages are captured. | Provider/profile-specific mailbox actions, explanatory dashboard copy, dry-run/audit records, and retry-safe handling. | shipped | Explicit policies now cover no-op, Gmail remove label, Gmail move label, Gmail trash, generic IMAP move, and confirmation-gated delete/expunge. Sync records post-process events, surfaces failures, preserves exported spool data, and avoids advancing the cursor past failed UIDs. | Continue live-provider validation and keep raw mail content out of audit views. |
| Retrieval snippets and explainability | P0 | Show why a result appeared so users can trust or tune search. | Query-aware snippets, highlighted terms, retrieval streams, raw ranks, source trust, freshness, duplicate/version suppression, lifecycle penalties, and configurable filters. | in progress | Query-aware snippets, highlight ranges, search-result explanation metadata, per-query retrieval filters, filter-exclusion traces, sanitized exact/version/semantic suppression metadata, and brief-packing traces are exposed through REST, MCP, CLI, and the dashboard. Deeper score/confidence separation and automated-action rationale remain planned. | Add deeper explanation for deprioritization, escalation, and automated lifecycle actions after V3 evaluation foundations land. |
| Scheduled sync and worker reliability | P1 | Make recurring mail sync observable, claim-safe, and recoverable after missed runs. | First-class IMAP scheduler state machine with claimed/running/completed/failed runs, drift, retry cooldown, auth blocks, ownership, missed-run reconciliation, and tests. | shipped | Claimable IMAP scheduled sync runs, lifecycle state, run history, drift/missed-run fields, owner/attempt metadata, health diagnostics, and dashboard scheduler counts are visible. | Continue live-provider validation for tight intervals across Gmail and standards-compliant IMAP providers. |
| Error diagnostics and operator UX | P1 | Turn failures into actionable messages instead of vague errors. | Standard API error envelopes and dashboard alerts with code, severity, component, target metadata, retryability, user action, technical detail, and links. | in progress | Structured API error envelopes, dashboard actionable diagnostics, expandable details, copyable JSON, and navigation targets exist. Deeper operator debug views remain. | Add debug views for mail sync runs, retrieval explanations, watcher events, worker heartbeats, and post-process outcomes. |

## V2.8: Indexer Acceleration And Local Inference Optimization

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Acceleration lane | P1 | Make large folder indexing faster while showing what is slowing it down. | Make high-volume indexing fast and predictable on a single PC without requiring GPU or heavyweight media tooling. | in progress | V2.8 foundation now exposes acceleration status, permanent cache layout, explicit watcher backend policy/probe, worker-family queues, worker caps, backpressure/debug rows, duration telemetry, OCR cache hit/miss telemetry, ASR cache hit/miss plus segment telemetry, recursive container member counters, local vision cache counters, decorative-image skips, frame sample counts, thumbnail cache telemetry, parser/cache telemetry, embedding refresh telemetry, bounded crawler hash parallelism, incremental scan manifest skip counters, and durable benchmark history through REST, CLI, MCP, and dashboard Health. Cache-backed local OCR, local ASR, recursive containers, optional configured loopback local inference for vision, scene-transition video sampling, local deterministic embedding refresh jobs, native watcher proof, scan/soak/watcher benchmark modes, real-root aggregate calibration, model-readiness benchmark mode, and labeled before/after comparison fields are implemented slices. | Continue throughput tuning with optional provider-specific acceleration only after evaluation controls exist. |
| Hardware capability detection | P1 | Show which local hardware and tools Flux can use before a job gets stuck. | Detect CPU, memory pressure, disk hints, NVIDIA/CUDA, ONNX Runtime providers, DirectML/OpenVINO, and local model servers; expose capability and blocked-runtime reasons. | in progress | CPU count, Windows memory when available, cache-root disk space, NVIDIA `nvidia-smi`, ONNX Runtime providers, watchdog availability, disabled-by-default loopback local-model probing, and model/tool readiness benchmark telemetry are reported. DirectML/OpenVINO-specific policy and model routing remain planned. | Extend provider-specific routing only when a worker family needs it. |
| Permanent cache and model layout | P1 | Keep expensive local work reusable across app updates. | Keep dependency, model, OCR, ASR, vision, thumbnail, parser caches, and generated sidecars under the production install root across deploy updates. | in progress | Cache root resolution and named directories for models, OCR, ASR, vision, thumbnails, parser output, embeddings, and temp files are visible in status/dashboard. OCR jobs store redacted Tesseract cache entries under the OCR cache, ASR jobs store redacted faster-whisper cache entries under the ASR cache, and embedding refresh records source hashes/cache keys in vector metadata without raw text. These families expose hit/miss telemetry; broader model lifecycle remains planned. | Extend cache records and hit/miss telemetry to parser output and future model-backed providers as those worker families land. |
| Resource-aware worker scheduling | P0 | Prevent one kind of heavy work from starving everything else. | Split queues by job family and locality with concurrency caps, priorities, rate limits, backpressure, cooldowns, and time budgets. | in progress | Corpus jobs carry fixed worker-family, resource-class, priority, and time-budget metadata; existing `--kind` filters claim by family; configured `acceleration.worker_cap.*` values cap concurrent family claims; completion/retry/block paths record duration telemetry; status surfaces show cap usage, worker-family backpressure, oldest pending age, retry/lock transitions, sanitized slow-job rows, parser cache telemetry, and `manifest_skipped_unchanged` counters. Benchmark `soak` mode creates benchmark-tagged synthetic jobs by worker family, claims them through the same cap logic, records cap/backpressure/duration telemetry, and purges them. | Use synthetic soak plus real observed telemetry to tune default caps from hardware evidence. |
| OCR, image, diagram, and vision acceleration | P2 | Make images and diagrams searchable locally while avoiding unnecessary or unsafe work. | Prefer structural extraction before OCR; add image hash caches, decorative skips, batching, thresholds, language routing, and local provider chains. | in progress | Image jobs and image-only PDFs can use local Tesseract OCR with `pdftoppm` rendering for PDFs, page caps, redacted cache reuse, explicit `blocked_missing_dependency` states, and cache hit/miss telemetry. Decorative-image skips avoid work for spacer assets. Optional local vision descriptions use configured loopback local inference when `acceleration.vision.enabled` and `acceleration.vision.model` are configured; the current implementation supports the Ollama-compatible API as its first runtime path, and redacted results are stored in the vision cache. Cloud OCR/vision remains off by default; language routing and provider-specific batching remain planned. | Add language routing and provider-specific batching once evaluation/controls are ready. |
| Audio/video transcription acceleration | P2 | Reuse transcripts and local speech tools so media becomes searchable without cloud transcription. | Reuse sidecar transcripts, then run local deferred transcription with ffmpeg/ffprobe or bundled equivalents and faster-whisper/CTranslate2. | in progress | Sidecars are recognized as targets; local ASR now probes media with `ffprobe`, respects `acceleration.asr.max_duration_seconds`, extracts bounded mono 16 kHz temp audio through `ffmpeg`, transcribes with faster-whisper from `acceleration.asr.model_path`, writes redacted ASR cache entries, and reports segment/cache telemetry. Video jobs can optionally use `acceleration.video.frame_sampling.enabled`, `acceleration.video.scene_threshold`, and `acceleration.video.frame_sample_count` for scene-transition sampling into the thumbnail cache. Media diagnostics now include sidecar use, ASR segment totals, frame sample counts/timestamps, thumbnail cache counters, stale-lock evidence, and blocked dependency reasons where available. | Add progress reporting and semantic media backfill. |
| Embedding and vectorization throughput | P1 | Keep search vectors fresh without redoing unchanged work. | Batch embeddings by model/provider/hardware target, support optional accelerated providers, and bulk upsert vectors. | in progress | Deterministic local `flux-hash-v1` embeddings now use a provider boundary, source-hash metadata, `corpus_embed` jobs, CLI/REST/MCP status/enqueue/backfill surfaces, and worker-family telemetry for vectors, skipped unchanged items, batches, and cache hits/misses. Optional accelerated providers remain planned. | Add provider-specific accelerated backends only after evaluation controls and dimensionality migration policy are designed. |
| Local model-assisted knowledge optimization | P2 | Use local models later to suggest better memory cleanup without sending data away. | Use local model backends only when available to enrich memory governance and indexing decisions. | planned | Local inference routing is not yet implemented as a shared provider layer for knowledge optimization. | Let optional local Llama/Gemma-class models assist librarian-worker proposals, semantic clustering, contradiction checks, canonical-summary drafts, and audit rationale generation without remote calls; fall back cleanly to rule-based behavior when unavailable. |
| Native and incremental filesystem performance | P0 | Make repeated scans and file watching cheaper on large folders. | Evaluate native watcher backends, incremental scan manifests, prefilters, content-hash caches, bounded parallel hashing, and temporary snapshots. | in progress | `watcher.backend` supports `auto`, `watchdog`, and `polling`, with `FLUX_KB_WATCHER_BACKEND` override, native/fallback status, fallback reasons, and a temp-directory synthetic probe that never touches private watched roots. Incremental scan manifest rows store metadata only and allow unchanged files to skip expensive hashing/extraction while reconciliation remains authoritative. `crawler.hash_parallelism` now bounds concurrent content hashing while preserving deterministic asset ordering, manifest skip behavior, stability gating, lock handling, and serial local parser extraction. Benchmark `watcher` mode stores backend policy, selected backend, fallback reason, event counts, and latency metadata. Real-root benchmark scope records aggregate-only calibration data for monitored roots and host-agent paths. | Broaden cloud-sync/native watcher compatibility proof and tune hash parallelism from real high-volume roots. |
| Observability and benchmarks | P0 | Give operators before/after evidence instead of guessing which setting or update helped. | Dashboard panels and benchmark fixtures for throughput, latency, cache hits, model warm/cold state, CPU/GPU mode, blocked dependencies, slow files, and p50/p95 indexing times. | in progress | Dashboard Health and Jobs show acceleration capabilities, selected watcher backend, cache root, worker-family queue counts, cap/backpressure status, p95 duration where available, OCR/ASR/container/parser/embedding telemetry, manifest skip counters, slow-job diagnostics, watcher event drill-down foundations, and durable benchmark history for text-heavy, Office/PDF-heavy, archive/container-heavy, image-heavy, audio/video-heavy, monitored-root, monitored-path, and model-readiness runs. Benchmark run modes cover `scan`, `soak`, `watcher`, `model`, and `all`; scan supports cold/warm passes, labels, deployment labels, compare labels, scope hashes, hash parallelism and manifest skip fields, throughput deltas, and read-only diagnostic recommendations that do not mutate settings. Stored benchmark records are metadata only and exclude raw text, private watched roots, mail contents, credentials, and embeddings. | Compare benchmark history across deployment updates and tune defaults from observed hardware evidence. |

## V3: Scale And Evaluation

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Code-aware corpus indexing | P0 | Help Codex answer code questions by understanding symbols, files, tests, and relationships. | Parser-backed code intelligence over opted-in repositories so Codex can find files, symbols, definitions, references, tests, routes, handlers, and implementation locations without treating code only as generic text. | planned | Today Flux indexes supported code-like files as text chunks in `asset_chunks`, searchable through `kb.search`, REST search, CLI search, and MCP wrappers. This is useful but not code-aware: there is no durable symbol index, function/class boundary chunking, AST/tree-sitter parser layer, definition/reference graph, or code-specific retrieval surface. | Build a future code-intelligence slice on top of V2.8 watcher, scheduling, manifest, parser-cache, and benchmark foundations. Preserve generic search compatibility while adding code-specific schema, ranking, diagnostics, and synthetic fixture repos. |
| Historical Codex backfill | P2 | Safely import useful older Codex work without pulling in unsafe or noisy history. | Historical Codex backfill with redaction. | planned | Codex capture exists, but historical backfill is not production-ready. | Design redaction-first backfill after V1 lifecycle and V2 review workflows are stronger. |
| Retrieval benchmarks | P0 | Measure whether Flux search is actually finding the right things. | Retrieval benchmark suite. | planned | Benchmarks are planned; V2.8 indexing benchmarks should land first for corpus throughput. | Define query sets and quality metrics for retrieval precision, recall loss, contradiction reduction, brief dilution, and librarian-worker shadow-mode evaluation after retrieval explainability work starts. |
| Optional search backend | P3 | Consider a stronger search engine only after the current baseline is measured. | Optional ParadeDB/BM25 path. | planned | Not started. | Evaluate only after baseline retrieval benchmarks exist. |
| Librarian workers | P2 | Let Flux suggest cleanup candidates so memory does not become stale or cluttered. | Automation-first local librarian workers for derived-memory cleanup, consolidation, and linting. | planned | Not started. | Start with rule-based and model-assisted candidate generation for stale, redundant, low-utility, contradictory, unscoped, and low-confidence memories; run in shadow mode before broader auto-apply. |
| Automated memory governance | P2 | Allow safe routine cleanup only after tests prove it will not damage useful memory. | Policy-gated automation for routine memory lifecycle optimization. | planned | Retention quality reporting and lifecycle primitives exist, but automatic mutation is not implemented. | Automatically apply low-risk reversible actions such as duplicate suppression, retrieval deprioritization, stale tagging, canonical cluster presentation, and lifecycle updates after evaluation thresholds are met. |
| Local consolidation and escalation | P2 | Combine obvious duplicates locally and ask a person only for risky decisions. | Local-only consolidation with rare human escalation. | planned | Canonical semantic/procedural consolidation and escalation policy are not implemented. | Use local model-assisted consolidation only when evidence is high-confidence and provenance is preserved; escalate only for hard deletion, privacy/security findings, high-authority contradictions, destructive policy changes, protected memories, low-confidence high-impact decisions, or failed evaluation thresholds. |
| Operator digests | P2 | Summarize what automation did or wants to do so the operator stays in control. | Periodic reporting for automated memory governance. | planned | Review UI exists, but automation digests are not implemented. | Provide periodic digests and recovery/audit views for operator awareness instead of per-item approval. |

### Future Slice: Code-Aware Corpus Indexing

Status: `planned`. This is a future cohesive implementation slice, separate
from V2.8 indexer reliability and benchmark history. It should use the V2.8
watcher backend policy, worker-family scheduling, crawl manifests, parser cache
telemetry, and synthetic benchmark history as foundations, but it should not
implement VSS extraction, provider-specific embedding backends, runtime tracing,
or V3 retrieval/governance benchmarks as part of the same slice.

Plain-English overview: this future work would help Codex answer questions
about codebases by understanding files, symbols, tests, routes, and references
instead of treating every source file like ordinary text.

Current behavior is intentionally generic: small supported code-like files are
recognized by extension, extracted as text, stored as `source_assets` plus
`asset_chunks`, embedded like other corpus chunks, and retrievable through
`kb.search`, REST search, CLI search, and MCP wrappers. The future requirement is
to make opted-in repositories code-aware while preserving that generic baseline.

Planned scope:

- Plain-English purpose: cover the kinds of source, config, test, and build
  files real projects contain.
  Broaden code and developer-artifact coverage beyond the current extension
  set, including common source languages, notebooks, build scripts, package
  manifests, infrastructure/config files, API schemas, SQL files, migrations,
  tests, generated-code markers, and patch/diff artifacts.
- Plain-English purpose: split code into meaningful pieces such as functions
  and classes so search lands on the right area.
  Add parser-backed chunking that prefers stable semantic boundaries over fixed
  text windows: module, class, function, method, interface/type, route or
  handler, config block, SQL object/query, migration step, notebook cell, and
  test case where reliably detectable.
- Plain-English purpose: remember where important code names live and what
  kind of thing they are.
  Store durable symbol metadata such as symbol name, kind, language, file path,
  line and byte ranges, parent symbol, exported/public flag where detectable,
  signature when safe, and docstring/comment summary when safe.
- Plain-English purpose: use real code parsers when available, but keep indexing
  useful when a parser cannot handle a file.
  Introduce optional AST/tree-sitter or language-specific parser adapters behind
  a parser abstraction. Unsupported languages and parser failures must fall back
  to inline text chunking with explicit sanitized fallback metadata.
- Plain-English purpose: store code facts in a way that can be searched,
  explained, and linked back to files.
  Add a future durable storage concept such as `code_symbols` and
  `code_references`, or an equivalent schema, tied back to `source_assets` and
  `asset_chunks` so code results can still participate in normal corpus
  retrieval and provenance flows.
- Plain-English purpose: show how code pieces relate so Codex can answer
  questions like "who calls this?" or "where are the tests?"
  Capture definition, reference, call, import, route, test-to-target, and
  config-to-implementation relationships where the parser can produce reliable
  evidence. The roadmap must not imply perfect static analysis across all
  languages or dynamic frameworks.
- Plain-English purpose: keep answers focused on the selected project instead
  of mixing unrelated code from elsewhere.
  Scope results by repository, workspace, monitored root, language, and path so
  Codex can ask targeted questions such as "Find the implementation of X",
  "Where is this CLI command registered?", "Show route handlers for Y", "Find
  tests for this function", "Find callers/references of this symbol", and
  "Summarize the public API of this module".

Codex-facing retrieval surfaces:

- Plain-English purpose: avoid breaking existing search users while adding code
  intelligence.
  Keep existing `kb.search`, REST search, CLI search, and corpus asset/chunk
  lookup backward compatible.
- Plain-English purpose: let callers narrow searches by language, path, symbol
  type, or code relationship.
  Add future generic search filters such as `logical_kinds=["file"]`,
  `file_kind="code"`, `language`, `symbol_kind`, `path_glob`, `repo`, `root`,
  `relationship`, and definition/reference/test/config/example facets if those
  fit the existing search contract cleanly.
- Plain-English purpose: add a clearer code-search command if generic search
  becomes too crowded.
  Consider dedicated MCP/CLI/REST surfaces such as `kb.code_search`,
  `kb.code_symbol_lookup`, or equivalent if code navigation becomes clearer as a
  separate contract than overloading generic search.
- Plain-English purpose: make code answers cite where they came from and why
  they matched.
  Return enough structured metadata for Codex to cite the defining file, line
  range, symbol kind, relationship type, parser/fallback status, and associated
  chunk without exposing raw private code outside the normal private corpus
  retrieval path.

Ranking requirements:

- Plain-English purpose: if the user names an exact function or file, that
  should win over a vague similarity match.
  Exact symbol and path matches should beat semantic guesses.
- Plain-English purpose: prefer the project the user is actually working in.
  Local workspace, selected root, and repository evidence should outrank
  unrelated corpus matches.
- Plain-English purpose: tell users whether a result defines something, uses
  it, tests it, or configures it.
  Definitions should be distinguishable from references, callers, imports,
  tests, examples, and configuration.
- Plain-English purpose: keep supporting files findable without confusing them
  with main implementation code.
  Tests, examples, migrations, generated files, and config should remain
  discoverable, but should not be mixed indistinguishably with implementation
  results unless requested.
- Plain-English purpose: show enough evidence to debug surprising code-search
  results.
  Parser-confidence, fallback status, symbol kind, path proximity, import/call
  relationships, file recency, duplicate/version suppression, and existing
  retrieval explanations should be visible enough to debug surprising results.

Privacy and safety constraints:

- Plain-English purpose: Flux should only inspect code locations the user chose.
  Indexing remains opt-in through monitored roots and workspace scopes.
- Plain-English purpose: keep this public repo safe even when private code is
  indexed locally.
  Public repository docs, fixtures, and tests must not contain raw private code,
  private paths, generated private wiki exports, credentials, embeddings, or
  local runtime database values.
- Plain-English purpose: diagnostics should explain what happened without
  leaking code content.
  Stored operational telemetry for parser failures, worker history, benchmark
  runs, and dashboard diagnostics must avoid raw code content unless that
  content is already part of private corpus storage.
- Plain-English purpose: test code-search behavior with fake projects, not user
  repositories.
  Public tests must use synthetic fixture repositories only, with small invented
  symbols, routes, configs, tests, and references.

Out of scope for this slice:

- Full IDE replacement.
- Perfect cross-language static analysis.
- Runtime tracing or profiling.
- Provider-specific accelerated embedding backends.
- VSS snapshot extraction.
- V3 retrieval benchmark design, librarian workers, or automated governance.

Prioritized implementation breakdown:

1. Priority: P0. Model-actionable item: expand code and developer-artifact
   classification for opted-in repositories, including source files, tests,
   configs, manifests, API schemas, SQL, infrastructure files, notebooks, and
   generated-code markers.
   Plain-English explanation: Flux first needs to recognize the files Codex is
   likely to ask about before deeper symbol search can add value.
2. Priority: P0. Model-actionable item: add public-safe synthetic fixture
   repositories that cover small invented implementations, tests, configs,
   routes, imports, generated files, unsupported languages, and parser failures.
   Plain-English explanation: The feature needs correctness tests before it can
   safely index private repositories or change retrieval ranking.
3. Priority: P0. Model-actionable item: implement a parser abstraction that
   produces stable semantic chunks for modules, classes, functions, methods,
   routes, SQL objects, notebook cells, and tests when reliable, with explicit
   sanitized fallback metadata when parsing is unavailable or fails.
   Plain-English explanation: Codex gets quick value when search lands on the
   right function or test, while fallback behavior keeps indexing useful for
   unsupported files.
4. Priority: P0. Model-actionable item: add durable code symbol and reference
   storage tied to `source_assets` and `asset_chunks`, including symbol name,
   kind, language, file path, range metadata, parent symbol, definition/reference
   relationship, parser status, and safe provenance.
   Plain-English explanation: A stored symbol layer is the core data foundation
   that turns generic corpus search into code-aware retrieval.
5. Priority: P0. Model-actionable item: add repository-scoped code retrieval and
   ranking so exact symbol/path matches, local workspace evidence, definitions,
   references, tests, configs, and examples are distinguishable in results.
   Plain-English explanation: This completes the basic user-facing behavior:
   Codex can ask where code lives and receive focused, explainable answers.
6. Priority: P1. Model-actionable item: expose code-aware retrieval through the
   least-surprising MCP, CLI, and REST contract, either as code filters on
   existing search or as dedicated code-search and symbol-lookup surfaces if the
   generic contract becomes crowded.
   Plain-English explanation: Tooling should be easy for Codex and scripts to
   call without breaking existing `kb.search` users.
7. Priority: P1. Model-actionable item: add dashboard and diagnostic views for
   code index coverage, parser failures, fallback rates, slow files, language
   coverage, and privacy-safe per-repository status.
   Plain-English explanation: Operators need to see what indexed well and what
   fell back before trusting code-aware results on real projects.

Acceptance criteria:

- Codex can query the index for a known symbol in a synthetic repository and get
  the defining file/chunk before generic text matches.
- Codex can distinguish definitions, references, tests, configuration, examples,
  imports, and callers in results.
- Existing `kb.search`, REST search, CLI search, and corpus asset/chunk lookup
  remain backward compatible.
- Unsupported languages still index as text with clear fallback metadata.
- Parser failures are visible as sanitized diagnostics in worker/status/debug
  surfaces.
- No private paths, raw private code, credentials, embeddings, or private corpus
  content appear in public fixtures, docs, tests, telemetry summaries, or
  benchmark records.

## V4: Collaboration And Transfer

| Piece | Priority | Plain-English Purpose | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- | --- | --- |
| Team/shared vault mode | P3 | Let multiple people share knowledge only after personal safety rules are proven. | Team/shared vault mode. | planned | Personal/local-first mode remains the focus. | Define trust, visibility, and audit boundaries after single-user governance matures. |
| Sync and export policies | P3 | Decide what can move between machines or users and under what rules. | Sync and export policies. | planned | Export exists for local audit; multi-user sync/export governance is not started. | Design policy model after shared vault requirements are clear. |
| Optional graph backend | P3 | Consider a specialized relationship database only if PostgreSQL stops being enough. | Optional Apache AGE graph backend. | planned | PostgreSQL remains the primary store; optional AGE is not started. | Evaluate after graph traversal and lifecycle semantics stabilize. |
| Synthetic data and fine-tuning | P3 | Use generated safe data later to improve testing or training without exposing private content. | Synthetic-data and fine-tuning pipeline. | planned | Not started. | Defer until evaluation and governance foundations are in place. |

## Queued Work In Priority Order

The queue uses the same priority labels as the detailed roadmap item tables.

Queued items are intentionally larger related slices so one model run can spend
more tokens on implementation and verification instead of repeated planning,
branch setup, and context rebuilding.

1. Priority: P0. Model-actionable item: ship a code-aware retrieval foundation
   bundle that includes the first five P0 items in the Future Slice breakdown
   plus a public-safe retrieval benchmark baseline. Implement classification,
   synthetic fixture repositories, parser/fallback chunks, durable
   symbol/reference storage, repository-scoped code retrieval ranking, expected
   benchmark results, precision/recall-style checks, brief-packing dilution
   checks, duplicate-suppression checks, and regression commands for CLI, REST,
   MCP, and service-level retrieval paths.
   Plain-English explanation: This is the highest-value next slice because it
   gives Codex useful code navigation while proving that retrieval changes still
   return correct, focused, explainable results.
2. Priority: P0. Model-actionable item: ship an indexer reliability and tuning
   bundle that validates lock/cloud-sync correctness, watcher reconciliation,
   retry-to-blocked transitions, benchmark comparison summaries, and default
   tuning recommendations for crawler hash parallelism, worker-family caps, and
   cache-readiness checks. Cover synthetic and local-safe scenarios for open
   Office files, large writes, rename/save patterns, OneDrive/SharePoint/Dropbox
   delayed availability, startup reconciliation, periodic reconciliation, and
   diagnostic runs that do not mutate settings.
   Plain-English explanation: Flux must notice and process real folders
   correctly before more heavy extractors or automation depend on the indexer.
   This bundle turns the V2.8 benchmark machinery into practical reliability
   evidence and safer defaults.
3. Priority: P1. Model-actionable item: ship an operator diagnostics and basic
   corpus completion bundle that completes code-aware MCP/CLI/REST contracts,
   code result metadata, dashboard code coverage views, parser failure views,
   fallback-rate summaries, privacy-safe per-repository status, retrieval
   explanation drill-downs, watcher event views, worker heartbeat views, mail
   sync/post-process views, blocked dependency views, slow-job history, and
   sample-first indexing for large tabular and structured data files.
   Plain-English explanation: After the core retrieval and indexer bundles,
   users need stable tool calls, dashboard evidence, and basic large-data
   coverage so daily operation does not require reading raw logs or forcing huge
   files through full extraction.
4. Priority: P2. Model-actionable item: ship an evaluated memory-governance
   automation bundle that implements librarian-worker shadow mode, benchmarked
   thresholds, reversible lifecycle actions, duplicate suppression, retrieval
   deprioritization, stale tagging, canonical cluster presentation, recovery
   views, audit records, operator digests, and optional loopback-only local model
   routing for proposals, semantic clustering, contradiction checks,
   canonical-summary drafts, and rationale generation with rule-based fallback.
   Plain-English explanation: Automation should arrive as one evaluated system,
   not as scattered cleanup mutations. This keeps memory quality improvements
   measurable, reversible, and private.
5. Priority: P3. Model-actionable item: defer optional ParadeDB/BM25,
   shared-vault collaboration, sync/export governance, Apache AGE, and
   synthetic-data/fine-tuning until single-user retrieval, code indexing,
   indexer reliability, diagnostics, evaluation, and recovery flows are stable.
   Plain-English explanation: These may matter later, but they should not spend
   roadmap or model context before the local single-user knowledge system is
   correct, inspectable, and useful.

## Update Rules

- Update this file in the same commit as any roadmap-significant feature.
- Keep roadmap status factual and conservative.
- Prefer `in progress` over `shipped` unless the piece has working code,
  documentation or UI where applicable, and verification.
- Link to docs, tests, or commits when a status changes materially.
- Never record private runtime state, private file paths, mail contents, tokens,
  raw memories, embeddings from private content, or database dumps.
