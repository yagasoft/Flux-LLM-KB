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
- `capture_jobs`: asynchronous ingestion, review, and consolidation jobs,
  including corpus worker-family metadata, resource class, priority, time
  budget, duration telemetry, capture-review lifecycle state, and metadata-only
  approved-ingestion status.
- `workspace_scopes`: workspace/project identity and visibility boundaries.
- `retention_policies`: decay and forgetting configuration by memory class.
- `monitored_roots`: opt-in local paths for recursive corpus crawling and watch mode.
- `source_assets`: file-level corpus records with metadata, hashes, extraction state,
  and duplicate/canonical tracking.
- `asset_chunks`: extracted text/code/document snippets for retrieval without turning
  every file into an interaction episode.
- `code_symbols` and `code_references`: parser-derived code definitions,
  imports, calls, routes, SQL objects, and configuration facts tied back to
  `source_assets` and `asset_chunks`.
- `code_retrieval_feedback_events`: privacy-safe code retrieval miss evidence
  with root names, stable scope/query/symbol hashes, safe filename leaves,
  categories, counts, and timestamps; raw queries, paths, snippets, code, and
  embeddings are not persisted.
- `crawl_runs`, `crawl_path_manifests`, `watcher_state`, and `watcher_events`:
  crawler statistics, per-root/path scan fingerprints, watcher heartbeat,
  event counters, sanitized event rows, and error state for dashboard
  monitoring.
- `acceleration_benchmark_runs`: metadata-only synthetic and aggregate scoped
  benchmark history for fixture names, benchmark modes, labels, comparison
  labels, scope type, stable scope hashes, deployment labels, sanitized build
  and settings snapshots, model/tool readiness telemetry, pass indexes, hash
  parallelism, worker counts, manifest skip counts, timings, throughput, cache
  counters, warm/cold state, worker-family breakdowns, watcher probe summaries,
  and previous-run deltas.
- `retrieval_benchmark_runs`: metadata-only retrieval-quality benchmark history
  for synthetic suite names, labels, comparison labels, query counts,
  passed/failed case counts, aggregate metrics, sanitized case ids, stable query
  hashes, ranks, result ids, stream/kind labels, reasons, case categories,
  confidence bands, score evidence, calibration summaries, advisory threshold
  candidates, governance-shadow proposal counts, guardrail summaries, and
  previous-run metrics/deltas. It must not store raw query text, snippets,
  private content, credentials, embeddings, or private watched roots.
- `memory_governance_runs`, `memory_governance_actions`,
  `memory_governance_digests`, and `memory_governance_policy_snapshots`:
  sanitized governance proposal runs, reversible action records, local digest
  read models, and effective policy snapshots. They store guardrail evidence,
  rationale, before/after state, actor, status, risk, source, audit ids when
  available, `settings_mutated: false`, and whether memory lifecycle state was
  actually mutated. They must not store raw memory text, private paths, raw
  queries, snippets, embeddings, local model prompts, or local model outputs.
- `operator_automation_runs` and `operator_automation_actions`: durable
  guarded automation run/action history for safe recurring dashboard actions.
  Rows store actor, trigger, mode, dry-run state, status, sanitized evidence,
  planned/executed action names, and `settings_mutated: false`. They must not
  store raw memory text, private paths, mail bodies, credentials, embeddings, or
  local file-open/reveal targets.
- `runtime_settings`, `runtime_setting_events`, `runtime_components`, and
  `runtime_control_requests`: settings catalog-backed configuration, audit trail, and
  reload/restart/reindex coordination.
