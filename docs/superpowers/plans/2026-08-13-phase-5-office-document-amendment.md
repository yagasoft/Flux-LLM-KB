# Phase 5 OOXML document processor implementation plan

> **Task 4B status:** plan-amended, pending independent design approval. The
> Task 4B material below is not approved implementation authority.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development task-by-task. Dispatch a fresh implementer and an independent reviewer for each task.

**Goal:** Deliver automatic, retained-only structural extraction for `.docx`,
`.xlsx` and `.pptx`; keep `.doc`, `.xls` and `.ppt` as durable unsupported
capability work until the operator supplies an approved legacy parser library;
and provide a fenced force-attempt only for genuinely blocked OOXML branches.

**Architecture:** `document-ooxml-structural-extract` is an InProcess retained
processor with precedence over generic ZIP expansion. It uses `ZipArchive` and
bounded `XmlReader` only, produces up to two private UTF-8 structural-text
children, and preserves the current fenced branch contract. The refactor makes
the archive-derived child manifest generic while keeping ZIP/TAR parent
`ArchiveExpansion` and derived-child `TextExtraction` behaviour unchanged.
Legacy Compound File Binary formats retain the unregistered required capability
`document-office-legacy-structural-extract` and reason
`legacy-office-binary-parser-unavailable`.

**Tech stack:** .NET 10, C#, EF Core/SQL Server, `System.IO.Compression`,
`System.Xml.XmlReader`, existing content-addressed private artifact store,
existing status-event feed, Blazor, xUnit and the disposable SQL fixture. No
Office automation, conversion executable, model/runtime download or network
client.

## Global constraints

- Active descriptor: `document-ooxml-structural-extract`, ID `3d72bf21-5358-482d-a6a9-576ff23012a3`, version `phase-5-ooxml-structural-v1`, fingerprint `phase-5-ooxml-retained-structural-v1`, output `retained:document-ooxml-structural-extract`, parent kind `TextExtraction`.
- `OoxmlDocumentStructuralExtractEnabled` defaults false; an enabled exact descriptor automatically processes at most 16 retained branches per batch.
- Active formats are `.docx`, `.xlsx` and `.pptx`. `.doc`, `.xls` and `.ppt` remain `DeferredUnsupported`, retain `document-office-legacy-structural-extract` and reason `legacy-office-binary-parser-unavailable`, and are neither registered, promoted, claimed nor forceable.
- All reads use only checksum-verified immutable retained artifacts through their revision/root/private-store binding. Never reread a source original, mailbox or watched path.
- Limits: 128 MiB retained input; 256 MiB expanded selected XML; 200,000 XML elements; depth 128; 32 MiB extracted text as at most two private 16 MiB UTF-8 children; 512 ZIP entries; 8,192 relationships; 32 MiB selected part; 512 path characters; 100:1 ratio.
- OOXML rejects invalid package topology, duplicate/linked/encrypted/multi-volume/unsupported entries, rooted/traversal/alternate-stream/NUL paths, invalid XML and unbounded content before a child write.
- Public SQL projections, audit, REST, MCP, CLI, status events, UI and source control must not contain raw content, private paths, source identifiers, mailbox identifiers, spool details, credentials or parser diagnostics.
- A force attempt never changes a limit, integrity check, descriptor, source binding or lease fence. It is unavailable for legacy-parser-unavailable and retained-artifact missing/path/checksum outcomes.
- Use additive migrations only in generated disposable SQL databases. Do not deploy, push, merge, run non-disposable migrations or live validation.

---

## Task 4A: retained OOXML extraction and derived-child manifest

**Files:**

