using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed partial class BgeGpuInferenceTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(16)]
    public async Task Background_owned_batches_share_one_load_and_preserve_order_with_tensor_runs_of_at_most_four(int count)
    {
        var models = new RecordingModels { EmbeddingResults = (_, texts) => OrderedResults(texts) };
        var result = await new BgeGpuInferenceSession(models).EmbedBatchAsync(Owner(),
            Enumerable.Range(0, count).Select(value => value.ToString()).ToArray(), CancellationToken.None);
        Assert.Null(result.RefusalReason);
        Assert.True(result.NativeCapacityReleased);
        Assert.Equal(count, result.Value!.Count);
        for (var row = 0; row < count; row++) Assert.Equal(1, result.Value[row].Values[row]);
        Assert.Equal((count + 3) / 4, models.Batches.Count);
        Assert.All(models.Batches, batch => Assert.InRange(batch.Length, 1, 4));
        Assert.Single(models.Events, value => value == "embedding.open");
        Assert.Single(models.Events, value => value == "embedding.dispose");
    }

    [Theory]
    [InlineData("owner-cancellation")]
    [InlineData("caller-cancellation")]
    [InlineData("run")]
    [InlineData("wrong-count")]
    [InlineData("invalid-result")]
    [InlineData("dispose")]
    public async Task Background_owned_batches_never_return_partial_results_after_a_later_failure(string failure)
    {
        using var ownerCancellation = new CancellationTokenSource();
        using var callerCancellation = new CancellationTokenSource();
        var runs = 0;
        var models = new RecordingModels
        {
            Failure = failure == "dispose" ? "dispose" : null,
            BeforeRun = () =>
            {
                if (++runs != 2) return;
                if (failure == "owner-cancellation") ownerCancellation.Cancel();
                if (failure == "caller-cancellation") callerCancellation.Cancel();
                if (failure == "run") throw new InvalidOperationException("later-run-failed");
            },
            EmbeddingResults = (run, texts) =>
            {
                if (run == 2 && failure == "wrong-count") return [];
                var results = OrderedResults(texts);
                if (run == 2 && failure == "invalid-result") results[0] = new(new float[1024], results[0].ModelFingerprint);
                return results;
            }
        };
        var result = await new BgeGpuInferenceSession(models).EmbedBatchAsync(Owner(ownerCancellation.Token),
            Enumerable.Range(0, 8).Select(value => value.ToString()).ToArray(), callerCancellation.Token);
        Assert.NotNull(result.RefusalReason);
        Assert.Null(result.Value);
        Assert.Equal(failure != "dispose", result.NativeCapacityReleased);
        Assert.Equal(2, models.Batches.Count);
        Assert.Single(models.Events, value => value == "embedding.dispose");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_owned_batches_validate_the_whole_request_before_loading(bool tooMany)
    {
        var texts = Enumerable.Repeat("passage", tooMany ? 17 : 16).ToArray();
        if (!tooMany) texts[^1] = " ";
        var models = new RecordingModels();
        var result = await new BgeGpuInferenceSession(models).EmbedBatchAsync(Owner(), texts, CancellationToken.None);
        Assert.NotNull(result.RefusalReason);
        Assert.True(result.NativeCapacityReleased);
        Assert.Empty(models.Events);
    }

    private static EmbeddingResult[] OrderedResults(IReadOnlyList<string> texts) => texts.Select(text =>
    {
        var values = new float[1024]; values[int.Parse(text)] = 1;
        return new EmbeddingResult(values, BgeOfflineModels.EmbeddingFingerprint);
    }).ToArray();
}
