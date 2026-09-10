# Native model-store implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans after the user's next approval. Steps use checkbox syntax for tracking.

**Goal:** Deliver one executable offline gate: local manifest → held, verified files → immutable receipt → CLI JSON and exit status. A cache miss is an auditable refusal, never permission to download or use another drive.

**Architecture:** Retain the approved Application contract, Inference resolver and existing Windows filesystem helper in Integrations. Use one narrow held-file adapter between assemblies, with a small internal filesystem seam for tests; no general storage/session/acquisition framework.

**Tech stack:** Existing .NET 10/C#, System.Text.Json, streaming SHA-256, native Windows handles and xUnit. No new third-party packages.

**Spec:** [Approved native model-store specification](../specs/2026-09-09-native-model-store-design.md). The explicit differences below require the user's next approval; this revision does not silently amend that specification.

## Status and global constraints

Implementation was approved and completed on 2026-09-10 in the dedicated worktree. The delivery used only tiny synthetic fixture files; it did not access or modify `J:\Models`, load/download/copy/move models, deploy, or touch PDF/VSDX/OCR work.

Resume only when instructed, in the existing worktree `E:\LLM KB\.worktrees\native-model-store`, branch `codex/native-model-store`. Check Git state and preserve unrelated changes. Do not create another worktree or replay unrelated branches.

- Production root is fixed to `J:\Models`. No CLI, environment, configuration or public root override; no fallback and no automatic root creation.
- This gate has no downloader, provider loader, network client, process launcher or implicit acquisition/hydration path. Offline flags alone are not enforcement.
- Verification reads existing payloads only. Its sole store writes are verification receipts and their protected metadata directories/staging. Existing inventory and payloads remain unchanged.
- Tests use tiny synthetic files in isolated directories through an internal test-only construction seam. They never use actual J: or existing model caches.
- No SQL/IIS/worker/provider initialisation, remote API, schema, pipeline, GPU, deployment, licence or document-processing changes.
- A receipt is evidence of one observation, not a reusable lease, model activation, runtime compatibility or permission to acquire anything.

## Reconciliation with the approved spec

The spec remains unchanged. Its core offline, fixed-root, handle-protection and immutable-receipt requirements remain binding. These are the exact scope differences to approve, not claims that the entire earlier spec is satisfied:

| Spec location and requirement | Smaller first milestone |
| --- | --- |
| **Contracts and ownership**, paragraph beginning “A bundle lists every required…”: mandatory repository, format/precision and conversion source/tool/runtime provenance. | Require only immutable revision, filename, SHA-256 and byte length per file. Repository/format/precision/conversion details may be optional metadata included in the manifest fingerprint. Do not claim verified upstream provenance or conversion reproducibility. Full provenance is deferred. |
| **Contracts and ownership**, store-layout paragraph; **Resolution and fail-closed behaviour**, step 3; stable-reasons paragraph: persistent per-artifact cross-process locks, ordered acquisition and lock-contention handling. | No lock-file subsystem for this read-only gate. Held files deny write/delete sharing; concurrent readers use independent handles and exclusive receipt publication. Acquisition-style coordination and its lock-specific outcomes are deferred. This changes an explicit spec requirement. |
| **Acceptance and verification**, repeated/concurrent-process bullet: cross-process contention, cancellation and process-exit evidence. | Test ordinary concurrent independent resolver instances and normal lease disposal/cancellation. Defer the child-process host/crash matrix unless these tests reveal a concrete need. Do not claim cross-process/crash coverage. |
| **Acceptance and verification**, containment bullet: replacement during verification/use and metadata creation/publication. | Retain the protection invariant and focused real-filesystem tests for unsafe/reparse ancestors, held-file lifetime and protected receipt writes. Defer a separate deterministic race/fault harness covering every boundary; do not claim that exhaustive evidence. |

The old plan's fixed free-space reserve and volume-GUID consumer-path API were implementation additions, not mechanisms prescribed by the spec. Remove them. Keep the spec's protected-volume/ancestor invariant through retained handles, expose held reads rather than paths that a consumer must reopen, and fail closed on actual access/disk-full/receipt-write errors. Do not promise a free-space reservation or a path-based provider contract.

