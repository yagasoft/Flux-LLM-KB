# Setup

Flux-LLM-KB is local-first. Runtime data belongs in your local PostgreSQL
database, not in this repository.

## Prerequisites

- Python 3.11+
- Git
- GitHub CLI for repository work (optional after bootstrap)
- Docker Desktop with `docker compose`

The default PostgreSQL runtime uses the repository Docker Compose profile. If
Docker or Compose is not available, `scripts/check-docker.ps1` exits with a clear error.
The normal application runtime is Docker-backed: PostgreSQL, RabbitMQ, FastAPI,
the dashboard, outbox relay, event scheduler, IMAP/corpus/search-index workers,
and callback dispatcher live in containers. Outlook COM is the exception; it
runs as a Windows host process outside Docker.

## Install

### Production Runtime

For day-to-day use, deploy Flux into a permanent PC runtime root instead of
running services from this repository. The default root is `D:\FluxLLMKB` and can
be changed with `-InstallRoot`.

```powershell
.\scripts\deploy\install-flux.ps1
.\scripts\deploy\status-flux.ps1
```

The production layout is:

- `D:\FluxLLMKB\app`: deployed compose files, app venv, host launchers, version metadata
- `D:\FluxLLMKB\private`: local env, OAuth tokens, mail spool, and private config
- Docker named volumes: PostgreSQL data, RabbitMQ data, container
  cache/data/runtime/logs, and the Docker Ollama model cache
- `D:\FluxLLMKB\data`: legacy PostgreSQL bind-mount rollback data after migration
- `D:\FluxLLMKB\logs`: host-agent and Outlook-host logs
- `D:\FluxLLMKB\models\ollama`: legacy Ollama model-cache rollback data after migration
- `D:\FluxLLMKB\runtime`: host process heartbeat/status files
- `D:\FluxLLMKB\backups`: local PostgreSQL dump/export target

The repository remains source code only. Production Docker Compose uses prebuilt
local image tags, not `build.context: .`. Container-owned persistent state lives
in Docker named volumes; Windows bind mounts are reserved for host-managed
private config, mail spool, and Windows-only watched roots. API access remains local at
`http://127.0.0.1:8765/dashboard`.

The local Docker profile assumes a single-user workstation with direct container
memory ceilings. Every Compose surface sets direct hard limits with
`memswap_limit` equal to `mem_limit`, so Docker cannot add swap-backed headroom
beyond the configured cap. Generated production Compose sums to 29.5 GB:
model-runner 5 GB, paddle-runner 5 GB, Ollama 4 GB, ASR 3 GB, Vespa 3 GB,
PostgreSQL 2 GB, API/worker/search-index worker 1 GB each, RabbitMQ/mail/Outlook
workers 512 MB each, automation/governance/callback/outbox workers 384 MB each,
and runtime-control/GPU-eviction/event workers 256 MB each. Development Compose
uses the same shared-service limits and stays below 10 GB. PostgreSQL uses a
leaner local profile
(`shared_buffers=768MB`, `effective_cache_size=2GB`, `work_mem=16MB`,
`maintenance_work_mem=256MB`, and `shm_size: "1gb"`) because Vespa and the
model runners own the heavy retrieval and inference workload. `status-flux.ps1`
prints Docker-visible memory, every Flux container's configured memory/swap
limit, and Postgres `/dev/shm` size so tuning decisions use the Linux
VM/container limits, not only Windows host free RAM.

When production GPU mode is available, the generated Compose deployment runs a
small `flux-ollama:local` image derived from the official `ollama/ollama`
runtime as the dedicated `flux-ollama` service. The derived image installs the
OS `ffmpeg` package so Ollama vision has both `ffmpeg` and `ffprobe` available
for image/video/media decode paths. Deploy updates rebuild that derived runtime
only when the `docker/ollama` context or local `ollama/ollama:latest` base image
changes; runtime-changing deploys also tag it with the current deploy tag. Flux
API and worker containers call Ollama through `http://ollama:11434`; Ollama is
not baked into the Flux image and is not run inside the API or worker
containers. Models persist in the `flux_llm_kb_ollama_models` Docker named
volume, so image rebuilds and container recreation do not re-download large
model blobs. After a GPU deployment, install the configured vision model
explicitly:

```powershell
docker exec flux-ollama ollama pull qwen3-vl:8b
```

Verify the deployed media runtime and a tiny local vision decode request after
deployment:

```powershell
docker exec flux-ollama sh -lc "command -v ffmpeg && command -v ffprobe"
.\scripts\deploy\test-ollama-vision.ps1 -OllamaHostPort 11435 -Model qwen3-vl:8b
```

