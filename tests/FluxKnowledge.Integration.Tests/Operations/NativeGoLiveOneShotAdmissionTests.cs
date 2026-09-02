using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using System.Text.Json;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

public sealed class NativeGoLiveOneShotAdmissionTests
{
    [Fact]
    public void Native_go_live_request_requires_an_explicit_legacy_removal_acknowledgement()
    {
        Assert.NotNull(typeof(NativeGoLiveRequest).GetProperty("ConfirmRemoveLegacyPlugin"));
    }

    [Fact]
    public async Task SQL_storage_directory_failure_has_only_a_bounded_hresult_detail()
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeSqlStorage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var blockedRoot = Path.Combine(root, "blocked-live-root");
            await File.WriteAllTextAsync(blockedRoot, "blocked");
            var payloadRoot = Path.Combine(root, "payload");
            Directory.CreateDirectory(payloadRoot);
            await File.WriteAllTextAsync(Path.Combine(payloadRoot, "payload.dll"), "one-shot-payload");
            var plan = NativeGoLivePlan.CreateForIsolatedTests(
                LiveRootLayout.CreateForIsolatedTests(blockedRoot), new string('a', 40));
            var bootstrap = NativeGoLiveSqlBootstrap.Parse(
                "Data Source=localhost;Initial Catalog=master;Integrated Security=True;" +
                "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
                "Pooling=False;Application Name=FluxKnowledge.NativeGoLive");
            var payload = NativeGoLivePayloadHasher.Compute(payloadRoot);
            var port = new NativeGoLiveWindowsSqlPort(plan, payloadRoot);

            var exception = await Assert.ThrowsAsync<NativeGoLiveContractException>(
                () => port.ProvisionEmptyCatalogueAsync(plan.Sql, bootstrap, payload, CancellationToken.None).AsTask());

