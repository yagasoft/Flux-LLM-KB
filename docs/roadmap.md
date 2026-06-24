# Roadmap

Last reviewed: 2026-06-24

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

## Version Summary

| Version | Status | Summary |
| --- | --- | --- |
| V0 Foundation | shipped | Public repo, safety model, ADRs, PostgreSQL/pgvector migrations, Docker Compose, fixtures, and initial interfaces exist. |
| V1 Working Knowledge Kernel | in progress | Core storage, CLI/REST/MCP surfaces, redaction/audit, wiki export, hybrid retrieval, Codex hooks, Codex MCP setup, safe reference capture, and V1 graph/lifecycle backend hardening exist; review UI depth remains future work. |
| V2 Review And Visualization | in progress | React dashboard is the unified operational UI; graph browsing, claim lifecycle review, capture-review visibility, approval/rejection decisions, audit-visible rationales, retention policy tuning, and memory quality reporting exist. |
| V2.5 Autonomous Corpus Expansion | in progress | Watch roots, host agent, reconciliation, worker processing, duplicate/version suppression, broad file-type roadmap, structured diagram extraction, business document extractor expansion, and bounded archive/container member enumeration exist; deeper media parsing remains planned. |
| V2.6 Mail Capture And Runtime Configuration | in progress | Settings catalog, production deployment, Gmail OAuth, IMAP capture, claimable IMAP scheduler state, Outlook host split, dashboard controls, provider-specific post-processing, and consumer access exist; broader live-provider validation should continue. |
| V2.7 Mail And Retrieval Production Hardening | in progress | Search result actions, in-app mail/file detail views, host-agent file actions, logical mail grouping, structured diagnostics, claimable IMAP sync runs, provider-specific mail post-processing, lock-tolerant indexing/watch states, query-aware snippets, retrieval/brief explainability, configurable retrieval filters, and suppression/lifecycle diagnostics exist; automated-action rationale remains planned. |
| V2.8 Indexer Acceleration And Local Inference Optimization | planned | Dedicated acceleration lane for GPU/local inference routing, local model-server routing, caches, bounded workers, OCR/ASR/vision batching, native watchers, vectorization throughput, and indexing benchmarks. |
| V3 Scale And Evaluation | planned | Historical backfill, retrieval benchmarks, automated memory governance, optional ParadeDB/BM25, and local-only librarian workers. |
| V4 Collaboration And Transfer | planned | Shared vault mode, sync/export policy, optional Apache AGE, and synthetic-data/fine-tuning pipeline. |

## V0: Foundation

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Public project baseline | Public GitHub repo with safety docs, architecture records, and public/private data boundaries. | shipped | Safety docs, architecture docs, and repository guidance exist. | Maintain boundaries during every roadmap-significant update. |
| Durable store | PostgreSQL plus pgvector schema migrations as the primary persistence backend. | shipped | PostgreSQL/pgvector migrations are present and remain the preferred backend. | Keep migrations backward-compatible as later features expand tables. |
| Runtime bootstrap | Docker Compose runtime profile with explicit Docker prerequisite checks. | shipped | Docker setup and doctor/status scripts fail clearly when prerequisites are missing. | Keep deployment scripts aligned with production layout changes. |
| Test fixtures | Synthetic fixture corpus for repeatable tests. | shipped | Fixtures support repeatable local tests without private content. | Expand fixtures only with synthetic data. |
| Initial interfaces | Initial MCP, CLI, and REST skeletons. | shipped | CLI, REST, and MCP-facing service layer exist. | Preserve surface compatibility while adding features. |

