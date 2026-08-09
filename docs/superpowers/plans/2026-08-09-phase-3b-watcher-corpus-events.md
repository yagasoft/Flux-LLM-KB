# Phase 3B watcher, corpus and events implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add restart-safe local watcher hints, a PipelineRecord-led Corpus explorer and a durable Events dashboard without weakening Phase 3A source safety or pipeline invariants.

**Architecture:** FileSystemWatcher notifications become persisted, coalesced watch state before releasing normal source scans. SQL projects PipelineRecords, SourceRevisions, SourceActivities, Jobs, indexed artefacts and extended AuditEvents into server-paged Corpus and Events read models. Blazor re-reads SQL after reconnect; StatusEventFeed remains a refresh hint only.

**Tech Stack:** .NET 10, nullable C#, ASP.NET Core Interactive Server/Blazor, EF Core SQL Server migrations, SQL Server Full-Text search, FileSystemWatcher, xUnit and the native SQL/browser harness.

## Global constraints

- SQL is authoritative; USearch is derived and never the Corpus or Events authority.
- PipelineRecord is the only corpus entry identity. Source-backed records enrich it; direct registrations remain visible.
- FileSystemWatcher is advisory. Persisted scan control and periodic reconciliation are authoritative.
- Preserve Phase 3A root policy, physical identity, no-reparse, immutable retained-byte, checksum and source-activity idempotency invariants.
- Reuse six public Job states. Watcher work cannot index directly or create an executor/MCP mutation route.
- Do not add process/PID/supervision, GPU admission, model work, external access or legacy/Docker/RabbitMQ/Vespa work.
- Audit details are bounded/sanitised and never contain raw retained text, bytes, credentials or opaque lease/process values.
- Corpus preview reads bounded indexed SQL artefact/chunk text only; it never re-opens a source path or source-artifact file.
- Cursor tokens contain a stable sort key, PipelineRecord ID and filter fingerprint. Default page size is 50; maximum is 200.
- No migration, deployment, IIS restart or live validation may run without current explicit approval.

---

## Execution boundaries

One implementation agent owns each task, with one focused read-only review before the next task. No agents edit shared files concurrently. Task 4 is the watcher-to-durable-scan checkpoint; Task 6 is the observable operator checkpoint.

### Task 1: Watch, event and corpus query contracts

**Files:**

- Create: `src/FluxKnowledge.Domain/Sources/SourceWatchSignalKind.cs`
- Create: `src/FluxKnowledge.Domain/Sources/SourceWatchState.cs`
- Create: `src/FluxKnowledge.Application/Contracts/OperatorEventContracts.cs`
- Create: `src/FluxKnowledge.Application/Contracts/CorpusContracts.cs`
- Create: `src/FluxKnowledge.Application/Ports/ISourceRootWatchStore.cs`
- Create: `src/FluxKnowledge.Application/Ports/ICorpusProjectionReader.cs`
- Create: `src/FluxKnowledge.Application/Sources/SourceWatchCoordinator.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/SourceWatchCoordinatorTests.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/OperatorEventContractsTests.cs`
- Test: `tests/FluxKnowledge.Domain.Tests/Sources/CorpusCursorTests.cs`

**Interfaces:**

- `SourceWatchSignalKind` has exactly `Created`, `Changed`, `Deleted`, `Renamed` and `Overflow`.
- `SourceWatchSignal(SourceRootId RootId, SourceWatchSignalKind Kind, DateTimeOffset ObservedAtUtc)` carries no path.
- `SourceWatchState` holds root ID, first/last signal time, signal count, debounce generation and due time. `Observe` uses two-second quiet/30-second maximum delay; `Overflow` is due immediately.
- `ISourceRootWatchStore.RecordSignalAsync` atomically upserts one root state. `ClaimDueBatchAsync` returns a fenced batch. `ReleaseScanAsync` records one watch event and releases/reuses the normal scan request/job/outbox.
- `CorpusQuery` has canonical filters, history flag, page size and opaque `CorpusCursor`. `CorpusCursor` validates a SHA-256 fingerprint of canonical filters. `CorpusPage` and `CorpusEntryDetail` contain only SQL-derived fields.
- `OperatorEventQuery` and `OperatorEventPage` use the same cursor/fingerprint pattern.