            Assert.Equal("sql-provisioning-storage-data-failed", exception.ReasonCode);
            Assert.Matches(@"\Ahresult-0x[0-9A-F]{8}\z", exception.DiagnosticDetail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Empty_root_creation_creates_the_required_runtime_hierarchy()
    {
        using var fixture = new DisposableAdmissionFixture(catalogueExists: false);

        await fixture.OwnedState.CreateEmptyRootAsync(CancellationToken.None);

        Assert.True(Directory.Exists(fixture.Layout.SqlDataRoot));
        Assert.True(Directory.Exists(fixture.Layout.SqlLogRoot));
        Assert.True(Directory.Exists(fixture.Layout.IndexRoot));
        Assert.True(Directory.Exists(fixture.Layout.RetainedRoot));
        Assert.True(Directory.Exists(fixture.Layout.SpoolRoot));
        Assert.True(Directory.Exists(fixture.Layout.TempRoot));
        Assert.True(Directory.Exists(fixture.Layout.LogsRoot));
    }

    [Fact]
    public async Task Missing_legacy_removal_acknowledgement_rejects_before_mutation()
    {
        var host = new OneShotAdmissionHost(NativeGoLiveDeploymentState.Absent);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(
            ConfirmedRequest() with { ConfirmRemoveLegacyPlugin = false }, host);

        Assert.False(result.Succeeded);
        Assert.Equal("go-live-acknowledgement-required", result.ReasonCode);
        Assert.Empty(host.Mutations);
    }

    [Fact]
    public void Production_configuration_admits_provisioned_worker_and_Outlook_operations_only()
    {
        var plan = NativeGoLivePlan.CreateForIsolatedTests(
            LiveRootLayout.CreateForIsolatedTests(Path.Combine(Path.GetTempPath(), "FluxKnowledgeRuntime", Guid.NewGuid().ToString("N"))),
            new string('a', 40));
        const string connectionString =
            "Data Source=localhost;Initial Catalog=FluxKnowledge;Integrated Security=True;" +
            "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
            "Pooling=False;Application Name=FluxKnowledge.Native";

        var bytes = NativeGoLiveProductionConfigurationSerializer.Serialize(plan, connectionString);

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.True(root.GetProperty("Worker").GetProperty("Enabled").GetBoolean());
        Assert.True(root.GetProperty("OutlookCapture").GetProperty("Enabled").GetBoolean());
        var runtime = root.GetProperty("Runtime");
        foreach (var provider in new[] { "Model", "Gpu", "Ocr", "Asr", "Ffmpeg", "NetworkParsing" })
        {
            Assert.False(runtime.GetProperty(provider).GetProperty("Enabled").GetBoolean());
        }

        NativeGoLiveRuntimeConfiguration.ValidateProductionConfiguration(bytes, plan, connectionString);
    }

    [Fact]
    public void Production_configuration_does_not_admit_an_unready_runtime_provider()
    {
        var plan = NativeGoLivePlan.CreateForIsolatedTests(
            LiveRootLayout.CreateForIsolatedTests(Path.Combine(Path.GetTempPath(), "FluxKnowledgeRuntime", Guid.NewGuid().ToString("N"))),
            new string('a', 40));
        const string connectionString =
            "Data Source=localhost;Initial Catalog=FluxKnowledge;Integrated Security=True;" +
            "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
            "Pooling=False;Application Name=FluxKnowledge.Native";
        var bytes = NativeGoLiveProductionConfigurationSerializer.Serialize(plan, connectionString);
        var providerEnabled = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(bytes).Replace(
                "\"Model\":{\"Enabled\":false}",
                "\"Model\":{\"Enabled\":true}",
                StringComparison.Ordinal));
        var mergedMainRoot = Path.Combine(Path.GetTempPath(), "FluxKnowledgeRuntime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mergedMainRoot);
        try
        {
            File.WriteAllBytes(Path.Combine(mergedMainRoot, "appsettings.json"), providerEnabled);

            var exception = Assert.Throws<NativeGoLiveContractException>(() =>
                NativeGoLiveRuntimeConfiguration.ReadMergedMain(plan, mergedMainRoot));

            Assert.Equal("runtime-provider-not-ready:model", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(mergedMainRoot, recursive: true);
        }
    }

    [Fact]
    public void Production_configuration_does_not_admit_a_legacy_provider_activation_beside_a_disabled_nested_provider()
    {
        var plan = NativeGoLivePlan.CreateForIsolatedTests(
            LiveRootLayout.CreateForIsolatedTests(Path.Combine(Path.GetTempPath(), "FluxKnowledgeRuntime", Guid.NewGuid().ToString("N"))),
            new string('a', 40));
        const string connectionString =
            "Data Source=localhost;Initial Catalog=FluxKnowledge;Integrated Security=True;" +
            "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
            "Pooling=False;Application Name=FluxKnowledge.Native";
        var bytes = NativeGoLiveProductionConfigurationSerializer.Serialize(plan, connectionString);
        var conflictingProviders = System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(bytes).Replace(
                "\"Model\":{\"Enabled\":false}",
                "\"Model\":{\"Enabled\":false},\"ModelRuntimeEnabled\":true",
                StringComparison.Ordinal));
        var mergedMainRoot = Path.Combine(Path.GetTempPath(), "FluxKnowledgeRuntime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mergedMainRoot);
        try
        {
            File.WriteAllBytes(Path.Combine(mergedMainRoot, "appsettings.json"), conflictingProviders);

            var exception = Assert.Throws<NativeGoLiveContractException>(() =>
                NativeGoLiveRuntimeConfiguration.ReadMergedMain(plan, mergedMainRoot));

            Assert.Equal("runtime-provider-not-ready:model", exception.ReasonCode);
        }
        finally
        {
            Directory.Delete(mergedMainRoot, recursive: true);
        }
    }

    private static readonly NativeGoLivePlan Plan = NativeGoLivePlan.CreateProduction(new string('a', 40));

    [Fact]
    public async Task Concrete_absent_root_and_catalogue_are_observed_as_admissible_without_mutation()
    {
        using var fixture = new DisposableAdmissionFixture(catalogueExists: false);

        var observed = await fixture.Admission.ObserveAsync(CancellationToken.None);

        Assert.True(observed.IsAbsent);
        Assert.False(File.Exists(fixture.Layout.Root));
        Assert.False(Directory.Exists(fixture.Layout.Root));
        Assert.Equal(0, fixture.Catalogue.DropCalls);
    }

    [Fact]
    public async Task Concrete_root_file_and_catalogue_are_wiped_and_reobserved_as_absent()
    {
        using var fixture = new DisposableAdmissionFixture(catalogueExists: true);
        File.WriteAllText(fixture.Layout.Root, "unknown-root-file");

        Assert.False((await fixture.Admission.ObserveAsync(CancellationToken.None)).IsAbsent);

        await fixture.Admission.WipeAsync(CancellationToken.None);

        Assert.True((await fixture.Admission.ObserveAsync(CancellationToken.None)).IsAbsent);
        Assert.False(File.Exists(fixture.Layout.Root));
        Assert.False(Directory.Exists(fixture.Layout.Root));
        Assert.Equal(1, fixture.Catalogue.DropCalls);
    }

    [Fact]
    public async Task Concrete_attached_catalogue_files_are_dropped_before_the_root_is_deleted()
    {
        using var fixture = new DisposableAdmissionFixture(catalogueExists: true);
        Directory.CreateDirectory(fixture.Layout.SqlDataRoot);
        Directory.CreateDirectory(fixture.Layout.SqlLogRoot);
        var dataFile = Path.Combine(fixture.Layout.SqlDataRoot, "FluxKnowledge.mdf");
        var logFile = Path.Combine(fixture.Layout.SqlLogRoot, "FluxKnowledge_log.ldf");
        File.WriteAllText(dataFile, "attached-data-file");
        File.WriteAllText(logFile, "attached-log-file");
        fixture.Catalogue.AttachFiles(dataFile, logFile);

        await fixture.Admission.WipeAsync(CancellationToken.None);

        Assert.Equal(1, fixture.Catalogue.DropCalls);
        Assert.True(fixture.Catalogue.DroppedWhileAttachedFilesExisted);
        Assert.True((await fixture.Admission.ObserveAsync(CancellationToken.None)).IsAbsent);
    }

    [Fact]
    public async Task Concrete_existing_root_and_catalogue_without_confirmation_do_not_mutate()
    {
        using var fixture = new DisposableAdmissionFixture(catalogueExists: true);
        File.WriteAllText(fixture.Layout.Root, "unknown-root-file");
        var host = new ConcreteAdmissionHost(fixture.Admission);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(
            ConfirmedRequest() with { ConfirmCleanSlate = false }, host);

        Assert.False(result.Succeeded);
        Assert.Equal("go-live-acknowledgement-required", result.ReasonCode);
        Assert.True(File.Exists(fixture.Layout.Root));
        Assert.True(fixture.Catalogue.Exists);
        Assert.Equal(0, fixture.Catalogue.DropCalls);
    }

    [Fact]
    public async Task Absent_root_and_catalogue_are_admitted_without_a_wipe()
    {
        var host = new OneShotAdmissionHost(NativeGoLiveDeploymentState.Absent);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(ConfirmedRequest(), host);

        Assert.True(result.Succeeded);
        Assert.Equal(1, host.AdmissionCalls);
        Assert.Equal(0, host.WipeCalls);
        Assert.Contains("one-shot-preflight", host.Calls);
        Assert.DoesNotContain("legacy-preflight", host.Calls);
        Assert.DoesNotContain("read-journal", host.Calls);
        Assert.DoesNotContain("compare-and-swap-journal", host.Calls);
        Assert.DoesNotContain("write-root-marker", host.Mutations);
    }

    [Theory]
    [InlineData(NativeGoLiveDeploymentState.Historic)]
    [InlineData(NativeGoLiveDeploymentState.Marker)]
    [InlineData(NativeGoLiveDeploymentState.Journal)]
    [InlineData(NativeGoLiveDeploymentState.Partial)]
    [InlineData(NativeGoLiveDeploymentState.Unknown)]
    public async Task Confirmed_same_invocation_wipes_every_existing_deployment_state(
        NativeGoLiveDeploymentState state)
    {
        var host = new OneShotAdmissionHost(state);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(ConfirmedRequest(), host);

        Assert.True(result.Succeeded);
        Assert.Equal(1, host.AdmissionCalls);
        Assert.Equal(1, host.WipeCalls);
        Assert.Equal(["stop-pool", "wipe-root-and-catalogue"], host.Mutations.Take(2));
        Assert.DoesNotContain("read-journal", host.Calls);
        Assert.DoesNotContain("compare-and-swap-journal", host.Calls);
    }

    [Theory]
    [InlineData(NativeGoLiveDeploymentState.Historic)]
    [InlineData(NativeGoLiveDeploymentState.Marker)]
    [InlineData(NativeGoLiveDeploymentState.Journal)]
    [InlineData(NativeGoLiveDeploymentState.Partial)]
    [InlineData(NativeGoLiveDeploymentState.Unknown)]
    public async Task Existing_deployment_state_without_wipe_confirmation_fails_before_mutation(
        NativeGoLiveDeploymentState state)
    {
        var host = new OneShotAdmissionHost(state);
        var request = ConfirmedRequest() with { ConfirmCleanSlate = false };

        var result = await new NativeGoLiveExecutor().ExecuteAsync(request, host);

        Assert.False(result.Succeeded);
        Assert.Equal("go-live-acknowledgement-required", result.ReasonCode);
        Assert.Equal(0, host.AdmissionCalls);
        Assert.Equal(0, host.WipeCalls);
        Assert.Empty(host.Mutations);
    }

    [Fact]
    public async Task Successful_go_live_removes_legacy_before_native_registration_and_validation()
    {
        var host = new OneShotAdmissionHost(NativeGoLiveDeploymentState.Absent);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(ConfirmedRequest(), host);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                "stop-pool",
                "configure-vss",
                "create-empty-root",
                "provision-empty-catalogue",
                "publish-and-start",
                "activate-native-tasks",
                "remove-legacy-plugin",
                "register-marketplace",
                "validate"
            ],
            host.Mutations);
    }

