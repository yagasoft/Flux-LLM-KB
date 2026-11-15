using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlIndexRecoveryReferences
{
    private async Task ValidateRetirementBindingAsync(FluxKnowledgeDbContext db, RecoveryArchiveRetirementPlan plan,
        CancellationToken token)
    {
        if (await DatabaseIdentityAsync(db, token) != plan.DatabaseIncarnation)
            throw new InvalidOperationException("index-recovery-retirement-database-changed");
        var state = await db.IndexState.AsNoTracking().SingleAsync(value => value.Id == 1, token);
        if (state.CorpusEpoch != plan.CorpusEpoch || state.CorpusVersion != plan.CorpusVersion ||
            state.ActiveIndexGenerationId != plan.ActiveGeneration ||
            state.CorpusRebuildOperationId is { } active && active != plan.CompletedRebuildId)
            throw new InvalidOperationException("index-recovery-retirement-publication-changed");
        foreach (var owner in plan.ServiceOwners)
        {
            owner.Validate();
            if (owners.Observe(owner) != GpuInteractiveOwnerObservation.Alive)
                throw new InvalidOperationException("index-recovery-retirement-running-service-changed");
        }
        var operations = await db.CorpusRebuildOperations.AsNoTracking().OrderBy(value => value.Id).Take(129)
            .Select(value => new { value.Id, value.TargetEpoch, value.ManifestHash, value.CompletedAtUtc, value.SupersedesOperationId })
            .ToArrayAsync(token);
        if (operations.Length > 128) throw new InvalidOperationException("index-recovery-retirement-operation-budget");
        var baseline = operations.SingleOrDefault(value => value.Id == plan.CompletedRebuildId);
        if (plan.CompletedRebuildId is not null && (baseline is null || baseline.CompletedAtUtc is null ||
            baseline.TargetEpoch != plan.CorpusEpoch || baseline.ManifestHash != plan.CompletedRebuildManifestHash))
            throw new InvalidOperationException("index-recovery-retirement-baseline-rebuild-changed");
        foreach (var operation in operations.Where(value => value.CompletedAtUtc is null))
        {
            var successor = operation;
            var visited = new HashSet<Guid> { successor.Id };
            while (successor.CompletedAtUtc is null)
            {
                successor = operations.SingleOrDefault(value => value.SupersedesOperationId == successor.Id);
                if (successor is null || !visited.Add(successor.Id))
                    throw new InvalidOperationException("index-recovery-retirement-unfinished-rebuild");
            }
            if (successor.Id != plan.CompletedRebuildId || successor.TargetEpoch != plan.CorpusEpoch ||
                successor.ManifestHash != plan.CompletedRebuildManifestHash)
                throw new InvalidOperationException("index-recovery-retirement-unbound-rebuild-successor");
        }
        if (await db.NativeOperationIntents.AsNoTracking().AnyAsync(value => value.ConsumedAtUtc == null &&
            value.ExpiresAtUtc > clock.GetUtcNow() && value.Action.Contains("index_retention"), token))
            throw new InvalidOperationException("index-recovery-retirement-pending-retention");
        var roots = await db.SourceRootConfigurations.AsNoTracking().Select(value => value.CanonicalPath).Take(1025).ToArrayAsync(token);
        var movedPaths = plan.Moves.Select(move => Path.Combine(plan.RecoveryRoot, move.RelativePath))
            .Append(plan.QuarantineRoot).ToArray();
        if (roots.Length > 1024 || roots.Any(root => movedPaths.Any(path => RecoveryArchiveRetirement.Within(path, root) ||
            RecoveryArchiveRetirement.Within(root, path))))
            throw new InvalidOperationException("index-recovery-retirement-quarantine-overlaps-source");
        await ValidateAttachedSqlFilesAsync(db, movedPaths, plan, token);
        RecoveryArchiveRetirement.Validate(plan);
        if (plan.RuntimeSnapshots is not null) RecoveryArchiveRetirement.ValidateRuntimeSnapshots(plan);
    }

    private static async Task ValidateAttachedSqlFilesAsync(FluxKnowledgeDbContext db, string[] paths,
        RecoveryArchiveRetirementPlan plan, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); command.CommandTimeout = 5;
        command.CommandText = "SELECT ISNULL(IS_SRVROLEMEMBER(N'sysadmin'),0);";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 1)
        {
            // Only the existing create-only native fixture may use restricted
            // metadata visibility, and never against a live application path.
            var catalog = db.Database.GetDbConnection().Database;
            var scratch = Path.GetFullPath(Path.GetTempPath());
            var all = paths.Concat([plan.RecoveryRoot, plan.IndexRoot, Path.GetDirectoryName(plan.ApplicationEntry)!]);
            if (!catalog.StartsWith("FluxKnowledge_Phase1Tests_", StringComparison.Ordinal) || all.Any(path =>
                !RecoveryArchiveRetirement.Within(path, scratch) ||
                RecoveryArchiveRetirement.Within(path, @"I:\FluxKnowledge") ||
                RecoveryArchiveRetirement.Within(path, @"C:\inetpub\FluxKnowledge") ||
                RecoveryArchiveRetirement.Within(path, @"J:\Models")))
                throw new InvalidOperationException("index-recovery-retirement-global-sql-visibility-required");
        }
        command.CommandText = "SELECT TOP (4097) physical_name FROM sys.master_files;";
        await using var reader = await command.ExecuteReaderAsync(token); var count = 0;
        while (await reader.ReadAsync(token))
        {
            if (++count > 4096 || paths.Any(path => RecoveryArchiveRetirement.Within(reader.GetString(0), path)))
                throw new InvalidOperationException("index-recovery-retirement-attached-sql-file-or-budget");
        }
    }

    private static bool WritersMatch(IReadOnlyList<IndexRecoveryWriterEntity> actual, IReadOnlyList<RecoveryWriterDisposition> expected) =>
        actual.Count == expected.Count && expected.Select(value => value.Id).Distinct().Count() == expected.Count &&
        actual.All(writer => expected.Any(value => value.Id == writer.Id && value.RecoveryRoot == writer.RecoveryRoot &&
            value.OwnerProcessId == writer.OwnerProcessId && value.OwnerStartedAtUtc == writer.OwnerStartedAtUtc &&
            value.OwnerMachineFingerprint == writer.OwnerMachineFingerprint && value.OwnerExitSufficient == writer.OwnerExitSufficient &&
            value.CreatedAtUtc == writer.CreatedAtUtc));
}