- Create: `src/FluxKnowledge.Application/Sources/OoxmlStructuralTextProcessor.cs`
- Create: `src/FluxKnowledge.Application/Sources/OoxmlStructuralTextReader.cs`
- Modify: `src/FluxKnowledge.Application/Sources/RetainedProcessorActivationService.cs`
- Modify: `src/FluxKnowledge.Application/Sources/RetainedProcessorActivationHostedService.cs`
- Modify: `src/FluxKnowledge.Application/Sources/ZipArchiveRetainedProcessor.cs`
- Modify: `src/FluxKnowledge.Application/Sources/TarArchiveRetainedProcessor.cs`
- Modify: `src/FluxKnowledge.Application/Ports/IRetainedProcessorBranchStore.cs`
- Modify: `src/FluxKnowledge.Domain/Sources/RetainedProcessorBranch.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceScanStore.cs`
- Modify: `src/FluxKnowledge.Application/Sources/SourceClassifier.cs`
- Modify: `src/FluxKnowledge.Application/Ports/IRetainedSourceReader.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Workers/OutboxWorkerRegistration.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`
- Create: `tests/FluxKnowledge.Domain.Tests/Sources/OoxmlStructuralTextProcessorTests.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Sources/OoxmlReplayIntegrationTests.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Components/OoxmlStructuralTextPrivacyTests.cs`
- Modify: `tests/FluxKnowledge.Domain.Tests/Sources/ZipArchiveRetainedProcessorTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Sources/ZipArchiveReplayIntegrationTests.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Sources/TarArchiveReplayIntegrationTests.cs`

**Consumes:** the ZIP/TAR branch receipts, the verified retained reader and BCL
ZIP/XML APIs.

**Produces:** an enabled automatic OOXML processor, durable legacy
parser-unavailable evidence and a generic derived-child SQL manifest that does
not regress archive provenance.

**Interfaces:**

```csharp
public sealed record RetainedProcessorDerivedChild(
    string MemberFingerprint,
    string SyntheticLocator,
    string StableSourceIdentity,
    string ContentSha256,
    string StoreRelativePath,
    long ByteLength,
    string Classification,
    int OriginKind,
    string Extension);

public sealed record RetainedProcessorCompletion(
    IReadOnlyList<RetainedProcessorDerivedChild> Members,
    string ReceiptFingerprint);

public sealed record RetainedProcessorPromotionCandidate(
    Guid LegacyActivityId,
    SourceRevisionId SourceRevisionId,
    string InputSha256,
    string Extension);

public sealed record RetainedArtifactInspection(
    SourceRevisionId SourceRevisionId,
    string ContentSha256,
    long ByteLength);

public interface IRetainedSourceReader
{
    ValueTask<RetainedArtifactInspection> InspectAsync(
        SourceRevisionId sourceRevisionId,
        CancellationToken cancellationToken);
}
```

The parent successor uses `SourceCapabilityDescriptor.AcceptedActivityKind`.
ZIP/TAR keep parent `ArchiveExpansion`; OOXML uses `TextExtraction`. Every
derived text child keeps `TextExtraction` / `phase-3a-v1`; only its opaque
manifest and origin differ. Map `OfficeStructuralSegment=2` only after a schema
test proves no existing constraint rejects the value.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[MemberData(nameof(OoxmlFixtures))]
public async Task Enabled_processor_replays_only_the_retained_ooxml_artifact(OoxmlFixture fixture)
{
    var result = await environment.ProcessWithMissingOriginalAsync(fixture);
    Assert.True(result.Completed);
    Assert.Equal("document-ooxml-structural-extract", result.Capability);
    Assert.InRange(result.PrivateChildCount, 1, 2);
}

[Theory]
[InlineData(".doc")]
[InlineData(".xls")]
[InlineData(".ppt")]
public async Task Legacy_binary_office_is_durable_unregistered_deferred_work(string extension)
{
    Assert.Equal("legacy-office-binary-parser-unavailable",
        await environment.ReadDeferredReasonAsync(extension));
    Assert.False(await environment.IsCapabilityRegisteredAsync("document-office-legacy-structural-extract"));
}

[Fact]
public async Task Ooxml_package_is_promoted_before_generic_zip() =>
    Assert.Equal("document-ooxml-structural-extract", await environment.PromoteAsync(DocxFixture));