Production GPU mode defaults to `qwen3-vl:8b` and a 2-minute Ollama keepalive so
VRAM is released shortly after Flux vision work goes idle. Local vision requests
allow a cold Docker Qwen load before timing out; failed model attempts are
recorded in image-job telemetry even when OCR text still lets the job complete.
Flux submits a bounded 1280-pixel vision copy with a larger answer budget, while
keeping cache keys tied to the original source file, so Qwen3-VL diagram captions
do not spend the whole default context on image tokens or internal thinking.

Windows host-agent roots, including watched folders that Docker cannot access
directly, use the same Docker Ollama service through a loopback-only host port:
`http://127.0.0.1:11435` by default. API and worker containers continue to use
the Compose service URL `http://ollama:11434`. The separate host port avoids
conflicts with any Windows-hosted Ollama process already bound to
`127.0.0.1:11434`; override it with `-OllamaHostPort` if needed. Install and
update scripts persist matching runtime settings so host-side diagnostics and
manual image backfills use `qwen3-vl:8b`.

Update an existing deployment from the current checkout with:

```powershell
.\scripts\deploy\update-flux.ps1 -RestartHostTasks
```

Production Docker builds use an image-backed offline wheelhouse by default.
`D:\FluxLLMKB\package-cache\wheelhouse` remains the canonical host cache, and
ordinary builds reference `docker-image://flux-llm-kb-wheelhouse:local` instead
of sending that host directory as a BuildKit client context. Runtime and Paddle
dependency locks under `docker/` pin large GPU and AWQ-support packages to
versions expected in the wheelhouse. If a required wheel is missing, the build
stops instead of downloading a replacement. Prefetch missing Python wheels only
as an explicit operator action, then rebuild `flux-llm-kb-wheelhouse:local`
from the persistent host cache before building from the cache.

Feature closeout through `scripts/dev/complete-feature.ps1` uses only local npm,
pip, and Docker dependencies by default. It runs npm in offline mode and passes
`-PipOffline:$true` into production deploy. A missing package or image stops
the operation instead of silently downloading a replacement. Docker Compose
starts and model-download runs use `--pull never`; the existing
`flux-ollama:local` runtime is discovered and reused when the upstream Ollama
base tag is not local, preserving its revision and runtime-fingerprint labels.

```powershell
npm --prefix dashboard ci --include=dev --cache "D:\FluxLLMKB\package-cache\npm" --offline
```

Network refreshes require explicit operator intent. Use `-AllowImagePull` only
when an image must be downloaded, and use `-AllowPackageRefresh` before setting
`-PipOffline:$false` or `-NpmOffline:$false` to permit a package refresh. The
same flags are available on feature closeout; its normal path never enables
either one.

```powershell
.\scripts\deploy\update-flux.ps1 -AllowImagePull
.\scripts\deploy\update-flux.ps1 -AllowPackageRefresh -PipOffline:$false -NpmOffline:$false
.\scripts\dev\complete-feature.ps1 -AllowImagePull
.\scripts\dev\complete-feature.ps1 -AllowPackageRefresh
```

Use `-DockerBaseMode python` only when Docker Desktop reports a local build-base
layer failure such as `mount options is too long`. If `python:3.12-slim` is not
already local, pair it with the explicit `-AllowImagePull` flag; otherwise the
deployment reuses `flux-llm-kb-api:local`.

Each production build records source provenance as OCI labels on the Flux image
and generated Flux containers. The short image tag is kept for local operations,
but `org.opencontainers.image.revision` is the authoritative full Git commit.
Before or after deployment, verify a built image with:

```powershell
$revision = (git rev-parse HEAD).Trim()
.\scripts\deploy\verify-image-traceability.ps1 -Image "flux-llm-kb-api:$(git rev-parse --short HEAD)" -ExpectedRevision $revision
docker image inspect "flux-llm-kb-api:$(git rev-parse --short HEAD)" --format '{{json .Config.Labels}}'
```

After an approved deployment, include the container check:

```powershell
.\scripts\deploy\verify-image-traceability.ps1 -Image "flux-llm-kb-api:$(git rev-parse --short HEAD)" -Container flux-llm-kb-api -ExpectedRevision $revision
```

Start and stop the deployed runtime with:

```powershell
.\scripts\deploy\start-flux.ps1
.\scripts\deploy\stop-flux.ps1 -StopHostTasks
```

