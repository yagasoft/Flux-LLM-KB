using System.Reflection;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

[Collection(NativeGoLiveMachineWideLeaseCollection.Name)]
public sealed class NativeGoLiveHostPrerequisiteRemediationTests
{
    private const string CanonicalBootstrap =
        "Data Source=localhost;Initial Catalog=master;Integrated Security=True;" +
        "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
        "Pooling=False;Application Name=FluxKnowledge.NativeGoLive";

    [Fact]
    public void Host_viable_localhost_integrated_bootstrap_is_accepted_and_numeric_or_remote_endpoints_are_refused()
    {
        var accepted = NativeGoLiveSqlBootstrap.Parse(CanonicalBootstrap);

        Assert.Equal("localhost", accepted.DataSource);
        Assert.True(accepted.IntegratedSecurity);
        Assert.Equal("master", accepted.InitialCatalog, ignoreCase: true);
        Assert.Equal(5, accepted.ConnectTimeout);
        var malformed = Assert.Throws<NativeGoLiveContractException>(() => NativeGoLiveSqlBootstrap.Parse(string.Empty));
        Assert.Equal("sql-bootstrap-malformed", malformed.Message);
        Assert.Throws<NativeGoLiveContractException>(() => NativeGoLiveSqlBootstrap.Parse(
            CanonicalBootstrap.Replace("Data Source=localhost", "Data Source=127.0.0.1", StringComparison.Ordinal)));
        Assert.Throws<NativeGoLiveContractException>(() => NativeGoLiveSqlBootstrap.Parse(
            CanonicalBootstrap.Replace("Data Source=localhost", "Data Source=192.0.2.99", StringComparison.Ordinal)));
    }

    [Fact]
    public void Named_IIS_site_and_pool_are_replaced_without_an_ownership_or_adoption_observation()
    {
        var plan = NativeGoLivePlan.CreateProduction(new string('a', 40));
        var administration = new RecordingIisAdministration();
        var port = new NativeGoLiveWindowsIisPort(administration);
        var replacement = typeof(NativeGoLiveWindowsIisPort).GetMethod(
            "ReplaceCanonical", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(replacement);
        var observation = Assert.IsType<NativeGoLiveIisObservation>(replacement!.Invoke(port, [plan]));

        Assert.Equal(["remove-site", "remove-pool", "create-pool", "create-site", "observe"], administration.Events);
        Assert.Same(plan, administration.ReplacementPlan);
        Assert.Equal(plan.Layout.ApplicationRoot, observation.PhysicalPath);
        Assert.Equal(
            [new NativeGoLiveIisBinding("http", "127.0.0.1", plan.LoopbackPort, string.Empty)],
            observation.Bindings);
        Assert.Equal(1, administration.ObserveCalls);
    }

    [Fact]
    public async Task Pool_stop_waits_for_the_terminal_stopped_observation()
    {
        var administration = new TransitionalPoolAdministration();
        var port = new NativeGoLiveWindowsIisPort(administration);

        var result = await port.StopAsync("FluxKnowledge", CancellationToken.None);

        Assert.True(result.WasRunning);
        Assert.Equal("Stopped", result.State);
        Assert.True(result.OperationSucceeded);
        Assert.Equal(3, administration.ObserveCalls);
    }

    private sealed class RecordingIisAdministration : INativeGoLiveIisAdministration
    {
        public List<string> Events { get; } = [];
        public NativeGoLivePlan? ReplacementPlan { get; private set; }
        public int ObserveCalls { get; private set; }

        public NativeGoLiveIisObservation Observe(NativeGoLivePlan plan)
        {
            Events.Add("observe");
            ObserveCalls++;
            return new NativeGoLiveIisObservation(
                plan.IisSiteName,
                plan.AppPoolName,
                plan.Layout.ApplicationRoot,
                true,
                false,
                [new NativeGoLiveIisBinding("http", "127.0.0.1", plan.LoopbackPort, string.Empty)]);
        }

        public void ReplaceCanonical(NativeGoLivePlan plan)
        {
            ReplacementPlan = plan;
            Events.Add("remove-site");
            Events.Add("remove-pool");
            Events.Add("create-pool");
            Events.Add("create-site");
        }

        public string ObservePoolState(string _) => "Stopped";
        public void StopPool(string _) => throw new NotSupportedException();
        public void StartPool(string _) => throw new NotSupportedException();
    }

    private sealed class TransitionalPoolAdministration : INativeGoLiveIisAdministration
    {
        private readonly Queue<string> _states = new(["Started", "Stopping", "Stopped"]);

        public int ObserveCalls { get; private set; }
        public NativeGoLiveIisObservation Observe(NativeGoLivePlan _) => throw new NotSupportedException();
        public void ReplaceCanonical(NativeGoLivePlan _) => throw new NotSupportedException();
        public string ObservePoolState(string _)
        {
            ObserveCalls++;
            return _states.Dequeue();
        }
        public void StopPool(string _) { }
        public void StartPool(string _) => throw new NotSupportedException();
    }
}