- [ ] **Step 1: Write the failing Domain tests.**

~~~csharp
[Fact]
public void Overflow_is_due_now_without_trusting_a_path()
{
    var state = SourceWatchState.Empty(new SourceRootId(Guid.NewGuid()));
    var next = state.Observe(
        new SourceWatchSignal(state.SourceRootId, SourceWatchSignalKind.Overflow, DateTimeOffset.UnixEpoch),
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));

    Assert.Equal(DateTimeOffset.UnixEpoch, next.DueAtUtc);
}

[Fact]
public void Cursor_rejects_a_different_filter_fingerprint()
{
    var cursor = CorpusCursor.Create(DateTimeOffset.UnixEpoch, Guid.NewGuid(), "root=a");
    Assert.Throws<ArgumentException>(() => cursor.ValidateFor("root=b"));
}
~~~

- [ ] **Step 2: Run tests to capture RED.**

~~~powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~SourceWatchCoordinator|FullyQualifiedName~OperatorEventContracts|FullyQualifiedName~CorpusCursor"
~~~

Expected: compilation fails because the contracts do not exist.

- [ ] **Step 3: Implement minimal contracts.**

~~~csharp
public sealed record CorpusCursor(DateTimeOffset LastActivityAtUtc, Guid PipelineRecordId, string FilterFingerprint)
{
    public void ValidateFor(string canonicalFilter)
    {
        if (!string.Equals(FilterFingerprint, CorpusFilterFingerprint.Compute(canonicalFilter), StringComparison.Ordinal))
            throw new ArgumentException("The cursor does not match the current filters.", nameof(canonicalFilter));
    }
}
~~~

- [ ] **Step 4: Run contracts with existing source-domain coverage.**

~~~powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --filter "FullyQualifiedName~SourceWatchCoordinator|FullyQualifiedName~OperatorEventContracts|FullyQualifiedName~CorpusCursor|FullyQualifiedName~Sources"
~~~

- [ ] **Step 5: Review Task 1.** Confirm watcher signals cannot convey paths and cursor mismatch rejects before query execution.

- [ ] **Step 6: Commit.**

~~~powershell
git add src/FluxKnowledge.Domain/Sources/SourceWatchSignalKind.cs src/FluxKnowledge.Domain/Sources/SourceWatchState.cs src/FluxKnowledge.Application/Contracts/OperatorEventContracts.cs src/FluxKnowledge.Application/Contracts/CorpusContracts.cs src/FluxKnowledge.Application/Ports/ISourceRootWatchStore.cs src/FluxKnowledge.Application/Ports/ICorpusProjectionReader.cs src/FluxKnowledge.Application/Sources/SourceWatchCoordinator.cs tests/FluxKnowledge.Domain.Tests/Sources/SourceWatchCoordinatorTests.cs tests/FluxKnowledge.Domain.Tests/Sources/OperatorEventContractsTests.cs tests/FluxKnowledge.Domain.Tests/Sources/CorpusCursorTests.cs
git commit -m "feat: define phase 3b watcher corpus contracts"
~~~
### Task 2: SQL watch state and event schema

- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/SourceRootWatchStateEntity.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Entities/AuditEventEntity.cs`

- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/FluxKnowledgeDbContext.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Configurations/CanonicalSchemaConfigurations.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260809110000_AddPhase3BWatcherCorpusEvents.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/20260809110000_AddPhase3BWatcherCorpusEvents.Designer.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/FluxKnowledgeDbContextModelSnapshot.cs`
- Modify: `tests/FluxKnowledge.Integration.Tests/Support/SqlTestData.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Persistence/Phase3BWatchAndEventSchemaTests.cs`

**Interfaces:**

- `SourceRootWatchStateEntity` is one row/root: first/last signal time, bounded count, debounce generation, due time, lease owner/generation/expiry and rowversion. It has a due-work index.
- `AuditEventEntity` gains nullable `SourceRootId`, `SourceScanRequestId`, `SourceRevisionId`, `SourceActivityId`, `CorrelationId`, `EventFamily` and `Severity`. Existing audit rows remain valid.
- New foreign keys are restricted/no-cascade. Indexes support descending page queries and timelines by PipelineRecord, root and revision.
- The migration is additive only. It must not alter jobs, scheduler/GPU, executor, legacy, Docker or USearch tables.