- `mail_profiles`, `mail_messages`, `mail_post_process_events`, and
  `mail_sync_runs`: IMAP and Outlook COM capture profiles, per-message export
  and post-process state, cursors, errors, claimable IMAP scheduler runs,
  provider-specific mailbox action audit records, drift/missed-run metadata,
  backoff state, and sync history.
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
Code-aware retrieval adds an exact symbol stream over `code_symbols`, preserves
the normal corpus chunk result shape, and exposes code metadata in retrieval
explanations. Callers can keep using `kb.search`, REST search, and CLI search
while narrowing by file kind, language, symbol kind, relationship, path glob,
and monitored root.
Embedding rows carry redacted provider metadata such as model, dimensions,
source hash, and cache key, but not raw source text. Existing synchronous writes
still create vectors for new episodes, claims, and chunks, while `corpus_embed`
jobs can batch-refresh missing or stale vectors for corpus chunks, episodes, and
claims through the same local deterministic provider boundary.
Semantic near-duplicate clusters are stored as advisory metadata in
`semantic_duplicate_clusters` and `semantic_duplicate_members` for corpus
chunks, episodes, and claims. Refreshes retire prior active clusters and create
new active metadata clusters from local embeddings; they do not delete or modify
the underlying memories. Retrieval suppresses only noncanonical members of
active semantic clusters and exposes sanitized cluster counts, paths, and
canonical identifiers when callers request suppressed metadata.
Retrieval explanations keep ranking score separate from confidence. Search and
explain results may include `retrieval_explanation.confidence` with stable bands
(`high`, `medium`, `low`, or `insufficient_evidence`) plus sanitized factors
such as rank margin, stream mix, exact/path/symbol match, local scope match,
lifecycle penalties, and suppression signals. Results can also include
`retrieval_explanation.deprioritization` when lifecycle, retention, or brief
packing penalties affected ranking or packing. Semantic duplicate suppression
is surfaced alongside exact duplicate and version-family suppression without
returning raw suppressed content.

Retrieval evaluation uses deterministic public-safe synthetic cases to exercise
search, explain, brief packing, scope filters, duplicate suppression,
current-only retrieval, lifecycle-deprioritized evidence, semantic duplicate
guardrails, and code retrieval. The standard suite records top-1 accuracy,
precision@3, recall@5, MRR, nDCG@5, brief recall, brief dilution, scope pass
counts, suppression pass counts, elapsed time, metric deltas, confidence-band
summaries, sanitized failed case evidence, and semantic duplicate threshold
candidates. The `governance-shadow` suite adds metadata-only synthetic cases for
stale, apply/recover, stale-proposal conflict, duplicate-cluster,
capture-ingestion, feedback-gap, contradicted, low-confidence,
protected/current, and false-positive guardrail scenarios. It runs a read-only
proposal evaluator over retention-quality, lifecycle, contradiction, duplicate,
capture-ingestion, and feedback-gap evidence and stores candidate counts,
categories, guardrail pass/fail counts, precision-style summary metrics, and
sanitized failed cases with `settings_mutated: false`. Benchmark outputs are
evidence for later calibration and governance apply gates; they do not mutate
ranking, thresholds, retention policy, semantic clusters, lifecycle state, or
settings.

## Memory Governance Automation

Governance automation is an evaluated proposal layer over existing memory
quality signals. A governance run persists sanitized proposals from retention
quality, claim lifecycle state, active semantic duplicate clusters,
capture-ingestion outcomes, code retrieval feedback summaries, and the latest
`governance-shadow` benchmark evidence. Proposal de-duplication uses
target/action keys so repeated runs do not spam the operator with the same open
work.

Actions are deliberately narrow: `mark_review`, `stale_tag`, `deprioritize`,
`retire`, `semantic_cluster_apply`, `canonical_cluster_promote`,
`capture_ingestion_recheck`, `feedback_gap_escalate`, and recovery. Hard delete
is not a governance action. Apply is blocked unless the latest persisted
`governance-shadow` benchmark has zero guardrail failures and proposal precision
meets `governance.librarian.min_shadow_precision` (default `0.80`). Protected
memories, high-risk actions, retire, contradiction handling, canonical cluster
promotion, and local-model-only recommendations require manual confirmation.

Apply and recover are idempotent. Claim lifecycle actions capture before-state,
mutate only through existing lifecycle transition/restore paths, append
audit-visible events, and can be recovered from stored before-state. Non-claim
governance actions are presentation or operator-workflow records until a later
explicit implementation mutates their target subsystem; they still append audit
events when applied or recovered. If the target state changed since proposal,
the action is marked `skipped_conflict` and a new shadow run is required.

The librarian worker integration is default-off. When enabled it runs
governance on cadence through `run_corpus_worker`; it remains shadow-only unless
settings explicitly request auto mode and enable auto-apply. Even then,
auto-apply is limited to low-risk `mark_review`, `stale_tag`, and
`deprioritize` claim actions that pass the benchmark gate and protected-memory
rules. Governance actions never mutate runtime settings, and every response
reports `settings_mutated: false`.

