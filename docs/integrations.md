# Integrations

Flux-LLM-KB exposes the same memory kernel through CLI, MCP, REST, and Codex
hooks.

## MCP

Install optional MCP dependencies:

```powershell
python -m pip install -e .[mcp]
python -m flux_llm_kb.mcp_server
```

Tools:

| Tool | Purpose |
| --- | --- |
| `kb.search` | Search Flux memory and corpus evidence, optionally scoped and filtered by workspace/root, evidence kind, lifecycle, or current-state policy, with optional confidence/deprioritization explanation metadata. |
| `kb.explain` | Search with query-aware snippets, ranking signals, confidence bands, deprioritization signals, filters, suppression metadata, and brief-packing rationale. |
| `kb.brief` | Build a compact task brief for non-trivial work. |
| `kb.remember` | Store a concise durable atomic save with optional workspace provenance; text masking follows `privacy.redactions.enabled`. |
| `kb.finalize_turn` | Store an end-of-turn summary for meaningful agent work; text masking follows `privacy.redactions.enabled`. |
| `kb.claim_upsert` | Create or update an atomic claim. |
| `kb.claim_transition` | Move a claim through lifecycle states with audit-visible rationale. |
| `kb.graph_traverse` | Traverse typed knowledge graph relations from an entity. |
| `kb.capture_review` | List capture-review jobs by sanitized lifecycle status without raw capture payloads. |
| `kb.capture_review_ingest` | Ingest approved Codex backfill review jobs in bounded dry-run or active batches. |
| `kb.capture_review_decide` | Approve or reject a capture-review job with rationale. |
| `kb.retention_policies` | List retention policies for claims, episodes, and corpus assets. |
| `kb.retention_quality` | Report retention and memory-quality candidates without raw content. |
| `kb.semantic_duplicates_refresh` | Refresh advisory semantic duplicate clusters for corpus chunks, episodes, or claims. |
| `kb.semantic_duplicates_list` | List active semantic duplicate clusters without raw suppressed content. |
| `kb.acceleration_status` | Return local capability, cache layout, and worker-family queue telemetry. |
| `kb.watch_probe` | Run a temp-directory watcher backend probe without touching private watched roots. |
| `kb.worker_status` | Return worker-family cap usage, backpressure, retry/lock, and slow-job status. |
| `kb.crawl_backfill` | Enqueue a bounded corpus backfill by kind or exact worker family, optionally scoped to one monitored root and callback URL. |
| `kb.benchmark_run` | Run deterministic synthetic scan, soak, watcher, or all-mode benchmarks and store metadata-only history. |
| `kb.benchmark_history` | List metadata-only synthetic benchmark history with mode, label, warm-state, and previous-run delta filters. |
| `kb.indexer_reliability_status` | Report metadata-only indexer reliability readiness from benchmark history and sanitized worker/watcher evidence. |
| `kb.indexer_reliability_run` | Run the indexer reliability validation suite without mutating settings. |
| `kb.operator_evidence` | Return combined reliability, code, and diagnostic evidence gates without mutating settings. |
| `kb.indexer_root_reliability` | Show a monitored-root reliability card with sanitized counts and latest scoped benchmark evidence. |
| `kb.indexer_reliability_roots` | Show sanitized multi-root reliability readiness for enabled monitored roots. |
| `kb.code_status` | Return privacy-safe code index coverage, parser/fallback, generated-file, and slow-row summaries; `cwd` can resolve the exact monitored root. |
| `kb.code_search` | Search code in `literal_symbol` mode for symbols/paths or `full_text` mode over indexed code chunks. |
| `kb.code_symbol_lookup` | Look up definitions and references for a code symbol. |
| `kb.code_feedback_record` | Record hashed/sanitized code retrieval miss feedback without raw query, code, or path persistence. |
| `kb.code_feedback_summary` | Summarize code retrieval feedback by category and root. |
| `kb.operational_diagnostics` | Return read-only retrieval, watcher, worker, job, and mail diagnostics with optional filters. |
| `kb.diagnostics_remediate` | Run a confirmation-worthy diagnostic remediation action such as retrying a corpus job, scoped backfill, or root cleanup; responses always report `settings_mutated: false`. |
| `kb.automation_status` | Return default-off guarded automation posture, eligible safe actions, manual-required items, recent runs, and next-run metadata. |
| `kb.automation_run` | Run a bounded guarded automation pass for allowlisted low-risk actions without mutating settings. |
| `kb.automation_actions` | List durable sanitized guarded automation action history by status, action, or run. |
| `kb.retrieval_benchmark_run` | Run the synthetic retrieval-quality benchmark suite and store metadata-only history with metric deltas, calibration summaries, and advisory candidates. |
| `kb.retrieval_benchmark_history` | List metadata-only retrieval benchmark history with suite, label, metrics, deltas, calibration summaries, and case-failure evidence. |
| `kb.governance_run` | Run evaluated memory governance in shadow, manual, or explicitly configured auto mode and persist sanitized proposals. |
| `kb.governance_actions` | List governance actions with telemetry by source, action, risk, status, and mutation result. |
| `kb.governance_apply` | Apply one confirmed governance action with rationale, benchmark-gate checks, conflict detection, and audit evidence. |
| `kb.governance_recover` | Recover one applied governance action from captured before-state with rationale and audit evidence. |
| `kb.governance_digest` | Return the latest bounded local governance digest. |
| `kb.governance_policy` | Return the effective sanitized governance policy and defaults. |
| `kb.audit` | List recent audit events. |
| `kb.forget` | Forget a memory item by id with an audit reason. |
| `kb.status` | Return Flux health and runtime status. |
| `kb.crawl_status` | Return corpus crawler, watcher, job, and retrieval status. |
| `kb.crawl_sync` | Sync monitored corpus roots or paths, optionally as a dry run. |
| `kb.crawl_watch_status` | List watched roots and watcher runtime state. |
| `kb.crawl_watch_enable` | Enable filesystem watching for one root or all roots. |
| `kb.crawl_watch_disable` | Disable filesystem watching for one root or all roots. |
| `kb.crawl_jobs` | List recent corpus extraction and capture jobs. |
| `kb.mail_status` | Return mail ingestion, OAuth, profile, and scheduler status. |

