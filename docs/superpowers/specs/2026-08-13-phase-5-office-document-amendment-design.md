# Phase 5 Office document processor amendment design

## Status and decision

**Status:** Task 4A is accepted. Task 4B is **plan-amended, pending independent
design approval** on 2026-08-14; it is not approved or implemented.

This amendment permits the currently available deterministic branch to
automatically process retained `.docx`, `.xlsx` and `.pptx` documents. Legacy
`.doc`, `.xls` and `.ppt` remain durable deferred capability work until the
user provides a proper safe parser library. It must never reread an Outlook,
Gmail, IMAP or watched-file original.

The active capability is **document-ooxml-structural-extract**. Its canonical
descriptor is:

| Field | Value |
| --- | --- |
| Identifier | `3d72bf21-5358-482d-a6a9-576ff23012a3` |
| Processor version | `phase-5-ooxml-structural-v1` |
| Processor fingerprint | `phase-5-ooxml-retained-structural-v1` |
| Execution class | `InProcess` |
| Parent branch activity kind | `TextExtraction` |
| Accepted classification | `OoxmlDocumentContainer` |
| Output contract | `retained:document-ooxml-structural-extract` |

`RetainedProcessorOptions.OoxmlDocumentStructuralExtractEnabled` defaults to
`false`, matching the installed ZIP and TAR options. Enabling the exact
descriptor registers it as runnable and automatically replays eligible retained
work in bounded batches of at most 16. Disabled configuration performs no
registration, promotion, claim, retry or replay.

## Format and parser rule

The active automatic formats are:

| Family | Formats | Required safe parser boundary |
| --- | --- | --- |
| Open XML packages | `.docx`, `.xlsx`, `.pptx` | Signature-confirmed ZIP package plus bounded `System.Xml.XmlReader` structural-part parser. |

The Open XML parser validates package structure before parsing selected
allow-listed structural XML parts. It disables DTD processing, sets
`XmlResolver` to `null`, prohibits external resources and ignores macros,
embedded objects, ActiveX, scripts, linked data, formulas requiring evaluation
and presentation rendering. It emits deterministic plain structural text only:
document paragraphs/text runs, worksheet string/cell text in sheet order, and
slide text runs in presentation order. The extracted text is retained private
content; public projections expose only identifiers, counts and fixed reason
codes.

The legacy formats are intentionally `DeferredUnsupported` for now. A
signature-confirmed Compound File Binary input whose immutable source revision
extension is `.doc`, `.xls` or `.ppt` retains required capability
`document-office-legacy-structural-extract` and fixed reason code
`legacy-office-binary-parser-unavailable`. No handler is registered, promoted
or claimed for that capability. These rows appear in existing deferred evidence,
not in Operator actions, because absence of the parser is neither an
integrity/policy block nor a safe force-attempt candidate.

The parser inventory has found no eligible local parser for each legacy format:
the locked .NET package graph, transitive assets and local NuGet cache contain
no Compound File Binary/DOC, BIFF or PowerPoint binary reader; the installed
Python packages support only OOXML; and no safe local conversion tool is
installed. The existing Outlook interop packages are not document readers. When
the user provides a proper library, a new approved amendment must specify its
pinned version, APIs, hostile CFB bounds and all three format readers before
`document-office-legacy-structural-extract` becomes runnable.

Office COM, automation, IFilter, Office applications, conversion utilities,
model/runtime download, network clients and any Phase 6 capability are not
substitutes. This designation is not an operator-force bypass.

## Bounded processing policy

The user-approved limits are exactly double the prior proposal:

| Limit | Value |
| --- | ---: |
| Retained document input | 134,217,728 bytes (128 MiB) |
| Expanded package XML total | 268,435,456 bytes (256 MiB) |
| XML elements | 200,000 |
| XML nesting depth | 128 |
| Extracted UTF-8 structural text | 33,554,432 bytes (32 MiB) |
| Automatic replay batch | 16 branches |

