using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceRetirementCandidateTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_obsolete_active_candidates_are_read_amid_unrelated_eligible_history(bool mixedActive)
    {
        await using var scenario = await DeletedSourceRecoveryIntegrationTests.Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "retirement fixture", StableKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = DateTimeOffset.UtcNow };
        db.SourceIdentities.Add(identity);
        db.PipelineRecords.AddRange(Enumerable.Range(1, 6000).Select(i =>
        {
            var id = Guid.NewGuid();
            var input = new SourceRevisionEntity { Id = Guid.NewGuid(), SourceRootId = scenario.Claim.SourceRoot.Id.Value,
                StableSourceIdentity = "retirement-history:" + i, Revision = 1, ContentSha256 = new string('a', 64),
                CanonicalPath = Path.Combine(scenario.Files, "history", i + ".txt"), Extension = ".txt", DiscoveredAtUtc = DateTimeOffset.UtcNow };
            db.SourceRevisions.Add(input);
            return new PipelineRecordEntity { Id = id, SourceIdentityId = identity.Id, SourceRevisionId = input.Id, Revision = i, ContentHash = new string('a', 64),
                RootLineageRecordId = id, RegisteredAtUtc = DateTimeOffset.UtcNow };
        }));
        await db.SaveChangesAsync();
        Guid? obsoleteTask = null;
        if (mixedActive)
        {
            var keeper = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt")).Select(x => x.Id).SingleAsync();
            var removed = await db.SourceRevisions.Where(x => x.CanonicalPath == Path.Combine(scenario.Files, "removed", "one.txt")).Select(x => x.Id).SingleAsync();
            var keeperJob = await scenario.QueueAsync(keeper); var removedJob = await scenario.QueueAsync(removed);
            await db.Jobs.Where(x => x.Id == keeperJob || x.Id == removedJob)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PublicState, (int)PublicJobState.GpuQueued));
            GpuMiniTaskEntity Task(Guid parent) => new() { Id = Guid.NewGuid(), ParentJobId = parent, SourceRevision = 1,
                PriorityLane = (int)GpuPriorityLane.DocumentIndexing, ModelRuntimeKey = "retirement-fixture", SettingsFingerprint = new string('b', 64),
                EstimatedBytes = 1, IdempotencyKey = Guid.NewGuid().ToString("N"), ExecutionState = (int)GpuMiniTaskExecutionState.Ready, CreatedAtUtc = DateTimeOffset.UtcNow };
            db.GpuMiniTasks.AddRange(Enumerable.Range(0, 128).Select(_ => Task(keeperJob))); await db.SaveChangesAsync();
            var last = Task(removedJob); obsoleteTask = last.Id; db.GpuMiniTasks.Add(last);
            db.GpuCapacitySlots.Add(new GpuCapacitySlotEntity { SlotKey = "gpu-0", State = 0, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var scheduler = new SqlGpuSchedulerStore(scenario.Factory, sourceAdmission: scenario.Admission);
            await scheduler.RunAdmissionRoundAsync(Guid.NewGuid(), GpuSchedulerWakeReason.WorkReady,
                new(256, 4096, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)),
                (_, _) => ValueTask.FromResult(new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, "gpu-0", "fixture", null, "fixture-executor")), default);
            Assert.Equal(129, await db.GpuMiniTasks.CountAsync(x => x.ExecutionState == (int)GpuMiniTaskExecutionState.Active));
            await db.SourceRevisions.Where(x => x.Id == removed).ExecuteUpdateAsync(set => set.SetProperty(x => x.SuppressedAtUtc, DateTimeOffset.UtcNow));
        }
        var query = SqlSourceWorkRetirement.UnavailableActiveTaskIds(db);
        output.WriteLine("CANDIDATE SQL:\n" + query.ToQueryString());
        await db.Database.OpenConnectionAsync();
        var statistics = new List<string>();
        var connection = (SqlConnection)db.Database.GetDbConnection();
        connection.InfoMessage += (_, message) => statistics.Add(message.Message);
        await db.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON;");
        var found = await query.ToArrayAsync();
        await db.Database.ExecuteSqlRawAsync("SET STATISTICS IO OFF;");
        output.WriteLine("CANDIDATE IO:\n" + string.Join('\n', statistics));
        var reads = statistics.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
            .Sum(match => long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        // Leave headroom for index depth, while rejecting a scan of the 6,000
        // unrelated records. This measures work rather than machine speed.
        Assert.InRange(reads, 1, mixedActive ? 4096 : 64);
        Assert.Equal(mixedActive ? [obsoleteTask!.Value] : Array.Empty<Guid>(), found);
        Assert.Equal(mixedActive ? 129 : 0, await db.GpuMiniTasks.CountAsync(x => x.ExecutionState == (int)GpuMiniTaskExecutionState.Active));
    }

}