Optional local-model rationale settings are loopback-only and advisory. When a
local model is unavailable or disabled, deterministic rule-based rationale and
canonical-summary drafts are used instead. Digests are local read models for the
dashboard/API/CLI/MCP only; this layer does not send outbound email, webhooks,
or notifications.

## Capture Review And Backfill

Capture review responses are metadata-only. Review listing supports
`pending_review`, `approved`, `rejected`, `completed`, `failed`,
`blocked_missing_dependency`, and `all`; the default remains `pending_review` so
existing operator workflows stay conservative. Review decisions store sanitized
decision metadata in `capture_jobs.payload.review`, update the job lifecycle,
and append audit-visible `capture.review_approved` or
`capture.review_rejected` events without returning raw capture text.

Approved Codex backfill ingestion is an explicit operator action. It processes
approved `codex_backfill` review jobs in bounded batches, with dry-run,
single-job, and limit-based modes. Inputs are bounded before parsing and may be
`.json`, `.jsonl`, `.md`, or `.txt`; each parsed record normalizes to a title,
body, source leaf, stable source hash, optional session/turn/workspace metadata,
and truncation flags. Flux redacts before persistence, skips empty or noisy
records, skips duplicate source hashes, and marks missing or unreadable sources
as `blocked_missing_dependency`.

Successful approved ingestions write durable episodes through
`KnowledgeService.remember` with provenance metadata for `source=codex_backfill`,
the review job id, review audit id when present, source hash, source leaf,
workspace/cwd/root hints, and redaction/truncation counts. Ingestion outcomes are
stored under `capture_jobs.payload.ingestion` and surfaced as sanitized status,
skip reasons, created memory ids, and recent audit events. Raw backfill text is
never exposed in review, dashboard, REST, CLI, or MCP status responses.

## Corpus Monitoring

Configured roots are crawled recursively according to root policy, `.gitignore`,
`.fluxignore`, `.fluxkbignore`, and `.exclude.codex` markers. Metadata is recorded
for every supported file type. Sync can target a full root, a subtree, or a
single file. Small text-like files are extracted and chunked locally; heavy
documents, images, audio, video, archive members, and practical export/report
formats are queued for local deferred processing.
Images are dimensioned locally, decorative-image spacers are skipped before
heavy enrichment, and optional local loopback vision descriptions run only when
`acceleration.vision.enabled` and a local model are configured. Media uses
sidecar transcripts, `ffprobe` metadata, scene-transition video frame sampling
with thumbnail cache reuse, and optional local faster-whisper ASR when
`acceleration.asr.model_path` points at an existing local model. EPUB and FB2
publications are parsed locally, MOBI/AZW/LIT use local Calibre `ebook-convert`
when available, and comic archive formats reuse bounded container extraction.
Draw.io and modern
VSDX/VSDM/VSSX/VSSM/VSTX/VSTM diagrams are parsed structurally. Bounded
archive/container extraction records related child assets, recursively expands
safe nested containers up to the configured depth, and routes embedded
documents, diagrams, images, audio, video, subtitles, mail exports,
calendar/contact files, structured data, reports, SQLite databases, and
metadata-first domain formats through the same local extractor chain from
temporary private files. Embedded media sidecar transcripts inside archives are
used before media probing or ASR. Unknown binaries remain metadata-only only for
pilot roots that explicitly allow metadata-only discovery.

File coverage is intentionally broad but tiered. Flux should first record stable
metadata for every encountered file: path, size, timestamps, hashes, MIME/signature,
source root, trust rank, and provenance. Extraction then escalates only when a
safe local path exists: inline UTF/code parsing; local document/data libraries;
optional local tools such as LibreOffice, Calibre `ebook-convert`, Tesseract,
ffprobe/ffmpeg, or faster-whisper; bounded archive/container expansion; and
finally metadata-only
terminal states for unsafe, encrypted, proprietary, or unsupported binaries.
The detailed target matrix lives in [file-type-coverage.md](file-type-coverage.md)
and explicitly includes common legacy and diagram formats such as `doc`, `xls`,
`ppt`, Draw.io, Visio `vsdx`, EPUB, FB2, and comic archive formats. This lets
Flux cover common text, code, office, PDF, spreadsheet, presentation,
publication, mail, calendar/contact, image, diagram, audio, video, subtitle,
archive, database/export, notebook, CAD/BIM/GIS/design,
security scan, operations log, and unknown-binary families without requiring
cloud services or blocking normal watch/crawl loops.

