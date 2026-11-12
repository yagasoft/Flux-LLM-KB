using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class CapturedGenerationIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Unavailable_windows_cannot_crowd_a_returnable_answer_out_of_semantic_top100()
    {
        const string answer = "A returnable answer with a complete surrounding line.";
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(
            fixture, new string('x', 120000), segmented: true);
        await environment.AddAndPumpAsync(answer);
        byte[] payload;
        long answerId;
        long answerVectorId;
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
            Assert.True(await db.Vectors.CountAsync() > 100);
            payload = await db.Vectors.OrderBy(vector => vector.VectorId).Select(vector => vector.Values).FirstAsync();
            answerId = await db.TextChunks.Where(chunk => chunk.Content == answer).Select(chunk => chunk.Id).SingleAsync();
            answerVectorId = await db.Vectors.Where(vector => vector.TextChunkId == answerId).Select(vector => vector.VectorId).SingleAsync();
        }
        var query = new float[payload.Length / sizeof(float)];
        Buffer.BlockCopy(payload, 0, query, 0, payload.Length);
        using var services = environment.Services.CreateScope();
        var captured = await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System));
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>()
            .OpenAsync(captured, default);
        var manifest = await services.ServiceProvider.GetRequiredService<ISegmentedIndexStore>().ReadManifestAsync(ann.Generation.Id, default);
        var unfiltered = await ann.SearchAsync(query, 100, default);
        Assert.Equal(100, unfiltered.Count);
        Assert.DoesNotContain(unfiltered, match => match.VectorId == answerVectorId);
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        var eligible = new HashSet<long>();
        foreach (var segment in manifest.Segments)
            eligible.UnionWith(await reader.ReadSegmentKeysAsync(captured, new("all", [], null), segment, default));
        Assert.Single(eligible);
        await ann.PrepareAsync(reader, default);
        var result = await reader.ReadDenseCandidatesAsync(ann, new("all", [], null), query, default);
        Assert.Equal("ready", result.Status);
        Assert.Equal(answerId, Assert.Single(result.Candidates).ChunkId);
    }

    [NativeSqlServerFact]
    public async Task An_unavailable_window_does_not_hide_a_corrupt_member_from_preparation()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(
            fixture, new string('x', 40000), segmented: true);
        await using (var db = await environment.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE v SET SearchInputHash = c.SearchInputHash FROM Vectors v INNER JOIN TextChunks c ON c.Id = v.TextChunkId");
            var vector = await db.Vectors.OrderBy(value => value.VectorId).FirstAsync();
            vector.PayloadChecksum = new string('f', 64);
            await db.SaveChangesAsync();
        }
        var reader = new SqlCorpusRetrievalReader(environment.Factory);
        using var services = environment.Services.CreateScope();
        await using var ann = await services.ServiceProvider.GetRequiredService<ICorpusAnnLeaseFactory>()
            .OpenAsync(await Acquire(new SqlCorpusGenerationLeaseStore(environment.Factory, TimeProvider.System)), default);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ann.PrepareAsync(reader, default).AsTask());
        Assert.Equal("segment-binding-membership-invalid", error.Message);
        Assert.False(ann.IsPrepared(reader));
    }
}
