using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed partial class SqlSegmentRetirementOperations
{
    private sealed record ManifestPinsProof(FluxKnowledgeDbContext Context,IDbContextTransaction Transaction,IReadOnlySet<Guid> Ids)
    {
        internal bool Covers(FluxKnowledgeDbContext context,Guid id)=>ReferenceEquals(Context,context) &&
            ReferenceEquals(Transaction,context.Database.CurrentTransaction) && Ids.Contains(id);
    }

    private static async Task<ManifestPinsProof> CheckManifestPagePinsAsync(FluxKnowledgeDbContext context,
        IReadOnlyList<IndexGenerationEntity> generations,Guid sourceDeletionOperation,CancellationToken token)
    {
        var transaction=context.Database.CurrentTransaction ?? throw new InvalidOperationException("segment-retirement-ownership-lost");
        if(generations.Count==0)return new(context,transaction,new HashSet<Guid>());
        if(generations.Count>ManifestRetirementPageSize)throw new InvalidOperationException("segment-retirement-source-target-invalid");
        var ids=generations.Select(row=>row.Id).ToArray();var nullableIds=ids.Select(id=>(Guid?)id).ToArray();
        var paths=generations.Select(row=>row.IndexPath).ToArray();var plain=ids.Select(id=>id.ToString("N")).ToArray();
        if(generations.Any(row=>row.FormatVersion!=2 || row.ValidatedAtUtc is null || row.EmbeddingJobId is not null) ||
            await context.IndexState.AnyAsync(row=>nullableIds.Contains(row.ActiveIndexGenerationId) || row.CorpusRebuildOperationId!=null,token) ||
            await context.CorpusQueryLeases.AnyAsync(row=>ids.Contains(row.GenerationId),token) ||
            await context.IndexPublicationCandidates.AnyAsync(row=>row.State<2 &&
                (nullableIds.Contains(row.BaseGenerationId) || nullableIds.Contains(row.DraftGenerationId) || nullableIds.Contains(row.ResultGenerationId)),token) ||
            await context.Vectors.AnyAsync(row=>ids.Contains(row.IndexGenerationId),token) ||
            await context.EmbeddingGpuRequests.AnyAsync(row=>ids.Contains(row.GenerationId),token) ||
            await context.SourceDeletionCleanupItems.AnyAsync(row=>row.State!=1 && plain.Contains(row.RelativePath) &&
                !(row.StorageKind==3 && (row.SourceDeletionOperationId==sourceDeletionOperation ||
                    context.SourceDeletionOperations.Any(operation=>operation.Id==row.SourceDeletionOperationId &&
                        context.SourceRootConfigurations.Any(root=>root.Id==operation.SourceRootId && root.State==(int)SourceRootState.Deleting)))),token))
            throw new InvalidOperationException("segment-retirement-manifest-referenced");
        var patterns=ids.SelectMany(id=>new[]{"%"+id.ToString("N")+"%","%"+id.ToString("D")+"%"}).ToArray();
        if(await context.IndexRecoveryPins.AnyAsync(row=>nullableIds.Contains(row.GenerationId) || paths.Contains(row.IndexPath!),token) ||
            await context.NativeOperationIntents.AnyAsync(row=>patterns.Any(pattern=>EF.Functions.Like(row.TargetMetadataJson,pattern,"\\")),token) ||
            await context.CorpusRebuildOperations.AnyAsync(row=>patterns.Any(pattern=>EF.Functions.Like(row.ManifestJson,pattern,"\\")),token))
            throw new InvalidOperationException("segment-retirement-recovery-referenced");
        var checkedIds=await CheckManifestArtifactPageAsync(context,generations,token);
        return new(context,transaction,checkedIds);
    }
}
