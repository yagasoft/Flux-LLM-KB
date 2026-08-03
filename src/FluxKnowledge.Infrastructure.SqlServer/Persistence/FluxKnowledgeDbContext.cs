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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasSequence<long>("GpuMiniTaskCreatedSequence");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FluxKnowledgeDbContext).Assembly);
    }
}