## V1: Working Knowledge Kernel

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Storage and retrieval core | Hybrid retrieval with lexical, vector, graph, and lifecycle scoring. | in progress | PostgreSQL full-text, pgvector, local deterministic `flux-hash-v1` embeddings, JSONB/GIN, trigram fuzzy matching, hybrid RRF ranking, durable claim lifecycle events, claim relations, bounded typed graph traversal, lifecycle scoring decay, and CLI/REST/MCP graph/claim primitives exist. V2 review UX and deeper retrieval explainability remain planned. | Add V2 graph browsing, stale claim review, contradiction review, capture approval, and retention tuning after this backend contract. |
| Codex integration | Codex hooks for automatic preflight retrieval and session capture. | shipped | Automatic non-trivial prompt briefs, final-turn capture, opt-out settings, dashboard-visible status, and audit records exist. | Continue real-session proof across Codex surfaces, long-running turns, opt-out habits, and duplicate capture review. |
| Hook policy | Configurable Codex hook policy with relevance gating, context-budget limits, opt-out controls, audit records, dashboard health/status, and suppression for trivial prompts. | shipped | Hook policy controls and status are available through settings, dashboard, and audit flow. | Keep policy tuning tied to real-session feedback. |
| Codex MCP/plugin setup | Codex personal plugin and MCP configuration for callable Flux tools. | shipped | `flux-kb codex install-plugin` configures MCP; Codex may expose raw names such as `kb.brief` or wrappers such as `mcp__flux_llm_kb.kb_brief`. | Keep installer diagnostics current when Codex plugin/MCP discovery changes. |
| Redaction and audit | Redaction and audit trail before any persistence. | shipped | Public docs and implementation require redaction and audit records before persistence; manual memory and claim write paths redact user-supplied text before database writes. | Extend audit coverage as new write paths are added. |
| Human audit export | Markdown wiki export for human auditability. | shipped | Wiki export exists as a CLI/runtime capability. | Keep exports free of private generated artifacts in the public repo. |
| Integration tests | Disposable PostgreSQL integration tests. | shipped | Disposable database tests exist for core flows. | Add targeted integration coverage when graph/lifecycle work lands. |

## V2: Review And Visualization

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Unified operations dashboard | Unified dashboard for health, monitoring, watcher status, crawler stats, search, graph browsing, stale claims, contradictions, capture review, runtime settings, and mail ingestion status. | in progress | React/Vite dashboard served by FastAPI is the unified operations UI for health, corpus monitoring, runtime settings, mail capture, worker state, Outlook COM host status, claim lifecycle review, selected-entity graph browsing, retention tuning, memory quality reporting, capture-review queue decisions, and recent decision audit visibility. | Keep retention review exception-oriented for rare escalations, policy tuning, audits, and recovery once automatic retention workers exist. |
| Retention and quality | Retention policy tuning and memory quality reports. | shipped | Fixed retention classes for claim, episode, and corpus are tunable through REST, CLI, MCP, and the dashboard Review tab; updates are audited, and quality reports aggregate sanitized claim, episode, and corpus candidates without raw content or automatic mutation. | Calibrate policy defaults from live review feedback, then use them as guardrails for future automatic low-risk lifecycle tagging, deprioritization, and duplicate suppression while keeping public docs free of private runtime values. |
| Sensitive capture review | Manual approval flows for sensitive or low-confidence captures. | in progress | Capture and audit paths exist, and the dashboard now exposes a pending capture-review queue with approval/rejection actions, required rationale capture, sanitized responses, and recent `capture.review_*` audit visibility. Approved Codex backfill ingestion remains future work. | Continue live usage feedback and design the later ingestion worker before processing approved historical backfill content. |
| Drill-down diagnostics | Dashboard drill-down views for retrieval explanations, watcher events, and worker history. | in progress | Dashboard has operational panels and structured diagnostics; deeper drill-down remains uneven. | Add retrieval explanation, watcher event, and worker history drill-downs in roadmap order after core review workflows. |

