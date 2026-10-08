using System.Text.Json;
using System.Diagnostics;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.IntegrationV1;

[Collection("sql-full-text")]
public sealed class IndexRetentionTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Completed_release_receipt_uses_the_real_phase_field()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(scenario.RecoveryRoot, "repository-recovery-receipt.json"),
            """{"SchemaVersion":11,"Phase":"Completed","Revision":11,"Baseline":{"IndexGenerations":{"Count":2634}}}""");
        var plan = await scenario.PlanAsync();
        Assert.Single(plan.Entries);
    }

    [NativeSqlServerTheory]
    [InlineData("before-directory")]
    [InlineData("after-directory")]
    public async Task Interrupted_directory_outcome_never_deletes_a_replacement(string boundary)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service(point => { if (point == boundary) throw new IOException("simulated process interruption"); });
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "directory-boundary", scenario.Actor, CancellationToken.None));
        if (Directory.Exists(scenario.Historical.IndexPath)) Directory.Delete(scenario.Historical.IndexPath);
        Directory.CreateDirectory(scenario.Historical.IndexPath);
        service = scenario.Service();
        var error = await Assert.ThrowsAsync<NativeOperationException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "directory-boundary", scenario.Actor, CancellationToken.None));
        Assert.Equal("index-retention-file-outcome-unknown", error.ReasonCode);
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scenario.Historical.IndexPath));
    }

    [NativeSqlServerFact]
    public async Task Exact_native_retention_preserves_canonical_data_replays_and_selectively_restores()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service();
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        var receipt = await service.CommitAsync(command, preview.ConfirmationId, "retention-first", scenario.Actor, CancellationToken.None);
        Assert.Equal("completed", receipt.Outcome);
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.IndexGenerations.AnyAsync(value => value.Id == scenario.Historical.Id));
            Assert.False(await db.IndexGenerationVectors.AnyAsync(value => value.GenerationId == scenario.Historical.Id));
            Assert.Equal(scenario.ActiveId, (await db.IndexState.SingleAsync()).ActiveIndexGenerationId);
            Assert.Equal(scenario.VectorHash, (await db.Vectors.SingleAsync()).PayloadChecksum);
            Assert.Equal("sql-completed-files-pending", (await db.NativeOperationReceipts.SingleAsync(value => value.ActorSurface == scenario.Actor)).Outcome);
        }
        Assert.False(Directory.Exists(scenario.Historical.IndexPath));
        Assert.True(Directory.Exists(Path.Combine(scenario.RecoveryRoot, "IndexRetention", plan.ManifestId.ToString("N"))));
        var replay = await service.CommitAsync(command, preview.ConfirmationId, "retention-first", scenario.Actor, CancellationToken.None);
        Assert.True(replay.WasReplay);
        Assert.Equal(receipt.OperationId, replay.OperationId);
        var restore = scenario.Mutation(plan, receipt.OperationId);
        var restoration = await service.PreviewAsync(restore, scenario.Actor, CancellationToken.None);
        var restored = await service.CommitAsync(restore, restoration.ConfirmationId, "retention-restore", scenario.Actor, CancellationToken.None);
        Assert.Equal("completed", restored.Outcome);
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            var row = await db.IndexGenerations.SingleAsync(value => value.Id == scenario.Historical.Id);
            Assert.Equal(scenario.Historical.MetadataChecksum, row.MetadataChecksum);
            Assert.Single(await db.IndexGenerationVectors.Where(value => value.GenerationId == row.Id).ToArrayAsync());
            Assert.Equal(scenario.ActiveId, (await db.IndexState.SingleAsync()).ActiveIndexGenerationId);
            Assert.Equal(scenario.VectorHash, (await db.Vectors.SingleAsync()).PayloadChecksum);
        }
        Assert.True(File.Exists(Path.Combine(scenario.Historical.IndexPath, "index.usearch")));
        var oldDeletion = await service.CommitAsync(command, preview.ConfirmationId, "retention-first", scenario.Actor, CancellationToken.None);
        Assert.Equal("restored", oldDeletion.Outcome);
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerTheory]
    [InlineData("active")]
    [InlineData("checkpoint")]
    [InlineData("canonical-vector")]
    [InlineData("artifact")]
    [InlineData("gpu")]
    [InlineData("query")]
    [InlineData("source-deletion")]
    [InlineData("rebuild")]
    [InlineData("recovery-json")]
    [InlineData("unfinished-release")]
    [InlineData("shared-path")]
    public async Task Reference_closure_refuses_protected_history(string reference)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            var row = await db.IndexGenerations.SingleAsync(value => value.Id == scenario.Historical.Id);
            switch (reference)
            {
                case "active": (await db.IndexState.SingleAsync()).ActiveIndexGenerationId = row.Id; break;
                case "checkpoint": row.EmbeddingJobId = await db.Jobs.Where(job => !db.IndexGenerations.Any(g => g.EmbeddingJobId == job.Id)).Select(value => value.Id).FirstAsync(); break;
                case "canonical-vector": (await db.Vectors.SingleAsync()).IndexGenerationId = row.Id; break;
                case "artifact": (await db.Artifacts.FirstAsync()).SearchText = row.Id.ToString("D"); break;
                case "gpu": db.EmbeddingGpuRequests.Add(new() { MiniTaskId = Guid.NewGuid(), ParentJobId = Guid.NewGuid(), PipelineRecordId = Guid.NewGuid(),
                    SourceRevision = 1, GenerationId = row.Id, CorpusEpoch = row.CorpusEpoch!.Value, ModelFingerprint = row.ModelFingerprint,
                    Dimensions = row.Dimensions, InputsJson = "[]", InputDigest = new string('0', 64), CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "query": db.CorpusQueryLeases.Add(new() { Id = Guid.NewGuid(), GenerationId = row.Id, CorpusEpoch = row.CorpusEpoch!.Value,
                    ModelFingerprint = row.ModelFingerprint, Dimensions = row.Dimensions, OwnerInstanceId = Guid.NewGuid(), OwnerProcessId = int.MaxValue,
                    OwnerStartedAtUtc = DateTimeOffset.UtcNow, OwnerMachineFingerprint = new string('0', 64), SqlSessionId = int.MaxValue, CreatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "source-deletion":
                    var deletion = new SourceDeletionOperationEntity { Id = Guid.NewGuid(), SourceRootId = Guid.NewGuid(), Phase = "cleanup", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                    db.SourceDeletionOperations.Add(deletion);
                    db.SourceDeletionCleanupItems.Add(new() { Id = Guid.NewGuid(), SourceDeletionOperationId = deletion.Id, StorageKind = 2,
                        RelativePath = row.Id.ToString("N"), CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "rebuild": db.CorpusRebuildOperations.Add(new() { Id = Guid.NewGuid(), TargetEpoch = Guid.NewGuid(), ManifestHash = new string('0', 64),
                    ManifestJson = JsonSerializer.Serialize(new { Generations = new[] { new { row.Id } } }), CreatedAtUtc = DateTimeOffset.UtcNow }); break;
                case "recovery-json": await File.WriteAllTextAsync(Path.Combine(scenario.RecoveryRoot, "rollback.json"), JsonSerializer.Serialize(new { Protected = row.Id })); break;
                case "unfinished-release": await File.WriteAllTextAsync(Path.Combine(scenario.RecoveryRoot, "repository-recovery-receipt.json"), """{"Phase":"Prepared"}"""); break;
                case "shared-path": (await db.IndexGenerations.SingleAsync(value => value.Id == scenario.ActiveId)).IndexPath = Path.Combine(row.IndexPath, "."); break;
            }
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<NativeOperationException>(() => scenario.PlanAsync());
        Assert.Equal(reference switch { "recovery-json" => "index-retention-recovery-reference", "unfinished-release" => "index-retention-release-not-completed",
            "shared-path" => "index-retention-path-shared", _ => "index-retention-referenced" }, error.ReasonCode);
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerTheory]
    [InlineData("row", "operation-fenced")]
    [InlineData("membership", "index-retention-membership-changed")]
    [InlineData("canonical-vector", "index-retention-membership-changed")]
    [InlineData("file", "index-retention-files-changed")]
    [InlineData("snapshot", "index-retention-snapshot-or-file-changed")]
    [InlineData("manifest", "index-retention-manifest-changed")]
    public async Task Stale_plan_or_snapshot_cannot_commit(string changed, string expected)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service();
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            if (changed == "row") (await db.IndexGenerations.SingleAsync(value => value.Id == scenario.Historical.Id)).RetiredAtUtc = DateTimeOffset.UtcNow;
            if (changed == "membership") await db.IndexGenerationVectors.Where(value => value.GenerationId == scenario.Historical.Id).ExecuteDeleteAsync();
            if (changed == "canonical-vector") (await db.Vectors.SingleAsync()).IsDeleted = true;
            await db.SaveChangesAsync();
        }
        if (changed == "file") await File.AppendAllTextAsync(Path.Combine(scenario.Historical.IndexPath, "metadata.json"), " ");
        if (changed == "snapshot") await File.AppendAllTextAsync(Path.Combine(scenario.Snapshot(plan), "members.bin"), "bad");
        if (changed == "manifest") await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(scenario.Snapshot(plan))!, "manifest.json"),
            JsonSerializer.Serialize(plan with { ProtectedGenerationIds = [scenario.ActiveId, scenario.Historical.Id] }));
        var error = await Assert.ThrowsAsync<NativeOperationException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "stale-plan", scenario.Actor, CancellationToken.None));
        Assert.Equal(expected, error.ReasonCode);
        await using var check = await scenario.Environment.Factory.CreateDbContextAsync();
        Assert.True(await check.IndexGenerations.AnyAsync(value => value.Id == scenario.Historical.Id));
        Assert.Empty(await check.NativeOperationReceipts.Where(value => value.ActorSurface == scenario.Actor).ToArrayAsync());
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerTheory]
    [InlineData("before-sql")]
    [InlineData("after-sql")]
    public async Task Sql_failure_rolls_back_members_generation_and_receipt(string boundary)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service(point => { if (point == boundary) throw new IOException("synthetic SQL interruption"); });
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "sql-boundary", scenario.Actor, CancellationToken.None));
        await using var db = await scenario.Environment.Factory.CreateDbContextAsync();
        Assert.True(await db.IndexGenerations.AnyAsync(value => value.Id == scenario.Historical.Id));
        Assert.Single(await db.IndexGenerationVectors.Where(value => value.GenerationId == scenario.Historical.Id).ToArrayAsync());
        Assert.Empty(await db.NativeOperationReceipts.Where(value => value.ActorSurface == scenario.Actor).ToArrayAsync());
        Assert.True(File.Exists(Path.Combine(scenario.Historical.IndexPath, "index.usearch")));
    }

    [NativeSqlServerTheory]
    [InlineData("before-files")]
    [InlineData("before-file:index.usearch")]
    [InlineData("after-file:index.usearch")]
    [InlineData("after-file:metadata.json")]
    [InlineData("after-directory")]
    [InlineData("after-file-receipt")]
    public async Task Durable_file_replay_converges_without_repeating_sql_or_inference(string boundary)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service(point => { if (point == boundary) throw new IOException("synthetic file interruption"); });
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "file-boundary", scenario.Actor, CancellationToken.None));
        service = scenario.Service();
        var completed = await service.CommitAsync(command, preview.ConfirmationId, "file-boundary", scenario.Actor, CancellationToken.None);
        Assert.True(completed.WasReplay);
        Assert.Equal("completed", completed.Outcome);
        await using var db = await scenario.Environment.Factory.CreateDbContextAsync();
        Assert.Single(await db.NativeOperationReceipts.Where(value => value.ActorSurface == scenario.Actor).ToArrayAsync());
        Assert.Equal(scenario.VectorHash, (await db.Vectors.SingleAsync()).PayloadChecksum);
        Assert.Empty(await db.EmbeddingGpuRequests.ToArrayAsync());
        Assert.False(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerFact]
    public async Task Incomplete_snapshot_is_reused_without_touching_originals()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        scenario.Retention.FailureInjector = point => { if (point == "snapshot-copied") throw new IOException("interrupted snapshot"); };
        await Assert.ThrowsAsync<IOException>(() => scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(scenario.Snapshot(plan))!, "manifest.json")));
        scenario.Retention.FailureInjector = null;
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(scenario.Snapshot(plan), "members.bin")));
        Assert.True(File.Exists(Path.Combine(scenario.Historical.IndexPath, "index.usearch")));
    }

    [NativeSqlServerTheory]
    [InlineData("members.bin")]
    [InlineData("manifest.json")]
    [InlineData("../escape")]
    [InlineData("unfinished.copy.tmp")]
    public async Task Snapshot_reserved_or_escaping_file_names_are_refused(string name)
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        var invalid = plan with { Entries = [plan.Entries[0] with { Files = [new(name, 0, new string('0', 64))] }] };
        var error = await Assert.ThrowsAsync<NativeOperationException>(() => scenario.Retention.StageAsync(invalid, scenario.PlanDirectory, CancellationToken.None));
        Assert.Equal("index-retention-manifest-invalid", error.ReasonCode);
        Assert.False(Directory.Exists(scenario.Snapshot(plan)));
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerFact]
    public async Task Recovery_reference_scan_refuses_a_reparse_subtree_without_following_it()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var outside = Path.Combine(scenario.Environment.IndexRoot, "unrelated");
        var link = Path.Combine(scenario.RecoveryRoot, "unsafe-link");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "preserve.json");
        await File.WriteAllTextAsync(marker, "{}");
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(scenario.Environment.IndexRoot, "create-junction.ps1");
            await File.WriteAllTextAsync(script, "param([string]$Link,[string]$Target) New-Item -ItemType Junction -Path $Link -Target $Target | Out-Null");
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-File", script, link, outside }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            var error = await Assert.ThrowsAsync<NativeOperationException>(() => scenario.PlanAsync());
            Assert.Equal("index-retention-path-unsafe", error.ReasonCode);
            Assert.Equal("{}", await File.ReadAllTextAsync(marker));
        }
        finally { if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link); }
    }

    [NativeSqlServerFact]
    public async Task Shared_recovery_owner_blocks_before_any_mutation()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service();
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        await using var db = await scenario.Environment.Factory.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        await using var lockCommand = new SqlCommand("DECLARE @result int; EXEC @result=sp_getapplock @Resource=@resource,@LockMode='Shared',@LockOwner='Session',@LockTimeout=0; SELECT @result", (SqlConnection)db.Database.GetDbConnection());
        lockCommand.Parameters.AddWithValue("@resource", SqlDerivedIndexRecoveryStore.LockResource);
        Assert.True(Convert.ToInt32(await lockCommand.ExecuteScalarAsync()) >= 0);
        var error = await Assert.ThrowsAsync<NativeOperationException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "shared-owner", scenario.Actor, CancellationToken.None));
        Assert.Equal("index-retention-owner-not-drained", error.ReasonCode);
        Assert.True(await db.IndexGenerations.AnyAsync(value => value.Id == scenario.Historical.Id));
        Assert.Empty(await db.NativeOperationReceipts.Where(value => value.ActorSurface == scenario.Actor).ToArrayAsync());
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
        await db.Database.CloseConnectionAsync();
    }

    [NativeSqlServerFact]
    public async Task Interrupted_restoration_reports_pending_and_original_delete_cannot_resume()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service();
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        var deleted = await service.CommitAsync(command, preview.ConfirmationId, "restore-delete", scenario.Actor, CancellationToken.None);
        var restore = scenario.Mutation(plan, deleted.OperationId);
        var restoration = await service.PreviewAsync(restore, scenario.Actor, CancellationToken.None);
        service = scenario.Service(point => { if (point == "before-files") throw new IOException("interrupted restoration"); });
        await Assert.ThrowsAsync<IOException>(async () => await service.CommitAsync(restore, restoration.ConfirmationId, "restore-pending", scenario.Actor, CancellationToken.None));
        service = scenario.Service();
        var oldDeletion = await service.CommitAsync(command, preview.ConfirmationId, "restore-delete", scenario.Actor, CancellationToken.None);
        Assert.Equal("restoration-files-pending", oldDeletion.Outcome);
        Assert.False(Directory.Exists(scenario.Historical.IndexPath));
        var restored = await service.CommitAsync(restore, restoration.ConfirmationId, "restore-pending", scenario.Actor, CancellationToken.None);
        Assert.Equal("completed", restored.Outcome);
        Assert.True(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerFact]
    public async Task Unknown_query_owner_blocks_even_when_its_sql_session_is_gone()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var command = scenario.Mutation(plan);
        var service = scenario.Service();
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        var ownerId = Guid.NewGuid();
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            db.CorpusQueryLeases.Add(new() { Id = ownerId, GenerationId = scenario.ActiveId, CorpusEpoch = scenario.Historical.CorpusEpoch!.Value,
                ModelFingerprint = scenario.Historical.ModelFingerprint, Dimensions = scenario.Historical.Dimensions, OwnerInstanceId = Guid.NewGuid(),
                OwnerProcessId = int.MaxValue, OwnerStartedAtUtc = DateTimeOffset.UtcNow, OwnerMachineFingerprint = new string('0', 64), SqlSessionId = int.MaxValue,
                CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<NativeOperationException>(async () => await service.CommitAsync(command, preview.ConfirmationId, "unknown-owner", scenario.Actor, CancellationToken.None));
        Assert.Equal("index-retention-owner-not-drained", error.ReasonCode);
        await using var check = await scenario.Environment.Factory.CreateDbContextAsync();
        Assert.True(await check.CorpusQueryLeases.AnyAsync(value => value.Id == ownerId));
        Assert.True(await check.IndexGenerations.AnyAsync(value => value.Id == scenario.Historical.Id));
        Assert.Empty(await check.NativeOperationReceipts.Where(value => value.ActorSurface == scenario.Actor).ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task File_finalisation_holds_the_real_publication_fence()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = scenario.Service(point => { if (point != "before-files") return; entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test barrier expired"); });
        var command = scenario.Mutation(plan);
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        var commit = Task.Run(async () => await service.CommitAsync(command, preview.ConfirmationId, "publication-race", scenario.Actor, CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var writer = await scenario.Environment.Factory.CreateDbContextAsync();
            await writer.Database.OpenConnectionAsync();
            await writer.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 250");
            await using var transaction = await writer.Database.BeginTransactionAsync();
            var blocked = await Assert.ThrowsAsync<SqlException>(() => SqlPublishedPassageSelection.AcquireFenceAsync(writer, CancellationToken.None));
            Assert.Equal(1222, blocked.Number);
            Assert.True(Directory.Exists(scenario.Historical.IndexPath));
        }
        finally { release.Set(); await commit.WaitAsync(TimeSpan.FromSeconds(10)); }
        await using var subsequent = await scenario.Environment.Factory.CreateDbContextAsync();
        await using var nextTransaction = await subsequent.Database.BeginTransactionAsync();
        await SqlPublishedPassageSelection.AcquireFenceAsync(subsequent, CancellationToken.None);
        Assert.False(await subsequent.IndexGenerations.AnyAsync(value => value.Id == scenario.Historical.Id));
        Assert.False(Directory.Exists(scenario.Historical.IndexPath));
    }

    [NativeSqlServerFact]
    public async Task Changed_canonical_vector_blocks_selective_restore()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        var plan = await scenario.PlanAsync();
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var service = scenario.Service();
        var command = scenario.Mutation(plan);
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        var deleted = await service.CommitAsync(command, preview.ConfirmationId, "restore-binding", scenario.Actor, CancellationToken.None);
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            (await db.Vectors.SingleAsync()).IsDeleted = true;
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<NativeOperationException>(async () => await service.PreviewAsync(scenario.Mutation(plan, deleted.OperationId), scenario.Actor, CancellationToken.None));
        Assert.Equal("index-retention-canonical-vectors-changed", error.ReasonCode);
        Assert.False(Directory.Exists(scenario.Historical.IndexPath));
    }

    [Visibility.RepositoryRecoveryMigrationDeploymentTests.RecoveryScaleFact]
    public async Task Exact_unit_handles_fifty_thousand_members_and_large_files_within_its_existing_deadline()
    {
        await using var scenario = await Scenario.CreateAsync(fixture);
        await using (var db = await scenario.Environment.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""
                SET NOCOUNT ON;
                DECLARE @artifact uniqueidentifier=(SELECT TOP(1) Id FROM Artifacts WHERE Stage=3);
                DECLARE @canonical uniqueidentifier=(SELECT TOP(1) IndexGenerationId FROM Vectors);
                DECLARE @payload varbinary(max)=0x0000803F+CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),CHAR(0)),4092));
                DECLARE @hash varchar(64)=LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@payload),2));
                SELECT TOP(49999) CONVERT(int,ROW_NUMBER() OVER(ORDER BY (SELECT NULL))) n INTO #Numbers FROM sys.all_objects a CROSS JOIN sys.all_objects b;
                INSERT TextChunks(ArtifactId,SourceRevision,Ordinal,StartOffset,Length,Content,ContentHash,PassagePolicyFingerprint,ContextHeader,SearchInputHash)
                    SELECT @artifact,1,n,0,1,N'x',REPLICATE('a',64),N'synthetic',N'',REPLICATE('a',64) FROM #Numbers;
                INSERT Vectors(TextChunkId,ModelFingerprint,Dimensions,[Values],TextChunkContentHash,PayloadChecksum,SourceRevision,IsDeleted,IndexGenerationId,CreatedAtUtc,SearchInputHash)
                    SELECT c.Id,N'synthetic-retention-profile',1024,@payload,REPLICATE('a',64),@hash,1,0,@canonical,SYSDATETIMEOFFSET(),REPLICATE('a',64)
                    FROM TextChunks c WHERE c.ArtifactId=@artifact AND c.Ordinal>0;
                INSERT IndexGenerationVectors(GenerationId,VectorId) SELECT {0},VectorId FROM Vectors WHERE VectorId NOT IN (SELECT VectorId FROM IndexGenerationVectors WHERE GenerationId={0});
                UPDATE IndexGenerations SET VectorCount=50000 WHERE Id={0};
                DROP TABLE #Numbers;
                """, scenario.Historical.Id);
        }
        await using (var file = File.OpenWrite(Path.Combine(scenario.Historical.IndexPath, "index.usearch"))) file.SetLength(210_000_000);
        var prepareTimer = Stopwatch.StartNew();
        var plan = await scenario.PlanAsync();
        var prepareMs = prepareTimer.Elapsed.TotalMilliseconds;
        Assert.Equal(50_000, plan.MaximumMembersPerUnit);
        await scenario.Retention.StageAsync(plan, scenario.PlanDirectory, CancellationToken.None);
        var service = scenario.Service();
        var command = scenario.Mutation(plan);
        var preview = await service.PreviewAsync(command, scenario.Actor, CancellationToken.None);
        var timer = Stopwatch.StartNew();
        var deleted = await service.CommitAsync(command, preview.ConfirmationId, "scale-delete", scenario.Actor, CancellationToken.None);
        var deleteMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal("completed", deleted.Outcome);
        var restore = scenario.Mutation(plan, deleted.OperationId);
        var restorePreview = await service.PreviewAsync(restore, scenario.Actor, CancellationToken.None);
        timer.Restart();
        var restored = await service.CommitAsync(restore, restorePreview.ConfirmationId, "scale-restore", scenario.Actor, CancellationToken.None);
        var restoreMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal("completed", restored.Outcome);
        Assert.True(deleteMs < 20_000 && restoreMs < 20_000);
        Assert.True(GC.GetTotalMemory(false) < 300_000_000);
        await using var check = await scenario.Environment.Factory.CreateDbContextAsync();
        Assert.Equal(50_000, await check.Vectors.LongCountAsync());
        Assert.Equal(50_000, await check.IndexGenerationVectors.LongCountAsync(value => value.GenerationId == scenario.Historical.Id));
        output.WriteLine($"members=50000 file_bytes=210000000 prepare_ms={prepareMs:F2} delete_ms={deleteMs:F2} restore_ms={restoreMs:F2} managed_bytes={GC.GetTotalMemory(false)}");
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required SqlToUsearchRebuildTests.PipelineEnvironment Environment { get; init; }
        public required IndexGenerationEntity Historical { get; init; }
        public string Actor => "retention-test:" + Historical.Id.ToString("N");
        public required Guid ActiveId { get; init; }
        public required string VectorHash { get; init; }
        public required string RecoveryRoot { get; init; }
        public required string PlanDirectory { get; init; }
        public required SqlIndexRetentionOperations Retention { get; init; }
        public static async Task<Scenario> CreateAsync(NativeSqlServerFixture fixture)
        {
            var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Canonical retained acceptance.");
            var recovery = Path.Combine(environment.IndexRoot, "recovery-evidence");
            var planDirectory = Path.Combine(environment.IndexRoot, "private-plan");
            Directory.CreateDirectory(recovery);
            await using var db = await environment.Factory.CreateDbContextAsync();
            var state = await db.IndexState.SingleAsync();
            var active = await db.IndexGenerations.SingleAsync(value => value.Id == state.ActiveIndexGenerationId);
            var id = Guid.NewGuid();
            var historical = new IndexGenerationEntity { Id = id, CorpusEpoch = state.CorpusEpoch, CorpusVersion = 0,
                ModelFingerprint = active.ModelFingerprint, Dimensions = active.Dimensions,
                IndexPath = Path.Combine(environment.IndexRoot, "generations", id.ToString("N")), MetadataChecksum = active.MetadataChecksum,
                VectorCount = 1, CreatedAtUtc = DateTimeOffset.UtcNow, ValidatedAtUtc = DateTimeOffset.UtcNow };
            Directory.CreateDirectory(historical.IndexPath);
            foreach (var file in Directory.EnumerateFiles(active.IndexPath)) File.Copy(file, Path.Combine(historical.IndexPath, Path.GetFileName(file)));
            db.IndexGenerations.Add(historical);
            var vector = await db.Vectors.SingleAsync();
            db.IndexGenerationVectors.Add(new() { GenerationId = id, VectorId = vector.VectorId });
            await db.SaveChangesAsync();
            var fs = new DerivedIndexFileSystem(new(environment.IndexRoot));
            return new() { Environment = environment, Historical = historical, ActiveId = active.Id, VectorHash = vector.PayloadChecksum,
                RecoveryRoot = recovery, PlanDirectory = planDirectory,
                Retention = new(environment.Factory, new(environment.IndexRoot, recovery,
                    path => fs.TryCanonicalIntendedGenerationPath(path, out var canonical) ? canonical : null, fs.IsValidDirectory)) };
        }
        public Task<IndexRetentionManifest> PlanAsync() => Retention.PrepareAsync(Guid.NewGuid(), [Historical.Id], [ActiveId], PlanDirectory, CancellationToken.None);
        public string Snapshot(IndexRetentionManifest plan) => Path.Combine(RecoveryRoot, "IndexRetention", plan.ManifestId.ToString("N"), Historical.Id.ToString("N"));
        public NativeCorpusMutation Mutation(IndexRetentionManifest plan, Guid? deletionReceipt = null) => new(
            deletionReceipt is null ? "index_retention" : "index_retention_restore",
            JsonSerializer.SerializeToElement(new { manifestId = plan.ManifestId, manifestHash = SqlIndexRetentionOperations.ManifestHash(plan),
                generationId = Historical.Id, deletionReceiptId = deletionReceipt }));
        public NativeCorpusCommandService Service(Action<string>? interrupt = null)
        {
            Retention.FailureInjector = interrupt;
            return new(new SqlNativeOperationStore(Environment.Factory, TimeProvider.System, indexRetention: Retention),
                new SqlNativeCorpusActionStore(Environment.Factory, new UnusedPathPolicy(), new LocalPrivateContentDisclosure(), indexRetention: Retention));
        }
        public ValueTask DisposeAsync() => Environment.DisposeAsync();
    }
    private sealed class UnusedPathPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) => throw new NotSupportedException();
    }
}
