using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class RepositoryRecoveryAuthorityMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [Fact]
    public void Original_recovery_binding_is_immutable_and_discovery_authority_is_replaceable()
    {
        using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Integrated Security=true;Encrypt=true;TrustServerCertificate=true").Options);
        Assert.Equal(PropertySaveBehavior.Throw, context.Model.FindEntityType(typeof(PipelineRecordEntity))!
            .FindProperty(nameof(PipelineRecordEntity.RepositoryRecoveryBindingJson))!.GetAfterSaveBehavior());
        Assert.Equal(PropertySaveBehavior.Save, context.Model.FindEntityType(typeof(SourceRevisionEntity))!
            .FindProperty(nameof(SourceRevisionEntity.CurrentDiscoveryEvidenceJson))!.GetAfterSaveBehavior());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downgrade_refuses_loss_of_recovery_evidence_and_upgrade_does_not_backfill_legacy_work(bool discovery)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "Migration baseline.");
        await environment.AddRetainedAndPumpAsync("Retained migration baseline.");
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.True(await context.PipelineRecords.AllAsync(value => value.RepositoryRecoveryBindingJson == null));
        Assert.True(await context.SourceRevisions.AllAsync(value => value.CurrentDiscoveryEvidenceJson == null));
        const string evidence = "{\"Version\":1}";
        if (discovery)
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE SourceRevisions SET CurrentDiscoveryEvidenceJson={evidence}");
        else
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE PipelineRecords SET RepositoryRecoveryBindingJson={evidence}");
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, migration => migration.EndsWith("_AddRepositoryRecoveryAuthority", StringComparison.Ordinal));
        Assert.True(index > 0);
        var migrator = context.GetService<IMigrator>();
        try
        {
            var refusal = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1]));
            Assert.Contains("repository-recovery-authority-downgrade-requires-empty-evidence", refusal.Message);
            Assert.Contains(migrations[index], await context.Database.GetAppliedMigrationsAsync());
            await context.Database.ExecuteSqlRawAsync("UPDATE SourceRevisions SET CurrentDiscoveryEvidenceJson=NULL; UPDATE PipelineRecords SET RepositoryRecoveryBindingJson=NULL;");
            await migrator.MigrateAsync(migrations[index - 1]);
            await migrator.MigrateAsync(migrations[index]);
            Assert.True(await context.PipelineRecords.AllAsync(value => value.RepositoryRecoveryBindingJson == null));
            Assert.True(await context.SourceRevisions.AllAsync(value => value.CurrentDiscoveryEvidenceJson == null));
        }
        finally { await context.Database.MigrateAsync(); }
    }
}