`install-flux.ps1` also registers `FluxKB Host Agent` and `FluxKB Outlook Host`
as Windows Scheduled Tasks at user logon. They run outside Docker because host
filesystem access, native folder browsing, and Outlook COM need the logged-in
desktop session.

### Developer Install

For temporary feature worktrees, use the worktree-safe wrapper instead of
changing the shared Python editable install:

```powershell
.\scripts\dev\flux-kb.ps1 lint
.\scripts\dev\flux-kb.ps1 status
```

The wrapper sets `PYTHONPATH` to the current checkout's `src` directory and then
runs `python -m flux_llm_kb.cli`. It is only for repository development. Do not run `python -m pip install -e .` inside temporary worktrees when using the shared `D:\FluxLLMKB\python` runtime; that can leave the global `flux-kb` launcher pointing at a worktree that will later be deleted. Production deployment continues to use the permanent `D:\FluxLLMKB\app\.venv` runtime.

For a long-lived development checkout, install the package in editable mode:

```powershell
python -m pip install -e .[dev]
Copy-Item .env.example .env
.\scripts\check-docker.ps1
.\scripts\start-postgres.ps1
flux-kb migrate
flux-kb doctor
```

Install optional local corpus extractors when you want richer file processing:

```powershell
python -m pip install -e .[dev,corpus,processors]
```

Before adding broad private folders, install and verify the local extractor
families you expect to rely on. Common go-live dependencies are LibreOffice for
legacy/OpenDocument Office conversion, Poppler plus PaddleOCR/PaddleOCR-VL for
image-only PDF/OCR, `ffmpeg`/`ffprobe` plus a local faster-whisper model for media, Calibre
`ebook-convert` for MOBI/AZW/LIT, archive tools such as 7-Zip/bsdtar/unar/unrar,
DuckDB/PyArrow for columnar data, an SVG renderer (`rsvg-convert` from
`librsvg2-bin` in Docker, or portable `resvg.exe` on Windows host-agent roots),
and mail export helpers such as `readpst` or `msgconvert` when you plan to
index exported mail stores. Missing dependencies leave the affected jobs in
`blocked_missing_dependency` instead of silently pretending content was indexed.
The production Docker image installs this practical processor pack, including
`libgl1` and `libglib2.0-0` for current Paddle/OpenCV-style OCR import paths.
Windows
host-agent installs use the `processors` Python extra and still depend on host
tools such as Office COM, LibreOffice, archive tools, and media utilities being
available on the host PATH. Production host launchers also use
`%FLUX_KB_INSTALL_ROOT%\tools\resvg\resvg.exe` as `FLUX_KB_SVG_RENDERER` when
that portable renderer exists. Policy limits such as strict-indexing
metadata-only outcomes or text/code files over the configured inline size limit
are reported as `blocked_by_policy`; corrupt or placeholder Office/package
inputs are reported as `blocked_invalid_source`.

Install Outlook COM support on Windows when you want local Outlook catch-up:

```powershell
python -m pip install -e .[mail]
```

External tools are detected at runtime and reported by `flux-kb crawl doctor`.
`ffprobe`/`ffmpeg`, PaddleOCR/PaddleOCR-VL, and local transcription runtimes are never
called through cloud services by default.
Deferred ASR can either load a local faster-whisper model from
`acceleration.asr.model_path` or call the local OpenAI-compatible ASR service
configured through `acceleration.asr.provider`, `acceleration.asr.model`, and
`acceleration.asr.base_url`. Production GPU deployments use the ASR service with
`large-v3-turbo`; model download is an explicit deploy step into the Docker
model volume, and extraction/transcription still use local files only. Missing
`ffmpeg`, service URL, service readiness, faster-whisper, or local model paths
leave only the related media job in `blocked_missing_dependency`.

## Useful Commands

From a temporary worktree, prefer the dev wrapper:

```powershell
.\scripts\dev\flux-kb.ps1 lint
.\scripts\dev\flux-kb.ps1 search "decision title"
```

From the permanent checkout or production environment:

