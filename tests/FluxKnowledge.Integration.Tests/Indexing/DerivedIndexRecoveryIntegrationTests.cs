using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class DerivedIndexRecoveryIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [Fact]
    public async Task Validated_empty_catalogue_is_healthy_without_resolving_filesystem_or_usearch_services()
    {
        var services = new ServiceCollection();
        var store = new RecordingRecoveryStore(new DerivedIndexRecoverySqlSnapshot(
            ActiveGenerationId: null,
            Generation: null,
            Membership: [],
            ReferencedGenerationIds: ImmutableHashSet<Guid>.Empty,
            ReferencedIndexPaths: ImmutableHashSet<string>.Empty,
            IsValidatedEmptyCatalogue: true));
        services.AddSingleton<IDerivedIndexRecoveryStore>(store);
        using var provider = services.BuildServiceProvider();
        var coordinator = new DerivedIndexRecoveryCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            UsearchIndexConfiguration.FromConfiguredRoot(Path.GetTempPath()),
            TimeProvider.System);

        await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.True(coordinator.Snapshot.IsValidatedEmptyCatalogue);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(1, store.LeaseAcquisitions);
    }

    [NativeSqlServerFact]
    public async Task Missing_active_directory_recovers_to_a_unique_path_without_changing_the_active_pointer()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var indexStore = new SqlPipelineStore(factory);
            var vectors = await indexStore.ReadVectorsAsync(generationId, CancellationToken.None);
            await using (var context = await factory.CreateDbContextAsync())
            {
                var storedVectors = await context.Vectors.OrderBy(item => item.VectorId).ToListAsync();
                for (var index = 0; index < storedVectors.Count; index++)
                {
                    var values = new byte[1024];
                    BitConverter.GetBytes(1F).CopyTo(values, index * sizeof(float));
                    storedVectors[index].Values = values;
                    storedVectors[index].PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values));
                }
                await context.SaveChangesAsync();
                vectors = await indexStore.ReadVectorsAsync(generationId, CancellationToken.None);
                var generation = await context.IndexGenerations.SingleAsync(item => item.Id == generationId);
                generation.IndexPath = Path.Combine(root, "generations", generationId.ToString("N"));
                foreach (var otherGeneration in await context.IndexGenerations.Where(item => item.Id != generationId).ToListAsync())
                {
                    otherGeneration.IndexPath = Path.Combine(root, "generations", otherGeneration.Id.ToString("N"));
                }
                generation.MetadataChecksum = UsearchGenerationValidator.ComputeChecksum(
                    generation.ModelFingerprint, generation.Dimensions, vectors);
                generation.VectorCount = vectors.Count;
                await context.SaveChangesAsync();
            }

            var options = UsearchIndexOptions.FromConfiguredRoot(root);
            var services = new ServiceCollection();
            services.AddSingleton(factory);
            services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
            services.AddScoped<SqlPipelineStore>();
            services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<SqlPipelineStore>());
            services.AddSingleton(options);
            services.AddSingleton<UsearchGenerationValidator>();
            services.AddScoped<UsearchGenerationBuilder>();
            services.AddSingleton<DerivedIndexFileSystem>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<DerivedIndexRecoveryCoordinator>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            var recovered = await indexStore.GetGenerationAsync(generationId, CancellationToken.None);
            Assert.NotNull(recovered);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
            Assert.Equal(generationId, await indexStore.GetActiveGenerationIdAsync(CancellationToken.None));
            Assert.NotEqual(Path.Combine(root, "generations", generationId.ToString("N")), recovered!.IndexPath);
            Assert.True(File.Exists(Path.Combine(recovered.IndexPath, UsearchGenerationValidator.IndexFileName)));
            await using var auditContext = await factory.CreateDbContextAsync();
            var lifecycle = await auditContext.AuditEvents
                .Where(item => item.EventType == "derived_index_recovery")
                .OrderBy(item => item.Id)
                .Select(item => item.DetailsJson)
                .ToListAsync();
            Assert.Collection(lifecycle,
                item => Assert.Contains("recovery_detected", item, StringComparison.Ordinal),
                item => Assert.Contains("recovery_attempt", item, StringComparison.Ordinal),
                item => Assert.Contains("recovery_rebuild_succeeded", item, StringComparison.Ordinal),
                item => Assert.Contains("recovery_cleanup_completed", item, StringComparison.Ordinal),
                item => Assert.Contains("recovery_healthy", item, StringComparison.Ordinal));
            Assert.DoesNotContain(lifecycle, item => item.Contains(root, StringComparison.OrdinalIgnoreCase));

            await coordinator.RunOnceAsync(CancellationToken.None);
            var periodicProbeAuditCount = await auditContext.AuditEvents
                .CountAsync(item => item.EventType == "derived_index_recovery");
            Assert.Equal(lifecycle.Count, periodicProbeAuditCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Corrupt_active_directory_is_replaced_before_the_old_path_is_quarantined()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, duplicateId);
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var oldPath = Path.Combine(root, "generations", generationId.ToString("N"));
        try
        {
            var store = new SqlPipelineStore(factory);
            var vectors = await MakeSnapshotRebuildableAsync(factory, store, generationId, oldPath);
            Directory.CreateDirectory(oldPath);
            await File.WriteAllTextAsync(Path.Combine(oldPath, UsearchGenerationValidator.IndexFileName), "corrupt");
            await File.WriteAllTextAsync(Path.Combine(oldPath, UsearchGenerationValidator.MetadataFileName), "{}");
            await using (var context = await factory.CreateDbContextAsync())
            {
                var duplicate = await context.IndexGenerations.SingleAsync(item => item.Id == duplicateId);
                duplicate.IndexPath = oldPath;
                await context.SaveChangesAsync();
            }
            using var provider = CreateRecoveryProvider(factory, root);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            var unchanged = await store.GetGenerationAsync(generationId, CancellationToken.None);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
            Assert.NotNull(unchanged);
            Assert.NotEqual(oldPath, unchanged!.IndexPath);
            Assert.Equal(generationId, await store.GetActiveGenerationIdAsync(CancellationToken.None));
            Assert.True(Directory.Exists(oldPath));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(oldPath, (await verification.IndexGenerations.SingleAsync(item => item.Id == duplicateId)).IndexPath);
            Assert.Equal(vectors.Count, unchanged.VectorCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Permission_fault_becomes_operator_actionable_without_a_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var fixture = CreateFaultingCoordinator(root, new UnauthorizedAccessException("injected"));
            var coordinator = fixture.Coordinator;

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.PermissionsDenied, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Placed_recovery_candidate_permission_fault_becomes_operator_actionable_without_a_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var fixture = CreateFaultingCoordinator(root,
                new RecoveryCandidatePlacementException(
                    Path.Combine(root, "generations", "candidate"),
                    new UnauthorizedAccessException("injected post-placement validation denial")));
            var coordinator = fixture.Coordinator;

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.PermissionsDenied, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Configuration_fault_becomes_operator_actionable_without_a_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var fixture = CreateFaultingCoordinator(root, new InvalidOperationException("configuration invalid"));
            var coordinator = fixture.Coordinator;

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sql_catalogue_error_4060_becomes_configuration_invalid_without_a_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var fixture = CreateFaultingCoordinator(root, CreateSqlException(4060));

            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, fixture.Coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid,
                fixture.Coordinator.Snapshot.FailureCategory);
            Assert.Null(fixture.Coordinator.Snapshot.NextRetryAtUtc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Access_denied_active_directory_requires_operator_action_without_rebuild_or_path_update()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var activeId = Guid.NewGuid();
        var activePath = Path.Combine(root, "generations", activeId.ToString("N"));
        try
        {
            var store = new RecordingRecoveryStore(CreateSnapshot(activeId, activePath));
            var services = new ServiceCollection();
            services.AddSingleton<IDerivedIndexRecoveryStore>(store);
            services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
            services.AddSingleton(new UsearchIndexOptions(root));
            services.AddSingleton<UsearchGenerationValidator>();
            services.AddScoped<UsearchGenerationBuilder>();
            var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root));
            services.AddSingleton(fileSystem);
            using var provider = services.BuildServiceProvider();
            var coordinator = new DerivedIndexRecoveryCoordinator(
                provider.GetRequiredService<IServiceScopeFactory>(), fileSystem, TimeProvider.System,
                getActivePathAttributes: _ => throw new UnauthorizedAccessException("injected ACL denial"));

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.PermissionsDenied, coordinator.Snapshot.FailureCategory);
            Assert.Equal(0, store.PathUpdateAttempts);
            Assert.False(Directory.Exists(Path.Combine(root, "staging")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unsafe_active_sql_path_requires_configuration_action_without_metadata_probe_or_mutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var activeId = Guid.NewGuid();
        var unsafePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        var metadataProbeCount = 0;
        try
        {
            var store = new RecordingRecoveryStore(CreateSnapshot(activeId, unsafePath));
            using var fixture = CreatePathProbeCoordinator(root, store, _ =>
            {
                metadataProbeCount++;
                return FileAttributes.Directory;
            });

            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, fixture.Coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid,
                fixture.Coordinator.Snapshot.FailureCategory);
            Assert.Null(fixture.Coordinator.Snapshot.NextRetryAtUtc);
            Assert.Equal(0, metadataProbeCount);
            Assert.Equal(0, store.PathUpdateAttempts);
            Assert.False(Directory.Exists(Path.Combine(root, "staging")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Malformed_active_sql_path_requires_configuration_action_without_metadata_probe_or_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var activeId = Guid.NewGuid();
        var malformedPath = root + "\0";
        var metadataProbeCount = 0;
        try
        {
            var store = new RecordingRecoveryStore(CreateSnapshot(activeId, malformedPath));
            using var fixture = CreatePathProbeCoordinator(root, store, _ =>
            {
                metadataProbeCount++;
                return FileAttributes.Directory;
            });

            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, fixture.Coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid,
                fixture.Coordinator.Snapshot.FailureCategory);
            Assert.Null(fixture.Coordinator.Snapshot.NextRetryAtUtc);
            Assert.Equal(0, metadataProbeCount);
            Assert.Equal(0, store.PathUpdateAttempts);
            Assert.False(Directory.Exists(Path.Combine(root, "staging")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task In_root_staging_sql_path_requires_configuration_action_without_component_or_metadata_probe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var activeId = Guid.NewGuid();
        var stagingPath = Path.Combine(root, "staging", activeId.ToString("N"));
        var componentSafetyCheckCount = 0;
        var metadataProbeCount = 0;
        try
        {
            var store = new RecordingRecoveryStore(CreateSnapshot(activeId, stagingPath));
            using var fixture = CreatePathProbeCoordinator(root, store, _ =>
            {
                metadataProbeCount++;
                return FileAttributes.Directory;
            }, _ =>
            {
                componentSafetyCheckCount++;
                return true;
            });

            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, fixture.Coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid,
                fixture.Coordinator.Snapshot.FailureCategory);
            Assert.Null(fixture.Coordinator.Snapshot.NextRetryAtUtc);
            Assert.Equal(0, componentSafetyCheckCount);
            Assert.Equal(0, metadataProbeCount);
            Assert.Equal(0, store.PathUpdateAttempts);
            Assert.False(Directory.Exists(Path.Combine(root, "staging")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reader_fault_immediately_marks_recovery_and_publishes_an_invalidation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var publisher = new RecordingStatusPublisher();
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            var coordinator = new DerivedIndexRecoveryCoordinator(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new DerivedIndexFileSystem(new UsearchIndexOptions(root)), TimeProvider.System, publisher);

            coordinator.Notify(new DerivedIndexRecoveryFault(
                DerivedIndexRecoveryFailureCategory.MissingDerivedIndex, Guid.NewGuid()));

            Assert.Equal(DerivedIndexRecoveryState.Recovering, coordinator.Snapshot.State);
            Assert.Equal("index-recovery", Assert.Single(publisher.Events).Projection);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Malformed_metadata_is_reported_as_an_invalid_derived_index()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, UsearchGenerationValidator.MetadataFileName), "{");
            var generation = EmptyGeneration(directory);

            Assert.Throws<IndexGenerationValidationException>(() =>
                new UsearchGenerationValidator().Validate(directory, generation, []));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Native_index_open_failure_is_reported_as_an_invalid_derived_index()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var generation = EmptyGeneration(directory);
            File.WriteAllText(Path.Combine(directory, UsearchGenerationValidator.MetadataFileName), JsonSerializer.Serialize(
                new UsearchGenerationValidator.Metadata(generation.Id, generation.ModelFingerprint, "cos", generation.Dimensions,
                    generation.VectorCount, generation.MetadataChecksum)));
            File.WriteAllText(Path.Combine(directory, UsearchGenerationValidator.IndexFileName), "not a USearch index");

            Assert.Throws<IndexGenerationValidationException>(() =>
                new UsearchGenerationValidator().Validate(directory, generation, []));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Retry_scheduled_state_rejects_reader_signals_and_an_early_sixth_attempt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var store = new CountingFailureRecoveryStore();
            using var provider = CreateFaultingRecoveryProvider(root, store);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);
            coordinator.Notify(new DerivedIndexRecoveryFault(DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex, null));
            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, store.Acquisitions);
            Assert.Equal(DerivedIndexRecoveryState.RetryScheduled, coordinator.Snapshot.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_retains_an_aged_candidate_when_sql_references_a_descendant()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var candidate = Path.Combine(root, "staging", "candidate");
            Directory.CreateDirectory(Path.Combine(candidate, "nested"));
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));
            var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root));

            var cleaned = fileSystem.Cleanup("staging", TimeSpan.FromHours(24), DateTimeOffset.UtcNow,
                [Path.Combine(candidate, "nested")]);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recovery_staging_candidate_is_a_direct_child_of_staging()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root));

            Assert.True(fileSystem.TryCreateRecoveryStagingDirectory(out var staging));

            Assert.Equal(Path.Combine(root, "staging"), Path.GetDirectoryName(staging));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_rejects_a_non_recovery_area()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var candidate = Path.Combine(root, "arbitrary", "candidate");
            Directory.CreateDirectory(candidate);
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

            var cleaned = new DerivedIndexFileSystem(new UsearchIndexOptions(root)).Cleanup(
                "arbitrary", TimeSpan.Zero, DateTimeOffset.UtcNow, []);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_retains_a_candidate_when_any_sql_reference_is_outside_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var candidate = Path.Combine(root, "staging", "candidate");
            Directory.CreateDirectory(candidate);
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

            var cleaned = new DerivedIndexFileSystem(new UsearchIndexOptions(root)).Cleanup(
                "staging", TimeSpan.Zero, DateTimeOffset.UtcNow, [Path.GetTempPath()]);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_removes_only_aged_unreferenced_non_reparse_candidates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var stagingRemoved = CreateAgedDirectory(root, "staging", "remove");
            var quarantineRemoved = CreateAgedDirectory(root, "quarantine", "remove");
            var referenced = CreateAgedDirectory(root, "staging", "referenced");
            var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root));

            Assert.Equal(1, fileSystem.Cleanup("staging", TimeSpan.FromHours(24), DateTimeOffset.UtcNow,
                [referenced, referenced]));
            Assert.Equal(1, fileSystem.Cleanup("quarantine", TimeSpan.FromDays(7), DateTimeOffset.UtcNow, []));
            Assert.False(Directory.Exists(stagingRemoved));
            Assert.False(Directory.Exists(quarantineRemoved));
            Assert.True(Directory.Exists(referenced));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Cleanup_retains_a_reparse_bearing_candidate_when_windows_allows_creation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var external = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecoveryExternal_{Guid.NewGuid():N}");
        try
        {
            var candidate = CreateAgedDirectory(root, "staging", "linked");
            Directory.CreateDirectory(external);
            var link = Path.Combine(candidate, "junction");
            try
            {
                Directory.CreateSymbolicLink(link, external);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }
            Directory.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

            var cleaned = new DerivedIndexFileSystem(new UsearchIndexOptions(root)).Cleanup(
                "staging", TimeSpan.Zero, DateTimeOffset.UtcNow, []);

            Assert.Equal(0, cleaned);
            Assert.True(Directory.Exists(candidate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public void Referenced_generation_path_rejects_an_existing_intermediate_reparse_point_when_available()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var external = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecoveryExternal_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(external);
            var generations = Path.Combine(root, "generations");
            try
            {
                Directory.CreateSymbolicLink(generations, external);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            Assert.False(new DerivedIndexFileSystem(new UsearchIndexOptions(root)).TryCanonicalInRoot(
                Path.Combine(generations, Guid.NewGuid().ToString("N")), out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Invalid_sql_checksum_requires_operator_action_without_filesystem_or_metadata_mutation()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var oldPath = Path.Combine(root, "generations", generationId.ToString("N"));
        try
        {
            await using (var context = await factory.CreateDbContextAsync())
            {
                var generation = await context.IndexGenerations.SingleAsync(item => item.Id == generationId);
                generation.IndexPath = oldPath;
                generation.MetadataChecksum = new string('f', 64);
                generation.VectorCount = 2;
                await context.SaveChangesAsync();
            }
            using var provider = CreateRecoveryProvider(factory, root);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.SqlMembershipInvalid, coordinator.Snapshot.FailureCategory);
            Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
            Assert.False(Directory.Exists(root));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(oldPath, (await verification.IndexGenerations.SingleAsync(item => item.Id == generationId)).IndexPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Invalid_nonactive_sql_path_requires_operator_action_without_filesystem_or_metadata_mutation()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        var referencedGenerationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, referencedGenerationId);
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        var activePath = Path.Combine(root, "generations", generationId.ToString("N"));
        try
        {
            var store = new SqlPipelineStore(factory);
            await MakeSnapshotRebuildableAsync(factory, store, generationId, activePath);
            await using (var context = await factory.CreateDbContextAsync())
            {
                var referenced = await context.IndexGenerations.SingleAsync(item => item.Id == referencedGenerationId);
                referenced.IndexPath = Path.Combine(root, "..", "outside");
                await context.SaveChangesAsync();
            }
            using var provider = CreateRecoveryProvider(factory, root);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, coordinator.Snapshot.FailureCategory);
            Assert.False(Directory.Exists(root));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(activePath, (await verification.IndexGenerations.SingleAsync(item => item.Id == generationId)).IndexPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Two_coordinators_perform_at_most_one_rebuild_and_both_later_converge_healthy()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var store = new SqlPipelineStore(factory);
            await MakeSnapshotRebuildableAsync(factory, store, generationId,
                Path.Combine(root, "generations", generationId.ToString("N")));
            using var firstProvider = CreateRecoveryProvider(factory, root);
            using var secondProvider = CreateRecoveryProvider(factory, root);
            var first = firstProvider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
            var second = secondProvider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await Task.WhenAll(first.RunOnceAsync(CancellationToken.None).AsTask(),
                second.RunOnceAsync(CancellationToken.None).AsTask());
            await first.RunOnceAsync(CancellationToken.None);
            await second.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.Healthy, first.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, second.Snapshot.State);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "generations")));
            Assert.Equal(generationId, await store.GetActiveGenerationIdAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Hosted_service_uses_exact_retry_schedule_then_converges_after_later_success()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            var pipelineStore = new SqlPipelineStore(factory);
            await MakeSnapshotRebuildableAsync(factory, pipelineStore, generationId,
                Path.Combine(root, "generations", generationId.ToString("N")));
            var sequenced = new SequencedRecoveryStore(
                new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System), failures: 4);
            using var provider = CreateRecoveryProvider(factory, root, sequenced);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
            var waits = new List<TimeSpan>();
            using var cancellation = new CancellationTokenSource();
            var service = new DerivedIndexRecoveryService(
                coordinator, DerivedIndexRecoveryOptions.Default, TimeProvider.System,
                (delay, _) =>
                {
                    waits.Add(delay);
                    if (coordinator.Snapshot.State == DerivedIndexRecoveryState.Healthy)
                        cancellation.Cancel();
                    return Task.CompletedTask;
                });

            await service.RunForTestingAsync(cancellation.Token);

            Assert.Equal([2, 5, 15, 30], waits.Take(4).Select(item => (int)Math.Round(item.TotalSeconds)));
            Assert.Equal(5, sequenced.Acquisitions);
            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Hosted_service_stops_after_five_recoverable_failures_without_a_sixth_attempt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRecovery_{Guid.NewGuid():N}");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var store = new ExhaustingRecoveryStore(cancellation);
            using var provider = CreateFaultingRecoveryProvider(root, store);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
            var waits = new List<TimeSpan>();
            var service = new DerivedIndexRecoveryService(coordinator,
                DerivedIndexRecoveryOptions.Default, TimeProvider.System,
                (delay, _) =>
                {
                    waits.Add(delay);
                    coordinator.Notify(new DerivedIndexRecoveryFault(DerivedIndexRecoveryFailureCategory.InvalidDerivedIndex, null));
                    return Task.CompletedTask;
                });

            await service.RunForTestingAsync(cancellation.Token);

            Assert.Equal([2, 5, 15, 30], waits.Select(item => (int)Math.Round(item.TotalSeconds)));
            Assert.Equal(5, store.Acquisitions);
            Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
            Assert.Equal(DerivedIndexRecoveryFailureCategory.RetryExhausted, coordinator.Snapshot.FailureCategory);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Recovery_snapshot_reads_active_immutable_membership_and_referenced_generations()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var activeGenerationId = Guid.NewGuid();
        var referencedGenerationId = Guid.NewGuid();
        var vectorIds = await SeedSnapshotAsync(factory, activeGenerationId, referencedGenerationId);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var snapshot = await store.ReadActiveAsync(CancellationToken.None);

        Assert.Equal(activeGenerationId, snapshot.ActiveGenerationId);
        Assert.NotNull(snapshot.Generation);
        Assert.Equal(activeGenerationId, snapshot.Generation!.Id);
        Assert.Equal(vectorIds.Order(), snapshot.Membership.Select(member => member.VectorId));
        Assert.Contains(activeGenerationId, snapshot.ReferencedGenerationIds);
        Assert.Contains(referencedGenerationId, snapshot.ReferencedGenerationIds);
    }

    [NativeSqlServerFact]
    public async Task Recovery_path_update_changes_only_the_existing_path_and_validation_timestamp()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, Guid.NewGuid());
        await using var beforeContext = await factory.CreateDbContextAsync();
        var before = await beforeContext.IndexGenerations.SingleAsync(item => item.Id == generationId);
        var immutable = (before.ModelFingerprint, before.Dimensions, before.MetadataChecksum, before.VectorCount, before.CreatedAtUtc);
        var validatedAt = DateTimeOffset.UtcNow;
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var updateSucceeded = await store.TryUpdateRecoveryPathAsync(generationId, before.IndexPath,
            @"C:\safe\generations\replacement", validatedAt, CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        Assert.True(updateSucceeded);
        var updated = await verification.IndexGenerations.SingleAsync(item => item.Id == generationId);
        Assert.Equal(@"C:\safe\generations\replacement", updated.IndexPath);
        Assert.Equal(validatedAt, updated.ValidatedAtUtc);
        Assert.Equal(immutable, (updated.ModelFingerprint, updated.Dimensions, updated.MetadataChecksum, updated.VectorCount, updated.CreatedAtUtc));
        Assert.Equal(2, await verification.IndexGenerations.CountAsync());
        Assert.Equal(generationId, (await verification.IndexState.SingleAsync(item => item.Id == 1)).ActiveIndexGenerationId);
    }

    [NativeSqlServerFact]
    public async Task Recovery_path_update_does_not_mutate_when_the_active_generation_has_changed()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        var replacementActiveGenerationId = Guid.NewGuid();
        await SeedSnapshotAsync(factory, generationId, replacementActiveGenerationId);
        await using var beforeContext = await factory.CreateDbContextAsync();
        var generation = await beforeContext.IndexGenerations.SingleAsync(item => item.Id == generationId);
        var originalPath = generation.IndexPath;
        var state = await beforeContext.IndexState.SingleAsync(item => item.Id == 1);
        state.ActiveIndexGenerationId = replacementActiveGenerationId;
        await beforeContext.SaveChangesAsync();
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var updated = await store.TryUpdateRecoveryPathAsync(generationId, originalPath,
            @"C:\safe\generations\replacement", DateTimeOffset.UtcNow, CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        Assert.False(updated);
        Assert.Equal(originalPath, (await verification.IndexGenerations.SingleAsync(item => item.Id == generationId)).IndexPath);
    }

    [NativeSqlServerFact]
    public async Task Recovery_snapshot_retains_a_shared_sql_index_path_reference()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var activeGenerationId = Guid.NewGuid();
        var referencedGenerationId = Guid.NewGuid();
        const string activeIndexPath = "pending";
        const string sharedIndexPath = @"C:\flux\indexes\shared";
        const string sharedIndexPathWithDifferentCasing = @"c:\FLUX\INDEXES\SHARED";
        await SeedSnapshotAsync(factory, activeGenerationId, referencedGenerationId);
        await using (var context = await factory.CreateDbContextAsync())
        {
            var referencedGeneration = await context.IndexGenerations
                .SingleAsync(generation => generation.Id == referencedGenerationId);
            referencedGeneration.IndexPath = @"C:\flux\indexes\referenced";
            context.IndexGenerations.AddRange(
                CreateGeneration(Guid.NewGuid(), sharedIndexPath),
                CreateGeneration(Guid.NewGuid(), sharedIndexPathWithDifferentCasing));
            await context.SaveChangesAsync();
        }

        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var snapshot = await store.ReadActiveAsync(CancellationToken.None);

        Assert.Contains(activeIndexPath, snapshot.ReferencedIndexPaths);
        Assert.Contains(snapshot.ReferencedIndexPaths, path =>
            string.Equals(path, sharedIndexPathWithDifferentCasing, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, snapshot.ReferencedIndexPaths.Count(path =>
            string.Equals(path, sharedIndexPath, StringComparison.OrdinalIgnoreCase)));
    }

    [NativeSqlServerFact]
    public async Task Exclusive_recovery_lease_allows_only_one_holder()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var otherStore = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var first = await store.TryAcquireExclusiveLeaseAsync(
            TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(first);
        await using var heldLease = first!;
        var second = await otherStore.TryAcquireExclusiveLeaseAsync(
            TimeSpan.Zero, CancellationToken.None);

        Assert.Null(second);
    }

    [NativeSqlServerFact]
    public async Task Disposed_recovery_lease_can_be_reacquired_and_double_disposed()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);

        var first = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(first);
        await first!.DisposeAsync();
        await first.DisposeAsync();

        var second = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Lease_disposal_closes_a_broken_session_before_the_next_acquisition()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var lease = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(lease);
        var field = lease!.GetType().GetField(
            "_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var connection = Assert.IsType<SqlConnection>(field!.GetValue(lease));
        await connection.CloseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.DisposeAsync());

        var reacquired = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Cancelled_recovery_lease_wait_preserves_cancellation()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var otherStore = new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System);
        var held = await store.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(held);
        var heldLease = held!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await otherStore.TryAcquireExclusiveLeaseAsync(TimeSpan.FromSeconds(5), cancellation.Token));
        await heldLease.DisposeAsync();

        var recovered = await otherStore.TryAcquireExclusiveLeaseAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotNull(recovered);
        await recovered!.DisposeAsync();
    }

    [NativeSqlServerFact]
    public async Task Recovery_audit_persists_only_safe_fields()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var generationId = Guid.NewGuid();

        await store.AppendAuditAsync(
            new DerivedIndexRecoveryAuditEvent(
                "rebuild_succeeded", generationId, null, 1,
                TimeSpan.FromSeconds(1), null, 0),
            CancellationToken.None);
        await using var context = await SqlTestData.CreateFactory(_fixture)
            .CreateDbContextAsync();
        var audit = await context.AuditEvents
            .OrderByDescending(item => item.Id)
            .FirstAsync();

        Assert.Null(audit.PipelineRecordId);
        Assert.Equal("derived_index_recovery", audit.EventType);
        Assert.Equal("DerivedIndexRecoveryService", audit.Actor);
        Assert.Contains("rebuild_succeeded", audit.DetailsJson, StringComparison.Ordinal);
        Assert.Contains(generationId.ToString("D"), audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [NativeSqlServerFact]
    public async Task Recovery_audit_bounds_and_sanitises_hostile_category_input()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var store = new SqlDerivedIndexRecoveryStore(
            SqlTestData.CreateFactory(_fixture), TimeProvider.System);
        var hostileCategory = "C:\\recovery\\password=secret\\" + new string('x', 4_000);

        await store.AppendAuditAsync(
            new DerivedIndexRecoveryAuditEvent(
                hostileCategory, Guid.NewGuid(), null, int.MaxValue,
                TimeSpan.MaxValue, null, int.MaxValue),
            CancellationToken.None);
        await using var context = await SqlTestData.CreateFactory(_fixture)
            .CreateDbContextAsync();
        var audit = await context.AuditEvents.OrderByDescending(item => item.Id).FirstAsync();

        Assert.True(audit.DetailsJson.Length < 512);
        Assert.DoesNotContain("C:\\", audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("password", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"category\":\"unknown\"", audit.DetailsJson, StringComparison.Ordinal);
    }

    private static async Task<long[]> SeedSnapshotAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        Guid activeGenerationId,
        Guid referencedGenerationId)
    {
        await using var context = await factory.CreateDbContextAsync();
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var values = new byte[1024];
        values[0] = 1;
        context.IndexGenerations.AddRange(
            CreateGeneration(activeGenerationId),
            CreateGeneration(referencedGenerationId));
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = sourceId,
            SourceKind = "test",
            StableKey = $"snapshot-{sourceId:N}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = sourceId,
            Revision = 1,
            ContentHash = new string('a', 64),
            RootLineageRecordId = recordId,
            RegisteredAtUtc = DateTimeOffset.UtcNow
        });
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = 3,
            ContentHash = new string('a', 64),
            ContentType = "text/plain",
            SearchText = "snapshot",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        var chunk = new TextChunkEntity
        {
            ArtifactId = artifactId,
            SourceRevision = 1,
            Ordinal = 0,
            StartOffset = 0,
            Length = 8,
            Content = "snapshot",
            ContentHash = new string('a', 64)
        };
        context.TextChunks.Add(chunk);
        await context.SaveChangesAsync();
        var firstVector = new VectorEntity
        {
            TextChunkId = chunk.Id,
            SourceRevision = 1,
            ModelFingerprint = "deterministic-tokenhash-v1:256",
            Dimensions = 256,
            Values = values,
            TextChunkContentHash = chunk.ContentHash,
            PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)),
            IndexGenerationId = referencedGenerationId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var secondVector = new VectorEntity
        {
            TextChunkId = chunk.Id,
            SourceRevision = 1,
            ModelFingerprint = "deterministic-tokenhash-v1:256",
            Dimensions = 256,
            Values = values,
            TextChunkContentHash = chunk.ContentHash,
            PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)),
            IndexGenerationId = activeGenerationId,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        context.Vectors.AddRange(firstVector, secondVector);
        await context.SaveChangesAsync();
        context.IndexGenerationVectors.AddRange(
            new IndexGenerationVectorEntity { GenerationId = activeGenerationId, VectorId = secondVector.VectorId },
            new IndexGenerationVectorEntity { GenerationId = activeGenerationId, VectorId = firstVector.VectorId });
        var state = await context.IndexState.SingleAsync(item => item.Id == 1);
        state.ActiveIndexGenerationId = activeGenerationId;
        await context.SaveChangesAsync();
        return [firstVector.VectorId, secondVector.VectorId];
    }

    private static IndexGenerationDescriptor EmptyGeneration(string indexPath)
    {
        const string fingerprint = "test-model";
        return new IndexGenerationDescriptor(Guid.NewGuid(), fingerprint, 1, indexPath,
            UsearchGenerationValidator.ComputeChecksum(fingerprint, 1, []), 0);
    }

    private static IndexGenerationEntity CreateGeneration(Guid id, string indexPath = "pending") => new()
    {
        Id = id,
        ModelFingerprint = "deterministic-tokenhash-v1:256",
        Dimensions = 256,
        IndexPath = indexPath,
        MetadataChecksum = new string('0', 64),
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static ServiceProvider CreateRecoveryProvider(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        string root,
        IDerivedIndexRecoveryStore? recoveryStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        if (recoveryStore is null) services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
        else services.AddSingleton(recoveryStore);
        services.AddScoped<SqlPipelineStore>();
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<SqlPipelineStore>());
        services.AddSingleton(UsearchIndexOptions.FromConfiguredRoot(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider CreateFaultingRecoveryProvider(string root, IDerivedIndexRecoveryStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
        services.AddSingleton(new UsearchIndexOptions(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string CreateAgedDirectory(string root, string area, string name)
    {
        var path = Path.Combine(root, area, name);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }

    private static async Task<IReadOnlyList<CanonicalVector>> MakeSnapshotRebuildableAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        SqlPipelineStore store,
        Guid generationId,
        string indexPath)
    {
        await using var context = await factory.CreateDbContextAsync();
        var storedVectors = await context.Vectors.OrderBy(item => item.VectorId).ToListAsync();
        for (var index = 0; index < storedVectors.Count; index++)
        {
            var values = new byte[1024];
            BitConverter.GetBytes(1F).CopyTo(values, index * sizeof(float));
            storedVectors[index].Values = values;
            storedVectors[index].PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values));
        }
        await context.SaveChangesAsync();
        var vectors = await store.ReadVectorsAsync(generationId, CancellationToken.None);
        var generation = await context.IndexGenerations.SingleAsync(item => item.Id == generationId);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(indexPath)!)!;
        foreach (var otherGeneration in await context.IndexGenerations.ToListAsync())
        {
            if (otherGeneration.Id != generationId)
            {
                otherGeneration.IndexPath = Path.Combine(root, "generations", otherGeneration.Id.ToString("N"));
            }
        }
        generation.IndexPath = indexPath;
        generation.MetadataChecksum = UsearchGenerationValidator.ComputeChecksum(
            generation.ModelFingerprint, generation.Dimensions, vectors);
        generation.VectorCount = vectors.Count;
        await context.SaveChangesAsync();
        return vectors;
    }

    private static FaultingCoordinatorFixture CreateFaultingCoordinator(string root, Exception fault)
    {
        var services = new ServiceCollection();
        var options = new UsearchIndexOptions(root);
        services.AddSingleton<IDerivedIndexRecoveryStore>(new FaultingRecoveryStore(fault));
        services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
        services.AddSingleton(options);
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new FaultingCoordinatorFixture(provider, provider.GetRequiredService<DerivedIndexRecoveryCoordinator>());
    }

    private static FaultingCoordinatorFixture CreatePathProbeCoordinator(string root,
        IDerivedIndexRecoveryStore store, Func<string, FileAttributes> getActivePathAttributes,
        Func<string, bool>? existingComponentsSafetyCheck = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddScoped<IIndexGenerationStore, EmptyIndexGenerationStore>();
        services.AddSingleton(new UsearchIndexOptions(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        var fileSystem = new DerivedIndexFileSystem(new UsearchIndexOptions(root), existingComponentsSafetyCheck);
        services.AddSingleton(fileSystem);
        var provider = services.BuildServiceProvider();
        var coordinator = new DerivedIndexRecoveryCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(), fileSystem, TimeProvider.System,
            getActivePathAttributes: getActivePathAttributes);
        return new FaultingCoordinatorFixture(provider, coordinator);
    }

    private static SqlException CreateSqlException(int number)
    {
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [number, (byte)0, (byte)14, "server", "catalogue unavailable", string.Empty, 1, 0, null],
            culture: null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: null, culture: null)!;
        typeof(SqlErrorCollection).GetMethod("Add", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethods(System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "server"])!;
    }

    private static DerivedIndexRecoverySqlSnapshot CreateSnapshot(Guid activeId, string activePath)
    {
        var values = BitConverter.GetBytes(1F);
        var vector = new CanonicalVector(1, 1, "test-model", 1, values, "chunk",
            Convert.ToHexStringLower(SHA256.HashData(values)), 1);
        var generation = new IndexGenerationDescriptor(activeId, "test-model", 1, activePath,
            UsearchGenerationValidator.ComputeChecksum("test-model", 1, [vector]), 1);
        return new DerivedIndexRecoverySqlSnapshot(activeId, generation, [vector],
            ImmutableHashSet<Guid>.Empty.Add(activeId),
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, activePath));
    }

    private sealed class FaultingRecoveryStore(Exception fault) : IDerivedIndexRecoveryStore
    {
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) => throw fault;
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(TimeSpan lockTimeout, CancellationToken cancellationToken) => throw fault;
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid expectedActiveGenerationId, string expectedIndexPath, string replacementIndexPath, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken) => throw fault;
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingRecoveryStore(DerivedIndexRecoverySqlSnapshot snapshot) : IDerivedIndexRecoveryStore
    {
        public int PathUpdateAttempts { get; private set; }
        public int ReadCount { get; private set; }
        public int LeaseAcquisitions { get; private set; }

        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout, CancellationToken cancellationToken)
        {
            LeaseAcquisitions++;
            return ValueTask.FromResult<IDerivedIndexRecoveryLease?>(new NoopRecoveryLease());
        }

        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid expectedActiveGenerationId, string expectedIndexPath,
            string replacementIndexPath, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken)
        {
            PathUpdateAttempts++;
            return ValueTask.FromResult(true);
        }

        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NoopRecoveryLease : IDerivedIndexRecoveryLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequencedRecoveryStore(IDerivedIndexRecoveryStore inner, int failures)
        : IDerivedIndexRecoveryStore
    {
        public int Acquisitions { get; private set; }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) =>
            inner.ReadActiveAsync(cancellationToken);
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout, CancellationToken cancellationToken)
        {
            Acquisitions++;
            if (Acquisitions <= failures) throw new IOException("injected transient failure");
            return inner.TryAcquireExclusiveLeaseAsync(lockTimeout, cancellationToken);
        }
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid expectedActiveGenerationId, string expectedIndexPath, string replacementIndexPath, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken) =>
            inner.TryUpdateRecoveryPathAsync(expectedActiveGenerationId, expectedIndexPath, replacementIndexPath, validatedAtUtc, cancellationToken);
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) =>
            inner.AppendAuditAsync(auditEvent, cancellationToken);
    }

    private sealed class ExhaustingRecoveryStore(CancellationTokenSource cancellation)
        : IDerivedIndexRecoveryStore
    {
        public int Acquisitions { get; private set; }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout, CancellationToken cancellationToken)
        {
            Acquisitions++;
            if (Acquisitions == 5) cancellation.Cancel();
            throw new IOException("injected transient failure");
        }
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid expectedActiveGenerationId, string expectedIndexPath, string replacementIndexPath, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class CountingFailureRecoveryStore : IDerivedIndexRecoveryStore
    {
        public int Acquisitions { get; private set; }
        public ValueTask<DerivedIndexRecoverySqlSnapshot> ReadActiveAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<IDerivedIndexRecoveryLease?> TryAcquireExclusiveLeaseAsync(
            TimeSpan lockTimeout, CancellationToken cancellationToken)
        {
            Acquisitions++;
            throw new IOException("injected transient failure");
        }
        public ValueTask<bool> TryUpdateRecoveryPathAsync(Guid expectedActiveGenerationId, string expectedIndexPath, string replacementIndexPath, DateTimeOffset validatedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask AppendAuditAsync(DerivedIndexRecoveryAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingStatusPublisher : IStatusEventPublisher
    {
        public List<StatusChanged> Events { get; } = [];
        public ValueTask PublishAsync(StatusChanged statusChanged, CancellationToken cancellationToken)
        {
            Events.Add(statusChanged);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyIndexGenerationStore : IIndexGenerationStore
    {
        public ValueTask<IReadOnlyList<CanonicalTextChunk>> ReadChunksAsync(FluxKnowledge.Domain.Common.PipelineRecordId pipelineRecordId, long sourceRevision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalTextChunk>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadVectorsAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([]);
        public ValueTask<IReadOnlyList<CanonicalVector>> ReadEligibleVectorsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<CanonicalVector>>([]);
        public ValueTask<IndexGenerationDescriptor?> GetGenerationAsync(Guid indexGenerationId, CancellationToken cancellationToken) => ValueTask.FromResult<IndexGenerationDescriptor?>(null);
        public ValueTask<Guid?> GetActiveGenerationIdAsync(CancellationToken cancellationToken) => ValueTask.FromResult<Guid?>(null);
        public ValueTask UpdateGenerationMetadataAsync(IndexGenerationDescriptor generation, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed record FaultingCoordinatorFixture(ServiceProvider Provider, DerivedIndexRecoveryCoordinator Coordinator) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }
}
