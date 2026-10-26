using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceJournalCompletionIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var db = new SourceJournalStoreIntegrationTests.ContextFactory(fixture.ConnectionString).CreateDbContext();
        await db.SourceVolumeJournalCheckpoints.ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Completion_of_26406_seen_revisions_stays_inside_the_existing_query_budget_without_suppression()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.SeedAsync(26406);
        var rows = await scenario.RowsAsync();
        var seen = rows.Select(id => new SourceRevisionId(id)).ToHashSet();
        await scenario.CompleteAsync(seen);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(26406, await db.SourceRevisions.CountAsync(x => x.SourceRootId == scenario.Claim.SourceRoot.Id.Value));
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
    }

    [NativeSqlServerFact]
    public async Task Completion_advances_past_a_fully_seen_page_and_preserves_other_root_suppressed_and_derived_rows()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.SeedAsync(1537);
        var rows = await scenario.RowsAsync(); // SQL uniqueidentifier order, not Guid.CompareTo.
        var seen = rows.Where((_, index) => index < 512 || index % 2 == 0).Select(id => new SourceRevisionId(id)).ToHashSet();
        var expected = rows.Where(id => !seen.Contains(new(id))).ToHashSet();
        var suppressedAt = DateTimeOffset.UtcNow.AddDays(-1);
        Guid other, derived, suppressed;
        await using (var db = scenario.Factory.CreateDbContext())
        {
            other = await scenario.CreateOtherRootAsync();
            derived = Guid.NewGuid(); suppressed = Guid.NewGuid();
            db.SourceRevisions.AddRange(new SourceRevisionEntity { Id = derived, SourceRootId = scenario.Claim.SourceRoot.Id.Value,
                StableSourceIdentity = "derived-control", Revision = 1, CanonicalPath = Path.Combine(scenario.Files, "derived.txt"), ContentSha256 = new string('b', 64),
                Classification = "text", Extension = ".txt", OriginKind = 1, DiscoveredAtUtc = DateTimeOffset.UtcNow },
                new SourceRevisionEntity { Id = suppressed, SourceRootId = scenario.Claim.SourceRoot.Id.Value,
                    StableSourceIdentity = "suppressed-control", Revision = 1, CanonicalPath = Path.Combine(scenario.Files, "suppressed.txt"), ContentSha256 = new string('c', 64),
                    Classification = "text", Extension = ".txt", OriginKind = 0, DiscoveredAtUtc = DateTimeOffset.UtcNow, SuppressedAtUtc = suppressedAt });
            await db.SaveChangesAsync();
        }
        await scenario.SeedAsync(1, other);
        await scenario.CompleteAsync(seen);
        await using var after = scenario.Factory.CreateDbContext();
        var removed = await after.SourceRevisions.Where(x => x.SourceRootId == scenario.Claim.SourceRoot.Id.Value &&
            x.Id != derived && x.Id != suppressed && x.SuppressedAtUtc != null).Select(x => x.Id).ToArrayAsync();
        Assert.True(expected.SetEquals(removed));
        Assert.Null((await after.SourceRevisions.SingleAsync(x => x.Id == derived)).SuppressedAtUtc);
        Assert.Equal(suppressedAt, (await after.SourceRevisions.SingleAsync(x => x.Id == suppressed)).SuppressedAtUtc);
        Assert.False(await after.SourceRevisions.AnyAsync(x => x.SourceRootId == other && x.SuppressedAtUtc != null));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_stops_before_the_next_page_on_cancellation_or_changed_ownership(bool changedOwnership)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.SeedAsync(1025);
        var rows = await scenario.RowsAsync();
        using var cancelled = new CancellationTokenSource();
        scenario.SetPageHook(async (page, _) =>
        {
            if (page != 2) return;
            if (changedOwnership)
            {
                await using var db = scenario.Factory.CreateDbContext();
                await db.SourceRootConfigurations.Where(x => x.Id == scenario.Claim.SourceRoot.Id.Value)
                    .ExecuteUpdateAsync(x => x.SetProperty(r => r.ConfigurationRevision, r => r.ConfigurationRevision + 1));
            }
            else cancelled.Cancel();
        });
        if (changedOwnership)
            await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => scenario.CompleteAsync(new HashSet<SourceRevisionId>(), cancelled.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.CompleteAsync(new HashSet<SourceRevisionId>(), cancelled.Token));
        await using var inspect = scenario.Factory.CreateDbContext();
        var removed = await inspect.SourceRevisions.Where(x => x.SuppressedAtUtc != null).Select(x => x.Id).ToArrayAsync();
        Assert.True(rows.Take(512).ToHashSet().SetEquals(removed));
        Assert.True((await inspect.SourceRootScanStates.SingleAsync()).RecoveryRequired);
    }

    [NativeSqlServerFact]
    public async Task Completion_rechecks_absence_after_a_later_page_is_loaded()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.SeedAsync(1025);
        var rows = await scenario.RowsAsync();
        var survivor = rows[700];
        string path;
        await using (var db = scenario.Factory.CreateDbContext())
            path = await db.SourceRevisions.Where(x => x.Id == survivor).Select(x => x.CanonicalPath).SingleAsync();
        scenario.SetPageHook(async (page, _) =>
        {
            if (page == 2) await File.WriteAllTextAsync(path, "reappeared during completion");
        });
        await scenario.CompleteAsync(new HashSet<SourceRevisionId>());
        await using var inspect = scenario.Factory.CreateDbContext();
        Assert.Null((await inspect.SourceRevisions.SingleAsync(x => x.Id == survivor)).SuppressedAtUtc);
        Assert.Equal(1024, await inspect.SourceRevisions.CountAsync(x => x.SuppressedAtUtc != null));
    }

    [NativeSqlServerFact]
    public async Task Targeted_completion_advances_over_out_of_scope_pages_without_suppressing_them()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.FinishEmptyBaselineAsync();
        await scenario.SeedAsync(1537);
        var rows = await scenario.RowsAsync();
        var last = rows[^1];
        await using (var db = scenario.Factory.CreateDbContext())
        {
            await db.SourceRevisions.Where(x => x.Id == last).ExecuteUpdateAsync(x =>
                x.SetProperty(r => r.CanonicalPath, Path.Combine(scenario.Files, "tail", "missing.txt")));
            var state = await db.SourceRootScanStates.SingleAsync();
            db.SourcePendingChanges.Add(new() { Id = Guid.NewGuid(), SourceRootId = scenario.Claim.SourceRoot.Id.Value,
                RelativePath = "tail", PathFingerprint = SqlSourceChangeJournalStore.PathKey("tail"),
                IsSubtree = true, Generation = ++state.Generation, ReasonFlags = SourcePendingReasons.ScopedNamespace,
                LastContentUsn = 0, LastUnjournaledGeneration = 0, FirstObservedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await scenario.ClaimTargetedAsync();
        Assert.False(scenario.Plan.IsBaseline);
        Assert.Contains(scenario.Plan.Targets, x => x.RelativePath == "tail" && x.IsSubtree);
        await scenario.CompleteAsync(new HashSet<SourceRevisionId>());
        await using var inspect = scenario.Factory.CreateDbContext();
        var removed = Assert.Single(await inspect.SourceRevisions.Where(x => x.SuppressedAtUtc != null).Select(x => x.Id).ToArrayAsync());
        Assert.Equal(last, removed);
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _area = Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge", "CompletionQuery-" + Guid.NewGuid().ToString("N"));
        internal string Files => Path.Combine(_area, "Sources");
        internal Factory Factory { get; }
        internal ClaimedSourceScan Claim { get; private set; } = null!;
        internal SqlSourceIncrementalCoordinator Coordinator { get; private set; } = null!;
        internal SourceIncrementalPlan Plan { get; private set; } = null!;
        private ContentAddressedSourceArtifactStore _artifacts = null!;
        private NativeCorpusCommandService _commands = null!;
        private Scenario(string connection) => Factory = new(connection);
        internal static async Task<Scenario> CreateAsync(string connection)
        {
            var scenario = new Scenario(connection);
            try
            {
                Directory.CreateDirectory(scenario.Files);
                scenario._commands = new(new NativeOperationService(new SqlNativeOperationStore(scenario.Factory, TimeProvider.System), []),
                    new SqlNativeCorpusActionStore(scenario.Factory, new SourceRootPathPolicy(new LocalIngressOptions([scenario._area])), new LocalPrivateContentDisclosure()));
                await scenario.CreateRootAsync(scenario.Files);
                var control = new SqlSourceScanStore(scenario.Factory, TimeProvider.System);
                scenario.Claim = Assert.IsType<ClaimedSourceScan>(await control.ClaimNextReleasedAsync("completion-query", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
                scenario._artifacts = new(Path.Combine(scenario._area, "Retained"));
                scenario.SetPageHook(null);
                scenario.Plan = Assert.IsType<SourceIncrementalPlan>(await scenario.Coordinator.PrepareAsync(scenario.Claim.SourceRoot, scenario.Claim.ScanRequest, default));
                return scenario;
            }
            catch { await scenario.DisposeAsync(); throw; }
        }
        internal async Task<Guid> CreateOtherRootAsync()
        {
            var path = Path.Combine(_area, "Other"); Directory.CreateDirectory(path); await CreateRootAsync(path);
            await using var db = Factory.CreateDbContext();
            return await db.SourceRootConfigurations.Where(x => x.CanonicalPath == path).Select(x => x.Id).SingleAsync();
        }
        private async Task CreateRootAsync(string path)
        {
            var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new { path, displayName = "Completion query fixture", recursive = true, maximumFileBytes = 1024 }));
            var preview = await _commands.PreviewAsync(mutation, "test", default);
            await _commands.CommitAsync(mutation, preview.ConfirmationId, "completion-" + Guid.NewGuid().ToString("N"), "test", default);
        }
        internal async Task SeedAsync(int count, Guid? rootId = null)
        {
            await using var db = Factory.CreateDbContext(); db.Database.SetCommandTimeout(30);
            var root = rootId ?? Claim.SourceRoot.Id.Value;
            var prefix = rootId is null ? Files : Path.Combine(_area, "Other");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO SourceRevisions (Id,SourceRootId,StableSourceIdentity,Revision,ContentSha256,CanonicalPath,Classification,Extension,OriginKind,ByteLength,DiscoveredAtUtc)
                SELECT NEWID(),{root},CONCAT(N'candidate-',n),1,REPLICATE('a',64),CONCAT({prefix},N'\candidate-',n,N'.txt'),N'text',N'.txt',0,0,{DateTimeOffset.UtcNow}
                FROM (SELECT TOP({count}) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) n FROM sys.all_objects a CROSS JOIN sys.all_objects b) numbers;
                """);
        }
        internal async Task<Guid[]> RowsAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.SourceRevisions.Where(x => x.SourceRootId == Claim.SourceRoot.Id.Value).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        }
        internal void SetPageHook(Func<int, CancellationToken, ValueTask>? hook) =>
            Coordinator = new(Factory, TimeProvider.System, new WindowsSourceChangeJournalReader(),
                new WindowsSourceObservationReader(), _artifacts, new() { Enabled = true,
                    ApprovedVolumes = [WindowsSourceChangeJournalReader.CanonicalVolume(Files)] }) { BeforeCompletionPage = hook };
        internal async Task CompleteAsync(IReadOnlySet<SourceRevisionId> seen, CancellationToken ct = default) =>
            _ = await Coordinator.CompleteAsync(Claim.SourceRoot, Claim.ScanRequest, Plan, [], [], seen, null, ct);
        internal async Task FinishEmptyBaselineAsync()
        {
            await CompleteAsync(new HashSet<SourceRevisionId>());
            await new SqlSourceScanStore(Factory, TimeProvider.System).CompleteAsync(Claim,
                new(Claim.SourceRoot.Id, Claim.ScanRequest.Id, 0, 0, 0, 0) { IsCompleteInventory = false, CompletesReconciliation = true }, null, default);
        }
        internal async Task ClaimTargetedAsync()
        {
            var mutation = new NativeCorpusMutation("source_sync", JsonSerializer.SerializeToElement(new { rootId = Claim.SourceRoot.Id.Value }));
            var preview = await _commands.PreviewAsync(mutation, "test", default);
            await _commands.CommitAsync(mutation, preview.ConfirmationId, "targeted-" + Guid.NewGuid().ToString("N"), "test", default);
            Claim = Assert.IsType<ClaimedSourceScan>(await new SqlSourceScanStore(Factory, TimeProvider.System)
                .ClaimNextReleasedAsync("completion-targeted", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
            Plan = Assert.IsType<SourceIncrementalPlan>(await Coordinator.PrepareAsync(Claim.SourceRoot, Claim.ScanRequest, default));
        }
        public ValueTask DisposeAsync()
        {
            _artifacts?.Dispose();
            var path = Path.GetFullPath(_area);
            if (!path.StartsWith(@"I:\Codex Workspaces\Staging\FluxKnowledge\CompletionQuery-", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe fixture cleanup.");
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Factory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection, sql => sql.CommandTimeout(3)).Options);
    }
}
