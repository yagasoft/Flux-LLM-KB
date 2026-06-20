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
```

`private/` is ignored by Git. Review any wiki export before sharing it outside
the machine.

## Environment

`FLUX_KB_DATABASE_URL` defaults to:

```text
postgresql://flux:flux@localhost:5432/flux_llm_kb
```

Override it in `.env` or the shell when you want a different local database.