## V2.5: Autonomous Corpus Expansion

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Watch roots and host bridge | Configurable recursive path monitoring plus local host-agent bridge for arbitrary host filesystem paths when API/dashboard run in Docker. | shipped | Host agent supports Windows/host paths, folder browse, validation, watch, sync, reconciliation, and host-side worker processing. | Continue compatibility validation across host platforms and sync clients. |
| Include/exclude policy | Global include/exclude glob defaults with per-root inherit, extend, or override behavior visible in the dashboard. | shipped | Settings catalog and dashboard expose effective root policy. | Keep policy explanations clear as file-type support expands. |
| Live watcher runtime | Reloadable enabled roots, debounce, bounded queues, heartbeat, stale-state reporting, and non-invasive watch semantics. | shipped | Watcher reload, heartbeat, stale-state reporting, stable-candidate gating, and non-exclusive semantics exist. | Broaden real-world validation against OneDrive, SharePoint, Dropbox, editors, build tools, and backup software. |
| Reconciliation and targeted sync | Startup and periodic reconciliation plus targeted file/subtree sync for watcher-triggered updates. | shipped | Startup/periodic reconciliation and targeted sync are implemented for enabled watched roots. | Add performance benchmarks for high-volume roots before V2.8 optimization work. |
| File-type coverage matrix | Broad file-type coverage through explicit support tiers and a dedicated coverage matrix. | in progress | Public `file-type-coverage.md` and broad roadmap targets exist; structured Draw.io, modern VSDX extraction, local business document expansion for Office variants, OpenDocument files, legacy Excel/PowerPoint adapters, and bounded archive/container member enumeration exist; many advanced formats remain metadata-first or optional-tool dependent. | Continue extractor expansion, prioritizing OCR/vision, ASR, embedded media, recursive containers, and remaining specialized local-tool stages. |
| Text/code/data extraction | Inline text extraction and local parser extraction for text, code, developer artifacts, structured data, and common exports. | in progress | Small text-like files and many local parser paths are supported; large tabular files need schema/profile/sample-first indexing before optional chunk backfill. | Add sample-first indexing and parser coverage where local libraries are reliable. |
| Documents, spreadsheets, and presentations | Local extraction for PDF, Office, OpenDocument, ebooks, scans, speaker notes, tables, and embedded media metadata. | in progress | Common PDF/Office extraction exists; Office macro/template variants, OpenDocument text/spreadsheet/presentation files, and legacy Excel/PowerPoint local adapters now have extraction paths. Scanned PDFs, ebooks, embedded media, and broader document families need deeper staged handling. | Prioritize scanned-PDF, ebook, embedded-media, and remaining local-tool support with metadata-first fallback. |
| Mail and collaboration exports | Treat EML, MSG, mbox, maildir, calendar/contact, chat exports, transcripts, and attachments as first-class indexed relationships. | in progress | IMAP/Outlook mail enters a private spool and related-evidence grouping exists for mail results. PST/OST and broader collaboration exports remain optional/future. | Keep mail spool logical grouping while adding provider-specific mail hardening in V2.7. |
| Images, diagrams, and vector assets | Structural extraction, image hash cache reuse, local OCR, optional local vision descriptions, and metadata-first handling for design/vector assets. | in progress | Draw.io, embedded Draw.io SVG/PNG payloads, and modern VSDX/VSDM/VSSX/VSSM/VSTX/VSTM containers are parsed locally into diagram chunks. Local OCR/vision stages still need bounded workers and dashboard controls before default use. | Add image hash caches, decorative-image skips, bounded OCR/vision job metadata, and deeper vector/design extractors. |
| Audio, video, and subtitles | Sidecar transcript indexing, optional local ASR, media metadata, stale lock recovery, and semantic media backfill. | planned | Media sidecars and metadata are first-class targets; transcription/frame sampling remains deferred. | Add local ASR/transcript worker design with cache keys, progress, and bounded temp extraction. |
| Archives and containers | Bounded archive/container expansion with depth, size, and file-count caps. | in progress | ZIP-family, TAR-family, gzip/bzip2/xz streams, and supported package containers are enumerated through bounded local adapters; optional-tool formats report explicit dependency states when local tools are missing; inline-safe text/code members become related child assets. Recursive extraction and rich parsing of embedded documents/media remain future work. | Add recursive extraction policy, richer embedded-member parsing, and broader optional-tool validation after worker-family scheduling and observability mature. |
| Worker claiming and retry | Low-priority bounded workers with `FOR UPDATE SKIP LOCKED`, retry/cooldown tracking, and no cloud/provider calls by default. | shipped | Deferred workers claim jobs, track retries/cooldowns, and use explicit terminal states. | Extend queue families for heavy extractor stages in V2.8. |
| Duplicate and version suppression | Duplicate suppression by content hash, conservative same-document/version-family suppression, and semantic near-duplicate grouping in retrieval. | in progress | Exact duplicate suppression, conservative path/title version-family suppression, and advisory semantic duplicate clusters for corpus chunks, episodes, and claims exist. Semantic clusters choose canonical members, suppress noncanonical retrieval siblings, and expose sanitized counts without automatic deletion. | Calibrate semantic thresholds from live review feedback, then feed clusters into V3 evaluation and automation-first librarian workers. |
| Corpus retrieval | Full-text, fuzzy, pgvector chunk embeddings, trust rank, freshness, deletion state, and canonical duplicate filtering. | shipped | Corpus retrieval combines these signals, including freshness reranking, and suppresses deleted/non-canonical duplicate assets. | Improve query-aware snippets and retrieval explainability in V2.7. |

