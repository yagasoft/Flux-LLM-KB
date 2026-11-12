using FluxKnowledge.Application.Ports;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class CorpusDisclosureDefaultTests
{
    [Fact]
    public async Task Default_methods_preserve_separate_limits_and_cancellation_before_individual_reads()
    {
        var source = new DefaultReader(); ICorpusRetrievalReader reader = source;
        var prototype = new CorpusEvidenceBinding(2, null, null, new string('a', 64), Guid.NewGuid(), 1,
            Guid.NewGuid(), new string('b', 64), 1, new string('c', 64), 0, 4, Guid.NewGuid());
        var bindings = Enumerable.Range(1, 251).Select(id => prototype with { ChunkId = id }).ToArray();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadForDisclosureAsync(bindings.Take(201).ToArray(), default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadExpandedForDisclosureAsync(bindings, default).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadExpandedForDisclosureAsync([prototype, prototype], default).AsTask());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadExpandedForDisclosureAsync([prototype], cancelled.Token).AsTask());
        Assert.Empty(source.ReadIds);
        Assert.Empty(await reader.ReadForDisclosureAsync(bindings.Take(200).ToArray(), default));
        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), source.ReadIds);
        source.ReadIds.Clear();
        Assert.Empty(await reader.ReadExpandedForDisclosureAsync(bindings.Take(250).ToArray(), default));
        Assert.Equal(Enumerable.Range(1, 250).Select(i => (long)i), source.ReadIds);
    }

    private sealed class DefaultReader : ICorpusRetrievalReader
    {
        public List<long> ReadIds { get; } = [];
        public ValueTask<EligibleContext?> ReadAsync(CorpusEvidenceBinding binding, int contextCharacters, CancellationToken ct)
        { ReadIds.Add(binding.ChunkId); return ValueTask.FromResult<EligibleContext?>(null); }
        public ValueTask<CorpusLexicalReadiness> GetLexicalReadinessAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> GetLexicalTermsAsync(string query, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<ResolvedCorpusScope?> ResolveScopeAsync(string kind, Guid? rootId, string? cwd, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<EligiblePassageCandidate>> SearchAsync(string query, ResolvedCorpusScope scope, int limit, CancellationToken ct) => throw new NotSupportedException();
    }
}