[Fact]
public async Task Disabled_ooxml_with_enabled_zip_leaves_a_likely_ooxml_package_deferred()
{
    var result = await environment.RunActivationAsync(ooxmlEnabled: false, zipEnabled: true, DocxFixture);
    Assert.Equal("DeferredUnsupported", result.State);
    Assert.Equal(0, result.ArchiveZipBranches);
}
```

Add hostile tests for each fixed OOXML outcome, DTD/external-resource rejection,
selected-part/relationship/path/ratio bounds, 200,001 elements, depth 129,
32 MiB plus one text byte, idempotency, stale fence, cancellation, missing and
corrupt retained artifacts, corrupt ZIP package, encrypted/password-wrapped CFB
OOXML and 128 MiB plus one input. Add source-neutral watched-file, Gmail/IMAP
and Outlook fixture promotion evidence. Add ZIP/TAR regressions proving parent
`ArchiveExpansion`, derived-child `TextExtraction`, OOXML exclusion when OOXML
is disabled and scan suppression are unchanged.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OoxmlStructuralTextProcessorTests" --nologo
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION='Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OoxmlReplayIntegrationTests" --nologo
Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION
```

Expected: fail because the OOXML descriptor, promotion precedence, structural
reader and generic child manifest do not exist.

- [ ] **Step 3: Implement bounded OOXML replay**

Create `OoxmlStructuralTextProcessor.Capability` with the exact descriptor and
register/run it only when `OoxmlDocumentStructuralExtractEnabled` is true.
Refactor `ReadPromotionCandidatesAsync` into source-neutral matching selectors:
OOXML selects `.docx`/`.xlsx`/`.pptx`, legacy designation selects
`.doc`/`.xls`/`.ppt`, ZIP excludes OOXML extensions. The database query filters
by selector before deterministic created-time/activity-ID pagination. It keeps
the predecessor and writes a supersession relation for every successor; legacy
designation writes DeferredUnsupported evidence only and creates no branch.

Call `InspectAsync` before promotion. It uses the immutable private-root
binding/no-follow/containment lease and verifies physical/recorded length
without allocating a buffer. Promote likely OOXML by private extension after
successful inspection, including invalid/encrypted and over-limit inputs. The
claimed processor emits `office-document-input-too-large` before `ReadBytesAsync`
when inspection length exceeds 128 MiB. Otherwise read only checksum-verified
`IRetainedSourceReader` bytes before opening `ZipArchive`. Preflight all package
entries and relationships before writing a child. Use:

```csharp
var settings = new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Prohibit,
    XmlResolver = null,
    MaxCharactersInDocument = 268_435_456,
    IgnoreComments = true,
    IgnoreProcessingInstructions = true
};
```

Stream only allow-listed Word document, Spreadsheet shared-string/worksheet and
PowerPoint slide text parts. Count selected expanded bytes, XML elements,
depth and strict UTF-8 output. Split output only at Unicode scalar/UTF-8
boundaries into at most two 16 MiB retained children. Use opaque segment hashes
derived from parent stable identity, capability fingerprint and ordinal.

Refactor completion to consume `RetainedProcessorDerivedChild`; derive private
locators and origin only from this manifest. Refactor promotion to use
`AcceptedActivityKind`. Generic ZIP must reject OOXML extension candidates even
when OOXML is disabled. For legacy CFB signature plus `.doc`/`.xls`/`.ppt`
extension, retain the fixed deferred capability/reason without registration or
a branch claim.

- [ ] **Step 4: Run GREEN and commit**

```powershell
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OoxmlStructuralTextProcessorTests|FullyQualifiedName~ZipArchiveRetainedProcessorTests|FullyQualifiedName~TarArchiveRetainedProcessorTests" --nologo
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION='Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OoxmlReplayIntegrationTests|FullyQualifiedName~ZipArchiveReplayIntegrationTests|FullyQualifiedName~TarArchiveReplayIntegrationTests|FullyQualifiedName~SchemaMappingTests" --nologo
Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OoxmlStructuralTextPrivacyTests|FullyQualifiedName~SourceRootProjection|FullyQualifiedName~CorpusProjection|FullyQualifiedName~OperatorEventProjection" --nologo
scripts\dev\assert-legacy-gmail-unchanged.ps1
git diff --check
git add src tests
git commit -m "feat: add retained OOXML structural extraction"
```

