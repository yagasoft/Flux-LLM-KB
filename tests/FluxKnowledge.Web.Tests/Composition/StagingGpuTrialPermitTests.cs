using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingGpuTrialPermitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static StagingGpuTrialPermit Valid => new(1,Guid.NewGuid(),Now,Now.AddSeconds(120),31,Now.AddMinutes(-1),41,Now.AddMinutes(-2),"app","config","ledger",true,true,true,0,4318,10115,"adapter");
    private static readonly StagingGpuAdapterIdentity Adapter = new(0,4318,10115,"adapter");

    [Fact]
    public void Exact_live_process_and_unchanged_payload_witness_is_required()
    {
        StagingGpuTrialGate.Validate(Valid,Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => true);
        Assert.Throws<PassageRetrievalRefusalException>(() => StagingGpuTrialGate.Validate(Valid,Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => false));
    }

    [Fact]
    public void Expired_foreign_unfenced_or_changed_witnesses_fail_closed()
    {
        foreach(var bad in new[]{ Valid with { DeadlineUtc=Now }, Valid with { DeadlineUtc=Now.AddMinutes(11) },
            Valid with { IssuedUtc=Now.AddSeconds(1) }, Valid with { HostProcessId=42 },
            Valid with { HostStartedUtc=Now }, Valid with { HostAssemblySha256="changed" },
            Valid with { SettingsSha256="changed" }, Valid with { SchedulerBindingSha256="changed" }, Valid with { AdmissionFenceHeld=false },
            Valid with { PoolStopped=false }, Valid with { ProductionOwnerAbsenceVerified=false } })
            Assert.Throws<PassageRetrievalRefusalException>(() => StagingGpuTrialGate.Validate(bad,Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => true));
    }

    [Fact]
    public void Adapter_identity_changes_refuse_before_model_open()
    {
        foreach(var bad in new[]{Adapter with { Luid="changed" },Adapter with { Index=1 },Adapter with { Vendor=1 },Adapter with { Device=1 }})
            Assert.Throws<PassageRetrievalRefusalException>(() => StagingGpuTrialGate.Validate(Valid,Now,41,Now.AddMinutes(-2),"app","config","ledger",bad,(_,_) => true));
    }

    [Fact]
    public async Task Cohort_claim_is_once_per_host_even_with_concurrent_or_reissued_permits()
    {
        var settings=FluxKnowledge.Web.Configuration.StagingHostSettings.Create(@"I:\Codex Workspaces\Staging\FluxKnowledge\PermitClaimTest",
            "Server=localhost;Database=FluxKnowledge_Staging_PermitClaimTest;User ID=stage;Password=synthetic","http://127.0.0.1:5147");
        var gate=new StagingGpuTrialGate(settings);
        var accepted=await Task.WhenAll(Enumerable.Range(1,20).Select(_ => Task.Run(() =>
        {
            try{gate.ClaimCohort();return true;}catch(PassageRetrievalRefusalException refusal){Assert.Equal("busy",refusal.Status);return false;}
        })));
        Assert.Single(accepted,a => a);
        Assert.Throws<PassageRetrievalRefusalException>(() => gate.ClaimCohort());
    }

    [Fact]
    public async Task Qualification_attempts_have_an_atomic_immutable_twenty_one_cohort_ceiling()
    {
        var settings=FluxKnowledge.Web.Configuration.StagingHostSettings.Create(@"I:\Codex Workspaces\Staging\FluxKnowledge\PermitClaimTest",
            "Server=localhost;Database=FluxKnowledge_Staging_PermitClaimTest;User ID=stage;Password=synthetic","http://127.0.0.1:5147");
        var gate=new StagingGpuTrialGate(settings,21);
        var accepted=await Task.WhenAll(Enumerable.Range(1,84).Select(_ => Task.Run(() =>
        {
            try{gate.ClaimCohort();return true;}catch(PassageRetrievalRefusalException refusal){Assert.Equal("busy",refusal.Status);return false;}
        })));
        Assert.Equal(21,accepted.Count(a => a));
        for(var i=0;i<3;i++) Assert.Throws<PassageRetrievalRefusalException>(() => gate.ClaimCohort());
    }

    [Fact]
    public void Qualification_permit_must_match_the_explicit_host_limit_and_cannot_raise_default_admission()
    {
        var permit=Valid with { MaximumCohorts=21 };
        StagingGpuTrialGate.Validate(permit,Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => true,21);
        Assert.Throws<PassageRetrievalRefusalException>(() => StagingGpuTrialGate.Validate(permit,Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => true));
        Assert.Throws<PassageRetrievalRefusalException>(() => StagingGpuTrialGate.Validate(Valid,Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => true,21));
        foreach(var cap in new[]{0,2,22})
            Assert.Throws<PassageRetrievalRefusalException>(() => StagingGpuTrialGate.Validate(Valid with { MaximumCohorts=cap },Now,41,Now.AddMinutes(-2),"app","config","ledger",Adapter,(_,_) => true,cap));
    }
}