- [ ] **Step 1: Write failing native SQL mapping tests.**

~~~csharp
[NativeSqlServerFact]
public async Task Existing_pipeline_audit_row_remains_valid_without_source_correlations()
{
    await using var context = _fixture.CreateContext();
    context.AuditEvents.Add(new AuditEventEntity
    {
        EventType = "pipeline.stage_completed",
        Actor = "test",
        DetailsJson = "{}",
        OccurredAtUtc = DateTimeOffset.UnixEpoch
    });

    await context.SaveChangesAsync();
    Assert.Null((await context.AuditEvents.SingleAsync()).SourceRootId);
}
~~~

- [ ] **Step 2: Run tests to capture RED.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~Phase3BWatchAndEventSchema"
~~~

Expected: entity/mapping or migration failure before schema work exists.

- [ ] **Step 3: Configure entities and generate/inspect the migration.** Add FK-ordered watch/event cleanup to `SqlTestData` and verify Up adds only the listed table, nullable columns, FKs and indexes.

~~~csharp
builder.HasOne(entity => entity.SourceRoot)
    .WithOne()
    .HasForeignKey<SourceRootWatchStateEntity>(entity => entity.SourceRootId)
    .OnDelete(DeleteBehavior.Restrict);

builder.HasIndex(entity => new { entity.DueAtUtc, entity.LeaseExpiresAtUtc });
~~~

- [ ] **Step 4: Run schema/migration tests.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~Phase3BWatchAndEventSchema|FullyQualifiedName~SchemaMapping"
~~~

- [ ] **Step 5: Review Task 2 migration safety.** Down must use the repository’s destructive-data guard convention instead of silently dropping durable watch/event rows.

- [ ] **Step 6: Commit.**

~~~powershell
git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence tests/FluxKnowledge.Integration.Tests/Persistence/Phase3BWatchAndEventSchemaTests.cs tests/FluxKnowledge.Integration.Tests/Support/SqlTestData.cs
git commit -m "feat: persist phase 3b watcher and event state"
~~~

### Task 3: Atomic source and pipeline event emission

**Files:**

- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/OperatorEventAppender.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceScanStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlJobClaimStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlStageTransitionStore.cs`
- Modify: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPipelineStore.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/SourceOperatorEventIntegrationTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Persistence/PipelineOperatorEventIntegrationTests.cs`

**Interfaces:**

- `OperatorEventAppender.Add(context, draft)` is an internal helper that adds to the caller’s DbContext. It never opens a second context or owns a transaction.
- `SqlSourceScanStore` returns a convergence disposition (added, changed/restored or unchanged) so source events are emitted only for authoritative changes. Suppressing an unseen current revision appends exactly one `source.removed` event.
- Source activity creation emits `activity.planned` or `activity.deferred`. Exact linked activity claim, fenced failure and completed publish emit matching activity events inside their existing transaction.
- Existing audit types are retained. New events use source correlations when known.

- [ ] **Step 1: Write failing native source/pipeline transition tests.**

~~~csharp
[NativeSqlServerFact]
public async Task Unchanged_rescan_does_not_duplicate_source_added()
{
    var first = await ScanAsync("sentinel.txt", "same bytes");
    var second = await ScanAsync("sentinel.txt", "same bytes");

    Assert.Equal(first.SourceRevisionId, second.SourceRevisionId);
    Assert.Single(await EventsForRevisionAsync(first.SourceRevisionId, "source.added"));
}
~~~

Also prove changed revision → one `source.updated`, unseen suppression → one `source.removed`, and claim/fenced failure/publish only updates the exact linked receipt.

- [ ] **Step 2: Run tests to capture RED.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SourceOperatorEventIntegration|FullyQualifiedName~PipelineOperatorEventIntegration"
~~~

- [ ] **Step 3: Implement in existing transaction scopes.** For raw claim SQL, insert the audit row inside the claim fence or move only that atomic mutation into the established context transaction.

~~~csharp
context.AuditEvents.Add(OperatorEventAppender.Create(
    OperatorEventDraft.SourceAdded(rootId, scanRequestId, revisionId, correlationId, safeDetails)));
