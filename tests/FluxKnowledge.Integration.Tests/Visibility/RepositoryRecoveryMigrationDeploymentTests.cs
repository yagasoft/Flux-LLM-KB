using System.Diagnostics;
using System.Security.Cryptography;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Visibility;

public sealed class RepositoryRecoveryMigrationDeploymentTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [RecoveryScaleFact]
    public async Task Retained_stream_verifies_production_cardinality_with_bounded_memory()
    {
        var work = await SqlTestData.SeedWorkItemAsync(fixture, DateTimeOffset.UnixEpoch, PublicJobState.WorkerQueued, null);
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "tests/native/repository-recovery-retained-scale.ps1";
        while (source is not null && !File.Exists(Path.Combine(source.FullName, relative))) source = source.Parent;
        Assert.NotNull(source);
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source.FullName, relative), "-SourceRoot", source.FullName, "-PipelineRecordId", work.PipelineRecordId.Value.ToString() })
            start.ArgumentList.Add(argument);
        start.Environment["FLUXKNOWLEDGE_RECOVERY_DISPOSABLE_SQL"] = fixture.ConnectionString;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    public sealed class RecoveryScaleFactAttribute : FactAttribute
    {
        public RecoveryScaleFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_RECOVERY_SCALE") != "1")
                Skip = "Opt-in production-scale retained-state benchmark; focused evidence is retained separately.";
        }
    }

    [NativeSqlServerFact]
    public async Task Incremental_recovery_migration_is_atomic_reconciles_exactly_and_preserves_retained_bytes()
    {
        var work = await SqlTestData.SeedWorkItemAsync(fixture, DateTimeOffset.UnixEpoch, PublicJobState.WorkerQueued, null);
        await using var db = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        // Cross the proof's page boundary and retain actual replay/owner evidence.
        for (var index = 0; index < 130; index++)
            db.GpuSchedulerOperationReceipts.Add(new GpuSchedulerOperationReceiptEntity
            {
                OperationId = Guid.NewGuid(), OperationKind = "admission", RequestFingerprint = new string('c', 64),
                AdmissionDisposition = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch
            });
        var workerId = Guid.NewGuid();
        db.NativeWorkerInstances.Add(new NativeWorkerInstanceEntity
        {
            InstanceId = workerId, ExecutorKey = "synthetic", ExecutableFingerprint = new string('d', 64),
            ProtocolVersion = "synthetic", State = 9, LaunchedAtUtc = DateTimeOffset.UnixEpoch, ExitedAtUtc = DateTimeOffset.UnixEpoch
        });
        db.NativeWorkerLifecycleEvidence.Add(new NativeWorkerLifecycleEvidenceEntity
        {
            OperationId = Guid.NewGuid(), InstanceId = workerId, LifecycleClass = 9,
            RequestFingerprint = new string('e', 64), ObservedAtUtc = DateTimeOffset.UnixEpoch, CreatedAtUtc = DateTimeOffset.UnixEpoch
        });
        var artifactId = Guid.NewGuid();
        var generationId = Guid.NewGuid();
        db.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId, PipelineRecordId = work.PipelineRecordId.Value, SourceRevision = 1, Stage = 3,
            ContentHash = new string('a', 64), ContentType = "text/plain",
            SearchText = new string('a', 8191) + "\U0001f642\"\\\r\n" + new string('b', 32771),
            CreatedAtUtc = DateTimeOffset.Parse("2026-10-05T01:02:03.4567890+03:00")
        });
        db.IndexGenerations.Add(new IndexGenerationEntity
        {
            Id = generationId, ModelFingerprint = "synthetic:256", Dimensions = 256,
            IndexPath = "synthetic", MetadataChecksum = new string('b', 64), CreatedAtUtc = DateTimeOffset.UnixEpoch
        });
        var chunk = new TextChunkEntity { ArtifactId = artifactId, SourceRevision = 1, Content = "saved", Length = 5, ContentHash = new string('a', 64) };
        db.TextChunks.Add(chunk);
        await db.SaveChangesAsync();
        var bytes = new byte[1024];
        bytes[0] = 1;
        var vector = new VectorEntity
        {
            TextChunkId = chunk.Id, SourceRevision = 1, ModelFingerprint = "synthetic:256", Dimensions = 256,
            Values = bytes, TextChunkContentHash = chunk.ContentHash, PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            IndexGenerationId = generationId, CreatedAtUtc = DateTimeOffset.UnixEpoch
        };
        db.Vectors.Add(vector);
        await db.SaveChangesAsync();
        db.IndexGenerationVectors.Add(new IndexGenerationVectorEntity { GenerationId = generationId, VectorId = vector.VectorId });
        await db.SaveChangesAsync();
        const string baseline = "20261003220411_AddCanonicalCodeDisclosureProof";
        const string target = "20261004175522_AddRepositoryRecoveryAuthority";
        await db.GetService<IMigrator>().MigrateAsync(baseline);
        var script = db.GetService<IMigrator>().GenerateScript(baseline, target);
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "tests/native/repository-recovery-migration-sql.ps1";
        while (source is not null && !File.Exists(Path.Combine(source.FullName, relative))) source = source.Parent;
        Assert.NotNull(source);
        var path = Path.Combine(Path.GetTempPath(), $"flux-recovery-{Guid.NewGuid():N}.sql");
        try
        {
            await File.WriteAllTextAsync(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source.FullName, relative), "-SourceRoot", source.FullName, "-SqlPath", path })
                start.ArgumentList.Add(argument);
            start.Environment["FLUXKNOWLEDGE_RECOVERY_DISPOSABLE_SQL"] = fixture.ConnectionString;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally { File.Delete(path); }
    }
}
