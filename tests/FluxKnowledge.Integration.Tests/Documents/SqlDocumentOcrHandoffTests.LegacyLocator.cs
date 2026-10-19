using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed partial class SqlDocumentOcrHandoffTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_document_locator_recovers_through_publication_without_rewriting_its_predecessor(bool pdf)
    {
        var seed = await CreateSettledImageFailureAsync(pdf, "document-ocr-runtime-failed",
            predecessorLocator: member => "retained-document-input:" + member.MemberFingerprint);
        string legacyLocator;
        await using (var before = await seed.Factory.CreateDbContextAsync())
            legacyLocator = (await before.SourceRevisions.SingleAsync(value => value.Id == seed.ChildId)).CanonicalPath;

        var recovery = await CreateCommittedRecoveryAsync(seed, pdf);
        var publication = await PrepareRecoveryPublicationAsync(seed, recovery.ChildId);
        await new SqlStageTransitionStore(seed.Factory).TransitionAsync(publication, CancellationToken.None);

        await using var verify = await seed.Factory.CreateDbContextAsync();
        var predecessor = await verify.SourceRevisions.SingleAsync(value => value.Id == seed.ChildId);
        var successor = await verify.SourceRevisions.SingleAsync(value => value.Id == recovery.ChildId);
        Assert.Equal(legacyLocator, predecessor.CanonicalPath);
        Assert.StartsWith($"retained-document-input:{seed.ParentId:N}:", successor.CanonicalPath, StringComparison.Ordinal);
        Assert.Equal(seed.ParentId, successor.ParentSourceRevisionId);
        Assert.Equal(seed.Hash, successor.ContentSha256);
        Assert.Equal(recovery.ChildId, (await verify.DocumentPublications.SingleAsync()).DocumentInputSourceRevisionId);
        Assert.True((await verify.PipelineRecords.SingleAsync(value => value.Id == publication.CurrentJob.PipelineRecordId.Value)).CompletionCriteriaMet);
        Assert.DoesNotContain(seed.FailedChildActivityId, await SqlCurrentSourceActivities.Query(verify).Select(value => value.Id).ToArrayAsync());
        Assert.Equal((int)PublicJobState.Failed, (await verify.Jobs.SingleAsync(value => value.Id == seed.FailedJobId)).PublicState);
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("wrong-fingerprint")]
    [InlineData("wrong-parent-locator")]
    [InlineData("wrong-hash")]
    [InlineData("ambiguous-source")]
    [InlineData("uncertain-dispatch")]
    public async Task Legacy_locator_compatibility_preserves_exact_identity_and_settlement_guards(string change)
    {
        var seed = await CreateSettledImageFailureAsync(predecessorLocator: member => change switch
        {
            "wrong-fingerprint" => "retained-document-input:" + new string('d', 64),
            "wrong-parent-locator" => $"retained-document-input:{Guid.NewGuid():N}:{member.MemberFingerprint}",
            _ => "retained-document-input:" + member.MemberFingerprint
        });
        if (change is "ambiguous-source" or "uncertain-dispatch")
            await ChangeImageRecoveryBindingAsync(seed, change);
        var request = ImageRecoveryRequest(seed);
        if (change == "wrong-hash") request = request with { ExpectedInputSha256 = new string('a', 64) };

        var result = await new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System)
            .RequestDocumentReprocessAsync(request, CancellationToken.None);

        Assert.False(result.Accepted);
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Single(await verify.SourceProcessorBranches.ToArrayAsync());
        Assert.Empty(await verify.SourceActivityRelations.ToArrayAsync());
        Assert.Empty(await verify.DocumentPublications.ToArrayAsync());
        Assert.Single(await verify.GpuExecutorResultReceipts.ToArrayAsync());
    }
}
