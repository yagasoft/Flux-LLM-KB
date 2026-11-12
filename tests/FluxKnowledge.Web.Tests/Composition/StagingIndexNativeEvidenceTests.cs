using System.Text.Json;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Web;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingIndexNativeEvidenceTests
{
    [Fact]
    public void Normal_gpu_capture_proves_embedding_and_ranking_calls_do_not_overlap()
    {
        using var proof=new StagingIndexNativeEvidence(gpu:true);
        HybridSearchDiagnostics.Log.ModelPhase("index","gpu-embedding","native-run",0,"begin");
        HybridSearchDiagnostics.Log.ModelPhase("rank","gpu-reranker","native-run",0,"begin");
        HybridSearchDiagnostics.Log.ModelPhase("rank","gpu-reranker","native-run",2,"settled");
        HybridSearchDiagnostics.Log.ModelPhase("index","gpu-embedding","native-run",3,"settled");
        var snapshot=JsonSerializer.SerializeToElement(proof.Snapshot());
        Assert.Equal(2,snapshot.GetProperty("settled").GetInt32());
        Assert.True(snapshot.GetProperty("invalid").GetBoolean());
        Assert.Contains(snapshot.GetProperty("intervals").EnumerateArray(),i=>i.GetProperty("Model").GetString()=="gpu-embedding");
    }

    [Fact]
    public void Captures_only_registered_actual_cpu_embedding_intervals()
    {
        using var proof=new StagingIndexNativeEvidence();var id=Guid.NewGuid().ToString();proof.Register(id);
        HybridSearchDiagnostics.Log.ModelPhase("other","cpu-embedding","native-run",0,"begin");
        HybridSearchDiagnostics.Log.ModelPhase(id,"gpu-reranker","native-run",0,"begin");
        var before=DateTimeOffset.UtcNow;
        HybridSearchDiagnostics.Log.ModelPhase(id,"cpu-embedding","native-run",0,"begin");
        HybridSearchDiagnostics.Log.ModelPhase(id,"cpu-embedding","native-run",3,"settled");
        var snapshot=JsonSerializer.SerializeToElement(proof.Snapshot());
        Assert.Equal(1,snapshot.GetProperty("settled").GetInt32());Assert.Equal(0,snapshot.GetProperty("active").GetInt32());
        Assert.False(snapshot.GetProperty("invalid").GetBoolean());
        var interval=Assert.Single(snapshot.GetProperty("intervals").EnumerateArray());
        Assert.Equal(id,interval.GetProperty("Id").GetString());Assert.Equal(3,interval.GetProperty("Milliseconds").GetDouble());
        Assert.True(interval.GetProperty("SettledUtc").GetDateTimeOffset()>=interval.GetProperty("StartedUtc").GetDateTimeOffset());
        Assert.InRange(interval.GetProperty("StartedUtc").GetDateTimeOffset(),before,DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Unbalanced_or_overlapping_owned_intervals_do_not_prove_settlement()
    {
        using var proof=new StagingIndexNativeEvidence();var id=Guid.NewGuid().ToString();proof.Register(id);
        HybridSearchDiagnostics.Log.ModelPhase(id,"cpu-embedding","native-run",0,"begin");
        HybridSearchDiagnostics.Log.ModelPhase(id,"cpu-embedding","native-run",0,"begin");
        var snapshot=JsonSerializer.SerializeToElement(proof.Snapshot());
        Assert.True(snapshot.GetProperty("invalid").GetBoolean());Assert.Equal(1,snapshot.GetProperty("active").GetInt32());
    }
}
