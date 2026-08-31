using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

[Collection(NativeGoLiveMachineWideLeaseCollection.Name)]
public sealed class NativeGoLiveLiveGateCompositionTests
{
    private const string CanonicalBootstrap =
        "Data Source=localhost;Initial Catalog=master;Integrated Security=True;" +
        "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
        "Pooling=False;Application Name=FluxKnowledge.NativeGoLive";

    [Fact]
    public async Task Confirmed_host_prerequisite_replacement_reaches_the_Windows_preflight_adapter_before_any_root_or_catalogue_operation()
    {
        using var fixture = new ExecutorOrderingFixture();
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.Equal("clean-slate-incomplete", result.ReasonCode);
        Assert.Equal(
            [
                "replace-canonical-iis",
                "install-direct-admin-bootstrap",
                "admission-observe-present",
                "admission-wipe",
                "admission-observe-absent",
                "windows-preflight-sql",
                "stop-iis",
                "configure-vss",
                "create-empty-root",
                "apply-acls",
                "provision-sql"
            ],
            fixture.Events);
        Assert.True(fixture.Acls.AppliedAfterRootCreation);
        Assert.True(Directory.Exists(fixture.Plan.Layout.SqlDataRoot));
        Assert.True(Directory.Exists(fixture.Plan.Layout.SqlLogRoot));
    }

    [Fact]
    public async Task Direct_admin_bootstrap_failure_with_a_safe_bootstrap_code_preserves_that_code_before_admission()
    {
        using var fixture = new ExecutorOrderingFixture(failBootstrap: true);
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.Equal("native-go-live-bootstrap-install-sql-batch-1-failed", result.ReasonCode);
        Assert.Equal(["replace-canonical-iis", "install-direct-admin-bootstrap"], fixture.Events);
        Assert.True(Directory.Exists(fixture.Plan.Layout.Root));
        Assert.True(File.Exists(Path.Combine(fixture.Plan.Layout.Root, "pre-wipe-sentinel.txt")));
    }

    [Fact]
    public async Task Generic_bootstrap_operation_failure_preserves_its_existing_fixed_code()
    {
        using var fixture = new ExecutorOrderingFixture(
            failBootstrap: true,
            bootstrapFailureCode: "native-go-live-bootstrap-reset-failed");
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.Equal("native-go-live-bootstrap-reset-failed", result.ReasonCode);
        Assert.Equal(["replace-canonical-iis", "install-direct-admin-bootstrap"], fixture.Events);
    }

    [Fact]
    public async Task Admission_failure_returns_only_the_fixed_admission_reason_code()
    {
        using var fixture = new ExecutorOrderingFixture(failAdmission: true);
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.Equal("clean-slate-admission-failed", result.ReasonCode);
        Assert.Equal(
            ["replace-canonical-iis", "install-direct-admin-bootstrap", "admission-observe-present"],
            fixture.Events);
        Assert.True(Directory.Exists(fixture.Plan.Layout.Root));
        Assert.True(File.Exists(Path.Combine(fixture.Plan.Layout.Root, "pre-wipe-sentinel.txt")));
    }

    [Fact]
    public async Task Admission_cancellation_returns_only_the_fixed_admission_reason_code()
    {
        using var fixture = new ExecutorOrderingFixture(cancelAdmission: true);
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.Equal("clean-slate-admission-failed", result.ReasonCode);
        Assert.Equal(
            ["replace-canonical-iis", "install-direct-admin-bootstrap", "admission-observe-present"],
            fixture.Events);
        Assert.True(Directory.Exists(fixture.Plan.Layout.Root));
    }

    [Fact]
    public async Task Vss_contract_failure_preserves_its_existing_fixed_code()
    {
        using var fixture = new ExecutorOrderingFixture(failVss: true);
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.Equal("vss-exact-action-not-proved", result.ReasonCode);
        Assert.Equal(
            [
                "replace-canonical-iis",
                "install-direct-admin-bootstrap",
                "admission-observe-present",
                "admission-wipe",
                "admission-observe-absent",
                "windows-preflight-sql",
                "stop-iis",
                "configure-vss"
            ],
            fixture.Events);
    }

