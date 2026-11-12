using FluxKnowledge.Application.Search;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

[Collection("sql-hosted-recovery")]
public sealed class StagingGpuNativeEventTests
{
    [Fact]
    public void Sequential_native_calls_can_share_one_trace_without_becoming_duplicate_calls()
    {
        using var listener=new StagingSqlGpuRankBackend.NativeEvents();
        for(var i=0;i<2;i++)
        {
            HybridSearchDiagnostics.Log.ModelPhase("same-trace/span","gpu-reranker","native-run",0,"begin");
            HybridSearchDiagnostics.Log.ModelPhase("same-trace/span","gpu-reranker","native-run",1,"settled");
        }
        var counts=listener.Counts();
        Assert.Equal(2,counts.Started);Assert.Equal(2,counts.Settled);Assert.False(counts.InvalidOrUnsettled);
    }

    [Fact]
    public void Unbalanced_or_overlapping_native_calls_fail_the_observation_gate()
    {
        using var listener=new StagingSqlGpuRankBackend.NativeEvents();
        HybridSearchDiagnostics.Log.ModelPhase("same-trace/span","cpu-reranker","native-run",0,"begin");
        Assert.Equal(0,listener.Counts().Started);
        HybridSearchDiagnostics.Log.ModelPhase("same-trace/span","gpu-reranker","native-run",0,"begin");
        Assert.True(listener.Counts().InvalidOrUnsettled);
        HybridSearchDiagnostics.Log.ModelPhase("same-trace/span","gpu-reranker","native-run",0,"begin");
        HybridSearchDiagnostics.Log.ModelPhase("same-trace/span","gpu-reranker","native-run",0,"settled");
        Assert.True(listener.Counts().InvalidOrUnsettled);
    }
}