```powershell
flux-kb lint
flux-kb status
flux-kb remember "Decision title" "Concise durable summary."
flux-kb search "decision title"
flux-kb audit --limit 20
flux-kb forget <memory-id> --reason user_request
flux-kb backfill-codex --source "$HOME\.codex" --dry-run
flux-kb export-wiki --output private\wiki-export
flux-kb crawl add E:\Projects --name projects --strict-indexing
flux-kb crawl sync --root projects
flux-kb crawl sync --path E:\Projects\README.md
flux-kb crawl watch enable --root projects
flux-kb crawl watch probe --timeout 2
flux-kb crawl watch run
flux-kb host-agent status
flux-kb host-agent run
flux-kb crawl backfill --kind all --limit 20
flux-kb crawl backfill --root docs --family office --limit 20
flux-kb search-index status --root projects
flux-kb search-index sync --owner-class all --root projects --limit 250
flux-kb search-index rebuild --owner-class all --root projects --limit 100
flux-kb maintenance reprocess --all-roots
flux-kb maintenance reprocess --root projects --force --confirm --clear-caches ocr,asr,vision --process --limit 1000 --max-passes 2
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
flux-kb code status --root docs
flux-kb code search build_invoice --root app --language python --relationship call --path-glob "src/*.py"
flux-kb code symbol OrderService.build_invoice
flux-kb code feedback add --query "redacted local query" --root app --miss-category missing_symbol --expected-symbol OrderService.build_invoice
flux-kb code feedback summary --root app
flux-kb diagnostics all --root docs --status blocked_by_policy --family office --include-details
flux-kb diagnostics remediate retry_corpus_job --target-type job --target-id <job-id> --root docs --family office --reason "dependency fixed"
flux-kb diagnostics remediate repair_asset_statuses --target-type root --root docs --reason "operator cleanup"
flux-kb diagnostics remediate clear_completed_errors --target-type root --root docs --reason "operator cleanup"
flux-kb crawl requeue-svg --root docs --limit 1000
flux-kb automation status
flux-kb automation run --mode guarded --limit 25
flux-kb automation actions --status all --limit 25
flux-kb search-index status --root projects
flux-kb search-index sync --owner-class all --root projects --limit 250
flux-kb search-index rebuild --owner-class all --root projects --limit 100
flux-kb governance run --mode shadow --limit 25
flux-kb governance actions list --status proposed --limit 25
flux-kb governance actions apply <action-id> --rationale "reviewed sanitized evidence" --confirm
flux-kb governance actions recover <action-id> --rationale "operator rollback" --confirm
flux-kb governance digest
flux-kb governance policy
flux-kb crawl doctor
flux-kb settings list
flux-kb settings set retrieval.token_budget 1600
# Before public/shared release hardening:
flux-kb settings set privacy.redactions.enabled true
flux-kb mail profile add-imap --name gmail-capture --account me@gmail.com --folder FluxCapture --spool private\mail-spool\gmail-capture
flux-kb mail oauth gmail start --profile gmail-capture --client-config private\google-oauth-client.json
flux-kb mail oauth status --profile gmail-capture
flux-kb mail post-process dry-run --profile gmail-capture
flux-kb mail post-process events --profile gmail-capture
flux-kb mail profile add-outlook --name outlook-catchup --folder "Mailbox - Me\Inbox\Flux Capture" --spool private\mail-spool\outlook-catchup
flux-kb outlook-host status
flux-kb outlook-host sync --profile outlook-catchup
flux-kb outlook-host run
flux-kb mail status
flux-kb mail sync --profile gmail-capture
```

Use `--strict-indexing` for go-live roots. Strict roots do not treat
`metadata_only` files as indexed knowledge: unsupported metadata-only outcomes
are blocked visibly as `blocked_by_policy`, real missing extractor dependencies
remain `blocked_missing_dependency`, invalid/corrupt source files are
`blocked_invalid_source`, and retrieval filters out any remaining legacy
metadata-only chunks.
Use `flux-kb crawl edit <root> --allow-metadata-only` only for a limited pilot
root where metadata-only discovery is intentional.

`private/` is ignored by Git. Review any wiki export before sharing it outside
the machine.

## Environment

`FLUX_KB_DATABASE_URL` defaults to:

```text
postgresql://flux:flux@localhost:5432/flux_llm_kb
```

Override it in `.env` or the shell when you want a different local database.

## Runtime Settings

Most operational values are exposed through `flux-kb settings` and the dashboard
Settings tab. Configuration is settings catalog-backed and cross-platform; it
does not use the Windows Registry. Environment variables override database
settings and appear as read-only effective values. Settings that require reload,
component restart, or search-index rebuild require confirmation and create runtime
control requests.

Global crawler include/exclude globs are also settings. Per-root glob policy can
inherit those defaults, extend them with root-specific lines, or override them
entirely. The Corpus dashboard shows the effective policy for each root.

