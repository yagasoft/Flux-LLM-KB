# Safety And Data Boundary

Flux-LLM-KB is intended to remember useful work without unauthorised or external disclosure of private data. Trusted local application surfaces may show useful retained-derived content and diagnostics under the [private-PC local visibility policy](superpowers/specs/2026-08-16-private-pc-local-visibility-policy-design.md).

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

The local UI, direct-loopback REST, user-invoked CLI/MCP, diagnostics, audit and
search may expose useful raw retained-derived paths, hashes, code, symbols,
signatures, relationships and parser diagnostics. This permission never includes
passwords, tokens, OAuth/client secrets, private keys, connection strings,
cookies, session material or credential-bearing headers. Public/shared/exported
output remains sanitised.

Mail ingestion writes raw messages and attachments to local private spool paths
before indexing. Keep those paths under ignored private directories and review
exports before sharing.

The Outlook COM bridge runs outside Docker under the logged-in Windows user. It
must write only to ignored private spool/runtime paths and report status through
the local Flux API or database; no raw mail or credentials belong in Git.

## Retained processor boundary

Retained processors, including the C# syntax processor, may read only the
checksum-verified retained artifact bound to the durable revision. They do not
reopen source originals. A retained-derived symbol, signature, relationship or
parser diagnostic is scanned before persistence and local presentation; a
detected secret is withheld with a fixed reason, while an unscannable fact blocks
the whole completion without a partial fact set. Local detail pages may reveal
the remaining useful raw facts, but public/export DTOs must not acquire those
members.

The C# parser is local and syntax-only. It does not invoke Office, Outlook,
cloud/network parsers, model runtimes or code execution. Generated/disposable
SQL catalogues and cached-browser synthetic tests are permitted for validation;
they must be loopback-bound, disposable, and unable to download a browser or
model. Production migrations, deployment, Outlook activation, source-original
rereads and live validation remain explicit approval gates.

## Capture Rules

- Runtime redaction is controlled by `privacy.redactions.enabled`
  (`FLUX_KB_REDACTIONS_ENABLED`). This personal deployment defaults it off so
  local memory, OCR/ASR/vision text, paths, and diagnostic details remain exact.
  Turn it on before any public, shared, or exported deployment that requires
  masking before persistence.
- Record provenance for every promoted claim.
- Preserve superseded facts instead of overwriting them silently.
- Audit every write, delete, export, and bulk operation.
- Prefer compact task briefs over large memory injection.
- Never default to permanently deleting mailbox messages after capture; prefer
  move-to-processed or remove-label policies.
