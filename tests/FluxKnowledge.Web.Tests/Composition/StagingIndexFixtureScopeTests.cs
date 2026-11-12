using FluxKnowledge.Web;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingIndexFixtureScopeTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UnixEpoch;
    private static readonly Guid ForeignRoot=Guid.NewGuid(),OwnRoot=Guid.NewGuid(),ForeignOperation=Guid.NewGuid(),OwnOperation=Guid.NewGuid();
    private static readonly StagingFixtureRoot Root=new(ForeignRoot,1,"root-version");
    private static readonly StagingFixtureDeletion Foreign=new(ForeignOperation,ForeignRoot,3,null,"deletion-version");
    private static StagingIndexFixtureScope Scope()=>new([Root],[Foreign]);

    [Fact]
    public void Foreign_running_deletion_is_refused_even_before_its_lease_expires()
    {
        var foreign=Foreign with {State=1,LeaseExpiresUtc=Now.AddHours(1)};
        Assert.Throws<InvalidOperationException>(()=>new StagingIndexFixtureScope([Root],[foreign]));
        Assert.Throws<InvalidOperationException>(()=>Scope().CanClaimDeletion([Root],[foreign],OwnRoot,OwnOperation,Now,true));
    }

    [Fact]
    public void New_or_changed_foreign_deletion_is_refused_before_native_claim()
    {
        Assert.Throws<InvalidOperationException>(()=>Scope().CanClaimDeletion([Root],[Foreign,Foreign with {Id=Guid.NewGuid()}],OwnRoot,OwnOperation,Now,true));
        Assert.Throws<InvalidOperationException>(()=>Scope().CanClaimDeletion([Root],[Foreign with {Version="changed"}],OwnRoot,OwnOperation,Now,true));
    }

    [Fact]
    public void Foreign_root_resume_or_configuration_change_refuses_the_closed_fixture()
    {
        Assert.Throws<InvalidOperationException>(()=>Scope().Validate([Root with {State=0}],[Foreign],OwnRoot,null,true));
        Assert.Throws<InvalidOperationException>(()=>Scope().Validate([Root with {Version="changed"}],[Foreign],OwnRoot,null,true));
        Assert.Throws<InvalidOperationException>(()=>Scope().Validate([Root,new(Guid.NewGuid(),1,"new")],[Foreign],OwnRoot,null,true));
    }

    [Fact]
    public void Only_the_exact_owned_operation_and_root_can_reach_native_claim()
    {
        var own=new StagingFixtureDeletion(OwnOperation,OwnRoot,0,null,"own");
        Assert.True(Scope().CanClaimDeletion([Root,new(OwnRoot,2,"own")],[Foreign,own],OwnRoot,OwnOperation,Now,true));
        Assert.Throws<InvalidOperationException>(()=>Scope().CanClaimDeletion([Root],[Foreign,own with {RootId=ForeignRoot}],OwnRoot,OwnOperation,Now,true));
        Assert.False(Scope().CanClaimDeletion([Root],[Foreign],OwnRoot,OwnOperation,Now,true));
    }

    [Fact]
    public void Owned_unexpired_lease_and_terminal_work_never_fall_back_to_global_claim()
    {
        var own=new StagingFixtureDeletion(OwnOperation,OwnRoot,1,Now.AddSeconds(1),"own");
        Assert.False(Scope().CanClaimDeletion([Root],[Foreign,own],OwnRoot,OwnOperation,Now,true));
        Assert.True(Scope().CanClaimDeletion([Root],[Foreign,own],OwnRoot,OwnOperation,Now.AddSeconds(1),true));
        Assert.False(Scope().CanClaimDeletion([Root],[Foreign,own with {State=3}],OwnRoot,OwnOperation,Now,true));
    }

    [Fact]
    public void Missing_exclusive_writer_witness_refuses_even_an_owned_claim()
    {
        Assert.Throws<InvalidOperationException>(()=>Scope().CanClaimDeletion([Root],[Foreign],OwnRoot,OwnOperation,Now,false));
    }
}