Codex may expose these tools through MCP wrapper names rather than literal
top-level `kb.*` names. For example, `kb.status`, `kb.brief`, and
`kb.finalize_turn` can appear as `mcp__flux_llm_kb.kb_status`,
`mcp__flux_llm_kb.kb_brief`, and
`mcp__flux_llm_kb.kb_finalize_turn`. Treat either naming form as the same Flux
MCP surface.

## REST

Install API dependencies:

```powershell
python -m pip install -e .[api]
uvicorn flux_llm_kb.rest_api:create_app --factory --host 127.0.0.1 --port 8765
```

Endpoints:

- `GET /api/health`
- `GET /api/acceleration/status`
- `POST /api/acceleration/benchmarks/run` with optional `fixture`, `files`, `mode`, `passes`, `label`, `compare_label`, `workers`, `family`, `scope`, `root_name`, `path`, `max_files`, `deployment_label`, and `include_model_probe`
- `GET /api/acceleration/benchmarks?fixture=<name>&mode=<scan|soak|watcher|model>&label=<label>&warm_state=<cold|warm>&scope_type=<synthetic|monitored_root|path>&scope_hash=<sha256:...>&deployment_label=<label>&scenario=<scenario>&freshness_hours=<n>&limit=<n>`
- `GET /api/acceleration/reliability?root_name=<name>&path=<path>&label=<label>&deployment_label=<label>&compare_label=<label>&freshness_hours=<n>&limit=<n>`
- `POST /api/acceleration/reliability/run` with optional `scope`, `root_name`, `path`, `label`, `deployment_label`, `compare_label`, `max_files`, `passes`, `include_cache_readiness`, `include_tuning`, and `evidence_level`
- `GET /api/acceleration/evidence?label=<label>&deployment_label=<label>&compare_label=<label>&freshness_hours=<n>&limit=<n>`
- `GET /api/acceleration/reliability/root/{root_name}`
- `GET /api/acceleration/reliability/roots`
- `GET /api/code/status?root_name=<name>&cwd=<workspace-path>`
- `GET /api/code/search?query=<q>&mode=<literal_symbol|full_text>&root_name=<name>&cwd=<workspace-path>&language=<language>&symbol_kind=<kind>&relationship=<definition|call|import|route|test|fixture|config|migration|notebook_cell>&path_glob=<glob>&include_generated=<true|false>&limit=<n>`
- `GET /api/code/symbols?symbol=<name>&root_name=<name>&language=<language>&include_references=<true|false>`
- `POST /api/code/feedback` with `query`, optional `root_name`, `result_count`, `surface`, `miss_category`, optional `expected_symbol`, optional `path`, and optional metadata; only hashes/safe leaves are persisted
- `GET /api/code/feedback/summary?root_name=<name>&limit=<n>`
- `GET /api/diagnostics/{section}` where `section` is `all`, `retrieval`, `watcher`, `workers`, `jobs`, or `mail`, with optional `root_name`, `status`, `family`, `since_hours`, and `include_details`
- `POST /api/diagnostics/actions` with `action`, `target_type`, optional `target_id`, `root_name`, `family`, and `reason`; supported actions are confirmation-gated and never mutate settings
- `POST /api/crawl/roots` and `PATCH /api/crawl/roots/{root_id}` accept `strict_indexing` to block metadata-only indexing behavior for go-live roots
  - Corpus extraction blockers are stored as `blocked_by_policy` for configured policy/limit blocks, `blocked_invalid_source` for corrupt or invalid package/source inputs, and `blocked_missing_dependency` for real missing tools, modules, services, or configuration.
