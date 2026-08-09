using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class SourceRestartRecoveryTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Restart_does_not_reclaim_a_running_scan_before_its_existing_lease_expires()
    {
        var now = DateTimeOffset.Parse("2026-08-08T00:00:00+00:00");
        var rootId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using (var setup = CreateContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\restart\\{rootId:N}", DisplayName = "Restart", State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 16 * 1024 * 1024, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity { Id = requestId, SourceRootId = rootId, RequestKind = 0, RequestedBy = "test", RequestedAtUtc = now, IsReleased = true, ReleasedAtUtc = now, State = (int)SourceScanRequestState.Running });
            setup.SourceScanJobs.Add(new SourceScanJobEntity { Id = jobId, SourceScanRequestId = requestId, State = (int)SourceScanJobState.Running, DueAtUtc = now, LeaseOwner = "prior-process", LeaseExpiresAtUtc = now.AddMinutes(10), LeaseGeneration = 7, AttemptCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }

        var store = new SqlSourceScanStore(new ContextFactory(_fixture.ConnectionString), new FixedTimeProvider(now));
        var claim = await store.ClaimNextReleasedAsync("restarted-process", now, TimeSpan.FromMinutes(15), CancellationToken.None);

        Assert.Null(claim);
        await using var verification = CreateContext();
        var job = await verification.SourceScanJobs.SingleAsync(value => value.Id == jobId);
        Assert.Equal((int)SourceScanJobState.Running, job.State);
        Assert.Equal("prior-process", job.LeaseOwner);
        Assert.Equal(7, job.LeaseGeneration);
        Assert.Equal(now.AddMinutes(10), job.LeaseExpiresAtUtc);
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