Task 4A is independently accepted. Task 4B remains unimplemented and must not
start until its corrective amended design and this executable plan pass
independent review.

## Task 4B: blocked OOXML operator actions and public privacy

**Corrective controls:** The Task 4B corrective amendment in the design is
authoritative over any earlier Task 4B wording in this plan. The implementation
must use a durable action-version identity from branch ID, descriptor identity/
fingerprint and blocked branch row-version; stable client `OperationId`; and
immutable `RequestFingerprint`. It must return any existing action receipt,
including a terminal receipt, before evaluating current eligibility. It must
also reconcile force requests from database time on every hosted pass even with
all descriptors disabled. No Task 4B implementation begins until independent
design approval.

**Files:**

- Create: `src/FluxKnowledge.Application/Ports/IOoxmlOperatorActionStore.cs`
- Create: `src/FluxKnowledge.Application/Sources/OoxmlOperatorActionService.cs`
- Modify: `src/FluxKnowledge.Application/Ports/IRetainedProcessorBranchStore.cs`
- Modify: `src/FluxKnowledge.Application/Sources/RetainedProcessorActivationService.cs`
- Modify: `src/FluxKnowledge.Application/Sources/RetainedProcessorActivationHostedService.cs`
- Modify: `src/FluxKnowledge.Domain/Sources/RetainedProcessorBranch.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceProcessorForceRequestEntity.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlOoxmlOperatorActionStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedProcessorBranchStore.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260814090000_AddSourceProcessorForceRequests.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260814090000_AddSourceProcessorForceRequests.Designer.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs`
- Create: `src/FluxKnowledge.Web/Components/Pages/OperatorActions.razor`
- Create: `src/FluxKnowledge.Web/Components/OperatorActions/OoxmlOperatorActionPageState.cs`
- Modify: `src/FluxKnowledge.Web/Components/Layout/NavMenu.razor`
- Create: `src/FluxKnowledge.Web/Endpoints/OperatorActionEndpoints.cs`
- Modify: `src/FluxKnowledge.Web/Program.cs`
- Modify: `src/FluxKnowledge.Web/OutlookOperatorLoopbackGate.cs`
- Create: `tests/FluxKnowledge.Integration.Tests/Sources/OoxmlOperatorActionIntegrationTests.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Components/OoxmlOperatorActionPrivacyTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Components/OutlookPageStateTests.cs`
- Modify: `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`
- Create: `tests/FluxKnowledge.Domain.Tests/Sources/RetainedProcessorActivationServiceTests.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Endpoints/OperatorActionEndpointsTests.cs`
- Create: `tests/FluxKnowledge.Web.Tests/Browser/OoxmlOperatorActionsBrowserTests.cs`

**Consumes:** Task 4A branch outcomes and the existing sanitised status-event
feed.

**Produces:** public-safe Operator actions and idempotent force-bounded attempts
for actual OOXML blocks only.

- [ ] **Step 1: Write the failing lifecycle, authority, schema and transport tests**

