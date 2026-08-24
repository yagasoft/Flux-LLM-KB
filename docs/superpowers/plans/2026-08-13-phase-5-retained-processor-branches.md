# Phase 5 retained-content processor branches implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development task-by-task. Dispatch a fresh implementer and an independent reviewer for each task. Complete the ZIP slice review before any other processor family.

**Goal:** Deliver archive-zip-expand, an explicitly configured runnable retained ZIP processor that automatically replays bounded durable work from private artifacts and commits safe child provenance and fenced branch receipts.

**Architecture:** ZIP adds a narrow retained-processor branch executor beside the retained UTF-8 pipeline registration path. It claims only SQL deferred work, reads bytes only through the immutable retained revision/root/private-store binding, streams safe members to the content-addressed store, and atomically records child source state plus a fenced parent receipt. Its enabled local hosted activation service processes at most 16 claims in each batch.

**Tech stack:** .NET 10, C#, EF Core and SQL Server, System.IO.Compression, xUnit, existing content-addressed artifact store and disposable SQL fixture.

## Global constraints

- Capability name is archive-zip-expand. Its output contract is retained:archive-zip-expand.
- Enabled configuration makes the exact installed handler runnable and automatically replays eligible retained work in batches of 16. Disabled configuration promotes, claims and replays nothing.
- A processor reads only a checksum-verified immutable retained artifact through IRetainedSourceReader. It never rereads Outlook, Gmail, IMAP or watched-file originals and never opens a mailbox.
- A branch commit requires the same branch id, lease owner, lease generation and unexpired lease used for its claim.
- ZIP limits are 64 MiB compressed input, 256 entries, 128 MiB expanded total, 16 MiB per member, 512 logical-path characters and 100:1 compression ratio. Nested ZIP processing is prohibited.
- Trusted local projections, audit details, REST, MCP, CLI, SignalR and UI may expose useful retained-derived content, member names, paths, hashes and diagnostics under the private-PC policy; source control and external/public/export surfaces never contain private content or secrets. Credentials, tokens, keys, connection strings and secret literals are never exposed.
- Do not activate Outlook, modify its COM host, deploy, run live validation, use a network client, download/activate a model or enable any Phase 6 capability. Maintained deterministic parser packages/runtimes are permitted only under an approved processor design with offline preflight.
- Every behaviour change starts with a focused RED command and ends with fresh GREEN output. Native SQL is a pass only when the generated disposable fixture runs; provision the safe fixture when absent and report exact evidence.
- Add an EF migration but do not apply it outside the disposable test database.

---

## File map and interfaces

| File | Role |
| --- | --- |
| src/FluxKnowledge.Domain/Sources/RetainedProcessorBranch.cs | Branch, member, attempt, disposition and OriginKind invariants. |
| src/FluxKnowledge.Application/Ports/IRetainedProcessorBranchStore.cs | Fenced claims, retained reads, completion and retryable failure port. |
| src/FluxKnowledge.Application/Sources/RetainedProcessorActivationService.cs | Enabled registration, ZIP-only promotion and automatic bounded batches. |
| src/FluxKnowledge.Application/Sources/ZipArchiveRetainedProcessor.cs | Signature, central-directory policy and safe member orchestration. |
| src/FluxKnowledge.Application/Sources/SourceCapabilityService.cs | Exact ZIP descriptor/registry matching without a UTF-8-only replay rule. |
| src/FluxKnowledge.Application/Ports/IRetainedSourceReader.cs | Verified binary retained-artifact read contract. |
| src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs | Serializable claims, child persistence, branch receipts and recovery. |
| src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs | Existing no-follow private-root reader, extended to verified binary reads. |
| src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities | SQL entities for branch, attempts, members, relations and origin. |
| src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs | Keys, check constraints, collations, relationships and indexes. |
| src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations | Additive migration and model snapshot. |
| src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs | Local handler and activation service registrations. |
| src/FluxKnowledge.Web/WebHostComposition.cs | Options and wake registration only. |
| tests/FluxKnowledge.Domain.Tests/Sources/ZipArchiveRetainedProcessorTests.cs | Policy, activation, identity and invariant tests. |
| tests/FluxKnowledge.Integration.Tests/Sources/ZipArchiveReplayIntegrationTests.cs | Disposable SQL success, fence, receipt and recovery tests. |
| tests/FluxKnowledge.Web.Tests/Components/ZipArchiveProcessorPrivacyTests.cs | Sources/Corpus/Events public-surface privacy tests. |

