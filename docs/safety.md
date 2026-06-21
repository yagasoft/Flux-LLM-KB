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
- mail spool contents, exported `.eml`/`.msg` files, attachments, heartbeat files,
  OAuth tokens, app passwords, or generated private mail configs
- local dashboard runtime PID/log files and Outlook host heartbeat/error payloads

## Runtime Boundary

Runtime data is local by default. The first implementation stores it in a local
PostgreSQL database and excludes all runtime paths from Git.

Mail ingestion writes raw messages and attachments to local private spool paths
before indexing. Keep those paths under ignored private directories and review
exports before sharing.

The Outlook COM bridge runs outside Docker under the logged-in Windows user. It
must write only to ignored private spool/runtime paths and report status through
the local Flux API or database; no raw mail or credentials belong in Git.

## Capture Rules

- Redact before persistence.
- Record provenance for every promoted claim.
- Preserve superseded facts instead of overwriting them silently.
- Audit every write, delete, export, and bulk operation.
- Prefer compact task briefs over large memory injection.
- Never default to permanently deleting mailbox messages after capture; prefer
  move-to-processed or remove-label policies.