~~~

- [ ] **Step 4: Run transition tests with Phase 3A lifecycle coverage.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SourceOperatorEventIntegration|FullyQualifiedName~PipelineOperatorEventIntegration|FullyQualifiedName~SourceActivityLifecycle|FullyQualifiedName~SourceReconciliation"
~~~

- [ ] **Step 5: Review Task 3 transaction boundaries.** Verify each event shares the state-change transaction and its details are bounded/sanitised.

- [ ] **Step 6: Commit.**

~~~powershell
git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence/OperatorEventAppender.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceScanStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceActivityStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlRetainedTextRegistrationStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlJobClaimStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlStageTransitionStore.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlPipelineStore.cs tests/FluxKnowledge.Integration.Tests/Sources/SourceOperatorEventIntegrationTests.cs tests/FluxKnowledge.Integration.Tests/Persistence/PipelineOperatorEventIntegrationTests.cs
git commit -m "feat: record correlated source and pipeline events"
~~~

### Task 4: Coalesced watcher release and local runtime

**Files:**

- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceRootWatchStore.cs`
- Create: `src/FluxKnowledge.Integrations/Files/LocalSourceRootWatchHostedService.cs`
- Modify: `src/FluxKnowledge.Application/Sources/SourceReconciliationService.cs`
- Modify: `src/FluxKnowledge.Web/WebHostComposition.cs`
- Modify: `src/FluxKnowledge.Integrations/FluxKnowledge.Integrations.csproj` if hosting abstractions are not already referenced
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/SourceRootWatchStoreIntegrationTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/SourceWatcherReconciliationIntegrationTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs`

**Interfaces:**

- `SqlSourceRootWatchStore` serializably records signals and has one open state/root. Claiming a due batch atomically creates or reuses/release the ordinary source request/control Job/outbox and emits one watch event.
- `SourceWatchCoordinator.RecordAsync` signals `ISourceScanWakeSignal` only after commit. `ReleaseDueAsync` drains due batches before source scans.
- `LocalSourceRootWatchHostedService` reads enabled roots, revalidates Phase 3A root identity before watching, forwards only root ID/kind/time, uses `NotifyFilter.FileName | LastWrite | Size | CreationTime`, and maps watcher errors to `Overflow`.
- `SourceReconciliationService` pumps due batches at startup and each tick without changing its opaque lease owner or capacity semantics.

- [ ] **Step 1: Write failing tests for coalescing, overflow, restart and post-commit wake.**

~~~csharp
[NativeSqlServerFact]
public async Task Burst_signals_release_one_scan_request_and_one_watch_event()
{
    await RecordAsync(SourceWatchSignalKind.Created, now);
    await RecordAsync(SourceWatchSignalKind.Changed, now.AddMilliseconds(200));
    await coordinator.ReleaseDueAsync(now.AddSeconds(2), CancellationToken.None);

    Assert.Single(await RequestsForRootAsync(rootId));
    Assert.Single(await EventsForRootAsync(rootId, "watch.batch_detected"));
}
~~~

- [ ] **Step 2: Run tests to capture RED.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SourceRootWatchStore|FullyQualifiedName~SourceWatcherReconciliation"
~~~

- [ ] **Step 3: Implement serializable release with established retry/lock conventions.** Persist state and released request before notifying the in-memory channel.

~~~csharp
await strategy.ExecuteAsync(() => ReleaseDueOnceAsync(nowUtc, cancellationToken));
wakeSignal.Notify(); // only after the release transaction commits
~~~

- [ ] **Step 4: Implement watcher hosting and composition registration.** Rebuild watchers from SQL at startup; on a handle error persist an overflow signal and leave periodic reconciliation as recovery.

- [ ] **Step 5: Run watcher tests with existing source recovery coverage.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~SourceRootWatchStore|FullyQualifiedName~SourceWatcherReconciliation|FullyQualifiedName~SourceReconciliation|FullyQualifiedName~SourceRestartRecovery"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~WebHostComposition"
~~~

- [ ] **Step 6: Review Task 4 vertical slice.** Confirm burst, overflow and restart all result in a normal authoritative full scan; a stop-period filesystem mutation remains discoverable by periodic reconciliation.

