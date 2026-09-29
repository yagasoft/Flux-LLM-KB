using System.Runtime.InteropServices;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>Loads two bounded CPU lanes from the already verified, offline J: model store.</summary>
public static class BgeCpuResidentModelFactory
{
    private const int LaneCount = 2;
    private const int RerankerShards = 4;
    private const int EmbeddingThreads = 4;
    private const int RerankerThreads = 4;
    private const long Gib = 1024L * 1024 * 1024;

    public static async Task<CpuResidentPassageLoad> LoadAsync(BgeGpuModelStores stores,
        Func<CancellationToken, Task<IDisposable>> acquireOwner, CancellationToken cancellationToken)
    {
        // No model allocation starts until this process exclusively owns the machine-wide pool.
        var owner = await acquireOwner(cancellationToken).ConfigureAwait(false);
        var lanes = new List<CpuPassageInferenceLane>(LaneCount);
        try
        {
            for (var index = 0; index < LaneCount; index++)
            {
                if (FreePhysicalBytes() is not long free || free < (index == 0 ? 32 : 24) * Gib)
                    throw new BgeInferenceException("cpu-search-memory-unavailable");
                lanes.Add(await OpenLaneAsync(stores, cancellationToken).ConfigureAwait(false));
            }
            if (FreePhysicalBytes() is not long remaining || remaining < 16 * Gib)
                throw new BgeInferenceException("cpu-search-memory-unavailable");
            return new CpuResidentPassageLoad(lanes, owner);
        }
        catch (Exception failure)
        {
            if (failure is CpuNativeReleaseUnconfirmedException)
            {
                CpuResidentPassageLoad.RetainUncertainOwner(owner);
                foreach (var lane in lanes)
                {
                    try { lane.Dispose(); }
                    catch { /* The owner remains pinned until process exit. */ }
                }
                throw;
            }
            try { new CpuResidentPassageLoad(lanes, owner).Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
    }

    private static async Task<CpuPassageInferenceLane> OpenLaneAsync(BgeGpuModelStores stores, CancellationToken ct)
    {
        IBgeTokenizer? embeddingTokenizer = null, rerankerTokenizer = null;
        BgeEmbeddingProvider? embedding = null;
        var rerankers = new List<BgeReranker>(RerankerShards);
        try
        {
            embeddingTokenizer = await BgeOfflineModels.CreateTokenizerAsync(stores.Embedding, stores.TokenizerRuntime,
                reranker: false, ct).ConfigureAwait(false);
            rerankerTokenizer = await BgeOfflineModels.CreateTokenizerAsync(stores.RerankerTokenizer, stores.TokenizerRuntime,
                reranker: true, ct).ConfigureAwait(false);
            embedding = await BgeOfflineModels.OpenCpuEmbeddingAsync(stores.Embedding, embeddingTokenizer, ct,
                EmbeddingThreads).ConfigureAwait(false);
            for (var shard = 0; shard < RerankerShards; shard++)
                rerankers.Add(await BgeOfflineModels.OpenCpuRerankerAsync(stores.Reranker, rerankerTokenizer, ct,
                    RerankerThreads).ConfigureAwait(false));
            var lane = new CpuPassageInferenceLane(embedding,
                new ParallelCpuPassageReranker(rerankers, BgeOfflineModels.RerankerFingerprint),
                () => DisposeModels(embedding, rerankers, embeddingTokenizer, rerankerTokenizer));
            return lane;
        }
        catch (Exception failure)
        {
            try { DisposeModels(embedding, rerankers, embeddingTokenizer, rerankerTokenizer); }
            catch (Exception cleanup) { throw new CpuNativeReleaseUnconfirmedException(failure, cleanup); }
            throw;
        }
    }

    private static void DisposeModels(BgeEmbeddingProvider? embedding, IReadOnlyList<BgeReranker> rerankers,
        IBgeTokenizer? embeddingTokenizer, IBgeTokenizer? rerankerTokenizer)
    {
        var errors = new List<Exception>();
        foreach (var reranker in rerankers)
        {
            try { reranker.Dispose(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        try { embedding?.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
        try { rerankerTokenizer?.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
        try { embeddingTokenizer?.Dispose(); }
        catch (Exception exception) { errors.Add(exception); }
        if (errors.Count > 0) throw new AggregateException("cpu-search-native-release-unconfirmed", errors);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static long? FreePhysicalBytes()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.AvailablePhysical <= long.MaxValue
            ? (long)status.AvailablePhysical : null;
    }

    private sealed class CpuNativeReleaseUnconfirmedException(Exception failure, Exception cleanup)
        : AggregateException("cpu-search-native-release-unconfirmed", failure, cleanup);
}
