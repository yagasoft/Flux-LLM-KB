using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Legacy remains the default; SQL format admission cannot be bypassed by a process flag.</summary>
public sealed class SegmentedIndexPublisher(UsearchGenerationBuilder legacy, ISegmentedIndexStore store,
    IDerivedIndexRecoveryStore recovery, UsearchSegmentBuilder segments, UsearchManifestValidator manifests,
    bool enabled) : IIndexGenerationPublisher
{
    public async ValueTask<IndexGenerationCandidateSnapshot> BuildAndPlaceAsync(Guid indexGenerationId, CancellationToken cancellationToken)
    {
        if (!await store.IsRequiredAsync(cancellationToken)) return await legacy.BuildAndPlaceAsync(indexGenerationId, cancellationToken);
        if (!enabled) throw new InvalidOperationException("segmented-publication-disabled-for-required-format");
        segments.RequireCapacity(0);
        // Actual build execution holds shared recovery ownership, then durable candidate/process pins cover a lost session.
        await using var held = await recovery.TryAcquireSharedLeaseAsync(TimeSpan.FromSeconds(5), cancellationToken)
            ?? throw new InvalidOperationException("segmented-publication-maintenance-active");
        var candidate = await store.PrepareAsync(indexGenerationId, cancellationToken);
        try
        {
            var built = await segments.BuildAsync(candidate.ModelFingerprint, candidate.Dimensions,
                store.ReadBuildVectorsAsync(candidate.Id, cancellationToken), cancellationToken,
                (segment, vectors, token) => store.SealSegmentAsync(candidate.Id, segment, vectors, token), candidate.Id);
            var manifest = manifests.Place(candidate.PublicationStamp, candidate.ModelFingerprint, candidate.Dimensions,
                candidate.ReusedSegments.Concat(built).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray());
            await store.SealManifestAsync(candidate.Id, manifest, cancellationToken);
            return new(manifest.Generation, [], candidate.ExpectedStamp, candidate.Id);
        }
        catch (Exception exception) when (exception is FluxKnowledge.Application.Pipeline.PublicationSnapshotConflictException or
            FluxKnowledge.Application.Indexing.IndexStorageCapacityException)
        {
            // These refusals occur between native units; BuildOne has returned and disposed its handles.
            // Arbitrary I/O/native/disposal failures retain the pin because execution outcome may be unknown.
            try { await store.AbandonAsync(candidate.Id, cancellationToken); }
            catch (Exception cleanup) when (!cancellationToken.IsCancellationRequested)
            { exception.Data["candidate-abandonment-failure"] = cleanup.GetType().Name; }
            throw;
        }
    }

    public async ValueTask<IndexGenerationDescriptor> RebuildFromSqlAsync(Guid indexGenerationId, CancellationToken cancellationToken)
    {
        if (await store.IsRequiredAsync(cancellationToken)) throw new InvalidOperationException("segmented-explicit-rebuild-required");
        return await legacy.RebuildFromSqlAsync(indexGenerationId, cancellationToken);
    }

    public ValueTask AbandonAsync(IndexGenerationCandidateSnapshot candidate, CancellationToken cancellationToken) =>
        candidate.SegmentedCandidateId is { } id ? store.AbandonAsync(id, cancellationToken) : ValueTask.CompletedTask;
}
