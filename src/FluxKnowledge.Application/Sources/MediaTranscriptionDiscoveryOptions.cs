namespace FluxKnowledge.Application.Sources;

/// <summary>Explicitly enables native media discovery for the named source roots.</summary>
public sealed record MediaTranscriptionDiscoveryOptions(
    IReadOnlyList<Guid> SourceRootIds,
    DateTimeOffset AfterUtc,
    IReadOnlyList<Guid> CatchUpRevisionIds)
{
    public const string Operation = "transcribe media";
    public const int MaximumBatchSize = 16;
    public const long MaximumMediaBytes = 1024L * 1024 * 1024;
    public const string InputTooLargeReason = "media-transcription-input-too-large";
    public const string ProcessorVersion = "native-media-transcription-v1";
    public const string DescriptorFingerprint = "ad50a61b6c6d19e2a7368d42e239ce142f05d37774004ea3a978e01e8bff23ca";
    public const string Capability = "native-media-transcription";

    public static MediaTranscriptionDiscoveryOptions Disabled { get; } = new([], default, []);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(SourceRootIds);
        ArgumentNullException.ThrowIfNull(CatchUpRevisionIds);
        if (SourceRootIds.Any(id => id == Guid.Empty) || SourceRootIds.Distinct().Count() != SourceRootIds.Count ||
            CatchUpRevisionIds.Count > MaximumBatchSize || CatchUpRevisionIds.Any(id => id == Guid.Empty) ||
            CatchUpRevisionIds.Distinct().Count() != CatchUpRevisionIds.Count)
            throw new ArgumentException("Media transcription discovery requires distinct root IDs and up to 16 exact revision IDs.");
        if (SourceRootIds.Count == 0 && CatchUpRevisionIds.Count > 0)
            throw new ArgumentException("Media transcription catch-up requires explicitly approved source roots.");
        if (SourceRootIds.Count > 0 && (AfterUtc == default || AfterUtc.Offset != TimeSpan.Zero))
            throw new ArgumentException("Media transcription discovery requires an explicit UTC start time.");
    }
}

public sealed record MediaTranscriptionDiscoveryRun(int Examined, int Queued, int Held, int Existing);