- [ ] **Step 7: Commit.**

~~~powershell
git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceRootWatchStore.cs src/FluxKnowledge.Integrations/Files/LocalSourceRootWatchHostedService.cs src/FluxKnowledge.Application/Sources/SourceReconciliationService.cs src/FluxKnowledge.Web/WebHostComposition.cs src/FluxKnowledge.Integrations/FluxKnowledge.Integrations.csproj tests/FluxKnowledge.Integration.Tests/Sources/SourceRootWatchStoreIntegrationTests.cs tests/FluxKnowledge.Integration.Tests/Sources/SourceWatcherReconciliationIntegrationTests.cs tests/FluxKnowledge.Web.Tests/Composition/WebHostCompositionTests.cs
git commit -m "feat: coalesce local source watcher scans"
~~~

### Task 5: SQL Corpus and Events projections

**Files:**

- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlCorpusProjectionReader.cs`
- Create: `src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlOperatorEventProjectionReader.cs`
- Modify: `src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs`
- Modify: `src/FluxKnowledge.Web/Components/Status/IProjectionReader.cs` only if needed for the compatibility redirect
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/CorpusProjectionIntegrationTests.cs`
- Test: `tests/FluxKnowledge.Integration.Tests/Sources/OperatorEventProjectionIntegrationTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Components/CorpusProjectionTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Components/EventsProjectionTests.cs`

**Interfaces:**

- `ReadPageAsync(CorpusQuery)` joins direct and source-backed PipelineRecords, derives activity state from unsuppressed activities and linked durable receipts, never scan counts or watcher offers.
- It sorts by latest correlated `AuditEvents.OccurredAtUtc` with `RegisteredAtUtc` fallback and keysets by `(LastActivityAtUtc DESC, PipelineRecordId DESC)`.
- `ReadFoldersAsync` derives root-relative child folders and aggregate current/deferred/blocked/failed counts from SourceRevision paths only.
- `ReadDetailAsync` returns bounded `Artifacts.SearchText`/`TextChunks` only for current eligible rows. Deferred, suppressed and non-text rows return no preview.
- `SqlOperatorEventProjectionReader` uses parameterised filters and stable `(OccurredAtUtc DESC, Id DESC)` paging.

- [ ] **Step 1: Write failing native/Web projection tests.**

~~~csharp
[NativeSqlServerFact]
public async Task Corpus_page_includes_direct_and_source_backed_records_without_counting_an_offer_as_indexed()
{
    var page = await reader.ReadPageAsync(new CorpusQuery(PageSize: 50), CancellationToken.None);

    Assert.Contains(page.Items, item => item.Location == "Direct");
    Assert.Contains(page.Items, item => item.SourceActivityState == "Deferred");
    Assert.DoesNotContain(page.Items, item => item.SourceActivityState == "Indexed" && item.ResultingPipelineRecordId is null);
}
~~~

Also prove folder stability when the original source is unavailable, cursor/filter mismatch rejection, preview bound and root/revision/correlation event filters.

- [ ] **Step 2: Run tests to capture RED.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~CorpusProjectionIntegration|FullyQualifiedName~OperatorEventProjectionIntegration"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~CorpusProjection|FullyQualifiedName~EventsProjection"
~~~

- [ ] **Step 3: Implement parameterised projections and preview bound.**

~~~csharp
var eligible = context.PipelineRecords.AsNoTracking()
    .Where(record => query.IncludeHistorical || (!record.IsDeleted &&
        (record.SourceRevision == null || record.SourceRevision.SuppressedAtUtc == null)));
~~~

- [ ] **Step 4: Run projections with source/pipeline reader coverage.**

~~~powershell
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --filter "FullyQualifiedName~CorpusProjectionIntegration|FullyQualifiedName~OperatorEventProjectionIntegration|FullyQualifiedName~SourceReconciliation"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~CorpusProjection|FullyQualifiedName~EventsProjection|FullyQualifiedName~PipelineRecordsProjection|FullyQualifiedName~SourceRootProjection"
~~~

- [ ] **Step 5: Review Task 5 query safety.** Inspect native SQL evidence for parameterisation, indexed keyset predicates, source-suppression eligibility and no N+1 child query loop.

- [ ] **Step 6: Commit.**

