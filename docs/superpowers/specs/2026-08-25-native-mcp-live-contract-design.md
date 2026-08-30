# Native MCP, REST, CLI and Codex plugin v1 design

## Status

Implemented and verified on 2026-08-26. This document defines the native v1
integration contract and go-live preparation work. It does not authorise
deployment, live database migration, model activation, GPU work, Outlook
activation, network parsing, FFmpeg, or clean-slate execution.

## Purpose

Make the native FluxKnowledge application usable as the single local knowledge
service for Codex, local scripts and direct loopback clients. The interface
must be small, deterministic, retained-data-first and consistent across MCP,
REST and CLI. It replaces the decommissioned application contract; it does not
translate or retain that contract.

Any future cutover would require a new, separately approved operational design.
This slice prepares no executable cutover and makes no assertion about an
existing host, database, registration or volume state.

## Approved constraints

- The application is permanently private and single-user. It binds integration
  surfaces to direct loopback only. Useful retained facts, paths, hashes and
  parser details may be returned after the existing bounded secret-disclosure
  policy. Secrets, credentials, connection strings and private keys remain
  protected.
- The only parser inputs are application-owned checksum-verified retained
  bytes. Integration requests never provide a source-original path to a
  parser.
- MCP, REST and CLI use one native v1 contract owned by the Application layer.
  Transport hosts bind that contract directly; no legacy compatibility wrapper,
  bridge or translation layer is introduced.
- All knowledge, memory, graph, code and retained-corpus mutations use explicit
  preview-then-commit confirmation and idempotency.
- Any other expensive or destructive operation also requires confirmation and
  idempotency. Read-only operations do not.
- Native plugin material and a status/repair seam are prepared for the local
  HTTP MCP endpoint. Normal application composition is status-only: it does
  not register, repair or launch a plugin or a legacy runtime.
- The application owns `I:\FluxKnowledge` as its ordinary persistent root.
  Volume Shadow Copy Service (VSS) remains the operating-system-managed
  recovery mechanism for `I:` and is the sole exception to the directory rule.
- VSS recovery is unencrypted, limited to 10% of `I:`, and used only for the
  new live installation. No file-copy backup is introduced.
- Future capabilities that are intentionally supported but not approved are
  reported as capability state without an activation operation. The contract
  does not advertise unrelated removed functionality.

## Native v1 contract

The v1 surface contains nine MCP tools. REST and CLI present the same commands,
request schemas, validation, response envelopes and reason codes.

| MCP tool | Function | REST shape | CLI shape |
| --- | --- | --- | --- |
| `knowledge.search` | Query retained knowledge. `presentation=matches|brief` controls whether the result is an evidence page or compact brief. | `POST /api/v1/knowledge/search` | `fluxknowledge knowledge search` |
| `knowledge.write` | Preview or commit a finite knowledge command: note creation, claim upsert, claim lifecycle transition or forgetting a knowledge item. | `POST /api/v1/knowledge/actions/preview` and `/commit` | `fluxknowledge knowledge write --preview|--commit` |
| `knowledge.graph` | Traverse typed relationships from an entity with bounded depth and result count. | `POST /api/v1/knowledge/graph/query` | `fluxknowledge knowledge graph` |
| `code.query` | Query code index status, symbol facts or bounded retained-code matches through `view=status|symbols|matches`. | `POST /api/v1/code/query` | `fluxknowledge code query` |
| `code.write` | Preview or commit privacy-safe code retrieval feedback. | `POST /api/v1/code/actions/preview` and `/commit` | `fluxknowledge code feedback --preview|--commit` |
| `corpus.query` | Read bounded source-root, retained asset, retained branch, processor or job projections via `view`. | `POST /api/v1/corpus/query` | `fluxknowledge corpus query` |
| `corpus.write` | Preview or commit a finite corpus command: root create/update/disable, source sync, watcher state change, or a supported job retry. | `POST /api/v1/corpus/actions/preview` and `/commit` | `fluxknowledge corpus write --preview|--commit` |
| `operations.status` | Return current health, persistence/index readiness, source and job summaries, worker state, processor readiness, recovery state and supported capability states. `view=overview|sources|jobs|workers|processors|recovery` limits the projection. | `GET /api/v1/operations/status` | `fluxknowledge operations status` |
| `operations.audit` | Return bounded immutable audit evidence, filterable by time, subject and operation family. | `POST /api/v1/operations/audit/query` | `fluxknowledge operations audit` |

