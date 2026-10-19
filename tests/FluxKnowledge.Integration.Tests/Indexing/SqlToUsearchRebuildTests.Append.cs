using System.Security.Cryptography;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SqlToUsearchRebuildTests
{
    [NativeSqlServerTheory]
    [InlineData("current")]
    [InlineData("unvalidated")]
    [InlineData("stale-stamp")]
    [InlineData("missing-membership")]
    public async Task Publication_snapshot_supplies_only_an_exact_validated_current_base(string state)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Fenced immutable publication baseline.");
        var baseline = await environment.ActiveGenerationAsync();
        await using (var change = await environment.Factory.CreateDbContextAsync())
        {
            var generation = await change.IndexGenerations.SingleAsync(value => value.Id == baseline.Id);
            switch (state)
            {
                case "unvalidated": generation.ValidatedAtUtc = null; break;
                case "stale-stamp": (await change.IndexState.SingleAsync()).CorpusVersion++; break;
                case "missing-membership": change.IndexGenerationVectors.Remove(await change.IndexGenerationVectors.FirstAsync(value => value.GenerationId == baseline.Id)); break;
            }
            await change.SaveChangesAsync();
        }
        var snapshot = await environment.Store.ReadPublicationSnapshotAsync(Guid.NewGuid(), CancellationToken.None);

        if (state != "current") Assert.Null(snapshot.AppendBase);
        else
        {
            Assert.NotNull(snapshot.AppendBase);
            Assert.Equal(baseline, snapshot.AppendBase.Generation);
            Assert.Equal(snapshot.ExpectedCorpusStamp, snapshot.AppendBase.Generation.CorpusStamp);
            Assert.Equal(snapshot.Vectors.Select(value => value.VectorId), snapshot.AppendBase.VectorIds);
        }
    }

    [NativeSqlServerFact]
    public async Task Append_reuse_publishes_through_dispatch_worker_activation_and_supported_lexical_readback()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "Previous published content.", appendReuse: true);
        var baseline = await environment.ActiveGenerationAsync();
        var vectors = await environment.Store.ReadVectorsAsync(baseline.Id, CancellationToken.None);
        var baseFile = Path.Combine(baseline.IndexPath, UsearchGenerationValidator.IndexFileName);
        using (var distinctive = new USearchIndex(MetricKind.Cos, ScalarKind.Float32, (ulong)baseline.Dimensions, 32, 0, 0, false))
        {
            foreach (var vector in vectors)
            {
                var values = new float[vector.Dimensions];
                Buffer.BlockCopy(vector.Values, 0, values, 0, vector.Values.Length);
                distinctive.Add((ulong)vector.VectorId, values);
            }
            distinctive.Save(baseFile);
        }
        var before = SHA256.HashData(File.ReadAllBytes(baseFile));

        await environment.AddAndPumpAtPathAsync("APPEND-VERIFIED-20261019 new available content.", "appended.txt");

        var recordId = environment.LastReceipt!.PipelineRecordId.Value;
        var active = await environment.ActiveGenerationAsync();
        Assert.NotEqual(baseline.Id, active.Id);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(baseFile)));
        using (var reopened = new USearchIndex(Path.Combine(active.IndexPath, UsearchGenerationValidator.IndexFileName), false))
            Assert.Equal(32UL, reopened.Connectivity());
        await using var verify = await environment.Factory.CreateDbContextAsync();
        Assert.True((await verify.PipelineRecords.SingleAsync(value => value.Id == recordId)).CompletionCriteriaMet);
        Assert.Equal(5, await verify.Jobs.CountAsync(value => value.PipelineRecordId == recordId && value.PublicState == (int)PublicJobState.Completed));
        Assert.All(await verify.Jobs.Where(value => value.PipelineRecordId == recordId).ToArrayAsync(), value => Assert.Null(value.LeaseOwner));
        var reader = new FluxKnowledge.Infrastructure.SqlServer.Search.SqlCorpusRetrievalReader(environment.Factory);
        var scope = await reader.ResolveScopeAsync("all", null, null, CancellationToken.None);
        Assert.NotNull(scope);
        Assert.NotEmpty(await reader.SearchAsync("APPEND-VERIFIED-20261019", scope, 5, CancellationToken.None));
        new UsearchGenerationValidator().Validate(active.IndexPath, active,
            await environment.Store.ReadVectorsAsync(active.Id, CancellationToken.None));
    }
}
