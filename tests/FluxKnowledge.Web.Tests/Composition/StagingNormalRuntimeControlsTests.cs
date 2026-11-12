using System.Text.Json;
using FluxKnowledge.Web.Configuration;
using Xunit;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Application.Gpu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingNormalRuntimeControlsTests
{
    [Fact]
    public void Continuation_can_advance_the_cleanup_generation_while_preserving_the_frozen_corpus()
    {
        var original=JsonSerializer.SerializeToNode(new {Catalogue=StagingNormalRuntimeBinding.Catalogue,Incarnation="1ebf9b01-1c71-4ad4-96e7-bc51abf72383",
            Roots=new[]{new {Id=Guid.NewGuid(),State=1,Version="unchanged"}},Segments=new[]{new {SegmentId=Guid.NewGuid(),FileChecksum="unchanged"}},
            Publication=new[]{new {Id=Guid.NewGuid(),CorpusEpoch=Guid.NewGuid(),CorpusVersion=10,VectorCount=16,SegmentCount=1,CorpusRebuildOperationId=(Guid?)null}}})!;
        var candidate=original.DeepClone();candidate["Publication"]![0]!["Id"]=Guid.NewGuid();candidate["Publication"]![0]!["CorpusVersion"]=11;
        StagingNormalRuntimeBinding.ValidateFrozenContinuation(JsonSerializer.SerializeToElement(original),JsonSerializer.SerializeToElement(candidate));
        foreach(var mutation in new Action<System.Text.Json.Nodes.JsonNode>[] {n=>n["Roots"]![0]!["State"]=0,n=>n["Segments"]![0]!["FileChecksum"]="changed",
            n=>n["Publication"]![0]!["CorpusEpoch"]=Guid.NewGuid(),n=>n["Publication"]![0]!["VectorCount"]=15,n=>n["Publication"]![0]!["CorpusVersion"]=9})
        {
            var bad=candidate.DeepClone();mutation(bad);
            Assert.Throws<InvalidOperationException>(()=>StagingNormalRuntimeBinding.ValidateFrozenContinuation(JsonSerializer.SerializeToElement(original),JsonSerializer.SerializeToElement(bad)));
        }
    }
    [Fact]
    public void Cleanup_profile_recognises_local_completed_embedding_work_without_registering_execution_services()
    {
        var services=new ServiceCollection();StagingNormalGpuComposition.AddCleanupProfile(services);
        using var provider=services.BuildServiceProvider();
        Assert.Equal(BgeOfflineModels.EmbeddingFingerprint,provider.GetRequiredService<EmbeddingGpuRuntime>().Profile.ModelFingerprint);
        Assert.Empty(provider.GetServices<IHostedService>());Assert.Null(provider.GetService<EmbeddingGpuExecutor>());
        Assert.Null(provider.GetService<IBgeGpuModelFactory>());
    }
    [Fact]
    public async Task Known_pre_handoff_guard_refusal_preserves_lexical_fallback_without_claiming_ownership_uncertainty()
    {
        var native=new UnexpectedBackend();
        var guard=new StagingNormalGpuGuards.GuardedBackend(native,()=>throw new PassageRetrievalRefusalException("busy"));
        var result=await guard.RunAsync(Guid.NewGuid(),(_,_)=>throw new InvalidOperationException("must-not-score"),default);
        Assert.Equal(ExpandedGpuRankSettlement.Settled,result.Settlement);
        Assert.Equal("busy",Assert.IsType<PassageRetrievalRefusalException>(result.Failure).Status);
        Assert.Empty(result.Results);Assert.Equal(0,native.Calls);
        var opened=new StagingNormalGpuGuards.GuardedBackend(native,()=>{});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>opened.RunAsync(Guid.NewGuid(),(_,_)=>throw new InvalidOperationException("must-not-score"),default));
        Assert.Equal(1,native.Calls);
    }
    private sealed class UnexpectedBackend:IExpandedGpuRankBackend
    {
        public int Calls;public string RerankerFingerprint=>"synthetic";
        public Task<ExpandedGpuRankOutcome> RunAsync(Guid cohortId,Func<IPassageReranker,CancellationToken,ValueTask<IReadOnlyDictionary<Guid,RerankResult>>> score,CancellationToken cancellationToken)
        {Calls++;throw new InvalidOperationException("unknown-native-outcome");}
    }
    [Fact]
    public void Normal_runtime_requires_an_explicit_reset_receipt()
    {
        Assert.Equal("NormalRuntime", StagingHostArguments.ParseMode(["--settings-file", "owned", "--normal-runtime", "--ledger-reset", "receipt"]).ToString());
        Assert.Throws<ArgumentException>(() => StagingHostArguments.ParseMode(["--settings-file", "owned", "--normal-runtime"]));
        Assert.Equal("NormalRuntime",StagingHostArguments.ParseMode(["--settings-file","owned","--normal-runtime","--ledger-reset","receipt","--continuation","proof"]).ToString());
        Assert.Equal("CheckNormalRuntime",StagingHostArguments.ParseMode(["--settings-file","owned","--check-normal-runtime","--ledger-reset","receipt","--continuation","proof"]).ToString());
        Assert.Throws<ArgumentException>(()=>StagingHostArguments.ParseMode(["--settings-file","owned","--normal-runtime","--ledger-reset","receipt","--continuation"," "]));
    }

    [Fact]
    public void Normal_runtime_exposes_only_controlled_search_reads_and_fixture_controls()
    {
        var operation = Guid.NewGuid();
        foreach (var path in new[] { "/api/v1/corpus/search", "/api/v1/corpus/read", "/mcp", "/staging/indexing/start" })
            Assert.True(StagingIndexWriteGate.Allowed(operation, operation.ToString(), "POST", path, normalRuntime: true));
        foreach (var path in new[] { "/api/v1/corpus/write", "/api/v1/knowledge/write", "/staging/expanded-gpu", "/api/operator/source-roots" })
            Assert.False(StagingIndexWriteGate.Allowed(operation, operation.ToString(), "POST", path, normalRuntime: true));
        Assert.False(StagingIndexWriteGate.Allowed(operation, Guid.NewGuid().ToString(), "POST", "/mcp", normalRuntime: true));
    }

    [Fact]
    public void Reset_receipt_must_bind_the_exact_stage_and_cannot_claim_native_cleanup()
    {
        var valid = JsonSerializer.SerializeToElement(new { catalogue = "FluxKnowledge_Staging_ScopedSemantic_20261029",
            incarnation = "1ebf9b01-1c71-4ad4-96e7-bc51abf72383", copiedMetadataOnly = true,
            nativeCleanupClaimedFromRowDeletion = false, productionOperations = 0,
            completedUtc = DateTimeOffset.UtcNow, operation = Guid.NewGuid(), planSha256 = new string('A', 64),
            backupReceiptSha256 = new string('B', 64) });
        StagingNormalRuntimeBinding.ValidateReceipt(valid);
        foreach (var (property, replacement) in new[] { ("catalogue", "FluxKnowledge"), ("incarnation", Guid.NewGuid().ToString()),
                     ("copiedMetadataOnly", "false"), ("nativeCleanupClaimedFromRowDeletion", "true"), ("productionOperations", "1") })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(valid.GetRawText())!;
            json[property] = property is "catalogue" or "incarnation" ? JsonSerializer.SerializeToNode(replacement) : System.Text.Json.Nodes.JsonNode.Parse(replacement);
            Assert.Throws<InvalidOperationException>(() => StagingNormalRuntimeBinding.ValidateReceipt(JsonSerializer.SerializeToElement(json)));
        }
    }

    [Fact]
    public void Phase_capture_is_bounded_and_retains_correlated_phase_outcomes()
    {
        using var capture = new StagingSearchPhaseCapture(maximumEvents: 2);
        FluxKnowledge.Application.Search.HybridSearchDiagnostics.Log.Phase("trace", "span", "", "dense-selection", "completed", 12);
        FluxKnowledge.Application.Search.HybridSearchDiagnostics.Log.Phase("trace", "span", "", "quality-ann-pool", "completed", 8);
        FluxKnowledge.Application.Search.HybridSearchDiagnostics.Log.Phase("trace", "span", "", "overflow", "completed", 1);
        var snapshot = capture.Snapshot();
        Assert.Equal(2, snapshot.Events.Length);
        Assert.Equal(1, snapshot.Dropped);
        Assert.Contains(snapshot.Events, e => e.TraceId == "trace" && e.Phase == "dense-selection" && e.Milliseconds == 12);
    }
}
