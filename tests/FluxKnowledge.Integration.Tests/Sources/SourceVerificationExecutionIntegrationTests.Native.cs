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
    public async Task Native_verification_query_reports_durable_progress_and_policy_without_reading_source_files()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        await scenario.Verification.StartAsync(scenario.RootId, true, default);
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var reader = new SqlNativeV1ProjectionReader(scenario.Factory, new LocalPrivateContentDisclosure(), null!, codec, scenario.Verification);
        var query = new NativeCorpusQueryService(reader, codec);
        var result = JsonSerializer.SerializeToElement(await query.ExecuteAsync(new("verification", scenario.RootId.Value, null, null, 1, null), default));
        Assert.Equal(2, result.GetProperty("Run").GetProperty("Total").GetInt32());
        Assert.Equal(0, result.GetProperty("Run").GetProperty("SourceBytesRead").GetInt64());
        Assert.False(result.GetProperty("Run").GetProperty("ReadCountsIncomplete").GetBoolean());
        Assert.Equal(TimeZoneInfo.Local.Id, result.GetProperty("Policy").GetProperty("TimeZoneId").GetString());
        Assert.True(result.GetProperty("FeatureEnabled").GetBoolean());
        Assert.True(result.GetProperty("Scanning").GetProperty("BaselineEpoch").GetInt64() > 0);
        Assert.NotNull(result.GetProperty("Scanning").GetProperty("LastBaselineAtUtc").GetString());
        Assert.NotNull(result.GetProperty("NextWindowAtUtc").GetString());
    }

    [NativeSqlServerFact]
    public async Task Native_verification_settings_and_manual_start_use_atomic_audited_idempotent_controls()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var commands = NativeCommands(scenario);
        await using var initial = scenario.Factory.CreateDbContext();
        var receiptCount = await initial.NativeOperationReceipts.CountAsync();
        var policy = SourceVerificationPolicy.Default("UTC") with { Weekday = DayOfWeek.Friday, LocalStart = new(23, 15), MaximumBytesPerSecond = 1048576 };
        var settings = new NativeCorpusMutation("source_verification_set", JsonSerializer.SerializeToElement(new { rootId = scenario.RootId.Value, policy, paused = true }));
        await CommitNativeAsync(commands, settings);
        var status = (await scenario.Verification.ReadAsync(scenario.RootId, default))!;
        Assert.True(status.Paused);
        Assert.Equal(policy, status.Policy);
        var start = new NativeCorpusMutation("source_verify", JsonSerializer.SerializeToElement(new { rootId = scenario.RootId.Value }));
        var preview = await commands.PreviewAsync(start, "test", default);
        var key = "manual-verification-" + Guid.NewGuid().ToString("N");
        await commands.CommitAsync(start, preview.ConfirmationId, key, "test", default);
        await commands.CommitAsync(start, preview.ConfirmationId, key, "test", default);
        await using var db = scenario.Factory.CreateDbContext();
        Assert.Equal(1, await db.SourceVerificationRuns.CountAsync());
        Assert.True((await db.SourceVerificationRuns.SingleAsync()).Manual);
        Assert.False(await db.SourceScanRequests.AnyAsync(x => x.RequestKind == SqlSourceVerificationStore.RequestKind));
        Assert.Equal(1, await db.SourceRootConfigurations.Select(x => x.ConfigurationRevision).SingleAsync());
        Assert.Equal(receiptCount + 2, await db.NativeOperationReceipts.CountAsync());
        Assert.Equal(1, await db.NativeOperationReceipts.CountAsync(x => x.IdempotencyKey == key));
        await CommitNativeAsync(commands, new("source_verification_set", JsonSerializer.SerializeToElement(new { rootId = scenario.RootId.Value, paused = false })));
        Assert.Equal(1, await scenario.Verification.PumpAsync(default));
    }

    [NativeSqlServerFact]
    public async Task Stale_verification_settings_confirmation_cannot_overwrite_a_new_policy()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await scenario.BaselineAsync();
        var commands = NativeCommands(scenario);
        var mutation = new NativeCorpusMutation("source_verification_set", JsonSerializer.SerializeToElement(new { rootId = scenario.RootId.Value, paused = true }));
        var old = await commands.PreviewAsync(mutation, "test", default);
        await CommitNativeAsync(commands, mutation);
        await Assert.ThrowsAsync<NativeOperationException>(() => commands.CommitAsync(mutation, old.ConfirmationId,
            "stale-" + Guid.NewGuid().ToString("N"), "test", default).AsTask());
        Assert.True((await scenario.Verification.ReadAsync(scenario.RootId, default))!.Paused);
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
