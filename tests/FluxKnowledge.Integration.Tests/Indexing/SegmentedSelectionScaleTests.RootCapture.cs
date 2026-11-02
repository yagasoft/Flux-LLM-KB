using System.Diagnostics;
using System.Data.Common;
using System.Xml.Linq;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Sources;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SegmentedSelectionScaleTests
{
    [NativeSqlServerFact]
    public async Task Root_capture_and_membership_commands_remain_bounded_at_production_scale_without_extending_the_writer_deadline()
        // The strict comparison run also proves the normal 30-second command
        // budget, while retaining the independent competing-writer deadline.
        // Avoid rebuilding the same full-width 100k-member fixture a second time.
        => await VerifyRootCaptureAsync(5);

    private async Task VerifyRootCaptureAsync(int comparisonCommandSeconds)
    {
        var scaleFixture = new NativeSqlServerFixture();
        await scaleFixture.InitializeAsync();
        try
        {
            await SqlTestData.ClearPhase3SourceDataAsync(scaleFixture);
            var additionalMembers = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_ROOT_SCALE_MEMBERS") is { } configured
                ? int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture) : 100_000;
            var unpublishedVectors = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_ROOT_SCALE_UNPUBLISHED") is { } unpublished
                ? int.Parse(unpublished, System.Globalization.CultureInfo.InvariantCulture) : 0;
            var fence = new MetadataFenceObserver(message => output.WriteLine(message));
            var comparison = new RootComparisonDeadline(comparisonCommandSeconds, message => output.WriteLine(message));
            await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(scaleFixture,
                "The root-wide metadata capture keeps canonical work available.", segmented: true,
                configureServices: services =>
                {
                    services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                        new MetadataFactory(scaleFixture.ConnectionString, fence, comparison));
                    services.AddSingleton<IEmbeddingProvider, WideScaleEmbeddingProvider>();
                });
            output.WriteLine($"fixture_scope catalog={scaleFixture.DatabaseName} index_root={environment.IndexRoot}");
            var retained = await environment.AddRetainedAndPumpAsync("The retained root supplies metadata-only scale rows.");
            var controls = new List<Guid> { retained };
            for (var owner = 1; owner < 4; owner++)
                controls.Add(await environment.AddRetainedAndPumpAsync($"Retained root control owner {owner}."));
            var removedRecord = await environment.AddRetainedAndPumpAsync("This one rooted member is removed while the large sibling survives.");
            Guid root;
            Guid[] retainedRevisions;
            Guid removedRevision;
            long removedVector;
            long expected;
            await using (var seed = await environment.Factory.CreateDbContextAsync())
            {
                seed.Database.SetCommandTimeout(180); // Seeding only; the operation keeps its normal command deadlines.
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    IF DB_NAME() <> {scaleFixture.DatabaseName} OR NOT EXISTS (
                        SELECT 1 FROM sys.databases WHERE database_id=DB_ID() AND owner_sid=SUSER_SID())
                        THROW 51000, 'The scale fixture database is not owned.', 1;
                    DECLARE @sql nvarchar(max)=N'ALTER DATABASE '+QUOTENAME(DB_NAME())+N' SET RECOVERY SIMPLE;';
                    EXEC sys.sp_executesql @sql;
                    """);
                var identity = await seed.PipelineRecords.Where(value => value.Id == retained).Select(value => value.SourceIdentityId).SingleAsync();
                var vector = await seed.CurrentIndexMembers.Where(value => value.SourceIdentityId == identity).Select(value => value.VectorId).FirstAsync();
                root = await seed.CurrentIndexMembers.Where(value => value.VectorId == vector).Select(value => value.SourceRootId!.Value).SingleAsync();
                retainedRevisions = await seed.PipelineRecords.Where(value => controls.Contains(value.Id))
                    .Select(value => value.SourceRevisionId!.Value).ToArrayAsync();
                await seed.SourceRevisions.Where(value => retainedRevisions.Contains(value.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRootId, root));
                await seed.CurrentIndexMembers.Where(value => value.OwnerSourceRevisionId.HasValue &&
                    retainedRevisions.Contains(value.OwnerSourceRevisionId.Value))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRootId, (Guid?)root));
                var controlVectors = await seed.CurrentIndexMembers.Where(value => value.OwnerSourceRevisionId.HasValue &&
                    retainedRevisions.Contains(value.OwnerSourceRevisionId.Value)).OrderBy(value => value.VectorId)
                    .Select(value => value.VectorId).ToArrayAsync();
                removedRevision = await seed.PipelineRecords.Where(value => value.Id == removedRecord).Select(value => value.SourceRevisionId!.Value).SingleAsync();
                removedVector = await seed.CurrentIndexMembers.Where(value => value.OwnerSourceRevisionId == removedRevision).Select(value => value.VectorId).SingleAsync();
                await seed.SourceRevisions.Where(value => value.Id == removedRevision).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRootId, root));
                await seed.CurrentIndexMembers.Where(value => value.OwnerSourceRevisionId == removedRevision).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRootId, (Guid?)root));
                for (var seeded = 0; seeded < additionalMembers; seeded += 25_000)
                {
                    await SeedUnrelatedAsync(seed, controlVectors[(seeded / 25_000) % controlVectors.Length],
                        Math.Min(25_000, additionalMembers - seeded));
                    if ((seeded + 25_000) % 100_000 == 0) await seed.Database.ExecuteSqlRawAsync("CHECKPOINT;");
                }
                var unrelated = await seed.CurrentIndexMembers.Where(value => value.SourceRootId == null)
                    .Select(value => value.VectorId).FirstAsync();
                for (var seeded = 0; seeded < unpublishedVectors; seeded += 25_000)
                {
                    var fileRoot = Environment.GetEnvironmentVariable(NativeSqlServerFixture.FileRootEnvironmentVariable)!;
                    if (new DriveInfo(Path.GetPathRoot(fileRoot)!).AvailableFreeSpace < 1_000_000_000)
                        throw new InvalidOperationException("The owned staging volume lacks the scale-test storage reserve.");
                    // Unpublished/checkpoint vectors occupy the raw key space
                    // without current membership. They retain the same full-width
                    // payload and headers as the compared rows.
                    await SeedUnrelatedAsync(seed, unrelated, Math.Min(25_000, unpublishedVectors - seeded), unpublished: true);
                    if ((seeded + 25_000) % 100_000 == 0) await seed.Database.ExecuteSqlRawAsync("CHECKPOINT;");
                }
                output.WriteLine($"Raw vectors: {await seed.Vectors.LongCountAsync():N0}; unpublished controls: {unpublishedVectors:N0}; all scale rows at 1024 dimensions with unchanged template headers.");
                // Keep the SQL metadata ledger/manifest coherent. The synthetic
                // segment descriptors are never handed to a native reader.
                await seed.Database.ExecuteSqlRawAsync("""
                    DECLARE @base uniqueidentifier=(SELECT ActiveIndexGenerationId FROM IndexState WHERE Id=1);
                    DECLARE @last int=(SELECT COALESCE(MAX(Ordinal),-1) FROM IndexGenerationSegments WHERE GenerationId=@base);
                    INSERT IndexGenerationSegments(GenerationId,SegmentId,Ordinal)
                    SELECT @base, SegmentId, @last+CONVERT(int,ROW_NUMBER() OVER(ORDER BY SegmentId))
                    FROM (SELECT DISTINCT SegmentId FROM CurrentIndexMembers) AS members
                    WHERE NOT EXISTS(SELECT 1 FROM IndexGenerationSegments existing
                        WHERE existing.GenerationId=@base AND existing.SegmentId=members.SegmentId);
                    UPDATE generation SET VectorCount=(SELECT COUNT_BIG(*) FROM CurrentIndexMembers),
                        SegmentCount=(SELECT COUNT(*) FROM IndexGenerationSegments WHERE GenerationId=@base)
                    FROM IndexGenerations generation WHERE Id=@base;
                    """);
                expected = await seed.CurrentIndexMembers.LongCountAsync(value => value.SourceRootId == root);
            }
            await new SqlSourceScanStore(environment.Factory, TimeProvider.System).SuppressUnseenAsync(new SourceRootId(root),
                retainedRevisions.Select(value => new SourceRevisionId(value)).ToHashSet(), CancellationToken.None);
            var additionalOwners = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_ROOT_SCALE_OWNERS") is { } owners
                ? int.Parse(owners, System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (additionalOwners > 0)
            {
                await using var ownerSeed = await environment.Factory.CreateDbContextAsync();
                ownerSeed.Database.SetCommandTimeout(180);
                await using var ownerTransaction = await ownerSeed.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                await SqlPublishedPassageSelection.AcquireFenceAsync(ownerSeed, default);
                var template = await ownerSeed.CurrentIndexMembers.Where(value => value.OwnerSourceRevisionId == retainedRevisions[0])
                    .Select(value => value.VectorId).FirstAsync();
                await SeedMetadataOwnersAsync(ownerSeed, template, additionalOwners);
                await SqlPublishedPassageSelection.AdvanceVersionAsync(ownerSeed, DateTimeOffset.UtcNow, default, [new(3, root)]);
                await ownerTransaction.CommitAsync();
                output.WriteLine($"Additional eligible metadata-only owners: {additionalOwners:N0}; no vectors added.");
            }
            using var scope = environment.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
            var watcherTurn = 0;
            SegmentedIndexCandidate prepared = null!;
            await RunWithWatcherAsync("capture", async token => prepared = await store.PrepareAsync(Guid.Empty, token));
            output.WriteLine($"Existing root debt: {expected:N0} members.");
            await using var after = await environment.Factory.CreateDbContextAsync();
            var candidate = await after.IndexPublicationCandidates.SingleAsync(value => value.Id == prepared.Id);
            Assert.True(candidate.WorklistComplete);
            Assert.Equal(expected, candidate.BeforeCount);
            Assert.Equal(expected - 1, candidate.AfterCount);
            Assert.NotEqual(candidate.BeforeChecksum, candidate.AfterChecksum);
            Assert.Equal(expected, await after.IndexCandidateMembers.LongCountAsync(value => value.CandidateId == prepared.Id && value.Phase == 0));
            Assert.Equal(expected - 1, await after.IndexCandidateMembers.LongCountAsync(value => value.CandidateId == prepared.Id && value.Phase == 1));
            Assert.True(await after.IndexProjectionChanges.AnyAsync(value => value.ScopeKind == 3 && value.ScopeId == root));
            var generation = new IndexGenerationDescriptor(Guid.NewGuid(), prepared.ModelFingerprint, prepared.Dimensions,
                "sql-only-no-native-manifest", new('0', 64), prepared.ReusedSegments.Sum(value => value.VectorCount),
                prepared.PublicationStamp, 2, prepared.ReusedSegments.Count);
            await RunWithWatcherAsync("manifest seal", token => store.SealManifestAsync(prepared.Id,
                new(generation, prepared.ReusedSegments), token).AsTask());
            if (Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_ROOT_SCALE_COVERING_INDEX") == "1")
            {
                await using var planTransaction = await after.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                await SqlPublishedPassageSelection.AcquireFenceAsync(after, default);
                var compare = typeof(SqlSegmentedIndexStore).GetMethod("CompareRootWindowAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                foreach (var phase in new[] { 0, 1 })
                {
                    if (phase == 1) await SqlSegmentedIndexStore.CreateRootEligibilityMapAsync(after, candidate.AffectedScopesJson, default);
                    if (phase == 1) Assert.Equal(4 + additionalOwners,
                        await after.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM #RootEligibleArtifacts").SingleAsync());
                    var raw = phase == 0
                        ? after.CurrentIndexMembers.Where(value => value.CorpusEpoch == prepared.ExpectedStamp.CorpusEpoch).Select(value => value.VectorId)
                        : after.Vectors.Select(value => value.VectorId);
                    var keys = await raw.Order().Take(16_384).ToArrayAsync();
                    var staged = await after.IndexCandidateMembers.Where(value => value.CandidateId == prepared.Id && value.Phase == phase)
                        .OrderBy(value => value.VectorId).Select(value => value.VectorId).Take(16_384).ToArrayAsync();
                    var through = keys.Concat(staged).Distinct().Order().Take(16_384).Last();
                    var execution = (Task)compare.Invoke(null, [after, candidate, phase, 0L, through, CancellationToken.None])!;
                    await execution;
                    var result = execution.GetType().GetProperty("Result")!.GetValue(execution)!;
                    Assert.Equal(0L, (long)result.GetType().GetProperty("Differences")!.GetValue(result)!);
                }
                await comparison.VerifyCoveringPlansAsync(after);
                await planTransaction.RollbackAsync();
            }
            var activated = false;
            await RunWithWatcherAsync("activation", async token => activated = await store.TryActivateRefreshAsync(
                new(generation, [], prepared.ExpectedStamp, prepared.Id), token));
            Assert.True(activated);
            Assert.Equal(generation.Id, await after.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
            Assert.Equal(expected - 1, await after.CurrentIndexMembers.LongCountAsync(value => value.SourceRootId == root));
            Assert.False(await after.CurrentIndexMembers.AnyAsync(value => value.OwnerSourceRevisionId == removedRevision));
            Assert.True(await after.Vectors.AnyAsync(value => value.VectorId == removedVector));
            Assert.True(await after.CurrentIndexMembers.AnyAsync(value => value.SourceRootId == null));
            Assert.False(await after.IndexCandidateMembers.AnyAsync(value => value.CandidateId == prepared.Id));

            // An invalidated root preview can be just as large as a successful
            // publication. Its terminal transition must also release the fence
            // before discarding the temporary metadata.
            await using (var change = await environment.Factory.CreateDbContextAsync())
            await using (var transaction = await change.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable))
            {
                await SqlPublishedPassageSelection.AcquireFenceAsync(change, default);
                var state = await change.IndexState.SingleAsync();
                state.CorpusVersion++;
                change.IndexProjectionChanges.Add(new()
                {
                    CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 3,
                    ScopeId = root, CreatedAtUtc = DateTimeOffset.UtcNow
                });
                await change.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            SegmentedIndexCandidate abandoned = null!;
            await RunWithWatcherAsync("abandonment capture", async token => abandoned = await store.PrepareAsync(Guid.Empty, token));
            await RunWithWatcherAsync("abandonment", token => store.AbandonAsync(abandoned.Id, token).AsTask());
            Assert.Equal(3, await after.IndexPublicationCandidates.Where(value => value.Id == abandoned.Id).Select(value => value.State).SingleAsync());
            Assert.False(await after.IndexCandidateMembers.AnyAsync(value => value.CandidateId == abandoned.Id));
            Assert.Equal(generation.Id, await after.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
            Assert.Equal(expected - 1, await after.CurrentIndexMembers.LongCountAsync(value => value.SourceRootId == root));

            async Task RunWithWatcherAsync(string phase, Func<CancellationToken, Task> action)
            {
                var watcher = new SqlSourceRootWatchStore(environment.Factory, TimeProvider.System);
                var now = DateTimeOffset.UtcNow.AddMinutes(2 * watcherTurn++);
                await watcher.RecordSignalAsync(new(new SourceRootId(root), SourceWatchSignalKind.Changed, now.AddMinutes(-1)), default);
                // Capture is now a series of bounded transactions; only a competing
                // writer has the existing 30-second lock deadline.
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var timer = Stopwatch.StartNew();
                fence.Arm(phase);
                var operation = action(deadline.Token);
                double maximumWriterSeconds = 0;
                var attempts = 0;
                try
                {
                    if (await Task.WhenAny(fence.Entered.Task, operation) == operation) await operation;
                    await fence.Entered.Task.WaitAsync(deadline.Token);
                    do
                    {
                        using var writerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var writer = Stopwatch.StartNew();
                        var claim = watcher.ClaimDueBatchAsync(now, "scale-" + phase, TimeSpan.FromMinutes(1), writerDeadline.Token).AsTask();
                        if (attempts == 0)
                        {
                            await fence.WaitingWriter.Task.WaitAsync(writerDeadline.Token);
                            Assert.False(claim.IsCompleted);
                            fence.Release.TrySetResult();
                        }
                        var result = await claim;
                        if (attempts == 0) Assert.NotNull(result);
                        maximumWriterSeconds = Math.Max(maximumWriterSeconds, writer.Elapsed.TotalSeconds);
                        attempts++;
                        if (!operation.IsCompleted) await Task.Delay(20, deadline.Token);
                    } while (!operation.IsCompleted);
                    await operation;
                }
                finally
                {
                    fence.Release.TrySetResult();
                    if (!operation.IsCompleted) { deadline.Cancel(); try { await operation; } catch (OperationCanceledException) { } }
                }
                output.WriteLine($"{phase}: total {timer.Elapsed.TotalSeconds:F3}s; {attempts} watcher claims; slowest {maximumWriterSeconds:F3}s.");
                Assert.True(maximumWriterSeconds < 30, "The watcher exceeded its unchanged writer deadline.");
            }
        }
        finally
        {
            await scaleFixture.DisposeAsync();
        }
    }

    [NativeSqlServerTheory]
    [InlineData(0)]
    [InlineData(100_000)]
    public async Task Guarded_removal_between_capture_pages_commits_and_invalidates_the_entire_candidate(int members)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var append = new MetadataAppendObserver();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The independent keeper remains available after a concurrent removal.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new MetadataFactory(fixture.ConnectionString, append)));
        var retained = await environment.AddRetainedAndPumpAsync("The large rooted metadata control is unchanged.");
        await using (var seed = await environment.Factory.CreateDbContextAsync())
        {
            seed.Database.SetCommandTimeout(180);
            var identity = await seed.PipelineRecords.Where(value => value.Id == retained).Select(value => value.SourceIdentityId).SingleAsync();
            var vector = await seed.CurrentIndexMembers.Where(value => value.SourceIdentityId == identity).Select(value => value.VectorId).FirstAsync();
            for (var count = 0; count < members; count += 25_000) await SeedUnrelatedAsync(seed, vector, Math.Min(25_000, members - count));
            var state = await seed.IndexState.SingleAsync();
            state.CorpusVersion++;
            seed.IndexProjectionChanges.Add(new()
            {
                CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 3,
                ScopeId = await seed.CurrentIndexMembers.Where(value => value.VectorId == vector).Select(value => value.SourceRootId!.Value).SingleAsync(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        await using var removal = await DeletedSourceRecoveryIntegrationTests.Scenario.CreateAsync(fixture.ConnectionString);
        var removedPath = Path.Combine(removal.Files, "removed", "one.txt");
        File.Delete(removedPath);
        await removal.PendingAsync(Path.Combine("removed", "one.txt"), false);
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        append.Arm();
        var preparing = store.PrepareAsync(Guid.Empty, budget.Token).AsTask();
        try
        {
            if (await Task.WhenAny(append.Entered.Task, preparing) == preparing) await preparing;
            await append.Entered.Task.WaitAsync(budget.Token);
            using var writerDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var timer = Stopwatch.StartNew();
            Assert.Equal(1, await new SqlSourceRemovalReconciler(removal.Factory, TimeProvider.System,
                new WindowsSourceObservationReader(), removal.Claim.SourceRoot.Id.Value).ReconcileAsync(writerDeadline.Token));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
            output.WriteLine($"Concurrent guarded removal: {timer.Elapsed.TotalSeconds:F3}s; capture rows={members + 1:N0}.");
        }
        finally { append.Release.TrySetResult(); }
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(() => preparing);
        await using var after = await environment.Factory.CreateDbContextAsync();
        var refused = await after.IndexPublicationCandidates.OrderByDescending(value => value.CreatedAtUtc).FirstAsync();
        Assert.Equal(3, refused.State);
        Assert.False(refused.WorklistComplete);
        Assert.False(await after.IndexCandidateMembers.AnyAsync(value => value.CandidateId == refused.Id));
        var removed = await after.SourceRevisions.SingleAsync(value => value.CanonicalPath == removedPath);
        Assert.NotNull(removed.SuppressedAtUtc);
        Assert.Contains("guarded-path-removal", removed.RetentionEvidenceJson, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(removal.Files, "keeper.txt")));
        var retry = await store.PrepareAsync(Guid.Empty, budget.Token);
        Assert.True(await after.IndexPublicationCandidates.Where(value => value.Id == retry.Id).Select(value => value.WorklistComplete).SingleAsync());
        Assert.Equal(refused.ExpectedCorpusVersion + 1, retry.ExpectedStamp.CorpusVersion);
        await store.AbandonAsync(retry.Id, CancellationToken.None);
    }

    [NativeSqlServerFact]
    public async Task Root_capture_advances_through_empty_raw_windows_and_keeps_later_eligible_members()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The unrelated source supplies raw-key gaps.", segmented: true);
        var retained = await environment.AddRetainedAndPumpAsync("Both rooted control members must be captured across empty windows.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        db.Database.SetCommandTimeout(180); // Synthetic seed only.
        var unrelated = await db.CurrentIndexMembers.Where(value => value.SourceRootId == null).Select(value => value.VectorId).FirstAsync();
        var identity = await db.PipelineRecords.Where(value => value.Id == retained).Select(value => value.SourceIdentityId).SingleAsync();
        var rooted = await db.CurrentIndexMembers.Where(value => value.SourceIdentityId == identity).Select(value => value.VectorId).SingleAsync();
        await SeedUnrelatedAsync(db, unrelated, 40_000);
        await SeedUnrelatedAsync(db, rooted, 1);
        await SeedUnrelatedAsync(db, unrelated, 40_000);
        var root = await db.CurrentIndexMembers.Where(value => value.VectorId == rooted).Select(value => value.SourceRootId!.Value).SingleAsync();
        var expected = await db.CurrentIndexMembers.Where(value => value.SourceRootId == root).OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync();
        Assert.Equal(2, expected.Length);
        var missing = expected[^1];
        await db.CurrentIndexMembers.Where(value => value.VectorId == missing).ExecuteDeleteAsync();
        var state = await db.IndexState.SingleAsync();
        state.CorpusVersion++;
        db.IndexProjectionChanges.Add(new()
        {
            CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 3,
            ScopeId = root, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var prepared = await store.PrepareAsync(Guid.Empty, budget.Token);
        var row = await db.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == prepared.Id);
        Assert.True(row.WorklistComplete);
        Assert.Equal(1, row.BeforeCount);
        Assert.Equal(2, row.AfterCount);
        Assert.NotEqual(row.BeforeChecksum, row.AfterChecksum);
        Assert.Equal(expected, await db.IndexCandidateMembers.Where(value => value.CandidateId == prepared.Id && value.Phase == 1)
            .OrderBy(value => value.VectorId).Select(value => value.VectorId).ToArrayAsync());
        await store.AbandonAsync(prepared.Id, CancellationToken.None);
    }

    [NativeSqlServerFact]
    public async Task Interrupted_metadata_append_keeps_process_ownership_and_cannot_seal_partial_work()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var append = new MetadataAppendObserver();
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The published keeper survives interrupted metadata staging.", segmented: true,
            configureServices: services => services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
                new MetadataFactory(fixture.ConnectionString, append)));
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            state.CorpusVersion++;
            context.IndexProjectionChanges.Add(new()
            {
                CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 1,
                ScopeId = await context.PipelineRecords.Select(value => value.SourceIdentityId).SingleAsync(), CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
        }
        using var scope = environment.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        using var interrupted = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        append.Arm();
        var preparing = store.PrepareAsync(Guid.Empty, interrupted.Token).AsTask();
        try
        {
            if (await Task.WhenAny(append.Entered.Task, preparing) == preparing) await preparing;
            await append.Entered.Task.WaitAsync(interrupted.Token);
            interrupted.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        }
        finally { append.Release.TrySetResult(); }
        await using var after = await environment.Factory.CreateDbContextAsync();
        var partial = await after.IndexPublicationCandidates.OrderByDescending(value => value.CreatedAtUtc).FirstAsync();
        Assert.Equal(0, partial.State);
        Assert.False(partial.WorklistComplete);
        Assert.Equal(-1, partial.LastAppendOrdinal);
        Assert.Equal(Environment.ProcessId, partial.OwnerProcessId);
        Assert.False(await after.IndexCandidateMembers.AnyAsync(value => value.CandidateId == partial.Id));
        Assert.False(await after.IndexSegments.AnyAsync(value => value.ProducingCandidateId == partial.Id));
        Assert.True(await after.CurrentIndexMembers.AnyAsync());
        await store.AbandonAsync(partial.Id, CancellationToken.None);
        var retry = await store.PrepareAsync(Guid.Empty, CancellationToken.None);
        Assert.NotEqual(partial.Id, retry.Id);
        Assert.True(await after.IndexPublicationCandidates.Where(value => value.Id == retry.Id).Select(value => value.WorklistComplete).SingleAsync());
        await store.AbandonAsync(retry.Id, CancellationToken.None);
    }

    internal sealed class MetadataFactory(string connection, params IInterceptor[] observers) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(observers).Options);
    }

    private sealed class RootComparisonDeadline(int seconds, Action<string> trace) : DbCommandInterceptor
    {
        private readonly Dictionary<int, (string Sql, DbParameter[] Parameters)> _examples = [];
        private static bool IsComparison(DbCommand command) => command.Parameters.Cast<DbParameter>()
            .Any(value => value.ParameterName == "@rootProofCandidate");

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // A stricter disposable-test budget exposes a whole-root command
            // even on a fast test server. Production's 30-second limit is unchanged.
            if (IsComparison(command))
            {
                command.CommandTimeout = seconds;
                var phase = (int)command.Parameters["@rootProofPhase"].Value!;
                lock (_examples)
                    if (!_examples.ContainsKey(phase))
                        _examples.Add(phase, (command.CommandText, command.Parameters.Cast<DbParameter>()
                            .Select(value => (DbParameter)((ICloneable)value).Clone()).ToArray()));
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (IsComparison(command)) trace($"Root membership command: {eventData.Duration.TotalSeconds:F3}s; budget {seconds}s.");
            return ValueTask.FromResult(result);
        }

        public async Task VerifyCoveringPlansAsync(FluxKnowledgeDbContext context)
        {
            if (context.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
                await context.Database.OpenConnectionAsync();
            foreach (var phase in new[] { 0, 1 })
            {
                var example = _examples[phase];
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandTimeout = seconds;
                command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = "SET STATISTICS XML ON;\n" + example.Sql + ";\nSET STATISTICS XML OFF;";
                foreach (var parameter in example.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) { } // Read-only replay with sealed staging still present.
                XDocument? plan = null;
                while (await reader.NextResultAsync())
                    if (reader.FieldCount == 1 && await reader.ReadAsync()) plan = XDocument.Parse(reader.GetString(0));
                Assert.NotNull(plan);
                XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                var vectorOperators = plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(access =>
                    (string?)access.Element(ns + "Object")?.Attribute("Table") == "[Vectors]")).ToArray();
                Assert.NotEmpty(vectorOperators);
                Assert.All(vectorOperators, op => Assert.All(op.Elements().Select(access => access.Element(ns + "Object"))
                    .Where(value => (string?)value?.Attribute("Table") == "[Vectors]"), value =>
                        Assert.Equal("[IX_Vectors_PublicationMetadata]", (string?)value!.Attribute("Index"))));
                var reads = vectorOperators.Sum(op => op.Descendants(ns + "RunTimeCountersPerThread")
                    .Sum(value => long.Parse((string?)value.Attribute("ActualRowsRead") ?? (string?)value.Attribute("ActualRows") ?? "0",
                        System.Globalization.CultureInfo.InvariantCulture)));
                var stagedOperators = plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(access =>
                    (string?)access.Element(ns + "Object")?.Attribute("Table") == "[IndexCandidateMembers]")).ToArray();
                var stagedRows = stagedOperators.Sum(op => op.Descendants(ns + "RunTimeCountersPerThread")
                    .Sum(value => long.Parse((string?)value.Attribute("ActualRows") ?? "0",
                        System.Globalization.CultureInfo.InvariantCulture)));
                Assert.True(stagedRows > 0, "The actual equality plan must include non-empty sealed staging.");
                trace($"Phase {phase} actual equality plan: publication metadata index only; Vectors rows read {reads:N0}; staged rows {stagedRows:N0}; wide-row lookups 0.");
                if (Environment.GetEnvironmentVariable(SqlFixtureTimings.EnvironmentVariable) is { } directory)
                    await File.WriteAllTextAsync(Path.Combine(directory, $"root-proof-stage-present-phase-{phase}.sqlplan"), plan.ToString());
            }
        }
    }

    private sealed class WideScaleEmbeddingProvider : IEmbeddingProvider
    {
        public ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Model-free test bytes reproduce the production SQL row width.
            var values = new float[1024];
            values[0] = 1;
            return ValueTask.FromResult(new EmbeddingResult(values, "synthetic-root-scale-1024-v1"));
        }
    }

    internal sealed class MetadataAppendObserver : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE [IndexPublicationCandidates]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[LastAppendOrdinal]", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Entered.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch { await result.DisposeAsync(); throw; }
            }
            return result;
        }
    }

    private sealed class MetadataFenceObserver(Action<string>? trace = null) : DbCommandInterceptor
    {
        private int _armed;
        private Stopwatch? _timer;
        private string _phase = "";
        public TaskCompletionSource Entered { get; private set; } = NewSignal();
        public TaskCompletionSource WaitingWriter { get; private set; } = NewSignal();
        public TaskCompletionSource Release { get; private set; } = NewSignal();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm(string phase)
        {
            Entered = NewSignal(); WaitingWriter = NewSignal(); Release = NewSignal();
            _timer = Stopwatch.StartNew(); _phase = phase;
            Volatile.Write(ref _armed, 1);
        }
        private void TraceMutation(DbCommand command, string state)
        {
            if (_timer is null || !command.CommandText.Contains("DELETE", StringComparison.Ordinal)) return;
            var mutation = command.CommandText.Contains("[CurrentIndexMembers]", StringComparison.Ordinal) ? "ledger"
                : command.CommandText.Contains("[IndexCandidateMembers]", StringComparison.Ordinal) ? "worklist cleanup" : null;
            if (mutation is not null) trace?.Invoke($"{_phase}: {mutation} {state} at {_timer.Elapsed.TotalSeconds:F3}s.");
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { TraceMutation(command, "started"); return ValueTask.FromResult(result); }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            TraceMutation(command, "finished");
            if (command.CommandText.Contains("INSERT #RootEligibleArtifacts", StringComparison.Ordinal))
                trace?.Invoke($"Eligibility map ({_phase}): {result:N0} artifacts in {eventData.Duration.TotalSeconds:F3}s.");
            return ValueTask.FromResult(result);
        }
        private static bool IsFence(DbCommand command) => command.CommandText.Contains("[IndexState]", StringComparison.Ordinal)
            && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsFence(command) && Entered.Task.IsCompleted && !Release.Task.IsCompleted) WaitingWriter.TrySetResult();
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (IsFence(command) && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Entered.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch { await result.DisposeAsync(); throw; }
            }
            return result;
        }
    }
}
