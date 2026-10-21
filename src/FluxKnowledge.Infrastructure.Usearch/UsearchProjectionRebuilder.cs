using System.Security.Cryptography;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Explicit conversion/repair only; not part of startup or ordinary Publish.</summary>
public sealed class UsearchProjectionRebuilder(UsearchIndexOptions options, IIndexSegmentReadLeaseFactory paths,
    ISegmentedIndexStore segments, IIndexProjectionRebuildStore rebuilds, IDerivedIndexRecoveryStore recovery,
    UsearchLegacyProjectionBuilder legacy, IndexSegmentBuildOptions? budgets = null)
{
    public async ValueTask<IndexGenerationDescriptor> RunAsync(IndexProjectionRebuildPlan plan, CancellationToken token)
    {
        await using var held = await recovery.TryAcquireSharedLeaseAsync(TimeSpan.FromSeconds(5), token)
            ?? throw new InvalidOperationException("index-rebuild-maintenance-active");
        var progress = await rebuilds.PrepareRebuildAsync(plan, token);
        if (progress.AlreadyCommitted) return progress.SealedGeneration ?? throw new InvalidOperationException("index-rebuild-committed-proof-missing");
        var result = progress.SealedGeneration;
        var manifests = new UsearchManifestValidator(options, paths);
        if (result is null)
        {
            if (plan.TargetFormat == 2)
            {
                var built = await new UsearchSegmentBuilder(options, paths, budgets).BuildAsync(plan.ModelFingerprint, plan.Dimensions,
                    segments.ReadBuildVectorsAsync(plan.OperationId, token), token,
                    (segment, vectors, ct) => segments.SealSegmentAsync(plan.OperationId, segment, vectors, ct), plan.OperationId);
                var manifest = manifests.Place(plan.Stamp, plan.ModelFingerprint, plan.Dimensions, built);
                await segments.SealManifestAsync(plan.OperationId, manifest, token);
                result = manifest.Generation;
            }
            else
            {
                result = await legacy.BuildAsync(plan, () => segments.ReadBuildVectorsAsync(plan.OperationId, token), token);
                await rebuilds.SealLegacyRebuildAsync(plan.OperationId, result, token);
            }
        }
        if (result.FormatVersion == 2)
        {
            var manifest = await segments.ReadManifestAsync(result.Id, token);
            manifests.Validate(manifest);
            foreach (var segment in manifest.Segments)
            {
                token.ThrowIfCancellationRequested();
                using var path = paths.Acquire(segment.DirectoryPath);
                using (var file = File.OpenRead(Path.Combine(segment.DirectoryPath, "index.usearch")))
                    if (file.Length != segment.FileLength || Convert.ToHexStringLower(SHA256.HashData(file)) != segment.FileChecksum)
                        throw new IndexGenerationValidationException("index-rebuild-segment-file-proof-mismatch-use-new-operation");
                using var native = new USearchIndex(Path.Combine(segment.DirectoryPath, "index.usearch"), false);
                if (native.Size() != (ulong)segment.VectorCount || native.Dimensions() != (ulong)segment.Dimensions)
                    throw new IndexGenerationValidationException("index-rebuild-segment-native-profile-invalid");
            }
        }
        else await legacy.ValidateAsync(result, () => segments.ReadBuildVectorsAsync(plan.OperationId, token), token);
        return await rebuilds.ActivateRebuildAsync(plan.OperationId, token);
    }
}
