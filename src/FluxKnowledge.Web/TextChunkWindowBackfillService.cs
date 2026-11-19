using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Threading.Channels;

namespace FluxKnowledge.Web;

internal sealed class TextChunkWindowBackfillSignal : ITextChunkWindowBackfillSignal
{
    private readonly Channel<bool> _requests=Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode=BoundedChannelFullMode.DropWrite,SingleReader=true,SingleWriter=false });
    internal ChannelReader<bool> Requests => _requests.Reader;
    public void RequestBackfill() => _requests.Writer.TryWrite(true);
}

/// <summary>Resumes bounded derived-proof work after readiness and the deployment hold; never acquires models.</summary>
internal sealed class TextChunkWindowBackfillService(IServiceScopeFactory scopes,TextChunkWindowBackfillSignal signal,
    CorpusAnnPreparationSignal preparation,ILogger<TextChunkWindowBackfillService> logger,
    IDatabaseStartupReadiness? readiness=null,IDeploymentValidationHold? hold=null,
    IHostedDatabaseFailureClassifier? failures=null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await (readiness?.WaitAsync(stoppingToken) ?? ValueTask.CompletedTask);
        signal.RequestBackfill();
        var progressed=false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await (hold ?? DeploymentValidationHold.None).WaitUntilReleasedAsync(stoppingToken);
                while (signal.Requests.TryRead(out _)) { }
                using var scope=scopes.CreateScope();
                var result=await new SqlTextChunkWindowEligibilityStore(
                    scope.ServiceProvider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(),
                    scope.ServiceProvider.GetService<TimeProvider>(),hold:hold).RunOneAsync(stoppingToken);
                progressed |= result.Recomputed>0;
                if (!result.Completed && result.Inspected>0)
                {
                    await Task.Delay(25,stoppingToken);
                    continue;
                }
                if (result.Completed && (progressed || preparation.State is {Status:"warming"} or {Status:"failed",Reason:"warming"}))
                    preparation.RequestPreparation();
                progressed=false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception failure) when (failures?.TryClassify(failure,out _) == true)
            {
                logger.LogWarning(failure,"Window eligibility backfill remains durable after a transient SQL failure.");
            }
            catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
            {
                // This optional derived projection fails closed without recycling
                // the app or repeatedly replaying an unclassified failed mutation.
                preparation.SetState(new("failed",Reason:"window-eligibility-backfill-unavailable"));
                logger.LogError(failure,"Window eligibility backfill stopped; committed proofs and progress remain durable.");
                return;
            }
            using var wait=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var timer=Task.Delay(TimeSpan.FromSeconds(30),wait.Token);
            var wake=signal.Requests.WaitToReadAsync(wait.Token).AsTask();
            await Task.WhenAny(timer,wake);
            await wait.CancelAsync();
        }
    }

    internal static void Add(IServiceCollection services)
    {
        if (services.Any(value => value.ServiceType==typeof(IHostedService) && value.ImplementationType==typeof(TextChunkWindowBackfillService))) return;
        services.AddSingleton<TextChunkWindowBackfillSignal>();
        services.AddSingleton<ITextChunkWindowBackfillSignal>(provider => provider.GetRequiredService<TextChunkWindowBackfillSignal>());
        services.AddSingleton<IHostedService,TextChunkWindowBackfillService>();
    }
}
