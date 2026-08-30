# Native v1 integrations

FluxKnowledge exposes one private, direct-loopback native v1 contract for
Codex and local scripts. It does not support a legacy service, compatibility
adapter or remote integration path.

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
| `corpus.write` | `POST /api/v1/corpus/actions/preview` or `/commit` | `FluxKnowledge.Cli corpus write --preview|--commit` |
| `operations.status` | `GET /api/v1/operations/status` | `FluxKnowledge.Cli operations status` |
| `operations.audit` | `POST /api/v1/operations/audit/query` | `FluxKnowledge.Cli operations audit` |

`operations.status` accepts the bounded views `overview`, `sources`, `jobs`,
`workers`, `processors`, and `recovery`. Code, corpus and audit queries use
bounded pages and opaque query-bound cursors.

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

## Unsupported and deferred operations

No external listener, remote MCP endpoint, credential bridge, legacy runtime,
or compatibility contract is supported. Deployment, database migration,
plugin lifecycle mutation, VSS configuration and fresh-start execution require
the separately authorised, one-shot `scripts/dev/complete-feature.ps1 -GoLive`
workflow. It can continue only from an absent root and target catalogue or a
same-invocation confirmed wipe; deployment recovery, journals, markers,
adoption, resume, repair and replay are not supported. These actions are not
application-startup or development-verification operations.