The active parser validates the immutable retained-artifact checksum before
format inspection. It applies all limits before allocating unbounded memory or
creating a child revision. Package parsing treats malformed metadata,
encryption, unsupported streams, recursive/linked content and parser exceptions
as untrusted-boundary outcomes. No action may increase these limits.

Open XML packages additionally have a maximum of 512 entries, 8,192
relationships, a 32 MiB uncompressed selected-part limit, 512 logical path
characters and a 100:1 expanded-to-compressed ratio. They reject duplicate,
linked, encrypted, multi-volume, alternate-stream, rooted, traversal, NUL or
unsupported package entries before an XML stream is opened.

The later legacy parser amendment must expose equivalent bounded Compound File
Binary controls: at most 4,096 directory entries, 2,048 streams, storage depth
64, 32 MiB per selected stream, 262,144 regular-sector references and
2,097,152 mini-sector references. It must detect cycles, duplicate directory
IDs, out-of-range sector links, invalid chain termination and encrypted/password
protected formats before structural extraction.

Fixed sanitised outcomes are: `office-document-input-too-large`,
`office-document-container-invalid`, `office-document-encrypted`,
`office-document-xml-invalid`, `office-document-expanded-xml-limit`,
`office-document-element-limit`, `office-document-depth-limit`,
`office-document-text-limit`, `office-document-part-unsupported`,
`retained-artifact-missing`, `retained-artifact-path-invalid` and
`retained-artifact-checksum-invalid`.

## Source-neutral designation, retained inspection and promotion

All source types use the same candidate query. It selects deferred-capability
activities with a checksum-bound retained artifact and no successor branch,
irrespective of whether the original activity came from a watched file, Gmail,
IMAP or Outlook. It retains its predecessor activity and writes an explicit
supersession relation whenever it creates a successor. It must not limit the
query to the prior `local-source-capability` string.

`RetainedProcessorPromotionCandidate` therefore includes the private immutable
revision extension in addition to opaque IDs and the input hash. Per-capability
SQL filters use that metadata before pagination: the OOXML selector admits only
`.docx`, `.xlsx` and `.pptx`; the legacy selector admits only `.doc`, `.xls`
and `.ppt`; archive ZIP excludes the OOXML extensions. Ordering is deterministic
by created timestamp/activity ID within the matching selector, so nonmatching
or permanently deferred first-page rows cannot starve eligible work.

The retained reader gains a two-stage private contract. `InspectAsync` opens the
same bound root with the current no-follow and containment lease, verifies that
the physical length equals the immutable record, and returns only revision ID,
immutable hash and byte length without allocating the file. `ReadBytesAsync`
then fully verifies the checksum before returning a bounded buffer. No child is
created until `ReadBytesAsync` succeeds. Inspection allows a likely OOXML input
above 128 MiB to become a fenced branch and receive the fixed
`office-document-input-too-large` outcome without an unbounded allocation; it
does not make that artifact processable or bypass integrity before a child write.

Promotion is intentionally based on a likely Office extension plus immutable
retained inspection, not a successfully parsed package. This permits corrupt,
encrypted, password-wrapped Compound File Binary OOXML and over-limit `.docx`,
`.xlsx` and `.pptx` inputs to obtain a bounded blocked branch and show the
sanitised actual outcome in Operator actions. A valid OOXML package is still
confirmed only by the processor after the bounded byte read.

When OOXML is enabled, its selector promotes likely OOXML work before archive
ZIP. When OOXML is disabled, archive ZIP still excludes `.docx`, `.xlsx` and
`.pptx`; they remain deferred and are never treated as generic ZIP work. Legacy
selector rows are redesignated to a successor DeferredUnsupported activity with
required capability `document-office-legacy-structural-extract` and reason
`legacy-office-binary-parser-unavailable`; no handler, branch or claim is made.