~~~powershell
git add src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlCorpusProjectionReader.cs src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlOperatorEventProjectionReader.cs src/FluxKnowledge.Web/Components/Status/SqlProjectionReader.cs src/FluxKnowledge.Web/Components/Status/IProjectionReader.cs tests/FluxKnowledge.Integration.Tests/Sources/CorpusProjectionIntegrationTests.cs tests/FluxKnowledge.Integration.Tests/Sources/OperatorEventProjectionIntegrationTests.cs tests/FluxKnowledge.Web.Tests/Components/CorpusProjectionTests.cs tests/FluxKnowledge.Web.Tests/Components/EventsProjectionTests.cs
git commit -m "feat: project corpus entries and operator events"
~~~

### Task 6: Corpus and Events pages

**Files:**

- Create: `src/FluxKnowledge.Web/Components/Corpus/CorpusPageState.cs`
- Create: `src/FluxKnowledge.Web/Components/Corpus/CorpusDetailPageState.cs`
- Create: `src/FluxKnowledge.Web/Components/Events/EventsPageState.cs`
- Create: `src/FluxKnowledge.Web/Components/Pages/Corpus.razor`
- Create: `src/FluxKnowledge.Web/Components/Pages/CorpusDetail.razor`
- Create: `src/FluxKnowledge.Web/Components/Pages/Events.razor`
- Modify: `src/FluxKnowledge.Web/Components/Pages/PipelineRecords.razor`
- Modify: `src/FluxKnowledge.Web/Components/Pages/SourceRootDetail.razor`
- Modify: `src/FluxKnowledge.Web/Components/Layout/NavMenu.razor`
- Modify: `src/FluxKnowledge.Web/wwwroot/app.css`
- Test: `tests/FluxKnowledge.Web.Tests/Components/CorpusPageStateTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Components/EventsPageStateTests.cs`
- Test: `tests/FluxKnowledge.Web.Tests/Browser/Phase3BCorpusAndEventsBrowserTests.cs`

**Interfaces:**

- `CorpusPageState` owns immutable query state, clears cursor after a filter change, reloads SQL for `corpus`/`sources`/`pipeline`/`reconnect` events and disposes subscriptions safely.
- `CorpusDetailPageState` shows entry/job/activity/event detail. `EventsPageState` has live-tail/pause rendering only; both discard stale asynchronous loads.
- `/pipeline-records` redirects to `/corpus`. No public REST/MCP mutation route is added.
- Source detail receives a bounded recent-events section from the same event reader.
- Controls are keyboard accessible; opaque IDs are only copyable on detail diagnostics.

- [ ] **Step 1: Write failing component tests.**

~~~csharp
[Fact]
public async Task Corpus_filter_change_clears_the_previous_cursor_before_reloading()
{
    var state = new CorpusPageState(new SequencedCorpusReader());
    await state.LoadAsync(CancellationToken.None);

    await state.ChangeRootAsync(Guid.NewGuid(), CancellationToken.None);

    Assert.Null(state.Query.Cursor);
    Assert.Equal(2, state.ReaderCallCount);
}
~~~

Also prove direct row rendering, deferred preview omission, reconnect reload, live-tail pause and subscription disposal.

- [ ] **Step 2: Run Web tests to capture RED.**

~~~powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~CorpusPageState|FullyQualifiedName~EventsPageState|FullyQualifiedName~Phase3BCorpusAndEvents"
~~~

- [ ] **Step 3: Implement page states, pages, nav and scoped CSS.** Default Corpus to current rows and Events to live-tail. Render loading, empty and failure states from server projections.

- [ ] **Step 4: Add a guarded browser flow.** Seed a direct and source-backed PipelineRecord plus correlated events through disposable SQL; navigate Corpus, filter root/folder, open detail and inspect a bounded indexed snippet; then filter Events by correlation and prove reconnect refresh. Use synthetic data only.

- [ ] **Step 5: Run focused Web/browser and existing source/pipeline/overview tests.**

~~~powershell
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --filter "FullyQualifiedName~CorpusPageState|FullyQualifiedName~EventsPageState|FullyQualifiedName~Phase3BCorpusAndEvents|FullyQualifiedName~SourceRoot|FullyQualifiedName~PipelineRecords|FullyQualifiedName~Overview"
~~~

