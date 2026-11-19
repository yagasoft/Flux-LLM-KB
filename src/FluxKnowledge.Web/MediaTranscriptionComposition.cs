using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integrations.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FluxKnowledge.Web;

internal static class MediaTranscriptionComposition
{
    internal static MediaTranscriptionDiscoveryOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("MediaTranscription");
        var roots = section.GetSection("SourceRootIds").Get<Guid[]>() ?? [];
        var catchUp = section.GetSection("CatchUpRevisionIds").Get<Guid[]>() ?? [];
        var options = new MediaTranscriptionDiscoveryOptions(roots, section.GetValue<DateTimeOffset>("AfterUtc"), catchUp);
        options.Validate();
        return options;
    }

    internal static void Add(IServiceCollection services, MediaTranscriptionDiscoveryOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(MediaTranscriptionLocalRuntime.Profile);
        services.AddScoped<SqlMediaTranscriptionRegistrationStore>();
        services.AddScoped<SqlMediaTranscriptionGpuStore>();
        services.AddScoped<IMediaTranscriptionGpuQueue>(provider => provider.GetRequiredService<SqlMediaTranscriptionGpuStore>());
        services.AddScoped<MediaTranscriptionLocalExecutor>();
        services.AddScoped<ITranscriptSidecarPublisher>(provider => provider.GetRequiredService<SqlMediaTranscriptionGpuStore>());
        services.TryAddSingleton<IDocumentOcrProcessRecovery, FluxKnowledge.Integrations.Documents.DocumentOcrProcessRecovery>();
        services.AddSingleton<IGpuExecutorAdapter, MediaTranscriptionGpuExecutor>();
        if (options.SourceRootIds.Count == 0) return;
        services.AddScoped<IStageWorker>(provider => new TranscribeMediaStageWorker(
            provider.GetRequiredService<IPipelineStageReader>(), provider.GetRequiredService<IMediaTranscriptionGpuQueue>(),
            provider.GetRequiredService<FluxKnowledge.Application.Pipeline.StageTransitionService>(),
            provider.GetRequiredService<TimeProvider>(), MediaTranscriptionLocalRuntime.IsAvailable,
            provider.GetRequiredService<ITranscriptSidecarPublisher>()));
        services.AddHostedService<MediaTranscriptionDiscoveryService>();
    }
}

internal sealed class MediaTranscriptionDiscoveryService(IServiceScopeFactory scopes, MediaTranscriptionDiscoveryOptions options,
    TimeProvider time, IOutboxWakeSignal outboxWake,
    IStatusEventPublisher events, ILogger<MediaTranscriptionDiscoveryService> logger, ISourceWorkAdmission admission,
    IDeploymentValidationHold? hold = null, IDatabaseStartupReadiness? readiness = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await (readiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask).ConfigureAwait(false);
            var resolvedConversions = new HashSet<Guid>();
            var conversionRetries = new Dictionary<Guid, int>();
            while (!stoppingToken.IsCancellationRequested)
            {
                await (hold ?? DeploymentValidationHold.None).WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
                var delay = TimeSpan.FromSeconds(30);
                try
                {
                    if (admission.ReadAdmittedRoots().Any(root => options.SourceRootIds.Contains(root.RootId)))
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var store = scope.ServiceProvider.GetRequiredService<SqlMediaTranscriptionRegistrationStore>();
                        var result = await store.RunOnceAsync(options, MediaTranscriptionLocalRuntime.IsAvailable(),
                            stoppingToken).ConfigureAwait(false);
                        if (options.CatchUpRevisionIds.Count > 0)
                        {
                            var transcription = scope.ServiceProvider.GetRequiredService<SqlMediaTranscriptionGpuStore>();
                            foreach (var id in options.CatchUpRevisionIds)
                            {
                                if (resolvedConversions.Contains(id)) continue;
                                try
                                {
                                    if (!await transcription.ConvertPublishedSidecarAsync(id, stoppingToken).ConfigureAwait(false)) continue;
                                    resolvedConversions.Add(id);
                                    scope.ServiceProvider.GetService<ICorpusAnnPreparationSignal>()?.RequestPreparation();
                                    await events.PublishAsync(new StatusChanged(null, "sources", time.GetUtcNow()), stoppingToken)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                                {
                                    var temporary = error is SourceContentUnavailableException or
                                        IOException { InnerException: System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 } };
                                    if (temporary && conversionRetries.GetValueOrDefault(id) < 3)
                                    {
                                        conversionRetries[id] = conversionRetries.GetValueOrDefault(id) + 1;
                                        continue;
                                    }
                                    resolvedConversions.Add(id);
                                    logger.LogWarning("Existing transcript conversion stopped for recording revision {RevisionId} ({FailureType}).",
                                        id, error.GetType().Name);
                                }
                            }
                        }
                        if (result.Queued > 0) outboxWake.Notify();
                        if (result.Queued + result.Held > 0)
                            await events.PublishAsync(new StatusChanged(null, "sources", time.GetUtcNow()), stoppingToken).ConfigureAwait(false);
                        if (result.Examined == MediaTranscriptionDiscoveryOptions.MaximumBatchSize && result.Queued > 0)
                            delay = TimeSpan.FromSeconds(1);
                    }
                }
                catch (Exception error) when (error is DbUpdateException or Microsoft.Data.SqlClient.SqlException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Recording discovery will resume after a temporary failure ({FailureType}).", error.GetType().Name);
                }
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