A successful document is deterministically segmented at strict UTF-8 character
boundaries into one or two private structural-text children of at most 16 MiB
each. The two-child maximum implements the approved 32 MiB document text cap
without weakening the established 16 MiB retained-text/pipeline admission
limit. A segment identity is SHA-256 over parent stable identity, Office
descriptor fingerprint and segment ordinal; it is never based on raw document
text or a source path.

The branch model must be refactored before Office activation so it can persist
processor-derived children rather than archive-only children. Two mappings are
deliberately independent:

| Processor family | Parent successor activity kind | Derived structural-text child activity |
| --- | --- | --- |
| ZIP/TAR | `ArchiveExpansion` | `TextExtraction`, processor version `phase-3a-v1`, origin `ArchiveMember` |
| OOXML | `TextExtraction` | `TextExtraction`, processor version `phase-3a-v1`, origin `OfficeStructuralSegment` |

`PromoteAsync` must create the parent successor from the capability descriptor's
declared activity kind rather than hard-coding `ArchiveExpansion`. A
`RetainedProcessorDerivedChild` supplies only the opaque child identity,
synthetic locator prefix, extension, classification and origin kind; every
derived structural-text child keeps the existing `TextExtraction`/
`phase-3a-v1` pipeline mapping. SQL derives its child canonical locator and
origin only from that manifest, not from a hard-coded archive path or
extension. ZIP/TAR regression tests must prove that their parent
`ArchiveExpansion` and child `TextExtraction` mappings remain unchanged.
Root-scan suppression must exclude every non-root-derived origin, not just
`ArchiveMember`.

Completion, retry, failure, activity supersession and operator event follow the
existing branch id, lease owner, generation and database-current unexpired-lease
fence. Replays converge by the same immutable content hash and capability
descriptor. No content or original locator is used as an idempotency key or
public value. No schema migration is needed for a new `OriginKind` value if the
existing integer column remains unconstrained; its mapping and regression tests
must prove that fact before relying on it.

## Operator actions and force-request contract

Task 4A is independently accepted. Task 4B is **not implemented**: this
section is a corrective, pending-review specification, not evidence that an
operator action, endpoint, migration or force request exists.

The web application will gain an **Operator actions** tab only for the current,
force-eligible blocked generation of the exact OOXML branch. It is not a queue
for healthy automatic work, disabled descriptors, legacy-parser-unavailable
deferred evidence, ZIP/TAR branches, retained-artifact integrity failures or a
manual replay requirement for otherwise eligible documents.

### Authoritative action identity and eligibility

An action is a projection of one *blocked attempt*, not of a path, source name
or broad activity history. Its identity is the opaque deterministic value over
`SourceProcessorBranches.Id`, the exact descriptor fingerprint and the final
`SourceProcessorAttempts.LeaseGeneration`; the input fields themselves are
never returned. The projection must select the final attempt by the composite
unique key `(BranchId, LeaseGeneration)`, require `FinishedAtUtc IS NOT NULL`,
and require that attempt's generation to equal the branch's current
`LeaseGeneration`. The fixed public reason is that attempt's `OutcomeCode`;
the branch state, an old attempt, a member reason and an activity reason must
not substitute for it. This prevents an old block from authorising a later
generation.

The query also requires one coherent immutable binding: branch source activity
and source revision, branch `InputSha256`, source activity input fingerprint,
source revision content hash and retained artifact content hash all match under
the existing scheduler-fence collation; the source activity is the branch's
successor activity; and the branch version/fingerprint equal the canonical
`document-ooxml-structural-extract` descriptor. The descriptor must be present
and runnable with the approved ID, version, fingerprint, `InProcess` class,
`TextExtraction` kind and output contract. A request records those immutable
values and never follows a later descriptor registration.

