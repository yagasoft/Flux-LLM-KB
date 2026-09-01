using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

public sealed class NativeGoLiveOneShotAdmissionTests
{
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

        Assert.True(result.Succeeded, result.ReasonCode);
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

        Assert.True(result.Succeeded, result.ReasonCode);
        Assert.Equal(1, host.AdmissionCalls);
        Assert.Equal(1, host.WipeCalls);
        Assert.Equal(["wipe-root-and-catalogue"], host.Mutations.Take(1));
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

    private static NativeGoLiveRequest ConfirmedRequest() => new(Plan, false, true, true, true, true);

    public enum NativeGoLiveDeploymentState
    {
        Absent,
        Historic,
        Marker,
        Journal,
        Partial,
        Unknown
    }

    private sealed class OneShotAdmissionHost(NativeGoLiveDeploymentState state) : INativeGoLiveHost
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
        public ValueTask ValidateAsync(NativeGoLivePlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("validate");
            return ValueTask.CompletedTask;
        }
        public ValueTask RegisterMarketplaceAsync(NativeGoLiveCodexIdentity codex, CancellationToken cancellationToken) =>
            Mutate("register-marketplace");

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
