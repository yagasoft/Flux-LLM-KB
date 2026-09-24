namespace FluxKnowledge.Application.Contracts;

public sealed record CorpusResolvedScope(string Kind, IReadOnlyList<Guid> RootIds, string? CanonicalCwd);

public sealed record CorpusLocation(
    string Kind, int StartOffset, int Length,
    int? PageNumber = null, int? PageIndex = null, int? VisioPageId = null,
    string? Method = null, int? ShapeId = null,
    int? Left = null, int? Top = null, int? Width = null, int? Height = null,
    int? OrientationDegrees = null, int? SourceWidth = null, int? SourceHeight = null,
    string? SourceTransform = null,
    int? ParentShapeId = null, int? MasterId = null, string? MasterShapeId = null,
    int? ConnectorFromShapeId = null, int? ConnectorToShapeId = null,
    string? BeginArrow = null, string? EndArrow = null, bool? IsBackground = null);

public sealed record CorpusSearchRequest(
    string Query,
    int Limit,
    string Scope,
    Guid? RootId,
    string? Cwd);

public sealed record CorpusReadRequest(string EvidenceRef, int ContextCharacters);

public sealed record CorpusPassageResponse(
    string EvidenceRef,
    string SourceIdentity,
    Guid? RootId,
    Guid? OwnerSourceRevisionId,
    Guid PipelineRecordId,
    long PipelineRecordRevision,
    string Title,
    long ChunkId,
    string ChunkHash,
    int CitedStart,
    int CitedLength,
    int StartOffset,
    int Length,
    string Text,
    bool ContextBounded,
    IReadOnlyList<CorpusLocation> Locations,
    string? ExtractionMethod,
    IReadOnlyList<string> Warnings);

public sealed record CorpusSearchHit(
    string EvidenceRef,
    string SourceIdentity,
    Guid? RootId,
    Guid? OwnerSourceRevisionId,
    Guid PipelineRecordId,
    long PipelineRecordRevision,
    string Title,
    long ChunkId,
    string ChunkHash,
    int StartOffset,
    int Length,
    string Passage,
    IReadOnlyList<CorpusLocation> Locations,
    string? ExtractionMethod,
    IReadOnlyList<string> Explanation);

public sealed record CorpusSearchResponse(
    IReadOnlyList<CorpusSearchHit> Results,
    CorpusResolvedScope ResolvedScope,
    string RetrievalMode,
    string SemanticStatus,
    Guid? IndexGeneration,
    IReadOnlyList<string> Warnings);
