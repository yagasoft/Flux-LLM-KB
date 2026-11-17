using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

public sealed partial class PublicationRetryTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_edit_and_rename_history_can_recover_original_publication(bool renamed)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Bootstrap.", publish: false);
        var recordId = await environment.AddRetainedAndPumpAsync("Physical predecessor publication recovery.");
        var failed = await FailPublishAsync(environment, recordId);
        await using (var db = environment.Factory.CreateDbContext())
        {
            var source = await db.SourceRevisions.SingleAsync(x => x.Id == db.PipelineRecords.Where(p => p.Id == recordId).Select(p => p.SourceRevisionId).Single());
            var parent = (SourceRevisionEntity)db.Entry(source).CurrentValues.ToObject(); parent.Id = Guid.NewGuid(); parent.RowVersion = [];
            parent.SuppressedAtUtc = DateTimeOffset.UtcNow; parent.ContentSha256 = new string('a', 64);
            if (renamed) parent.CanonicalPath += ".old";
            await db.SourceRevisions.Where(x => x.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, source.Revision + 1));
            db.SourceRevisions.Add(parent); await db.SaveChangesAsync();
            await db.SourceRevisions.Where(x => x.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ParentSourceRevisionId, parent.Id));
        }
        var service = Service(environment, new WakeSignal()); var command = Mutation(failed.Job.JobId.Value);
        var preview = await service.PreviewAsync(command, "test", default);
        await service.CommitAsync(command, preview.ConfirmationId, "physical-publication-" + renamed, "test", default);
        var recovered = await ClaimAsync(environment, recordId);
        var transitions = new StageTransitionService(new SqlStageTransitionStore(environment.Factory, sourceAdmission: new FixtureSourceAdmission(environment.Factory)),
            new StatusPublisher(), new WakeSignal(), TimeProvider.System);
        await new PublishStageWorker(environment.Store, environment.Store, environment.Builder, transitions, TimeProvider.System).ExecuteAsync(recovered, default);
        await using var verify = environment.Factory.CreateDbContext();
        Assert.True((await verify.PipelineRecords.SingleAsync(x => x.Id == recordId)).CompletionCriteriaMet);
    }
}