    [Fact]
    public async Task Legacy_removal_failure_prevents_native_registration()
    {
        var host = new OneShotAdmissionHost(NativeGoLiveDeploymentState.Absent, failLegacyRemoval: true);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(ConfirmedRequest(), host);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy-plugin-removal-failed", result.ReasonCode);
        Assert.DoesNotContain("register-marketplace", host.Mutations);
    }

    [Fact]
    public async Task Native_registration_failure_never_restores_legacy_plugin()
    {
        var host = new OneShotAdmissionHost(NativeGoLiveDeploymentState.Absent, failNativeInstall: true);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(ConfirmedRequest(), host);

        Assert.False(result.Succeeded);
        Assert.Equal("native-install-failed", result.ReasonCode);
        Assert.DoesNotContain("validate", host.Mutations);
        Assert.Contains("remove-legacy-plugin", host.Mutations);
        Assert.DoesNotContain("restore-legacy-plugin", host.Mutations);
    }

    [Fact]
    public async Task Failed_task_activation_prevents_native_registration_and_legacy_removal()
    {
        var host = new OneShotAdmissionHost(NativeGoLiveDeploymentState.Absent, failTaskActivation: true);

        var result = await new NativeGoLiveExecutor().ExecuteAsync(ConfirmedRequest(), host);

        Assert.False(result.Succeeded);
        Assert.Equal("native-task-activation-failed", result.ReasonCode);
        Assert.DoesNotContain("register-marketplace", host.Mutations);
        Assert.DoesNotContain("remove-legacy-plugin", host.Mutations);
    }