- `GET /api/automation/status`
- `POST /api/automation/run` with optional `mode`, `limit`, and `dry_run`
- `GET /api/automation/actions?run_id=<id>&status=<proposed|applied|skipped|blocked|failed|all>&action=<name>&limit=<n>`
- `GET /api/settings`
- `GET /api/settings/{key}`
- `PUT /api/settings/{key}`
- `POST /api/settings/apply`
- `POST /api/settings/{key}/reset`
- `GET /api/mail/status`
- `GET /api/mail/profiles`
- `POST /api/mail/profiles`
- `POST /api/mail/sync`
- `POST /api/mail/watch`
- `POST /api/mail/oauth/gmail/start`
- `GET /api/mail/oauth/gmail/callback`
- `GET /api/mail/oauth/status`
- `GET /api/outlook-host/status`
- `POST /api/outlook-host/request-sync`
- `POST /api/outlook-host/profiles/{name}/enable`
- `POST /api/outlook-host/profiles/{name}/disable`
- `GET /api/host/status`
- `POST /api/host/browse-folder`
- `POST /api/host/validate-path`
- `GET /api/crawl/status`
- `POST /api/crawl/sync`
- `POST /api/crawl/backfill` enqueues work and returns `202` operation metadata with `operation_id`, `job_ids`, `status_url`, event topics, and optional callback tracking.
- `POST /api/crawl/watch`
- `POST /api/crawl/watch/probe`
- `GET /api/crawl/watch/events`
- `GET /api/crawl/workers?family=<name|all>`
- `POST /api/search`
- `GET /api/search?query=<q>&limit=<n>`
- `POST /api/brief`
- `GET /api/brief?query=<q>&token_budget=<n>`
- `POST /api/explain`
- `GET /api/explain?query=<q>&limit=<n>&token_budget=<n>`
- `POST /api/retrieval/benchmarks/run` with optional `suite` (`standard` or `governance-shadow`), `label`, `compare_label`, `limit_per_query`, `token_budget`, and `persist`
- `GET /api/retrieval/benchmarks?suite=<standard|governance-shadow>&label=<label>&limit=<n>`
- `GET /api/governance/runs?limit=<n>`
- `POST /api/governance/run` with optional `mode` (`shadow`, `manual`, or `auto`) and `limit`
- `GET /api/governance/actions?status=<proposed|blocked|applied|recovered|skipped_conflict|failed|all>&limit=<n>`
- `POST /api/governance/actions/{action_id}/apply` with required `rationale` and `confirm=true`
- `POST /api/governance/actions/{action_id}/recover` with required `rationale` and `confirm=true`
- `GET /api/governance/digest`
- `GET /api/governance/policy`
- `GET /api/claims?review=<all|needs_review|current>&state=<state>&q=<q>&limit=<n>`
- `POST /api/claims`
- `GET /api/claims/{claim_id}`
- `POST /api/claims/{claim_id}/transitions`
- `GET /api/graph/traverse?entity_id=<id>&relation_type=<type>&max_depth=<n>`
- `GET /api/capture/review?status=<pending_review|approved|rejected|completed|failed|blocked_missing_dependency|all>&limit=<n>`
- `POST /api/capture/review/ingest` with optional `job_id`, `limit`, and `dry_run`
- `POST /api/capture/review/{job_id}/decision`
- `POST /api/semantic-duplicates/refresh`
- `GET /api/semantic-duplicates?memory_class=<corpus|episode|claim>&root_name=<name>&limit=<n>`
- `GET /api/corpus/assets`
- `GET /api/corpus/assets/{asset_id}`
- `GET /api/corpus/chunks/{chunk_id}`
- `POST /api/remember`
- `GET /api/audit`
- `POST /api/forget`

## Consumer Access

External consumers should use one of three read paths:

- REST for simple tools and scripts:
  `GET /api/search?query=customer%20RFP&limit=5` or
  `GET /api/brief?query=customer%20RFP&token_budget=1200`. Use
  `GET /api/explain?query=customer%20RFP&limit=5` when a consumer needs snippets,
  ranking signals, filters, suppression metadata, and the brief-packing trace.
- MCP for agent runtimes: `kb.search`/`kb.explain`/`kb.brief` in raw MCP clients, or Codex
  wrapper names such as `mcp__flux_llm_kb.kb_search` and
  `mcp__flux_llm_kb.kb_explain` and `mcp__flux_llm_kb.kb_brief`.
- CLI for local shell automation: `flux-kb search "customer RFP" --limit 5` or
  `flux-kb explain "customer RFP" --limit 5`.

Search, explain, and brief reads accept optional `cwd`, `root_name`, and `scope_mode`
parameters. They also accept per-query retrieval filters without changing global
settings: `logical_kinds` (`episode`, `file`, `mail`), `current_only`,
`lifecycle_states`, `include_suppressed`, and code filters. Broad search,
explain, and brief exclude `file_kind=code` results by default; callers that
want code from these broad surfaces must pass `file_kind=code` /
`filters={"file_kinds":["code"]}` as the only requested file kind, or use
dedicated code search/symbol lookup surfaces. Mixed code plus non-code file-kind
filters are rejected; make separate broad non-code and code-specific calls when
both contexts are needed. REST POST bodies use a `filters` object; REST GET
accepts `kind`, `current_only`, `lifecycle_state`, `include_suppressed`, and
`file_kind=code` query parameters; MCP tools accept an optional `filters` object;
CLI search/explain use `--kind`, `--current-only`, `--lifecycle-state`,
`--include-suppressed`, and `--file-kind code`.

For code tools, prefer passing `cwd` when the agent knows the workspace path.
Do not derive `root_name` from a display folder name; call `kb.code_status` or
`flux-kb code status --cwd <path>` and use the exact returned root name when an
explicit root is needed. `kb.code_search` defaults to `mode=literal_symbol`,
which matches symbols, qualified names, and paths. Use `mode=full_text` for
natural-language terms, stderr fragments, job text, and implementation-body
phrases that may only appear inside indexed code chunks.
`scope_mode=local_first` is the default: Flux searches matching
workspace/root evidence first, then falls back to global memory only when local
results have no lexical or fuzzy evidence. Search and explain responses may
include `retrieval_explanation.confidence` and
`retrieval_explanation.deprioritization`; the `score` field remains the ranking
score, not a confidence value. Semantic near-duplicate suppression is reported
alongside exact duplicate and same-document version suppression when
`include_suppressed` is enabled. Use `local_only` to forbid global fallback, or
`global` for deliberate cross-workspace retrieval. Explicit mid-turn searches
can use `scope_mode=workspace_boosted` to blend local
workspace/root evidence with strong cross-workspace or general indexed evidence
while suppressing weak trust-only global matches. Briefing should keep the
default `local_first` mode unless the caller intentionally requests a broader
scope.

