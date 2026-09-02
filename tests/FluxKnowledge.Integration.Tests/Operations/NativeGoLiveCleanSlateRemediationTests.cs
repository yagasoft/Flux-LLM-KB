using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

[Collection(NativeGoLiveMachineWideLeaseCollection.Name)]
public sealed class NativeGoLiveCleanSlateRemediationTests
{
    private const string CanonicalBootstrap =
        "Data Source=localhost;Initial Catalog=master;Integrated Security=True;" +
        "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
        "Pooling=False;Application Name=FluxKnowledge.NativeGoLive";

    [Fact]
    public async Task Empty_hierarchy_fails_closed_before_SQL_when_the_application_access_grant_is_not_proved()
    {
        using var fixture = new CleanSlateFixture();
        await using var lease = await fixture.AcquireLeaseAsync();

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.CreateEmptyRootAsync(fixture.Plan, CancellationToken.None).AsTask());

        Assert.Equal("effective-acl-postcondition-failed", exception.Message);
        Assert.Equal(["create-empty-root", "apply-and-validate-acls"], fixture.Events);
    }

    private sealed class CleanSlateFixture : IDisposable
    {
        private readonly string _root;

        public CleanSlateFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeCleanSlateRemediation", Guid.NewGuid().ToString("N"));
            var payloadRoot = Path.Combine(_root, "payload");
            Directory.CreateDirectory(payloadRoot);
            File.WriteAllText(Path.Combine(payloadRoot, "payload.dll"), "one-shot-payload");
            Plan = NativeGoLivePlan.CreateForIsolatedTests(
                LiveRootLayout.CreateForIsolatedTests(Path.Combine(_root, "live")),
                new string('a', 40));
            var manifest = NativeGoLivePayloadHasher.Compute(payloadRoot);
            _capability = new NativeGoLiveCloseoutCapabilityIssuer().Issue(Plan, payloadRoot, manifest.Sha256);
            _request = new NativeGoLiveRequest(
                Plan, false, true, true, true, true, true, payloadRoot, manifest.Sha256, manifest);
            Host = new GuardedNativeGoLiveHost(
                _capability,
                Plan,
                payloadRoot,
                NativeGoLiveSqlBootstrap.Parse(CanonicalBootstrap),
                new NativeGoLiveHostPorts(
                    null!,
                    null!,
                    new RecordingOwnedStatePort(Events),
                new SqlPortThatMustRunWithoutAclAdministration(Events),
                    new InvalidAclPort(Events),
                    null!,
                    null!,
                    null!,
                    null!));
        }

        public NativeGoLivePlan Plan { get; }
        public GuardedNativeGoLiveHost Host { get; }
        public List<string> Events { get; } = [];
        private readonly NativeGoLiveCloseoutCapability _capability;
        private readonly NativeGoLiveRequest _request;

        public ValueTask<INativeGoLiveLease> AcquireLeaseAsync()
        {
            Assert.True(_capability.TryBeginExecution());
            return Host.AcquireLeaseAsync(_request, CancellationToken.None);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingOwnedStatePort(List<string> events) : INativeGoLiveOwnedStatePort
    {
        public ValueTask WipeRootAsync(CancellationToken _) => throw new NotSupportedException();

        public ValueTask CreateEmptyRootAsync(CancellationToken _)
        {
            events.Add("create-empty-root");
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteProductionConfigurationAsync(CancellationToken _) => throw new NotSupportedException();
    }

    private sealed class SqlPortThatMustRunWithoutAclAdministration(List<string> events) : INativeGoLiveSqlPort
    {
        public ValueTask ProvisionEmptyCatalogueAsync(
            NativeGoLiveSqlIdentity _,
            NativeGoLiveSqlBootstrapConnection __,
            NativeGoLivePayloadManifest ___,
            CancellationToken ____)
        {
            events.Add("provision-sql");
            return ValueTask.FromException(
                new NativeGoLiveContractException("sql-called-without-acl-administration"));
        }
    }

    private sealed class InvalidAclPort(List<string> events) : INativeGoLiveAclPort
    {
        public ValueTask<NativeGoLiveAclObservation> ApplyAndObserveAsync(
            NativeGoLivePlan _, CancellationToken __)
        {
            events.Add("apply-and-validate-acls");
            return ValueTask.FromResult(new NativeGoLiveAclObservation(
                [], [], [], [], false, false, false, false, false, string.Empty, string.Empty, []));
        }

        public ValueTask<NativeGoLiveAclObservation> ObserveEffectiveAsync(
            NativeGoLivePlan _, CancellationToken __) => throw new NotSupportedException();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeGoLiveMachineWideLeaseCollection
{
    public const string Name = "Native go-live machine-wide lease";
}
