# Phase 4 native Outlook ingress implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Deliver an Outlook-only, read-only classic Outlook COM source path that exports complete messages and attachments to a private spool, persists source/deferred-work evidence, and is configured through the native local operator UI.

**Architecture:** A dedicated FluxKnowledge.OutlookHost executable is the sole classic-Outlook COM client and runs in the logged-in Windows user's session. COM events create durable wake hints; an overlap-safe last-modification-time cursor catch-up is authoritative for new and moved mail. A committed export is bridged into the existing source revision, artifact and deferred-activity model, so unsupported Outlook attachments and ordinary watched files share later processor replay semantics.

**Tech Stack:** .NET 10/C#, SQL Server/EF Core migrations, classic Outlook COM interop in an STA Windows executable, Blazor Interactive Server, xUnit, disposable SQL fixture.

## Global constraints

- Native Phase 4 is Outlook-only. Do not change, remove, invoke or migrate legacy Gmail code, configuration, tests, APIs or documentation.
- Support classic desktop Outlook COM only, in the logged-in Windows user session. Do not add Microsoft Graph, EWS, IMAP or New Outlook support.
- Capture is read/export-only: no move, delete, category, flag, mark-read, reply or other mailbox mutation.
- The host is separate from IIS, Docker and the Phase 2 deterministic worker. It is never a model, GPU, driver/runtime, RabbitMQ, Vespa, Docker, network-client or legacy worker.
- Raw body text, attachments, Outlook identifiers, credentials and COM diagnostics stay in ignored private spool/host paths. SQL and outward surfaces contain only sanitised metadata, hashes and private relative references.
- The local Blazor UI may configure profiles, folders and spools. Native REST, MCP and CLI remain read-only.
- COM notifications are hints only. SQL cursor, export receipt, source activity and deferred-replay state are authoritative and idempotent.
- Profiles and hosted integration default disabled. No test, deployment or validation touches a real mailbox unless later explicitly authorised for a non-production Outlook profile/folder.
- Preserve existing source/revision/artifact/activity, SQL authority and fenced executor contracts. Do not activate a model, GPU, processor, external source, legacy action or Windows Service.
- Use TDD: capture focused RED evidence before each changed invariant and fresh GREEN evidence after. Keep Release builds warning-free; never run dotnet format.

---

## Task 1: Outlook contracts and closed state model

**Files:**

- Create: src/FluxKnowledge.Domain/Outlook/OutlookCaptureProfile.cs
- Create: src/FluxKnowledge.Domain/Outlook/OutlookCaptureFolder.cs
- Create: src/FluxKnowledge.Domain/Outlook/OutlookCaptureExport.cs
- Create: src/FluxKnowledge.Domain/Outlook/OutlookCaptureState.cs
- Create: src/FluxKnowledge.Application/Contracts/OutlookCaptureContracts.cs
- Create: src/FluxKnowledge.Application/Ports/IOutlookCaptureStore.cs
- Test: tests/FluxKnowledge.Domain.Tests/Outlook/OutlookCaptureContractTests.cs

**Consumes:** SourceActivity, SourceArtifact, SourceRevision, ExecutionClass and existing domain value-object validation.

**Produces:**

~~~csharp
public sealed record OutlookCaptureProfileId(Guid Value);
public sealed record OutlookCaptureFolderId(Guid Value);
public sealed record OutlookCaptureExportId(Guid Value);

public enum OutlookIncrementalBasis { LastModificationTime, ReceivedTime }
public enum OutlookCaptureState { Disabled, AwaitingHost, CatchUpPending, CatchingUp, Ready, Blocked, Stale }
public enum OutlookExportState { Inflight, ReadyForIngestion, Ingested, Deferred, Blocked }

public sealed record OutlookFolderIdentity(string StoreId, string FolderEntryId, string DisplayName);
public sealed record OutlookExportIdentity(Guid ProfileId, Guid FolderId, string EntryId, string SourceFingerprint);
~~~

- [ ] **Step 1: Write the failing contract tests.**