```csharp
[NativeSqlServerFact]
public async Task Existing_terminal_action_and_matching_operation_replay_the_original_receipt_without_side_effects()
{
    var operationId = Guid.NewGuid();
    var first = await actions.RequestForceAttemptAsync(
        blocked.ActionId, operationId, blocked.RequestFingerprint, blocked.RowVersionToken, CancellationToken.None);
    var claim = await environment.ClaimForceAttemptAsync(first);
    Assert.Equal(blocked.LeaseGeneration + 1, claim.LeaseGeneration);
    Assert.Equal("office-document-input-too-large", await environment.FailForceAttemptAsync(claim));

    var replay = await actions.RequestForceAttemptAsync(
        blocked.ActionId, operationId, blocked.RequestFingerprint, blocked.RowVersionToken, CancellationToken.None);
    Assert.Equal(first.RequestId, replay.RequestId);
    Assert.Equal(0, await environment.ReadNewForceAuditOrStatusCountAsync());
}

[Fact]
public async Task Operator_actions_rest_and_status_payloads_exclude_private_sentinels()
{
    var payloads = await environment.ReadOperatorActionRestAndStatusPayloadsAsync();
    Assert.All(payloads, payload =>
    {
        Assert.DoesNotContain("C:\\private-spool-sentinel", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential-document-sentinel", payload, StringComparison.Ordinal);
    });
}

[Fact]
public async Task Legacy_parser_unavailable_is_not_listed_or_forceable()
{
    Assert.Empty(await environment.ReadOperatorActionsAsync("legacy-office-binary-parser-unavailable"));
    await Assert.ThrowsAsync<InvalidOperationException>(() => environment.RequestLegacyForceAttemptAsync());
}
```

Add focused tests before implementation for all of the following.

- The public action comes only from a `Blocked` OOXML branch whose final
  `SourceProcessorAttempt` has a non-null finish time and the same lease
  generation as the branch; an old block, member reason, activity reason or a
  branch without a final attempt is neither listed nor forceable.
- The eligibility matrix is exact: all nine accepted OOXML outcomes list and
  force; missing/path/checksum outcomes, descriptor-disabled/mismatched rows,
  pending/running/completed/cancelled/superseded rows, ZIP/TAR rows and legacy
  `DeferredUnsupported` rows are absent and immutable. An open request is
  listed only as a non-forceable public request state.
- `ActionId` is derived from branch ID, descriptor ID/fingerprint and the
  blocked row-version. It is globally unique and stable only for that blocked
  version. A reblocked forced generation and a pre-claim expired request that
  restores `Blocked` each have a new row-version and ActionId.
- Exact same `OperationId` and immutable request fingerprint replay the
  original receipt. Reusing that operation ID with another fingerprint, action
  or row-version fails closed with `operator-operation-conflict`. A distinct
  operation racing the same current action-version returns the single index
  winner. Existing action lookup runs before current-branch validation, so an
  open **or terminal** request returns HTTP 200 with no new audit/status; it
  must never throw a terminal replay exception.
- The optimistic precondition is the GET action ID plus its opaque expected
  blocked row-version token. Changed revision/hash, descriptor, generation,
  row-version or branch state without an existing durable receipt returns
  `operator-action-stale` with no request, branch, audit or status mutation.
- Request state transitions cover requested claim, completion, repeat block,
  transient retry, pre-claim expiry to a newly versioned block, claimed lease
  expiry/reclaim, cancellation/supersession before and after claim, descriptor
  disable before and after claim, and stale-operation rejection. Assert that a
  request binds to exactly one `(BranchId, LeaseGeneration)` attempt; a
  transient retry or reclaimed normal attempt never reuses that request.
- Domain/hosted-service tests prove `ReconcileForceRequestsAsync` runs at the
  start of every hosted pass before registration or descriptor work, including
  when OOXML/ZIP/TAR options are all disabled. It uses database time; it neither
  reads retained bytes nor promotes/claims ordinary work. Pending requested
  force work is excluded from ordinary claims, and cancelled/superseded
  activities are excluded from listing and every claim.
- Native SQL tests assert `SERIALIZABLE`, `UPDLOCK, HOLDLOCK`,
  `SYSUTCDATETIME()` current-lease/expiry predicates, exact branch owner and
  generation fences, action/operation global uniqueness, attempt composite-FK
  association and post-commit-only one-refresh visibility.
- The generated disposable migration creates every named column with its exact
  type/nullability, restrictive FKs including the nullable composite
  `(ForceAttemptBranchId, ForceAttemptLeaseGeneration)` FK, state/reason/
  timestamp checks, immutable identity fields, global ActionId/OperationId
  indexes, the additional filtered current-open-action index and reconciliation
  index. The designer/snapshot match. Upgrade a database at the predecessor
  migration and prove no backfill or historical force request is created.
