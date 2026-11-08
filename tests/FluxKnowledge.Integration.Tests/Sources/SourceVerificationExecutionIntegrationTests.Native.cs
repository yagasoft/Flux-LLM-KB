using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationExecutionIntegrationTests
{
    [NativeSqlServerFact]
    public async Task Native_verification_query_preserves_historical_progress_while_reporting_the_feature_retired()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await SeedLegacyVerificationAsync(scenario);
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var reader = new SqlNativeV1ProjectionReader(scenario.Factory, new LocalPrivateContentDisclosure(), null!, codec, scenario.Verification);
        var query = new NativeCorpusQueryService(reader, codec);
        var result = JsonSerializer.SerializeToElement(await query.ExecuteAsync(new("verification", scenario.RootId.Value, null, null, 1, null), default));
        Assert.Equal(2, result.GetProperty("Run").GetProperty("Total").GetInt32());
        Assert.Equal(0, result.GetProperty("Run").GetProperty("SourceBytesRead").GetInt64());
        Assert.False(result.GetProperty("Run").GetProperty("ReadCountsIncomplete").GetBoolean());
        Assert.Equal(TimeZoneInfo.Local.Id, result.GetProperty("Policy").GetProperty("TimeZoneId").GetString());
        Assert.False(result.GetProperty("FeatureEnabled").GetBoolean());
        Assert.True(result.GetProperty("Scanning").GetProperty("BaselineEpoch").GetInt64() > 0);
        Assert.NotNull(result.GetProperty("Scanning").GetProperty("LastBaselineAtUtc").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("NextWindowAtUtc").ValueKind);
    }



    [NativeSqlServerFact]
    public async Task Invalid_verification_timezone_is_rejected_before_mutation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var commands = NativeCommands(scenario);
        var mutation = new NativeCorpusMutation("source_verification_set", JsonSerializer.SerializeToElement(new
        { rootId = scenario.RootId.Value, policy = SourceVerificationPolicy.Default("definitely-not-a-timezone") }));
        var rejected = await Assert.ThrowsAsync<NativeOperationException>(() => commands.PreviewAsync(mutation, "test", default).AsTask());
        Assert.Equal("invalid-payload", rejected.ReasonCode);
        Assert.Equal(TimeZoneInfo.Local.Id, (await scenario.Verification.ReadAsync(scenario.RootId, default))!.Policy.TimeZoneId);
    }

    private static NativeCorpusCommandService NativeCommands(Scenario scenario, TimeProvider? clock = null) => new(
        new NativeOperationService(new SqlNativeOperationStore(scenario.Factory, clock ?? TimeProvider.System, sourceJournalOptions: scenario.Options), []),
        new SqlNativeCorpusActionStore(scenario.Factory, new SourceRootPathPolicy(new LocalIngressOptions([scenario.Sources])), new LocalPrivateContentDisclosure()));

    private static async Task CommitNativeAsync(NativeCorpusCommandService commands, NativeCorpusMutation mutation)
    {
        var preview = await commands.PreviewAsync(mutation, "test", default);
        await commands.CommitAsync(mutation, preview.ConfirmationId, "verification-control-" + Guid.NewGuid().ToString("N"), "test", default);
    }
}
