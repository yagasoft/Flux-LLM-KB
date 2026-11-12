using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Cloud.Unum.USearch;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Usearch;

/// <summary>Owned read-only native view; callers retain the guarded file and serialise use/disposal.</summary>
public sealed class UsearchReadIndex : IDisposable
{
    // The normal cache must use the width qualified on the full published corpus.
    public const int DefaultSearchExpansion = 512;
    public const int MaximumSearchExpansion = 8192;
    private const int MaximumExactSelection = 1024;
    private IntPtr _index;
    private readonly int _dimensions;
    private readonly nuint _searchExpansion;
    private bool _opened;

    public UsearchReadIndex(int dimensions, int searchExpansion = DefaultSearchExpansion)
    {
        if (dimensions <= 0) throw new ArgumentException("segment-native-profile-invalid");
        if (searchExpansion is < 1 or > MaximumSearchExpansion) throw new ArgumentOutOfRangeException(nameof(searchExpansion));
        _dimensions = dimensions;
        _searchExpansion = (nuint)searchExpansion;
        var options = new IndexOptions();
        _index = Native.Init(ref options, out var error);
        Check(error);
        if (_index == IntPtr.Zero) throw new IndexGenerationValidationException("segment-native-open-failed");
    }

    public UsearchReadIndex(string path, int dimensions, long count, int searchExpansion = DefaultSearchExpansion) : this(dimensions, searchExpansion)
    {
        try { OpenView(path, count); }
        catch { Dispose(); throw; }
    }

    public void OpenView(string path, long count)
    {
        ObjectDisposedException.ThrowIf(_index == IntPtr.Zero, this);
        if (_opened || count < 0) throw new InvalidOperationException("segment-native-already-open-or-invalid");
        Native.View(_index, path, out var error);
        Check(error);
        var actualDimensions = Native.Dimensions(_index, out error);
        Check(error);
        var actualCount = Native.Size(_index, out error);
        Check(error);
        if (actualDimensions != (nuint)_dimensions || actualCount != (nuint)count)
            throw new IndexGenerationValidationException("segment-native-profile-invalid");
        Native.Expansion(_index, _searchExpansion, out error);
        Check(error);
        _opened = true;
    }

    internal IReadOnlyList<AnnMatch> SearchCandidatePool(IReadOnlyList<float> query, int limit,
        IReadOnlySet<long>? eligible, CancellationToken cancellationToken) =>
        SearchCore(query, limit, IAnnCandidatePool.MaximumResults, eligible, cancellationToken);

    public IReadOnlyList<AnnMatch> Search(IReadOnlyList<float> query, int limit,
        IReadOnlySet<long>? eligible, CancellationToken cancellationToken) => SearchCore(query, limit, 100, eligible, cancellationToken);

