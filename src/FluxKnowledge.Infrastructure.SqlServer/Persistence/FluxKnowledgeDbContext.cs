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
    public DbSet<IndexStateEntity> IndexState => Set<IndexStateEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<GpuMiniTaskEntity> GpuMiniTasks => Set<GpuMiniTaskEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FluxKnowledgeDbContext).Assembly);
    }
}
