# Safety And Data Boundary

Flux-LLM-KB is intended to remember useful work without leaking private data.

## Public Repository Boundary

Allowed in Git:

- source code
- tests using synthetic fixtures
- migrations
- documentation
- example configuration
- generated documentation that contains no private memory data

Forbidden in Git:

- live memory databases
- raw transcripts
- private workspace files
- credentials, tokens, API keys, cookies, or session material
- embeddings created from private content
- generated private wiki exports
- private user or customer data

## Runtime Boundary

Runtime data is local by default. The first implementation stores it in a local
PostgreSQL database and excludes all runtime paths from Git.

## Capture Rules

- Redact before persistence.
- Record provenance for every promoted claim.
- Preserve superseded facts instead of overwriting them silently.
- Audit every write, delete, export, and bulk operation.
- Prefer compact task briefs over large memory injection.

