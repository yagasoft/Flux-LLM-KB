using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
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
    public async Task Physical_revision_history_supports_exact_document_admission_claim_and_commit(bool pdf)
    {
        var seed = await CreateSettledImageFailureAsync(pdf,
            failureReason: pdf ? "document-ocr-runtime-failed" : "image-document-dimensions-exceeded", withPhysicalHistory: true);
        await using var arranged = await seed.Factory.CreateDbContextAsync();
        var previousId = Assert.IsType<Guid>((await arranged.SourceRevisions.SingleAsync(value => value.Id == seed.ParentId)).ParentSourceRevisionId);
        var descriptor = pdf ? PdfDocumentProcessor.Capability : ImageDocumentInputProcessor.Capability;
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        var request = new DocumentReprocessRequest(new(seed.ParentId), seed.Hash, descriptor.ProcessorFingerprint, seed.FailedJobId, 1);
        var admitted = await store.RequestDocumentReprocessAsync(request, CancellationToken.None);
        Assert.True(admitted.Created);
        var claim = await store.ClaimDocumentBranchAsync(admitted.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
            descriptor.ProcessorFingerprint, "physical-history-recovery", CancellationToken.None);
        Assert.NotNull(claim);
        var retained = new RetainedSourceBytes(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length);
        var completion = pdf
            ? await new PdfDocumentProcessor(new SqlRetainedArtifactWriter(seed.Factory, seed.ArtifactRoot)).ProcessAsync(claim, retained, new(), CancellationToken.None)
            : await new ImageDocumentInputProcessor().ProcessAsync(claim, retained, new(), CancellationToken.None);
        Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Equal(previousId, (await verify.SourceRevisions.SingleAsync(value => value.Id == seed.ParentId)).ParentSourceRevisionId);
        var child = Assert.IsType<Guid>((await verify.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == claim.BranchId)).ChildSourceRevisionId);
        Assert.NotEqual(seed.ChildId, child);
        Assert.Equal(seed.ParentId, (await verify.SourceRevisions.SingleAsync(value => value.Id == child)).ParentSourceRevisionId);
    }

    [NativeSqlServerTheory]
    [InlineData("admission")]
    [InlineData("claim")]
    [InlineData("commit")]
    public async Task Physical_revision_history_never_recovers_an_ambiguous_current_binding(string phase)
    {
        var seed = await CreateSettledImageFailureAsync(withPhysicalHistory: true, differentHistoryIdentity: true);
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        DocumentReprocessRequestResult? admitted = null;
        RetainedProcessorClaim? claim = null;
        RetainedProcessorCompletion? completion = null;
        if (phase != "admission")
        {
            admitted = await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None);
            Assert.True(admitted.Created);
        }
        if (phase == "commit")
        {
            claim = await store.ClaimDocumentBranchAsync(admitted!.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
                ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "history-competitor-test", CancellationToken.None);
            Assert.NotNull(claim);
            completion = await new ImageDocumentInputProcessor().ProcessAsync(claim,
                new(new(seed.ParentId), seed.Bytes, seed.Hash, seed.Bytes.Length), new(), CancellationToken.None);
        }
        await using (var change = await seed.Factory.CreateDbContextAsync())
        {
            var current = await change.SourceRevisions.SingleAsync(value => value.Id == seed.ParentId);
            (await change.SourceRevisions.SingleAsync(value => value.Id == current.ParentSourceRevisionId)).SuppressedAtUtc = null;
            await change.SaveChangesAsync();
        }
        if (phase == "admission")
            Assert.False((await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None)).Created);
        else if (phase == "claim")
            Assert.Null(await store.ClaimDocumentBranchAsync(admitted!.SuccessorBranchId!.Value, new(seed.ParentId), seed.Hash,
                ImageDocumentInputProcessor.Capability.ProcessorFingerprint, "history-competitor-test", CancellationToken.None));
        else Assert.False(await store.CommitAsync(claim!, completion!, CancellationToken.None));
        await using var verify = await seed.Factory.CreateDbContextAsync();
        Assert.Single(await verify.SourceProcessorBranchMembers.ToArrayAsync());
        Assert.Empty(await verify.DocumentPublications.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Physical_revision_history_does_not_make_a_derived_owner_or_the_earlier_contract_eligible()
    {
        var seed = await CreateSettledImageFailureAsync(withPhysicalHistory: true, parentOriginKind: 2);
        var store = new SqlRetainedProcessorBranchStore(seed.Factory, TimeProvider.System);
        Assert.False((await store.RequestDocumentReprocessAsync(ImageRecoveryRequest(seed), CancellationToken.None)).Created);
        seed = await CreateSettledImageFailureAsync(withPhysicalHistory: true);
        store = new(seed.Factory, TimeProvider.System);
        var earlierRequest = new DocumentReprocessRequest(new(seed.ParentId), seed.Hash,
            ImageDocumentInputProcessor.Capability.ProcessorFingerprint);
        Assert.False((await store.RequestDocumentReprocessAsync(earlierRequest, CancellationToken.None)).Created);
    }
}
