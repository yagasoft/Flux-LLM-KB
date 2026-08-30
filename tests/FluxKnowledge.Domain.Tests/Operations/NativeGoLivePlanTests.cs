using FluxKnowledge.Application.Operations;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Operations;

public sealed class NativeGoLivePlanTests
{
    [Fact]
    public void Production_plan_has_only_approved_native_literals()
    {
        var plan = NativeGoLivePlan.CreateProduction(new string('a', 40));

        Assert.Equal(@"I:\FluxKnowledge", plan.Layout.Root);
        Assert.Equal("FluxKnowledge", plan.Sql.CatalogName);
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf", plan.Sql.DataFilePath);
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf", plan.Sql.LogFilePath);
        Assert.Equal("I:", plan.Vss.Volume);
        Assert.Equal(0.10m, plan.Vss.MaximumStorageFraction);
        Assert.Equal(@"I:\FluxKnowledge\CodexPlugin", plan.Codex.MarketplaceRoot);
        Assert.Equal("fluxknowledge", plan.Codex.MarketplaceName);
        Assert.Equal("fluxknowledge", plan.Codex.PluginName);
        Assert.Equal("FluxKnowledge", plan.IisSiteName);
        Assert.Equal("FluxKnowledge", plan.AppPoolName);
        Assert.Equal(5137, plan.LoopbackPort);
    }

    [Fact]
    public void Plan_hash_is_deterministic_and_binds_the_committed_sha()
    {
        var first = NativeGoLivePlan.CreateProduction(new string('a', 40));
        var same = NativeGoLivePlan.CreateProduction(new string('a', 40));
        var different = NativeGoLivePlan.CreateProduction(new string('b', 40));

        Assert.Equal(first.PlanHash, same.PlanHash);
        Assert.NotEqual(first.PlanHash, different.PlanHash);
        Assert.Matches("^[a-f0-9]{64}$", first.PlanHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Production_plan_rejects_noncanonical_committed_sha(string committedSha) =>
        Assert.Throws<ArgumentException>(() => NativeGoLivePlan.CreateProduction(committedSha));
}
