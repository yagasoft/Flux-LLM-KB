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

## Safety Model

This public repository stores only code, documentation, migrations, test
fixtures, and example configuration. It must never store live memories, raw
transcripts, private workspace data, secrets, embeddings from private content,
or generated private wiki exports.

See [docs/safety.md](docs/safety.md) for the data boundary.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md).