| Candidate at read/request time | Listed | Force available | Required result |
| --- | --- | --- | --- |
| Exact runnable OOXML branch is `Blocked`, final attempt matches current generation, and its reason is an allowed OOXML block | Yes | Yes, if no open request | Create/reuse the generation-bound request. |
| Same exact blocked OOXML action with an open `Requested` or `Claimed` request | Yes, with its public request state | No | Return that same durable request for a duplicate of this action identity. A terminal historical action receipt is also resolved and replayed before this current-candidate matrix is evaluated. |
| Exact OOXML branch is `Pending`, `Running`, `Completed`, its associated activity/revision is cancelled or superseded, it is stale, descriptor-disabled or has no final attempt matching the current generation | No | No | Do not create a request; a stale action identifier receives the fixed stale response. |
| Exact OOXML branch final reason is `retained-artifact-missing`, `retained-artifact-path-invalid` or `retained-artifact-checksum-invalid` | No | No | Integrity/root binding failure is never forceable. |
| OOXML reason is `office-document-container-invalid`, `office-document-encrypted`, `office-document-xml-invalid`, `office-document-expanded-xml-limit`, `office-document-element-limit`, `office-document-depth-limit`, `office-document-text-limit`, `office-document-part-unsupported` or `office-document-input-too-large` | Yes | Yes, subject to the first two rows | One new bounded attempt only. |
| Legacy `.doc`, `.xls` or `.ppt` `DeferredUnsupported` evidence, including `legacy-office-binary-parser-unavailable` | No | No | Legacy remains deferred and unforceable. |
| ZIP/TAR, another processor descriptor, an OOXML-looking generic ZIP row, or a branch whose descriptor/version/fingerprint differs | No | No | Do not project or mutate it through this feature. |

When a forced claim blocks, its next current generation is a new action
identity. A later force is permitted only after that new action is durably
blocked with one of the nine allowed reasons and the preceding force request is
terminal. Repeated clicks for the same action identity always return its one
durable request; they cannot create a second generation, second attempt or
second child set.

### Durable request and branch state machines

`SourceProcessorForceRequests` has these request states: `Requested`,
`Claimed`, `Completed`, `Blocked`, `Transient`, `Cancelled` and `Expired`.
Only `Requested` and `Claimed` are open. They use a five-minute server-UTC
claim/lease window consistent with the current retained-branch lease; terminal
states are immutable receipts and are never reopened.

| Request transition | Branch/attempt transition | Fence and consequence |
| --- | --- | --- |
| `Requested` | `Blocked` → `Pending` | Creation snapshots the blocked generation and final reason; no attempt is yet associated. |
| `Requested` → `Claimed` | `Pending` → `Running`, generation increments, one `SourceProcessorAttempt` is inserted | In the same serialisable transaction, write the request's immutable `ForceAttemptLeaseGeneration` to exactly that new attempt generation. |
| `Claimed` → `Completed` | matching `Running` → `Completed` | `CommitAsync` completes both only when owner, generation and database-current lease fences match. |
| `Claimed` → `Blocked` | matching `Running` → `Blocked` | `FailAsync` records the repeated fixed outcome and finalises the one forced attempt. |
| `Claimed` → `Transient` | matching `Running` → `Pending` | `RetryAsync` ends this force request; a later normal retry must not be attached to, or inherit force from, the request. |
| `Claimed` → `Expired` | expired matching attempt is closed during reclaim; branch is reclaimed into a later normal generation | Reclaim writes `lease-expired-reconciled` for the exact forced generation and never rebinds the request to the reclaim attempt. |
| `Requested` → `Expired` | matching `Pending` → `Blocked`, with no new attempt | If the descriptor does not claim the request before its server-UTC deadline, restore only the snapshotted blocked branch under the original generation fence. |
| `Requested`/`Claimed` → `Cancelled` | associated activity/revision is superseded, cancelled or no longer has the immutable binding | The same serialisable transition prevents a late completion from changing the affected branch. |

Cancellation before the creation transaction commits leaves no request; after
commit it cannot roll back a durable request. Disabling the descriptor rejects
new requests and prevents claims. A pre-existing `Requested` request stays
dormant until its deadline, then becomes `Expired`; it is not silently claimed
or replayed while disabled. A request is stale, and receives no mutation, if
its opaque action ID no longer resolves to the current blocked attempt/identity,
the exact descriptor is no longer runnable, an immutable binding differs, or
the branch is no longer `Blocked`. The database, not application-clock time,
decides expiry and current-lease comparisons.

