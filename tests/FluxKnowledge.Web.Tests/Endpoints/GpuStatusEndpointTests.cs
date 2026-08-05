using System.Net;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class GpuStatusEndpointTests : IClassFixture<GpuStatusEndpointTests.GpuStatusApplicationFactory>
{
    private readonly HttpClient _client;

    public GpuStatusEndpointTests(GpuStatusApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Gpu_status_endpoint_exposes_only_the_sanitised_read_only_snapshot()
    {
        using var response = await _client.GetAsync("/api/gpu-status");
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            new[]
            {
                "activeBatchLane",
                "activeCount",
                "availableSlotCount",
                "deferredCount",
                "hasActiveBatch",
                "laneCounts",
                "nextDeferredAtUtc",
                "outcomeUncertainCount",
                "readyCount",
                "reservedSlotCount",
                "uncertainCapacity",
                "uncertainSlotCount"
            },
            result.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(2, result.GetProperty("readyCount").GetInt32());
        Assert.Equal(1, result.GetProperty("activeCount").GetInt32());
        Assert.Equal(1, result.GetProperty("deferredCount").GetInt32());
        Assert.Equal(1, result.GetProperty("outcomeUncertainCount").GetInt32());
        Assert.Equal("DocumentIndexing", result.GetProperty("activeBatchLane").GetString());
        Assert.True(result.GetProperty("hasActiveBatch").GetBoolean());
        Assert.Equal(3, result.GetProperty("availableSlotCount").GetInt32());
        Assert.Equal(1, result.GetProperty("reservedSlotCount").GetInt32());
        Assert.Equal(1, result.GetProperty("uncertainSlotCount").GetInt32());
        Assert.Equal("2026-07-30T12:05:00+00:00", result.GetProperty("nextDeferredAtUtc").GetString());

        var laneCounts = result.GetProperty("laneCounts");
        Assert.Equal(
            new[] { "documentIndexing", "imageEnrichment", "imageOcr", "interactiveRetrieval", "videoOrUnknown" },
            laneCounts.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(2, laneCounts.GetProperty("interactiveRetrieval").GetInt32());
        Assert.Equal(1, laneCounts.GetProperty("documentIndexing").GetInt32());
        Assert.Equal(0, laneCounts.GetProperty("imageOcr").GetInt32());
        Assert.Equal(0, laneCounts.GetProperty("imageEnrichment").GetInt32());
        Assert.Equal(0, laneCounts.GetProperty("videoOrUnknown").GetInt32());

        var uncertainCapacity = result.GetProperty("uncertainCapacity");
        Assert.Equal(new[] { "ageMinutes", "state" }, uncertainCapacity.EnumerateObject()
            .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("Uncertain", uncertainCapacity.GetProperty("state").GetString());
        Assert.Equal(45, uncertainCapacity.GetProperty("ageMinutes").GetInt32());

        Assert.DoesNotContain("mini-task-id-0001", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("batch-id-0001", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("slot/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("owner/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("settings/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private\\input.txt", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private exception text", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private runtime output", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Gpu_status_endpoint_rejects_every_mutation_verb(string method)
    {
        using var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/gpu-status"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Gpu_status_endpoint_sanitises_an_expected_projection_failure()
    {
        using var factory = new FailingGpuStatusApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/gpu-status");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(body);
        Assert.DoesNotContain(FailingProjectionReader.SensitiveExceptionMarker, body, StringComparison.Ordinal);
    }

    public sealed class GpuStatusApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(
                "ConnectionStrings:FluxKnowledge",
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                "Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting(
                "Usearch:RootPath",
                Path.Combine(Path.GetTempPath(), "FluxKnowledgeGpuStatusEndpointTests"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProjectionReader>();
                services.AddScoped<IProjectionReader, FixedProjectionReader>();
            });
        }
    }

    private sealed class FailingGpuStatusApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(
                "ConnectionStrings:FluxKnowledge",
                "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;" +
                "Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting(
                "Usearch:RootPath",
                Path.Combine(Path.GetTempPath(), "FluxKnowledgeGpuStatusEndpointFailureTests"));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProjectionReader>();
                services.AddScoped<IProjectionReader, FailingProjectionReader>();
            });
        }
    }

    private sealed class FixedProjectionReader : IProjectionReader
    {
        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OverviewProjection(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "none",
                new IndexRecoverySummary("Starting", null, null, null, null, 0)));

        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new GpuSchedulerStatusProjection(
                2,
                1,
                1,
                1,
                new GpuSchedulerLaneCounts(2, 1, 0, 0, 0),
                true,
                "DocumentIndexing",
                3,
                1,
                1,
                DateTimeOffset.Parse("2026-07-30T12:05:00+00:00"),
                new GpuCapacityUncertaintySummary("Uncertain", 45)));

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(null);
    }

    private sealed class FailingProjectionReader : IProjectionReader
    {
        public const string SensitiveExceptionMarker = "private-gpu-status-exception-marker";

        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SensitiveExceptionMarker);

        public ValueTask<GpuSchedulerStatusProjection> ReadGpuSchedulerStatusAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SensitiveExceptionMarker);

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SensitiveExceptionMarker);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SensitiveExceptionMarker);
    }
}