V2.8 acceleration foundation settings are also catalog-backed. The default cache
root resolves under the production install root when `FLUX_KB_INSTALL_ROOT` is
set, otherwise under the local user cache. `flux-kb acceleration status` and the
dashboard Performance tab show CPU/disk hints, optional NVIDIA and ONNX Runtime
availability, local model-server state, Docker container CPU/memory/writable
layer/block-I/O usage, cache directories, and worker-family queue counts. Local
vision inference is enabled by default for the local
loopback/Ollama path and accepts only loopback HTTP(S) URLs such as
`http://127.0.0.1:11434`, the Docker host gateway
`http://host.docker.internal:11434`, or the internal production Compose service
URL `http://ollama:11434`.
Media ASR is controlled by `acceleration.asr.enabled`,
`acceleration.asr.provider`, `acceleration.asr.model`,
`acceleration.asr.base_url`, `acceleration.asr.model_path`, and
`acceleration.asr.max_duration_seconds`, which now bounds one staged ASR segment
rather than the total media duration.
ASR cache text follows `privacy.redactions.enabled`; entries live under the
configured ASR cache directory and worker-family telemetry reports ASR cache
hits, misses, and segment counts.
Recursive archive/container extraction is controlled by
`crawler.container_max_depth`, `crawler.container_max_members`,
`crawler.container_max_total_bytes`, and `crawler.container_max_member_bytes`.
Watcher backend policy is controlled by `watcher.backend` or the
`FLUX_KB_WATCHER_BACKEND` environment override. Valid values are `auto`,
`watchdog`, and `polling`; `auto` prefers the native watchdog backend and records
the polling fallback reason when watchdog is unavailable. Use `flux-kb crawl
watch probe --timeout <seconds>` to run a temporary-directory create/update/delete
probe. The probe does not touch private watched roots.
`crawler.hash_parallelism` defaults to conservative serial hashing. Incremental
scan manifests record path fingerprints and expose `manifest_skipped_unchanged`
counters when unchanged files skip expensive hashing/extraction. Raising hash
parallelism enables bounded concurrent content hashing for changed files while
preserving deterministic scan ordering, manifest skip behavior, lock fallback,
and serial local parser extraction.
The acceleration status also includes deterministic benchmark fixture summaries
and durable benchmark history for text-heavy, Office/PDF-heavy,
archive/container-heavy, image-heavy, and audio/video-heavy synthetic roots.
Run `flux-kb acceleration benchmark run --fixture <name|all> --files <n>
--mode <scan|soak|watcher|model|all>` and
inspect prior metadata-only runs with `flux-kb acceleration benchmark history
--fixture <name> --mode <scan|soak|watcher> --label <label> --warm-state
<cold|warm> --scope-type <synthetic|monitored_root|path>
--deployment-label <label> --limit <n>`. Scan mode supports `--passes`; pass 1
is cold and later passes reuse an in-memory manifest as warm scans. Add
`--scope root --root <name>` or `--scope path --path <path>` to record
aggregate-only calibration for opted-in monitored roots; host-agent roots are
handled by the host agent. Add `--deployment-label <label>` to compare before
and after updates without storing private paths. Soak mode supports
`--workers` and `--family`, creates benchmark-tagged synthetic jobs through the
normal worker cap logic, and purges them after the run. Watcher mode runs the
temporary watcher probe and records backend policy, selected backend, fallback
reason, event counts, and latency. Model mode records local-only model/tool
readiness, warm/cold timings, cache signals, and blocked dependency counts.
Labels, deployment labels, and `--compare-label` support before/after
comparisons without changing runtime settings. Benchmark storage records fixture
names, mode, labels, scope type, stable scope hashes, counts, timings, cache
counters, hash-parallelism, worker-count, manifest-skip, model/tool telemetry,
backend/provider metadata, and sanitized summaries only; it does not store raw
text, mail contents, private watched roots, credentials, or embeddings.
The indexer reliability gate aggregates this metadata-only benchmark history
with sanitized worker-family and watcher evidence. Use
`flux-kb acceleration reliability status` to inspect readiness, and
`flux-kb acceleration reliability run --scope root --root <name> --label <label>`
to run synthetic reliability, scoped host/cloud calibration, and tuning evidence
under one label. Add `--full` when you want synthetic reliability, scoped
host/cloud evidence for enabled roots, cache readiness, and tuning comparison
evidence in one pass. The gate reports `ready`, `partial`, `blocked`, or
`not_run`, keeps `settings_mutated: false`, and emits manual follow-up commands
for tuning candidates. It does not apply settings, change worker caps, read raw
content, or change VSS settings/provider-specific acceleration without fresh
operator evidence.
Use `flux-kb acceleration reliability roots` to inspect sanitized readiness for
all enabled monitored roots, and `flux-kb acceleration reliability run --scope
all-roots --full` when you need a read-only all-root evidence pass. Use
`flux-kb acceleration evidence` to inspect the combined operator evidence report
with VSS validation/provider gate decisions; those gates can become
`eligible_for_design` but never change VSS settings or enable provider
acceleration.
Code index diagnostics are available with `flux-kb code status`, `flux-kb code
search`, and `flux-kb code symbol`; privacy-safe miss feedback is available with
`flux-kb code feedback add|summary`. Operational evidence summaries are
available with `flux-kb diagnostics retrieval|watcher|workers|jobs|mail|all`,
optionally filtered by `--root`, `--status`, `--family`, `--since-hours`, and
`--include-details`. Diagnostic rows can include confirmation-gated remediation
actions. Use `flux-kb diagnostics remediate retry_corpus_job` for retryable
failed or dependency-blocked corpus jobs, `run_backfill` for scoped root/family
backfill, and `repair_asset_statuses` or `clear_completed_errors` for
root-scoped cleanup. These actions append audit events and do not mutate runtime
settings.
Guarded operator automation is available with `flux-kb automation
status|run|actions` and the dashboard Automation tab. Recurring automation is
default disabled through `operator.automation.enabled=false`; the default mode is
`operator.automation.mode=guarded`. The allowlist is intentionally narrow:
evidence refreshes, already-approved capture ingestion, safe diagnostic
recovery, search-index sync/rebuild, and governance shadow proposal
runs. Deletes, destructive mail policies, OAuth, host startup, restart or
reindex settings, capture approve/reject decisions, high-risk governance, local
file open/reveal, and ambiguous actions remain manual. Guarded automation audit
rows store sanitized evidence and report `settings_mutated: false`.
Evaluated memory governance is available with `flux-kb governance run`,
`flux-kb governance actions list`, `flux-kb governance actions apply`,
`flux-kb governance actions recover`, `flux-kb governance digest`, and
`flux-kb governance policy`. Run `flux-kb retrieval benchmark run --suite
governance-shadow` before applying proposals; apply is blocked until the latest
persisted benchmark has zero guardrail failures and proposal precision meets
`governance.librarian.min_shadow_precision` (default `0.80`). Governance
responses are sanitized, include `settings_mutated: false`, and never expose raw
memory text, private paths, raw queries, snippets, embeddings, local model
prompts, or local model outputs.
Worker-family status is available with `flux-kb crawl worker status --family
<name|all>` and reports configured caps, cap pressure, worker-family
backpressure, oldest pending age, slow recent jobs, retry/lock transitions,
parser cache counters, and manifest skip counters.
Large CSV, TSV, JSON, JSONL, and OpenPyXL-supported workbook files use
sample-first extraction when they exceed the inline extraction limit. Legacy
Excel and OpenDocument spreadsheets converted locally through LibreOffice use
the same sample-first workbook profiling when the converted workbook is still
too large for inline extraction. The stored chunk contains a bounded
schema/profile/sample with row estimates and truncation metadata rather than a
full-file dump.
Search-index refresh uses local Snowflake embeddings through the model-runner
boundary and writes active evidence documents to Vespa. PostgreSQL keeps
source hashes, model identity, dimensions, and sync state without raw source
text. Use `flux-kb search-index status` to inspect coverage,
`flux-kb search-index sync` for bounded refresh work, or
`flux-kb search-index rebuild` when Vespa documents need rebuilding. The same
counters appear in the dashboard Performance tab as indexed, skipped, deleted,
failed, and stale records.
For a broader maintenance refresh, use `flux-kb maintenance reprocess` with
either `--all-roots` or `--root <name>`. Without `--confirm`, the command is a
dry-run inventory only. Confirmed runs require `--force` before they reset
healthy indexed assets, clear stale chunks/code metadata, obsolete pending
extraction/search jobs, mark `search_index_records` pending, and enqueue fresh
corpus plus search-index work. `--clear-caches all` clears only derived OCR,
ASR, vision, thumbnail, parser, and embedding caches; it never clears model
caches, temp files, or private `mail_content` sidecars. Confirmed runs refuse
to start while scoped corpus or search-index jobs are already running.
Qwen reranking uses explicit quantisation settings. The default
`retrieval.reranker_quantization=awq_int4` loads
`retrieval.reranker_awq_model`, defaulting to
`drawais/Qwen3-Reranker-4B-AWQ-INT4`, through the AWQ checkpoint's
`compressed-tensors` metadata. `nf4_4bit` is the separate bitsandbytes NF4
path, and `fp16` is the half-precision path. Legacy aliases are accepted only
for compatibility: `int4_awq` and `awq` canonicalise to `awq_int4`, while
`int4` and `4bit` canonicalise to `nf4_4bit`. The matching environment
overrides are `FLUX_KB_RETRIEVAL_RERANKER_QUANTIZATION`,
`FLUX_KB_RETRIEVAL_RERANKER_MODEL`, and
`FLUX_KB_RETRIEVAL_RERANKER_AWQ_MODEL`. Flux does not silently fall back between
AWQ, NF4, and FP16; model-runner health and rerank metadata report the requested
quantisation, canonical quantisation, backend, base model, AWQ model, and actual
loaded model.

