using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>One owned request/batch; embedding unloads before ranking, with no retained GPU model.</summary>
public sealed class BgeGpuInferenceSession(IBgeGpuModelFactory models) : IEmbeddingGpuInference
{
    public async ValueTask<GpuInteractiveNativeResult<T>> ExecuteSearchAsync<T>(GpuOwnedWorkContext ownership,
        Func<IEmbeddingProvider, IPassageReranker, CancellationToken, ValueTask<T>> work)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(work);
        var access = new RequestAccess(models, ownership);
        try
        {
            access.RequireActive();
            var value = await work(access.Embedding, access.Reranker, ownership.CancellationToken).ConfigureAwait(false);
            access.RequireActive();
            if (!access.IsComplete) throw new BgeInferenceException("bge-request-phase-incomplete");
            return new(value, ownership.NativeCapacityReleased);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(default, ownership.NativeCapacityReleased, Reason(exception));
        }
        finally { access.End(); }
    }

    public async ValueTask<GpuInteractiveNativeResult<IReadOnlyList<EmbeddingResult>>> EmbedBatchAsync(
        GpuOwnedWorkContext ownership, IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        try
        {
            ownership.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
            if (texts.Count is < 1 or > BgeInputBatch.MaximumBatch) throw new BgeInferenceException("bge-batch-invalid");
            foreach (var text in texts) ValidateText(text);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownership.CancellationToken);
            IReadOnlyList<EmbeddingResult> results;
            using (var lease = await models.OpenEmbeddingAsync(ownership).ConfigureAwait(false))
            {
                ownership.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
                ValidateProfile(lease.Model);
                results = await lease.Model.CreateEmbeddingsAsync(texts, linked.Token).ConfigureAwait(false);
                if (results.Count != texts.Count) throw new BgeInferenceException("bge-embedding-output-invalid");
                foreach (var result in results) ValidateEmbedding(result);
            }
            linked.Token.ThrowIfCancellationRequested();
            return new(results, ownership.NativeCapacityReleased);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new(null, ownership.NativeCapacityReleased, Reason(exception));
        }
    }

    private static string Reason(Exception exception) => exception is PassageRetrievalRefusalException refusal ? refusal.Status :
        exception is PublicationSnapshotConflictException ? "index-updating" :
        exception is BgeInferenceException bge ? bge.ReasonCode :
        exception is OperationCanceledException ? "bge-request-cancelled" : "bge-request-work-failed";

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16384) throw new BgeInferenceException("bge-embedding-input-invalid");
    }

    private static void ValidateProfile(IBatchedEmbeddingProvider provider)
    {
        if (provider.Profile != new EmbeddingProfile(BgeOfflineModels.EmbeddingFingerprint, 1024) || provider.MaximumBatchSize < 4)
            throw new BgeInferenceException("bge-embedding-profile-invalid");
    }

    private static void ValidateEmbedding(EmbeddingResult result)
    {
        if (result.ModelFingerprint != BgeOfflineModels.EmbeddingFingerprint || result.Values.Count != 1024 ||
            result.Values.Any(value => !float.IsFinite(value)) || Math.Abs(result.Values.Sum(value => (double)value * value) - 1) > 0.001)
            throw new BgeInferenceException("bge-embedding-output-invalid");
    }

    private sealed class RequestAccess
    {
        private readonly IBgeGpuModelFactory _models;
        private readonly GpuOwnedWorkContext _ownership;
        private int _ended;
        private int _phase;
        internal RequestAccess(IBgeGpuModelFactory models, GpuOwnedWorkContext ownership)
        {
            _models = models; _ownership = ownership; Embedding = new EmbeddingAccess(this); Reranker = new RerankerAccess(this);
        }
        internal IEmbeddingProvider Embedding { get; }
        internal IPassageReranker Reranker { get; }
        internal bool IsComplete => Volatile.Read(ref _phase) is 2 or 4;
        internal void End() => Interlocked.Exchange(ref _ended, 1);
        internal void RequireActive()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended) != 0, this);
            _ownership.RequireActive(BgeOfflineModels.GpuRuntimeKey, BgeOfflineModels.GpuSettingsFingerprint);
        }

        private sealed class EmbeddingAccess(RequestAccess request) : IEmbeddingProvider
        {
            public async ValueTask<EmbeddingResult> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
            {
                request.RequireActive(); ValidateText(text);
                if (Interlocked.CompareExchange(ref request._phase, 1, 0) != 0) throw new BgeInferenceException("bge-request-phase-invalid");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request._ownership.CancellationToken);
                EmbeddingResult result;
                using (var lease = await request._models.OpenEmbeddingAsync(request._ownership).ConfigureAwait(false))
                {
                    request.RequireActive(); ValidateProfile(lease.Model);
                    result = await lease.Model.CreateEmbeddingAsync(text, linked.Token).ConfigureAwait(false);
                    ValidateEmbedding(result);
                }
                linked.Token.ThrowIfCancellationRequested();
                request.RequireActive(); Interlocked.Exchange(ref request._phase, 2);
                return result;
            }
        }

        private sealed class RerankerAccess(RequestAccess request) : IPassageReranker
        {
            public async ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankPassage> passages, CancellationToken cancellationToken)
            {
                request.RequireActive(); ValidateText(query);
                if (passages.Count is < 1 or > 50 || passages.Any(p => p is null || p.PassageId <= 0 || string.IsNullOrWhiteSpace(p.SearchText) || p.SearchText.Length > 16384) ||
                    passages.Select(p => p.PassageId).Distinct().Count() != passages.Count)
                    throw new BgeInferenceException("bge-reranker-input-invalid");
                if (Interlocked.CompareExchange(ref request._phase, 3, 2) != 2) throw new BgeInferenceException("bge-request-phase-invalid");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, request._ownership.CancellationToken);
                RerankResult result;
                try
                {
                    using var lease = await request._models.OpenRerankerAsync(request._ownership).ConfigureAwait(false);
                    request.RequireActive();
                    result = await lease.Model.RerankAsync(query, passages, linked.Token).ConfigureAwait(false);
                    if (result.ModelFingerprint != BgeOfflineModels.RerankerFingerprint || result.Scores.Count != passages.Count ||
                        !result.Scores.Select(score => score.PassageId).SequenceEqual(passages.Select(p => p.PassageId)) ||
                        result.Scores.Any(score => !float.IsFinite(score.Logit)))
                        throw new BgeInferenceException("bge-reranker-output-invalid");
                }
                finally
                {
                    // A caller may retain fused results after scoring refuses, but only
                    // when disposal proved that every native allocation is released.
                    if (request._ownership.NativeCapacityReleased) Interlocked.Exchange(ref request._phase, 4);
                }
                linked.Token.ThrowIfCancellationRequested();
                request.RequireActive();
                return result;
            }
        }
    }
}
