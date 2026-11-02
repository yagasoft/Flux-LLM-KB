using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SegmentedSelectionScaleTests
{
    [NativeSqlServerTheory]
    [InlineData(0, "equal")]
    [InlineData(1, "equal")]
    [InlineData(0, "checksum")]
    [InlineData(1, "checksum")]
    [InlineData(0, "identity")]
    [InlineData(1, "identity")]
    [InlineData(0, "owner-null")]
    [InlineData(1, "owner-null")]
    [InlineData(0, "root-null")]
    [InlineData(1, "root-null")]
    [InlineData(0, "canonical-only")]
    [InlineData(1, "canonical-only")]
    [InlineData(0, "staged-only")]
    [InlineData(1, "staged-only")]
    [InlineData(0, "extra-tail")]
    [InlineData(1, "extra-tail")]
    [InlineData(0, "extra-hole")]
    [InlineData(1, "extra-hole")]
    [InlineData(0, "cancel")]
    [InlineData(1, "cancel")]
    public async Task Root_membership_comparison_exhausts_sparse_windows_and_rejects_late_differences(int phase, string difference)
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "Unrelated raw keys create empty root windows.", segmented: true);
        var record = await environment.AddRetainedAndPumpAsync("The rooted survivor crosses a raw-key gap.");
        await using var seed = await environment.Factory.CreateDbContextAsync();
        seed.Database.SetCommandTimeout(180); // Disposable seeding only.
        var unrelated = await seed.CurrentIndexMembers.Where(value => value.SourceRootId == null)
            .Select(value => value.VectorId).FirstAsync();
        var source = await seed.PipelineRecords.Where(value => value.Id == record).Select(value => value.SourceIdentityId).SingleAsync();
        var rooted = await seed.CurrentIndexMembers.Where(value => value.SourceIdentityId == source).SingleAsync();
        await SeedUnrelatedAsync(seed, unrelated, 17_000);
        await SeedUnrelatedAsync(seed, rooted.VectorId, 2);
        await SeedUnrelatedAsync(seed, unrelated, 17_000);
        var root = rooted.SourceRootId!.Value;
        var late = await seed.CurrentIndexMembers.Where(value => value.SourceRootId == root).MaxAsync(value => value.VectorId);
        Assert.True(late > 16_384);
        var state = await seed.IndexState.AsNoTracking().SingleAsync();
        var expected = await seed.CurrentIndexMembers.LongCountAsync(value => value.SourceRootId == root);
        var candidate = new IndexPublicationCandidateEntity
        {
            Id = Guid.NewGuid(), CorpusEpoch = state.CorpusEpoch, BaseGenerationId = state.ActiveIndexGenerationId,
            ExpectedCorpusVersion = state.CorpusVersion, PublicationCorpusVersion = state.CorpusVersion,
            AffectedScopesJson = JsonSerializer.Serialize(new[] { new IndexAffectedScope(3, root) }),
            BeforeCount = expected, AfterCount = expected, BeforeChecksum = new('0', 64), AfterChecksum = new('0', 64),
            OwnerMachineFingerprint = new('0', 64), CreatedAtUtc = DateTimeOffset.UtcNow, OwnerStartedAtUtc = DateTimeOffset.UtcNow,
            WorklistComplete = true
        };
        seed.IndexPublicationCandidates.Add(candidate);
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT IndexCandidateMembers(CandidateId,Phase,VectorId,PayloadChecksum,SourceIdentityId,OwnerSourceRevisionId,SourceRootId)
            SELECT {candidate.Id},{phase},member.VectorId,vector.PayloadChecksum,
                member.SourceIdentityId,member.OwnerSourceRevisionId,member.SourceRootId
            FROM CurrentIndexMembers member JOIN Vectors vector ON vector.VectorId=member.VectorId
            WHERE member.SourceRootId={root};
            """);
        var staged = seed.IndexCandidateMembers.Where(value => value.CandidateId == candidate.Id && value.Phase == phase && value.VectorId == late);
        if (difference == "checksum") await staged.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PayloadChecksum, new string('A', 64)));
        if (difference == "identity") await staged.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceIdentityId, Guid.NewGuid()));
        if (difference == "owner-null") await staged.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.OwnerSourceRevisionId, (Guid?)null));
        if (difference == "root-null") await staged.ExecuteUpdateAsync(setters => setters.SetProperty(value => value.SourceRootId, (Guid?)null));
        if (difference == "canonical-only") await staged.ExecuteDeleteAsync();
        if (difference == "staged-only")
        {
            await seed.CurrentIndexMembers.Where(value => value.VectorId == late).ExecuteDeleteAsync();
            await seed.Vectors.Where(value => value.VectorId == late).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.IsDeleted, true));
        }
        if (difference is "extra-tail" or "extra-hole")
        {
            var extra = difference == "extra-tail" ? await seed.Vectors.MaxAsync(value => value.VectorId) + 11 : late + 1;
            if (difference == "extra-hole")
            {
                await seed.CurrentIndexMembers.Where(value => value.VectorId == extra).ExecuteDeleteAsync();
                await seed.IndexSegmentVectors.Where(value => value.VectorId == extra).ExecuteDeleteAsync();
                await seed.Vectors.Where(value => value.VectorId == extra).ExecuteDeleteAsync();
                Assert.False(await seed.Vectors.AnyAsync(value => value.VectorId == extra));
            }
            var checksum = await seed.Vectors.Where(value => value.VectorId == rooted.VectorId).Select(value => value.PayloadChecksum).SingleAsync();
            seed.IndexCandidateMembers.Add(new()
            {
                CandidateId = candidate.Id, Phase = phase, VectorId = extra, PayloadChecksum = checksum,
                SourceIdentityId = rooted.SourceIdentityId, OwnerSourceRevisionId = rooted.OwnerSourceRevisionId, SourceRootId = root
            });
            await seed.SaveChangesAsync();
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var observer = new CancelRootComparison(cancellation, difference == "cancel");
        await using var comparison = new MetadataFactory(fixture.ConnectionString, observer).CreateDbContext();
        await using var transaction = await comparison.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlPublishedPassageSelection.AcquireFenceAsync(comparison, cancellation.Token);
        // Isolate the actual SQL equality proof from the outer staging digest,
        // which would otherwise reject deliberately poisoned staging first.
        var method = typeof(SqlSegmentedIndexStore).GetMethod("RootMembershipMatchesAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var operation = Assert.IsAssignableFrom<Task<bool>>(method.Invoke(null, [comparison, candidate, phase, cancellation.Token]));
        if (difference == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else Assert.Equal(difference == "equal", await operation);
        if (difference != "cancel")
            Assert.Null(await comparison.Database.SqlQuery<int?>($"SELECT OBJECT_ID(N'tempdb..#RootEligibleArtifacts') AS [Value]").SingleAsync());
        await transaction.RollbackAsync();
        Assert.Equal(state.ActiveIndexGenerationId, await seed.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(0, await seed.IndexPublicationCandidates.Where(value => value.Id == candidate.Id).Select(value => value.State).SingleAsync());
    }

    private sealed class CancelRootComparison(CancellationTokenSource cancellation, bool enabled) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (enabled && command.Parameters.Cast<DbParameter>().Any(value => value.ParameterName == "@rootProofCandidate"))
                cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
