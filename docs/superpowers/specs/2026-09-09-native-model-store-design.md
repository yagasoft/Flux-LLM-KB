# Native model-store enforcement

Date: 2026-09-09
Status: implemented and verified on 2026-09-10; runtime/provider activation remains separately deferred

Implementation plan: [native model-store](../plans/2026-09-09-native-model-store.md).

## Outcome and scope

Make the existing `J:\Models` rule enforceable before adding any model-backed
adapter. The first deliverable is a native, offline model resolver and local CLI
verification command: an explicit bundle specification enters, complete local
files are checked, an immutable verification receipt is retained, and the caller
receives either a held verified bundle or a precise refusal. A cache miss cannot
cause a download, including through a provider wrapper.

This is a narrowly justified foundation: implicit model acquisition has a stated
financial cost. It is not completion of OCR or of the broader document work.
PDF and VSDX remain in scope as the next delivery-bearing document slice; their
model-free extraction need not wait for acquiring OCR weights. The intended
document contract remains one source/document with subordinate chunks and
page/shape provenance, not independent package-member corpus entries.

Existing evidence at base commit `9aa3a58`:

- `AGENTS.md` contains the mandatory central-cache and acquisition-approval rule.
- `FluxKnowledge.Infrastructure.Inference` contains the model-free deterministic
  embedding implementation, not a native model resolver or real OCR adapter.
- Native GPU scheduling and supervision have durable ownership contracts but
  are not a working model execution path. This slice leaves them unchanged.
- Existing document text registration and chunk storage do not yet provide the
  full document-owned parsing/provenance/legacy-member-retirement contract.
  Those changes need their own focused design; this cache slice does not make
  them implicitly approved or implemented.

## Selected approach

Use a provider-independent native store in the existing inference assembly,
behind an application interface. Do not rely only on environment variables or
provider caches: those are additional defences, not proof that a loader cannot
download. Do not add an automatic download manager: it would create the very
cost-bearing path that must remain separately authorised.

The production store root is fixed to `J:\Models`. It is not selectable from a
source document, job payload, environment variable, CLI flag or fallback list.
Unit and integration tests may use an internal test-only root with tiny
synthetic bytes; this must not become a production root override.

No inference provider, network client, external executable, package-specific
model loader, GPU admission change, SQL schema, source replay or production
deployment is introduced in this slice. Legacy Python/provider caches and
settings remain untouched. No model payload, licence contents or private cache
inventory enters Git.

## Contracts and ownership

Add a bounded `ModelBundleSpecification` and `ILocalModelStore` contract under
`FluxKnowledge.Application/Models`, with implementation under
`FluxKnowledge.Infrastructure.Inference/Models`.

A bundle lists every required weight/shard, tokenizer, vocabulary, processor
configuration and projector. Each item has an upstream repository identifier,
immutable revision, upstream relative filename, format/precision, SHA-256 and
byte length. Mutable tags alone are invalid. Converted artifacts additionally
identify source hashes and converter/runtime versions. The canonical
specification hash identifies the bundle; a display name never identifies
content. Validate bounded counts, strings and total byte arithmetic before any
filesystem work. Reject duplicate identities, conflicting hashes/lengths and
case-insensitive path collisions.

Store payloads by digest under `J:\Models\artifacts\sha256\<digest>\<leaf>`;
the leaf is a validated filename, not an upstream path appended unchecked.
Keep immutable verification receipts under `J:\Models\inventory\verifications`
and persistent cross-process lock files under `J:\Models\locks`. Existing
discovery inventories remain unmodified and are not proof of verification.
There is no automatic population, adoption, conversion or migration of files
into this layout.

`ResolveAsync` accepts an explicitly selected, validated bundle specification
and cancellation token. It returns a typed result, not an arbitrary usable
path on failure. A successful `VerifiedLocalModelLease` owns open read handles
and the checked local file mapping until disposed. A future native adapter must
hold that lease throughout model loading/use and receive only those local
paths; it must not receive a provider repository ID as a fallback source.