## V2.6: Mail Capture And Runtime Configuration

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Runtime settings catalog | Settings catalog-backed runtime settings with dashboard editing, environment override visibility, masked secrets, audit events, and confirmation-gated apply actions. | shipped | Settings catalog, dashboard editing, masking, audit events, and apply coordination exist. | Keep new settings catalog-backed and avoid implying Windows Registry usage. |
| Terminology cleanup | Public docs and dashboard wording should not imply Windows Registry usage. | shipped | Settings are documented as cross-platform catalog definitions plus local PostgreSQL overrides. | Preserve this wording in future settings docs. |
| Dashboard forms | Dashboard forms for settings edits, mail profile creation, Gmail OAuth setup, confirmation-gated apply actions, validation errors, and mail/token status. | shipped | Dashboard forms exist for settings, mail profiles, OAuth, validation, and status. | Improve action copy as provider-specific mail policies are added. |
| Dashboard delivery | React/Vite operations console served by FastAPI at `/dashboard`, with raw JSON only in developer/debug contexts. | shipped | FastAPI serves the bundled dashboard; raw JSON is diagnostic rather than primary monitoring UI. | Keep primary operator flows in the dashboard, not raw JSON panels. |
| Production deployment | Production PC deployment under `D:\FluxLLMKB` with repo-independent app, private, data, logs, runtime, and backup directories plus install/update/start/stop/status scripts. | shipped | Production scripts and separated runtime layout exist under a configurable install root. | Keep closeout/deploy scripts aligned with layout changes. |
| Docker control plane | Docker-hosted PostgreSQL/pgvector, FastAPI, REST APIs, dashboard assets, IMAP worker, crawler, and normal extraction workers. | shipped | Docker hosts the normal Flux API/dashboard/worker runtime. | Keep host-only actions routed through host agent. |
| Gmail OAuth and IMAP capture | Gmail installed-client OAuth, token refresh before IMAP XOAUTH2 login, token health reporting, blocked auth states, TLS IMAP capture, UID cursors, optional IDLE, and reconciliation. | in progress | Gmail OAuth, profile-scoped IMAP capture, token health, blocked auth states, and scheduler reconciliation exist. Broader live-provider validation should continue across Gmail and standards-compliant IMAP servers. | Broaden live-provider validation and keep auth-required/expired states actionable. |
| Mail post-processing defaults | Safe defaults to move/remove capture label or move to processed folder; permanent trash/delete opt-in and confirmation-gated. | shipped | Provider-specific policies exist for Gmail labels/trash and generic IMAP copy/delete/expunge, with dry-run, audit events, dashboard visibility, and destructive-action confirmation. | Continue live-provider validation across Gmail and standards-compliant IMAP servers. |
| Outlook COM catch-up | Classic Outlook COM catch-up for selected mailbox folder paths, with a separate Windows host process, heartbeat, blocked-state reporting, and request claiming. | in progress | Separate Outlook host process model and dashboard status exist. Broader real-world Outlook catch-up verification remains a Windows-host validation task. | Validate Outlook catch-up across selected-folder scenarios and blocked dependency states. |
| Mail dashboard controls | Dashboard controls for IMAP worker state, Outlook COM host state, per-profile schedule fields, manual sync requests, last sync, next sync, backlog, and errors. | shipped | Dashboard exposes mail worker state, scheduler counts, profile sync controls, Outlook state, backlogs, errors, post-process policy fields, dry-run, and recent post-process outcomes. | Add deeper per-run debug detail only when operator workflows need it. |
| Profile-scoped OAuth | Profile-scoped Gmail OAuth actions and status for multiple IMAP/Gmail accounts. | shipped | Multiple profile-scoped OAuth controls and status exist without floating global OAuth controls. | Keep OAuth diagnostics profile-scoped. |
| Unified private mail spool | Unified private spool for IMAP and Outlook exports; Flux indexes only `ready` and ignores `_inflight`. | shipped | IMAP and Outlook exports use a private spool with `_inflight` and `ready` states. | Keep public docs free of raw mail, spool data, and local runtime values. |
| Consumer access | Read-only lookup endpoints for REST, MCP, and CLI consumers, including search, brief, asset lookup, and chunk lookup. | shipped | REST/CLI/MCP consumer search and brief access plus corpus asset/chunk lookup exist. | Maintain compatibility while adding explainability fields. |

