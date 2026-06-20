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

## Install

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

## Useful Commands

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
flux-kb crawl backfill --kind all --limit 20
flux-kb crawl doctor
flux-kb settings list
flux-kb settings set retrieval.token_budget 1600
flux-kb mail profile add-imap --name gmail-capture --account me@gmail.com --folder FluxCapture --spool private\mail-spool\gmail-capture
flux-kb mail oauth gmail start --profile gmail-capture --client-config private\google-oauth-client.json
flux-kb mail oauth status --profile gmail-capture
flux-kb mail profile add-outlook --name outlook-catchup --folder "Mailbox - Me\Inbox\Flux Capture" --spool private\mail-spool\outlook-catchup
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

Outlook COM profiles are for catch-up from selected classic Outlook folder
paths. They use local Outlook automation and write into the same spool shape as
IMAP.