    [Fact]
    public async Task Preflight_rejects_missing_direct_admin_app_pool_identity()
    {
        using var fixture = new ExecutorOrderingFixture(ValidPreflight() with
        {
            AppPoolLoginExists = false,
            AppPoolLoginSid = null,
            AppPoolLoginSidHex = null
        });
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("stop-iis", fixture.Events);
    }

    [Fact]
    public async Task Preflight_rejects_wrong_fixed_procedure_manifest()
    {
        using var fixture = new ExecutorOrderingFixture(ValidPreflight() with
        {
            BootstrapProcedures = ValidProcedures().Skip(1).ToArray()
        });
        fixture.BeginExecution();

        var result = await new NativeGoLiveExecutor().ExecuteAsync(fixture.Request, fixture.Host);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("stop-iis", fixture.Events);
    }

    [Fact]
    public async Task Post_bootstrap_validation_accepts_direct_admin_app_pool_authority()
    {
        using var fixture = new PostBootstrapFixture();
        await using var lease = await fixture.AcquireLeaseAsync();

        await fixture.Host.ProvisionEmptyCatalogueAsync(fixture.Plan.Sql, CancellationToken.None);
    }

    [Fact]
    public async Task Post_bootstrap_validation_rejects_missing_app_pool_sysadmin_membership()
    {
        using var fixture = new PostBootstrapFixture(appPoolIsSysAdmin: false);
        await using var lease = await fixture.AcquireLeaseAsync();

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ProvisionEmptyCatalogueAsync(fixture.Plan.Sql, CancellationToken.None).AsTask());

