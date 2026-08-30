using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Knowledge;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.IntegrationV1;

public sealed class NativeV1FacadeTests
{
    [Fact]
    public async Task ExecuteQueryAsync_rejects_an_unknown_view_before_the_projection_reader_is_called()
    {
        var reader = new RecordingProjectionReader();
        var facade = CreateFacade(reader);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await facade.ExecuteQueryAsync("corpus", new NativeCorpusQuery("shell", null, null, null, 1, null), CancellationToken.None));

        Assert.Equal("view-not-allowed", exception.ReasonCode);
        Assert.False(reader.WasCalled);
    }

    [Fact]
    public async Task PreviewAsync_rejects_an_unknown_family_before_the_mutation_store_is_called()
    {
        var reader = new RecordingProjectionReader();
        var facade = CreateFacade(reader);
        using var document = JsonDocument.Parse("{}");

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await facade.PreviewAsync("shell", new NativeCorpusMutation("root_create", document.RootElement.Clone()), "test", CancellationToken.None));

        Assert.Equal("family-not-allowed", exception.ReasonCode);
        Assert.False(reader.WasCalled);
    }

    [Fact]
    public async Task ExecuteQueryAsync_recognises_knowledge_as_a_closed_family_before_validating_its_request()
    {
        var reader = new RecordingProjectionReader();
        var facade = CreateFacade(reader);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await facade.ExecuteQueryAsync("knowledge", new object(), CancellationToken.None));

        Assert.Equal("invalid-request", exception.ReasonCode);
        Assert.False(reader.WasCalled);
    }

    [Fact]
    public async Task ExecuteQueryAsync_rejects_an_unprotected_code_cursor_before_reading_a_projection()
    {
        var reader = new RecordingProjectionReader();
        var facade = CreateFacade(reader);

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await facade.ExecuteQueryAsync("code", new NativeCodeQuery("symbols", null, null, 1, "plain-text"), CancellationToken.None));

        Assert.Equal("cursor-invalid", exception.ReasonCode);
        Assert.False(reader.WasCalled);
    }

    [Fact]
    public async Task Code_cursor_is_opaque_and_bound_to_view_query_filter_limit_and_position_but_can_replay_the_same_page()
    {
        var reader = new RecordingProjectionReader();
        var codec = CursorCodec();
        var service = new NativeCodeQueryService(reader, codec);
        var branchId = Guid.NewGuid();
        var request = new NativeCodeQuery("matches", "needle", branchId, 2, null);
        var cursor = codec.Encode(
            NativeV1CursorBindings.Code(request),
            new NativeV1CursorPosition(branchId, Ordinal: 4));
        Assert.DoesNotContain("needle", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain(branchId.ToString("D"), cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Continuation", JsonSerializer.Serialize(request with
        {
            Continuation = new NativeV1CursorPosition(branchId, Ordinal: 4)
        }), StringComparison.Ordinal);

        await service.ExecuteAsync(request with { Cursor = cursor }, CancellationToken.None);
        await service.ExecuteAsync(request with { Cursor = cursor }, CancellationToken.None);
        Assert.Equal(2, reader.CallCount);

        var rejected = new[]
        {
            request with { View = "symbols", Query = null, Cursor = cursor },
            request with { Query = "other", Cursor = cursor },
            request with { BranchId = Guid.NewGuid(), Cursor = cursor },
            request with { Limit = 3, Cursor = cursor },
            request with { Cursor = cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A") }
        };
        foreach (var invalid in rejected)
        {
            var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
                await service.ExecuteAsync(invalid, CancellationToken.None));
            Assert.Equal("cursor-invalid", exception.ReasonCode);
        }
        Assert.Equal(2, reader.CallCount);
    }

    [Theory]
    [InlineData("roots")]
    [InlineData("assets")]
    [InlineData("branches")]
    [InlineData("processors")]
    [InlineData("jobs")]
    [InlineData("detail")]
    public async Task ExecuteQueryAsync_routes_each_closed_corpus_view(string view)
    {
        var reader = new RecordingProjectionReader();
        await CreateFacade(reader).ExecuteQueryAsync(
            "corpus",
            new NativeCorpusQuery(view, null, view == "detail" ? Guid.NewGuid() : null, null, 1, null),
            CancellationToken.None);
        Assert.True(reader.WasCalled);
    }

    [Theory]
    [InlineData("status", null)]
    [InlineData("symbols", null)]
    [InlineData("matches", "needle")]
    public async Task ExecuteQueryAsync_routes_each_closed_code_view(string view, string? query)
    {
        var reader = new RecordingProjectionReader();
        await CreateFacade(reader).ExecuteQueryAsync(
            "code",
            new NativeCodeQuery(view, query, null, 1, null),
            CancellationToken.None);
        Assert.True(reader.WasCalled);
    }

    [Theory]
    [InlineData("knowledge", 2047, true)]
    [InlineData("knowledge", 2048, true)]
    [InlineData("knowledge", 2049, false)]
    [InlineData("graph", 2047, true)]
    [InlineData("graph", 2048, true)]
    [InlineData("graph", 2049, false)]
    [InlineData("code", 2047, true)]
    [InlineData("code", 2048, true)]
    [InlineData("code", 2049, false)]
    public async Task Canonical_query_boundaries_are_identical_before_any_store_access(
        string family,
        int canonicalLength,
        bool accepted)
    {
        var reader = new RecordingProjectionReader();
        var knowledge = new RecordingKnowledgeQueryService();
        var facade = CreateFacade(reader, knowledge);
        var value = new string('x', canonicalLength);
        object request = family switch
        {
            "knowledge" => new NativeKnowledgeQuery(value, 1),
            "graph" => new NativeGraphQuery(value, 1, 1),
            _ => new NativeCodeQuery("matches", value, null, 1, null)
        };

        if (accepted)
        {
            _ = await facade.ExecuteQueryAsync(family, request, CancellationToken.None);
            Assert.Equal(1, family == "code" ? reader.CallCount : knowledge.CallCount);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<NativeOperationException>(() =>
                facade.ExecuteQueryAsync(family, request, CancellationToken.None).AsTask());
            Assert.Equal("invalid-query", failure.ReasonCode);
            Assert.Equal(0, reader.CallCount);
            Assert.Equal(0, knowledge.CallCount);
        }
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("sources")]
    [InlineData("jobs")]
    [InlineData("workers")]
    [InlineData("processors")]
    [InlineData("recovery")]
    public async Task ExecuteQueryAsync_routes_each_closed_operations_status_view(string view)
    {
        var reader = new RecordingProjectionReader();
        await CreateFacade(reader).ExecuteQueryAsync(
            "operations.status",
            new NativeOperationsStatus(view, null, null, 1),
            CancellationToken.None);
        Assert.True(reader.WasCalled);
    }

    [Fact]
    public async Task ExecuteQueryAsync_routes_the_closed_audit_view()
    {
        var reader = new RecordingProjectionReader();
        await CreateFacade(reader).ExecuteQueryAsync(
            "operations.audit",
            new NativeAuditQuery("events", null, null, 1, null),
            CancellationToken.None);
        Assert.True(reader.WasCalled);
    }

    [Theory]
    [InlineData("corpus")]
    [InlineData("audit")]
    public async Task ExecuteQueryAsync_rejects_other_unprotected_cursors_before_reading_a_projection(string family)
    {
        var reader = new RecordingProjectionReader();
        var facade = CreateFacade(reader);
        object query = family == "corpus"
            ? new NativeCorpusQuery("branches", null, null, null, 1, "plain-text")
            : new NativeAuditQuery("events", null, null, 1, "plain-text");

        var exception = await Assert.ThrowsAsync<NativeOperationException>(async () =>
            await facade.ExecuteQueryAsync(family == "audit" ? "operations.audit" : family, query, CancellationToken.None));

        Assert.Equal("cursor-invalid", exception.ReasonCode);
        Assert.False(reader.WasCalled);
    }

    private static NativeV1Facade CreateFacade(
        RecordingProjectionReader reader,
        RecordingKnowledgeQueryService? knowledgeQueries = null) => new(
        new NativeCorpusQueryService(reader, CursorCodec()),
        new NativeCorpusCommandService(new RecordingOperationStore(), reader),
        new NativeCodeQueryService(reader, CursorCodec()),
        new NativeCodeFeedbackService(new RecordingOperationStore(), new SafeDisclosure()),
        new NativeOperationsStatusService(reader),
        new NativeAuditQueryService(reader, CursorCodec()),
        knowledgeQueries ?? new RecordingKnowledgeQueryService(),
        new RecordingKnowledgeCommandService());

    private static INativeV1CursorCodec CursorCodec() =>
        new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());

    private sealed class RecordingKnowledgeQueryService : IKnowledgeQueryService
    {
        public int CallCount { get; private set; }
        public ValueTask<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<IReadOnlyList<KnowledgeSearchResult>>([]);
        }
        public ValueTask<IReadOnlyList<KnowledgeGraphResult>> GraphAsync(string node, int maxDepth, int maxResults, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<IReadOnlyList<KnowledgeGraphResult>>([]);
        }
    }

    private sealed class RecordingKnowledgeCommandService : IKnowledgeCommandService
    {
        public ValueTask<NativeActionPreview> PreviewAsync(KnowledgeMutation command, string surface, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("The knowledge command service must not be called.");
        public ValueTask<NativeActionReceipt> CommitAsync(KnowledgeMutation command, string confirmationId, string idempotencyKey, string surface, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("The knowledge command service must not be called.");
    }

    private sealed class RecordingProjectionReader : INativeV1ProjectionReader, INativeCorpusActionStore
    {
        public bool WasCalled { get; private set; }
        public int CallCount { get; private set; }
        public ValueTask<object> ReadCorpusAsync(NativeCorpusQuery query, CancellationToken cancellationToken) { WasCalled = true; CallCount++; return ValueTask.FromResult<object>(new { }); }
        public ValueTask<object> ReadCodeAsync(NativeCodeQuery query, CancellationToken cancellationToken) { WasCalled = true; CallCount++; return ValueTask.FromResult<object>(new { }); }
        public ValueTask<object> ReadStatusAsync(NativeOperationsStatus query, CancellationToken cancellationToken) { WasCalled = true; CallCount++; return ValueTask.FromResult<object>(new { }); }
        public ValueTask<object> ReadAuditAsync(NativeAuditQuery query, CancellationToken cancellationToken) { WasCalled = true; CallCount++; return ValueTask.FromResult<object>(new { }); }
        public ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action, string payload, CancellationToken cancellationToken) { WasCalled = true; return ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>([]); }
        public ValueTask<NativeActionCommitOperation> CreateCommitOperationAsync(string action, string payload, IReadOnlyList<NativeTargetVersion> targets, CancellationToken cancellationToken) { WasCalled = true; return ValueTask.FromResult<NativeActionCommitOperation>(new NativeCorpusMutationCommitOperation(action, payload)); }
        public ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveCorpusTargetsAsync(string action, string payload, CancellationToken cancellationToken) => ResolveTargetsAsync(action, payload, cancellationToken);
        public ValueTask<NativeActionCommitOperation> CreateCorpusCommitOperationAsync(string action, string payload, IReadOnlyList<NativeTargetVersion> targets, CancellationToken cancellationToken) => CreateCommitOperationAsync(action, payload, targets, cancellationToken);
        public ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveCodeFeedbackTargetsAsync(string action, string payload, CancellationToken cancellationToken) { WasCalled = true; return ValueTask.FromResult<IReadOnlyList<NativeTargetVersion>>([]); }
        public ValueTask<NativeActionCommitOperation> CreateCodeFeedbackCommitOperationAsync(string action, string payload, IReadOnlyList<NativeTargetVersion> targets, CancellationToken cancellationToken) { WasCalled = true; return ValueTask.FromResult<NativeActionCommitOperation>(new NativeCodeFeedbackCommitOperation(payload)); }
    }

    private sealed class RecordingOperationStore : INativeOperationStore
    {
        public ValueTask<NativeActionReceipt?> TryReplayAsync(string action, string canonicalPayload, string confirmationId, string idempotencyKey, string actorSurface, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("The operation store must not be called.");
        public ValueTask<NativeActionPreview> CreatePreviewAsync(NativeActionPreviewRequest request, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("The operation store must not be called.");
        public ValueTask<NativeActionReceipt> CommitAsync(NativeActionCommitRequest request, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("The operation store must not be called.");
    }

    private sealed class SafeDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }
}
