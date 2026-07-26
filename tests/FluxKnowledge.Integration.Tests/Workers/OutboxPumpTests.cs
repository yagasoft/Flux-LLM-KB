using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class OutboxPumpTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Hosted_pump_drains_extract_and_normalise_but_leaves_canonical_index_queued()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var ingressRoot = Path.Combine(
            Path.GetTempPath(),
            $"FluxKnowledgePump_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ingressRoot);
        var sourcePath = Path.Combine(ingressRoot, "a.txt");
        await File.WriteAllTextAsync(sourcePath, "cafe\u0301\r\nline\r");
        var clock = new FixedTimeProvider(
            DateTimeOffset.Parse("2026-07-27T08:00:00+00:00"));
        var services = new ServiceCollection();
        services.AddSingleton(SqlTestData.CreateFactory(_fixture));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IUtf8FileSourceReader>(
            new Utf8FileSourceReader(new LocalIngressOptions([ingressRoot])));
        services.AddFluxKnowledgeOutboxWorkers();
        await using var provider = services.BuildServiceProvider();
        var registration = new RegisterUtf8FileHandler(
            provider.GetRequiredService<IUtf8FileSourceReader>(),
            provider.GetRequiredService<IRegistrationStore>());
        var receipt = await registration.HandleAsync(
            new(sourcePath, "integration-test", "a.txt"),
            CancellationToken.None);
        var hosted = provider.GetRequiredService<OutboxPumpService>();

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await WaitForCanonicalSuccessorAsync(
                provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
                receipt.PipelineRecordId.Value);
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
            Directory.Delete(ingressRoot, recursive: true);
        }

        await using var context = await provider
            .GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>()
            .CreateDbContextAsync();
        var normaliseArtifact = await context.Artifacts.AsNoTracking().SingleAsync(
            artifact =>
                artifact.PipelineRecordId == receipt.PipelineRecordId.Value &&
                artifact.Stage == (int)PipelineStage.Normalise);
        Assert.Equal("café\nline\n", normaliseArtifact.SearchText);
        var canonicalJob = await context.Jobs.AsNoTracking().SingleAsync(
            job =>
                job.PipelineRecordId == receipt.PipelineRecordId.Value &&
                job.Stage == (int)PipelineStage.CanonicalIndex);
        Assert.Equal((int)PublicJobState.WorkerQueued, canonicalJob.PublicState);
        Assert.Null(canonicalJob.LeaseOwner);
        var canonicalDispatch = await context.OutboxMessages.AsNoTracking().SingleAsync(
            message =>
                message.PipelineRecordId == receipt.PipelineRecordId.Value &&
                message.Stage == (int)PipelineStage.CanonicalIndex);
        Assert.Null(canonicalDispatch.DispatchedAtUtc);
        Assert.Null(canonicalDispatch.LeaseOwner);
        Assert.Equal(0, canonicalDispatch.LeaseGeneration);
        Assert.Equal(
            2,
            await context.OutboxMessages.CountAsync(
                message =>
                    message.PipelineRecordId == receipt.PipelineRecordId.Value &&
                    message.DispatchedAtUtc != null));
    }

    private static async Task WaitForCanonicalSuccessorAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid pipelineRecordId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var context = await factory.CreateDbContextAsync();
            if (await context.Artifacts.AnyAsync(
                    artifact =>
                        artifact.PipelineRecordId == pipelineRecordId &&
                        artifact.Stage == (int)PipelineStage.Normalise) &&
                await context.Jobs.AnyAsync(
                    job =>
                        job.PipelineRecordId == pipelineRecordId &&
                        job.Stage == (int)PipelineStage.CanonicalIndex))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            "The hosted pump did not persist the Normalise boundary in time.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
