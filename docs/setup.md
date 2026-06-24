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
flux-kb crawl watch run
flux-kb host-agent status
flux-kb host-agent run
flux-kb crawl backfill --kind all --limit 20
flux-kb crawl backfill --kind embeddings --limit 20
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
The acceleration status also includes deterministic benchmark fixture summaries
for text-heavy, Office/PDF-heavy, archive/container-heavy, image-heavy, and
audio/video-heavy roots.
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