Verification proves local identity, completeness and integrity, not OCR
accuracy, runtime compatibility, licensing or approval to download. It does
not register or activate a model. A verified receipt alone must never be
accepted as a substitute for the current resolve/lease checks.

## Resolution and fail-closed behaviour

1. Validate the specification and canonical root. Before any create or write,
   pin the actual local J: volume and existing `Models` directory with no-follow
   handles retained against replacement. Reject unavailable or unwritable
   storage and inadequate space for required metadata. Do not create another
   root, follow a substituted destination or change another app's cache
   environment. No path may be re-resolved through an unprotected drive mapping
   or ancestor after validation.
2. Reject unsafe filenames, traversal, absolute/drive/UNC/device paths, alternate
   data streams, trailing-dot/space aliases, reserved names and reparse points.
   Establish containment from opened handles and keep directory/file handles
   that prevent rename, replacement or writing for the verification/use
   interval. A lexical prefix check alone is insufficient. Do not follow a
   symlink to a legacy cache, even when its target happens to be on J:. Apply
   the same pre-write boundary to every metadata component: lock directories
   and files, inventory, staging and receipt destinations. Create any missing
   metadata directory only through its already pinned, protected parent. A
   post-open final-path check cannot undo an earlier redirected create/write.
3. Acquire per-artifact cross-process locks in deterministic digest order, with
   bounded cancellable waiting. Recheck presence and integrity under the lock.
   Open all files without write/delete sharing; if that protection cannot be
   obtained, refuse the bundle. Lock files are persistent, never deleted as
   "stale" to break a live lock. Process exit releases OS locks, not permissions
   to repair or download content.
4. Verify each exact length and SHA-256 using bounded streaming reads. Missing
   companions, changed files, invalid metadata or incomplete bundles cannot
   produce a lease. Never overwrite, delete, quarantine or repair a mismatching
   payload. Never guess another variant or encoding. Release acquired handles
   and locks on cancellation/failure.
5. Persist an immutable result receipt using exclusive staging/create through
   the protected metadata paths, flush it, then atomically publish without
   overwriting an existing destination. Keep the volume/ancestor protections
   through publication; success is possible only after flush and publication
   complete. Include specification hash,
   verifier version, observation time, exact per-artifact result and reason,
   verified byte counts and `transferredBytes: 0`. Repeated/concurrent
   resolutions may share verified files but must not overwrite receipts. A
   receipt failure makes resolution fail; it cannot return a success lease.
6. Return the lease or refusal. No branch calls a downloader, provider loader or
   network endpoint. A missing root or unwritable receipt store makes durable
   recording there impossible: return the refusal and explicitly report
   `receiptPersisted: false`, rather than inventing a receipt or writing one on
   another drive.

Stable reasons distinguish invalid specification, unsafe path/reparse risk,
store unavailable/not writable/insufficient space, missing artifact, length or
checksum mismatch, lock contention and receipt failure. Cancellation remains
cancellation. An unapproved acquisition is not a download attempt: this slice
has no acquisition operation at all.

## Observable interface and cache discovery

Add `FluxKnowledge.Cli models verify --manifest <path>`. It validates a bounded
local manifest, uses the same resolver as future adapters, emits structured
JSON with the bundle fingerprint, result, missing/invalid artifact identities,
reason and receipt location/status, then releases its lease. Exit 0 means local
verification succeeded, 1 means refusal, and 2 means invalid command/input. It
must not open SQL, start IIS/workers or initialise any provider. CLI success
does not grant another process a durable lease; that process must resolve again.

This is a local operational utility, not a new remotely callable download
surface. Existing REST/MCP/native-v1 contracts stay unchanged. Tests exercise
the real CLI parsing/composition and filesystem implementation, not only mocks.

Before any later acquisition request, read the persistent J inventory and
inventory the relevant existing Paddle/Hugging Face files read-only: actual
SHA-256, length, snapshot revision where recorded, companion files and duplicate
hashes. Cache directory names are not completeness evidence; local hashes alone
do not prove an upstream revision. Mark unresolved identity/completeness
explicitly. Never invoke provider imports/loaders to discover caches.

