using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingPayloadReadinessRankerTests
{
    [Theory]
    [InlineData(1,"unavailable")]
    [InlineData(7,"timeout")]
    public async Task Records_real_submit_attempts_before_refusing_without_scores(int seconds,string expected)
    {
        var clock=new Clock();var capture=new StagingPayloadReadinessRanker(clock);
        await using var ticket=capture.Reserve(clock.GetUtcNow(),"trace","span",default);
        clock.Now=clock.Now.AddSeconds(seconds);
        var refusal=await Assert.ThrowsAsync<PassageRetrievalRefusalException>(() => ticket.SubmitAsync("query",[new(1,"verified passage")]).AsTask());
        Assert.Equal(expected,refusal.Status);
        var record=Assert.Single(capture.Snapshot());
        Assert.Equal(seconds*1000,record.SubmittedMilliseconds);Assert.Equal(new long[]{1},record.PassageIds);
        Assert.Equal(seconds>6,record.AfterReadinessCutoff);Assert.False(record.GpuExecuted);Assert.False(record.ScoresReturned);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now=DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