The only cross-task execution seam is:

~~~csharp
public interface IRetainedProcessor
{
    SourceCapabilityDescriptor Descriptor { get; }
    ValueTask<RetainedProcessorResult> ProcessAsync(
        RetainedProcessorClaim claim,
        CancellationToken cancellationToken);
}

public interface IRetainedProcessorBranchStore
{
    ValueTask<int> PromoteSignatureConfirmedZipAsync(
        Guid capabilityId, int maximumCount, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimNextAsync(
        RegisteredSourceCapability capability,
        int maximumCount,
        string owner,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    ValueTask<RetainedArtifactPayload> ReadVerifiedArtifactAsync(
        RetainedProcessorClaim claim,
        CancellationToken cancellationToken);

    ValueTask<bool> CompleteAsync(
        RetainedProcessorCompletion completion,
        CancellationToken cancellationToken);

    ValueTask<bool> RecordRetryableFailureAsync(
        RetainedProcessorFailure failure,
        CancellationToken cancellationToken);
}

public enum SourceRevisionOriginKind
{
    RootDiscovered = 0,
    ArchiveMember = 1
}

public sealed record RetainedProcessorClaim(
    Guid BranchId,
    Guid SourceActivityId,
    SourceRevisionId SourceRevisionId,
    string ContentSha256,
    string ProcessorVersion,
    string ProcessorFingerprint,
    string LeaseOwner,
    long LeaseGeneration,
    DateTimeOffset LeaseExpiresAtUtc);

public sealed record RetainedArtifactPayload(
    SourceRevisionId SourceRevisionId,
    byte[] Bytes,
    string ContentSha256,
    long ByteLength);

public sealed record RetainedProcessorResult(
    int CreatedChildCount,
    int DeferredMemberCount,
    int BlockedMemberCount);

public sealed record RetainedProcessorCompletion(
    RetainedProcessorClaim Claim,
    RetainedProcessorResult Result,
    string CompletionReceiptFingerprint);

public sealed record RetainedProcessorFailure(
    RetainedProcessorClaim Claim,
    string ReasonCode);
~~~

RetainedProcessorClaim contains opaque ids, SourceRevisionId, immutable hash, descriptor version/fingerprint, owner and generation only. RetainedArtifactPayload contains verified bytes, length and hash only. Neither contains an original path or private root.

## Task 1: ZIP automatic replay vertical slice

**Files:**

- Create: src/FluxKnowledge.Domain/Sources/RetainedProcessorBranch.cs
- Create: src/FluxKnowledge.Application/Ports/IRetainedProcessorBranchStore.cs
- Create: src/FluxKnowledge.Application/Sources/RetainedProcessorActivationService.cs
- Create: src/FluxKnowledge.Application/Sources/ZipArchiveRetainedProcessor.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceProcessorBranchEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceProcessorAttemptEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceProcessorBranchMemberEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceActivityRelationEntity.cs
- Create: tests/FluxKnowledge.Domain.Tests/Sources/ZipArchiveRetainedProcessorTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Sources/ZipArchiveReplayIntegrationTests.cs
- Modify: src/FluxKnowledge.Application/Ports/IRetainedSourceReader.cs
- Modify: src/FluxKnowledge.Application/Sources/SourceCapabilityService.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs
- Modify: src/FluxKnowledge.Web/WebHostComposition.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260813120000_AddRetainedZipProcessorBranches.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs

**Consumes:** Durable deferred SourceActivity/DeferredCapability evidence, retained SourceRevision/SourceArtifact binding, content-addressed storage, source activity idempotency and NativeSqlServerFixture.

**Produces:** One valid ZIP can be automatically replayed from retained storage, creating one child retained text activity and one fenced parent completion receipt. A disabled option does none of this.

- [ ] **Step 1: Write domain RED tests**

~~~csharp
[Fact]
public async Task Enabled_archive_zip_expand_automatically_claims_one_signature_confirmed_retained_zip()
{
    var result = await activation.RunOnceAsync(CancellationToken.None);

    Assert.Equal("archive-zip-expand", result.Capability);
    Assert.Equal(1, result.CompletedBranches);
}

[Fact]
public void Archive_member_identity_uses_a_fingerprint_not_the_raw_entry_name()
{
    var identity = ArchiveMemberIdentity.Create("parent-stable", "docs/readme.txt");

    Assert.DoesNotContain("readme", identity.SyntheticLocator, StringComparison.OrdinalIgnoreCase);
}
~~~

- [ ] **Step 2: Capture RED evidence**

Run:

~~~powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~ZipArchiveRetainedProcessorTests" --nologo
~~~

Expected: FAIL because RetainedProcessorActivationService, ZipArchiveRetainedProcessor and ArchiveMemberIdentity are absent.

- [ ] **Step 3: Write integration RED evidence**

Seed DeferredUnsupported generic evidence with a retained ZIP in a disposable private artifact root and an intentionally missing source-original path. Assert that enabled activation creates one ArchiveExpansion successor, supersedes only the old activity, writes one child SourceRevision/SourceArtifact/activity, records one branch/attempt/completion receipt and succeeds without the source original.

~~~csharp
[NativeSqlServerFact]
public async Task Activation_replays_retained_zip_without_reading_missing_source_original()
{
    var result = await environment.ActivateAndDrainAsync();

    Assert.Equal(1, result.CompletedBranches);
    Assert.Single(await environment.ReadArchiveChildrenAsync());
    Assert.True(await environment.HasFencedCompletionReceiptAsync());
}
~~~

- [ ] **Step 4: Capture SQL RED evidence or exact skip**

Run:

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --filter "FullyQualifiedName~ZipArchiveReplayIntegrationTests" --nologo
~~~

Expected with FLUXKNOWLEDGE_TEST_SQL_CONNECTION: FAIL because branch schema and activation do not exist. Without it: record the Native SQL result as skipped because disposable SQL is unavailable, not as passed.

- [ ] **Step 5: Implement the minimum complete ZIP path**

Add the branch, attempt, member, relation and SourceRevision OriginKind migration described in the design. Use serializable SQL claims that increment lease generation. Extend the retained reader with verified binary reads by reusing its selected private-root lease, containment, no-follow, exact-length and checksum checks.

Register this exact descriptor:

~~~csharp
public static readonly SourceCapabilityDescriptor Capability = new(
    new Guid("b4a06e5d-6f01-4f73-9722-79b6df4e85c3"),
    "archive-zip-expand",
    "phase-5-zip-v1",
    ExecutionClass.InProcess,
    "phase-5-zip-retained-archive-v1",
    SourceActivityKind.ArchiveExpansion,
    "ArchiveZip",
    "retained:archive-zip-expand");
~~~

On enabled configuration, promote only signature-confirmed retained ZIP activities, claim at most 16 branches, process a regular UTF-8 member by streaming it directly into ContentAddressedSourceArtifactStore, insert the child revision/artifact/activity plus member row, and commit the parent receipt through the claim's current fence. On false configuration, do not register runnable, promote, claim or replay.

- [ ] **Step 6: Capture GREEN evidence**

Run:

~~~powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ZipArchiveRetainedProcessorTests" --nologo
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ZipArchiveReplayIntegrationTests" --nologo
~~~

Expected: restore/build are clean, domain tests pass, and configured Native SQL tests pass. If SQL is unavailable, state its exact skip rather than claiming this integration gate passed.

- [ ] **Step 7: Commit**

~~~powershell
git add src tests
git commit -m "feat: add retained ZIP processor replay"
~~~

## Task 2: ZIP hostile input, recovery and public privacy

**Files:**

- Modify: src/FluxKnowledge.Application/Sources/ZipArchiveRetainedProcessor.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs
- Modify: src/FluxKnowledge.Domain/Sources/RetainedProcessorBranch.cs
- Modify: src/FluxKnowledge.Application/Sources/RetainedProcessorActivationService.cs
- Modify: tests/FluxKnowledge.Domain.Tests/Sources/ZipArchiveRetainedProcessorTests.cs
- Modify: tests/FluxKnowledge.Integration.Tests/Sources/ZipArchiveReplayIntegrationTests.cs
- Create: tests/FluxKnowledge.Web.Tests/Components/ZipArchiveProcessorPrivacyTests.cs
- Modify: tests/FluxKnowledge.Integration.Tests/Persistence/SchemaMappingTests.cs

**Consumes:** Task 1's runnable ZIP branch contract and retained-only reader.

**Produces:** Every hostile ZIP outcome, automatic batch continuation, restart/fence proof and no-private-data public projections. Completion of this task completes the first processor slice.

- [ ] **Step 1: Write RED policy, fence and privacy tests**

Use data-only ZIP fixtures for traversal, rooted path, alternate stream, link/reparse type, encrypted entry, unsupported compression, duplicate identity, 257 entries, 64 MiB plus one input, 128 MiB plus one expanded total, 16 MiB plus one member, 513-character path, 100:1 plus one ratio and nested ZIP.

~~~csharp
[Theory]
[MemberData(nameof(RejectedZipCases))]
public async Task Unsafe_zip_records_sanitised_outcome_and_creates_no_child(
    string expectedCode, byte[] archive)
{
    var result = await environment.ProcessAsync(archive);

    Assert.Equal(expectedCode, Assert.Single(result.BlockedReasons));
    Assert.Empty(await environment.ReadArchiveChildrenAsync());
}

[NativeSqlServerFact]
public async Task Stale_branch_generation_cannot_commit_children_or_completion()
{
    var stale = await environment.ClaimAsync();
    await environment.ReclaimAsync();

    Assert.False(await environment.CompleteAsync(stale));
}
~~~

Use private-root sentinel C:\private-spool-sentinel and entry sentinel confidential-member-sentinel.txt in a Sources/Corpus/Events projection test.

- [ ] **Step 2: Capture RED evidence**

Run:

~~~powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ZipArchiveRetainedProcessorTests" --nologo
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ZipArchiveReplayIntegrationTests" --nologo
~~~

Expected with disposable SQL: FAIL because hostile outcomes, stale-fence protection and restart reconciliation are absent. Without disposable SQL, record the integration command as skipped.

- [ ] **Step 3: Implement safety and reconciliation**

Validate central-directory information before member streaming and apply every Global Constraint value. Give each unsafe in-scope member one member disposition with its exact fixed reason code. On cancellation or transient retained-store failure, write a retryable attempt receipt through the current fence and write no completion receipt. On restart, reconcile incomplete branch/member state with content-addressed artifacts before releasing expired claims.

Wake after every non-empty batch. Assert false configuration leaves old generic work untouched. Make branch completion require every member to have a child record or explicit blocked/deferred outcome. Assert serialised Sources, Corpus, Events, audit, REST and SignalR projection records contain neither privacy sentinel.

- [ ] **Step 4: Capture GREEN evidence**

Run:

~~~powershell
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ZipArchiveRetainedProcessorTests" --nologo
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ZipArchiveReplayIntegrationTests|FullyQualifiedName~SchemaMappingTests" --nologo
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ZipArchiveProcessorPrivacyTests|FullyQualifiedName~SourceRootProjection|FullyQualifiedName~CorpusProjection|FullyQualifiedName~OperatorEventProjection" --nologo
~~~

Expected: every runnable selected test passes and the build emits no warning. If Native SQL does not run, state its exact fixture skip and leave that gate unpassed.

- [ ] **Step 5: Commit and slice-review**

~~~powershell
git add src tests
git commit -m "test: harden retained ZIP processor safety"
~~~

Create a complete Task 1 base-to-head diff package. An independent reviewer must approve both specification compliance and task quality before Task 3 begins.

## Task 3: deterministic TAR archive branch

**Files:**

- Create: src/FluxKnowledge.Application/Sources/TarArchiveRetainedProcessor.cs
- Modify: src/FluxKnowledge.Application/Sources/RetainedProcessorActivationService.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs
- Create: tests/FluxKnowledge.Domain.Tests/Sources/TarArchiveRetainedProcessorTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Sources/TarArchiveReplayIntegrationTests.cs

**Consumes:** ZIP branch claim, member receipt, retained-reader and privacy contracts.

**Produces:** archive-tar-expand using System.Formats.Tar, an already available target-framework API. It has TAR-specific signatures, member metadata validation and bounded retained replay.

- [ ] **Step 1: Write TAR RED tests**

Test ustar/GNU TAR retained-only success, idempotency, disabled configuration, missing/corrupt retained artifact, path/link/device rejection, all inherited bounds and public-projection privacy. The TAR capability name is archive-tar-expand.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~TarArchiveRetainedProcessorTests" --nologo
~~~

Expected: FAIL because the TAR processor is absent.

- [ ] **Step 3: Implement TAR only**

Use System.Formats.Tar and the ZIP branch/member receipts. Stream only validated regular TAR members into private content-addressed storage. Reject links, devices, sparse/unsupported entries and unsafe paths. Do not add a downloader or a runtime. Compressed TAR variants remain deferred until their dedicated safety policy is approved.

- [ ] **Step 4: Run GREEN, commit and review**

Run TAR domain, disposable SQL and projection tests using the configured-versus-skipped SQL rule. Commit with:

~~~powershell
git add src tests
git commit -m "feat: add retained TAR archive processor"
~~~

Obtain independent slice approval.

## Task 4: Office document structural extraction

This task is amended by the [Office document implementation plan](2026-08-13-phase-5-office-document-amendment.md). That amendment supersedes the earlier OOXML-only files and capability assumptions. It delivers automatic retained-only `.docx`, `.xlsx` and `.pptx` structural extraction; `.doc`, `.xls` and `.ppt` are durable deferred work with `legacy-office-binary-parser-unavailable` until the user approves a proper legacy parser library.

**Consumes:** retained branches and archive-member provenance.

**Produces:** Safe OOXML structural text, durable legacy Office parser-unavailable evidence and a fenced blocked-OOXML-document operator action.

## Task 5: deterministic retained UTF-8 code parsing

**Files:**

- Create: src/FluxKnowledge.Application/Sources/RetainedCodeParser.cs
- Modify: src/FluxKnowledge.Application/Sources/SourceClassifier.cs
- Modify: src/FluxKnowledge.Application/Sources/SourceCapabilityService.cs
- Create: tests/FluxKnowledge.Domain.Tests/Sources/RetainedCodeParserTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Sources/RetainedCodeReplayIntegrationTests.cs

**Consumes:** Verified retained bytes, explicit activation and the actual available local parser inventory.

**Produces:** Code classification/parsing for one proven language. Unsupported languages remain deferred.

- [ ] **Step 1: Write and run RED tests**

Cover a supported language, invalid UTF-8, bound exceeded, disabled capability, retained-only success, idempotent replay, missing/corrupt artifact and no raw code in public evidence.

- [ ] **Step 2: Implement minimal proven parser**

Read only retained strict UTF-8 bytes, bound length/depth and persist only deterministic safe identifiers/relationships through existing contracts. Do not download a grammar, add an unavailable parser or persist raw source to public surfaces.

- [ ] **Step 3: Capture GREEN, commit and review**

Run focused domain, disposable SQL and privacy tests, reporting SQL skips accurately. Commit:

~~~powershell
git add src tests
git commit -m "feat: add retained deterministic code parsing"
~~~

Obtain independent slice approval.

## Task 6: deterministic image/media metadata

**Files:**

- Create: src/FluxKnowledge.Application/Sources/MediaMetadataRetainedProcessor.cs
- Create: tests/FluxKnowledge.Domain.Tests/Sources/MediaMetadataRetainedProcessorTests.cs
- Create: tests/FluxKnowledge.Integration.Tests/Sources/MediaMetadataReplayIntegrationTests.cs

**Consumes:** Retained branch execution and a verified already-present parser.

**Produces:** Deterministic metadata only, otherwise durable deferred evidence.

- [x] **Step 1: Write and run RED tests**

Cover signature, retained-only success, idempotency, disabled capability, bounds, missing/corrupt artifact and public privacy. Capture the missing-processor failure.

- [x] **Step 2: Implement only safe local metadata**

Persist dimensions, duration or container values only where a safe present parser needs no model, download or network client. OCR, descriptions, transcript, ASR, frame extraction and embeddings remain excluded.

- [x] **Step 3: Capture GREEN, commit and review**

Run focused tests and accurate SQL-gate reporting. Commit:

~~~powershell
git add src tests
git commit -m "feat: add retained media metadata processor"
~~~

Independent whole-slice approval completed after remediation. The delivered processor is disabled by default and uses only checksum-verified app-owned retained bytes with `MetadataExtractor` 2.9.3. It writes one bounded, canonical, secret-scanned structural metadata child through the existing generic branch path; OCR, frames, vision, ASR, embeddings, FFmpeg, models and GPU work remain Phase 6.

Final evidence: locked restore; zero-warning Release build; focused Domain/Classifier 78, disposable-SQL Integration 23 and Web 23; EF reported no pending model changes; full non-browser native Release suite passed Domain 565, Integration 744, Web 173 and Outlook 71. The final independent whole-slice review was clean.

## Milestone verification

- [ ] Run after each completed slice:

~~~powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Sources" --nologo
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Sources|FullyQualifiedName~SchemaMappingTests" --nologo
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~SourceRoot|FullyQualifiedName~CorpusProjection|FullyQualifiedName~OperatorEventProjection" --nologo
~~~

- [ ] Report a Native SQL command as passed only when its fixture executed. Otherwise report FLUXKNOWLEDGE_TEST_SQL_CONNECTION absent and its precise skipped count.
- [ ] Run scripts/dev/assert-legacy-gmail-unchanged.ps1 after a task changes shared composition or contracts. Investigate any difference.
- [ ] Obtain an independent whole-branch review after all authorised slices. Do not run scripts/dev/complete-feature.ps1, deploy, merge, push or use a non-disposable migration without a new user authorisation.

## Plan self-review

- Coverage: Tasks 1 and 2 deliver all ZIP activation, automatic replay, hostile-input, retained binding, provenance, fence, receipt, recovery and privacy requirements. Tasks 3 through 6 sequence the remaining Phase 5 branches without Phase 6 activation.
- Type consistency: IRetainedProcessor, IRetainedProcessorBranchStore, RetainedProcessorClaim, RetainedArtifactPayload, RetainedProcessorCompletion and RetainedProcessorFailure are the only new execution interfaces.
- Scope: ZIP first exercises the shared contract; no separate generic foundation milestone exists.