- REST/UI tests require direct loopback, reject forwarded/proxy headers,
  cross-origin/missing-antiforgery posts and non-loopback peers, accept a
  same-origin antiforgery-protected loopback post, and prove anonymous authority
  records only `anonymous-direct-loopback`. Browser coverage checks the
  antiforgery form/circuit path and disabled open-request control.
- DTO, error, audit, REST, status-feed, SignalR and browser DOM sentinel tests
  exclude private paths, raw structural text, hashes, source identities,
  mailbox/spool details and exception diagnostics. MCP/CLI contract tests prove
  no force/mutation route is added.

The concrete Task 4B matrix includes these named cases and exact expectations:

| Case | Required assertion |
| --- | --- |
| `Terminal_action_replay_returns_original_receipt` | POSTing the old action after a forced block, completion, transient retry, cancellation or expiry returns 200/original receipt; no branch, attempt, audit or refresh changes. |
| `Operation_id_collision_fails_closed` | Same operation ID with a changed action, row-version token or fingerprint returns 409 `operator-operation-conflict`; no durable mutation. |
| `Preclaim_expiry_creates_new_action_version` | Database-time expiry changes `Pending` to `Blocked`, persists `force-request-claim-expired`, changes branch row-version and exposes a new ActionId; old ActionId replays its expired receipt. |
| `Concurrent_distinct_operations_share_one_current_receipt` | Two operations for one current action leave one request/audit/refresh and both obtain that request; global ActionId/OperationId indexes hold. |
| `Unknown_stale_action_returns_409_without_side_effect` | Changed branch version, descriptor, final attempt, binding or state without a durable action receipt returns `operator-action-stale` with zero request/attempt/audit/refresh additions. |
| `Reconciliation_runs_when_every_descriptor_disabled` | Each hosted pass invokes only database-time force reconciliation before descriptor registration; requested expiry, pre/post-claim cancellation and disable-after-claim transitions still converge. |
| `Requested_force_work_cannot_be_stolen` | An ordinary claim skips a `Pending` branch with a requested force row; after forced claim it binds the exact composite attempt; after claimed expiry, a later normal claim uses a new generation. |
| `Cancelled_activity_cannot_be_reclaimed` | Before/after-claim cancellation closes the request/attempt as required and neither automatic nor force claim can reacquire the activity. |
| `Composite_force_attempt_fk_rejects_mismatch` | Disposable SQL rejects a different branch or generation and rejects partial nullable association; generated migration, Designer and snapshot are identical in model semantics. |
| `Transition_refresh_is_exactly_once_after_commit` | Every successful creation/claim/terminal/reconciliation transaction writes one fixed audit and produces one postcommit refresh; replay, conflict and rollback produce none. |

- [ ] **Step 2: Run RED**

```powershell
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION='Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OoxmlOperatorActionIntegrationTests" --nologo
Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OoxmlOperatorActionPrivacyTests" --nologo
```

Expected: fail because force requests, endpoint and page do not exist.

- [ ] **Step 3: Implement the one-generation force lifecycle, then the public tab**

Implement the design's lifecycle in `IRetainedProcessorBranchStore` and
`SqlRetainedProcessorBranchStore`, with `IOoxmlOperatorActionStore` limited to
safe projection/entry. Do not add a parallel claim, retry, completion or clock
path. First resolve an existing operation, then resolve an existing action even
when terminal, before checking the live branch. Only an unknown action may lock
the exact blocked branch/final attempt and validate expected row-version,
derived action identity and immutable retained binding. A winning creation
inserts `Requested`, appends one fixed sanitised audit event and moves only that
branch `Blocked` → `Pending`; replay, operation conflict and stale rejection
write neither audit nor status. Claim binds the request to exactly one new
`(BranchId, LeaseGeneration)` attempt in the same transaction. Commit, fail,
transient retry, expiry/reclaim and cancellation update the exact request and
attempt only through existing owner/generation/database-current-lease fences.
Never reread an original source or relax a limit/checksum/private-root failure.