### Atomic SQL lifecycle, schema and idempotency

Extend `IRetainedProcessorBranchStore` and
`SqlRetainedProcessorBranchStore` so force-request creation, claim, commit,
failure, transient retry, expiry and reclaim share the existing branch fence;
`IOoxmlOperatorActionStore` must not implement a parallel lifecycle. Each
mutation uses one SQL Server serialisable transaction, `UPDLOCK, HOLDLOCK` on
the branch/request action identity, and `SYSUTCDATETIME()`/`TODATETIMEOFFSET`
for every current-UTC lease or expiry predicate. Do not calculate correctness
from `TimeProvider` on the application host.

Creation locks the exact blocked branch and final matching attempt, validates
the full identity above, reuses the one matching open request or inserts a
`Requested` row, writes the sanitised durable audit/event row, then moves the
branch to `Pending`. Claim locks the branch and its one requested row, verifies
the descriptor still runnable and that the branch generation remains the
snapshotted blocked generation, increments branch generation once, inserts the
attempt and binds that request to that precise attempt generation. Commit,
`FailAsync` and `RetryAsync` lock both branch and request and update only where
request `ForceAttemptLeaseGeneration`, attempt generation, branch owner and
generation, state and database-current lease all agree. Reclaim finalises the
matching request before a later normal claim. Every rejection is side-effect
free apart from an allowed sanitised rejection audit record.

The additive entity must contain required immutable `SourceActivityId`,
`SourceProcessorBranchId`, `SourceRevisionId`, descriptor ID/fingerprint,
expected input SHA-256, original blocked attempt generation and original fixed
reason; mutable request state/timestamps, nullable-until-claim `ForceAttemptId`
and `ForceAttemptLeaseGeneration`, bounded terminal receipt/reason; and
`RowVersion`. It has restrictive FKs to `SourceActivities`,
`SourceProcessorBranches`, `SourceRevisions` and, once claimed,
`SourceProcessorAttempts`; the unique `ForceAttemptId` relationship and store
fence make one request map to one attempt and its one generation. It also has a
unique foreign-key-compatible action identity, an index for state/expiry claim scans,
and a filtered unique index for one open request per
`(SourceActivityId, SourceProcessorBranchId, DescriptorId, ExpectedInputSha256,
OriginalBlockedLeaseGeneration)`. Checks restrict state and reason columns to
the declared fixed vocabulary, require a force-attempt generation only once
`Claimed` or terminal-from-claimed, and preserve the immutable request-to-one-
attempt-generation relation. Reuse `SchemaConfiguration.ConfigureHash`,
`ConfigureRowVersion`, immutable-after-insert configuration, existing scheduler
fence collation and restrictive delete behaviour. The migration is additive,
contains no data backfill, and includes the EF migration designer and model
snapshot; it is applied only by the generated disposable SQL fixture. Upgrade
artefacts must prove an existing database upgrades without altering, inventing
or exposing historical requests.

### Operator authority, transports and public contract

Only the Web UI and its REST endpoint may create a force request. Extend the
existing direct-loopback gate to cover `/operator-actions`, `/api/operator-actions`
and the interactive `/_blazor` circuit; reject a non-loopback peer and any
forwarded/proxy header. The POST requires ASP.NET Core antiforgery validation
and an absent-or-same-origin `Origin`/`Referer` check against the loopback host.
The component uses the existing anonymous direct-loopback policy pattern; it
must not infer authority from a supplied actor, IP header, Windows identity or
authentication state. The audit actor is the fixed sanitised value
`anonymous-direct-loopback`, never a user name, SID, host name, address or
request header.

