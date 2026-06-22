# Flux-LLM-KB

Flux-LLM-KB is a local-first knowledge kernel for agent workflows. It is designed
to help coding agents recall prior work without injecting large, noisy memory
files into every conversation.

The project targets PostgreSQL with pgvector as the primary durable store, with
interfaces for:

- MCP tools for Codex and other MCP-capable agents
- A command-line interface for local automation
- A REST API for non-MCP integrations
- A React/Vite operations dashboard served by FastAPI
- Codex hooks and a personal plugin for global, cross-workspace use

## Current Kernel

- PostgreSQL schema for episodes, sources, entities, claims, relations,
  embeddings, audit events, capture jobs, workspace scopes, and retention
  policies.
- pgvector, full-text search, JSONB/GIN, trigram fuzzy matching, and hybrid RRF
  ranking.
- Deterministic local `flux-hash-v1` embeddings so the vector pipeline works
  without sending private text to an external service.
- CLI commands for init, migration, status, search, remember, audit, forget,
  Codex backfill queueing, wiki export, runtime settings, mail ingestion, lint,
  and doctor checks.
- MCP and REST entrypoints over the same service layer.
- Codex personal plugin scaffold with hook scripts.
- Unified React dashboard for health, corpus monitoring, runtime settings, mail
  capture, worker state, and Outlook COM host status.
- IMAP mail capture with Gmail OAuth support and a separate Windows Outlook COM
  host process for selected-folder catch-up.

## Quick Start

```powershell
python -m pip install -e .[dev]
.\scripts\check-docker.ps1
.\scripts\start-postgres.ps1
flux-kb migrate
flux-kb doctor
flux-kb remember "Project decision" "Use PostgreSQL and pgvector from day one."
flux-kb search "pgvector decision"
.\scripts\start-dashboard-dev.ps1
```

Docker Compose is the default runtime profile. If Docker is missing, the setup
scripts fail clearly instead of silently switching storage engines.

The normal Flux API/dashboard/worker runtime is Docker-hosted. Classic Outlook
COM catch-up is intentionally split into `flux-kb outlook-host run` on Windows
because COM must run in the logged-in user session.

See [docs/setup.md](docs/setup.md) and [docs/integrations.md](docs/integrations.md).

## Safety Model

This public repository stores only code, documentation, migrations, test
fixtures, and example configuration. It must never store live memories, raw
transcripts, private workspace data, secrets, embeddings from private content,
or generated private wiki exports.

See [docs/safety.md](docs/safety.md) for the data boundary.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md). Current implementation progress is
tracked in [docs/progress.md](docs/progress.md).