Memory writes accept optional `cwd` and `root_name` as workspace provenance.
Pass the active workspace `cwd` when calling `kb.remember`,
`kb.finalize_turn`, `/api/remember`, or `flux-kb remember`; the CLI defaults
manual remembers to its current directory. Use `kb.remember` for concise
durable atomic saves when a verified decision, fix, reusable procedure, command,
or project fact should be retrievable before the turn ends; runtime text masking
follows `privacy.redactions.enabled`.
Use `kb.finalize_turn` at the end of meaningful work for the turn summary, and
avoid duplicating every prior `kb.remember` item. Explicit repair of older
unscoped episodes is available through `flux-kb episodes scope-backfill --cwd
<path> --id <episode-id> [--dry-run]`; it only updates caller-selected IDs.

Claim lifecycle and graph primitives are available through the same surfaces for
kernel-level automation:

```powershell
flux-kb claim upsert --subject-type project --subject Flux --predicate uses --object PostgreSQL --confidence 0.8
flux-kb claim transition <claim-id> confirm --reason "verified"
flux-kb graph traverse <entity-id> --relation-type depends_on --max-depth 2
flux-kb capture review list --status approved --limit 50
flux-kb capture review decide <job-id> --decision approve --rationale "Verified metadata and source."
flux-kb capture review ingest --job-id <job-id> --dry-run
flux-kb capture review ingest --limit 25
flux-kb semantic-duplicates refresh --memory-class all --limit 1000
flux-kb semantic-duplicates list --memory-class corpus --limit 50
flux-kb acceleration status
flux-kb crawl add E:\Projects --name projects --strict-indexing
flux-kb crawl edit projects --strict-indexing
flux-kb crawl watch probe --timeout 2
flux-kb crawl worker status --family all
flux-kb acceleration benchmark run --fixture all --files 10 --mode scan --passes 2 --label after-change --compare-label baseline
flux-kb acceleration benchmark run --fixture image-heavy --files 20 --mode soak --workers 2 --family media
flux-kb acceleration benchmark run --fixture all --files 5 --mode watcher
flux-kb acceleration benchmark run --scope root --root docs --max-files 1000 --mode scan --deployment-label after-update
flux-kb acceleration benchmark run --fixture image-heavy --mode model --passes 2 --deployment-label after-update
flux-kb acceleration benchmark history --fixture text-heavy --mode scan --warm-state warm --label after-change --limit 10
flux-kb acceleration evidence --compare-label baseline
flux-kb acceleration reliability roots
flux-kb acceleration reliability run --scope all-roots --full --compare-label baseline
flux-kb code status --cwd "E:/LLM KB"
flux-kb code search build_invoice --root app --mode literal-symbol --language python --relationship call --path-glob "src/*.py"
flux-kb code search "PaddleOCR stderr worker" --cwd "E:/LLM KB" --mode full-text --language python
flux-kb code symbol OrderService.build_invoice
flux-kb code feedback add --query "redacted local query" --root app --miss-category missing_symbol --expected-symbol OrderService.build_invoice
flux-kb code feedback summary --root app
flux-kb diagnostics all --root docs --status blocked_by_policy --family office --include-details
flux-kb diagnostics remediate retry_corpus_job --target-type job --target-id <job-id> --root docs --family office --reason "dependency fixed"
flux-kb automation status
flux-kb automation run --mode guarded --limit 25
flux-kb automation actions --status all --limit 25
flux-kb crawl backfill --root docs --family office --limit 20
flux-kb crawl backfill --root docs --family office --limit 20 --callback-url http://127.0.0.1:8765/callback
flux-kb retrieval benchmark run --suite standard --label after-change --compare-label baseline
flux-kb retrieval benchmark run --suite governance-shadow --label before-automation
flux-kb retrieval benchmark history --suite standard --label after-change --limit 10
flux-kb governance run --mode shadow --limit 25
flux-kb governance actions list --status proposed --limit 25
flux-kb governance actions apply <action-id> --rationale "reviewed sanitized evidence" --confirm
flux-kb governance actions recover <action-id> --rationale "operator rollback" --confirm
flux-kb governance digest
flux-kb governance policy
```

Lifecycle transitions append audit-visible events. Superseded, contradicted,
stale, and retired claims remain available for review but normal brief packing
prefers current evidence. `include_suppressed` returns sanitized counts, paths,
canonical identifiers, and reasons for exact duplicate, same-document version,
and semantic near-duplicate suppression; it does not return raw suppressed
content. Semantic duplicate clusters are advisory metadata only and do not delete
or rewrite source assets, episodes, or claims.

The dashboard Review tab uses `GET /api/claims` and `GET /api/graph/traverse`
to browse lifecycle review work and selected-entity graph edges. The
`needs_review` filter includes stale, contradicted, superseded, and retired
claims, plus claims with non-`keep` retention actions. `GET /api/capture/review`
returns capture-review job metadata only and accepts status filters for
`pending_review`, `approved`, `rejected`, `completed`, `failed`,
`blocked_missing_dependency`, and `all`; raw capture text is never returned.
Operators can approve or reject pending review jobs with
`POST /api/capture/review/{job_id}/decision` and a required `rationale`;
decisions update job status, store `payload.review`, keep raw capture payload
fields out of responses, and append audit-visible `capture.review_approved` or
`capture.review_rejected` events. Approved Codex backfill jobs can then be
ingested through `POST /api/capture/review/ingest`, `flux-kb capture review
ingest`, or `kb.capture_review_ingest`; ingestion records sanitized status,
skip reasons, created memory ids, and `capture.ingested`,
`capture.ingestion_skipped`, or `capture.ingestion_failed` audit events under
`capture_jobs.payload.ingestion`.