Retain the spec's exclusive staging, flush and atomic no-overwrite receipt publication. Reduce fault tests to receipt-write failure preventing success and collision leaving the existing receipt unchanged. The future-acquisition section stays future-only; none of its adoption, download or conversion machinery belongs in this milestone.

## Small contract and file map

All paths are relative to this worktree; files below are planned, not implemented.

| Files | Responsibility |
| --- | --- |
| `src/FluxKnowledge.Application/Models/ModelBundleSpecification.cs` | Immutable manifest snapshot, bounded parsing/validation and canonical fingerprint. |
| `src/FluxKnowledge.Application/Models/ILocalModelStore.cs` | `ResolveAsync`, typed result and abstract disposable `VerifiedLocalModelLease` contract; its internal implementation stays with the Inference resolver. |
| `src/FluxKnowledge.Application/Models/ModelVerificationFiles.cs` | One narrow disposable held-file adapter: open a literal artifact for reading and publish a receipt. No storage/session/lock/provider hierarchy. |
| `src/FluxKnowledge.Infrastructure.Inference/Models/LocalModelStore.cs` | Validate, stream exact length/hash checks, collect observations, require receipt before issuing lease. |
| `src/FluxKnowledge.Integrations/Models/WindowsModelVerificationFiles.cs` | Fixed-root production factory, pinned handles and small internal test seam for isolated roots and I/O failures. |
| `src/FluxKnowledge.Integrations/Windows/NativeGoLive/HandleRelativeNativeFileSystem.cs` and `src/FluxKnowledge.Integrations/Windows/NativeGoLive/HandleRelativeNativeFileSystem.ModelStore.cs` | Make the existing class partial; add only held read/create and no-replace publication operations using its existing native primitives. Do not change old callers' semantics or invoke GoLive. |
| `src/FluxKnowledge.Cli/Commands/ModelStoreCommand.cs`, `src/FluxKnowledge.Cli/Program.cs`, `src/FluxKnowledge.Cli/FluxKnowledge.Cli.csproj` | Direct local composition, one dispatch arm and the required Inference project reference. |
| `tests/FluxKnowledge.Integration.Tests/Models/LocalModelStoreTests.cs`, `tests/FluxKnowledge.Integration.Tests/Models/LocalModelFixture.cs`, `tests/FluxKnowledge.Integration.Tests/Cli/ModelStoreCommandTests.cs` | Synthetic real-filesystem, resolver and CLI coverage; reuse existing test-assembly access. No new test-host project. |

Keep result/observation/receipt records with the small contract or resolver they serve. Do not split each into a framework or separate service. Existing Application and Integrations test-friend access is sufficient for internal fixture construction; do not add a public alternate-root factory.

The manifest is bounded local UTF-8 JSON with a version and a non-empty file list. Each file requires `revision`, `filename`, `sha256`, `byteLength`; optional metadata is descriptive, never an executable instruction or source URL to fetch. Copy parsed collections into an immutable snapshot. Reject duplicate fields/identities, conflicting or case-colliding local names, unknown core fields, mutable revisions, invalid hashes, negative lengths and checked-total overflow before store access. Use a 1 MiB manifest bound, at most 256 files and bounded strings/depth; revision is a 40- or 64-hex immutable identifier and SHA-256 is 64 hex.

`filename` is a safe relative artifact identifier. Derive the existing specified local layout `J:\Models\artifacts\sha256\<digest>\<leaf>` from its validated leaf; never append unchecked input to a root. Every listed companion is required. This proves completeness against that manifest, not that an incomplete user-authored list is a usable model.

`ILocalModelStore.ResolveAsync(specification, cancellationToken)` returns success/refusal, reason, manifest fingerprint, per-file observations, `receiptPersisted`, receipt location and an optional lease. Failure always has no lease. The lease owns all read handles and protected ancestors until disposal; expose read-only content by manifest identity, not an unprotected pathname or repository fallback. Dispose deterministically, including cancellation and partial verification failure; a later resolve always hashes again.

Receipts under `J:\Models\inventory\verifications` include verifier version, time, unique identity, manifest fingerprint, per-file observations/reasons, verified bytes and `transferredBytes: 0`. A usable store records missing/corrupt-file refusals too. If J: or its receipt directory cannot be safely written, return structured refusal with `receiptPersisted: false`, preserving the original file failure where applicable; never fabricate durable evidence or write elsewhere. Receipt failure prevents a success lease.

## Milestone 1: executable offline model gate

