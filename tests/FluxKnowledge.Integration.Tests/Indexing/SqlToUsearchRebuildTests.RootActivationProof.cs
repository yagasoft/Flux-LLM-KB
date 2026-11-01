using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerTheory]
    [InlineData("owner-null")]
    [InlineData("checksum-case")]
    [InlineData("checksum-byte")]
    public async Task Root_activation_rejects_metadata_corruption_without_a_corpus_stamp_change(string corruption)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The independent keeper.", segmented: true);
        var record = await environment.AddRetainedAndPumpAsync("Exact metadata corruption must refuse root activation.");
        var (root, _) = await AddRootDebtAsync(environment, record);
        using var scope = environment.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, default);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var before = await db.IndexState.AsNoTracking().SingleAsync();
        var candidate = await db.IndexPublicationCandidates.SingleAsync(value => value.Id == snapshot.SegmentedCandidateId);
        var row = await db.IndexCandidateMembers.AsNoTracking().SingleAsync(value => value.CandidateId == candidate.Id && value.Phase == 0);
        if (corruption == "owner-null")
            await db.CurrentIndexMembers.Where(value => value.SourceRootId == root)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.OwnerSourceRevisionId, (Guid?)null));
        else
        {
            // Candidate staging has no hash-format constraint. Model a consistently
            // forged sealed receipt so the final comparison itself must reject it.
            var changed = corruption == "checksum-case" ? row.PayloadChecksum.ToUpperInvariant()
                : (row.PayloadChecksum[0] == '0' ? "1" : "0") + row.PayloadChecksum[1..];
            Assert.NotEqual(row.PayloadChecksum, changed);
            await db.IndexCandidateMembers.Where(value => value.CandidateId == candidate.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.PayloadChecksum, changed));
            var framed = string.Create(CultureInfo.InvariantCulture,
                $"{row.VectorId}|{changed}|{row.SourceIdentityId:N}|{row.OwnerSourceRevisionId:N}|{row.SourceRootId:N}\n");
            candidate.BeforeChecksum = candidate.AfterChecksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(framed)));
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>()
            .TryActivateRefreshAsync(snapshot, default).AsTask());
        Assert.Equal("segmented-refresh-proof-invalid", error.Message);
        Assert.Equal(before.CorpusVersion, await db.IndexState.Select(value => value.CorpusVersion).SingleAsync());
        Assert.Equal(before.ActiveIndexGenerationId, await db.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        Assert.Equal(1, await db.IndexPublicationCandidates.Where(value => value.Id == candidate.Id).Select(value => value.State).SingleAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Root_activation_updates_nullable_ownership_and_replays_after_staging_cleanup(bool nullToOwned)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "The plain metadata control.", segmented: true);
        var plain = environment.LastReceipt!.PipelineRecordId.Value;
        var retained = await environment.AddRetainedAndPumpAsync("The retained metadata control.");
        await using var db = await environment.Factory.CreateDbContextAsync();
        var owner = await db.PipelineRecords.Where(value => value.Id == retained).Select(value => value.SourceRevisionId!.Value).SingleAsync();
        var root = await db.SourceRevisions.Where(value => value.Id == owner).Select(value => value.SourceRootId).SingleAsync();
        var targetRecord = nullToOwned ? retained : plain;
        var identity = await db.PipelineRecords.Where(value => value.Id == targetRecord).Select(value => value.SourceIdentityId).SingleAsync();
        await db.CurrentIndexMembers.Where(value => value.SourceIdentityId == identity).ExecuteUpdateAsync(setters =>
            setters.SetProperty(value => value.OwnerSourceRevisionId, nullToOwned ? null : (Guid?)owner)
                .SetProperty(value => value.SourceRootId, (Guid?)root));
        await AddRootDebtAsync(environment, retained, await db.PipelineRecords.Where(value => value.Id == plain).Select(value => value.SourceIdentityId).SingleAsync());
        using var scope = environment.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>().BuildAndPlaceAsync(Guid.Empty, default);
        var store = scope.ServiceProvider.GetRequiredService<ISegmentedIndexStore>();
        Assert.True(await store.TryActivateRefreshAsync(snapshot, default));
        var result = await db.CurrentIndexMembers.AsNoTracking().SingleAsync(value => value.SourceIdentityId == identity);
        Assert.Equal<Guid?>(nullToOwned ? owner : null, result.OwnerSourceRevisionId);
        Assert.Equal<Guid?>(nullToOwned ? root : null, result.SourceRootId);
        Assert.False(await db.IndexCandidateMembers.AnyAsync(value => value.CandidateId == snapshot.SegmentedCandidateId));
        Assert.True(await store.TryActivateRefreshAsync(snapshot, default));
        await AssertSegmentedOracleAsync(environment);
    }

    private static async Task<(Guid Root, Guid Owner)> AddRootDebtAsync(PipelineEnvironment environment, Guid record, Guid? otherSource = null)
    {
        await using var db = await environment.Factory.CreateDbContextAsync();
        var owner = await db.PipelineRecords.Where(value => value.Id == record).Select(value => value.SourceRevisionId!.Value).SingleAsync();
        var root = await db.SourceRevisions.Where(value => value.Id == owner).Select(value => value.SourceRootId).SingleAsync();
        var state = await db.IndexState.SingleAsync();
        state.CorpusVersion++;
        db.IndexProjectionChanges.Add(new() { CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 3, ScopeId = root, CreatedAtUtc = DateTimeOffset.UtcNow });
        if (otherSource is { } source)
            db.IndexProjectionChanges.Add(new() { CorpusEpoch = state.CorpusEpoch, CorpusVersion = state.CorpusVersion, ScopeKind = 1, ScopeId = source, CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return (root, owner);
    }
}