Go-live roots should set `strict_indexing=true` in root metadata, usually via
`flux-kb crawl add <path> --name <root> --strict-indexing` or `flux-kb crawl edit
<root> --strict-indexing`. Under strict indexing, foreground scans and deferred
workers convert metadata-only extraction outcomes to `blocked_missing_dependency`
with diagnostic metadata, and corpus retrieval excludes any remaining legacy
`metadata_only` assets. Operators must either install the missing local extractor
or exclude that file family with glob policy before treating the root as ready.

Code-like files use parser-backed chunking when a reliable local parser exists.
The first parser layer uses Python `ast` for modules, classes, functions,
methods, imports, calls, and common route decorators; conservative local pattern
parsers cover SQL objects, JavaScript/TypeScript symbols/routes, notebook
cells, generated-code markers, and common configuration/manifests. Parser
failures and unsupported code-like files still index as redacted fallback
chunks with sanitized parser status metadata.

Large structured files use sample-first indexing before any full-file backfill.
For CSV, TSV, PSV, SSV, JSON, JSONL, NDJSON, JSON-LD, and
OpenPyXL-supported workbook files, oversized inputs produce a bounded
schema/profile/sample chunk plus metadata such as
columns, row-count estimate, sample row count, parse status, source format,
sheet count where applicable, and truncation state. Legacy Excel and
OpenDocument spreadsheet adapters that convert through LibreOffice preserve the
source and converted extensions, then use the same sample-first workbook
profiling when the converted workbook is oversized. The sample-first path
avoids returning full tail rows or raw private dumps while still making large
data assets discoverable and diagnosable.

Practical local parsers cover common transcript, exchange, and report families
without adding heavyweight required dependencies. Subtitle files are cleaned
into transcript chunks without cue/timestamp noise. EML/MBOX mail exports,
ICS/VCF calendar/contact files, SARIF, SPDX, CycloneDX, JUnit-style XML, TRX,
TAP, LCOV, coverage XML, HAR, and SQLite schema metadata use bounded summaries
and sanitized parser-count metadata. SQLite extraction is schema-only by default
and does not index table rows.
Proprietary CAD/BI/geospatial/scientific/database formats remain
metadata-first until a safe local parser or local tool stage is implemented.

When the API/dashboard is Docker-hosted, arbitrary Windows/macOS/Linux host
paths are accessed through a separate local host agent (`flux-kb host-agent run`).
The dashboard can ask that host process to open a native folder picker, validate
the selected path on the host OS, and execute host-side sync/watch work against
the same PostgreSQL database. Docker never interprets Windows drive paths such
as `E:\Projects` with Linux path rules.

Global crawler include/exclude globs live in the settings catalog. Each
monitored root chooses `inherit`, `extend`, or `override`; the dashboard shows
both root-local globs and the effective policy used by crawl/watch.
Container caps are also catalog settings. `crawler.container_max_depth`,
`crawler.container_max_members`, `crawler.container_max_total_bytes`, and
`crawler.container_max_member_bytes` apply consistently to foreground sync and
deferred worker extraction.

The media backfill path is deliberately local and staged. Flux should prefer
cheap structural signals first: file hash caches, dimensions, SVG/draw.io
and modern Visio structure, sidecar transcripts including embedded media
sidecar files from archives, and decorative-image skips.
Optional richer stages can then run as bounded jobs: Tesseract or PaddleOCR OCR,
configured loopback local inference for image descriptions, scene-transition
frame sampling with thumbnail cache reuse, and faster-whisper audio/video
transcription. Vision requires `acceleration.vision.enabled`, a configured
local vision model identifier in `acceleration.vision.model`, and
`acceleration.local_inference.*` pointing at a healthy loopback local provider.
The first implemented vision runtime uses an Ollama-compatible API, so local
Gemma-class vision models can be selected by model tag when that runtime has
them installed. ASR requires `ffmpeg`, the `faster-whisper` Python
module, and `acceleration.asr.model_path`; Flux passes `local_files_only=True`
and never performs a remote model download. Redacted ASR cache entries live
under the ASR cache directory and expose cache hit/miss plus segment telemetry.
Redacted vision cache entries live under the vision cache directory, and sampled
frame images live under the thumbnail cache directory.
Cloud transcription remains off by default. Semantic media embeddings are a
separate backfill phase so large media files do not slow normal crawl/watch
loops.

