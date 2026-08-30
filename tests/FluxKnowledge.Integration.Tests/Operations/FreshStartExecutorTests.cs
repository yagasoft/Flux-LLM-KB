using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Cli.Commands;
using FluxKnowledge.Integrations.Codex;
using FluxKnowledge.Integrations.Windows;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Operations;

public sealed class FreshStartExecutorTests
{
    [Fact]
    public void Vss_policy_is_a_pure_ten_percent_i_drive_value_with_no_process_shape()
    {
        var plan = VssRecoveryPolicy.CreatePlan(LiveRootLayout.Production);

        Assert.Equal("I:", plan.Volume);
        Assert.Equal(0.10m, plan.MaximumStorageFraction);
    }

    [Fact]
    public async Task Wrong_mode_and_live_root_are_refused_before_any_port_is_observed()
    {
        await using var fixture = new Fixture();

        var wrongMode = await fixture.Executor.ExecuteAsync(
            FreshStartPlan.CreateForDisposableSimulation("reset", fixture.Layout));
        var live = await fixture.Executor.ExecuteAsync(FreshStartPlan.CreateProduction("fresh-start"));

        Assert.Equal("fresh-start-mode-required", wrongMode.Reason);
        Assert.Equal("live-execution-unavailable", live.Reason);
        Assert.Equal(0, fixture.TotalPortCalls);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("ownership")]
    [InlineData("path")]
    [InlineData("reparse")]
    [InlineData("database")]
    [InlineData("attached-file")]
    [InlineData("plugin")]
    [InlineData("snapshot")]
    public async Task Foreign_or_unexpected_state_is_rejected_before_any_mutation(string mismatch)
    {
        await using var fixture = new Fixture();
        fixture.ApplyMismatch(mismatch);

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan);

        Assert.False(result.Succeeded);
        Assert.Equal(0, fixture.MutationCalls);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Successful_disposable_execution_removes_only_precreated_owned_targets_after_validation()
    {
        await using var fixture = new Fixture();
        var first = fixture.CreateOwnedFile(fixture.Layout.IndexRoot, "index.bin");
        var second = fixture.CreateOwnedFile(fixture.Layout.RetainedRoot, "artifact.bin");
        var unrelated = fixture.CreateUnrelatedFile(fixture.Layout.ApplicationRoot, "keep.txt");

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(2, result.RemovedFileCount);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(1, fixture.Sql.ResetCalls);
        Assert.Equal(1, fixture.Codex.ClearCalls);
    }

