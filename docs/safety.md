# Safety and data boundaries

FluxKnowledge retains useful local knowledge while preventing unauthorised
disclosure, unsafe parser access and accidental external acquisition.

## Repository boundary

Git may contain source, native SQL migrations, synthetic tests, documentation
and non-secret example configuration. It must not contain live databases,
private documents or embeddings, raw transcripts, mail/spool contents,
credentials, tokens, cookies, connection strings containing credentials, model
payloads or generated private exports. Reference backups belong outside the
workspace and must not become runtime dependencies.

## Trusted local presentation

The private UI, direct-loopback REST, user-invoked CLI/MCP, diagnostics, audit
and search may show useful retained-derived paths, code, hashes, symbols and
parser evidence. They still withhold passwords, keys, tokens, session material,
credential-bearing URIs and secret headers. Public/shared/exported output needs
its own sanitisation. Local presentation permission is not external disclosure
permission.

## Retained processing and lifecycle

Processors consume only bounded, checksum-verified retained artifacts bound to
the durable revision. They do not reopen originals. Archive path and size
checks, parser limits, completion ownership and publication fences remain
mandatory. C# analysis does not build projects, restore dependencies, execute
code or load project-supplied generators/analyzers.

Pause, suppression, deletion and supersession are enforced against durable
state. Deletion removes only application-owned data after draining work and
preserving shared survivors. Source files and `J:\Models` remain outside that
cleanup boundary. Unknown process termination or unsettled GPU ownership does
not grant permission to retry or publish.

## Model and runtime boundary

All reusable model payloads and download staging belong under `J:\Models`.
Check the central inventory and relevant existing provider caches before every
proposed acquisition. A missing, corrupt or unavailable artifact causes a
refusal; it never triggers an automatic download or another-drive fallback.
Acquisition requires exact identities, immutable revisions, expected transfer
bytes, destination and explicit approval. Approved transfers must deduplicate,
verify and publish atomically with receipts.

Normal loading uses verified local paths and offline provider settings. Tests
use synthetic fixtures. Startup, retries, publication and rollback cannot
download models or modify the cache to repair a loader failure.

## Windows and integration boundaries

HTTP/MCP and CLI use the fixed direct-loopback origin and reject forwarding,
proxies and redirects. Mutations use closed actions, command-bound confirmations,
idempotency keys, row versions and durable receipts. Responses and cursors are
bounded and scoped to retained projections.

Outlook and Visio run through the logged-in desktop companion under explicit
configuration. Visio refuses an existing user session and requires proven
process cleanup; it is not a general unattended Office automation service.
Normal application startup cannot change Codex registrations.

Deployment, restart, migration, clean-slate installation, VSS configuration and
plugin lifecycle actions require approval for the concrete operation. Review
the verification evidence and recovery conditions before invoking them.
[Setup](setup.md) distinguishes incremental updates from destructive installation.
