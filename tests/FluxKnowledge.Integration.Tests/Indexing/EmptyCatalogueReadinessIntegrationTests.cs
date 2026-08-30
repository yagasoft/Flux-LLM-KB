using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class EmptyCatalogueReadinessIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Empty_catalogue_is_ready_without_a_usearch_file()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        await using var context = CreateContext();
        var bootstrapper = new EmptyCatalogueBootstrapper();

        await bootstrapper.ProveAndMarkAsync(context, CancellationToken.None);

        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
        Assert.NotNull(state.EmptyCatalogueValidatedAtUtc);
        Assert.Null(state.ActiveIndexGenerationId);

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        Assert.True((await new SqlServerReadinessValidator().ValidateAsync(connection)).IsReady);
    }

    [NativeSqlServerFact]
    public async Task Validated_empty_catalogue_is_explicit_in_the_derived_recovery_snapshot()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        await using (var context = await factory.CreateDbContextAsync())
        {
            await new EmptyCatalogueBootstrapper().ProveAndMarkAsync(context, CancellationToken.None);
        }

        var snapshot = await new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System)
            .ReadActiveAsync(CancellationToken.None);

        Assert.True(snapshot.IsValidatedEmptyCatalogue);
        Assert.Null(snapshot.ActiveGenerationId);
        Assert.Null(snapshot.Generation);
        Assert.Empty(snapshot.Membership);
        Assert.Empty(snapshot.ReferencedGenerationIds);
        Assert.Empty(snapshot.ReferencedIndexPaths);
    }

    [NativeSqlServerFact]
    public async Task Marker_and_active_generation_are_unavailable_even_if_the_schema_constraint_is_untrusted()
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        var factory = SqlTestData.CreateFactory(_fixture);
        var generationId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            await new EmptyCatalogueBootstrapper().ProveAndMarkAsync(context, CancellationToken.None);
            context.IndexGenerations.Add(new IndexGenerationEntity
            {
                Id = generationId,
                ModelFingerprint = "empty-catalogue-test",
                Dimensions = 1,
                IndexPath = "validated-index",
                MetadataChecksum = new string('a', 64),
                VectorCount = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ValidatedAtUtc = DateTimeOffset.UtcNow
            });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                ALTER TABLE [dbo].[IndexState] NOCHECK CONSTRAINT [CK_IndexState_ActiveGenerationOrEmptyCatalogue];
                UPDATE [dbo].[IndexState] SET [ActiveIndexGenerationId] = {generationId} WHERE [Id] = 1;
                """);
        }

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        Assert.False((await new SqlServerReadinessValidator().ValidateAsync(connection)).IsReady);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqlDerivedIndexRecoveryStore(factory, TimeProvider.System)
                .ReadActiveAsync(CancellationToken.None));
    }

    [NativeSqlServerTheory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task Empty_marker_with_nonempty_state_is_unavailable(
        int vectors,
        int generations,
        int memberships)
    {
        await SqlTestData.ClearPipelineAsync(_fixture);
        await using var context = CreateContext();
        await new EmptyCatalogueBootstrapper().ProveAndMarkAsync(context, CancellationToken.None);

        await SeedContradictionAsync(context, vectors, generations, memberships);

        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        Assert.False((await new SqlServerReadinessValidator().ValidateAsync(connection)).IsReady);
    }

    [Fact]
    public async Task Normal_cli_usage_does_not_advertise_or_dispatch_provision_sql()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(typeof(FluxKnowledge.Cli.CliProgram).Assembly.Location);
        start.ArgumentList.Add("provision-sql");

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(2, process.ExitCode);
        Assert.DoesNotContain("provision-sql", output + error, StringComparison.Ordinal);
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString)
            .Options);

    private static async Task SeedContradictionAsync(
        FluxKnowledgeDbContext context,
        int vectors,
        int generations,
        int memberships)
    {
        if (vectors == 1)
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [dbo].[Vectors] NOCHECK CONSTRAINT ALL; " +
                "SET IDENTITY_INSERT [dbo].[Vectors] ON; " +
                "INSERT INTO [dbo].[Vectors] ([VectorId], [TextChunkId], [ModelFingerprint], [Dimensions], [Values], [TextChunkContentHash], [PayloadChecksum], [SourceRevision], [IsDeleted], [IndexGenerationId], [CreatedAtUtc]) " +
                "VALUES (900001, 900001, 'empty-catalogue-test', 1, 0x00000000, REPLICATE('a', 64), REPLICATE('b', 64), 1, 0, '00000000-0000-0000-0000-000000000001', SYSUTCDATETIME()); " +
                "SET IDENTITY_INSERT [dbo].[Vectors] OFF;");
        }

        if (generations == 1)
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO [dbo].[IndexGenerations] ([Id], [ModelFingerprint], [Dimensions], [IndexPath], [MetadataChecksum], [VectorCount], [CreatedAtUtc], [ValidatedAtUtc]) " +
                "VALUES ('00000000-0000-0000-0000-000000000002', 'empty-catalogue-test', 1, 'empty-catalogue-test', REPLICATE('c', 64), 0, SYSUTCDATETIME(), SYSUTCDATETIME());");
        }

        if (memberships == 1)
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE [dbo].[IndexGenerationVectors] NOCHECK CONSTRAINT ALL; " +
                "INSERT INTO [dbo].[IndexGenerationVectors] ([GenerationId], [VectorId]) VALUES ('00000000-0000-0000-0000-000000000003', 900003);");
        }
    }
}