    private static NativeGoLiveRequest ConfirmedRequest() => new(Plan, false, true, true, true, true, true);

    public enum NativeGoLiveDeploymentState
    {
        Absent,
        Historic,
        Marker,
        Journal,
        Partial,
        Unknown
    }

    private sealed class OneShotAdmissionHost(
        NativeGoLiveDeploymentState state,
        bool failNativeProof = false,
        bool failNativeInstall = false,
        bool failTaskActivation = false,
        bool failLegacyRemoval = false) : INativeGoLiveHost
    {
        public List<string> Calls { get; } = [];
        public List<string> Mutations { get; } = [];
        public int AdmissionCalls { get; private set; }
        public int WipeCalls { get; private set; }

        public ValueTask<INativeGoLiveLease> AcquireLeaseAsync(
            NativeGoLiveRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add("acquire-lease");
            return ValueTask.FromResult<INativeGoLiveLease>(new Lease(Calls));
        }

        public ValueTask PrepareHostPrerequisitesAsync(NativeGoLivePlan _, CancellationToken __)
        {
            Calls.Add("prepare-host-prerequisites");
            return ValueTask.CompletedTask;
        }

        // This is deliberately not part of the old host contract.  The red test requires the
        // executor to enter the one-shot admission path before any operational mutation.
        public ValueTask AdmitAndWipeAsync(NativeGoLiveRequest request, CancellationToken cancellationToken)
        {
            AdmissionCalls++;
            if (state == NativeGoLiveDeploymentState.Absent) return ValueTask.CompletedTask;
            if (!request.ConfirmCleanSlate)
                throw new NativeGoLiveContractException("go-live-wipe-confirmation-required");
            WipeCalls++;
            Mutations.Add("wipe-root-and-catalogue");
            return ValueTask.CompletedTask;
        }

        public ValueTask PreflightAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("legacy-preflight");
            return ValueTask.FromException(new InvalidOperationException("legacy-preflight-must-not-run"));
        }

        public ValueTask VerifyOneShotPreflightAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("one-shot-preflight");
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> StopPoolAsync(CancellationToken cancellationToken) => Mutate("stop-pool", true);
        public ValueTask RestorePoolAsync(CancellationToken cancellationToken) => Mutate("restore-pool");
        public ValueTask ConfigureVssAsync(NativeGoLiveVssPolicy policy, CancellationToken cancellationToken) =>
            Mutate("configure-vss");
        public ValueTask CreateEmptyRootAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) =>
            Mutate("create-empty-root");
        public ValueTask ProvisionEmptyCatalogueAsync(NativeGoLiveSqlIdentity sql, CancellationToken cancellationToken) =>
            Mutate("provision-empty-catalogue");
        public ValueTask PublishAndStartAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) =>
            Mutate("publish-and-start");
        public ValueTask ActivateNativeTasksAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) =>
            failTaskActivation
                ? ValueTask.FromException(new NativeGoLiveContractException("native-task-activation-failed"))
                : Mutate("activate-native-tasks");
        public ValueTask ValidateAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("validate");
            if (failNativeProof)
                return ValueTask.FromException(new NativeGoLiveContractException("native-proof-failed"));
            Mutations.Add("validate");
            return ValueTask.CompletedTask;
        }
        public ValueTask RegisterMarketplaceAsync(NativeGoLiveCodexIdentity codex, CancellationToken cancellationToken) =>
            failNativeInstall
                ? ValueTask.FromException(new NativeGoLiveContractException("native-install-failed"))
                : Mutate("register-marketplace");
        public ValueTask RemoveLegacyPluginAsync(CancellationToken cancellationToken) =>
            failLegacyRemoval
                ? ValueTask.FromException(new NativeGoLiveContractException("legacy-plugin-removal-failed"))
                : Mutate("remove-legacy-plugin");

        private ValueTask Mutate(string operation)
        {
            Calls.Add(operation);
            Mutations.Add(operation);
            return ValueTask.CompletedTask;
        }

        private ValueTask<bool> Mutate(string operation, bool value)
        {
            Calls.Add(operation);
            Mutations.Add(operation);
            return ValueTask.FromResult(value);
        }

        private sealed class Lease(List<string> calls) : INativeGoLiveLease
        {
            public ValueTask DisposeAsync()
            {
                calls.Add("release-lease");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ConcreteAdmissionHost(INativeGoLiveAdmissionPort admission) : INativeGoLiveHost
    {
        public ValueTask<INativeGoLiveLease> AcquireLeaseAsync(
            NativeGoLiveRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<INativeGoLiveLease>(new Lease());

        public async ValueTask AdmitAndWipeAsync(NativeGoLiveRequest request, CancellationToken cancellationToken)
        {
            var observed = await admission.ObserveAsync(cancellationToken);
            if (observed.IsAbsent) return;
            if (!request.ConfirmCleanSlate)
                throw new NativeGoLiveContractException("go-live-wipe-confirmation-required");
            await admission.WipeAsync(cancellationToken);
            if (!(await admission.ObserveAsync(cancellationToken)).IsAbsent)
                throw new NativeGoLiveContractException("go-live-wipe-not-proved");
        }

        public ValueTask PrepareHostPrerequisitesAsync(NativeGoLivePlan _, CancellationToken __) => ValueTask.CompletedTask;

        public ValueTask VerifyOneShotPreflightAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> StopPoolAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask RestorePoolAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ConfigureVssAsync(NativeGoLiveVssPolicy policy, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask CreateEmptyRootAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ProvisionEmptyCatalogueAsync(NativeGoLiveSqlIdentity sql, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask PublishAndStartAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ValidateAsync(NativeGoLivePlan plan, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask RegisterMarketplaceAsync(NativeGoLiveCodexIdentity codex, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        private sealed class Lease : INativeGoLiveLease
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableAdmissionFixture : IDisposable
    {
        private readonly string _root;

        public DisposableAdmissionFixture(bool catalogueExists)
        {
            _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeOneShotAdmission", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Layout = LiveRootLayout.CreateForIsolatedTests(Path.Combine(_root, "live"));
            var plan = NativeGoLivePlan.CreateForIsolatedTests(Layout, new string('a', 40));
            var bootstrap = NativeGoLiveSqlBootstrap.Parse(
                "Data Source=localhost;Initial Catalog=master;Integrated Security=True;" +
                "Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;" +
                "Pooling=False;Application Name=FluxKnowledge.NativeGoLive");
            var sql = new NativeGoLiveWindowsSqlPort(plan, _root);
            OwnedState = new NativeGoLiveWindowsOwnedStatePort(plan, bootstrap);
            Catalogue = new DisposableCatalogue(catalogueExists);
            Admission = new NativeGoLiveWindowsOneShotAdmissionPort(
                plan,
                OwnedState,
                Catalogue.ExistsAsync,
                Catalogue.DropAsync);
        }

        public LiveRootLayout Layout { get; }
        public NativeGoLiveWindowsOwnedStatePort OwnedState { get; }
        public DisposableCatalogue Catalogue { get; }
        public INativeGoLiveAdmissionPort Admission { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class DisposableCatalogue(bool exists)
    {
        private readonly List<string> _attachedFiles = [];
        public bool Exists { get; private set; } = exists;
        public int DropCalls { get; private set; }
        public bool DroppedWhileAttachedFilesExisted { get; private set; }

        public void AttachFiles(params string[] paths) => _attachedFiles.AddRange(paths);

        public ValueTask<bool> ExistsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Exists);
        }

        public ValueTask DropAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_attachedFiles.Count > 0)
            {
                DroppedWhileAttachedFilesExisted = _attachedFiles.All(File.Exists);
                if (!DroppedWhileAttachedFilesExisted)
                    throw new InvalidOperationException("attached-catalogue-files-were-removed-before-drop");
                foreach (var path in _attachedFiles) File.Delete(path);
            }
            DropCalls++;
            Exists = false;
            return ValueTask.CompletedTask;
        }
    }
}
