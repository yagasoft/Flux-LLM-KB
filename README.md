# Flux-LLM-KB

Flux-LLM-KB is a local-first knowledge kernel for agent workflows. It is designed
to help coding agents recall prior work without injecting large, noisy memory
files into every conversation.

The project targets PostgreSQL with pgvector as the primary durable store, with
interfaces for:

- MCP tools for Codex and other MCP-capable agents
- A command-line interface for local automation
- A REST API for non-MCP integrations
- Codex hooks and a personal plugin for global, cross-workspace use

## Current V1 Kernel

- PostgreSQL schema for episodes, sources, entities, claims, relations,
  embeddings, audit events, capture jobs, workspace scopes, and retention
  policies.
- pgvector, full-text search, JSONB/GIN, trigram fuzzy matching, and hybrid RRF
  ranking.
- Deterministic local `flux-hash-v1` embeddings so the vector pipeline works
  without sending private text to an external service.
- CLI commands for init, migration, status, search, remember, audit, forget,
  Codex backfill queueing, wiki export, lint, and doctor checks.
- MCP and REST entrypoints over the same service layer.
- Codex personal plugin scaffold with hook scripts.

## Quick Start

```powershell
python -m pip install -e .[dev]
.\scripts\check-docker.ps1
.\scripts\start-postgres.ps1
flux-kb migrate
flux-kb doctor
flux-kb remember "Project decision" "Use PostgreSQL and pgvector from day one."
flux-kb search "pgvector decision"
```

Docker Compose is the default runtime profile. If Docker is missing, the setup
scripts fail clearly instead of silently switching storage engines.

See [docs/setup.md](docs/setup.md) and [docs/integrations.md](docs/integrations.md).

## Safety Model

This public repository stores only code, documentation, migrations, test
fixtures, and example configuration. It must never store live memories, raw
transcripts, private workspace data, secrets, embeddings from private content,
or generated private wiki exports.

See [docs/safety.md](docs/safety.md) for the data boundary.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md).
