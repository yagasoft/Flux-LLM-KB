using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class FluxKnowledgeDbContext(DbContextOptions<FluxKnowledgeDbContext> options)
    : DbContext(options)
{
    public DbSet<SourceIdentityEntity> SourceIdentities => Set<SourceIdentityEntity>();
    public DbSet<PipelineRecordEntity> PipelineRecords => Set<PipelineRecordEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<JobAttemptEntity> JobAttempts => Set<JobAttemptEntity>();
    public DbSet<OutboxMessageEntity> OutboxMessages => Set<OutboxMessageEntity>();
    public DbSet<ArtifactEntity> Artifacts => Set<ArtifactEntity>();
    public DbSet<TextChunkEntity> TextChunks => Set<TextChunkEntity>();
    public DbSet<VectorEntity> Vectors => Set<VectorEntity>();
    public DbSet<IndexGenerationEntity> IndexGenerations => Set<IndexGenerationEntity>();
    public DbSet<IndexGenerationVectorEntity> IndexGenerationVectors => Set<IndexGenerationVectorEntity>();
    public DbSet<IndexStateEntity> IndexState => Set<IndexStateEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<GpuMiniTaskEntity> GpuMiniTasks => Set<GpuMiniTaskEntity>();
    public DbSet<GpuBatchEntity> GpuBatches => Set<GpuBatchEntity>();
    public DbSet<GpuCapacitySlotEntity> GpuCapacitySlots => Set<GpuCapacitySlotEntity>();
    public DbSet<GpuSchedulerStateEntity> GpuSchedulerStates => Set<GpuSchedulerStateEntity>();
    public DbSet<GpuSchedulerOperationReceiptEntity> GpuSchedulerOperationReceipts => Set<GpuSchedulerOperationReceiptEntity>();
    public DbSet<GpuExecutorDispatchEntity> GpuExecutorDispatches => Set<GpuExecutorDispatchEntity>();
    public DbSet<GpuExecutorResultReceiptEntity> GpuExecutorResultReceipts => Set<GpuExecutorResultReceiptEntity>();
    public DbSet<GpuExecutorEvidenceEntity> GpuExecutorEvidence => Set<GpuExecutorEvidenceEntity>();
    public DbSet<NativeWorkerInstanceEntity> NativeWorkerInstances => Set<NativeWorkerInstanceEntity>();
    public DbSet<NativeWorkerLifecycleEvidenceEntity> NativeWorkerLifecycleEvidence => Set<NativeWorkerLifecycleEvidenceEntity>();
    public DbSet<SourceRootConfigurationEntity> SourceRootConfigurations => Set<SourceRootConfigurationEntity>();
    public DbSet<SourceRootWatchStateEntity> SourceRootWatchStates => Set<SourceRootWatchStateEntity>();
    public DbSet<SourceScanRequestEntity> SourceScanRequests => Set<SourceScanRequestEntity>();
    public DbSet<SourceScanJobEntity> SourceScanJobs => Set<SourceScanJobEntity>();
    public DbSet<SourceScanOutboxEntity> SourceScanOutbox => Set<SourceScanOutboxEntity>();
    public DbSet<SourceRevisionEntity> SourceRevisions => Set<SourceRevisionEntity>();
    public DbSet<SourceArtifactEntity> SourceArtifacts => Set<SourceArtifactEntity>();
    public DbSet<SourceActivityEntity> SourceActivities => Set<SourceActivityEntity>();
    public DbSet<SourceCapabilityEntity> SourceCapabilities => Set<SourceCapabilityEntity>();
    public DbSet<OutlookCaptureProfileEntity> OutlookCaptureProfiles => Set<OutlookCaptureProfileEntity>();
    public DbSet<OutlookCaptureFolderEntity> OutlookCaptureFolders => Set<OutlookCaptureFolderEntity>();
    public DbSet<OutlookCaptureOperationEntity> OutlookCaptureOperations => Set<OutlookCaptureOperationEntity>();
    public DbSet<OutlookCaptureExportEntity> OutlookCaptureExports => Set<OutlookCaptureExportEntity>();
    public DbSet<OutlookBrowseRequestEntity> OutlookBrowseRequests => Set<OutlookBrowseRequestEntity>();
    public DbSet<OutlookBrowseResultEntity> OutlookBrowseResults => Set<OutlookBrowseResultEntity>();
    public DbSet<OutlookCatchUpEntity> OutlookCatchUps => Set<OutlookCatchUpEntity>();
    public DbSet<DeferredCapabilityEntity> DeferredCapabilities => Set<DeferredCapabilityEntity>();
    public DbSet<SourceProcessorBranchEntity> SourceProcessorBranches => Set<SourceProcessorBranchEntity>();
    public DbSet<SourceProcessorAttemptEntity> SourceProcessorAttempts => Set<SourceProcessorAttemptEntity>();
    public DbSet<SourceProcessorForceRequestEntity> SourceProcessorForceRequests => Set<SourceProcessorForceRequestEntity>();
    public DbSet<OperatorActionHardDenialEntity> OperatorActionHardDenials => Set<OperatorActionHardDenialEntity>();
    public DbSet<OperatorActionCapabilityPolicyEntity> OperatorActionCapabilityPolicies => Set<OperatorActionCapabilityPolicyEntity>();
    public DbSet<OperatorActionActionLedgerEntity> OperatorActionActionLedger => Set<OperatorActionActionLedgerEntity>();
    public DbSet<OperatorActionOperationLedgerEntity> OperatorActionOperationLedger => Set<OperatorActionOperationLedgerEntity>();
    public DbSet<SourceProcessorActionIgnoreHeadEntity> SourceProcessorActionIgnoreHeads => Set<SourceProcessorActionIgnoreHeadEntity>();
    public DbSet<SourceProcessorBranchMemberEntity> SourceProcessorBranchMembers => Set<SourceProcessorBranchMemberEntity>();
    public DbSet<SourceActivityRelationEntity> SourceActivityRelations => Set<SourceActivityRelationEntity>();
    public DbSet<SourceProcessorCodeDocumentEntity> SourceProcessorCodeDocuments => Set<SourceProcessorCodeDocumentEntity>();
    public DbSet<SourceProcessorCodeSymbolEntity> SourceProcessorCodeSymbols => Set<SourceProcessorCodeSymbolEntity>();
    public DbSet<SourceProcessorCodeReferenceEntity> SourceProcessorCodeReferences => Set<SourceProcessorCodeReferenceEntity>();
    public DbSet<SourceProcessorCodeDiagnosticEntity> SourceProcessorCodeDiagnostics => Set<SourceProcessorCodeDiagnosticEntity>();
    public DbSet<SourceProcessorCodeCompletionReceiptEntity> SourceProcessorCodeCompletionReceipts => Set<SourceProcessorCodeCompletionReceiptEntity>();
    public DbSet<SourceProcessorCodeBlockedDiagnosticEntity> SourceProcessorCodeBlockedDiagnostics => Set<SourceProcessorCodeBlockedDiagnosticEntity>();
    public DbSet<NativeOperationIntentEntity> NativeOperationIntents => Set<NativeOperationIntentEntity>();
    public DbSet<NativeOperationReceiptEntity> NativeOperationReceipts => Set<NativeOperationReceiptEntity>();
    public DbSet<NativeOperationFenceTargetEntity> NativeOperationFenceTargets => Set<NativeOperationFenceTargetEntity>();
    public DbSet<KnowledgeItemEntity> KnowledgeItems => Set<KnowledgeItemEntity>();
    public DbSet<KnowledgeClaimEntity> KnowledgeClaims => Set<KnowledgeClaimEntity>();
    public DbSet<KnowledgeClaimHistoryEntity> KnowledgeClaimHistory => Set<KnowledgeClaimHistoryEntity>();
    public DbSet<KnowledgeRelationEntity> KnowledgeRelations => Set<KnowledgeRelationEntity>();
    public DbSet<KnowledgeTombstoneEntity> KnowledgeTombstones => Set<KnowledgeTombstoneEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasSequence<long>("GpuMiniTaskCreatedSequence");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FluxKnowledgeDbContext).Assembly);
    }
}