~~~csharp
[Fact]
public void Folder_identity_rejects_blank_store_or_entry_ids() =>
    Assert.Throws<ArgumentException>(() => new OutlookFolderIdentity("", "folder", "Capture"));

[Fact]
public void Profile_defaults_to_disabled_and_last_modification_time() =>
    Assert.Equal(OutlookCaptureState.Disabled, OutlookCaptureProfile.Create("Inbox capture").State);

[Fact]
public void Export_identity_cannot_be_rebound_to_a_different_fingerprint()
{
    // Create first identity, then assert conflicting same profile/folder/EntryId is rejected.
}
~~~

- [ ] **Step 2: Run RED evidence.**

Run: dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"

Expected: FAIL because Outlook domain/application types do not exist.

- [ ] **Step 3: Implement the smallest closed contract.**

Implement immutable profile/folder/export records, strict nonblank/length/canonical validation, disabled defaults and the store below. Every mutating store command takes a stable operation ID plus request fingerprint and returns Accepted, Committed and IsReplay.

~~~csharp
public interface IOutlookCaptureStore
{
    ValueTask<OutlookOperationReceipt> SaveProfileAsync(OutlookProfileSaveRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookOperationReceipt> RecordHintAsync(OutlookHintRequest request, CancellationToken cancellationToken);
    ValueTask<OutlookCatchUpClaim?> ClaimCatchUpAsync(OutlookHostIdentity host, CancellationToken cancellationToken);
    ValueTask<OutlookExportCommitReceipt> CommitExportAsync(OutlookExportCommitRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<OutlookProfileProjection>> ReadLocalProjectionAsync(CancellationToken cancellationToken);
}
~~~

Do not put message text, attachment bytes, credentials, raw COM exceptions or Outlook identifiers in UI projection records.

- [ ] **Step 4: Run GREEN evidence.**

Run: dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureContractTests"

Expected: PASS.

- [ ] **Step 5: Commit.**

~~~powershell
git add src/FluxKnowledge.Domain/Outlook src/FluxKnowledge.Application/Contracts/OutlookCaptureContracts.cs src/FluxKnowledge.Application/Ports/IOutlookCaptureStore.cs tests/FluxKnowledge.Domain.Tests/Outlook
git commit -m "feat: define Outlook capture contracts"
~~~

## Task 2: SQL-authoritative profile, folder, operation and export evidence

**Files:**

- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/OutlookCaptureProfileEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/OutlookCaptureFolderEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/OutlookCaptureOperationEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/OutlookCaptureExportEntity.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlOutlookCaptureStore.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs
- Create: migration AddNativeOutlookIngress and its generated designer under src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs
- Test: tests/FluxKnowledge.Integration.Tests/Outlook/SqlOutlookCaptureStoreTests.cs
- Test: tests/FluxKnowledge.Integration.Tests/Persistence/OutlookSchemaMappingTests.cs

**Consumes:** Task 1 contracts; current EF configuration, transaction, operation receipt and OperatorEventAppender patterns.

**Produces:** Private SQL tables:

~~~text
OutlookCaptureProfiles   (Id, DisplayName, SpoolRoot, IsEnabled, State, RowVersion, timestamps)
OutlookCaptureFolders    (Id, ProfileId, StoreId, FolderEntryId, DisplayName, Basis, CursorUtc, CursorFingerprint, State)
OutlookCaptureOperations (Id, ProfileId, Kind, OperationId, RequestFingerprint, CompletedAtUtc)
OutlookCaptureExports    (Id, ProfileId, FolderId, EntryId, SourceFingerprint, ManifestHash, RelativeSpoolPath, State, SourceRevisionId)
~~~

- [ ] **Step 1: Write failing disposable-SQL tests.**

~~~csharp
[Fact]
public async Task Save_profile_replays_matching_operation_and_rejects_divergence() { }

[Fact]
public async Task Cursor_advances_only_after_committed_export() { }

[Fact]
public async Task Replayed_entry_id_creates_no_second_export_or_event() { }

[Fact]
public async Task Schema_excludes_raw_mail_attachment_and_credential_columns() { }
~~~

- [ ] **Step 2: Run RED evidence.**

Run: set FLUXKNOWLEDGE_TEST_SQL_CONNECTION to the disposable server connection, then run:

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SqlOutlookCaptureStoreTests|FullyQualifiedName~OutlookSchemaMappingTests"
~~~

Expected: FAIL because the store and migration are absent.

- [ ] **Step 3: Implement schema and replay-safe store.**

Use unique constraints for profile operation replay, canonical folder identity and canonical observed EntryID. Same operation/fingerprint replays; the same operation with a different fingerprint fails closed. Store canonical COM identifiers and relative spool paths only in private columns. Sanitize audit events to local identifiers, counts and fixed reason codes; never append StoreId, EntryID, path, content or raw exception. A failed/pending export cannot update CursorUtc.

Generate the migration with dotnet ef migrations add AddNativeOutlookIngress. Keep the generated designer and snapshot aligned; do not hand-edit a divergent snapshot.

- [ ] **Step 4: Run GREEN evidence.**

Run the Step 2 command.

Expected: PASS against generated disposable catalogues.

- [ ] **Step 5: Verify the model and commit.**

~~~powershell
dotnet ef migrations has-pending-model-changes --project src/FluxKnowledge.Infrastructure.SqlServer --startup-project src/FluxKnowledge.Infrastructure.SqlServer --no-build
git add src/FluxKnowledge.Infrastructure.SqlServer tests/FluxKnowledge.Integration.Tests/Outlook tests/FluxKnowledge.Integration.Tests/Persistence/OutlookSchemaMappingTests.cs
git commit -m "feat: persist Outlook capture evidence"
~~~

Expected: EF reports no pending changes.

## Task 3: Complete private exports and shared deferred source work

**Files:**

- Create: src/FluxKnowledge.Application/Outlook/OutlookCaptureService.cs
- Create: src/FluxKnowledge.Application/Outlook/OutlookCatchUpCoordinator.cs
- Create: src/FluxKnowledge.Application/Outlook/OutlookExportIngestionService.cs
- Create: src/FluxKnowledge.Integrations/Outlook/OutlookSpoolLayout.cs
- Create: src/FluxKnowledge.Integrations/Outlook/OutlookExportManifest.cs
- Create: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityWriter.cs
- Modify: src/FluxKnowledge.Application/Ports/ISourceActivityStore.cs
- Modify: src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs
- Test: tests/FluxKnowledge.Integration.Tests/Outlook/OutlookExportIngestionTests.cs
- Test: tests/FluxKnowledge.Integration.Tests/Sources/DeferredActivityReplayTests.cs

**Consumes:** Task 2 committed receipts; existing source revision/artifact/activity/replay contracts.

**Produces:** One SQL-authoritative transaction from an already-promoted,
verified ready export directory to parent message/child attachment source
revisions, content-addressed private artifacts and normal/deferred activities.
Filesystem promotion is not part of that transaction.

- [ ] **Step 1: Write failing full-export/deferred-capability tests.**

~~~csharp
[Fact]
public async Task Complete_message_and_two_attachments_create_parent_child_revisions_and_private_artifacts() { }

[Fact]
public async Task Unsupported_attachment_is_deferred_not_failed_and_replays_after_matching_processor_is_enabled() { }

[Fact]
public async Task Missing_manifest_file_or_hash_mismatch_blocks_without_advancing_cursor() { }
~~~

- [ ] **Step 2: Run RED evidence.**

Run with the already-approved process-scoped disposable SQL connection in FLUXKNOWLEDGE_TEST_SQL_CONNECTION:

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~OutlookExportIngestionTests|FullyQualifiedName~DeferredActivityReplayTests"
~~~

Expected: FAIL because Outlook export cannot enter the source/activity model.

- [ ] **Step 3: Implement private spool validation and source bridge.**

OutlookSpoolLayout owns the exact _inflight/export-id then ready/export-id sequence. It rejects absolute paths, traversal, reparse traversal, missing manifest files and checksum mismatch; it hashes all content before atomic directory promotion.

Replace Task 2's independently cursor-mutating `CommitExportAsync` path with
an internal receipt operation callable only by the ready-export ingestion
transaction. OutlookExportIngestionService validates the ready manifest then
commits one SQL transaction:

1. commit/replay the Outlook export receipt;
2. create one parent message source revision and child body/attachment revisions;
3. create content-addressed private artifacts;
4. create SourceActivityDraft for supported content or DeferredUnsupported with the required capability;
5. record bounded conflict/blocked evidence where required; and
6. advance the folder cursor only after every prior state is committed.

The ingestion transaction exclusively owns Outlook receipt and cursor mutation.
A SQL failure leaves the cursor unchanged and the ready directory recoverable
for idempotent retry; do not claim filesystem promotion and SQL are one
transaction. Add an immutable private profile-to-source-root provenance binding
only if SourceRevision requires `SourceRootId`; otherwise reuse the established
canonical source identity. Never infer that binding from display names.

Extract SqlSourceActivityWriter from SqlSourceActivityStore and use it from both stores, so the export bridge reuses existing activity idempotency and sanitised event rules. Future processors use DeferredActivityReplayService against retained artifacts only, never Outlook or a watched original file.

- [ ] **Step 4: Run GREEN evidence.**

Run the Step 2 command.

Expected: PASS; parent/child provenance, complete-only visibility, deferred retention and replay pass.

- [ ] **Step 5: Commit.**

~~~powershell
git add src/FluxKnowledge.Application/Outlook src/FluxKnowledge.Application/Ports/ISourceActivityStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityWriter.cs src/FluxKnowledge.Integrations/Outlook tests/FluxKnowledge.Integration.Tests/Outlook tests/FluxKnowledge.Integration.Tests/Sources/DeferredActivityReplayTests.cs
git commit -m "feat: retain Outlook exports as deferred source work"
~~~

## Task 4: Isolated default-disabled classic Outlook COM host

**Files:**

- Create: src/FluxKnowledge.OutlookHost/FluxKnowledge.OutlookHost.csproj
- Create: src/FluxKnowledge.OutlookHost/Program.cs
- Create: src/FluxKnowledge.OutlookHost/OutlookHostOptions.cs
- Create: src/FluxKnowledge.OutlookHost/ClassicOutlookComAdapter.cs
- Create: src/FluxKnowledge.OutlookHost/OutlookHostLoop.cs
- Create: src/FluxKnowledge.OutlookHost/OutlookFolderBrowser.cs
- Modify: FluxKnowledge.slnx
- Create: tests/FluxKnowledge.OutlookHost.Tests/FluxKnowledge.OutlookHost.Tests.csproj
- Create: tests/FluxKnowledge.OutlookHost.Tests/FakeClassicOutlookAdapter.cs
- Create: tests/FluxKnowledge.OutlookHost.Tests/OutlookHostLoopTests.cs

**Consumes:** Tasks 1–3.

**Produces:** A net10.0-windows STA executable that accepts only app-owned host/control arguments, runs one instance per interactive user/session and accesses Outlook through a deliberately read-only adapter.

- [ ] **Step 1: Write failing fake-COM tests.**

~~~csharp
[Fact]
public async Task Item_notification_records_one_hint_and_never_exports_inside_the_callback() { }

[Fact]
public async Task Last_modification_overlap_captures_an_older_item_moved_into_the_folder() { }

[Fact]
public async Task Host_never_calls_a_mailbox_mutation_member() { }

[Fact]
public async Task Restart_after_hint_loss_replays_catch_up_without_duplicate_export() { }
~~~

- [ ] **Step 2: Run RED evidence.**

Run: dotnet test tests/FluxKnowledge.OutlookHost.Tests/FluxKnowledge.OutlookHost.Tests.csproj --filter "FullyQualifiedName~OutlookHostLoopTests"

Expected: FAIL because the host project does not exist.

- [ ] **Step 3: Implement the narrow host/COM seam.**

Target net10.0-windows, set STAThread, and add the smallest locked classic-Outlook interop dependency supported by the local SDK. Do not put a mailbox address, profile, folder, credential or raw diagnostics in arguments, environment variables or configuration.

~~~csharp
internal interface IClassicOutlookAdapter : IAsyncDisposable
{
    ValueTask<IReadOnlyList<OutlookFolderDescriptor>> BrowseFoldersAsync(CancellationToken cancellationToken);
    ValueTask<IAsyncDisposable> SubscribeHintsAsync(OutlookFolderIdentity folder, Func<OutlookHint, ValueTask> onHint, CancellationToken cancellationToken);
    IAsyncEnumerable<OutlookItemEnvelope> EnumerateAsync(OutlookFolderIdentity folder, OutlookCursor cursor, CancellationToken cancellationToken);
    ValueTask<OutlookMessagePayload> ReadForExportAsync(OutlookItemEnvelope item, CancellationToken cancellationToken);
}
~~~

Do not give the adapter mutation members. The callback writes only a stable coalesced metadata hint. OutlookHostLoop claims durable catch-up work, enumerates by configured basis with overlap, deduplicates by EntryID and invokes Task 3's export bridge. Map not-Windows, no interactive session, missing COM dependency, unavailable Outlook, folder denial and stale lease to bounded reason codes; write raw COM detail only to ignored host logs.

- [ ] **Step 4: Run GREEN evidence and mutation scan.**

~~~powershell
dotnet test tests/FluxKnowledge.OutlookHost.Tests/FluxKnowledge.OutlookHost.Tests.csproj --filter "FullyQualifiedName~OutlookHostLoopTests"
rg -n -i "Move\(|Delete\(|Categories|UnRead|Flag|Reply" src/FluxKnowledge.OutlookHost
dotnet build src/FluxKnowledge.OutlookHost/FluxKnowledge.OutlookHost.csproj -c Release -warnaserror
~~~

Expected: tests PASS; forbidden mutation scan has no production call; build has zero warnings/errors.

- [ ] **Step 5: Commit.**

~~~powershell
git add FluxKnowledge.slnx src/FluxKnowledge.OutlookHost tests/FluxKnowledge.OutlookHost.Tests
git commit -m "feat: add read-only Outlook COM host"
~~~

## Task 5: Local UI profile/folder/spool configuration and safe projections

**Files:**

- Create: src/FluxKnowledge.Web/Components/Outlook/OutlookPageState.cs
- Create: src/FluxKnowledge.Web/Components/Outlook/SqlOutlookProjectionReader.cs
- Create: src/FluxKnowledge.Web/Components/Pages/Outlook.razor
- Modify: src/FluxKnowledge.Web/Components/Layout/NavMenu.razor
- Modify: src/FluxKnowledge.Web/WebHostComposition.cs
- Modify: src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs
- Test: tests/FluxKnowledge.Web.Tests/Components/OutlookPageStateTests.cs
- Test: tests/FluxKnowledge.Web.Tests/Components/OutlookProjectionReaderIntegrationTests.cs
- Test: tests/FluxKnowledge.Web.Tests/Browser/NativeOutlookConfigurationBrowserTests.cs
- Test: tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs

**Consumes:** Task 2 local projections/operations and Task 4 host-mediated browsing.

**Produces:** A local /outlook page that creates/edits/pauses native Outlook profiles, selects canonical folders through an available host, displays configured folder/spool health and records a durable manual catch-up.

- [ ] **Step 1: Write failing page-state and safe-projection tests.**

~~~csharp
[Fact]
public async Task Page_rejects_save_when_the_folder_browse_result_is_stale() { }

[Fact]
public async Task Projection_shows_folder_and_spool_status_but_not_entry_ids_or_raw_diagnostics() { }

[Fact]
public void Native_outlook_adds_no_rest_mcp_or_cli_mutation_surface() { }
~~~

- [ ] **Step 2: Run RED evidence.**

Run: dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~OutlookPageStateTests|FullyQualifiedName~OutlookProjectionReaderIntegrationTests|FullyQualifiedName~NativeOutlookConfigurationBrowserTests"

Expected: FAIL because the page/state/projection does not exist.

- [ ] **Step 3: Implement the local operator flow.**

Follow SourceRootPageState semantics: a configuration change invalidates the prior browse result; save requires a current result. Browse folders creates a durable host-mediated request and accepts only bounded folder display metadata plus canonical identity. Catch up records durable work; it never starts a process or calls COM in the Web host. Enabling a profile makes it eligible for a later host claim, not an immediate source connection.

Display profile/folder display name, configured spool location, capacity/health, capture state, timestamps and aggregate export/deferred/blocked counts. Do not display messages, subjects, bodies, attachment bytes, EntryID, StoreId, credentials, process IDs or raw COM diagnostics. Add no MapPost endpoint, MCP tool or CLI command; update read-only status projections only with sanitised aggregate state.

- [ ] **Step 4: Run GREEN evidence.**

Run: dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~OutlookPageStateTests|FullyQualifiedName~OutlookProjectionReaderIntegrationTests|FullyQualifiedName~NativeOutlookConfigurationBrowserTests|FullyQualifiedName~WebHostCompositionTests"

Expected: PASS; UI-only configuration works, safe fields render and disabled composition registers no COM host.

- [ ] **Step 5: Commit.**

~~~powershell
git add src/FluxKnowledge.Web tests/FluxKnowledge.Web.Tests
git commit -m "feat: configure Outlook capture in the local UI"
~~~

## Task 6: Disabled hosting, recovery and deployment contract

**Files:**

- Create: src/FluxKnowledge.Integrations/Outlook/OutlookCaptureRecoveryService.cs
- Modify: src/FluxKnowledge.Web/appsettings.json
- Modify: src/FluxKnowledge.Web/WebHostComposition.cs
- Modify: scripts/deploy/update-native-windows.ps1
- Create: scripts/deploy/validate-native-outlook-ingress.ps1
- Modify: scripts/dev/complete-feature.ps1
- Modify: tests/native/native-deployment-plan.ps1
- Modify: tests/native/complete-feature-dryrun.ps1
- Test: tests/FluxKnowledge.Integration.Tests/Outlook/OutlookCaptureRecoveryServiceTests.cs
- Test: tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs

**Consumes:** Tasks 1–5.

**Produces:** Disabled-by-default recovery coordination, deployment migration target and sanitised post-deploy validator. It does not install a Windows Service or start the interactive COM host.

- [ ] **Step 1: Write failing recovery/composition/deployment tests.**

~~~csharp
[Fact]
public async Task Restart_releases_only_stale_catch_up_leases_and_replays_pending_hints() { }

[Fact]
public void Disabled_options_register_no_com_host_or_external_capture_service() { }
~~~

~~~powershell
Assert-Contains $scriptText "AddNativeOutlookIngress"
Assert-Contains $scriptText "validate-native-outlook-ingress.ps1"
~~~

- [ ] **Step 2: Run RED evidence.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureRecoveryServiceTests"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~WebHostCompositionTests"
pwsh -NoProfile -File tests/native/native-deployment-plan.ps1
~~~

Expected: FAIL because options, recovery and deployment target/validator are absent.

- [ ] **Step 3: Implement the safe default.**

Add OutlookCapture:Enabled=false, bounded debounce/cadence and stale-lease defaults. The recovery service may reconcile durable hints/leases only when explicitly enabled; it cannot create a host, access COM, advance a cursor or activate a deferred processor.

Update the native migration target to the latest generated Outlook migration,
including `AddNativeOutlookIngress` and every later Outlook hardening migration
required by the compiled model. The validator checks that
`AddNativeOutlookIngress` is present as the base migration and that the
database reaches the compiled-model migration head, alongside loopback
health/readiness/status, disabled Outlook configuration and private schema
policy only. Its record contains timestamps, loopback status codes, migration
IDs, enabled state, aggregate counts and policy result—not folder names,
spools, identifiers, content, credentials or diagnostics. Retain the
repository-owned post-deploy validator/record/commit/push ordering in
complete-feature.ps1.

- [ ] **Step 4: Run GREEN evidence.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~OutlookCaptureRecoveryServiceTests"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~WebHostCompositionTests"
pwsh -NoProfile -File tests/native/native-deployment-plan.ps1
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1
~~~

Expected: PASS; recovery is inactive by default and deployment ordering includes the validator.

- [ ] **Step 5: Commit.**

~~~powershell
git add src/FluxKnowledge.Integrations/Outlook src/FluxKnowledge.Web/appsettings.json src/FluxKnowledge.Web/WebHostComposition.cs scripts/deploy scripts/dev tests/native tests/FluxKnowledge.Integration.Tests/Outlook tests/FluxKnowledge.Web.Tests/Composition
git commit -m "build: validate disabled Outlook ingress deployment"
~~~

## Task 7: Whole-branch verification and operational handoff

**Files:**

- Modify: docs/roadmap.md
- Modify: docs/architecture.md
- Create: docs/operations/native-windows-phase-4-outlook-ingress-validation.md only after fresh post-deploy evidence
- Create: .superpowers/sdd/2026-08-11-phase-4-native-outlook-ingress-implementation/task-1-report.md through task-7-report.md as ignored working evidence

**Consumes:** Tasks 1–6 and the approved design.

**Produces:** Evidence-backed delivery or an explicit external-access blocker. It does not connect to Outlook without later written non-production source approval.

- [ ] **Step 1: Run full offline/disposable verification.**

~~~powershell
dotnet restore FluxKnowledge.slnx --locked-mode
dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror
dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"
pwsh -NoProfile -File tests/native/complete-feature-dryrun.ps1
pwsh -NoProfile -File tests/native/native-deployment-plan.ps1
git diff --check
~~~

Expected: all tests pass, Release has zero warnings/errors and only explicit browser/disposable-fixture skips are reported.

- [ ] **Step 2: Obtain independent milestone and whole-branch review.**

Give reviewers the approved design, this plan, task reports, exact changed-file list, test output and diff. Require review of COM isolation, absence of mailbox mutation, event/cursor/export replay, private content containment, deferred replay, UI-only configuration, legacy Gmail preservation, migration and deployment order. Fix each Critical/Important finding with focused RED/GREEN evidence and scoped re-review.

- [ ] **Step 3: Update documentation from fresh evidence only.**

Record implemented capability, disabled-default state, verification and remaining approval in docs/roadmap.md and docs/architecture.md. Do not create the validation record before actual post-deployment evidence. Keep raw mail, folder identities, spool contents, credentials and diagnostics out of public docs.

- [ ] **Step 4: Stop at the operational approval gate or use the repository closeout script.**

Without explicit non-production Outlook profile/folder and local-deployment approval, report the external-source blocker after offline verification. With that approval, use only:

~~~powershell
pwsh -NoProfile -File scripts/dev/complete-feature.ps1 -FeatureWorktree 'E:\LLM KB\.worktrees\phase-4-native-outlook-ingress' -ApplyMigrations -ConfirmApplyMigrations
~~~

Do not manually substitute its merge/deploy/validation sequence. Report its current-turn JSON and, on failure, only failed_step and log_path.

- [ ] **Step 5: Commit evidence or complete through the repository script.**

~~~powershell
git add docs/roadmap.md docs/architecture.md .superpowers/sdd
git commit -m "docs: record Outlook ingress evidence"
~~~

Use complete-feature.ps1 for merge, deployment, validation record, push and cleanup when fully authorised.

## Independent plan-review corrections (2026-08-11)

These corrections are binding additions to Tasks 1–7. They resolve the
independent review findings without widening the approved Outlook-only scope.

1. **Durable host-control plane (Tasks 1, 2, 4 and 5).** Add closed contracts,
   private SQL entities and store operations for browse requests/results and
   catch-up work. Each browse request has a correlation ID, configuration
   revision, expiry, fenced host claim and bounded completion/failure result.
   Each catch-up has profile scope, coalescing key, manual/schedule/hint
   provenance, retry count/reason, lease owner/expiry/heartbeat and fencing
   token. Add request, claim, renew, complete, fail/requeue and stale-release
   store operations. The Web host communicates only through these SQL records;
   it cannot reference COM or accept stale/unsolicited results. Test duplicate
   hint coalescing, disabled-profile rejection, stale takeover, non-stale lease
   protection, fenced browse completion and stale browse-result rejection.

2. **COM isolation and fail-closed activation (Task 4 and Task 6).** Add a
   gated COM factory that checks Windows, the interactive signed-in user
   session, one-instance-per-session ownership and an explicitly enabled
   durable host claim *before* COM construction; bind host identity/leases to
   the Windows user and session. Add a host-side default-disabled option,
   prohibit autostart/service registration, and prove no COM activation from
   startup, deployment validation, profile save, profile enablement or any
   non-host project. Use fake COM only in offline tests. Add architecture and
   negative tests that IIS, Docker and Phase 2 worker projects cannot reference
   or construct the adapter.

3. **Local operator configuration completeness (Task 1 and Task 5).** Include
   create/edit/pause/remove profile commands, basis selection, bounded
   schedule/overlap validation, and a `received_time` warning with a manual
   reconciliation path. Save-time spool validation must check local path, ACL,
   capacity and writability. Add tests for local policy, antiforgery and
   sanitised append-only audit evidence; REST/MCP/CLI stay read-only.

4. **Deferred and export recovery invariants (Tasks 2 and 3).** Persist a
   `DeferredCapability` record with immutable provenance, artifact fingerprint
   and required capability. A future processor is eligible only after explicit
   enablement and claims with artifact fingerprint plus processor-version
   identity; it never reopens Outlook or a watched original file. Add Outlook
   attachment and watched-file tests for exactly-once replay and for missing or
   invalid retained sidecars becoming bounded blocked evidence. Also test
   ready-but-uncommitted export recovery, conservative removal only of verified
   abandoned `_inflight` directories without a receipt, and durable blocking of
   conflicting same-folder/EntryID source-fingerprint observations. In all
   cases, prove the cursor remains unchanged until complete commit.

5. **Private-data boundary (Tasks 1, 2, 3, 5, 6 and 7).** Canonical StoreId,
   FolderEntryId, EntryID and configured spool root are allowed only in
   access-restricted private SQL fields needed for reconciliation; exports keep
   only private relative sidecar paths. Explicitly test that these values, raw
   content, credentials and raw COM diagnostics cannot appear in audit details,
   local/public projections, REST/MCP/CLI, SignalR, logs or validation records.

6. **Legacy Gmail preservation gate (Task 7).** Add a closeout diff guard for
   Gmail-owned paths/configuration/APIs/tests/documentation and focused legacy
   Gmail regression tests. Any detected Gmail change stops execution pending
   separate approval. Add these checks to the Task 7 offline verification
   matrix.

## Plan self-review

- **Spec coverage:** Task 1 defines closed/read-only contracts; Task 2 SQL authority/replay; Task 3 complete export plus shared deferred retention; Task 4 isolated COM/event/catch-up; Task 5 UI configuration/safe projection; Task 6 disabled hosting/deployment safety; Task 7 verification/review/operational handoff.
- **Scope:** Gmail is preserved legacy only. No task adds Graph, IMAP, mailbox mutation, model/GPU work, processor activation, Windows Service or a real mailbox test.
- **Ambiguity resolved:** last-modification-time is the default moved-item detector; events are hints; local UI is the only native mutation surface; a later processor reads retained artifacts rather than reopening Outlook or watched originals.
- **Placeholder scan:** Every implementation task has files, interfaces, RED/GREEN commands and a commit. The SQL fixture is the already-approved process-scoped environment variable, and a future Outlook profile/folder remains a separately authorised operational input rather than a plan omission.

## Execution handoff

Plan complete and saved to docs/superpowers/plans/2026-08-11-phase-4-native-outlook-ingress-implementation.md.

Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task and review between tasks.
2. **Inline Execution** — execute task-by-task in this session with review checkpoints.