Evaluated memory governance is exposed through REST, CLI, MCP, and the
dashboard Review tab. `POST /api/governance/run`, `flux-kb governance run`, and
`kb.governance_run` persist sanitized proposal runs sourced from retention
quality, claim lifecycle state, active semantic duplicate clusters, capture
ingestion outcomes, code retrieval feedback summaries, and the latest
`governance-shadow` benchmark. Apply and recover require explicit confirmation
and rationale. They are blocked unless the latest persisted
`governance-shadow` evidence has no guardrail failures and meets the configured
precision threshold. Responses expose action ids, guardrail status, telemetry,
and before/after state, but never raw memory text, private paths, raw queries,
snippets, embeddings, local model prompts, or local model outputs.

The dashboard shell loads with `GET /api/dashboard/snapshot`, which returns
`generated_at` plus the bounded dashboard sections `health`, `crawl`, `jobs`,
`retrieval`, `modelActivity`, `mail`, `outlook`, and `settings`. After that the
browser opens `WS /api/dashboard/stream` and sends `dashboard.subscribe` with
the desired sections, active tab, Jobs filters/sort/page, and model activity
options. The stream sends `dashboard.connected`, `dashboard.snapshot`,
`dashboard.section`, `dashboard.event`, and recoverable `dashboard.error`
messages. RabbitMQ-backed updates are non-competing with durable dashboard
event workers; if RabbitMQ is unavailable, the stream reports a degraded state
instead of re-enabling periodic REST polling.

Lookup endpoints are read-only and return stable JSON payloads for asset and
chunk inspection. The API binds to `127.0.0.1` by default; do not expose it to a
network interface without an explicit local access-control policy.

## Gmail OAuth

The default Gmail OAuth redirect URI is `http://127.0.0.1:8765`. Google returns
the authorization code to the Flux root route, which completes setup and shows a
small local result page. Keep the dashboard/API running before starting consent.

The explicit `GET /api/mail/oauth/gmail/callback` endpoint remains available for
custom clients or manually configured redirect URIs, but Flux will not silently
reuse a generic `http://localhost` redirect from a downloaded Google client JSON
because another local service, such as IIS, may already own that URL.

## Runtime Settings

Runtime settings are settings catalog-backed and available through CLI and REST.
Use the dashboard settings tab for interactive edits; it shows whether a value
comes from the environment, database, or catalog default. Sensitive values are
masked. This is cross-platform application configuration, not the Windows
Registry.

```powershell
flux-kb settings list
flux-kb settings get retrieval.token_budget
flux-kb settings set retrieval.token_budget 1600
flux-kb settings reset retrieval.token_budget
flux-kb settings apply --component watcher
flux-kb search-index status --root projects
flux-kb search-index sync --owner-class all --root projects --limit 250
flux-kb search-index rebuild --owner-class all --root projects --limit 100
```

Crawler glob settings are global defaults. Monitored roots can inherit, extend,
or override them; effective globs are returned in dashboard crawl payloads.

