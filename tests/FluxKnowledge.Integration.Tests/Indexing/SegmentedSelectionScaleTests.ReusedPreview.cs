using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SegmentedSelectionScaleTests
{
    [NativeSqlServerFact]
    public async Task Reuse_preview_compares_a_root_worklist_once_and_preserves_removed_repacked_and_ordered_segments()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The SQL-only reuse preview fixture.", segmented: true);
        await using var db = await environment.Factory.CreateDbContextAsync();
        var template = await db.Vectors.Select(value => value.VectorId).FirstAsync();
        await SeedUnrelatedAsync(db, template, 25_000);
        var state = await db.IndexState.AsNoTracking().SingleAsync();
        var profile = await db.IndexSegments.AsNoTracking().FirstAsync();
        var segments = Enumerable.Range(0, 48).Select(value => (value + 1_000).ToString("x64", CultureInfo.InvariantCulture)).ToArray();
        foreach (var id in segments) db.IndexSegments.Add(new()
        {
            Id = id, ModelFingerprint = profile.ModelFingerprint, Dimensions = profile.Dimensions,
            NativeFormat = "synthetic-sql-only", DirectoryPath = "sql-only-no-native-payload",
            MembershipChecksum = new('0', 64), FileChecksum = new('0', 64), FileLength = 1, VectorCount = 1,
            State = 1, CreatedAtUtc = DateTimeOffset.UtcNow, SealedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var mapping = JsonSerializer.Serialize(segments.Select((id, ordinal) => new { Id = id, Ordinal = ordinal }));
        // These metadata-only rows never enter an ANN reader or publication.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT [IndexSegmentVectors] ([SegmentId],[VectorId])
            SELECT [segment].[Id],[member].[VectorId] FROM [CurrentIndexMembers] AS [member]
            INNER JOIN OPENJSON({mapping}) WITH ([Id] char(64) '$.Id',[Ordinal] int '$.Ordinal') AS [segment]
              ON [member].[VectorId]%48=[segment].[Ordinal];
            UPDATE [member] SET [SegmentId]=[segment].[Id]
            FROM [CurrentIndexMembers] AS [member]
            INNER JOIN OPENJSON({mapping}) WITH ([Id] char(64) '$.Id',[Ordinal] int '$.Ordinal') AS [segment]
              ON [member].[VectorId]%48=[segment].[Ordinal];
            INSERT [IndexGenerationSegments] ([GenerationId],[SegmentId],[Ordinal])
            SELECT {state.ActiveIndexGenerationId},[Id],[Ordinal]+1
            FROM OPENJSON({mapping}) WITH ([Id] char(64) '$.Id',[Ordinal] int '$.Ordinal');
            """);
        var removed = await db.CurrentIndexMembers.Where(value => value.SegmentId == segments[7] || value.SegmentId == segments[16])
            .GroupBy(value => value.SegmentId).Select(group => group.Min(value => value.VectorId)).ToArrayAsync();
        Assert.Equal(2, removed.Length);
        var count = await db.CurrentIndexMembers.LongCountAsync();
        var repacks = new[] { segments[3], segments[7], segments[11], segments[47] };
        var candidate = new IndexPublicationCandidateEntity
        {
            Id = Guid.NewGuid(), BaseGenerationId = state.ActiveIndexGenerationId, CorpusEpoch = state.CorpusEpoch,
            ExpectedCorpusVersion = state.CorpusVersion, PublicationCorpusVersion = state.CorpusVersion,
            BeforeChecksum = new('0', 64), AfterChecksum = new('0', 64), OwnerMachineFingerprint = new('0', 64),
            CreatedAtUtc = DateTimeOffset.UtcNow, OwnerStartedAtUtc = DateTimeOffset.UtcNow,
            BeforeCount = count, AfterCount = count - removed.Length, WorklistComplete = true,
            RepackedSegmentsJson = JsonSerializer.Serialize(repacks)
        };
        db.IndexPublicationCandidates.Add(candidate);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT [IndexCandidateMembers] ([CandidateId],[Phase],[VectorId],[PayloadChecksum],[SourceIdentityId],[OwnerSourceRevisionId],[SourceRootId])
            SELECT {candidate.Id},[phase].[Id],[member].[VectorId],[vector].[PayloadChecksum],
                   [member].[SourceIdentityId],[member].[OwnerSourceRevisionId],[member].[SourceRootId]
            FROM [CurrentIndexMembers] AS [member] INNER JOIN [Vectors] AS [vector] ON [member].[VectorId]=[vector].[VectorId]
            CROSS JOIN (VALUES(0),(1)) AS [phase]([Id])
            WHERE [phase].[Id]=0 OR [member].[VectorId] NOT IN ({removed[0]},{removed[1]});
            """);
        var expected = await (from link in db.IndexGenerationSegments
                              join segment in db.IndexSegments on link.SegmentId equals segment.Id
                              where link.GenerationId == candidate.BaseGenerationId && !repacks.Contains(segment.Id) && segment.Id != segments[16]
                              orderby link.Ordinal select segment).AsNoTracking().ToArrayAsync();
        var capture = new ReusePreviewCommands();
        await using var measured = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(capture).Options);
        var actual = await SqlSegmentedIndexStore.ReadReusedSegmentsAsync(measured, candidate, CancellationToken.None);
        Assert.Equal(expected.Select(SqlSegmentedIndexStore.Describe), actual.Select(SqlSegmentedIndexStore.Describe));
        var commands = capture.Commands.ToArray();
        capture.Commands.Clear();
        await measured.Database.OpenConnectionAsync();
        long rowsRead = 0, logicalReads = 0;
        foreach (var item in commands)
        {
            await using var command = measured.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SET STATISTICS XML ON;\n" + item.Sql + ";\nSET STATISTICS XML OFF;";
            foreach (var parameter in item.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
            await using var reader = await command.ExecuteReaderAsync();
            XDocument? plan = null;
            do
            {
                while (await reader.ReadAsync())
                    if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.Ordinal))
                        plan = XDocument.Parse(reader.GetString(0));
            } while (await reader.NextResultAsync());
            Assert.NotNull(plan);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            foreach (var operation in plan.Descendants(ns + "RelOp").Where(op => op.Elements().Any(access =>
                (string?)access.Element(ns + "Object")?.Attribute("Table") is "[IndexCandidateMembers]" or "[CurrentIndexMembers]")))
                foreach (var counter in operation.Descendants(ns + "RunTimeCountersPerThread"))
                {
                    rowsRead += long.Parse((string?)counter.Attribute("ActualRowsRead") ?? (string?)counter.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture);
                    logicalReads += long.Parse((string?)counter.Attribute("ActualLogicalReads") ?? "0", CultureInfo.InvariantCulture);
                }
        }
        output.WriteLine($"reuse_preview members={count} base_segments={expected.Length + 5} rows_read={rowsRead} logical_reads={logicalReads}");
        Assert.InRange(rowsRead, 1, 6 * count + 2_000);
        // Explicit full builds continue to replace every base segment.
        candidate.BuildPurpose = 1;
        Assert.Empty(await SqlSegmentedIndexStore.ReadReusedSegmentsAsync(measured, candidate, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task A_publication_between_reuse_preview_reads_cannot_seal_the_stale_candidate()
    {
        var interceptor = new ReusePreviewCommands();
        var factory = new ReusePreviewFactory(fixture.ConnectionString, interceptor);
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,
            "The first generation remains readable until the winner publishes.", segmented: true,
            configureServices: services => services.AddScoped<ISegmentedIndexStore>(provider => new SqlSegmentedIndexStore(
                factory, TimeProvider.System, provider.GetRequiredService<FluxKnowledge.Application.Gpu.IGpuInteractiveOwnerProbe>(),
                provider.GetRequiredService<IDeploymentValidationHold>(), provider.GetRequiredService<FluxKnowledge.Application.Indexing.IndexSegmentBuildOptions>())));
        Guid staleId = default, winner = default;
        interceptor.BeforeReuse = async () =>
        {
            await using var db = await environment.Factory.CreateDbContextAsync();
            staleId = await db.IndexPublicationCandidates.Where(value => value.State == 0 && value.WorklistComplete)
                .Select(value => value.Id).SingleAsync();
            await environment.AddAndPumpAsync("A competing real publication wins between the two preview reads.");
            winner = (await environment.ActiveGenerationAsync()).Id;
        };
        using var scope = environment.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IIndexGenerationPublisher>();
        await Assert.ThrowsAsync<PublicationSnapshotConflictException>(async () =>
            await publisher.BuildAndPlaceAsync(Guid.Empty, CancellationToken.None));
        Assert.NotEqual(Guid.Empty, staleId);
        Assert.NotEqual(Guid.Empty, winner);
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var stale = await verification.IndexPublicationCandidates.AsNoTracking().SingleAsync(value => value.Id == staleId);
        Assert.Equal(3, stale.State); // Normal publisher abandonment succeeded.
        Assert.Null(stale.ResultGenerationId);
        Assert.False(await verification.IndexCandidateMembers.AnyAsync(value => value.CandidateId == staleId));
        Assert.Equal(winner, await verification.IndexState.Select(value => value.ActiveIndexGenerationId).SingleAsync());
        await SqlToUsearchRebuildTests.AssertSegmentedOracleAsync(environment);
    }

    private sealed class ReusePreviewFactory(string connection, ReusePreviewCommands capture) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).AddInterceptors(capture).Options);
    }

    private sealed class ReusePreviewCommands : DbCommandInterceptor
    {
        public List<(string Sql, DbParameter[] Parameters)> Commands { get; } = [];
        public Func<Task>? BeforeReuse { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[IndexGenerationSegments]", StringComparison.Ordinal) &&
                command.CommandText.Contains("ORDER BY [i].[Ordinal]", StringComparison.Ordinal) && BeforeReuse is { } publish)
            {
                BeforeReuse = null;
                await publish();
            }
            Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(value => (DbParameter)((ICloneable)value).Clone()).ToArray()));
            return result;
        }
    }
}
