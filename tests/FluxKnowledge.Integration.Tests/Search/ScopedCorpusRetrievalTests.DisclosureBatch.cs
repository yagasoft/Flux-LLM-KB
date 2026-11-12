using System.Data.Common;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed partial class ScopedCorpusRetrievalTests
{
    [NativeSqlServerFact]
    public async Task Disclosure_preflight_matches_native_windows_in_a_bounded_batch_without_citation_metadata()
    {
        var setup = SqlTestData.CreateFactory(fixture);
        var root = Guid.NewGuid();
        var bindings = new List<CorpusEvidenceBinding>();
        await using (var db = await setup.CreateDbContextAsync())
        {
            AddPublishedText(db, root, @"C:\disclosure-batch", "ordinary.txt", ["Earlier line.\nA complete answer.\nLater line."], "{\"test\":true}");
            AddPublishedText(db, root, @"C:\disclosure-batch", "long-line.txt", Enumerable.Repeat(new string('x', 1000), 30).ToArray());
            AddPublishedText(db, root, @"C:\disclosure-batch", "credential.txt", ["password=", new string('A', 1000), new string('A', 1000), "\nA public answer.\n"]);
            await db.SaveChangesAsync();
            var epoch = (await db.IndexState.SingleAsync(state => state.Id == 1)).CorpusEpoch;
            foreach (var file in new[] { ("ordinary.txt", 0), ("long-line.txt", 16), ("credential.txt", 3) })
            {
                var source = db.SourceRevisions.Local.Single(item => item.CanonicalPath.EndsWith(file.Item1, StringComparison.Ordinal));
                var record = db.PipelineRecords.Local.Single(item => item.SourceRevisionId == source.Id);
                var artifact = db.Artifacts.Local.Single(item => item.PipelineRecordId == record.Id);
                var chunk = db.TextChunks.Local.Single(item => item.ArtifactId == artifact.Id && item.Ordinal == file.Item2);
                bindings.Add(new(2, root, source.Id, Hash(source.CanonicalPath), record.Id, record.Revision,
                    artifact.Id, artifact.ContentHash, chunk.Id, chunk.ContentHash, chunk.StartOffset, chunk.Length, epoch));
            }
        }
        ICorpusRetrievalReader serial = new SqlCorpusRetrievalReader(setup);
        var expected = new List<EligibleContext>();
        foreach (var binding in bindings) expected.Add(Assert.IsType<EligibleContext>(await serial.ReadAsync(binding, 0, default)));
        Assert.Null(expected[1].DisclosureText);

        var counter = new DisclosureStatementCounter();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(counter).Options;
        ICorpusRetrievalReader batch = new SqlCorpusRetrievalReader(new DisclosureBatchFactory(options));
        var current = await batch.ReadForDisclosureAsync(bindings, default);
        Assert.InRange(counter.Reads, 1, 4);
        Assert.Equal(bindings.Count, current.Count);
        for (var index = 0; index < bindings.Count; index++)
        {
            var read = current[bindings[index].ChunkId]; var old = expected[index];
            Assert.Equal(old.Candidate, read.Candidate);
            Assert.Equal(old.StartOffset, read.StartOffset);
            Assert.Equal(old.Text, read.Text);
            Assert.Equal(old.DisclosureText, read.DisclosureText);
            Assert.Null(read.DocumentMetadataJson);
        }
        var forged = bindings[0] with { SourceIdentityHash = Hash("another path") };
        Assert.Empty(await batch.ReadForDisclosureAsync([forged], default));
        await using (var db = await setup.CreateDbContextAsync())
        {
            (await db.SourceRevisions.FindAsync(bindings[0].OwnerSourceRevisionId))!.SuppressedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Empty(await batch.ReadForDisclosureAsync([bindings[0]], default));
    }

    [NativeSqlServerFact]
    public async Task Disclosure_preflight_rejects_oversized_or_cancelled_batches_before_SQL()
    {
        var counter = new DisclosureStatementCounter();
        var options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).AddInterceptors(counter).Options;
        ICorpusRetrievalReader reader = new SqlCorpusRetrievalReader(new DisclosureBatchFactory(options));
        var binding = new CorpusEvidenceBinding(2, null, null, Hash("source"), Guid.NewGuid(), 1, Guid.NewGuid(), Hash("artifact"),
            1, Hash("body"), 0, 4, Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadForDisclosureAsync(Enumerable.Repeat(binding, 201).ToArray(), default).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadForDisclosureAsync([binding], cancelled.Token).AsTask());
        Assert.Equal(0, counter.Reads);
    }

    private sealed class DisclosureBatchFactory(DbContextOptions<FluxKnowledgeDbContext> options) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }
    private sealed class DisclosureStatementCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Reads++; return ValueTask.FromResult(result); }
    }
}
