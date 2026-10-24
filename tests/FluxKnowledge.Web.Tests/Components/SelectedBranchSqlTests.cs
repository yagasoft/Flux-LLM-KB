using System.Data.Common;
using System.Data;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SelectedBranchSqlTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    public static IEnumerable<object[]> BranchSelectionCases()
    {
        foreach (var origin in new[] { 2, 3 })
        foreach (var (selection, historical) in new[]
        {
            ("empty", false), ("valid", false), ("duplicate-valid", false), ("foreign", false),
            ("missing", true), ("valid-and-missing", true), ("wrong-child", true),
            ("wrong-activity", true), ("wrong-record", true), ("missing-revision", true)
        }) yield return [origin, selection, historical];
    }

    [NativeSqlServerTheory]
    [MemberData(nameof(BranchSelectionCases))]
    public async Task Selected_parent_branches_preserve_exact_historical_binding_and_GPU_precedence(
        int origin, string selection, bool historical)
    {
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var parent = Revision(Guid.NewGuid(), root, "parent.zip", now, false);
        var foreign = Revision(Guid.NewGuid(), root, "foreign.zip", now, false);
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        context.SourceRevisions.AddRange(parent, foreign);
        await context.SaveChangesAsync();
        var child = Revision(Guid.NewGuid(), root, "derived.txt", now, false);
        child.OriginKind = origin;
        child.ParentSourceRevisionId = parent.Id;
        var recordId = await SeedActivityJobAsync(root, now, PublicJobState.GpuQueued, null, source: child);
        var job = await context.Jobs.SingleAsync(j => j.PipelineRecordId == recordId);
        var valid = AddBranch(parent.Id);
        var missing = AddBranch(parent.Id);
        var foreignBranch = AddBranch(foreign.Id);
        var binding = Activity(selection == "wrong-activity" ? foreign.Id : child.Id, SourceActivityState.Completed, recordId);
        binding.ProcessorVersion = Guid.NewGuid().ToString("N");
        if (selection == "wrong-record")
            binding.ResultingPipelineRecordId = await SeedActivityJobAsync(root, now, PublicJobState.Completed, null);
        if (selection == "missing-revision") binding.ResultingPipelineRecordRevision = null;
        context.SourceActivities.Add(binding);
        context.SourceProcessorBranchMembers.Add(new()
        {
            Id = Guid.NewGuid(), BranchId = valid, MemberFingerprint = new string('c', 64),
            ChildSourceRevisionId = selection == "wrong-child" ? foreign.Id : child.Id,
            ChildSourceActivityId = binding.Id, Disposition = "registered", ByteLength = 1, CreatedAtUtc = now
        });
        var tasks = new[] { 0, 1, 3 }.Select(state => new GpuMiniTaskEntity
        {
            Id = Guid.NewGuid(), ParentJobId = job.Id, SourceRevision = 1, ExecutionState = state,
            ModelRuntimeKey = "synthetic", SettingsFingerprint = "synthetic", EstimatedBytes = 1,
            IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = now
        }).ToArray();
        context.GpuMiniTasks.AddRange(tasks);
        await context.SaveChangesAsync();
        Guid[] ids = selection switch
        {
            "empty" => [], "foreign" => [foreignBranch], "missing" => [missing],
            "duplicate-valid" => [valid, valid], "valid-and-missing" => [valid, missing], _ => [valid]
        };
        await using (var branches = await SqlSelectedBranchReadScope.CreateAsync(context, ids, CancellationToken.None))
        {
            var evidence = new SqlGpuActivityReadEvidence("[]", 0);
            var row = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), branches, evidence)
                .SingleAsync(row => row.PipelineRecordId == recordId);
            Assert.Equivalent(new PipelineJobActivityRow
            {
                Id = job.Id, PipelineRecordId = recordId, SourceRootId = root, Stage = (int)PipelineStage.Extract,
                LogicalSourceRevisionId = parent.Id, SourceRevision = 1, CurrentStage = (int)PipelineStage.Extract,
                FullPath = parent.CanonicalPath,
                PublicState = (int)PublicJobState.GpuQueued, IsHistorical = historical, SourceAdmitted = true,
                HasReadyGpu = true, HasUncertainGpu = true, HasPendingDispatch = true, IsDue = true, RecordedAtUtc = now
            }, row);
            await branches.MaterializeGpuFactsAsync(now, evidence, CancellationToken.None);
            var materialized = await SqlPipelineActivityQuery.Jobs(context, now, new(false, null), branches, evidence)
                .SingleAsync(candidate => candidate.PipelineRecordId == recordId);
            Assert.Equivalent(row, materialized);
            var gpu = await SqlPipelineActivityQuery.GpuTasks(context, now, new(false, null), branches, evidence)
                .Where(row => row.PipelineRecordId == recordId).ToArrayAsync();
            Assert.Equal(3, gpu.Length);
            Assert.Equal(historical ? "Historical unresolved" : "Ready", Assert.Single(gpu, row => row.Id == tasks[0].Id).State);
            Assert.Equal("Ownership unconfirmed", Assert.Single(gpu, row => row.Id == tasks[1].Id).State);
            Assert.Equal("Outcome uncertain", Assert.Single(gpu, row => row.Id == tasks[2].Id).State);
            Assert.All(gpu, row => { Assert.Equal(root, row.SourceRootId); Assert.False(row.IsInteractive); });
            await branches.CompleteAsync(CancellationToken.None);
        }
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        Guid AddBranch(Guid owner)
        {
            var activity = Activity(owner, SourceActivityState.Completed);
            activity.ProcessorVersion = Guid.NewGuid().ToString("N");
            context.SourceActivities.Add(activity);
            var branchId = Guid.NewGuid();
            context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = branchId, SourceActivityId = activity.Id, SourceRevisionId = owner,
                InputSha256 = new string('a', 64), ProcessorVersion = activity.ProcessorVersion,
                ProcessorFingerprint = activity.ProcessorVersion, State = 2, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            return branchId;
        }
    }

    [NativeSqlServerFact]
    public async Task Selected_branch_input_is_consumed_once_for_both_activity_aggregates()
    {
        var commands = new SelectedBranchCommands();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString, commands).CreateDbContext();
        _ = await new SqlPipelineActivityReader(null, TimeProvider.System)
            .ReadAsync(context, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(commands.InputConsumers);
        Assert.DoesNotContain(commands.InputConsumers, sql => sql.Contains("FROM Jobs j", StringComparison.Ordinal)
            || sql.Contains("FROM GpuMiniTasks t", StringComparison.Ordinal));
    }

    private sealed class SelectedBranchCommands : DbCommandInterceptor
    {
        public List<string> InputConsumers { get; } = [];
        private void Capture(DbCommand command)
        {
            if (command.CommandText.Contains("OPENJSON(@selectedBranches)", StringComparison.Ordinal))
                InputConsumers.Add(command.CommandText);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }
    }
    private async Task<Guid> AddActivityRootAsync(DateTimeOffset now)
    {
        var root = Guid.NewGuid();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = root, CanonicalPath = $"C:\\overview-source-indexing\\{root:N}", DisplayName = "Activity test",
            State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
            AllowedClassificationsJson = "[\"text/plain\"]", MaximumFileBytes = 10000,
            ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return root;
    }

    private async Task<Guid> SeedActivityJobAsync(Guid root, DateTimeOffset now, PublicJobState state,
        DateTimeOffset? leaseExpiresAtUtc, long leaseGeneration = 0, int attemptCount = 0,
        bool suppressed = false, SourceRevisionEntity? source = null)
    {
        var recordId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using var context = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext();
        var revision = source ?? Revision(Guid.NewGuid(), root, $"{recordId:N}.txt", now, suppressed);
        context.SourceRevisions.Add(revision);
        context.SourceIdentities.Add(new SourceIdentityEntity { Id = identityId, SourceKind = "local file",
            StableKey = $"C:\\activity-ingress\\{recordId:N}.txt", CreatedAtUtc = now });
        context.PipelineRecords.Add(new PipelineRecordEntity { Id = recordId, SourceIdentityId = identityId, SourceRevisionId = revision.Id,
            Revision = 1, ContentHash = revision.ContentSha256, RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Extract, RegisteredAtUtc = now });
        context.Jobs.Add(new JobEntity { Id = jobId, PipelineRecordId = recordId, SourceRevision = 1,
            Stage = (int)PipelineStage.Extract, Operation = PipelineOperations.ExtractUtf8, PublicState = (int)state,
            DueAtUtc = now, AttemptCount = attemptCount, LeaseOwner = state == PublicJobState.WorkerProcessing ? "activity-worker" : null,
            LeaseExpiresAtUtc = leaseExpiresAtUtc, LeaseGeneration = leaseGeneration });
        context.OutboxMessages.Add(new OutboxMessageEntity { Id = Guid.NewGuid(), JobId = jobId,
            PipelineRecordId = recordId, SourceRevision = 1, Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8, DispatchGeneration = 0,
            IdempotencyKey = $"{recordId:N}:1:Extract:0", DueAtUtc = now, CreatedAtUtc = now });
        await context.SaveChangesAsync();
        return recordId;
    }

    private static SourceRevisionEntity Revision(
        Guid id,
        Guid rootId,
        string path,
        DateTimeOffset now,
        bool suppressed) => new()
    {
        Id = id,
        SourceRootId = rootId,
        StableSourceIdentity = $"overview-source:{id:N}",
        Revision = 1,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"C:\\overview-source-indexing\\{rootId:N}\\{path}",
        Classification = "AcceptedUtf8Text",
        Extension = Path.GetExtension(path),
        ByteLength = 1,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = "{}",
        SuppressedAtUtc = suppressed ? now : null
    };

    private static SourceActivityEntity Activity(
        Guid sourceRevisionId,
        SourceActivityState state,
        Guid? resultingPipelineRecordId = null) => new()
    {
        Id = Guid.NewGuid(),
        SourceRevisionId = sourceRevisionId,
        ActivityKind = (int)SourceActivityKind.TextExtraction,
        ExecutionClass = (int)(state == SourceActivityState.DeferredUnsupported
            ? ExecutionClass.DeferredCapability
            : ExecutionClass.InProcess),
        ProcessorVersion = "phase-3a-v1",
        InputFingerprint = new string('a', 64),
        State = (int)state,
        ResultingPipelineRecordId = resultingPipelineRecordId,
        ResultingPipelineRecordRevision = resultingPipelineRecordId is null ? null : 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch
    };

    private sealed class TestDbContextFactory(string connectionString, params IInterceptor[] interceptors)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(interceptors)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }

}