Deferred workers claim jobs with `FOR UPDATE SKIP LOCKED`, use retry/cooldown
state in `capture_jobs`, and do not call cloud providers by default. Jobs move to
explicit terminal states such as `completed`, `metadata_only`, or
`blocked_missing_dependency`; locked reads move through `retrying_locked` with
`next_attempt_at` cooldown and then `blocked_locked` after configured attempts.
Corpus jobs are classified into fixed worker families (`text`, `office`,
`image`, `diagram`, `archive`, `media`, `embedding`, `preview`, and `general`)
with resource class, priority, and time budget metadata. Worker/backfill
commands translate `--kind` options into these families before claiming jobs,
including broader operator aliases such as `data`, `mail`, `reports`, and
`metadata`, so family-specific workers do not lock unrelated work. Claiming can apply
the configured `acceleration.worker_cap.*` map to cap concurrent running jobs per
family and expose worker-family backpressure, oldest pending age, retrying
locked counts, blocked locked counts, sanitized slow-job rows, parser cache
hits/misses, and manifest skip counters. `corpus_embed` jobs route to vector
refresh instead of file extraction and support owner class, optional root
scoping, stale-only refresh, and bounded limits. Completion, retry, and blocked
transitions record last duration and sanitized telemetry for queue
observability, including OCR/ASR cache counters, embedding vector/cache
counters, recursive container member, parsed-child, skipped-child, and
blocked-dependency counts, practical parser counts for mail, calendar/contact,
reports, BOMs, coverage, HAR, database schema extraction, sensitive metadata,
ASR segment totals, frame sample counts/timestamps, thumbnail cache counters,
stale-lock evidence, and blocked dependency reasons.
Files observed before their size/mtime fingerprint stabilizes are recorded as
`pending_stable` instead of failing the root crawl. Jobs are not completed merely
because they were claimed. Duplicate content is suppressed by content hash while
preserving every observed path and source asset record. Retrieval also applies a
conservative same-document/version-family collapse for common filename variants such as
`v1`, `v2`, `final`, dated copies, and copy suffixes. It suppresses sibling
versions only in result presentation and exposes the canonical path plus
suppressed sibling count.
On-demand semantic duplicate refresh extends this with embedding-similar
near-duplicate clusters across corpus chunks, episodes, and claims. The
canonical member is selected deterministically from local metadata such as trust,
confidence, reinforcement/usage, text size, recency, and stable identifiers.
This foundation is intentionally advisory; later librarian workers may propose
automated lifecycle actions, but this layer performs no hard deletion.

The watcher runtime reloads enabled roots while running, so `watch enable` and
`watch disable` take effect without a restart. It applies a stable-candidate
gate before emitting change events: the same size/mtime fingerprint must survive
the configured quiet window, and the timer resets while the file keeps changing.
Large files can use a longer quiet window. Deletes remain immediate, and the
runtime still keeps a bounded event queue, heartbeat recording, and stale-state
reporting. Live filesystem events are not the only correctness mechanism:
watcher services run startup reconciliation and periodic reconciliation for
enabled watched roots. A
reconciliation is a full-root sync recorded in `crawl_runs.reason` as
`startup_reconcile` or `periodic_reconcile`; it compares the current filesystem
snapshot with persisted `source_assets` hashes, marks deleted files as deleted,
queues changed deferred files, and treats empty folders as a clean no-op. Watch
events continue to use targeted sync with reason `watch_event`.

Watcher backend selection is explicit. `watcher.backend` accepts `auto`,
`watchdog`, or `polling`, and the `FLUX_KB_WATCHER_BACKEND` environment
override follows the normal settings precedence. `auto` chooses the native
watchdog backend when importable and records a polling fallback reason when it
is not. Explicit `watchdog` fails visibly if watchdog is unavailable; explicit
`polling` records `policy_polling`. The synthetic watcher probe creates,
updates, and deletes files only in a temporary directory. Probe payloads report
backend policy, selected backend, native/fallback state, fallback reason,
observed event counts, normalized actions, and latency without touching private
watched roots.