`GET /api/operator-actions` returns only opaque action/request IDs, durable
source activity ID, capability, public request/branch state, fixed reason,
timestamps and `forceAvailable`; `POST /api/operator-actions/{actionId}/force-process`
returns the same public request DTO with `201` on creation or `200` for the
same action identity. Fixed, diagnostics-free errors are `400` malformed ID,
`403` loopback/origin/antiforgery failure, `404` not listed, `409`
`operator-action-stale` or `operator-action-not-forceable`, and `503`
`operator-action-descriptor-disabled` when the supplied otherwise-current
action resolves but its exact descriptor is disabled. No raw exception, path, content, source
identity, mailbox/spool value, locator, hash or parser diagnostic enters a DTO,
error, audit details, status event, SignalR message, UI, REST, MCP or CLI.
MCP and CLI expose no create, force, retry or mutation command for this feature;
they may not route around the web authority check.

The database audit/event record is written in the successful transaction with
only fixed event kind, opaque IDs, descriptor kind, state/reason and sanitised
actor. Any status-feed/SignalR publication occurs only after commit and only
from that public projection; a rollback, conflict, cancellation or failed
commit publishes nothing. The UI contains an antiforgery token and displays
only the same public DTO, including a disabled bounded-force control when an
open request exists.

## Task 4B corrective amendment: durable identities and unconditional reconciliation

This section supersedes every earlier Task 4B statement in this document where
they differ. It is **plan-amended, pending independent design approval**. It
does not approve or implement Task 4B.

### Action and operation idempotency

An action is one immutable blocked branch action-version. `ActionId` is the
lower-case SHA-256 hex of the length-prefixed, domain-separated tuple
`ooxml-force-action:v1`, branch ID, descriptor ID, descriptor fingerprint and
the branch `RowVersion` observed while the branch is `Blocked`. It is a
64-character opaque, globally unique durable identity. The original blocked
row-version is persisted. A forced re-block changes lease generation and row
version, and therefore has a different action identity. A pre-claim expiry
must also write `Pending` -> `Blocked` as a new row-version before a new
action can be listed; it cannot reuse the expired request's action ID.

Every POST also carries a stable client `OperationId` (`uniqueidentifier`) and
an immutable `RequestFingerprint` (64-character lower-case SHA-256). The
server recomputes that fingerprint from the length-prefixed tuple
`ooxml-force-request:v1`, route, `ActionId` and the opaque expected blocked
row-version token. Both are persisted on the request and are globally unique
for their respective request records. The application contract, REST DTO and
persistence entity use those exact names.

Under the serialisable request transaction, the operation collision guard runs
first: the same `OperationId` and fingerprint returns the original receipt;
the same operation ID with a different fingerprint, action or expected version
returns `409 operator-operation-conflict`, with no mutation, audit or refresh.
It then resolves an existing `ActionId` **before checking current eligibility**.
That lookup includes every terminal request state; it returns the original
receipt with HTTP 200 and no new audit/status record. Only if neither durable
identity exists may the store evaluate current blocked creation. It must lock
the branch and final matching attempt, require the expected blocked row-version
token to equal the current blocked row-version, and recompute an equal action
ID. Any version, generation, binding or state mismatch is
`409 operator-action-stale`; a current but disallowed reason is
`409 operator-action-not-forceable`. Distinct operation IDs racing a new,
current action-version are decided by the database filtered open-action index;
the loser rereads and returns the winner's receipt without extra side effects.

`GET /api/operator-actions` therefore exposes an opaque `ActionId` and opaque
blocked row-version token for each eligible current action. The POST body for
`/api/operator-actions/{actionId}/force-process` is exactly
`operationId`, `requestFingerprint` and `expectedBlockedRowVersion`. It returns
201 only for the new request and 200 for operation or action receipt replay,
including terminal receipts. The opaque row-version token is an optimistic
precondition, not a source identity or a private value.

### Database-time reconciliation and force isolation

