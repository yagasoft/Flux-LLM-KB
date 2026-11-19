using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class LegacySourceStatusReadCostTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Mixed_pending_and_deferred_history_keeps_exact_assessment_with_bounded_summary_reads()
    {
        const int currentFiles = 3000, candidates = 1000, pendingOnly = 10, unrelatedFiles = 6000;
        var root = Id(1); var unrelated = Id(2); var now = DateTimeOffset.UtcNow;
        var factory = new StatusCostFactory(fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            foreach (var rootId in new[] { root, unrelated })
                setup.SourceRootConfigurations.Add(new()
                {
                    Id = rootId, CanonicalPath = $"I:\\synthetic-status-cost\\{rootId:N}", DisplayName = rootId.ToString(),
                    State = (int)SourceRootState.Enabled, ConfigurationRevision = 1, MaximumFileBytes = SourcePolicyDefaults.MaximumTextBytes,
                    CreatedAtUtc = now, UpdatedAtUtc = now
                });
            await setup.SaveChangesAsync();
        }
        for (var start = 0; start < currentFiles + unrelatedFiles; start += 500)
        {
            await using var setup = factory.CreateDbContext();
            for (var index = start; index < Math.Min(start + 500, currentFiles + unrelatedFiles); index++)
            {
                var ownerRoot = index < currentFiles ? root : unrelated;
                var revision = new SourceRevisionEntity
                {
                    Id = Id(10000 + index), SourceRootId = ownerRoot, StableSourceIdentity = $"cost:{index}", Revision = 1,
                    ContentSha256 = new string('a', 64), CanonicalPath = $"I:\\synthetic-status-cost\\{ownerRoot:N}\\{index}.txt",
                    Classification = index < candidates ? "Unknown" : "AcceptedUtf8Text", Extension = ".txt", ByteLength = 1,
                    DiscoveryEvidenceJson = "{}", DiscoveredAtUtc = now
                };
                setup.SourceRevisions.Add(revision);
                setup.SourceActivities.Add(Activity(Id(30000 + index), revision.Id,
                    index < candidates ? SourceActivityState.Pending : SourceActivityState.DeferredUnsupported));
                if (index is >= pendingOnly and < candidates)
                {
                    var deferred = Activity(Id(50000 + index), revision.Id,
                        index % 2 == 0 ? SourceActivityState.DeferredPolicy : SourceActivityState.DeferredUnsupported);
                    deferred.ActivityKind = (int)SourceActivityKind.DocumentParsing;
                    deferred.ProcessorVersion = "fixture-deferred-owner";
                    deferred.Reason = "fixture-existing-owner";
                    setup.SourceActivities.Add(deferred);
                }
            }
            await setup.SaveChangesAsync();
        }
        await using var context = factory.CreateDbContext();
        await context.Database.OpenConnectionAsync();
        var before = await LogicalReadsAsync(context);
        var timer = Stopwatch.StartNew();
        var result = (await SourceRootProjectionReader.ReadStateSummariesAsync(context, [root], default))[root];
        timer.Stop();
        var reads = await LogicalReadsAsync(context) - before;
        var projection = string.Join("\n", result.Files.OrderBy(value => value.SourceRevisionId)
            .Select(value => $"{value.SourceRevisionId:D}|{value.Category}|{value.Reason}"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));
        output.WriteLine($"Status-cost: files={result.Known}; logicalReads={reads}; elapsedMs={timer.Elapsed.TotalMilliseconds:F1}; statusDigest={digest}.");
        // Golden projection captured before the performance change: all 3,000
        // current IDs, categories and reasons must stay byte-identical.
        Assert.Equal("7d3150339a22ff7878856eecf5e7e8924b9f2ba47039c50eb390b41b34b23884", digest);
        Assert.Equal(currentFiles, result.Known);
        Assert.Equal(currentFiles, result.Files.Count);
        for (var index = 0; index < pendingOnly; index++)
        {
            var file = Assert.Single(result.Files, value => value.SourceRevisionId == Id(10000 + index));
            Assert.Equal("Blocked", file.Category);
            Assert.Equal(RetainedDefaultPolicy.UnadmittedLegacyTextReason, file.Reason);
        }
        // Unrelated deferred revisions, including those in this same root, cannot
        // become an owner for these ten pending-only files.
        var eligible = await SqlCurrentSourceActivities.UnadmittedLegacyTextIds(context).ToArrayAsync();
        Assert.Equal(Enumerable.Range(0, pendingOnly).Select(index => Id(30000 + index)).Order(), eligible.Order());
        Assert.True(reads < 150000, $"Summary consumed {reads} logical reads for {currentFiles} current files.");
        // List/detail readers use their normal independent contexts. Reading
        // projections never needs source path validation or file enumeration.
        var reader = new SourceRootProjectionReader(factory, null!, null!, new LocalSourceCapabilityHandlerRegistry([]));
        var list = (await reader.ReadRootsAsync(default)).Single(value => value.Id == root);
        Assert.Equal(result.Blocked, list.BlockedCount);
        Assert.Equal(result.Deferred, list.DeferredCount);
        var detail = await reader.ReadRootAsync(root, default);
        Assert.NotNull(detail);
        Assert.Equal(Enumerable.Range(0, currentFiles).Select(index => $"{index}.txt").Order(StringComparer.Ordinal),
            detail.Files.Select(value => value.RelativePath).Order(StringComparer.Ordinal));
        for (var index = 0; index < pendingOnly; index++)
        {
            var file = Assert.Single(detail.Files, value => value.RelativePath == $"{index}.txt");
            Assert.Equal("Blocked", file.Status);
            Assert.Equal(RetainedDefaultPolicy.UnadmittedLegacyTextReason, file.Reason);
        }
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static SourceActivityEntity Activity(Guid id, Guid revision, SourceActivityState state) => new()
    {
        Id = id, SourceRevisionId = revision, ActivityKind = (int)SourceActivityKind.TextExtraction,
        ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "phase-3a-v1",
        DescriptorFingerprint = SourceActivityEntity.LegacyDescriptorFingerprint, InputFingerprint = new string('a', 64),
        State = (int)state, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
    };
    private static async Task<long> LogicalReadsAsync(FluxKnowledgeDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id=@@SPID";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class StatusCostFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection).Options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
