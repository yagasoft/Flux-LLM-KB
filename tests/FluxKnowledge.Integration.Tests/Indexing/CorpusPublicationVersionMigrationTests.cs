using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class CorpusPublicationVersionMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Downgrade_refuses_loss_of_version_fences_and_empty_round_trip_preserves_epoch()
    {
        await using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).Options);
        var state = await context.IndexState.SingleAsync();
        var epoch = state.CorpusEpoch;
        state.CorpusVersion = 1;
        await context.SaveChangesAsync();
        var migrations = context.Database.GetMigrations().ToArray();
        var index = Array.FindIndex(migrations, migration => migration.EndsWith("_AddCorpusPublicationVersions", StringComparison.Ordinal));
        Assert.True(index > 0);
        var migrator = context.GetService<IMigrator>();
        var failure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1]));
        Assert.Contains("corpus-publication-version-downgrade-requires-empty-reset", failure.Message);
        Assert.Contains(migrations[index], await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await context.IndexState.Select(value => value.CorpusVersion).SingleAsync());
        // A newer reversible migration may have completed before this older guard
        // refuses. Restore current columns before using the current EF model.
        await context.Database.MigrateAsync();
        await context.Entry(state).ReloadAsync();
        state.CorpusVersion = 0;
        await context.SaveChangesAsync();
        var stampedGeneration = new IndexGenerationEntity
        {
            Id = Guid.NewGuid(), CorpusEpoch = epoch, CorpusVersion = 0, ModelFingerprint = "migration-test", Dimensions = 1,
            MetadataChecksum = new string('a', 64), CreatedAtUtc = DateTimeOffset.UtcNow
        };
        context.IndexGenerations.Add(stampedGeneration);
        await context.SaveChangesAsync();
        var generationFailure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[index - 1]));
        Assert.Contains("corpus-publication-version-downgrade-requires-empty-reset", generationFailure.Message);
        await context.Database.MigrateAsync();
        Assert.Equal(epoch, (await context.IndexGenerations.AsNoTracking().SingleAsync()).CorpusEpoch);
        context.IndexGenerations.Remove(stampedGeneration);
        await context.SaveChangesAsync();
        await migrator.MigrateAsync(migrations[index - 1]);
        await context.Database.MigrateAsync();
        var restored = await context.IndexState.AsNoTracking().SingleAsync();
        Assert.Equal(epoch, restored.CorpusEpoch);
        Assert.Equal(0, restored.CorpusVersion);
    }
}
