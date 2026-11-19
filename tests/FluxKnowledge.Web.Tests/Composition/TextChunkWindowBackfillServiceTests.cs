using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class TextChunkWindowBackfillServiceTests
{
    [Fact]
    public void Production_BGE_composition_registers_backfill_with_the_ANN_preparation_service()
    {
        var services=new ServiceCollection();
        BgeSearchRuntimeComposition.Add(services, enableWindowBackfill: true);
        Assert.Single(services,descriptor => descriptor.ServiceType==typeof(Microsoft.Extensions.Hosting.IHostedService) &&
            descriptor.ImplementationType==typeof(TextChunkWindowBackfillService));
        Assert.Single(services,descriptor => descriptor.ServiceType==typeof(ITextChunkWindowBackfillSignal));
    }

    [Fact]
    public void Signals_coalesce_without_constructing_hosted_services()
    {
        var services=new ServiceCollection();
        TextChunkWindowBackfillService.Add(services);TextChunkWindowBackfillService.Add(services);
        using var provider=services.BuildServiceProvider();
        var signal=provider.GetRequiredService<ITextChunkWindowBackfillSignal>();
        for(var i=0;i<100;i++) signal.RequestBackfill();
        var queue=provider.GetRequiredService<TextChunkWindowBackfillSignal>().Requests;
        Assert.True(queue.TryRead(out _));Assert.False(queue.TryRead(out _));
    }

    [Fact]
    public async Task Readiness_and_deployment_hold_precede_SQL_and_an_unclassified_failure_does_not_fault_the_host()
    {
        var factory=new FailingFactory();var readiness=new Readiness();var hold=new Hold();
        using var provider=Services(factory).BuildServiceProvider();
        var preparation=new CorpusAnnPreparationSignal();
        using var host=new TextChunkWindowBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),new(),preparation,
            NullLogger<TextChunkWindowBackfillService>.Instance,readiness,hold);
        await host.StartAsync(default);
        Assert.Equal(0,factory.Calls);
        readiness.Ready.SetResult();
        await hold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,factory.Calls);
        hold.Released.SetResult();
        await host.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.ExecuteTask.IsCompletedSuccessfully);
        Assert.Equal(1,factory.Calls);
        Assert.Equal("window-eligibility-backfill-unavailable",preparation.State.Reason);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Classified_SQL_unavailability_defers_without_a_hot_retry_or_host_failure()
    {
        var factory=new FailingFactory();using var provider=Services(factory).BuildServiceProvider();
        var preparation=new CorpusAnnPreparationSignal();
        using var host=new TextChunkWindowBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),new(),preparation,
            NullLogger<TextChunkWindowBackfillService>.Instance,failures:new Availability());
        await host.StartAsync(default);
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1,factory.Calls);
        Assert.NotEqual("failed",preparation.State.Status);
        Assert.False(host.ExecuteTask!.IsFaulted);
        await host.StopAsync(default);
        Assert.Equal(1,factory.Calls);
    }

    private static ServiceCollection Services(FailingFactory factory)
    {
        var services=new ServiceCollection();services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(factory);return services;
    }
    private sealed class FailingFactory : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FluxKnowledgeDbContext CreateDbContext(){Calls++;Entered.TrySetResult();throw new InvalidOperationException("synthetic database failure");}
    }
    private sealed class Readiness : IDatabaseStartupReadiness
    {
        public TaskCompletionSource Ready { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsReady=>Ready.Task.IsCompletedSuccessfully;
        public async ValueTask WaitAsync(CancellationToken cancellationToken)=>await Ready.Task.WaitAsync(cancellationToken);
    }
    private sealed class Hold : IDeploymentValidationHold
    {
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsHeld=>!Released.Task.IsCompletedSuccessfully;
        public async ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken){Entered.SetResult();await Released.Task.WaitAsync(cancellationToken);}
    }
    private sealed class Availability : IHostedDatabaseFailureClassifier
    {
        public bool TryClassify(Exception exception,out int errorNumber){errorNumber=-2;return true;}
    }
}