The destructive legacy retrieval purge is deliberately not run by ordinary
database migration. Migration `0033_legacy_retrieval_purge` installs
`run_legacy_retrieval_purge()` for operators to call only after a fresh database
backup, passing retrieval benchmark, PaddleOCR replacement/exclusion for legacy
OCR assets, and search-index coverage for corpus chunks, episodes, and claims.
The procedure retires old hash-vector duplicate clusters and drops the obsolete
embedding table, related indexes, broad body trigram index, and vector extension
when no remaining object depends on it.

Governance librarian settings are catalog-backed and default conservative:
`governance.librarian.enabled=false`,
`governance.librarian.interval_seconds=3600`,
`governance.librarian.mode=shadow`,
`governance.librarian.max_actions_per_run=25`,
`governance.librarian.min_shadow_precision=0.8`,
`governance.librarian.auto_apply_enabled=false`,
`governance.librarian.auto_apply_risk_ceiling=low`,
`governance.librarian.digest_retention_days=30`, and
`governance.librarian.protected_memory_rules` for rule-based protected-memory
thresholds. Optional local rationale settings
`governance.local_model_rationale.enabled` and
`governance.local_model_rationale.model` are local-only and fall back to
deterministic rule-based rationales when unavailable. The corpus worker runs the
librarian only when enabled, stays shadow-only unless auto mode and auto-apply
are explicitly configured, and auto-applies only low-risk claim `mark_review`,
`stale_tag`, and `deprioritize` actions that pass the benchmark gate.