    [Fact]
    public async Task A_mid_operation_failure_stops_without_widening_the_deletion_scope_or_issuing_authority()
    {
        await using var fixture = new Fixture();
        var first = fixture.CreateOwnedFile(fixture.Layout.IndexRoot, "first.bin");
        var blocked = fixture.CreateOwnedFile(fixture.Layout.RetainedRoot, "blocked.bin");
        var later = fixture.CreateOwnedFile(fixture.Layout.SpoolRoot, "later.bin");
        fixture.FileSystem.FailDeletePath = blocked;

        var result = await fixture.Executor.ExecuteAsync(fixture.Plan);

        Assert.False(result.Succeeded);
        Assert.Equal("operation-failed", result.Reason);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(blocked));
        Assert.True(File.Exists(later));
    }

    [Fact]
    public async Task Disposable_fresh_start_cannot_issue_or_bind_native_go_live_authority()
    {
        await using var fixture = new Fixture();
        var result = await fixture.Executor.ExecuteAsync(fixture.Plan);
        var suppliedAuthorityText = Guid.NewGuid().ToString("D");
        var output = new StringWriter();
        var error = new StringWriter();

        var cli = await FreshStartCommand.ExecuteAsync(
            ["fresh-start", "--authority", suppliedAuthorityText],
            output,
            error);

        Assert.True(result.Succeeded, result.Reason);
        Assert.DoesNotContain(
            typeof(FreshStartExecutionResult).GetProperties(),
            property => property.Name.Contains("Authority", StringComparison.Ordinal) ||
                property.Name.Contains("GoLiveBinding", StringComparison.Ordinal));
        Assert.Equal(2, cli);
        Assert.DoesNotContain(suppliedAuthorityText, output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(suppliedAuthorityText, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fresh_start_cli_returns_a_plan_only_and_does_not_construct_an_execution_path()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await FreshStartCommand.ExecuteAsync(["fresh-start"], output, error);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(@"I:\FluxKnowledge", document.RootElement.GetProperty("root").GetString());
        Assert.False(document.RootElement.GetProperty("executionAvailable").GetBoolean());
        Assert.Equal("live-execution-unavailable", document.RootElement.GetProperty("reasonCode").GetString());
        Assert.Empty(error.ToString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxKnowledgeFreshStartTests", Guid.NewGuid().ToString("N"));

        public Fixture(TimeSpan? authorityLifetime = null)
        {
            Directory.CreateDirectory(_root);
            Layout = LiveRootLayout.CreateForIsolatedTests(_root);
            Plan = FreshStartPlan.CreateForDisposableSimulation("fresh-start", Layout, authorityLifetime);
            FileSystem = new FakeFileSystem(Layout);
            Sql = new FakeSql(Layout);
            Codex = new FakeCodex(Plan.PluginIdentity);
            Vss = new FakeVss(Plan.Volume);
            Clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero));
            Executor = new FreshStartExecutor(FileSystem, Sql, Codex, Vss, Clock);
        }

        public LiveRootLayout Layout { get; }
        public FreshStartPlan Plan { get; }
        public FakeFileSystem FileSystem { get; }
        public FakeSql Sql { get; }
        public FakeCodex Codex { get; }
        public FakeVss Vss { get; }
        public ManualTimeProvider Clock { get; }
        public FreshStartExecutor Executor { get; }
        public int MutationCalls => FileSystem.DeleteCalls + Sql.ResetCalls + Codex.ClearCalls;
        public int TotalPortCalls => FileSystem.TotalCalls + Sql.TotalCalls + Codex.TotalCalls + Vss.TotalCalls;

        public string CreateOwnedFile(string root, string name)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, name);
            File.WriteAllText(path, "owned");
            FileSystem.Files.Add(new FreshStartFileState(path, FreshStartOwnership.Application, false, path));
            return path;
        }

        public string CreateUnrelatedFile(string root, string name)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, name);
            File.WriteAllText(path, "unrelated");
            return path;
        }

        public void ApplyMismatch(string mismatch)
        {
            switch (mismatch)
            {
                case "root":
                    FileSystem.RootState = FileSystem.RootState with { RootPath = Path.Combine(_root, "other") };
                    break;
                case "ownership":
                    CreateOwnedFile(Layout.IndexRoot, "foreign.bin");
                    FileSystem.Files[0] = FileSystem.Files[0] with { Owner = "foreign" };
                    break;
                case "path":
                    CreateOwnedFile(Layout.ApplicationRoot, "outside-reset-roots.bin");
                    break;
                case "reparse":
                    CreateOwnedFile(Layout.IndexRoot, "reparse.bin");
                    FileSystem.Files[0] = FileSystem.Files[0] with
                    {
                        IsReparsePoint = true,
                        ResolvedPath = Path.Combine(_root, "foreign-target.bin")
                    };
                    break;
                case "database":
                    Sql.State = Sql.State with { CatalogName = "Other" };
                    break;
                case "attached-file":
                    Sql.State = Sql.State with { DataFilePath = Path.Combine(_root, "foreign.mdf") };
                    break;
                case "plugin":
                    Codex.State = Codex.State with { Identity = Codex.State.Identity with { PluginName = "foreign" } };
                    break;
                case "snapshot":
                    Vss.State = Vss.State with { HasInheritedSnapshot = true };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mismatch));
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFileSystem(LiveRootLayout layout) : IFreshStartFileSystem
    {
        public FreshStartRootState RootState { get; set; } = new(layout.Root, FreshStartOwnership.Application, false, layout.Root);
        public List<FreshStartFileState> Files { get; } = [];
        public string? FailDeletePath { get; set; }
        public int InspectRootCalls { get; private set; }
        public int EnumerateCalls { get; private set; }
        public int InspectFileCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int TotalCalls => InspectRootCalls + EnumerateCalls + InspectFileCalls + DeleteCalls;

        public ValueTask<FreshStartRootState> InspectRootAsync(string expectedRoot, CancellationToken cancellationToken)
        {
            InspectRootCalls++;
            return ValueTask.FromResult(RootState);
        }

        public ValueTask<IReadOnlyList<FreshStartFileState>> EnumerateFilesAsync(IReadOnlyList<string> approvedRoots, CancellationToken cancellationToken)
        {
            EnumerateCalls++;
            return ValueTask.FromResult<IReadOnlyList<FreshStartFileState>>(Files.ToArray());
        }

        public ValueTask<FreshStartFileState?> InspectFileAsync(string path, CancellationToken cancellationToken)
        {
            InspectFileCalls++;
            return ValueTask.FromResult<FreshStartFileState?>(Files.SingleOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase)));
        }

        public ValueTask DeleteFileAsync(FreshStartFileState expected, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            if (string.Equals(expected.Path, FailDeletePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("simulated bounded delete failure");
            }

            File.Delete(expected.Path);
            Files.RemoveAll(file => string.Equals(file.Path, expected.Path, StringComparison.OrdinalIgnoreCase));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSql(LiveRootLayout layout) : IFreshStartSql
    {
        public FreshStartDatabaseState State { get; set; } = new(
            "FluxKnowledge",
            layout.SqlDataFilePath,
            layout.SqlLogFilePath,
            FreshStartOwnership.Application,
            true);
        public int InspectCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public int TotalCalls => InspectCalls + ResetCalls;

        public ValueTask<FreshStartDatabaseState> InspectAsync(CancellationToken cancellationToken)
        {
            InspectCalls++;
            return ValueTask.FromResult(State);
        }

        public ValueTask ResetAsync(FreshStartDatabaseState expected, CancellationToken cancellationToken)
        {
            ResetCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCodex(FreshStartPluginIdentity identity) : IFreshStartCodex
    {
        public FreshStartPluginState State { get; set; } = new(identity, FreshStartOwnership.Application, true);
        public int InspectCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public int TotalCalls => InspectCalls + ClearCalls;

        public ValueTask<FreshStartPluginState> InspectAsync(CancellationToken cancellationToken)
        {
            InspectCalls++;
            return ValueTask.FromResult(State);
        }

        public ValueTask ClearKnownPluginAsync(FreshStartPluginState expected, CancellationToken cancellationToken)
        {
            ClearCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVss(string volume) : IFreshStartVss
    {
        public FreshStartVolumeSnapshotState State { get; set; } = new(volume, false);
        public int TotalCalls { get; private set; }

        public ValueTask<FreshStartVolumeSnapshotState> InspectAsync(string expectedVolume, CancellationToken cancellationToken)
        {
            TotalCalls++;
            return ValueTask.FromResult(State);
        }
    }

    public sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
