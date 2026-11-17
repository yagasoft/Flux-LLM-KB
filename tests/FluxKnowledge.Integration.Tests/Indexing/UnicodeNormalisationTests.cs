using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class UnicodeNormalisationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(0xfffe)]
    [InlineData(0xffff)]
    public async Task Retained_valid_utf8_noncharacters_survive_normalisation_and_reach_durable_publication(int codePoint)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Bootstrap.");
        var text = "Noncharacter regression: e\u0301" + char.ConvertFromUtf32(codePoint) + "\u2460\r\nFinal line.";
        var record = await environment.AddRetainedAndPumpAsync(text);
        await using var db = environment.Factory.CreateDbContext();
        Assert.True((await db.PipelineRecords.SingleAsync(x => x.Id == record)).CompletionCriteriaMet);
        var extract = await db.Artifacts.SingleAsync(x => x.PipelineRecordId == record && x.Stage == 1);
        var canonical = await db.Artifacts.SingleAsync(x => x.PipelineRecordId == record && x.Stage == 3);
        Assert.Equal(text, extract.SearchText);
        Assert.Equal("Noncharacter regression: é" + char.ConvertFromUtf32(codePoint) + "1\nFinal line.", canonical.SearchText);
        Assert.NotEmpty(await db.Vectors.Where(x => x.TextChunk.ArtifactId == canonical.Id && !x.IsDeleted).ToArrayAsync());
    }
}
