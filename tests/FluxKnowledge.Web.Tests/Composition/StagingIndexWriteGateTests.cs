using FluxKnowledge.Web;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingIndexWriteGateTests
{
    [Theory]
    [InlineData("POST","/api/v1/corpus/mutate")]
    [InlineData("POST","/mcp")]
    [InlineData("POST","/api/pipeline-records/utf8-file")]
    [InlineData("DELETE","/staging/indexing/prepare")]
    [InlineData("GET","/staging/indexing/prepare")]
    public void General_or_mismatched_write_surfaces_cannot_intervene(string method,string path)
    {
        var id=Guid.NewGuid();Assert.False(StagingIndexWriteGate.Allowed(id,id.ToString(),method,path));
    }

    [Fact]
    public void Only_the_bound_controller_can_use_even_readiness_or_private_fixture_routes()
    {
        var id=Guid.NewGuid();
        Assert.True(StagingIndexWriteGate.Allowed(id,id.ToString(),"GET","/staging"));
        Assert.True(StagingIndexWriteGate.Allowed(id,id.ToString(),"POST","/staging/indexing/prepare"));
        Assert.False(StagingIndexWriteGate.Allowed(id,null,"GET","/staging"));
        Assert.False(StagingIndexWriteGate.Allowed(id,Guid.NewGuid().ToString(),"POST","/staging/indexing/prepare"));
        Assert.False(StagingIndexWriteGate.Allowed(Guid.Empty,Guid.Empty.ToString(),"GET","/staging"));
    }
}