The observable acceptance path is **synthetic manifest → real Windows verification → receipt → CLI result**, for both success and missing-companion refusal. Deliver this as one coherent implementation batch, not separate foundation/harness milestones.

- [x] **1. Add focused failing tests before implementation.**

Use `LocalModelStoreTests` and `ModelStoreCommandTests` with generated synthetic files. Add only enough contract scaffolding to compile, then demonstrate behavioural failures before implementing the gate. Retain this focused matrix:

| Case | Required assertion |
| --- | --- |
| Valid bundle | Every required file verifies; held read-only lease and readable immutable receipt; exit 0. |
| Missing companion | Specific `model-artifact-missing` observation, no lease, auditable refusal and no payload creation; exit 1. |
| Length/hash mismatch | Distinct length and SHA-256 reasons; no repair or overwrite. A previous receipt cannot bypass a fresh hash. |
| Unsafe paths/reparse points | Reject traversal, absolute/drive/UNC/backslash/ADS and Windows aliases; reject file, payload-ancestor and receipt-ancestor reparse points before following/writing. Outside sentinel remains unchanged. |
| Unavailable/unwritable J: simulation | Internal fixture seam supplies missing-root/access-denied conditions; refusal, honest receipt status and no fallback. Simulate disk-full at receipt write, not a reservation subsystem. |
| Held lease | Attempts to write/delete/replace synthetic payloads fail while leased; read remains usable; disposal releases protection. |
| Receipt write failure/collision | Write failure gives no success lease. A forced receipt-name collision leaves prior bytes unchanged; no overwrite or silent reuse. |
| Zero acquisition/provider calls | Record the narrow adapter's operations and assert only local open/read/receipt work. Retain a composition/dependency test that the gate and direct CLI path have no downloader, provider, network or process-launch dependency; inspect their call graph too. Do not invent a downloader to count it. `transferredBytes: 0` alone is insufficient evidence. |
| Ordinary concurrency | `Task.WhenAll` with independent resolver instances over one fixture returns correct complete leases/refusals and distinct receipts; repeated reads never change payloads. Cancellation/failure releases owned handles. |
| CLI isolation | Structured results/exit codes; root overrides and unknown options rejected; no SQL, IIS, workers or providers initialised. |

For example, use the real resolver, delete only a fixture-owned companion, then assert the refusal:

~~~csharp
using var fixture = LocalModelFixture.Create();
var manifest = fixture.SeedCompleteBundle();
fixture.RemoveCompanion();
var result = await fixture.Store.ResolveAsync(manifest, CancellationToken.None);
Assert.False(result.Succeeded);
Assert.Equal("model-artifact-missing", result.ReasonCode);
Assert.Null(result.Lease);
Assert.True(result.ReceiptPersisted);
Assert.False(fixture.CompanionExists);
~~~

Run the focused command in step 4; record the expected behavioural failures, not just missing-type/compiler errors.

- [x] **2. Implement the minimal verification/receipt path.**

Compose `LocalModelStore` with a factory for the narrow `ModelVerificationFiles` adapter. The production Windows factory always opens `J:\Models`; only the internal test seam supplies fixture roots or typed I/O failures. Validate first; then pin the actual J: volume and each ancestor through existing no-follow, handle-relative operations before any read/create/write. Keep those handles for the operation/lease lifetime; no later unprotected root/path resolution.

Open payloads read-only with no write/delete sharing and bounded streaming SHA-256/length verification. Refuse unsafe/reparse/offline-recall files before reading; do not hydrate them. Missing/corrupt/unavailable files never invoke any alternate path, repair or acquisition. Use native error codes for stable reasons, not exception-message matching.

Reuse `OpenRelative` and no-replace `RenameRelative` primitives. Existing `ReadLiteralFileAsync` uses `ShareAll` and materialises content; it is unsuitable for model payloads. Existing directory walking drops earlier ancestors; retain the chain here. Existing replacement helpers must not publish receipts. Add only the small held-handle operations needed, preserving old helper behaviour.

Create receipt metadata/staging through pinned parents, write and flush, then atomically publish without replacement. Transfer ownership of verified handles to the lease only after receipt success; otherwise release them. Keep stable reasons for invalid manifest, unsafe path, unavailable/not-writable store, missing artifact, length/hash mismatch, sharing conflict and receipt I/O failure. Preserve cancellation as cancellation. No per-artifact locks, retries that acquire content, free-space reservation or consumer-path API.