Incremental scan manifests are performance metadata, not a source of truth.
`crawl_path_manifests` stores root/path size, mtime, quick hash, content hash,
and sanitized metadata. When size, mtime, and quick hash match, the crawler
reuses the prior content hash, skips expensive content hashing and inline
extraction, and records `manifest_skipped_unchanged`; reconciliation still
persists the observed asset row and verifies deletions/changes. Bounded hash
parallelism is controlled by `crawler.hash_parallelism` and defaults to serial
hashing. When raised above one, the scanner precomputes changed-file content
hashes with bounded concurrency while keeping deterministic asset ordering,
manifest reuse, stability gating, lock fallback behavior, and local parser
extraction serial.

VSS is a host-agent controlled future fallback for Windows local NTFS roots, not
a Docker/API desktop action. The setting is disabled by default; the host agent
reports disabled or unavailable capability, size and timeout limits, and locked
files fall back to retry/cooldown states until snapshot extraction is
implemented. Future VSS use must be opt-in, permission-aware, audited, bounded
by file size and timeout, and must fall back cleanly when unavailable.

Production deployments are intentionally not repo-coupled. The default Windows
PC install root is `D:\FluxLLMKB`, with deployed app files under `app`, private
runtime/config/spool data under `private`, PostgreSQL bind-mounted data under
`data`, and logs under `logs`. Docker runs PostgreSQL/API/dashboard/worker from
prebuilt local images and bind-mounts only deployed runtime paths. Host-agent and
Outlook-host run as Windows Scheduled Tasks in the logged-in user session.

The V2.8 acceleration status model is read-only. It detects CPU count, Windows
memory when available, cache-root disk space, NVIDIA/CUDA through `nvidia-smi`,
optional ONNX Runtime providers, selected watcher backend policy/native state,
and optional local model servers. Local model probing is disabled by default
and accepts only loopback HTTP(S) URLs. The permanent cache layout is resolved from
`acceleration.cache_root`, `FLUX_KB_CACHE_ROOT`, `FLUX_KB_INSTALL_ROOT`, or the
user cache, and exposes named directories for models, OCR, ASR, vision,
thumbnails, parser output, embeddings, and temp files.

Benchmark history is durable and public-safe. Synthetic runs are generated from
temporary fixture trees (`text-heavy`, `code-heavy`, `office-pdf-heavy`,
`archive-container-heavy`, `image-heavy`, and `audio-video-heavy`). Aggregate
real-root calibration can also dry-run opted-in monitored roots or paths and
stores only scope type, a stable scope hash, optional operator labels, counts,
timings, and sanitized summaries. Stored records contain fixture names, counts,
mode, label, compare label, deployment label, pass index, timings, p50/p95/max,
throughput, warm/cold state, cache hit/miss counters, hash parallelism, worker
count, manifest skip counts, worker-family breakdowns, model/tool readiness
telemetry, comparable elapsed and throughput deltas, scenario metadata in
existing JSON fields, and sanitized summaries only.

Benchmark callers can pass `scenario=standard|reliability|host_cloud|
cache_readiness|tuning` through REST, CLI, MCP, and the host-agent proxy. The
response keeps the existing `runs[]` array and adds `scenario`, `diagnostics[]`,
and `recommendations.candidates[]`. `standard` preserves the older benchmark
shape with empty diagnostics and no automatic settings changes. `reliability`
summarizes file churn, warm manifest-skip proof, lock retry/block evidence, and
watcher reconciliation proof from the same scan/soak/watcher paths. `host_cloud`
requires a monitored-root or path scope and stores only aggregate scope hashes,
host access mode, and counts for Windows/OneDrive/SharePoint/Dropbox-style
delayed availability checks. `cache_readiness` summarizes cache-root presence,
cache directory count, local model readiness, and extractor/tool blocks without
storing cache paths. `tuning` runs bounded comparisons for crawler hash
parallelism and worker-family caps, returning manual candidates only.

The indexer reliability gate is a read-only interpretation layer over
`acceleration_benchmark_runs`, worker-family telemetry, watcher event summaries,
and monitored-root crawl summaries. It reports `ready`, `partial`, `blocked`, or
`not_run` readiness, required check status, latest run references, selected-root
cards, and evidence-scored manual candidates. It does not create a separate
evidence table, store private paths or raw content, mutate settings, or
automatically unblock VSS/provider-specific acceleration.