- [ ] **Step 6: Review the observable vertical slice.** Confirm file change → watch batch → released scan → source/pipeline event → Corpus row → Events row without any executor action.

- [ ] **Step 7: Commit.**

~~~powershell
git add src/FluxKnowledge.Web/Components/Corpus src/FluxKnowledge.Web/Components/Events src/FluxKnowledge.Web/Components/Pages/Corpus.razor src/FluxKnowledge.Web/Components/Pages/CorpusDetail.razor src/FluxKnowledge.Web/Components/Pages/Events.razor src/FluxKnowledge.Web/Components/Pages/PipelineRecords.razor src/FluxKnowledge.Web/Components/Pages/SourceRootDetail.razor src/FluxKnowledge.Web/Components/Layout/NavMenu.razor src/FluxKnowledge.Web/wwwroot/app.css tests/FluxKnowledge.Web.Tests/Components/CorpusPageStateTests.cs tests/FluxKnowledge.Web.Tests/Components/EventsPageStateTests.cs tests/FluxKnowledge.Web.Tests/Browser/Phase3BCorpusAndEventsBrowserTests.cs
git commit -m "feat: add corpus and events operator views"
~~~

### Task 7: Verification, review and approval-gated operations

**Files:**

- Modify: `docs/roadmap.md` only after fresh implementation evidence
- Modify: `docs/architecture.md` only if implementation changes the approved design
- Create: `docs/operations/native-windows-phase-3b-watcher-corpus-events-validation.md` only after separately approved local validation

**Interfaces:**

- This task creates no runtime interface. It proves Phase 1/2/3A invariants remain valid and documents only evidence actually run.
- Roadmap remains planned 0% until a fresh delivery batch passes focused tests. Mark in progress only with stated evidence weighting. Do not mark complete without separately approved native SQL/IIS/local-root validation.

- [ ] **Step 1: Run the focused matrix serially and retain exact output.**

~~~powershell
dotnet test tests/FluxKnowledge.Domain.Tests/FluxKnowledge.Domain.Tests.csproj --configuration Release --filter "FullyQualifiedName~Sources|FullyQualifiedName~Corpus|FullyQualifiedName~OperatorEvent"
dotnet test tests/FluxKnowledge.Integration.Tests/FluxKnowledge.Integration.Tests.csproj --configuration Release --filter "FullyQualifiedName~Source|FullyQualifiedName~Corpus|FullyQualifiedName~OperatorEvent|FullyQualifiedName~SqlToUsearchRebuild"
dotnet test tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj --configuration Release --filter "FullyQualifiedName~Corpus|FullyQualifiedName~Events|FullyQualifiedName~SourceRoot|FullyQualifiedName~PipelineRecords|FullyQualifiedName~Overview"
dotnet build FluxKnowledge.slnx --configuration Release -warnaserror
~~~

- [ ] **Step 2: Run one whole-branch review.** Review migration/Down safety, watcher authority, path/identity invariants, transaction exactness, cursor/filter isolation, preview containment, reconnect behaviour, compatibility redirect and prohibited scope.

- [ ] **Step 3: Fix only review findings with a focused RED/GREEN cycle.** Re-run the exact affected command from Step 1 after each repair.

- [ ] **Step 4: Update documentation only with fresh evidence.** Record Domain/Integration/Web pass and skip counts separately. Do not deploy, restart IIS, apply migrations or create a validation record without current user approval.

- [ ] **Step 5: Run final checks and commit evidence docs when they exist.**

~~~powershell
git diff --check
dotnet build FluxKnowledge.slnx --configuration Release -warnaserror
git add docs/roadmap.md docs/architecture.md
if (Test-Path docs/operations/native-windows-phase-3b-watcher-corpus-events-validation.md) {
    git add docs/operations/native-windows-phase-3b-watcher-corpus-events-validation.md
}
git commit -m "docs: record phase 3b watcher corpus evidence"
~~~

Stage a validation record only when it was created and every claim has fresh command output.

- [ ] **Step 6: Close out only with explicit user authority.**

~~~powershell
.\scripts\dev\complete-feature.ps1
~~~

This command is intentionally out of scope until the user explicitly approves closeout and operational actions.