Present non-destructive adoption separately if usable legacy bytes exist.
Only after resolving reuse can an acquisition proposal list exact missing
files, immutable revisions, hashes, transfer-byte totals and J destinations.
No download, copy, conversion, global environment change or cache repair is
authorised by this specification. The selected OCR model family does not
waive that separate file-level acquisition gate.

An eventual approved acquisition service must use the same artifact identities
and locks, recheck under lock, transfer only missing content, resume verified
partial work where supported and atomically publish verified content with
actual transfer receipts. Its concurrent-transfer and crash/retry tests are
required before enabling it. This slice's zero-transfer tests are not presented
as proof of a downloader that does not yet exist.

## Acceptance and verification

Use focused failing tests before implementation. Tiny synthetic fixtures only;
ordinary tests neither acquire real models nor depend on machine model caches.

- A complete bundle produces a held verified lease and readable immutable
  receipt; zero network/provider/download calls occur.
- Missing weights or any companion file, wrong length/hash, mutable/incomplete
  identity and malformed manifests refuse without modifying payloads.
- Repeated and concurrent processes resolving the same bundle cannot bypass
  locks, overwrite receipts or transfer any bytes. Cancellation, lock contention
  and process exit do not trigger repair, stale-lock deletion or fallback.
- Unavailable/unwritable/full J storage and receipt-publication failure fail
  closed. No directory or payload appears on another drive.
- Windows containment tests cover junctions/reparse points, path aliases and
  replacement during verification/use and metadata creation/publication. A
  rejected unsafe path is not opened through its target; redirected metadata
  ancestors cause zero writes outside the protected store. Invalid manifests
  do not cause partial store writes.
- Restart/context/worktree changes retain receipt usefulness but cannot bypass
  a fresh integrity check. Corruption after an earlier receipt still refuses.
- The CLI uses production-root policy and the real resolver, reports durable
  versus unpersisted refusal honestly and cannot accept a root override.
- Existing in-process indexing, GPU ownership/admission, deployment and provider
  default-disabled contracts remain unchanged.

Proposed retained test class names are `LocalModelStoreTests`,
`LocalModelStoreFileSystemTests` and `ModelStoreCommandTests`. Focused commands:

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter FullyQualifiedName~LocalModelStore
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~LocalModelStore|FullyQualifiedName~ModelStoreCommand"
dotnet build FluxKnowledge.slnx -c Release -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build
git diff --check
```

Set provider offline flags and J-only cache environment for the command process;
do not alter user/machine settings. Report fresh results and any skipped
platform tests explicitly. No real-model verification success may be claimed
from synthetic fixtures. Hashing a discovered legacy model is not a benchmark.

## Delivery, review and rollback

Likely changed locations are the two Models directories, CLI dispatch/command,
focused native Domain/Integration tests, and the affected architecture/roadmap
entries. No document parser, corpus suppression, SQL migration, provider package,
model payload or production configuration belongs in this branch.

First checkpoint: the working CLI-to-resolver-to-receipt path, refusal matrix
and preserved default-disabled native composition. Perform one independent
security/cost-invariant review of the completed implementation, remediate
specific findings, then run broad verification once. The architecture review
must settle any unsafe handle/lock boundary before implementation.

The next meaningful checkpoint is the PDF/VSDX document-owned runtime slice;
do not turn successive plans, inventories or fixture-only work into milestones.
At two meaningful checkpoints, if there is still no executable path, stop and
re-plan the smallest safe deliverable. GPU OCR still requires exact acquisition
approval, native runtime validation and an accuracy benchmark before activation.

Use `scripts/dev/complete-feature.ps1` for feature closeout, retaining the
worktree. There is no production action in this first cache slice. A later
authorised deployment uses only the incremental updater's reviewed `-PlanOnly`
then `-Apply`; this specification grants no additional deployment/replay scope.
Rollback disables the new consumer/command or reverts application code, never
deletes, moves or rewrites `J:\Models`, legacy caches or verification evidence.