## V2.7: Mail And Retrieval Production Hardening

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Search result actions and previews | Search results should open useful logical representations, including sanitized mail views and host-agent file actions. | shipped | Dashboard search result actions, sanitized in-app mail viewing, file text previews, host-agent open/reveal actions, copy path, action states, and related-evidence grouping exist. | Keep host-agent validation/audit strict as more file actions are added. |
| Logical related evidence | Archive members, mail attachments, embedded objects, and sidecars should appear under parent logical results. | in progress | Mail spool siblings and known child assets are grouped as related evidence. Archive and embedded-object grouping needs broader extractor support. | Extend related-evidence grouping as archive/container and embedded media extraction lands. |
| Lock-tolerant indexing and cloud-sync coexistence | Read-only/shared access, temporary extraction snapshots, no exclusive file ownership, cloud-sync edge handling, optional VSS, and compatibility tests. | in progress | Watcher stability gating, `pending_stable`, `retrying_locked`, `blocked_locked`, retry/cooldown, VSS settings/capability reporting, and fallback to retry/cooldown exist. Actual VSS snapshot extraction and broad cloud-sync compatibility proof remain. | Validate OneDrive/SharePoint/Dropbox, open Office files, large writes, and editor save/rename patterns; then implement opt-in VSS extraction. |
| Mail post-process hardening | Provider/profile-specific mailbox actions, explanatory dashboard copy, dry-run/audit records, and retry-safe handling. | shipped | Explicit policies now cover no-op, Gmail remove label, Gmail move label, Gmail trash, generic IMAP move, and confirmation-gated delete/expunge. Sync records post-process events, surfaces failures, preserves exported spool data, and avoids advancing the cursor past failed UIDs. | Continue live-provider validation and keep raw mail content out of audit views. |
| Retrieval snippets and explainability | Query-aware snippets, highlighted terms, retrieval streams, raw ranks, source trust, freshness, duplicate/version suppression, lifecycle penalties, and configurable filters. | in progress | Query-aware snippets, highlight ranges, search-result explanation metadata, per-query retrieval filters, filter-exclusion traces, sanitized exact/version/semantic suppression metadata, and brief-packing traces are exposed through REST, MCP, CLI, and the dashboard. Deeper score/confidence separation and automated-action rationale remain planned. | Add deeper explanation for deprioritization, escalation, and automated lifecycle actions after V3 evaluation foundations land. |
| Scheduled sync and worker reliability | First-class IMAP scheduler state machine with claimed/running/completed/failed runs, drift, retry cooldown, auth blocks, ownership, missed-run reconciliation, and tests. | shipped | Claimable IMAP scheduled sync runs, lifecycle state, run history, drift/missed-run fields, owner/attempt metadata, health diagnostics, and dashboard scheduler counts are visible. | Continue live-provider validation for tight intervals across Gmail and standards-compliant IMAP providers. |
| Error diagnostics and operator UX | Standard API error envelopes and dashboard alerts with code, severity, component, target metadata, retryability, user action, technical detail, and links. | in progress | Structured API error envelopes, dashboard actionable diagnostics, expandable details, copyable JSON, and navigation targets exist. Deeper operator debug views remain. | Add debug views for mail sync runs, retrieval explanations, watcher events, worker heartbeats, and post-process outcomes. |

