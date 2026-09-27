using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Cli;

[Collection("sql-full-text")]
public sealed class CorpusRebuildCommandTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Empty_operator_rebuild_preserves_an_exact_replayable_receipt_and_reports_completion()
    {
        await SqlTestData.ClearPhase3SourceDataAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var context = factory.CreateDbContext())
        {
            context.GpuCapacitySlots.Add(new GpuCapacitySlotEntity
                { SlotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        var builder = new PassageBuilder(new Tokenizer());
        var operation = Guid.NewGuid().ToString("D");
        async Task<(int Code, string Output, string Error)> Run(params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await CorpusRebuildCommand.ExecuteAsync(args, factory, builder, new UsearchGenerationValidator(), output, error);
            return (code, output.ToString(), error.ToString());
        }
        var planned = await Run("plan", "--operation", operation);
        Assert.Equal(0, planned.Code);
        Assert.Empty(planned.Error);
        var manifestPath = Path.Combine(Path.GetTempPath(), $"corpus-rebuild-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(manifestPath, planned.Output);
            var committed = await Run("commit", "--manifest", manifestPath);
            Assert.Equal(0, committed.Code);
            Assert.False(JsonDocument.Parse(committed.Output).RootElement.GetProperty("AlreadyCommitted").GetBoolean());
            var replay = await Run("commit", "--manifest", manifestPath);
            Assert.Equal(0, replay.Code);
            Assert.True(JsonDocument.Parse(replay.Output).RootElement.GetProperty("AlreadyCommitted").GetBoolean());
            Assert.Equal(0, (await Run("prepare", "--operation", operation)).Code);
            var status = await Run("status", "--operation", operation);
            var before = JsonDocument.Parse(status.Output).RootElement;
            Assert.True(before.GetProperty("Committed").GetBoolean());
            Assert.False(before.GetProperty("Completed").GetBoolean());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var finished = await Run("finish", "--operation", operation);
                if (finished.Code == 0) break;
                Assert.Contains("corpus-rebuild-full-text-not-ready", finished.Error, StringComparison.Ordinal);
                await Task.Delay(100, timeout.Token);
            }
            var completed = await Run("status", "--operation", operation);
            Assert.True(JsonDocument.Parse(completed.Output).RootElement.GetProperty("Completed").GetBoolean());
        }
        finally { File.Delete(manifestPath); }
    }

    [Fact]
    public async Task Invalid_command_returns_usage_without_database_or_model_work()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await CorpusRebuildCommand.ExecuteAsync(["commit", "--operation", "not-a-guid"],
            null!, new PassageBuilder(new Tokenizer()), new UsearchGenerationValidator(), output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class Tokenizer : IPassageTokenizer
    {
        public string Fingerprint => BgeOfflineModels.EmbeddingTokenizerFingerprint;
        public int CountTokens(string text) => text.Length;
    }
}