Add `ReconcileForceRequestsAsync` to `IRetainedProcessorBranchStore` and
`SqlRetainedProcessorBranchStore`. `RetainedProcessorActivationService` calls
it at the beginning of every `RunOnceAsync` pass, before legacy designation,
descriptor registration and every enabled-option branch.
`RetainedProcessorActivationHostedService` continues to invoke that pass even
when every processor descriptor is disabled. Reconciliation uses durable rows
and `SYSUTCDATETIME()` only; it does not require activation, promotion, a
runnable processor, retained-byte reads or application-clock time.

Each reconciliation mutation is a serialisable `UPDLOCK, HOLDLOCK` transaction
on the request, branch and forced attempt. Its exact outcomes are:

| Durable condition | Atomic transition | Result |
| --- | --- | --- |
| `Requested` and database claim deadline expired | request `Requested` -> `Expired`; branch `Pending` -> `Blocked` | No attempt is created; branch update produces the next action-version; receipt reason `force-request-claim-expired`. |
| Requested work then activity/revision cancelled, superseded or immutable binding invalid | request `Requested` -> `Cancelled`; branch `Pending` -> `Blocked` | No attempt. Receipt reason `force-request-cancelled`; cancelled/superseded source activity remains unlistable and unclaimable. |
| Claimed work then activity/revision cancelled, superseded or binding invalid | request `Claimed` -> `Cancelled`; matching attempt closed; branch `Running` -> `Blocked` | Close only the bound forced attempt with `force-request-cancelled`; late completion is fenced out. |
| Descriptor disabled before claim | requested request -> `Cancelled`; branch `Pending` -> `Blocked` | Receipt reason `force-request-descriptor-disabled`; no force claim occurs. |
| Descriptor disabled after claim | claimed request -> `Cancelled`; matching attempt closed; branch `Running` -> `Blocked` | Close only the bound forced attempt with `force-request-descriptor-disabled`; late completion is fenced out. |
| Claimed lease expired | request `Claimed` -> `Expired`; matching attempt closed; branch `Running` -> `Pending` | Attempt outcome and receipt reason `lease-expired-reconciled`; later normal claim gets its own generation and is never attached to this request. |

Claim SQL must exclude branches with an open `Requested` force request, and
exclude cancelled or superseded source activities. This isolates pending force
work from ordinary claims and prevents cancelled activities from being
reclaimed. A forced claim binds exactly one new generation. Its commit, failure
and transient-retry paths require request state, exact attempt generation,
branch owner/generation and database-current lease to agree. Reconciliation
finalises an expired forced request before any later normal claim.

### Exact additive schema

Create `SourceProcessorForceRequests` with exactly these columns:

| Column | SQL type and nullability |
| --- | --- |
| `Id` | `uniqueidentifier NOT NULL` primary key |
| `ActionId` | `char(64) NOT NULL` |
| `OperationId` | `uniqueidentifier NOT NULL` |
| `RequestFingerprint` | `char(64) NOT NULL` |
| `SourceActivityId`, `SourceProcessorBranchId`, `SourceRevisionId`, `DescriptorId` | each `uniqueidentifier NOT NULL` |
| `DescriptorFingerprint` | `nvarchar(256) NOT NULL` |
| `ExpectedInputSha256` | `char(64) NOT NULL` |
| `OriginalBlockedLeaseGeneration` | `bigint NOT NULL` |
| `OriginalBlockedRowVersion` | `binary(8) NOT NULL` |
| `OriginalOutcomeCode` | `nvarchar(128) NOT NULL` |
| `State` | `tinyint NOT NULL` |
| `RequestedAtUtc`, `ClaimExpiresAtUtc` | each `datetimeoffset(7) NOT NULL` |
| `ClaimedAtUtc`, `TerminalAtUtc` | each `datetimeoffset(7) NULL` |
| `ForceAttemptBranchId` | `uniqueidentifier NULL` |
| `ForceAttemptLeaseGeneration` | `bigint NULL` |
| `TerminalReceiptFingerprint` | `char(64) NULL` |
| `TerminalReasonCode` | `nvarchar(128) NULL` |
| `RowVersion` | `rowversion NOT NULL` |