public sealed class GpuStatusEndpointTestsSql(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Gpu_status_endpoint_reads_only_the_sanitised_aggregate_SQL_projection()
    {
        var now = DateTimeOffset.Parse("2026-07-30T12:00:00+00:00");
        var nextDeferredAtUtc = now.AddMinutes(5);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var batchId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var dispatchId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");
        var receiptOperationId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var evidenceOperationId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var privateDigest = Encoding.UTF8.GetBytes("private-result-digest-marker-000");
        await ClearSchedulerStatusAsync(factory);
        await SeedSchedulerStatusAsync(
            factory,
            now,
            nextDeferredAtUtc,
            batchId,
            dispatchId,
            receiptOperationId,
            evidenceOperationId,
            privateDigest);
        await using var application = await CreateApplicationAsync(factory, now);

        using var response = await application.GetTestClient().GetAsync("/api/gpu-status");
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, result.GetProperty("readyCount").GetInt32());
        Assert.Equal(1, result.GetProperty("activeCount").GetInt32());
        Assert.Equal(1, result.GetProperty("deferredCount").GetInt32());
        Assert.Equal(1, result.GetProperty("outcomeUncertainCount").GetInt32());
        Assert.True(result.GetProperty("hasActiveBatch").GetBoolean());
        Assert.Equal("DocumentIndexing", result.GetProperty("activeBatchLane").GetString());
        Assert.Equal(1, result.GetProperty("availableSlotCount").GetInt32());
        Assert.Equal(1, result.GetProperty("reservedSlotCount").GetInt32());
        Assert.Equal(1, result.GetProperty("uncertainSlotCount").GetInt32());
        Assert.Equal("2026-07-30T12:05:00+00:00", result.GetProperty("nextDeferredAtUtc").GetString());
        Assert.Equal(1, result.GetProperty("laneCounts").GetProperty("interactiveRetrieval").GetInt32());
        Assert.Equal(2, result.GetProperty("laneCounts").GetProperty("documentIndexing").GetInt32());
        Assert.Equal(1, result.GetProperty("laneCounts").GetProperty("imageOcr").GetInt32());
        Assert.Equal(0, result.GetProperty("laneCounts").GetProperty("imageEnrichment").GetInt32());
        Assert.Equal(0, result.GetProperty("laneCounts").GetProperty("videoOrUnknown").GetInt32());
        Assert.Equal("Uncertain", result.GetProperty("uncertainCapacity").GetProperty("state").GetString());
        Assert.Equal(24 * 60, result.GetProperty("uncertainCapacity").GetProperty("ageMinutes").GetInt32());

        Assert.DoesNotContain("C:\\private\\input.txt", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("mini-task-id-0001", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private-task-idempotency-key", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(batchId.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(dispatchId.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(receiptOperationId.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(evidenceOperationId.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private-dispatch-slot-key", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("owner/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private-executor-key", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private-verifier-key", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(privateDigest), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(privateDigest), payload, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("settings/private", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-07-28T12:00:00+00:00", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private exception text", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private runtime output", payload, StringComparison.Ordinal);
    }

    [NativeSqlServerFact]
    public async Task Gpu_status_endpoint_excludes_completed_history_from_current_lane_counts()
    {
        var now = DateTimeOffset.Parse("2026-07-30T12:00:00+00:00");
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await ClearSchedulerStatusAsync(factory);
        await using (var seed = factory.CreateDbContext())
        {
            AddMiniTask(
                seed,
                now,
                "completed-history-only",
                GpuPriorityLane.InteractiveRetrieval,
                GpuMiniTaskExecutionState.Completed,
                null,
                null);
            await seed.SaveChangesAsync();
        }

        await using var application = await CreateApplicationAsync(factory, now);
        using var response = await application.GetTestClient().GetAsync("/api/gpu-status");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, result.GetProperty("readyCount").GetInt32());
        Assert.Equal(0, result.GetProperty("activeCount").GetInt32());
        Assert.Equal(0, result.GetProperty("deferredCount").GetInt32());
        Assert.Equal(0, result.GetProperty("outcomeUncertainCount").GetInt32());
        foreach (var laneCount in result.GetProperty("laneCounts").EnumerateObject())
        {
            Assert.Equal(0, laneCount.Value.GetInt32());
        }
    }

    private static async Task<WebApplication> CreateApplicationAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        DateTimeOffset now)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(factory);
        builder.Services.AddSingleton<IDerivedIndexRecoveryStatus>(new FixedRecoveryStatus());
        builder.Services.AddScoped<IGpuSchedulerStore>(_ =>
            new SqlGpuSchedulerStore(factory, timeProvider: new FixedTimeProvider(now)));
        builder.Services.AddScoped<IProjectionReader, SqlProjectionReader>();
        var application = builder.Build();
        application.MapFluxKnowledgeGpuStatus();
        await application.StartAsync();
        return application;
    }

    private static async Task SeedSchedulerStatusAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        DateTimeOffset now,
        DateTimeOffset nextDeferredAtUtc,
        Guid batchId,
        Guid dispatchId,
        Guid receiptOperationId,
        Guid evidenceOperationId,
        byte[] privateDigest)
    {
        await using var context = await factory.CreateDbContextAsync();
        context.GpuCapacitySlots.AddRange(
            new GpuCapacitySlotEntity
            {
                SlotKey = "slot/available",
                State = (int)GpuCapacitySlotState.Available,
                UpdatedAtUtc = now
            },
            new GpuCapacitySlotEntity
            {
                SlotKey = "private-dispatch-slot-key",
                State = (int)GpuCapacitySlotState.Reserved,
                OwnerKey = "owner/private",
                LastHeartbeatAtUtc = now.AddMinutes(-2),
                UpdatedAtUtc = now
            },
            new GpuCapacitySlotEntity
            {
                SlotKey = "slot/uncertain",
                State = (int)GpuCapacitySlotState.Uncertain,
                OwnerKey = "owner/private",
                LastHeartbeatAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now
            });
        context.GpuBatches.Add(new GpuBatchEntity
        {
            Id = batchId,
            CapacitySlotKey = "private-dispatch-slot-key",
            PriorityLane = (int)GpuPriorityLane.DocumentIndexing,
            ModelRuntimeKey = "runtime/private",
            SettingsFingerprint = "settings/private",
            ItemCount = 1,
            EstimatedBytes = 100,
            AdmissionGeneration = 1,
            OwnerKey = "owner/private",
            State = (int)GpuBatchState.Active,
            LastHeartbeatAtUtc = now.AddMinutes(-2),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        AddMiniTask(context, now, "mini-task-id-0001", GpuPriorityLane.InteractiveRetrieval, GpuMiniTaskExecutionState.Ready, null, null);
        AddMiniTask(context, now, "mini-task-id-0002", GpuPriorityLane.DocumentIndexing, GpuMiniTaskExecutionState.Ready, nextDeferredAtUtc, null);
        var activeMiniTaskId = AddMiniTask(context, now, "private-task-idempotency-key", GpuPriorityLane.DocumentIndexing, GpuMiniTaskExecutionState.Active, null, batchId);
        AddMiniTask(context, now, "mini-task-id-0004", GpuPriorityLane.ImageOcr, GpuMiniTaskExecutionState.OutcomeUncertain, null, null);
        context.GpuExecutorDispatches.Add(new GpuExecutorDispatchEntity
        {
            DispatchId = dispatchId,
            BatchId = batchId,
            CapacitySlotKey = "private-dispatch-slot-key",
            OwnerKey = "owner/private",
            ExecutorKey = "private-executor-key",
            AdmissionGeneration = 1,
            State = (int)GpuExecutorDispatchState.ReceiptRecorded,
            AcknowledgedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.GpuExecutorResultReceipts.Add(new GpuExecutorResultReceiptEntity
        {
            OperationId = receiptOperationId,
            DispatchId = dispatchId,
            BatchId = batchId,
            MiniTaskId = activeMiniTaskId,
            ExecutorKey = "private-executor-key",
            AdmissionGeneration = 1,
            Disposition = (int)GpuMiniTaskBoundaryDisposition.Completed,
            EvidenceClass = (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed,
            OpaqueResultDigest = privateDigest,
            RequestFingerprint = "private-receipt-fingerprint",
            CreatedAtUtc = now
        });
        context.GpuExecutorEvidence.Add(new GpuExecutorEvidenceEntity
        {
            OperationId = evidenceOperationId,
            DispatchId = dispatchId,
            BatchId = batchId,
            CapacitySlotKey = "private-dispatch-slot-key",
            ExecutorKey = "private-executor-key",
            AdmissionGeneration = 1,
            EvidenceClass = (int)GpuExecutorEvidenceClass.TaskOutcomeConfirmed,
            VerifierKey = "private-verifier-key",
            ObservedAtUtc = now,
            RequestFingerprint = "private-evidence-fingerprint",
            CreatedAtUtc = now
        });
        var scheduler = await context.GpuSchedulerStates.SingleAsync(state => state.Id == 1);
        scheduler.NextDeferredAtUtc = nextDeferredAtUtc;
        scheduler.UpdatedAtUtc = now;
        await context.SaveChangesAsync();
    }

    private static async Task ClearSchedulerStatusAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        await context.GpuSchedulerOperationReceipts.ExecuteDeleteAsync();
        await context.GpuExecutorEvidence.ExecuteDeleteAsync();
        await context.GpuExecutorResultReceipts.ExecuteDeleteAsync();
        await context.GpuExecutorDispatches.ExecuteDeleteAsync();
        await context.GpuMiniTasks.ExecuteDeleteAsync();
        await context.GpuCapacitySlots
            .Where(slot => slot.ActiveBatchId != null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(slot => slot.ActiveBatchId, (Guid?)null));
        await context.GpuBatches.ExecuteDeleteAsync();
        await context.GpuCapacitySlots.ExecuteDeleteAsync();
        var scheduler = await context.GpuSchedulerStates.SingleAsync(candidate => candidate.Id == 1);
        scheduler.WakeGeneration = 0;
        scheduler.PendingWakeReasons = 0;
        scheduler.NextDeferredAtUtc = null;
        scheduler.UpdatedAtUtc = DateTimeOffset.UnixEpoch;
        await context.SaveChangesAsync();
    }

    private static Guid AddMiniTask(
        FluxKnowledgeDbContext context,
        DateTimeOffset now,
        string idempotencyKey,
        GpuPriorityLane lane,
        GpuMiniTaskExecutionState executionState,
        DateTimeOffset? deferredUntilUtc,
        Guid? batchId)
    {
        var sourceId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = sourceId,
            SourceKind = "local file",
            StableKey = idempotencyKey == "mini-task-id-0001"
                ? "C:\\private\\input.txt"
                : $"C:\\private\\{idempotencyKey}.txt",
            CreatedAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId,
            SourceIdentityId = sourceId,
            Revision = 1,
            ContentHash = new string('a', 64),
            RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Extract,
            RegisteredAtUtc = now
        });
        context.Jobs.Add(new JobEntity
        {
            Id = jobId,
            PipelineRecordId = recordId,
            SourceRevision = 1,
            Stage = (int)PipelineStage.Extract,
            Operation = PipelineOperations.ExtractUtf8,
            PublicState = (int)PublicJobState.GpuQueued,
            DueAtUtc = now,
            ErrorDetails = "private exception text"
        });
        var miniTaskId = Guid.NewGuid();
        context.GpuMiniTasks.Add(new GpuMiniTaskEntity
        {
            Id = miniTaskId,
            ParentJobId = jobId,
            SourceRevision = 1,
            PriorityLane = (int)lane,
            ModelRuntimeKey = "runtime/private",
            SettingsFingerprint = "settings/private",
            EstimatedBytes = 100,
            IdempotencyKey = idempotencyKey,
            HandoffLeaseOwner = "owner/private",
            ExecutionState = (int)executionState,
            DeferredUntilUtc = deferredUntilUtc,
            BatchId = batchId,
            CreatedAtUtc = now
        });
        return miniTaskId;
    }

    private sealed class FixedRecoveryStatus : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = new(
            DerivedIndexRecoveryState.Healthy,
            null,
            null,
            null,
            null,
            0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestDbContextFactory(string connectionString)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
    }
}
