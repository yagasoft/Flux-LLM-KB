using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed class NativeCodeFeedbackIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T18:00:00+00:00");
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Preview_commit_mismatch_and_replay_persist_one_privacy_safe_feedback_effect_in_the_generic_transaction()
    {
        var actor = $"feedback-{Guid.NewGuid():N}";
        var mutation = Mutation(new { category = "relevant", resultId = "public-result-42", comment = "useful result" });
        var canonicalPayload = NativeOperationCanonicalization.CanonicalizeJson(mutation.Payload.GetRawText());
        var service = CreateService();

        var preview = await service.PreviewAsync(mutation, actor, CancellationToken.None);
        await AssertReasonAsync("confirmation-mismatch", () => service.CommitAsync(
            mutation, "wrong-confirmation", "feedback-key", actor, CancellationToken.None).AsTask());
        var first = await service.CommitAsync(
            mutation, preview.ConfirmationId, "feedback-key", actor, CancellationToken.None);
        var replay = await service.CommitAsync(
            mutation, preview.ConfirmationId, "feedback-key", actor, CancellationToken.None);

        Assert.False(first.WasReplay);
        Assert.True(replay.WasReplay);
        Assert.Equal(first.OperationId, replay.OperationId);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        var effect = Assert.Single(await context.AuditEvents.AsNoTracking()
            .Where(value => value.Actor == actor && value.EventType == "native_code_feedback.recorded")
            .ToListAsync());
        using var details = JsonDocument.Parse(effect.DetailsJson);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))),
            details.RootElement.GetProperty("feedbackHash").GetString());
        Assert.Equal(Hash("relevant"), details.RootElement.GetProperty("categoryHash").GetString());
        Assert.Equal(Hash("public-result-42"), details.RootElement.GetProperty("resultHash").GetString());
        Assert.DoesNotContain("public-result-42", effect.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("useful result", effect.DetailsJson, StringComparison.Ordinal);
        Assert.Equal(1, await context.NativeOperationReceipts.CountAsync(value => value.ActorSurface == actor));
        Assert.DoesNotContain("public-result-42", (await context.NativeOperationIntents.SingleAsync(value => value.ActorSurface == actor)).TargetMetadataJson, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Protected_feedback_is_rejected_before_any_durable_intent_or_effect()
    {
        var actor = $"feedback-secret-{Guid.NewGuid():N}";
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<NativeOperationException>(() => service.PreviewAsync(
            Mutation(new { category = "relevant", comment = "secret-content-sentinel" }),
            actor,
            CancellationToken.None).AsTask());

        Assert.Equal("secret-content-withheld", exception.ReasonCode);
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.False(await context.NativeOperationIntents.AnyAsync(value => value.ActorSurface == actor));
        Assert.False(await context.AuditEvents.AnyAsync(value => value.Actor == actor));
    }

    [NativeSqlServerFact]
    public async Task Cancelled_feedback_commit_leaves_the_preview_unconsumed_without_a_receipt_or_effect()
    {
        var actor = $"feedback-cancel-{Guid.NewGuid():N}";
        var mutation = Mutation(new { category = "irrelevant", resultId = "public-result-99" });
        var service = CreateService();
        var preview = await service.PreviewAsync(mutation, actor, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CommitAsync(
            mutation, preview.ConfirmationId, "cancelled-feedback", actor, cancellation.Token).AsTask());

        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        Assert.Null((await context.NativeOperationIntents.SingleAsync(value => value.ActorSurface == actor)).ConsumedAtUtc);
        Assert.False(await context.NativeOperationReceipts.AnyAsync(value => value.ActorSurface == actor));
        Assert.False(await context.AuditEvents.AnyAsync(value => value.Actor == actor));
    }

    private NativeCodeFeedbackService CreateService() => new(
        new SqlNativeOperationStore(SqlTestData.CreateFactory(_fixture), new FixedTimeProvider(Now)),
        new LocalPrivateContentDisclosure());

    private static NativeCodeFeedbackMutation Mutation(object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new NativeCodeFeedbackMutation(document.RootElement.Clone());
    }

    private static async Task AssertReasonAsync(string reason, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<NativeOperationException>(action);
        Assert.Equal(reason, exception.ReasonCode);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
