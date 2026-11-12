using System.Threading.Channels;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Gpu;

namespace FluxKnowledge.Web;

internal sealed record CorpusAnnPreparationState(string Status, Guid? Generation = null, string? Reason = null);

/// <summary>One owned model-free warmup; request deadlines never own or cancel this work.</summary>
internal sealed class CorpusAnnPreparationService(IServiceScopeFactory scopes,
    ILogger<CorpusAnnPreparationService> logger, IDatabaseStartupReadiness? readiness = null) : BackgroundService, ICorpusAnnPreparationSignal
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private CorpusAnnPreparationState _state = new("warming");
    internal CorpusAnnPreparationState State => Volatile.Read(ref _state);
    public void RequestPreparation() => _requests.Writer.TryWrite(true);
    public string? FailureFor(Guid generation) => State is { Status: "failed" } state && state.Generation == generation ? state.Reason : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (readiness is not null) await readiness.WaitAsync(stoppingToken);
        RequestPreparation();
        while (await _requests.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_requests.Reader.TryRead(out _)) { }
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
            Volatile.Write(ref _state, new("warming", generation));
            var metadata = provider.GetRequiredService<ICorpusRetrievalReader>() as ICorpusAnnBindingPreparation
                ?? throw new PassageRetrievalRefusalException("unavailable");
            await ann.PrepareAsync(metadata, stoppingToken);
            if (!ann.IsPrepared(metadata) || !await ann.IsReadableAsync(stoppingToken))
                throw new PassageRetrievalRefusalException("warming");
            Volatile.Write(ref _state, new("prepared", generation));
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
            Volatile.Write(ref _state, new("failed", generation, reason));
            logger.LogWarning(exception, "Corpus ANN preparation is unavailable. Generation {Generation}, reason {Reason}.", generation, reason);
        }
    }

    internal static void Add(IServiceCollection services)
    {
        if (services.Any(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(CorpusAnnPreparationService))) return;
        // The concrete hosted registration is visible to strict composition validation.
        // Both aliases resolve that same hosted singleton, never a second runner.
        services.AddSingleton<IHostedService, CorpusAnnPreparationService>();
        services.AddSingleton(provider => provider.GetServices<IHostedService>().OfType<CorpusAnnPreparationService>().Single());
        services.AddSingleton<ICorpusAnnPreparationSignal>(provider => provider.GetRequiredService<CorpusAnnPreparationService>());
    }
}
