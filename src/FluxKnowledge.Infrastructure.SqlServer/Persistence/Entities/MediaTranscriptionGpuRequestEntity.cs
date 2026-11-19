namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;

public sealed class MediaTranscriptionGpuRequestEntity
{
    public Guid MiniTaskId { get; set; }
    public Guid ParentJobId { get; set; }
    public Guid PipelineRecordId { get; set; }
    public long SourceRevision { get; set; }
    public Guid RetainedSourceRevisionId { get; set; }
    public string ContentSha256 { get; set; } = "";
    public string ModelRuntimeKey { get; set; } = "";
    public string SettingsFingerprint { get; set; } = "";
    public int ExecutionPhase { get; set; }
    public Guid? ExecutionInstanceId { get; set; }
    public Guid? ExecutionBatchId { get; set; }
    public Guid? ExecutionDispatchId { get; set; }
    public long? ExecutionAdmissionGeneration { get; set; }
    public Guid? SupervisorInstanceId { get; set; }
    public int? SupervisorProcessId { get; set; }
    public DateTimeOffset? SupervisorProcessStartedAtUtc { get; set; }
    public string? MachineFingerprint { get; set; }
    public DateTimeOffset? ClaimedAtUtc { get; set; }
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    public int? NativeProcessId { get; set; }
    public DateTimeOffset? NativeProcessStartedAtUtc { get; set; }
    public DateTimeOffset? CleanupConfirmedAtUtc { get; set; }
    public int? CleanupKind { get; set; }
    public string? ResultJson { get; set; }
    public byte[]? ResultDigest { get; set; }
    public DateTimeOffset? RequeuedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