## Host Filesystem Agent

When Flux services run in Docker, Windows paths such as `E:\Projects` are not
valid Linux container paths. Start the host agent in the logged-in desktop
session to enable dashboard folder browsing and host-side crawl/watch work:

```powershell
flux-kb host-agent run
```

The dashboard uses this bridge for `Browse`, path validation, and host-path sync
requests. If it is not running, the UI keeps manual entry available and shows a
clear `host_agent_offline` state.

The host agent also performs startup reconciliation and periodic reconciliation
for host-owned watched roots. If the PC, Docker, or the host agent was offline,
the next startup scan compares files against persisted `source_assets` state and
indexes new files, re-indexes changed files, and marks deleted files as removed
from retrieval without requiring a manual backfill. Empty folders are a no-op.
By default, `flux-kb host-agent run` also starts the host-side RabbitMQ consumer
for `flux.commands.corpus_host_agent`, so host-only corpus jobs are processed on
the Windows host and ACKed through RabbitMQ after durable state updates.

## Mail Capture

Mail capture is local-first. IMAP profiles monitor configured folders or labels
and export messages into `private\mail-spool\<profile>`. Gmail profiles should
use installed-app OAuth2/XOAUTH2 rather than basic passwords. Create a private
Google OAuth desktop client JSON outside Git, run `flux-kb mail oauth gmail
start`, open the returned URL, and let the local callback store a masked refresh
token. Flux refreshes short-lived access tokens before IMAP login and reports
token health in the dashboard. The default post-process policy moves messages to
a processed folder or removes the capture label; permanent trash/delete is not
the default.
Completed exports live under `ready\<export_id>` with `manifest.json`,
`body.txt`, optional `body.html`, the original `.eml` or `.msg`, and
`attachments\*`. Flux indexes the manifest metadata normally. It makes the
canonical `body.txt` and attachment files searchable through private disk
content sidecars: PostgreSQL keeps blank chunk bodies plus sidecar
references/hashes and vectors, not plaintext mail body or attachment chunk
text. Raw message backups and duplicate HTML bodies stay on disk as spool
artifacts and are skipped by the searchable corpus index.

