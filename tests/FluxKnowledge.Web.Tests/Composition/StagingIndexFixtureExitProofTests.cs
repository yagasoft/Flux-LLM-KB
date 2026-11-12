using System.Text.Json;
using FluxKnowledge.Web;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingIndexFixtureExitProofTests
{
    [Theory]
    [InlineData(true,true,true)]
    [InlineData(false,true,false)]
    [InlineData(true,false,false)]
    public void Recovery_requires_both_exact_exit_and_empty_owned_job(bool exited,bool empty,bool expected)
    {
        var op=Guid.NewGuid();var birth=DateTimeOffset.UnixEpoch;
        var start=JsonSerializer.SerializeToElement(new {id=17,startedUtc=birth,operationId=op,assignedOwnedJob=true,noBreakaway=true,killOnLastJobHandleClose=true,activeProcessLimit=1});
        var end=JsonSerializer.SerializeToElement(new {id=17,startedUtc=birth,actualExitObserved=true,ownedJobEmpty=empty});
        var fixture=JsonSerializer.SerializeToElement(new {operation=op,hostProcessId=17,hostStartedUtc=birth});
        if(expected)StagingIndexFixtureExitProof.Validate(start,end,fixture,(_,_)=>exited);
        else Assert.Throws<InvalidOperationException>(()=>StagingIndexFixtureExitProof.Validate(start,end,fixture,(_,_)=>exited));
    }

    [Fact]
    public void Another_window_or_reused_process_cannot_authorise_root_cleanup()
    {
        var birth=DateTimeOffset.UnixEpoch;var op=Guid.NewGuid();
        var start=JsonSerializer.SerializeToElement(new {id=17,startedUtc=birth,operationId=op,assignedOwnedJob=true,noBreakaway=true,killOnLastJobHandleClose=true,activeProcessLimit=1});
        var end=JsonSerializer.SerializeToElement(new {id=17,startedUtc=birth,actualExitObserved=true,ownedJobEmpty=true});
        foreach(var fixture in new[]{JsonSerializer.SerializeToElement(new {operation=Guid.NewGuid(),hostProcessId=17,hostStartedUtc=birth}),
            JsonSerializer.SerializeToElement(new {operation=op,hostProcessId=17,hostStartedUtc=birth.AddTicks(1)})})
            Assert.Throws<InvalidOperationException>(()=>StagingIndexFixtureExitProof.Validate(start,end,fixture,(_,_)=>true));
    }
}