Acceleration settings define the permanent cache root, explicit watcher backend
policy (`watcher.backend`, with `FLUX_KB_WATCHER_BACKEND` override),
local loopback or Docker host-gateway model probing, per-family worker caps,
hash parallelism, and recursive container caps. Local inference is enabled by
default for the configured local provider and rejects non-local URLs. The read-only acceleration status is available
through `flux-kb acceleration status`, `GET /api/acceleration/status`,
`kb.acceleration_status`, and the dashboard Performance tab. The payload includes
selected watcher backend, native/fallback state, fallback reason,
worker-family OCR/ASR/container/parser/search-index telemetry, worker-family
backpressure, cap usage, retry/lock transitions, `manifest_skipped_unchanged`
counters, and deterministic benchmark fixture summaries for text-heavy,
Office/PDF-heavy, archive/container-heavy, image-heavy, and audio/video-heavy
roots.
Watcher probes and benchmark runs are metadata-only operational checks.
Synthetic runs use temporary files; scoped runs dry-run opted-in monitored roots
and store only aggregate counts, stable scope hashes, and sanitized labels. They
must not store raw text, mail contents, private paths, credentials, or
embeddings. Benchmark history is metadata only and is exposed through CLI, REST,
and MCP as fixture names, modes, labels, compare labels, deployment labels,
scope types, pass indexes, counts, timings, p50/p95/max, throughput, warm/cold
state, cache hit/miss counters, hash-parallelism, worker-count, manifest-skip
fields, model/tool readiness telemetry, worker-family breakdowns, watcher
backend summaries, comparable elapsed and throughput deltas, and sanitized
summaries. Benchmark `scan` mode supports cold/warm passes, `soak` mode
exercises benchmark-tagged synthetic worker-family jobs through existing
cap/backpressure logic and purges them, `watcher` mode stores temporary probe
metadata, `model` mode records local-only model/tool readiness and blocked
dependencies, and `all` mode runs scan/soak/watcher unless model probing is
explicitly requested. Recommendation payloads are diagnostic only and report
`settings_mutated: false`; callers must change settings explicitly through the
normal settings APIs.
The indexer reliability gate is a read-only aggregation over the same benchmark
history plus sanitized worker-family, watcher, and monitored-root summaries.
`flux-kb acceleration reliability status`, `GET /api/acceleration/reliability`,
and `kb.indexer_reliability_status` return readiness (`ready`, `partial`,
`blocked`, or `not_run`), required checks, latest run references, watcher and
worker summaries, and evidence-scored manual candidates. `flux-kb acceleration
reliability run`, `POST /api/acceleration/reliability/run`, and
`kb.indexer_reliability_run` run the validation suite under one label while
keeping `settings_mutated: false`. `evidence_level=full` or CLI `--full`
includes synthetic reliability, scoped host/cloud evidence for enabled roots,
cache readiness, and tuning comparison evidence. `root-status`,
`root/{root_name}`, and `kb.indexer_root_reliability` expose a per-root
readiness card. `flux-kb acceleration evidence`,
`GET /api/acceleration/evidence`, and `kb.operator_evidence` combine
reliability, code, and diagnostic evidence into VSS validation/provider gate
decisions. VSS settings changes, provider-specific acceleration, and automatic
settings changes remain outside these gates.
`flux-kb acceleration reliability roots`,
`GET /api/acceleration/reliability/roots`, and
`kb.indexer_reliability_roots` summarize enabled monitored roots as sanitized
readiness cards with stale/missing scoped evidence, blocked job/asset counts,
strict-indexing state, latest benchmark references, and manual tuning
candidates. Strict roots treat metadata-only asset leftovers as blockers.
`--scope all-roots` on the reliability run executes the read-only evidence
workflow across enabled roots without mutating settings.
Code diagnostics use the existing `source_assets`, `asset_chunks`,
`code_symbols`, `code_references`, and `code_retrieval_feedback_events` tables.
`flux-kb code status|search|symbol`, `GET /api/code/status`,
`GET /api/code/search`, `GET /api/code/symbols`, and the matching MCP tools
expose coverage summaries, parser/fallback rates, generated counts,
symbol/reference lookup, feedback summaries, benchmark-derived code gaps, and
sanitized file labels without raw code content. Code search accepts optional
`mode`, `cwd`, `relationship`, `path_glob`, and `include_generated` filters.
`mode=literal_symbol` uses the stored symbol/reference metadata and remains the
default. `mode=full_text` delegates to indexed code corpus chunks and may return
bounded snippets/excerpts, but not complete source files. Generic
`kb.search`, REST search, CLI search, and explain filters accept the same code
filter fields through the normal `filters` contract. Sanitized result metadata
may include generated status, relationship, source/target symbols, route or test
target, parser status, language, symbol kind, and line ranges. Feedback can be
recorded through `flux-kb code feedback add`, `POST /api/code/feedback`, or
`kb.code_feedback_record`; these surfaces hash the query/symbol/scope and store
only safe filename leaves and category metadata.
Operational diagnostics are available through `flux-kb diagnostics <section>`,
`GET /api/diagnostics/{section}`, and `kb.operational_diagnostics`; they
aggregate retrieval traces, watcher events, worker state, slow/blocked jobs,
mail sync runs, and mail post-process events as bounded evidence instead of raw
log dumps, with optional root/status/family/time/detail filters. Diagnostic
items may include sanitized `remediation_actions[]` for retrying retryable
corpus jobs, running scoped backfill, repairing root-scoped asset statuses, or
clearing stale completed-job errors. Those actions run through `flux-kb
diagnostics remediate`, `POST /api/diagnostics/actions`, or
`kb.diagnostics_remediate`, append audit events, and always return
`settings_mutated: false`.
Diagnostic items include operator guidance that distinguishes policy blockers
from invalid source/package inputs and real missing dependencies.
Long-running corpus backfill surfaces are enqueue-only. REST, MCP, and CLI return
accepted operation metadata; `--wait`-style observation can be added by clients,
but processing remains in RabbitMQ workers. Callback URLs are accepted only for
loopback, private-network, or explicitly allowlisted destinations. Callback
requests are signed with `FLUX_KB_CALLBACK_SIGNING_SECRET`, use idempotency keys,
and retry through RabbitMQ-backed `callback_deliveries` state.
Guarded operator automation is available through `flux-kb automation
status|run|actions`, `GET /api/automation/status`, `POST
/api/automation/run`, `GET /api/automation/actions`, and
`kb.automation_status`, `kb.automation_run`, and `kb.automation_actions`.
Recurring automation is controlled by default-off `operator.automation.*`
settings. A manually triggered guarded pass may refresh retrieval evidence,
ingest already-approved capture jobs, run safe diagnostic recovery, sync or
rebuild the search index, and run governance in shadow mode. Deletes, destructive
mail policies, OAuth, host startup, restart/reindex settings, capture decisions,
high-risk governance, opening/revealing files, and ambiguous actions remain
manual. Automation history stores sanitized evidence and reports
`settings_mutated: false`.
Retrieval benchmarks are separate from acceleration benchmarks. They seed
temporary public-safe synthetic retrieval cases, call the same search, explain,
and brief paths used by consumers, and persist metadata-only quality evidence:
top-1 accuracy, precision@3, recall@5, MRR, nDCG@5, brief recall, brief
dilution, scope and suppression pass counts, elapsed time, sanitized case ids,
query hashes, ranks, result ids, stream/kind labels, case categories, confidence
bands, score evidence, and failure reasons. The `standard` suite includes code
cases for callers, tests, routes, generated-file handling, config/migration
lookup, fallback recovery, and cross-root disambiguation, and also reports
`metric_deltas`, `calibration_summary`, semantic duplicate
`recommendations.candidates[]`, richer sanitized `case_results[]`, and
`settings_mutated: false`. The `governance-shadow` suite adds read-only,
metadata-only stale, apply/recover, stale-proposal conflict, duplicate-cluster,
capture-ingestion, feedback-gap, contradicted, low-confidence,
protected/current, and false-positive guardrail cases. Its output includes
proposal categories, candidate counts, guardrail pass/fail counts,
precision-style summaries, sanitized failed cases, and `settings_mutated:
false`; benchmark output is advisory evidence for later calibration and
governance apply gates, not automatic ranking, threshold, semantic-cluster,
lifecycle, settings, or policy mutation.
## Host Filesystem Agent

Use the host agent when the dashboard/API is Docker-hosted but watched paths live
on the host filesystem:

```powershell
flux-kb host-agent status
flux-kb host-agent run
```

The agent exposes local-only status, path validation, native folder browse, and
host-side crawl sync endpoints. `flux-kb host-agent run` also starts the
host-side RabbitMQ consumer for `flux.commands.corpus_host_agent` by default; use
`flux-kb event worker run --queue flux.commands.corpus_host_agent --worker-id
host-agent` only when manually supervising separate fallback processes. It stores
no private content in Git.

## Mail Capture

IMAP is the preferred ongoing capture path. Configure a Gmail label or IMAP
folder as the capture queue, then export into a private spool that Flux indexes.
For managed IMAP/Outlook mail, canonical body and attachment plaintext is kept
in private disk content sidecars and hydrated from disk for search, previews,
and search-index sync. PostgreSQL stores blank chunk bodies plus sidecar
references, hashes, and metadata, not plaintext body/attachment chunk
text.

```powershell
flux-kb mail profile add-imap `
  --name gmail-capture `
  --account me@gmail.com `
  --server imap.gmail.com `
  --folder FluxCapture `
  --spool private\mail-spool\gmail-capture `
  --post-process remove_label `
  --processed-folder FluxProcessed

flux-kb mail oauth gmail start `
  --profile gmail-capture `
  --client-config private\google-oauth-client.json

flux-kb mail oauth status --profile gmail-capture
flux-kb mail post-process dry-run --profile gmail-capture --limit 5
flux-kb mail post-process events --profile gmail-capture --limit 20
flux-kb mail watch run --profile gmail-capture
```

Open the returned authorization URL, approve the local desktop app, and let the
loopback callback complete through the local dashboard/API. Flux stores the
refresh token locally, masks it in all responses, and refreshes short-lived
access tokens before XOAUTH2 IMAP login.

Mail post-processing is policy-driven per profile:

- `none`: export only and leave the message in place.
- `remove_label`: Gmail-only; remove the capture label with Gmail IMAP label
  commands.
- `move_to_processed`: Gmail adds the processed label and removes the capture
  label; generic IMAP copies to the processed folder, marks the source deleted,
  and expunges.
- `trash`: confirmation-gated; Gmail applies Trash semantics and generic IMAP
  copies to `trash_folder` when configured, then deletes and expunges the
  source message.
- Outlook COM profiles should use `none`; non-`none` Outlook COM policies are
  blocked because Outlook mailbox mutation is not implemented.

Use `flux-kb mail post-process dry-run` or
`POST /api/mail/profiles/{profile}/post-process/dry-run` before enabling a new
policy. Recent outcomes are available through
`flux-kb mail post-process events` and
`GET /api/mail/post-process/events?profile_name=<name>`. Audit views include
profile, provider, policy, action, status, command metadata, and errors, but not
raw mail bodies.

Classic Outlook COM catch-up is scoped to selected folder paths:

```powershell
flux-kb mail profile add-outlook `
  --name outlook-catchup `
  --folder "Mailbox - Me\Inbox\Flux Capture" `
  --spool private\mail-spool\outlook-catchup `
  --incremental-basis received-time

