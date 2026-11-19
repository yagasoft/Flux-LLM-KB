using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentRetirementOperations
{
    private const int ManifestRetirementPageSize = 16;

    private async ValueTask<string?> AdvanceManifestPageAsync(SourceDeletionWorkItem work, CancellationToken token)
    {
        await using var read = await contexts.CreateDbContextAsync(token);
        var targets = await read.SourceDeletionCleanupItems.AsNoTracking().Where(item => item.SourceDeletionOperationId == work.OperationId &&
            item.StorageKind == 3 && item.State == 0).OrderBy(item => item.RelativePath).Take(ManifestRetirementPageSize).ToArrayAsync(token);
        var ids = targets.Select(target => Guid.TryParseExact(target.RelativePath,"N",out var id)
            ? id : throw new InvalidOperationException("segment-retirement-source-target-invalid")).ToArray();
        if (ids.Length == 0) return null;

        // Only SQL work can be retried here. The entire page's durable intents must
        // commit before any physical deletion, including after a lost acknowledgement.
        var plans = await read.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var mark = await contexts.CreateDbContextAsync(token);
            await using var transaction = await mark.Database.BeginTransactionAsync(IsolationLevel.Serializable,token);
            if (!await EnterAsync(mark,token)) throw new InvalidOperationException("segment-retirement-query-owned");
            var generations = await mark.IndexGenerations.Where(row => ids.Contains(row.Id)).ToArrayAsync(token);
            if (generations.Length != ids.Length) throw new InvalidOperationException("segment-retirement-source-target-invalid");
            var withMemberships=(await mark.IndexGenerationSegments.Where(row=>ids.Contains(row.GenerationId)).Select(row=>row.GenerationId).Distinct().ToArrayAsync(token)).ToHashSet();
            var toMark=generations.Where(row=>row.ReclaimedAtUtc is null &&
                (row.RetirementOperationId is null || row.RetiredAtUtc is null || withMemberships.Contains(row.Id))).ToArray();
            var proof = await CheckManifestPagePinsAsync(mark,toMark,work.OperationId,token);
            var result = new List<(Guid Id,Guid Operation)>();
            foreach (var id in ids)
            {
                await ValidateSourceDeletionAsync(mark,work,3,id.ToString("N"),token);
                var generation = generations.Single(row => row.Id == id);
                var operation = generation.RetirementOperationId ?? Guid.NewGuid();
                if (toMark.Any(row=>row.Id==id))
                    await MarkManifestCoreAsync(mark,id,operation,work,proof,token);
                else if (generation.RetirementOperationId is null || generation.RetiredAtUtc is null)
                    throw new InvalidOperationException("segment-retirement-intent-mismatch");
                result.Add((id,operation));
            }
            await transaction.CommitAsync(token);
            return result;
        });

        if (FileOperationCheckpoint is { } marked) await marked("manifest-page-marked",read,token);
        var completed = new List<Guid>();
        var segmentProgress=false;
        // A fresh original session and a fresh reference proof protect this phase.
        // Never run filesystem work through an EF execution strategy.
        await using (var reclaim = await NewFileOperationContextAsync(token))
        await using (var transaction = await reclaim.Database.BeginTransactionAsync(IsolationLevel.Serializable,token))
        {
            if (!await EnterAsync(reclaim,token)) return "source-delete-segment-owner-active";
            if (FileOperationCheckpoint is { } admitted) await admitted("admitted",reclaim,token);
            var generations = await reclaim.IndexGenerations.Where(row => ids.Contains(row.Id)).ToArrayAsync(token);
            if (generations.Length != ids.Length) throw new InvalidOperationException("segment-retirement-source-target-invalid");
            foreach(var plan in plans)
            {
                await ValidateSourceDeletionAsync(reclaim,work,3,plan.Id.ToString("N"),token);
                var generation=generations.Single(row=>row.Id==plan.Id);
                if(generation.RetirementOperationId!=plan.Operation || generation.RetiredAtUtc is null)
                    throw new InvalidOperationException("segment-retirement-intent-mismatch");
            }
            if (FileOperationCheckpoint is { } checking) await checking("manifest-page-reference-check",reclaim,token);
            var proof = await CheckManifestPagePinsAsync(reclaim,generations.Where(row => row.ReclaimedAtUtc is null).ToArray(),work.OperationId,token);
            var operations=plans.Select(plan=>plan.Operation).ToArray();
            var segments=await reclaim.IndexSegments.AsNoTracking().Where(row=>row.State!=3 && row.RetirementOperationId!=null &&
                operations.Contains(row.RetirementOperationId.Value) && reclaim.SourceDeletionCleanupItems.Any(target=>
                    target.SourceDeletionOperationId==work.OperationId && target.StorageKind==4 && target.RelativePath==row.Id))
                .OrderBy(row=>row.Id).Take(16).Select(row=>new{row.Id,row.RetirementOperationId}).ToArrayAsync(token);
            foreach(var segment in segments)
                segmentProgress |= await TryReclaimSegmentCoreAsync(reclaim,(SqlTransaction)transaction.GetDbTransaction(),segment.Id,segment.RetirementOperationId!.Value,work,token);
            foreach (var plan in plans)
                if (await TryReclaimManifestCoreAsync(reclaim,(SqlTransaction)transaction.GetDbTransaction(),plan.Id,plan.Operation,work,proof,token))
                    completed.Add(targets.Single(target => target.RelativePath == plan.Id.ToString("N")).Id);
            await transaction.CommitAsync(token);
        }
        if (FileOperationCheckpoint is { } reclaimed) await reclaimed("manifest-page-reclaimed",read,token);
        if (completed.Count > 0)
        {
            await new SqlSourceDeletionStore(contexts,clock,queryOwnerProbe:probe).RecordCompletedManifestCleanupAsync(work,completed,token);
            return "source-delete-segment-retirement-progressing";
        }
        return segmentProgress ? "source-delete-segment-retirement-progressing" : "source-delete-segment-owner-active";
    }

    private static async Task<IReadOnlySet<Guid>> CheckManifestArtifactPageAsync(FluxKnowledgeDbContext context,
        IReadOnlyList<IndexGenerationEntity> generations,CancellationToken token)
    {
        if (generations.Count == 0) return new HashSet<Guid>();
        var references = generations.SelectMany(row => new[] { row.Id.ToString("N"),row.Id.ToString("D"),row.IndexPath }).ToArray();
        if (generations.Count > ManifestRetirementPageSize || references.Any(text => text.Length > 2048))
            throw new InvalidOperationException("segment-retirement-source-target-invalid");
        // Keep the original linguistic SQL equality, including case, padding and
        // zero-weight characters. A prefix/hash prefilter would change that contract.
        var collation = await context.Database.SqlQuery<string>(
            $"SELECT collation_name AS Value FROM sys.columns WHERE object_id=OBJECT_ID(N'Artifacts') AND name=N'SearchText'").SingleAsync(token);
        if (!Regex.IsMatch(collation,"^[A-Za-z0-9_]+$",RegexOptions.CultureInvariant))
            throw new InvalidOperationException("segment-retirement-reference-collation-invalid");
        var values = await context.Database.SqlQuery<string>(FormattableStringFactory.Create(
            "SELECT DISTINCT Reference COLLATE "+collation+" AS Value FROM OPENJSON({0}) WITH(Reference nvarchar(2048) '$') ORDER BY Value",
            JsonSerializer.Serialize(references))).ToArrayAsync(token);
        if (values.Length is < 1 or > 48) throw new InvalidOperationException("segment-retirement-reference-page-invalid");
        // SQL orders the bounded references using the actual column collation.
        // A balanced CASE reads only logarithmically many comparison prefixes per
        // body; a join/IN loop repeats LOB reads, while a hash join reads whole bodies.
        string Decision(int first,int last)
        {
            if (first == last) return "CASE WHEN artifact.[SearchText]={"+first.ToString(CultureInfo.InvariantCulture)+"} THEN 1 ELSE 0 END";
            var middle=(first+last)/2;
            return "CASE WHEN artifact.[SearchText]<={"+middle.ToString(CultureInfo.InvariantCulture)+"} THEN "+Decision(first,middle)+" ELSE "+Decision(middle+1,last)+" END";
        }
        var sql = $"""
            /* segment-retirement-manifest-reference-page */
            SELECT TOP(1) 1 AS Value
            FROM [Artifacts] artifact
            WHERE ({Decision(0,values.Length-1)})=1 AND (artifact.Stage <> 5 OR NOT EXISTS
                (SELECT 1 FROM [OutboxMessages] delivery WHERE delivery.CompletedArtifactId=artifact.Id AND delivery.DispatchedAtUtc IS NOT NULL))
            """;
        var pins = await context.Database.SqlQuery<int>(FormattableStringFactory.Create(sql,values.Cast<object>().ToArray())).ToArrayAsync(token);
        if (pins.Length > 0) throw new InvalidOperationException("segment-retirement-artefact-unconfirmed");
        return generations.Select(row => row.Id).ToHashSet();
    }
}