    private IReadOnlyList<AnnMatch> SearchCore(IReadOnlyList<float> query, int limit, int maximum,
        IReadOnlySet<long>? eligible, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_index == IntPtr.Zero, this);
        if (!_opened) throw new InvalidOperationException("segment-native-not-open");
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Count != _dimensions || query.Any(value => !float.IsFinite(value)) || !query.Any(value => value != 0))
            throw new ArgumentException("corpus-query-vector-invalid");
        if (limit < 1 || limit > maximum) throw new ArgumentOutOfRangeException(nameof(limit));
        if (eligible?.Count == 0) return [];
        var values = query.ToArray();
        // Sparse filters can make graph traversal explore mostly rejected neighbours.
        // Read only this bounded selected set from the owned native view, never from SQL.
        if (eligible is { Count: <= MaximumExactSelection })
            return SearchSelected(values, limit, eligible, cancellationToken);
        var keys = new ulong[limit];
        var distances = new float[limit];
        ExceptionDispatchInfo? failure = null;
        Native.Predicate? predicate = eligible is null ? null : (key, _) =>
        {
            // Never unwind a managed exception through a native graph traversal.
            try { return !cancellationToken.IsCancellationRequested && key <= long.MaxValue && eligible.Contains((long)key) ? 1 : 0; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { failure ??= ExceptionDispatchInfo.Capture(exception); return 0; }
        };
        nuint found;
        IntPtr error;
        if (predicate is null) found = Native.Search(_index, values, ScalarKind.Float32, (nuint)limit, keys, distances, out error);
        else found = Native.FilteredSearch(_index, values, ScalarKind.Float32, (nuint)limit,
            predicate, IntPtr.Zero, keys, distances, out error);
        GC.KeepAlive(predicate);
        GC.KeepAlive(eligible);
        failure?.Throw();
        Check(error);
        cancellationToken.ThrowIfCancellationRequested();
        if (found > (nuint)limit) throw new IndexGenerationValidationException("segment-ann-output-invalid");
        var matches = new AnnMatch[(int)found];
        for (var index = 0; index < matches.Length; index++)
        {
            if (keys[index] is 0 or > long.MaxValue || !float.IsFinite(distances[index]) ||
                eligible is not null && !eligible.Contains((long)keys[index]))
                throw new IndexGenerationValidationException("segment-ann-output-invalid");
            matches[index] = new((long)keys[index], distances[index]);
        }
        if (matches.Select(value => value.VectorId).Distinct().Count() != matches.Length)
            throw new IndexGenerationValidationException("segment-ann-output-invalid");
        return matches;
    }

    private IReadOnlyList<AnnMatch> SearchSelected(float[] query, int limit, IReadOnlySet<long> eligible,
        CancellationToken cancellationToken)
    {
        var expected = eligible.Count;
        if (expected is < 1 or > MaximumExactSelection)
            throw new IndexGenerationValidationException("segment-scope-membership-invalid");
        var seen = new HashSet<long>();
        var vector = new float[_dimensions];
        var top = new PriorityQueue<AnnMatch, (float Distance, long Key)>(limit);
        foreach (var key in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key <= 0 || seen.Count >= expected || seen.Count >= MaximumExactSelection || !seen.Add(key))
                throw new IndexGenerationValidationException("segment-scope-membership-invalid");
            var found = Native.Get(_index, (ulong)key, 1, vector, ScalarKind.Float32, out var error);
            Check(error);
            if (found != 1) throw new IndexGenerationValidationException("segment-native-vector-missing");
            var nonzero = false;
            foreach (var value in vector)
            {
                if (!float.IsFinite(value)) throw new IndexGenerationValidationException("segment-native-vector-invalid");
                nonzero |= value != 0;
            }
            if (!nonzero) throw new IndexGenerationValidationException("segment-native-vector-invalid");
            cancellationToken.ThrowIfCancellationRequested();
            var distance = Native.Distance(query, vector, ScalarKind.Float32, (nuint)_dimensions, MetricKind.Cos, out error);
            Check(error);
            if (!float.IsFinite(distance)) throw new IndexGenerationValidationException("segment-ann-output-invalid");
            var match = new AnnMatch(key, distance);
            var priority = (-distance, -key); // The worst retained distance/ID is the minimum heap entry.
            if (top.Count < limit) top.Enqueue(match, priority);
            else top.EnqueueDequeue(match, priority);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (seen.Count != expected) throw new IndexGenerationValidationException("segment-scope-membership-invalid");
        return top.UnorderedItems.Select(item => item.Element).OrderBy(match => match.Distance).ThenBy(match => match.VectorId).ToArray();
    }

    public void Dispose()
    {
        if (_index == IntPtr.Zero) return;
        Native.Free(_index, out var error);
        Check(error); // An uncertain free must retain the cache's guard and durable owner.
        _index = IntPtr.Zero;
    }

    private static void Check(IntPtr error)
    {
        if (error != IntPtr.Zero) throw new IndexGenerationValidationException("segment-native-operation-failed");
    }

    // Pinned ABI: unum-cloud/usearch v2.19.3 c/usearch.h. The package already supplies the exact native runtime.
    private static class Native
    {
        private const string Library = "libusearch_c";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Predicate(ulong key, IntPtr state);
        [DllImport(Library, EntryPoint = "usearch_init", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr Init(ref IndexOptions options, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_view", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void View(IntPtr index, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_size", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint Size(IntPtr index, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_dimensions", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint Dimensions(IntPtr index, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_change_expansion_search", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Expansion(IntPtr index, nuint expansion, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_search", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint Search(IntPtr index, [In] float[] vector, ScalarKind kind, nuint count,
            [Out] ulong[] keys, [Out] float[] distances, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_filtered_search", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint FilteredSearch(IntPtr index, [In] float[] vector, ScalarKind kind, nuint count,
            Predicate predicate, IntPtr state, [Out] ulong[] keys, [Out] float[] distances, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_get", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nuint Get(IntPtr index, ulong key, nuint count, [Out] float[] vector, ScalarKind kind, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_distance", CallingConvention = CallingConvention.Cdecl)]
        internal static extern float Distance([In] float[] left, [In] float[] right, ScalarKind kind, nuint dimensions,
            MetricKind metric, out IntPtr error);
        [DllImport(Library, EntryPoint = "usearch_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(IntPtr index, out IntPtr error);
    }
}
