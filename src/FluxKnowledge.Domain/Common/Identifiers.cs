namespace FluxKnowledge.Domain.Common;

public sealed record PipelineRecordId(Guid Value)
{
    public static PipelineRecordId New() => new(Guid.NewGuid());
}

public sealed record SourceIdentityId(Guid Value)
{
    public static SourceIdentityId New() => new(Guid.NewGuid());
}

public sealed record JobId(Guid Value)
{
    public static JobId New() => new(Guid.NewGuid());
}

public sealed record DispatchMessageId(Guid Value)
{
    public static DispatchMessageId New() => new(Guid.NewGuid());
}

public sealed record IndexGenerationId(Guid Value)
{
    public static IndexGenerationId New() => new(Guid.NewGuid());
}
