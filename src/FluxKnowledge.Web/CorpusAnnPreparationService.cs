using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Web;

internal sealed record CorpusAnnPreparationState(string Status, Guid? Generation = null, string? Reason = null);

/// <summary>One owned model-free warmup; request deadlines never own or cancel this work.</summary>
internal sealed class CorpusAnnPreparationService(IServiceScopeFactory scopes,
    ILogger<CorpusAnnPreparationService> logger, IDatabaseStartupReadiness? readiness = null,
    CorpusAnnPreparationSignal? preparation = null) : BackgroundService, ICorpusAnnPreparationSignal
{
    private readonly CorpusAnnPreparationSignal _preparation = preparation ?? new();
    internal CorpusAnnPreparationState State => _preparation.State;
    public void RequestPreparation() => _preparation.RequestPreparation();
    public string? FailureFor(Guid generation) => _preparation.FailureFor(generation);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (readiness is not null) await readiness.WaitAsync(stoppingToken);
        RequestPreparation();
        while (await _preparation.Requests.WaitToReadAsync(stoppingToken))
        {
            while (_preparation.Requests.TryRead(out _)) { }
            await PrepareOnceAsync(stoppingToken);
        }
    }

    internal async Task PrepareOnceAsync(CancellationToken stoppingToken)
    {
        Guid? generation = null;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider;
            var profile = provider.GetRequiredService<IScheduledPassageInference>().EmbeddingProfile;
            var leases = provider.GetRequiredService<ICorpusGenerationLeaseStore>();
            var sql = await leases.TryAcquireReadableAsync(Guid.NewGuid(), provider.GetRequiredService<IGpuInteractiveOwnerProbe>().Current,
                profile.ModelFingerprint, profile.Dimensions, stoppingToken);
            if (sql is null) throw new PassageRetrievalRefusalException("index-updating");
            if (sql.Generation.FormatVersion != 2)
            {
                await sql.DisposeAsync();
                throw new PassageRetrievalRefusalException("unavailable");
            }
            await using var ann = await provider.GetRequiredService<ICorpusAnnLeaseFactory>().OpenAsync(sql, stoppingToken);
            generation = ann.Generation.Id;
            // A non-fitting immutable generation cannot become fitting by retrying it.
            if (State is { Status: "failed", Reason: "scope-capacity-exceeded" } prior && prior.Generation == generation) return;
            _preparation.SetState(new("warming", generation));
            var metadata = provider.GetRequiredService<ICorpusRetrievalReader>() as ICorpusAnnBindingPreparation
                ?? throw new PassageRetrievalRefusalException("unavailable");
            await ann.PrepareAsync(metadata, stoppingToken);
            if (!ann.IsPrepared(metadata) || !await ann.IsReadableAsync(stoppingToken))
                throw new PassageRetrievalRefusalException("warming");
            _preparation.SetState(new("prepared", generation));
            logger.LogInformation("Corpus ANN projection prepared. Generation {Generation}.", generation);
            // Finish this captured generation instead of restarting on each publication.
            if (!await ann.IsCurrentAsync(stoppingToken))
            {
                // Strict currentness also changes when publication advances before
                // projection refresh. Only a different active identity needs another pass.
                await using var active = await leases.TryAcquireReadableAsync(Guid.NewGuid(),
                    provider.GetRequiredService<IGpuInteractiveOwnerProbe>().Current,
                    profile.ModelFingerprint, profile.Dimensions, stoppingToken);
                if (active is not null && (active.Generation.Id != ann.Generation.Id ||
                    active.Generation.CorpusStamp?.CorpusEpoch != ann.Generation.CorpusStamp?.CorpusEpoch))
                    RequestPreparation();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var reason = exception is PassageRetrievalRefusalException refusal ? refusal.Status : "unavailable";
            _preparation.SetState(new("failed", generation, reason));
            logger.LogWarning(exception, "Corpus ANN preparation is unavailable. Generation {Generation}, reason {Reason}.", generation, reason);
        }
    }

    internal static void Add(IServiceCollection services)
    {
        if (services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(CorpusAnnPreparationService))) return;
        // The concrete hosted registration is visible to strict composition validation.
        // The concrete alias resolves that same hosted singleton, never a second runner.
        // Publishers resolve the independent signal without constructing hosted services.
        services.AddSingleton<IHostedService, CorpusAnnPreparationService>();
        services.AddSingleton(provider => provider.GetServices<IHostedService>().OfType<CorpusAnnPreparationService>().Single());
        services.AddSingleton<CorpusAnnPreparationSignal>();
        services.AddSingleton<ICorpusAnnPreparationSignal>(provider => provider.GetRequiredService<CorpusAnnPreparationSignal>());
    }
}
