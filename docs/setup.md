# Setup

Flux-LLM-KB is local-first. Runtime data belongs in your local PostgreSQL
database, not in this repository.

## Prerequisites

- Python 3.11+
- Git
- GitHub CLI for repository work (optional after bootstrap)
- Docker Desktop with `docker compose`

The default PostgreSQL runtime uses `pgvector/pgvector:pg16`. If Docker or
Compose is not available, `scripts/check-docker.ps1` exits with a clear error.
The normal application runtime is Docker-backed: PostgreSQL, FastAPI, the
dashboard, IMAP workers, corpus crawlers, and extraction workers live in
containers. Outlook COM is the exception; it runs as a Windows host process
outside Docker.

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
- `D:\FluxLLMKB\data`: PostgreSQL bind-mounted data on the D drive
- `D:\FluxLLMKB\logs`: API, worker, host-agent, and Outlook-host logs
- `D:\FluxLLMKB\runtime`: process heartbeat/status files
- `D:\FluxLLMKB\backups`: future local backup/export target

The repository remains source code only. Production Docker Compose uses prebuilt
local image tags, not `build.context: .`, and it bind-mounts only the deployed
private/data/log paths. API access remains local at
`http://127.0.0.1:8765/dashboard`.

Update an existing deployment from the current checkout with:

```powershell
.\scripts\deploy\update-flux.ps1 -RestartHostTasks
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
python -m pip install -e .[dev,corpus]
```

Install Outlook COM support on Windows when you want local Outlook catch-up:

```powershell
python -m pip install -e .[mail]
```

External tools are detected at runtime and reported by `flux-kb crawl doctor`.
`ffprobe`/`ffmpeg`, `tesseract`, and local transcription runtimes are never
called through cloud services by default.
Deferred ASR uses local faster-whisper only when `acceleration.asr.model_path`
points at an existing local model. Flux passes `local_files_only=True`, so it
does not perform a remote model download; missing `ffmpeg`, `faster-whisper`, or
model path leaves the related media job in `blocked_missing_dependency`.

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
flux-kb remember "Decision title" "Redacted durable summary."
flux-kb search "decision title"
flux-kb audit --limit 20
flux-kb forget <memory-id> --reason user_request
flux-kb backfill-codex --source "$HOME\.codex" --dry-run
flux-kb export-wiki --output private\wiki-export
flux-kb crawl add E:\Projects --name projects
flux-kb crawl sync --root projects
flux-kb crawl sync --path E:\Projects\README.md
flux-kb crawl watch enable --root projects
flux-kb crawl watch probe --timeout 2
flux-kb crawl watch run
flux-kb host-agent status
flux-kb host-agent run
flux-kb crawl backfill --kind all --limit 20
flux-kb crawl backfill --kind embeddings --limit 20
flux-kb crawl backfill --root docs --family office --limit 20
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
flux-kb diagnostics all --root docs --status blocked_missing_dependency --family office --include-details
flux-kb diagnostics remediate retry_corpus_job --target-type job --target-id <job-id> --root docs --family office --reason "dependency fixed"
flux-kb diagnostics remediate repair_asset_statuses --target-type root --root docs --reason "operator cleanup"
flux-kb diagnostics remediate clear_completed_errors --target-type root --root docs --reason "operator cleanup"
flux-kb embeddings status
flux-kb embeddings enqueue --owner-class corpus --root projects --limit 100
flux-kb embeddings backfill --owner-class all --limit 100
flux-kb crawl doctor
flux-kb settings list
flux-kb settings set retrieval.token_budget 1600
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
settings tab. Configuration is settings catalog-backed and cross-platform; it
does not use the Windows Registry. Environment variables override database
settings and appear as read-only effective values. Settings that require reload,
component restart, or embedding reindex require confirmation and create runtime
control requests.

Global crawler include/exclude globs are also settings. Per-root glob policy can
inherit those defaults, extend them with root-specific lines, or override them
entirely. The Corpus dashboard shows the effective policy for each root.

V2.8 acceleration foundation settings are also catalog-backed. The default cache
root resolves under the production install root when `FLUX_KB_INSTALL_ROOT` is
set, otherwise under the local user cache. `flux-kb acceleration status` and the
dashboard Health tab show CPU/disk hints, optional NVIDIA and ONNX Runtime
availability, local model-server state, cache directories, and worker-family
queue counts. Local model probing is disabled by default and accepts only
loopback HTTP(S) URLs such as `http://127.0.0.1:11434`.
Media ASR is controlled by `acceleration.asr.enabled`,
`acceleration.asr.model_path`, and `acceleration.asr.max_duration_seconds`.
Redacted ASR cache entries live under the configured ASR cache directory and
worker-family telemetry reports ASR cache hits, misses, and segment counts.
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
content, or unblock VSS/provider-specific acceleration without fresh operator
evidence.
Use `flux-kb acceleration reliability roots` to inspect sanitized readiness for
all enabled monitored roots, and `flux-kb acceleration reliability run --scope
all-roots --full` when you need a read-only all-root evidence pass. Use
`flux-kb acceleration evidence` to inspect the combined operator evidence report
with VSS/provider gate decisions; those gates can become `eligible_for_design`
but never enable VSS or provider acceleration.
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
Embedding refresh uses the local deterministic `flux-hash-v1` provider by
default. New vectors keep source hashes and cache keys in embedding metadata
without raw source text. Use `flux-kb embeddings status` to inspect coverage,
`flux-kb embeddings enqueue` to queue `corpus_embed` jobs, or `flux-kb
embeddings backfill` for an immediate bounded refresh. The same counters appear
in the dashboard Health acceleration panel as vectors processed, unchanged
items skipped, batches, and cache hits/misses.

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

Profile post-processing supports `none`, `move_to_processed`, `remove_label`,
and `trash`. Gmail profiles use Gmail IMAP label commands for label operations
and Trash handling. Generic IMAP profiles use folder copy plus delete/expunge
only for policies that require it, and can copy to `trash_folder` before deleting
when trash is configured. `trash` requires explicit destructive confirmation in
CLI/API/dashboard profile metadata. Use `flux-kb mail
post-process dry-run --profile <name>` before enabling a new policy, then review
recent command outcomes with `flux-kb mail post-process events --profile
<name>`. Event views show operational metadata and errors, not raw mail body
content.

Outlook COM profiles are for catch-up from selected classic Outlook folder
paths. They use local Outlook automation and write into the same spool shape as
IMAP, but the automation runs in a separate Windows host process:

```powershell
flux-kb outlook-host run
```

The dashboard and Docker-hosted API create sync requests. The Windows host polls
and claims those requests, exports messages through classic Outlook COM, reports
heartbeat/status, and indexes the ready spool. If the host is not running, the
dashboard shows `host_offline` and the command above.

## Dashboard Development

The dashboard is a React/Vite app under `dashboard/` and is served by FastAPI at
`http://127.0.0.1:8765/dashboard`. Use the helper script whenever dashboard code
changes; it rebuilds assets and refreshes the running deployment:

```powershell
.\scripts\start-dashboard-dev.ps1
.\scripts\status-dashboard-dev.ps1
.\scripts\stop-dashboard-dev.ps1
```

When Docker is on PATH, the script runs `docker compose up -d --build postgres
api worker`. If Docker is unavailable on the current PATH, it falls back to a
local FastAPI process on the same URL so browser refresh still shows the current
build; in that fallback mode, start `flux-kb crawl worker run` separately when
you need continuous local job processing.