The multi-root reliability view applies that same interpretation across enabled
monitored roots and returns sanitized root cards plus readiness totals,
stale/missing scoped evidence, blocked job and asset counts, latest benchmark
references, and manual tuning candidates. The `all_roots` reliability run
orchestrates metadata-only synthetic reliability, scoped host/cloud evidence,
cache readiness, and tuning diagnostics for enabled roots when
`evidence_level=full` while preserving `settings_mutated: false`.

The operator evidence report is a read-only decision layer over reliability,
code diagnostics, and operational diagnostics. It reports the
`settings_mutated` field as `false`, plus root readiness totals, freshness,
latest benchmark references, top blockers, manual follow-up commands, and the
explicit `vss_snapshot` and `provider_acceleration` gates. Gate states are
`blocked`, `hold`, or `eligible_for_design`; they never enable VSS, provider
acceleration, worker caps, hash parallelism, or any setting automatically.

Code diagnostics are read-only and privacy-safe. They aggregate coverage from
`source_assets`, `asset_chunks`, `code_symbols`, and `code_references`, reporting
per-root language counts, parser status/fallback counts, generated-file counts,
definition/reference coverage, and slow/problematic code-index rows without raw
code content or private root paths. Dedicated code status/search/symbol lookup
surfaces reuse the stored symbol/reference tables and sanitize path output.
Code and generic corpus search filters accept `relationship`, `path_glob`, and
`include_generated`; generated files are excluded when
`include_generated=false` is part of the active filter set. Sanitized code
results can include `is_generated`, `relationship`, `target_symbol`,
`source_symbol`, `route`, `test_target`, `parser_status`, `language`,
`symbol_kind`, and line ranges. Code retrieval feedback records only
hashed/sanitized miss evidence and appears in `code status` as
`feedback_summary`, `gaps[]`, retrieval benchmark summary metadata, and
benchmark-derived code gap priorities when available.

Operational diagnostics aggregate retrieval explain traces, watcher events,
worker heartbeat/history, slow jobs, blocked dependencies, mail sync runs, and
mail post-process events into bounded dashboard/API evidence summaries rather
than raw log dumps. Diagnostic payloads support `root_name`, `status`, `family`,
`since_hours`, and `include_details` filters and include standardized evidence
items with section, severity, status, root name, summary, bounded evidence,
follow-up command, dashboard target metadata, and optional sanitized
`remediation_actions[]`. Remediation actions are confirmation-gated public
contracts for retrying eligible corpus jobs, running scoped root/family
backfill, repairing root-scoped asset statuses, or clearing stale completed-job
errors. They execute through REST, CLI, or MCP, append audit events, and always
report `settings_mutated: false`; they do not mutate runtime settings or expose
raw host paths. Raw mail bodies, private paths, credentials, embeddings, and
runtime dumps remain out of public-safe surfaces.

`scan` mode creates temporary fixtures or aggregate real-root dry-runs and can
run multiple passes; pass 1 is recorded as `cold`, later passes reuse an
in-memory manifest and are recorded as `warm`. `soak` mode creates
benchmark-tagged synthetic corpus jobs by worker family, claims them through the
same cap/backpressure logic as normal workers, completes or blocks them
deterministically, and purges the tagged jobs in cleanup. `watcher` mode runs
the temporary watcher probe and stores backend policy, selected backend,
fallback reason, event counts, and latency metadata. `model` mode records
local-only model/tool readiness, warm/cold timings, and blocked dependency
counts without running cloud providers. `all` mode runs scan, soak, and watcher
modes for the selected fixtures, and can include model probing only when
explicitly requested. Benchmark responses always include `settings_mutated:
false`; they never call settings mutation APIs. They never store raw text, mail
contents, credentials, embeddings, private cache roots, or private watched roots.

Example CLI diagnostics:

```powershell
flux-kb acceleration benchmark run --scenario reliability --mode all --passes 2
flux-kb acceleration benchmark run --scenario host_cloud --scope root --root docs --max-files 100
flux-kb acceleration benchmark run --scenario cache_readiness --mode model
flux-kb acceleration benchmark run --scenario tuning --mode scan --passes 2
flux-kb acceleration evidence --compare-label baseline
flux-kb acceleration reliability roots
flux-kb acceleration reliability run --scope all-roots --full --compare-label baseline
flux-kb code status --root docs
flux-kb code search build_invoice --root app --language python --relationship call --path-glob "src/*.py"
flux-kb code symbol OrderService.build_invoice
flux-kb code feedback add --query "redacted local query" --root app --miss-category missing_symbol --expected-symbol OrderService.build_invoice
flux-kb code feedback summary --root app
flux-kb diagnostics all --root docs --status blocked_missing_dependency --family office --include-details
flux-kb diagnostics remediate retry_corpus_job --target-type job --target-id <job-id> --root docs --family office --reason "dependency fixed"
flux-kb crawl backfill --root docs --family office --limit 20
```

The dashboard is the single UI surface for overview status, guarded automation,
diagnostics, performance evidence, watcher status, crawler stats, backlog,
errors, retrieval/index stats, runtime settings, mail ingestion status, and
graph/review/governance workflows. The UI is a React/Vite operations console
bundled into the Python package and served by FastAPI at `/dashboard`; raw JSON
payloads are diagnostic-only, not the primary monitoring surface.
Overview is read-only and friendly: system status, attention items, work Flux
handled automatically, and the next recommended safe action. Automation shows
Guarded Auto posture, eligible actions, manual-required work, last/next run, a
run-now control, and durable sanitized audit history. Diagnostics owns
structured operational errors, filters, copy/detail/navigation actions, and
confirmation-gated remediation buttons where the service has a safe scoped
recovery action. Performance owns operator evidence gates, acceleration
capability, all-root reliability, benchmark history, cache/model readiness, and
worker-family telemetry. Retrieval owns code diagnostics with feedback capture,
top code gaps, parser/fallback hotspots, generated-file counts, and direct code
search/symbol lookup controls. Settings owns Codex hooks, deployment, runtime
actions, restart requests, and reindex-required settings.
The Review tab includes claim/capture review plus Governance Automation,
Digest, Guardrails, and Recovery panels for high-risk proposals, blocked
guardrails, stale proposals, recoverable actions, and recent governance action
state.

REST errors preserve a readable `detail`/`message` string for existing clients
and also include a structured `error` envelope for operators. Envelopes carry a
stable code, severity, component, stage, retryability, user action, technical
detail, target metadata, links, and status code. Dashboard health keeps legacy
`recent_errors` strings and adds structured `recent_error_details`; the UI
renders those as actionable alerts with expandable detail, copyable JSON, and
navigation to the relevant profile, root, or job when available. Diagnostics
redact secrets only, so local paths, profile names, root names, and job ids can
remain visible on the local PC.

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
Governance settings are catalog-backed and follow the same precedence, but
governance apply/recover never mutates runtime settings; it mutates only memory
lifecycle state through reversible audited actions.
Operator automation settings are catalog-backed under `operator.automation.*`
and default to disabled guarded mode. Worker-scheduled guarded passes only run
when `operator.automation.enabled=true`; manual dashboard, CLI, REST, or MCP
runs can still execute the same bounded allowlist. Automation never mutates
runtime settings and records blocked/manual-required work instead of attempting
deletes, OAuth, host startup, restart/reindex settings, capture decisions,
high-risk governance, local file open/reveal, or ambiguous actions.

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
Post-processing is policy-driven per profile. Gmail profiles use Gmail IMAP
label commands for remove-label, processed-label, and Trash handling. Generic
IMAP profiles use COPY, delete flags, and EXPUNGE only for policies that require
moving or deletion, including optional `trash_folder` copy before source
deletion. Destructive trash/delete policies require explicit confirmation. Every
exported IMAP message records a post-process event; failure
preserves the ready spool export, surfaces in the sync run, and keeps the folder
cursor retry-safe by not advancing past the failed UID.

Scheduled IMAP sync is represented as explicit `mail_sync_runs` lifecycle state.
Due profiles are selected from enabled/sync-enabled profile settings and
`next_sync_at`, then claimed with database row locks so competing workers do not
process the same profile concurrently. Runs record queued, claimed, running,
completed, failed, auth-blocked, and backoff states, with worker ownership,
attempt count, errors, next attempts, and drift/missed-run fields for dashboard
and health diagnostics. Manual dashboard sync also creates an explicit run.

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