Each action command is a closed discriminator with a dedicated request model
and validator. `corpus.write` is not a generic executor: it cannot run shell
commands, arbitrary programs, arbitrary URLs, arbitrary database commands, or
unregistered actions.

The public schemas use lower camel case JSON and stable v1 reason codes. MCP
uses the same names and fields in snake case only where required by the MCP
library; the application command is canonical and neither transport translates
from a legacy schema.

## Confirmation, idempotency and concurrency

Every mutation follows this protocol.

1. The caller submits the complete command in `preview` mode.
2. Validation determines the exact target set, expected target row versions,
   estimated work and a canonical request fingerprint. It stores a short-lived
   confirmation intent and returns its opaque `confirmationId`, expiry and
   human-readable effect summary.
3. The caller sends exactly the same canonical command in `commit` mode with
   the `confirmationId` and a caller-provided idempotency key.
4. The service verifies loopback origin, intent expiry, request fingerprint,
   target row versions and action allowlist before a single durable commit.
   Existing source/job lease and fencing rules remain authoritative.
5. Repeating the same idempotency key and fingerprint returns the original
   receipt. Reusing a key with another request returns a deterministic conflict.
   Cancellation before commit leaves no durable mutation; cancellation after a
   durable commit returns the recoverable receipt on retry.

This needs one native operation-intent/receipt persistence model because the
current operator-action model is specialised to dashboard action cards and
cannot prove replay safety for all v1 commands. EF migration work is therefore
required, tested only against disposable SQL until a separately approved
go-live cutover. Receipts include actor surface, request fingerprint, target
references, result reference, timestamp and outcome; they do not retain
unfiltered request bodies or secrets.

## Privacy and retained-data boundary

- Request validation rejects parser-source paths and network locations.
- Detail, code and corpus results are read from durable retained projections or
  checksum-verified retained children only. Results are bounded and paged.
- The existing secret-disclosure policy runs before every durable manifest,
  audit summary, response and local disclosure. It masks secrets while allowing
  trusted local technical facts that are safe under the policy.
- REST remains bound to `127.0.0.1`; MCP uses that same loopback endpoint; CLI
  calls the local application contract. No credential bridge, remote listener
  or cloud parsing path is added.
- Failures are deterministic reason-code envelopes. They reveal neither
  exception stacks nor protected values.

## Composition and plugin registration

`FluxKnowledge.Application` owns v1 command/query DTOs, validators, operation
intent rules and result envelopes. Infrastructure provides SQL persistence,
retained-data readers, worker/job ports and atomic operation receipts. The Web
host exposes HTTP MCP and `/api/v1`; the CLI is a thin local client of the same
contract.

The native plugin material is prepared beneath `I:\FluxKnowledge\CodexPlugin`.
Its manifest points only to the loopback MCP endpoint. The current registrar
can inspect status and recognise drift, but normal composition deliberately
denies lifecycle repair or registration. Any future lifecycle design must be
separately authorised, idempotent, limited to the known application
registration, and prohibited from rewriting unrelated Codex configuration or
installing a runtime outside the application deployment.

## I: data hierarchy and recovery

The provisioner and deployment configuration use this hierarchy:

```text
I:\FluxKnowledge\
  App\
  Config\
  Data\
    Sql\
      Data\FluxKnowledge.mdf
      Log\FluxKnowledge_log.ldf
    Index\
    Retained\
  Runtime\
    Spool\
    Temp\
    Logs\
  CodexPlugin\
  Recovery\
```

