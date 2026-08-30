using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;
using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Configuration;

public sealed class SqlServerProvisionerTests
{
    [Fact]
    public void Every_production_provisioner_factory_requires_a_claimed_go_live_capability()
    {
        var factories = typeof(SqlServerProvisioner)
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(SqlServerProvisioner))
            .ToArray();

        var factory = Assert.Single(factories);
        Assert.Equal("CreateForClaimedGoLive", factory.Name);
        Assert.Equal([typeof(NativeGoLiveProvisioningCapability)],
            factory.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Provisioner_exposes_only_the_claimed_go_live_production_factory()
    {
        var factories = typeof(SqlServerProvisioner)
            .GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(method => method.ReturnType == typeof(SqlServerProvisioner))
            .ToArray();

        var claimedFactory = Assert.Single(factories, method => method.Name == "CreateForClaimedGoLive");
        Assert.Equal(
            [typeof(NativeGoLiveProvisioningCapability)],
            claimedFactory.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(factories, method => method.Name == "CreateForNonProductionTests");
        Assert.Empty(typeof(NativeGoLiveProvisioningCapability).GetConstructors());
    }

    [Fact]
    public void Provisioning_accepts_the_canonical_files_without_a_file_copy_backup_target()
    {
        var failures = SqlServerProvisioner.Validate(ValidRequest());

        Assert.Empty(failures);
    }

    [Fact]
    public void Provisioning_rejects_the_obsolete_sql_file_locations()
    {
        var request = ValidRequest() with { DataFilePath = @"I:\FluxKnowledge\Sql\Data\FluxKnowledge.mdf" };

        Assert.NotEmpty(SqlServerProvisioner.Validate(request));
    }

    [Theory]
    [InlineData(UnsafePathState.MissingRoot)]
    [InlineData(UnsafePathState.RootReparse)]
    [InlineData(UnsafePathState.AncestorReparse)]
    [InlineData(UnsafePathState.LeafReparse)]
    [InlineData(UnsafePathState.ForeignResolution)]
    [InlineData(UnsafePathState.InspectionFailure)]
    public async Task Provisioning_rejects_unsafe_path_state_before_filesystem_or_SQL_calls(UnsafePathState state)
    {
        var inspector = new FakePathInspector(state);
        var fileSystem = new FakeProvisioningFileSystem(inspector);
        var database = new FakeProvisioningDatabase();
        var provisioner = CreateForNonProductionTests(
            LiveRootLayout.Production,
            inspector,
            fileSystem,
            database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync(ValidRequest()));

        Assert.Empty(fileSystem.CreatedDirectories);
        Assert.Equal(0, database.Calls);
    }

    [Theory]
    [InlineData(@"I:\FluxKnowledge\Data\Sql\Data\..\Data\FluxKnowledge.mdf")]
    [InlineData(@"C:\Foreign\FluxKnowledge.mdf")]
    public async Task Provisioning_rejects_traversal_and_root_escape_before_inspection_filesystem_or_SQL(
        string dataFilePath)
    {
        var inspector = new FakePathInspector(UnsafePathState.Safe);
        var fileSystem = new FakeProvisioningFileSystem(inspector);
        var database = new FakeProvisioningDatabase();
        var provisioner = CreateForNonProductionTests(
            LiveRootLayout.Production,
            inspector,
            fileSystem,
            database);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.ProvisionAsync(ValidRequest() with { DataFilePath = dataFilePath }));

        Assert.Equal(0, inspector.Calls);
        Assert.Empty(fileSystem.CreatedDirectories);
        Assert.Equal(0, database.Calls);
    }

    [Fact]
    public async Task Provisioning_revalidates_path_state_before_each_mutation()
    {
        var inspector = new FakePathInspector(UnsafePathState.Safe)
        {
            BecomeUnsafeAfterDirectoryCreationCount = 1
        };
        var fileSystem = new FakeProvisioningFileSystem(inspector);
        var database = new FakeProvisioningDatabase();
        var provisioner = CreateForNonProductionTests(
            LiveRootLayout.Production,
            inspector,
            fileSystem,
            database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync(ValidRequest()));

        Assert.Single(fileSystem.CreatedDirectories);
        Assert.Equal(0, database.Calls);
    }

    [Fact]
    public async Task Provisioning_revalidates_path_state_after_directory_creation_before_SQL()
    {
        var inspector = new FakePathInspector(UnsafePathState.Safe)
        {
            BecomeUnsafeAfterDirectoryCreationCount = 2
        };
        var fileSystem = new FakeProvisioningFileSystem(inspector);
        var database = new FakeProvisioningDatabase();
        var provisioner = CreateForNonProductionTests(
            LiveRootLayout.Production,
            inspector,
            fileSystem,
            database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.ProvisionAsync(ValidRequest()));

        Assert.Equal(2, fileSystem.CreatedDirectories.Count);
        Assert.Equal(0, database.Calls);
    }

    private static SqlServerProvisioner CreateForNonProductionTests(
        LiveRootLayout layout,
        ILiveRootPathInspector pathInspector,
        ISqlProvisioningFileSystem fileSystem,
        ISqlProvisioningDatabase database)
    {
        var constructor = typeof(SqlServerProvisioner).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(LiveRootLayout), typeof(ILiveRootPathInspector), typeof(ISqlProvisioningFileSystem),
                typeof(ISqlProvisioningDatabase)],
            modifiers: null) ?? throw new InvalidOperationException("The test-only provisioner constructor is unavailable.");
        return (SqlServerProvisioner)constructor.Invoke([layout, pathInspector, fileSystem, database]);
    }

    private static SqlServerProvisioningRequest ValidRequest() =>
        new(
            "Server=localhost;Initial Catalog=master;Integrated Security=true;" +
            "Encrypt=true;TrustServerCertificate=true",
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath,
            ConfirmProvision: true);

    public enum UnsafePathState
    {
        Safe,
        MissingRoot,
        RootReparse,
        AncestorReparse,
        LeafReparse,
        ForeignResolution,
        InspectionFailure
    }

    private sealed class FakePathInspector(UnsafePathState state) : ILiveRootPathInspector
    {
        public int Calls { get; private set; }
        public int BecomeUnsafeAfterDirectoryCreationCount { get; init; } = int.MaxValue;
        public int DirectoryCreationCount { get; set; }

        public LiveRootPathInspection Inspect(string path)
        {
            Calls++;
            if (state == UnsafePathState.InspectionFailure) throw new IOException("simulated inspection failure");
            var layout = LiveRootLayout.Production;
            if (state == UnsafePathState.MissingRoot && Same(path, layout.Root)) return new(false, false, null);
            if ((state == UnsafePathState.RootReparse || DirectoryCreationCount >= BecomeUnsafeAfterDirectoryCreationCount) &&
                Same(path, layout.Root))
            {
                return new(true, true, layout.Root);
            }
            if (state == UnsafePathState.AncestorReparse && Same(path, layout.DataRoot)) return new(true, true, layout.DataRoot);
            if (state == UnsafePathState.LeafReparse && Same(path, layout.SqlDataFilePath)) return new(true, true, layout.SqlDataFilePath);
            if (state == UnsafePathState.ForeignResolution && Same(path, layout.SqlRoot)) return new(true, false, @"C:\foreign-sql");
            return new(true, false, path);
        }

        private static bool Same(string left, string right) =>
            string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeProvisioningFileSystem(FakePathInspector inspector) : ISqlProvisioningFileSystem
    {
        public List<string> CreatedDirectories { get; } = [];

        public void CreateDirectory(string path)
        {
            CreatedDirectories.Add(path);
            inspector.DirectoryCreationCount++;
        }
    }

    private sealed class FakeProvisioningDatabase : ISqlProvisioningDatabase
    {
        public int Calls { get; private set; }

        public Task ProvisionAsync(string administratorConnectionString, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
