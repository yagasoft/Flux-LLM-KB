using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerFact]
    public async Task Segmented_readiness_catches_up_derived_membership_while_canonical_work_remains_held()
    {
        var hold = new RefreshValidationHold();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            "The independent keeper remains published during deployment validation.", segmented: true,
            configureServices: services => services.AddSingleton<IDeploymentValidationHold>(hold));
        var removedRecord = await environment.AddRetainedAndPumpAsync("The removed source must leave the derived projection.");
        Guid removedRoot;
        await using (var context = await environment.Factory.CreateDbContextAsync())
            removedRoot = await (from record in context.PipelineRecords
                join revision in context.SourceRevisions on record.SourceRevisionId equals revision.Id
                where record.Id == removedRecord select revision.SourceRootId).SingleAsync();
        await new SqlSourceScanStore(environment.Factory, TimeProvider.System).SuppressUnseenAsync(new SourceRootId(removedRoot),
            new HashSet<SourceRevisionId>(), CancellationToken.None);
        await environment.AddAndPumpAtPathAsync("This pending pipeline input cannot run while validation is held.", "pending.txt", pump: false);
        hold.IsHeld = true;
        var before = await HeldRefreshFingerprintAsync(environment);

        var coordinator = environment.Services.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await coordinator.RunOnceAsync(budget.Token);

        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.True(hold.IsHeld);
        await environment.PumpAsync();
        Assert.Equal(before, await HeldRefreshFingerprintAsync(environment));
        await AssertSegmentedOracleAsync(environment);
        await using var after = await environment.Factory.CreateDbContextAsync();
        Assert.False(await after.CurrentIndexMembers.AnyAsync(value => value.SourceRootId == removedRoot));
        Assert.True(await after.CurrentIndexMembers.AnyAsync());
        var state = await after.IndexState.SingleAsync();
        var active = await after.IndexGenerations.SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
        Assert.Equal(state.CorpusEpoch, active.CorpusEpoch);
        Assert.Equal(state.CorpusVersion, active.CorpusVersion);
    }

    private static async Task<string> HeldRefreshFingerprintAsync(PipelineEnvironment environment)
    {
        await using var context = await environment.Factory.CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        var fingerprints = new List<string>();
        foreach (var table in new[] { "SourceActivities", "SourceProcessorBranches", "SourceProcessorAttempts", "PipelineRecords", "Jobs", "OutboxMessages", "SourceRevisions", "Vectors" })
        {
            var key = table == "Vectors" ? "VectorId" : "Id";
            command.CommandText = $"SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max), (SELECT * FROM [{table}] ORDER BY [{key}] FOR JSON PATH, INCLUDE_NULL_VALUES))), 2)";
            fingerprints.Add(table + ":" + await command.ExecuteScalarAsync());
        }
        command.CommandText = "SELECT CONVERT(varchar(36), CorpusEpoch) + ':' + CONVERT(varchar(20), CorpusVersion) FROM IndexState WHERE Id=1";
        fingerprints.Add("canonical-stamp:" + await command.ExecuteScalarAsync());
        return string.Join("|", fingerprints);
    }

    [NativeSqlServerTheory]
    [InlineData("epoch")]
    [InlineData("version")]
    [InlineData("base")]
    [InlineData("maintenance")]
    [InlineData("draft")]
    public async Task Held_segmented_refresh_preserves_snapshot_and_draft_fences(string conflict)
    {
        var hold = new RefreshValidationHold();
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture,
            "The held refresh cannot publish a draft or activate stale metadata.", segmented: true,
            configureServices: services => services.AddSingleton<IDeploymentValidationHold>(hold));
        using var scope = environment.Services.CreateScope();
        var candidate = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>()
            .BuildAndPlaceAsync(Guid.Empty, CancellationToken.None);
        Guid? expectedActive;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var state = await context.IndexState.SingleAsync();
            switch (conflict)
            {
                case "epoch": state.CorpusEpoch = Guid.NewGuid(); break;
                case "version": state.CorpusVersion++; break;
                case "base": state.ActiveIndexGenerationId = null; break;
                case "maintenance":
                    var operationId = Guid.NewGuid();
                    context.CorpusRebuildOperations.Add(new()
                    {
                        Id = operationId, TargetEpoch = Guid.NewGuid(), ManifestHash = new string('a', 64),
                        ManifestJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow
                    });
                    state.CorpusRebuildOperationId = operationId;
                    break;
                case "draft":
                    var row = await context.IndexPublicationCandidates.SingleAsync(value => value.Id == candidate.SegmentedCandidateId);
                    row.DraftGenerationId = state.ActiveIndexGenerationId;
                    break;
            }
            await context.SaveChangesAsync();
            expectedActive = state.ActiveIndexGenerationId;
        }
        hold.IsHeld = true;
        var before = await HeldRefreshFingerprintAsync(environment);
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        if (conflict == "draft")
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryActivateRefreshAsync(candidate, CancellationToken.None).AsTask());
        else Assert.False(await store.TryActivateRefreshAsync(candidate, CancellationToken.None));
        Assert.True(hold.IsHeld);
        Assert.Equal(before, await HeldRefreshFingerprintAsync(environment));
        await using var after = await environment.Factory.CreateDbContextAsync();
        Assert.Equal(expectedActive, (await after.IndexState.AsNoTracking().SingleAsync()).ActiveIndexGenerationId);
        Assert.Equal(1, (await after.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == candidate.SegmentedCandidateId)).State);
    }

    private sealed class RefreshValidationHold : IDeploymentValidationHold
    {
        public bool IsHeld { get; set; }
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) =>
            IsHeld ? new(Task.Delay(Timeout.Infinite, cancellationToken)) : ValueTask.CompletedTask;
    }
}
