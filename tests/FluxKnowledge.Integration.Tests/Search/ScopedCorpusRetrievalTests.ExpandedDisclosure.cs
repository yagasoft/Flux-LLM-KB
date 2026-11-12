using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class ScopedCorpusRetrievalTests
{
    [NativeSqlServerFact]
    public async Task Expanded_disclosure_reads_two_hundred_fifty_independent_bindings_and_refuses_forgery_or_withdrawal()
    {
        var setup = SqlTestData.CreateFactory(fixture); var root = Guid.NewGuid();
        var bindings = new List<CorpusEvidenceBinding>();
        await using (var db = await setup.CreateDbContextAsync())
        {
            AddPublishedText(db, root, @"C:\expanded-disclosure", "ordinary.txt",
                Enumerable.Range(1, 250).Select(i => $"Evidence paragraph {i:D3}.\n").ToArray());
            await db.SaveChangesAsync();
            var source = Assert.Single(db.SourceRevisions.Local);
            var record = Assert.Single(db.PipelineRecords.Local);
            var artifact = Assert.Single(db.Artifacts.Local);
            var epoch = (await db.IndexState.SingleAsync(s => s.Id == 1)).CorpusEpoch;
            foreach (var chunk in db.TextChunks.Local.OrderBy(c => c.Ordinal))
                bindings.Add(new(2, root, source.Id, Hash(source.CanonicalPath), record.Id, record.Revision,
                    artifact.Id, artifact.ContentHash, chunk.Id, chunk.ContentHash, chunk.StartOffset, chunk.Length, epoch));
        }
        var counter = new DisclosureStatementCounter();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(counter).Options;
        ICorpusRetrievalReader reader = new SqlCorpusRetrievalReader(new DisclosureBatchFactory(options));
        var current = await reader.ReadExpandedForDisclosureAsync(bindings, default);
        Assert.Equal(250, current.Count); Assert.InRange(counter.Reads, 1, 4);
        for (var index = 0; index < bindings.Count; index++)
        {
            var read = current[bindings[index].ChunkId];
            Assert.Equal($"Evidence paragraph {index + 1:D3}.\n", read.Text);
            Assert.Equal(bindings[index].CitedStart, read.StartOffset);
            Assert.NotNull(read.DisclosureText); Assert.Null(read.DocumentMetadataJson);
        }
        Assert.Empty(await reader.ReadExpandedForDisclosureAsync([bindings[^1] with { RootId = Guid.NewGuid() }], default));
        await using (var db = await setup.CreateDbContextAsync())
        {
            (await db.SourceRevisions.FindAsync(bindings[0].OwnerSourceRevisionId))!.SuppressedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Empty(await reader.ReadExpandedForDisclosureAsync(bindings, default));
    }

    [NativeSqlServerFact]
    public async Task Normal_two_hundred_and_expanded_two_hundred_fifty_limits_refuse_before_SQL()
    {
        var counter = new DisclosureStatementCounter();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(counter).Options;
        ICorpusRetrievalReader reader = new SqlCorpusRetrievalReader(new DisclosureBatchFactory(options));
        var prototype = new CorpusEvidenceBinding(2, null, null, Hash("source"), Guid.NewGuid(), 1, Guid.NewGuid(), Hash("artifact"),
            1, Hash("body"), 0, 4, Guid.NewGuid());
        var unique = Enumerable.Range(1, 251).Select(id => prototype with { ChunkId = id }).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadForDisclosureAsync(unique.Take(201).ToArray(), default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadExpandedForDisclosureAsync(unique, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadExpandedForDisclosureAsync([prototype, prototype], default).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadExpandedForDisclosureAsync([prototype], cancelled.Token).AsTask());
        Assert.Equal(0, counter.Reads);
    }
}