Implement `ReconcileForceRequestsAsync` and call it at the very beginning of
every activation/hosted pass regardless of descriptor activation. It performs
only the design's database-time expiry, cancellation, descriptor-disable and
claimed-lease-reclaim transitions, with each request/branch/attempt/audit
transition atomic. Exclude open requested work from ordinary claims and exclude
cancelled/superseded activities from ordinary claims. Emit one fixed public
status refresh only after each committed transition.

Persist the additive force entity with the exact action ID, operation ID,
request fingerprint, blocked row-version/action-version snapshot, server-UTC
timestamps, nullable composite force-attempt association, bounded receipt/reason
and rowversion specified in the design. Use existing hash, immutable,
collation, FK and delete conventions. Generate the migration, Designer and
snapshot together; no data migration, deployment or non-disposable database
update is authorised.

Map the GET and POST endpoints using the corrective DTO/error vocabulary,
including `operationId`, immutable request fingerprint and opaque expected
blocked row-version. Expand
the existing loopback gate rather than creating a bypass: protect the page,
endpoint and Blazor circuit, reject proxy headers, require same-origin and
antiforgery on POST, and use only a sanitised anonymous direct-loopback actor.
The tab renders opaque IDs, capability, public state/reason/timestamps and the
bounded-force control only. Publish public status/SignalR evidence after the
database transaction commits; expose no mutation in MCP or CLI.

- [ ] **Step 4: Run GREEN, commit and review**

```powershell
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
$env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION='Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OoxmlOperatorActionIntegrationTests|FullyQualifiedName~OoxmlReplayIntegrationTests" --nologo
Remove-Item Env:\FLUXKNOWLEDGE_TEST_SQL_CONNECTION
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OoxmlOperatorActionPrivacyTests|FullyQualifiedName~OperatorActionEndpointsTests|FullyQualifiedName~OoxmlOperatorActionsBrowserTests|FullyQualifiedName~WebHostCompositionTests|FullyQualifiedName~OutlookPageStateTests|FullyQualifiedName~SourceRootProjection|FullyQualifiedName~CorpusProjection|FullyQualifiedName~OperatorEventProjection" --nologo
scripts\dev\assert-legacy-gmail-unchanged.ps1
git diff --check
git add src tests docs
git commit -m "feat: add fenced OOXML document operator actions"
```

Obtain independent Task 4B approval before the next processor family.

## Future legacy Office binary capability

`.doc`, `.xls` and `.ppt` remain durable `DeferredUnsupported` work with required
capability `document-office-legacy-structural-extract` and reason
`legacy-office-binary-parser-unavailable`. They are outside Tasks 4A/4B,
require no operator action and must not be manually forceable. When the user
supplies an acceptable library, write and independently review a new amendment
that inventories and pins it before creating a legacy processor task.

## Plan self-review

- Coverage: active OOXML formats, durable legacy deferral, exact descriptor,
  ZIP precedence, branch provenance, bounded reasons, generation-bound
  blocked-only actions, request/attempt lifecycle, serialisable SQL fences,
  loopback/antiforgery/same-origin authority, public DTO/audit/status privacy
  and Phase 6 exclusions all have tasks. Task 4B additionally covers durable
  ActionId/OperationId/fingerprint idempotency, terminal replay, optimistic
  blocked-version fencing, unconditional database-time reconciliation, force
  isolation, exact schema checks and one-refresh postcommit observability.
- Migration: only Task 4B adds an additive force-request table plus its
  Designer/snapshot, verified in a generated disposable SQL database and an
  upgrade/no-backfill test only. It includes global ActionId/OperationId
  uniqueness and the composite force-attempt branch/generation FK.
- Legacy scope: no task or action silently claims to extract `.doc`, `.xls` or
  `.ppt` without the later library amendment.
- Approval gate: Task 4B is plan-amended and pending independent design
  approval; this plan grants no implementation authority.