flux-kb outlook-host sync --profile outlook-catchup
flux-kb outlook-host run
```

`flux-kb mail sync --profile <outlook-profile>` does not attempt COM from the
Docker-hosted worker. It reports that the Windows Outlook host is required. Run
`flux-kb outlook-host run` in the logged-in Windows session to consume brokered
Outlook work from `flux.commands.outlook`, or queue a one-off request with
`flux-kb outlook-host sync --profile <name>`.
Outlook COM profile setup does not require an IMAP server or account value; the
Windows Outlook profile owns that connection.

Outlook COM sync keeps profile-scoped cursors in mail profile metadata and
filters each folder before enumerating messages. The default incremental basis
is `received-time`, which exports messages whose Outlook `ReceivedTime` is newer
than the prior cursor, with a small overlap for timestamp safety. Use
`--incremental-basis last-modification-time` or REST field
`outlook_incremental_basis: "last_modification_time"` for watched folders where
operators move older mail in after it was originally received.

Mailbox credentials, OAuth tokens, raw messages, attachments, and private mail
content sidecars stay local and must remain outside Git.

## Codex Plugin

The personal plugin scaffold lives in `plugins/flux-llm-kb`.

The hook scripts call:

```powershell
python -m flux_llm_kb.cli hook user-prompt-submit
python -m flux_llm_kb.cli hook pre-compact
python -m flux_llm_kb.cli hook stop
```

Set `FLUX_KB_PYTHON` if Codex should use a specific Python executable:

```powershell
$env:FLUX_KB_PYTHON = "C:\Path\To\python.exe"
```

Codex has three Flux integration surfaces:

- Plugin hooks and skills provide automatic context/capture behavior and user
  guidance inside Codex turns.
- MCP tools provide callable Flux tools when `[mcp_servers.flux_llm_kb]` is
  present in `~/.codex/config.toml`. Depending on Codex tool discovery, they may
  appear as raw MCP names such as `kb.brief`, `kb.search`, and
  `kb.remember`, `kb.finalize_turn`, or as Codex wrappers such as
  `mcp__flux_llm_kb.kb_brief`, `mcp__flux_llm_kb.kb_search`, and
  `mcp__flux_llm_kb.kb_remember`, and
  `mcp__flux_llm_kb.kb_finalize_turn`. Models may query mid-turn when they need
  prior decisions, unresolved project context, patterns from other workspaces,
  general indexed documents, previous fixes, or user-referenced history. Use
  `kb.brief` for compact workspace-scoped context and `kb.search` with
  `scope_mode=workspace_boosted` for expanded discovery; skip KB retrieval when
  local files, the prompt, or current tool output already answer the question.
  Use `kb.remember` for concise durable atomic saves during work, with active
  `cwd` or `root_name` provenance, and use `kb.finalize_turn` for the
  end-of-turn summary without repeating every mid-turn save. Runtime masking is
  controlled by `privacy.redactions.enabled`.
- REST remains the fallback surface for tools that can call the local API
  directly, for example `GET /api/brief?query=...`.

`flux-kb codex install-plugin` installs the plugin and writes the Flux MCP
server config block:

```toml
[mcp_servers.flux_llm_kb]
command = "<Flux Python>"
args = ["-m", "flux_llm_kb.mcp_server"]
cwd = "<Flux app root>"
enabled = true
startup_timeout_sec = 15
tool_timeout_sec = 60
```

The command prefers `FLUX_KB_PYTHON`, then the production app virtual
environment when available, then the active Python. Production installs must run
the MCP server from the host Python environment, not through `docker exec`,
because container-backed MCP processes can be killed by deployment restarts and
surface to Codex as a closed transport. `flux-kb codex status` reports whether
this MCP block is configured, enabled, and able to import the optional MCP
dependency.

Use the MCP readiness probe after install or deployment:

```powershell
flux-kb codex mcp-readiness --json
```

The probe spawns the configured stdio MCP server and calls `kb.status`,
`kb.search`, and `kb.brief`. It fails if the MCP command is container-backed, if
the stdio transport closes, or if a tool reports `temporary_unavailable`.
Deployment validation runs this check separately from dashboard HTTP health.

When the Flux API, PostgreSQL, or search backend is temporarily unavailable,
core read-only MCP tools such as `kb.status`, `kb.search`, `kb.explain`,
`kb.brief`, code lookup, and operational diagnostics retry with bounded backoff
and a refreshed service client. Persistent outages return an explicit typed
payload instead of an empty success:

```json
{
  "ok": false,
  "status": "temporary_unavailable",
  "settings_mutated": false,
  "error": {
    "code": "mcp.temporary_unavailable",
    "component": "mcp",
    "stage": "kb.brief",
    "retryable": true,
    "status_code": 503
  }
}
```

Mutating MCP tools such as `kb.remember` and `kb.finalize_turn` do not replay a
failed write automatically when the backend connection drops, because the commit
state may be unknown. They return the same typed temporary-unavailable payload
with `settings_mutated: false`; callers should retry deliberately after the
backend recovers.

When `FLUX_KB_APP_ROOT` is not set, `flux-kb codex install-plugin` preserves an
existing valid Flux local marketplace source before falling back to the imported
package location. This keeps Codex pointed at a deployed app such as
`J:\FluxLLMKB\app` even after developer editable installs are repaired back to
the repository checkout.

`flux-kb codex status` also checks the Codex plugin discovery cache under
`~/.codex/plugins/cache`. A cache entry is considered discoverable only when the
cached Flux manifest, skills, hooks, and scripts match the installed plugin
source. If the cache is stale, status reports `ready_restart_required` with a
stale-cache message instead of `ready`. Running `flux-kb codex install-plugin`
safely invalidates stale Flux-owned cache directories and leaves unrelated
plugin caches untouched; restart Codex Desktop afterward so it rebuilds the
cache from the current plugin source.

For an end-to-end Codex smoke test, run `flux-kb codex mcp-readiness --json`,
then verify that at least the status, brief, and finalize tools are callable
through either naming form. A successful manual test should call
`kb.status`/`mcp__flux_llm_kb.kb_status`, call
`kb.brief`/`mcp__flux_llm_kb.kb_brief` with a harmless smoke-test task, and
store only a concise outcome through
`kb.finalize_turn`/`mcp__flux_llm_kb.kb_finalize_turn`, passing the active
workspace `cwd` so the saved memory remains locally retrievable. Enable
`privacy.redactions.enabled` before smoke tests for public/shared release
hardening.

Codex hooks run a configurable local policy by default:

- `UserPromptSubmit` skips empty, short, slash-command, and trivial prompts; for
  non-trivial prompts it injects guidance for indexable final responses and
  retrieves a compact workspace-scoped Flux brief when search results include
  lexical or fuzzy evidence. If only global fallback evidence is available, the
  injected context is labeled as global fallback memory and audited as such.
- `Stop` captures the final assistant message once per `session_id` and
  `turn_id`, subject to the global `capture.enabled` setting and Codex hook
  capture limits. It can also index bounded public web references and file
  references that already belong to enabled monitored roots.
- `PreCompact` remains non-blocking and does not parse transcript files because
  Codex transcript paths are not a stable hook contract.

Hook failures never block Codex. They return a warning and continue. Audit
events use the `codex_hook.*` prefix, and dashboard health shows hook policy
state plus recent hook events.

Runtime settings:

```powershell
flux-kb settings get codex.hooks.enabled
flux-kb settings set codex.hooks.preflight_enabled false
flux-kb settings set codex.hooks.capture_enabled false
flux-kb settings set codex.hooks.capture_guidance_enabled true
flux-kb settings set codex.hooks.reference_indexing_enabled true
flux-kb settings set codex.hooks.reference_max_count 5
flux-kb settings set codex.hooks.reference_max_bytes 1048576
flux-kb settings set codex.hooks.reference_fetch_timeout_seconds 3
flux-kb settings set codex.hooks.reference_allow_private_urls false
flux-kb settings set codex.hooks.token_budget 900
flux-kb settings set codex.hooks.min_prompt_chars 32
flux-kb settings set codex.hooks.capture_min_chars 160
flux-kb settings set codex.hooks.capture_max_chars 8000
```
