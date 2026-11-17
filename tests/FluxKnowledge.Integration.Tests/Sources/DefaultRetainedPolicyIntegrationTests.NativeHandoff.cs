using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DefaultRetainedPolicyIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(true, "none")]
    [InlineData(false, "none")]
    [InlineData(false, "before-registration")]
    [InlineData(false, "after-registration")]
    public async Task Native_policy_command_hands_off_one_job_without_a_scan_loop_and_receipt_replay_recovers_finalization(
        bool withPolicyOwner, string failure)
    {
        var (seeded, _) = await SeedStrandedTextAsync(withPolicyOwner);
        var factory = SqlTestData.CreateFactory(fixture);
        using var reader = new SqlRetainedSourceReader(factory, seeded.ArtifactRoot);
        var inner = new SqlRetainedTextRegistrationStore(factory, TimeProvider.System, seeded.ArtifactRoot,
            sourceAdmission: new FixtureSourceAdmission(factory));
        IRetainedTextRegistrationStore registration = failure == "none" ? inner : new FailOnceRegistration(inner, failure == "after-registration");
        var wake = new ChannelOutboxWakeSignal();
        using var provider = new ServiceCollection()
            .AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(factory)
            .AddSingleton<ISourceRootPathPolicy>(new NoOriginalIoPolicy())
            .AddSingleton<FluxKnowledge.Application.Visibility.ILocalPrivateContentDisclosure>(new LocalPrivateContentDisclosure())
            .AddSingleton<IRetainedSourceReader>(reader)
            .AddSingleton(new RetainedTextActivityPlanner(registration, wake))
            .BuildServiceProvider();
        var actions = ActivatorUtilities.CreateInstance<SqlNativeCorpusActionStore>(provider);
        var service = new NativeCorpusCommandService(new NativeOperationService(new SqlNativeOperationStore(factory, TimeProvider.System), []), actions, wake);
        var mutation = Mutation(seeded);
        var preview = await service.PreviewAsync(mutation, "test", default);
        var key = "legacy-native-handoff-" + seeded.RevisionId.ToString("N");
        if (failure == "none") await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        else
        {
            await Assert.ThrowsAsync<IOException>(() => service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default).AsTask());
            await using var interrupted = await factory.CreateDbContextAsync();
            Assert.Equal(failure == "after-registration" ? 1 : 0,
                await interrupted.PipelineRecords.CountAsync(value => value.SourceRevisionId == seeded.RevisionId));
        }
        var replay = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        Assert.True(replay.WasReplay);
        var repeated = await service.CommitAsync(mutation, preview.ConfirmationId, key, "test", default);
        Assert.Equal(replay.OperationId, repeated.OperationId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await wake.WaitAsync(deadline.Token);
        await using var db = await factory.CreateDbContextAsync();
        var record = await db.PipelineRecords.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
        var successor = await db.SourceActivities.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId && value.ProcessorVersion == RetainedDefaultPolicy.Version);
        Assert.Equal(record.Id, successor.ResultingPipelineRecordId);
        Assert.Equal((int)PipelineStage.Extract, Assert.Single(await db.Jobs.Where(value => value.PipelineRecordId == record.Id).ToArrayAsync()).Stage);
        Assert.Single(await db.OutboxMessages.Where(value => value.PipelineRecordId == record.Id).ToArrayAsync());
        Assert.Single(await db.SourceActivityRelations.Where(value => value.PredecessorActivityId == seeded.ActivityId).ToArrayAsync());
        Assert.False(Directory.Exists(seeded.OriginalRoot));
    }

    private sealed class FailOnceRegistration(IRetainedTextRegistrationStore inner, bool after) : IRetainedTextRegistrationStore
    {
        private bool _armed = true;
        public async ValueTask<bool> RegisterAsync(SourceActivity activity, CancellationToken token)
        {
            var fail = _armed;
            _armed = false;
            if (fail && !after) throw new IOException("Injected stop before registration.");
            var registered = await inner.RegisterAsync(activity, token);
            if (fail) throw new IOException("Injected lost response after registration.");
            return registered;
        }
    }
}