## V2.8: Indexer Acceleration And Local Inference Optimization

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Acceleration lane | Make high-volume indexing fast and predictable on a single PC without requiring GPU or heavyweight media tooling. | planned | Current extractors detect some optional tools, but there is no cohesive acceleration lane. | Design the V2.8 acceleration architecture before implementing worker changes. |
| Hardware capability detection | Detect CPU, memory pressure, disk hints, NVIDIA/CUDA, ONNX Runtime providers, DirectML/OpenVINO, and local model servers; expose capability and blocked-runtime reasons. | planned | Some optional tool detection exists, but hardware/provider routing is not implemented as a unified capability model. | Add capability detection, dashboard Health/Settings exposure, CPU/GPU policy settings, and optional local-only Llama/Gemma routing through configurable local model backends such as Ollama or llama.cpp-compatible services. |
| Permanent cache and model layout | Keep dependency, model, OCR, ASR, vision, thumbnail, parser caches, and generated sidecars under the production install root across deploy updates. | planned | Cache/model layout visibility and reuse are planned. | Define cache directories, cache hit/miss metadata, model warmup, lazy load/reuse, and unload behavior. |
| Resource-aware worker scheduling | Split queues by job family and locality with concurrency caps, priorities, rate limits, backpressure, cooldowns, and time budgets. | planned | Current workers are bounded but not yet split by all heavy extraction families. | Add queue families for text/parser, Office/PDF, OCR, vision, ASR, embeddings, archive expansion, and preview generation. |
| OCR, image, diagram, and vision acceleration | Prefer structural extraction before OCR; add image hash caches, decorative skips, batching, thresholds, language routing, and local provider chains. | planned | Rich OCR/vision stages are planned and local-first; cloud OCR/vision remains off by default. | Implement structural extraction and cache-backed OCR/vision jobs with local provider fallback. |
| Audio/video transcription acceleration | Reuse sidecar transcripts, then run local deferred transcription with ffmpeg/ffprobe or bundled equivalents and faster-whisper/CTranslate2. | planned | Sidecars are recognized as targets; ASR pipeline is not implemented as a cohesive worker stage. | Add transcript metadata, stale lock recovery, progress reporting, segment diagnostics, and bounded temp audio extraction. |
| Embedding and vectorization throughput | Batch embeddings by model/provider/hardware target, support optional accelerated providers, and bulk upsert vectors. | planned | Deterministic lightweight embeddings exist for tests/offline bootstrap; accelerated batching and bulk vector updates remain planned. | Add batching, model/version metadata, chunk hash checks, and bulk pgvector upserts. |
| Local model-assisted knowledge optimization | Use local model backends only when available to enrich memory governance and indexing decisions. | planned | Local inference routing is not yet implemented as a shared provider layer for knowledge optimization. | Let optional local Llama/Gemma-class models assist librarian-worker proposals, semantic clustering, contradiction checks, canonical-summary drafts, and audit rationale generation without remote calls; fall back cleanly to rule-based behavior when unavailable. |
| Native and incremental filesystem performance | Evaluate native watcher backends, incremental scan manifests, prefilters, content-hash caches, bounded parallel hashing, and temporary snapshots. | planned | Watch/reconciliation exist; high-volume performance work remains planned. | Benchmark current watcher/indexer behavior, then add native watcher and incremental scan optimizations where proven useful. |
| Observability and benchmarks | Dashboard panels and benchmark fixtures for throughput, latency, cache hits, model warm/cold state, CPU/GPU mode, blocked dependencies, slow files, and p50/p95 indexing times. | planned | No cohesive V2.8 benchmark suite exists yet. | Add fixtures for text-heavy, Office/PDF-heavy, image-heavy, and audio/video-heavy roots with before/after metrics. |