- [x] **3. Add and exercise the local CLI.**

Accept exactly `models verify --manifest <local-file>`; reject root/download options, URLs and unknown arguments. Read bounded local manifest bytes once; its location may be outside J: but never becomes a payload root or fallback. Reject offline/recall-required manifest input rather than hydrating it.

Dispatch directly to `ModelStoreCommand` and compose the gate without the normal service host. Emit JSON metadata, not streams/lease internals; dispose the lease before return. Exit 0 means complete verification plus receipt; 1 means refusal (including receipt failure); 2 means invalid command/manifest. Report cancellation without pretending verification succeeded. Exercise the real resolver through internal fixture composition and public argument dispatch without touching actual J:.

- [x] **4. Run focused green checks and review the executable slice.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~LocalModelStore|FullyQualifiedName~ModelStoreCommand|FullyQualifiedName~HandleRelativeNativeFileSystem"
~~~

Require all focused cases to pass and inspect actual synthetic CLI/receipt output. Review the direct gate/command call graph for zero acquisition/provider/network initialisation and unchanged existing filesystem semantics. Use one independent review of the completed cost/security invariant family and whole diff, not a reviewer per test. Revisit deferred harness work only if a concrete failure cannot be established or resolved with ordinary tests; report the reason before expanding the milestone.

**Budget checkpoint:** completed. The end-to-end synthetic manifest → Windows verification → immutable receipt → CLI success/refusal path is exercised. Focused Release evidence passed 52 tests; an independent re-review corrected and then approved receipt-staging, offline/recall, mapped-drive and cancellation/ownership boundaries.

- [x] **5. Verify and close out after implementation approval and completion.**

Update only the affected architecture/roadmap entries with demonstrated gate capability and remaining work. Review the final diff for scope. Ordinary tests must not acquire/load models; do not import providers as a validation step.

~~~powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build
git diff --check
~~~

Fresh completion evidence passed locked restore, a Release `-warnaserror` build with zero warnings/errors, and the serial full suite: 2,130 passed, 15 existing browser skips, 0 failures. Use `scripts/dev/complete-feature.ps1` with `-FeatureWorktree 'E:\LLM KB\.worktrees\native-model-store' -MainRoot 'E:\LLM KB' -CommitMessage 'Enforce offline native model-store verification' -KeepWorktree`; omit deployment/GoLive flags. If it fails, report JSON `failed_step` and `log_path`, correct that failure and rerun.

No deployment or live source processing belongs in this milestone. Rollback reverts/disables application code; it never changes payloads, receipts or legacy caches.

## Explicitly deferred

- Any downloader, cache adoption/copy/move/conversion or acquisition coordination; no cache inventory/adoption task is a prerequisite for implementing synthetic offline verification.
- Runtime/provider/GPU activation, OCR, PDF/VSDX and other document work.
- Full conversion-provenance machinery beyond optional manifest metadata.
- Child-process host/crash harness unless ordinary concurrency reveals a demonstrated need.
- Exhaustive per-boundary receipt fault injection beyond write-failure and no-overwrite tests.
- Elaborate free-space reservation and volume-GUID consumer-path work.
- Broad filesystem/storage/session abstractions or new services beyond the narrow held-file bridge and internal test seam.

The hard `AGENTS.md` central-cache rule remains unchanged for any future model operation. Before any separately proposed acquisition, check existing J: and relevant legacy caches, establish exact missing artifacts/bytes, and obtain explicit acquisition approval. This plan neither authorises acquisition nor implements that future subsystem.

## Plan self-review and completion record

- [x] One independently executable milestone, with real CLI → verifier → immutable receipt and auditable refusal.
- [x] All requested first-slice cases retained; mandatory provenance, lock/process and exhaustive-test differences identified against the unchanged spec.
- [x] Fixed root, no-follow protection, held reads, receipt-before-success and zero-acquisition invariant preserved.
- [x] No model-cache operations, deployment or document work occurred during implementation.
- [x] File names, contract names, test filter and success/refusal semantics checked for consistency; no child-host or deferred subsystem remains in the execution steps.

The completed gate remains an offline verification boundary only. It does not authorise any model acquisition, adoption, activation or document-processing work.