`ActionId` and `OperationId` each have a global unique index. Restrictive FKs
target source activity, branch and revision. A composite restrictive FK from
`(ForceAttemptBranchId, ForceAttemptLeaseGeneration)` to the existing unique
`SourceProcessorAttempts(BranchId, LeaseGeneration)` is mandatory; it is the
database-enforced force-attempt branch/generation association. A check requires
the two attempt fields to be null together or non-null together and, when
non-null, requires `ForceAttemptBranchId = SourceProcessorBranchId`.

Checks restrict state to `Requested`, `Claimed`, `Completed`, `Blocked`,
`Transient`, `Cancelled`, `Expired`; original outcome to the nine forceable
OOXML block reasons; and terminal reason to the fixed state-appropriate
vocabulary. They require `RequestedAtUtc <= ClaimExpiresAtUtc`, monotonic
claimed/terminal timestamps, no force attempt on requested or pre-claim
expired/cancelled rows, and a force attempt for claimed and every
terminal-from-claimed row. They require `force-request-claim-expired` for a
pre-claim expiry, `lease-expired-reconciled` for claimed expiry, and one of
`force-request-cancelled`/`force-request-descriptor-disabled` for cancellation.
`ActionId`, fingerprints, hashes and fence strings use scheduler-fence
collation and immutable-after-insert configuration; row-version uses
`ConfigureRowVersion`.

The filtered open-request index is additionally scoped to the immutable action
version: unique `(SourceProcessorBranchId, DescriptorId, DescriptorFingerprint,
OriginalBlockedRowVersion)` where state is `Requested` or `Claimed`. It is in
addition to, not instead of, the global action and operation indexes. Add the
non-unique `(State, ClaimExpiresAtUtc)` reconciliation index. The entity,
configuration, `DbContext`, migration, migration Designer and model snapshot
must be generated together. The migration is additive, contains no backfill,
and upgrade tests must prove predecessor databases gain no invented historical
request.

### Transaction, audit and public refresh rule

Creation, claim, completion, repeated block, transient retry, cancellation,
pre-claim expiry and claimed-lease reconciliation each atomically write the
request, branch, force attempt where applicable, and one sanitised audit/event
record. A committed transition emits exactly one fixed public status refresh
after commit and only from the public projection. Replay, stale action,
operation conflict, rollback and failed commit emit neither audit/event nor
refresh. `IOoxmlOperatorActionStore` remains read-only projection/entry glue;
all state mutation stays in the retained branch store.

## Privacy and exclusions

The existing Phase 5 privacy invariant remains unchanged. A local private PC
does not make raw document text, private paths, mailbox identifiers, credentials
or private-spool information valid public projection, audit, REST, MCP, CLI,
SignalR, UI or source-control data. Tests use synthetic private-root and text
sentinels and assert that Sources, Corpus, Events, audit, REST and the status
event feed do not serialise them.

Phase 5 still excludes OCR, AI vision, transcription/ASR, embeddings,
reranking, model-dependent document processing, model/runtime download, GPU
work, Office automation, Outlook activation, mailbox mutation, deployment,
non-disposable migration and live validation.

## Acceptance evidence

The OOXML slice is accepted only after focused RED then GREEN evidence for
`.docx`, `.xlsx` and `.pptx`: retained-only success, automatic enabled replay,
disabled inert behaviour, ZIP/OOXML promotion precedence,
idempotency/concurrency, bounded XML/package hostile inputs,
encrypted/corrupt/over-limit blocks, force-attempt fence/idempotency,
missing/corrupt retained artifacts, parent-child provenance,
restart/cancellation, legacy deferred designation, and every public-surface
sentinel check. Native SQL tests use generated disposable databases only when
`FLUXKNOWLEDGE_TEST_SQL_CONNECTION` is set; otherwise the exact skipped gate is
reported, never passed.