## V3: Scale And Evaluation

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Historical Codex backfill | Historical Codex backfill with redaction. | planned | Codex capture exists, but historical backfill is not production-ready. | Design redaction-first backfill after V1 lifecycle and V2 review workflows are stronger. |
| Retrieval benchmarks | Retrieval benchmark suite. | planned | Benchmarks are planned; V2.8 indexing benchmarks should land first for corpus throughput. | Define query sets and quality metrics for retrieval precision, recall loss, contradiction reduction, brief dilution, and librarian-worker shadow-mode evaluation after retrieval explainability work starts. |
| Optional search backend | Optional ParadeDB/BM25 path. | planned | Not started. | Evaluate only after baseline retrieval benchmarks exist. |
| Librarian workers | Automation-first local librarian workers for derived-memory cleanup, consolidation, and linting. | planned | Not started. | Start with rule-based and model-assisted candidate generation for stale, redundant, low-utility, contradictory, unscoped, and low-confidence memories; run in shadow mode before broader auto-apply. |
| Automated memory governance | Policy-gated automation for routine memory lifecycle optimization. | planned | Retention quality reporting and lifecycle primitives exist, but automatic mutation is not implemented. | Automatically apply low-risk reversible actions such as duplicate suppression, retrieval deprioritization, stale tagging, canonical cluster presentation, and lifecycle updates after evaluation thresholds are met. |
| Local consolidation and escalation | Local-only consolidation with rare human escalation. | planned | Canonical semantic/procedural consolidation and escalation policy are not implemented. | Use local model-assisted consolidation only when evidence is high-confidence and provenance is preserved; escalate only for hard deletion, privacy/security findings, high-authority contradictions, destructive policy changes, protected memories, low-confidence high-impact decisions, or failed evaluation thresholds. |
| Operator digests | Periodic reporting for automated memory governance. | planned | Review UI exists, but automation digests are not implemented. | Provide periodic digests and recovery/audit views for operator awareness instead of per-item approval. |

## V4: Collaboration And Transfer

| Piece | Roadmap Intent | Status | Current Evidence / Remaining Gap | Queued Next |
| --- | --- | --- | --- | --- |
| Team/shared vault mode | Team/shared vault mode. | planned | Personal/local-first mode remains the focus. | Define trust, visibility, and audit boundaries after single-user governance matures. |
| Sync and export policies | Sync and export policies. | planned | Export exists for local audit; multi-user sync/export governance is not started. | Design policy model after shared vault requirements are clear. |
| Optional graph backend | Optional Apache AGE graph backend. | planned | PostgreSQL remains the primary store; optional AGE is not started. | Evaluate after graph traversal and lifecycle semantics stabilize. |
| Synthetic data and fine-tuning | Synthetic-data and fine-tuning pipeline. | planned | Not started. | Defer until evaluation and governance foundations are in place. |

## Queued Work In Roadmap Order

1. Design and implement the V2.8 acceleration foundation: hardware and local
   model-server capability detection, permanent cache layout, worker-family
   queues, resource caps, vector batching, native watcher evaluation, and
   throughput telemetry.
2. Build heavy extractor expansion on top of the V2.8 foundation: OCR/vision,
   ASR, embedded-media parsing, recursive containers, and remaining specialized
   local-tool stages.
3. Add V3 retrieval benchmarks and governance evaluation: query sets, quality
   metrics, shadow-mode librarian evaluation, and thresholds for automated
   lifecycle actions.
4. Add automation-first librarian workers for reversible low-risk stale tagging,
   deprioritization, duplicate suppression, canonical cluster presentation,
   audit recovery, and operator digests.
5. Defer V4 collaboration/shared-vault design until single-user governance,
   evaluation, and recovery flows are stable.

## Update Rules

- Update this file in the same commit as any roadmap-significant feature.
- Keep roadmap status factual and conservative.
- Prefer `in progress` over `shipped` unless the piece has working code,
  documentation or UI where applicable, and verification.
- Link to docs, tests, or commits when a status changes materially.
- Never record private runtime state, private file paths, mail contents, tokens,
  raw memories, embeddings from private content, or database dumps.