`Recovery` contains only local snapshot-policy and validation metadata, not a
second copy of application data. `VssRecoveryPolicy` emits an unencrypted,
10%-of-`I:` command plan only; it neither configures shadow storage nor creates,
enumerates or restores snapshots. A future authorised operational design would
need to inspect the then-current volume and application-root state before any
live action. No current-host snapshot condition is assumed by this design.

## Clean-slate preparation

`fresh-start` is a preparation-only CLI plan. It reports the canonical layout
and VSS policy with `executionAvailable: false`; it has no runnable host,
database, filesystem, VSS, plugin or loopback-probe lifecycle. Disposable fake
tests prove that the proposed ownership checks fail closed, but they do not
implement a live sequence.

If a future go-live design is separately approved, it must first establish the
then-current application identity, owned paths, database identity, Codex
registration scope and volume snapshot state. An unexpected or ambiguous value
must stop that future workflow for an explicit decision. This document does not
specify or authorise stopping a host, altering registrations, deleting files,
altering a database, configuring VSS, or probing a live endpoint.

## Error handling and capability reporting

All surfaces return one canonical envelope with `ok`, `reasonCode`, `message`,
`retryable`, `operationId` when applicable, and bounded safe detail. Read-only
transient failures may retry within a small time budget. Mutations do not retry
after an uncertain commit; the caller queries or repeats the same idempotency
key instead.

`operations.status` reports only supported native capabilities and their current
state: `available`, `disabled`, `deferred`, `unhealthy` or `unavailable`.
It includes no action that can activate a deferred capability. Status fields
give stable reason codes and remediation guidance appropriate to the local
operator.

## Verification

Implementation begins with red tests and is delivered in coherent milestones.

1. Contract and operation-intent foundation: unit tests for canonical request
   fingerprints, confirmation expiry/mismatch, idempotency replay/conflict,
   cancellation, row-version fencing, disclosure filtering and error envelopes;
   disposable-SQL integration tests for persistence and concurrent commits.
2. Read/query parity: focused MCP, REST and CLI tests proving equivalent
   schema/results for knowledge, code, corpus, status and audit; retained-only
   input and no-source-path tests remain mandatory.
3. Mutating control plane: contract tests and disposable-SQL integration tests
   for every allowed corpus and knowledge action, long-running enqueue-only
   behaviour, leases/fencing, confirmation, replay and cancellation.
4. Plugin, I: hierarchy and clean-slate preparation: filesystem-isolated tests
   for generated plugin material, idempotent registration/repair, root-path
   guards, VSS command construction and disposable clean-slate simulation.
5. Release gate: locked restore, zero-warning Release build, focused
   Domain/Integration/Web/CLI checks, full native Release suite, EF
   no-pending-model verification and an independent whole-slice review. Browser
   validation runs only if the native UI changes. Final branch closeout uses
   `scripts/dev/complete-feature.ps1`.

The 2026-08-26 non-live release gate passed locked restore, a zero-warning
Release build, the focused contract/safety matrix, the full Release suite
(Domain 611/611, Integration 914/914, Web 198/211 with 13 browser skips, and
Outlook host 72/72), and EF no-pending-model verification. The isolated
key-ring failure was corrected and independently reviewed in `addf758` before
that final suite. No browser validation, live deployment, migration, VSS
configuration, plugin lifecycle action, fresh-start execution or loopback probe
was performed.

## Acceptance criteria

- A clean native v1 MCP, REST and CLI contract exists with the nine commands
  above and no compatibility surface.
- All mutation receipts are confirmation-bound, idempotent, fenced and
  cancellation-safe across all three transports.
- No parser is ever handed a source-original path through these integrations.
- All disclosures and durable integration records pass secret filtering.
- Plugin material and status/repair preparation are native; lifecycle mutation
  is unimplemented and needs separate authority and review.
- Every application-owned file, including MDF/LDF, indexes, retained artifacts,
  spool and temporary files, lives beneath `I:\FluxKnowledge`.
- The clean-slate plan is safe by construction, unavailable for execution, and
  requires a separately approved operational design before any live action.
- A future VSS policy, if separately authorised, must be unencrypted and
  constrained to 10% of `I:`.