Profile post-processing supports `none`, `move_to_processed`, `remove_label`,
and `trash`. Gmail profiles use Gmail IMAP label commands for label operations
and Trash handling. Generic IMAP profiles use folder copy plus delete/expunge
only for policies that require it, and can copy to `trash_folder` before deleting
when trash is configured. Outlook COM profiles export through the local Outlook
host and should keep post-processing set to `none`; non-`none` Outlook COM
policies are reported as blocked configuration instead of issuing IMAP commands.
`trash` requires explicit destructive confirmation in
CLI/API/dashboard profile metadata. Use `flux-kb mail
post-process dry-run --profile <name>` before enabling a new policy, then review
recent command outcomes with `flux-kb mail post-process events --profile
<name>`. Event views show operational metadata and errors, not raw mail body
content.

During rollout, start mail profiles with `none`, `remove_label`, or
`move_to_processed`. Do not use `trash` for important mailboxes until a dry-run,
post-process event review, and a small pilot label/folder have all succeeded.
Before adding important mailboxes, run diagnostics and the managed-mail repair
path so any legacy plaintext mail chunks are converted to sidecar-backed chunks
and search-index sync can rebuild Vespa documents from disk sidecars.

Outlook COM profiles are for catch-up from selected classic Outlook folder
paths. They do not need an IMAP server or account value in Flux; classic Outlook
owns the mailbox connection. They use local Outlook automation and write into
the same spool shape as IMAP, but the automation runs in a separate Windows host
process:

```powershell
flux-kb outlook-host run
```

The dashboard and Docker-hosted API create sync requests and broker commands.
The Windows host consumes exact Outlook request ids from `flux.commands.outlook`,
exports messages through classic Outlook COM, reports heartbeat/status, and
indexes the ready spool. Host-agent file roots enqueue to
`flux.commands.corpus_host_agent`; `flux-kb host-agent run` starts that
host-side consumer by default so Docker workers do not pick work for host-only
paths. If the host is not running, the dashboard shows `host_offline` and the
commands above. The old
Outlook DB-claim loop is development-only behind `--legacy-db-loop` and
`FLUX_KB_ALLOW_INLINE_WORKERS=1`.

## Dashboard Development

The dashboard is a React/Vite app under `dashboard/` and is served by FastAPI at
`http://127.0.0.1:8765/dashboard`. Overview is a friendly read-only status page,
Automation shows guarded run state and manual-required work, Diagnostics owns
structured errors and safe remediation, Performance owns acceleration and
reliability evidence, State owns model activity, scheduler/resident model state,
and live job updates, Retrieval owns code diagnostics, and Settings owns Codex
hooks, deployment, runtime actions, restart, and reindex settings. Dashboard
load uses one `GET /api/dashboard/snapshot` call; live updates arrive through
`WS /api/dashboard/stream`, with manual **Refresh data** remaining a one-shot
snapshot reload. Normal WebSocket tab changes, reconnects, and client
disconnects do not mark the dashboard degraded; only real broker/subscription
failures surface as stream-status errors. Dashboard job remediation includes a
guarded operator retry for ASR/media jobs blocked by GPU capacity, routed
through the existing corpus-job retry action without changing ASR or GPU
settings. Use the
helper script whenever dashboard code changes; it rebuilds assets and refreshes
the running deployment:

The Review tab includes Governance Automation, Digest, Guardrails, and Recovery
panels for proposal review, shadow runs, confirmed apply/recover actions, and
bounded local digest status.

```powershell
.\scripts\start-dashboard-dev.ps1
.\scripts\status-dashboard-dev.ps1
.\scripts\stop-dashboard-dev.ps1
```

When Docker is on PATH, the script runs the local event stack: PostgreSQL,
RabbitMQ, API, outbox relay, event scheduler, command workers, and callback
worker, plus durable event-subscriber workers for audit/dashboard/diagnostics
event journals and `gpu-eviction-worker` for brokered resident-model unload
requests. Long-running REST/MCP/CLI requests enqueue work and return accepted
operation metadata; the relay publishes from `message_outbox`, RabbitMQ handles
delivery/retry, and workers ACK only after durable state and event writes. If
Docker is unavailable on the current PATH, the script falls back to a local
FastAPI process on the same URL; in that fallback mode, run the needed event
processes explicitly, for example `flux-kb event outbox relay`,
`flux-kb event scheduler run`, `flux-kb event worker run --queue
flux.commands.corpus`, `flux-kb event worker run --queue
flux.commands.corpus_host_agent --worker-id host-agent`, and
`flux-kb event worker run --queue flux.commands.gpu_eviction`.