        Assert.Equal("sql-bootstrap-postcondition-failed", exception.Message);
    }

    [Fact]
    public async Task Post_bootstrap_validation_rejects_non_app_pool_catalogue_owner()
    {
        using var fixture = new PostBootstrapFixture(catalogueOwnerSidHex: OtherSidHex);
        await using var lease = await fixture.AcquireLeaseAsync();

        var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
            () => fixture.Host.ProvisionEmptyCatalogueAsync(fixture.Plan.Sql, CancellationToken.None).AsTask());

        Assert.Equal("sql-bootstrap-postcondition-failed", exception.Message);
    }

    private sealed class ExecutorOrderingFixture : IDisposable
    {
        private readonly string _root;
        private readonly NativeGoLiveCloseoutCapability _capability;

        public ExecutorOrderingFixture(
            NativeGoLiveSqlPreflightObservation? sqlPreflight = null,
            bool failBootstrap = false,
            string bootstrapFailureCode = "native-go-live-bootstrap-install-sql-batch-1-failed",
            bool failAdmission = false,
            bool cancelAdmission = false,
            bool failVss = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeLiveGateOrdering", Guid.NewGuid().ToString("N"));
            var payloadRoot = CreatePayloadRoot(_root);
            Plan = NativeGoLivePlan.CreateForIsolatedTests(
                LiveRootLayout.CreateForIsolatedTests(Path.Combine(_root, "live")),
                new string('a', 40));
            Directory.CreateDirectory(Plan.Layout.Root);
            File.WriteAllText(Path.Combine(Plan.Layout.Root, "pre-wipe-sentinel.txt"), "wipe-me");
            var manifest = NativeGoLivePayloadHasher.Compute(payloadRoot);
            _capability = new NativeGoLiveCloseoutCapabilityIssuer().Issue(Plan, payloadRoot, manifest.Sha256);
            Request = new NativeGoLiveRequest(
                Plan, false, true, true, true, true, payloadRoot, manifest.Sha256, manifest);
            var vss = new RecordingVssPort(Plan, Events, failVss);
            Acls = new OrderingAclPort(Plan, Events);
            var bootstrap = NativeGoLiveSqlBootstrap.Parse(CanonicalBootstrap);
            var preflight = new NativeGoLiveWindowsPreflightPort(
                Plan,
                new NativeGoLiveWindowsPreflightSources(
                    plan => CanonicalIis(plan),
                    (_, _) =>
                    {
                        Events.Add("windows-preflight-sql");
                        Assert.False(Directory.Exists(Plan.Layout.Root));
                        Assert.False(Directory.Exists(Plan.Layout.SqlDataRoot));
                        Assert.False(Directory.Exists(Plan.Layout.SqlLogRoot));
                        return ValueTask.FromResult(sqlPreflight ?? ValidPreflight());
                    },
                    _ => DisabledRuntime(),
                    (_, _) => ValueTask.FromResult(MissingMarketplace(Plan)),
                    _ => vss.Observation));
            Host = new GuardedNativeGoLiveHost(
                _capability,
                Plan,
                payloadRoot,
                bootstrap,
                new NativeGoLiveHostPorts(
                    preflight,
                    new RecordingIisPort(Plan, Events),
                    new DisposableOwnedStatePort(Plan, Events),
                    new OrderingSqlPort(Plan, Acls, Events),
                    Acls,
                    null!,
                    null!,
                    null!,
                    vss,
                    null,
                    new DisposableAdmissionPort(Plan, Events, failAdmission, cancelAdmission)),
                (connectionString, _) =>
                {
                    Assert.Equal(bootstrap.ConnectionString, connectionString);
                    Events.Add("install-direct-admin-bootstrap");
                    return failBootstrap
                        ? Task.FromException(new NativeGoLiveContractException(
                            bootstrapFailureCode))
                        : Task.CompletedTask;
                });
        }

        public NativeGoLivePlan Plan { get; }
        public NativeGoLiveRequest Request { get; }
        public GuardedNativeGoLiveHost Host { get; }
        internal OrderingAclPort Acls { get; }
        public List<string> Events { get; } = [];

        public void BeginExecution() => Assert.True(_capability.TryBeginExecution());

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class PostBootstrapFixture : IDisposable
    {
        private readonly string _root;
        private readonly NativeGoLiveCloseoutCapability _capability;
        private readonly NativeGoLiveRequest _request;

        public PostBootstrapFixture(
            bool appPoolIsSysAdmin = true,
            string? catalogueOwnerSidHex = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeLiveGateAuthority", Guid.NewGuid().ToString("N"));
            var payloadRoot = CreatePayloadRoot(_root);
            Plan = NativeGoLivePlan.CreateForIsolatedTests(
                LiveRootLayout.CreateForIsolatedTests(Path.Combine(_root, "live")),
                new string('a', 40));
            var manifest = NativeGoLivePayloadHasher.Compute(payloadRoot);
            _capability = new NativeGoLiveCloseoutCapabilityIssuer().Issue(Plan, payloadRoot, manifest.Sha256);
            _request = new NativeGoLiveRequest(
                Plan, false, true, true, true, true, payloadRoot, manifest.Sha256, manifest);
            Host = new GuardedNativeGoLiveHost(
                _capability,
                Plan,
                payloadRoot,
                NativeGoLiveSqlBootstrap.Parse(CanonicalBootstrap),
                new NativeGoLiveHostPorts(
                    null!,
                    null!,
                    null!,
                    new StaticPostBootstrapSqlPort(
                        ValidPostBootstrap(Plan) with
                        {
                            AppPoolLoginIsSysAdmin = appPoolIsSysAdmin,
                            CatalogueOwnerSidHex = catalogueOwnerSidHex ?? AppPoolSidHex
                        }),
                    new ExactAclPort(Plan),
                    null!,
                    null!,
                    null!,
                    null!));
        }

        public NativeGoLivePlan Plan { get; }
        public GuardedNativeGoLiveHost Host { get; }

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

    private sealed class DisposableAdmissionPort(
        NativeGoLivePlan plan,
        List<string> events,
        bool failAdmission,
        bool cancelAdmission) : INativeGoLiveAdmissionPort
    {
        public ValueTask<NativeGoLiveOneShotAdmission> ObserveAsync(CancellationToken _)
        {
            events.Add(Directory.Exists(plan.Layout.Root) ? "admission-observe-present" : "admission-observe-absent");
            return ValueTask.FromResult(new NativeGoLiveOneShotAdmission(Directory.Exists(plan.Layout.Root), false));
        }

        public ValueTask WipeAsync(CancellationToken _)
        {
            if (failAdmission)
                return ValueTask.FromException(new NativeGoLiveContractException("admission-failure"));
            if (cancelAdmission)
                return ValueTask.FromException(new OperationCanceledException());
            events.Add("admission-wipe");
            Directory.Delete(plan.Layout.Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableOwnedStatePort(NativeGoLivePlan plan, List<string> events) : INativeGoLiveOwnedStatePort
    {
        public ValueTask WipeRootAsync(CancellationToken _) => throw new NotSupportedException();

        public ValueTask CreateEmptyRootAsync(CancellationToken _)
        {
            events.Add("create-empty-root");
            Directory.CreateDirectory(plan.Layout.Root);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteProductionConfigurationAsync(CancellationToken _) => throw new NotSupportedException();
    }

    private sealed class RecordingIisPort(NativeGoLivePlan plan, List<string> events) : INativeGoLiveIisPort
    {
        public NativeGoLiveIisObservation ReplaceCanonical(NativeGoLivePlan _)
        {
            events.Add("replace-canonical-iis");
            return CanonicalIis(plan);
        }

        public ValueTask<NativeGoLivePoolObservation> StopAsync(string _, CancellationToken __)
        {
            events.Add("stop-iis");
            return ValueTask.FromResult(new NativeGoLivePoolObservation(plan.AppPoolName, true, "Stopped", true));
        }

        public ValueTask<NativeGoLivePoolObservation> RestoreAsync(string _, CancellationToken __) =>
            throw new NotSupportedException();

        public ValueTask<NativeGoLivePoolObservation> StartAsync(string _, CancellationToken __) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingVssPort : INativeGoLiveVssPort
    {
        public RecordingVssPort(NativeGoLivePlan plan, List<string> events, bool fail)
        {
            _events = events;
            _fail = fail;
            const ulong capacity = 20_000_000_000;
            var maximum = checked((ulong)decimal.Floor(capacity * plan.Vss.MaximumStorageFraction));
            var state = new VssDiffAreaState(VssAssociationState.ExactExisting, "V:", "V:", maximum);
            Observation = new NativeGoLiveVssPreflightObservation(state, capacity, maximum);
        }

        private readonly List<string> _events;
        private readonly bool _fail;
        public NativeGoLiveVssPreflightObservation Observation { get; }

        public NativeGoLiveVssPreflightObservation Query(CancellationToken _) => Observation;

        public NativeGoLiveVssMutationObservation Ensure(
            NativeGoLiveVssPolicy _, NativeGoLiveVssPreflightObservation expected, CancellationToken __)
        {
            _events.Add("configure-vss");
            if (_fail)
            {
                var failed = expected.Association with { State = VssAssociationState.Failed };
                return new NativeGoLiveVssMutationObservation(
                    expected.Association, failed, NativeGoLiveVssAction.ChangeDiffAreaMaximumSize);
            }
            return new NativeGoLiveVssMutationObservation(
                expected.Association, expected.Association, NativeGoLiveVssAction.ChangeDiffAreaMaximumSize);
        }
    }

    internal sealed class OrderingAclPort(NativeGoLivePlan plan, List<string> events) : INativeGoLiveAclPort
    {
        public bool AppliedAfterRootCreation { get; private set; }

        public ValueTask<NativeGoLiveAclObservation> ApplyAndObserveAsync(NativeGoLivePlan _, CancellationToken __)
        {
            events.Add("apply-acls");
            AppliedAfterRootCreation = Directory.Exists(plan.Layout.Root);
            if (!AppliedAfterRootCreation)
                return ValueTask.FromException<NativeGoLiveAclObservation>(
                    new NativeGoLiveContractException("acl-applied-before-root-creation"));
            Directory.CreateDirectory(plan.Layout.SqlDataRoot);
            Directory.CreateDirectory(plan.Layout.SqlLogRoot);
            return ValueTask.FromResult(ExactAcl(plan));
        }

        public ValueTask<NativeGoLiveAclObservation> ObserveEffectiveAsync(NativeGoLivePlan _, CancellationToken __) =>
            throw new NativeGoLiveContractException("acl-observed-before-hierarchy-application");
    }

    private sealed class ExactAclPort(NativeGoLivePlan plan) : INativeGoLiveAclPort
    {
        public ValueTask<NativeGoLiveAclObservation> ApplyAndObserveAsync(NativeGoLivePlan _, CancellationToken __) =>
            ValueTask.FromResult(ExactAcl(plan));

        public ValueTask<NativeGoLiveAclObservation> ObserveEffectiveAsync(NativeGoLivePlan _, CancellationToken __) =>
            ValueTask.FromResult(ExactAcl(plan));
    }

    private sealed class OrderingSqlPort(
        NativeGoLivePlan plan,
        OrderingAclPort acls,
        List<string> events) : INativeGoLiveSqlPort
    {
        public ValueTask<NativeGoLiveSqlPostBootstrapObservation> ProvisionAndObserveAsync(
            NativeGoLiveSqlIdentity _,
            NativeGoLiveSqlBootstrapConnection __,
            NativeGoLivePayloadManifest ___,
            CancellationToken ____)
        {
            events.Add("provision-sql");
            if (!acls.AppliedAfterRootCreation ||
                !Directory.Exists(plan.Layout.SqlDataRoot) ||
                !Directory.Exists(plan.Layout.SqlLogRoot))
                return ValueTask.FromException<NativeGoLiveSqlPostBootstrapObservation>(
                    new NativeGoLiveContractException("sql-provisioned-before-hierarchy-acls"));
            return ValueTask.FromException<NativeGoLiveSqlPostBootstrapObservation>(
                new NativeGoLiveContractException("stop-after-ordered-provision"));
        }
    }

    private sealed class StaticPostBootstrapSqlPort(NativeGoLiveSqlPostBootstrapObservation observation) : INativeGoLiveSqlPort
    {
        public ValueTask<NativeGoLiveSqlPostBootstrapObservation> ProvisionAndObserveAsync(
            NativeGoLiveSqlIdentity _,
            NativeGoLiveSqlBootstrapConnection __,
            NativeGoLivePayloadManifest ___,
            CancellationToken ____) => ValueTask.FromResult(observation);
    }

    private static NativeGoLiveIisObservation CanonicalIis(NativeGoLivePlan plan) =>
        new(plan.IisSiteName, plan.AppPoolName, plan.Layout.ApplicationRoot, true, false,
            [new NativeGoLiveIisBinding("http", "127.0.0.1", plan.LoopbackPort, string.Empty)]);

    private static NativeGoLiveRuntimeObservation DisabledRuntime() => new(false, false, false, false, false, false);

    private static NativeGoLiveMarketplaceObservation MissingMarketplace(NativeGoLivePlan plan) =>
        new("Missing", plan.Codex.MarketplaceName, plan.Codex.MarketplaceRoot, plan.Codex.PluginName);

    private static NativeGoLiveSqlPreflightObservation ValidPreflight() => new(
        true,
        @"IIS AppPool\FluxKnowledge",
        AppPoolSid,
        true,
        AppPoolSid,
        AppPoolSidHex,
        true,
        ValidProcedures());

    private static NativeGoLiveSqlPostBootstrapObservation ValidPostBootstrap(NativeGoLivePlan plan) => new(
        "FluxKnowledge",
        5,
        AppPoolSidHex,
        [
            new NativeGoLiveSqlDatabaseFileObservation(1, "ROWS", plan.Sql.DataFilePath),
            new NativeGoLiveSqlDatabaseFileObservation(2, "LOG", plan.Sql.LogFilePath)
        ],
        true,
        NativeGoLiveDatabaseContract.RequiredMigrations,
        NativeGoLiveDatabaseContract.RequiredMigrations,
        true,
        true,
        true,
        @"IIS AppPool\FluxKnowledge",
        AppPoolSid,
        true,
        AppPoolSid,
        AppPoolSidHex,
        true,
        false,
        0,
        0,
        0,
        0,
        ValidProcedures());

    private static IReadOnlyList<NativeGoLiveSqlProcedureObservation> ValidProcedures() =>
    [
        Procedure("FluxKnowledgeNativeGoLiveCreate", 101,
        [
            new("@Catalogue", "nvarchar", 256, false),
            new("@DataFile", "nvarchar", 520, false),
            new("@LogFile", "nvarchar", 520, false),
            new("@AppPoolLogin", "nvarchar", 256, false),
            new("@AppPoolSid", "varbinary", 85, false)
        ]),
        Procedure("FluxKnowledgeNativeGoLiveDrop", 102, [new("@Catalogue", "nvarchar", 256, false)]),
        Procedure("FluxKnowledgeNativeGoLiveManageAppPool", 103,
        [
            new("@Catalogue", "nvarchar", 256, false),
            new("@AppPoolLogin", "nvarchar", 256, false),
            new("@AppPoolSid", "varbinary", 85, false),
            new("@BootstrapLogin", "nvarchar", 256, false)
        ]),
        Procedure("FluxKnowledgeNativeGoLiveObserveAppPool", 104,
        [
            new("@Catalogue", "nvarchar", 256, false),
            new("@AppPoolLogin", "nvarchar", 256, false)
        ])
    ];

    private static NativeGoLiveSqlProcedureObservation Procedure(
        string name,
        int objectId,
        IReadOnlyList<NativeGoLiveSqlParameterObservation> parameters) => new(
        name,
        objectId,
        NativeGoLiveSqlBootstrapAuthorityContract.DefinitionSha256(name),
        parameters);

    private static NativeGoLiveAclObservation ExactAcl(NativeGoLivePlan plan)
    {
        const long read = 131209;
        const long readAndExecute = 131241;
        const long modify = 197055;
        const long fullControl = 2032127;
        var layout = plan.Layout;
        var modifyPaths = new[]
        {
            Path.Combine(layout.ConfigRoot, "data-protection"), layout.IndexRoot, layout.RetainedRoot,
            layout.SpoolRoot, layout.TempRoot, layout.LogsRoot
        };
        var boundaryPaths = new[]
        {
            layout.Root, layout.DataRoot, layout.SqlRoot, layout.RuntimeRoot, layout.CodexPluginRoot, layout.RecoveryRoot
        };
        NativeGoLiveAclPathObservation PathWithRules(string path, params (string Sid, long Rights)[] additions) =>
            new(path, true,
            [
                Ace("S-1-5-18", fullControl),
                Ace("S-1-5-32-544", fullControl),
                .. additions.Select(item => Ace(item.Sid, item.Rights))
            ]);
        var paths = boundaryPaths.Select(path => PathWithRules(path))
            .Append(PathWithRules(layout.ApplicationRoot, (AppPoolSid, readAndExecute)))
            .Append(PathWithRules(layout.ConfigRoot, (AppPoolSid, read)))
            .Concat(modifyPaths.Select(path => PathWithRules(path, (AppPoolSid, modify))))
            .Append(PathWithRules(layout.SqlDataRoot, (SqlServiceSid, modify)))
            .Append(PathWithRules(layout.SqlLogRoot, (SqlServiceSid, modify)))
            .ToArray();
        return new NativeGoLiveAclObservation(
            [layout.SqlDataRoot, layout.SqlLogRoot],
            [layout.ApplicationRoot],
            [layout.ConfigRoot],
            modifyPaths,
            false, false, false, false, true,
            AppPoolSid,
            SqlServiceSid,
            paths);
    }

    private static NativeGoLiveAclAceObservation Ace(string sid, long rights) =>
        new(sid, rights, true, false, 3, 0, true, true, true);

    private static string CreatePayloadRoot(string root)
    {
        var payloadRoot = Path.Combine(root, "payload");
        Directory.CreateDirectory(payloadRoot);
        File.WriteAllText(Path.Combine(payloadRoot, "payload.dll"), "one-shot-payload");
        return payloadRoot;
    }

    private const string AppPoolSid = "S-1-5-21-101-202-303-404";
    private const string AppPoolSidHex = "01050000000000051500000065000000ca0000002f01000094010000";
    private const string OtherSidHex = "01050000000000051500000065000000ca0000002f01000095010000";
    private const string SqlServiceSid = "S-1-5-21-501-602-703-804";
}
