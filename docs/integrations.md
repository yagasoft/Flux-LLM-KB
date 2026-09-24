# Native v1 integrations

FluxKnowledge exposes one private, direct-loopback native v1 contract for
Codex and local scripts. All supported clients share the native application facade.

## Boundary

- HTTP and MCP bind only to `http://127.0.0.1:5137`.
- REST routes live under `/api/v1`; MCP is served at `/mcp`.
- The CLI is a thin HTTP client of that same loopback contract. It disables
  redirects and proxies.
- Requests can read only bounded, application-retained projections. Parser
  inputs never accept a source-original path, and every returned value is
  secret-filtered.
- The contract has stable lower-camel-case JSON envelopes and reason codes.
  A response contains `ok`, `result`, `reasonCode`, `message`, `retryable`,
  and, where applicable, an operation identifier.

## Native tools

| MCP tool | REST | CLI |
| --- | --- | --- |
| `knowledge.search` | `POST /api/v1/knowledge/search` | `FluxKnowledge.Cli knowledge search` |
| `knowledge.write` | `POST /api/v1/knowledge/actions/preview` or `/commit` | `FluxKnowledge.Cli knowledge write --preview|--commit` |
| `knowledge.graph` | `POST /api/v1/knowledge/graph/query` | `FluxKnowledge.Cli knowledge graph` |
| `code.query` | `POST /api/v1/code/query` | `FluxKnowledge.Cli code query` |
| `code.write` | `POST /api/v1/code/actions/preview` or `/commit` | `FluxKnowledge.Cli code feedback --preview|--commit` |
| `corpus.query` | `POST /api/v1/corpus/query` | `FluxKnowledge.Cli corpus query` |
| `corpus.search` | `POST /api/v1/corpus/search` | `FluxKnowledge.Cli corpus search` |
| `corpus.read` | `POST /api/v1/corpus/read` | `FluxKnowledge.Cli corpus read` |
| `corpus.write` | `POST /api/v1/corpus/actions/preview` or `/commit` | `FluxKnowledge.Cli corpus write --preview|--commit` |
| `operations.status` | `GET /api/v1/operations/status` | `FluxKnowledge.Cli operations status` |
| `operations.audit` | `POST /api/v1/operations/audit/query` | `FluxKnowledge.Cli operations audit` |

`operations.status` accepts the bounded views `overview`, `sources`, `jobs`,
`workers`, `processors`, and `recovery`. Code, corpus and audit queries use
bounded pages and opaque query-bound cursors.

`corpus.search` searches published retained text across `all`, one registered
`root_id`, or a canonical Windows `cwd` workspace. It returns bounded canonical
passages, source and owner identity, typed locations where retained provenance
supports them, and an opaque `evidence_ref`. `corpus.read` accepts that reference
and returns up to 4,096 UTF-16 units of surrounding retained context while
rechecking current publication and deletion state. Both operations have a
256 KiB response limit. Their `retrieval_mode` is `lexical`; learned semantic
ranking remains a separate evaluated increment.

## Mutations

`knowledge.write`, `code.write`, and `corpus.write` are closed allowlists.
They cannot execute arbitrary programs, URLs, database commands or paths.

1. Send the complete command to its `preview` route or tool mode.
2. Keep the returned opaque `confirmationId`; it expires after a short period
   and is bound to the command fingerprint and target row versions.
3. Send the identical command to `commit` with that confirmation and a
   caller-provided idempotency key. REST uses the `Idempotency-Key` header;
   MCP uses `idempotency_key`; CLI uses `--confirmation-id` and
   `--idempotency-key`.

Replaying the same key and request returns the original receipt. A key reused
for a different request conflicts. Existing corpus lease and fencing rules
remain authoritative. A cancelled request creates no mutation before commit;
after a durable commit, retrying the same idempotency key recovers the receipt.

## Codex plugin

The application-owned marketplace is `I:\FluxKnowledge\CodexPlugin`. Its
native manifest references only `http://127.0.0.1:5137/mcp`. The registration
is designed to be idempotent and to replace only the known FluxKnowledge
registration, leaving unrelated Codex configuration unchanged.

`FluxKnowledge.Cli codex plugin status` is the normal non-mutating diagnostic.
`FluxKnowledge.Cli codex plugin repair` requires a typed, separately authorised
go-live authority; ordinary CLI composition denies it. Normal application
startup does not register, repair or otherwise alter Codex plugin state.

## Operational actions and planned extensions

External listeners and remote MCP are outside this private local contract.
Deployment, migration, VSS configuration and plugin lifecycle changes are
operational actions governed by [setup](setup.md), rather than normal application
startup or development verification.

The native plugin is verified by its exact identity and installed/enabled state.
Unrelated plugin registrations are not removed. The application has no plugin
retirement or migration task.

The [retrieval design](design/corpus-retrieval.md) records the scoped lexical
contract. Current hybrid search still uses a deterministic embedding baseline;
learned semantic corpus retrieval remains a separate delivery.
