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
- `capture_jobs`: asynchronous ingestion and consolidation jobs.
- `workspace_scopes`: workspace/project identity and visibility boundaries.
- `retention_policies`: decay and forgetting configuration by memory class.

## Retrieval

Queries combine four signals:

- lexical retrieval from PostgreSQL full-text search
- semantic retrieval from pgvector
- graph traversal through typed relations
- lifecycle scoring from confidence, recency, reinforcement, and supersession

The merged result uses reciprocal rank fusion and then packs a compact task brief
within a strict token budget.

## Integration Surfaces

- MCP exposes memory tools to Codex and other MCP-capable agents.
- CLI supports local automation, diagnostics, migration, and export.
- REST mirrors the MCP operations for clients that do not support MCP.
- Codex hooks enforce preflight retrieval and post-turn capture across workspaces.

