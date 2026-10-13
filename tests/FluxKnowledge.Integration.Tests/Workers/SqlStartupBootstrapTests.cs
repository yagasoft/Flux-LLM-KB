using System.Data.Common;
using System.Reflection;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Workers;

public sealed class SqlStartupBootstrapTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Read_only_probe_targets_the_disposable_database_without_mutating_capacity()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        await SqlDatabaseStartupReadiness.ProbeOnceAsync(SqlTestData.CreateFactory(fixture), CancellationToken.None);
        await using var check = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Empty(await check.GpuCapacitySlots.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Concurrent_slot_creation_reuses_only_the_freshly_proved_winning_row()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var barrier = new ConcurrentInsertBarrier();
        var factory = new ContextFactory(fixture.ConnectionString, barrier);
        await using var provider = Provider(factory);
        PaddleOcrVlmCapacityBootstrapService Bootstrap() => new(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        await Task.WhenAll(Bootstrap().PrepareCapacityAsync(CancellationToken.None),
            Bootstrap().PrepareCapacityAsync(CancellationToken.None));

        Assert.Equal(2, barrier.Inserts);
        Assert.Equal(3, factory.Created);
        await using var check = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        var slot = Assert.Single(await check.GpuCapacitySlots.ToArrayAsync());
        Assert.Equal(PaddleOcrVlmRuntimeContract.CapacitySlotKey, slot.SlotKey);
        Assert.Equal((int)GpuCapacitySlotState.Available, slot.State);
    }

    [NativeSqlServerTheory]
    [InlineData(GpuCapacitySlotState.Reserved)]
    [InlineData(GpuCapacitySlotState.Uncertain)]
    public async Task Existing_reserved_or_uncertain_slot_is_preserved_byte_for_byte(GpuCapacitySlotState state)
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        byte[] originalVersion;
        var originalTime = DateTimeOffset.Parse("2026-08-11T08:00:00Z");
        await using (var setup = await factory.CreateDbContextAsync())
        {
            var slot = new GpuCapacitySlotEntity { SlotKey=PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                State=(int)state, OwnerKey="retained-owner", UpdatedAtUtc=originalTime, LastHeartbeatAtUtc=originalTime };
            setup.GpuCapacitySlots.Add(slot);
            await setup.SaveChangesAsync();
            originalVersion = slot.RowVersion.ToArray();
        }
        await using var provider = Provider(factory);
        await new PaddleOcrVlmCapacityBootstrapService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System)
            .PrepareCapacityAsync(CancellationToken.None);
        await using var check = await factory.CreateDbContextAsync();
        var retained = Assert.Single(await check.GpuCapacitySlots.ToArrayAsync());
        Assert.Equal((int)state, retained.State);
        Assert.Equal("retained-owner", retained.OwnerKey);
        Assert.Equal(originalTime, retained.UpdatedAtUtc);
        Assert.Equal(originalTime, retained.LastHeartbeatAtUtc);
        Assert.Equal(originalVersion, retained.RowVersion);
    }

    [NativeSqlServerFact]
    public async Task Duplicate_claim_without_the_exact_slot_is_refused()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var failure = new DbUpdateException("synthetic-duplicate", SqlFailure(2627));
        var factory = new ContextFactory(fixture.ConnectionString, new FailingInsert(failure));
        await using var provider = Provider(factory);
        var bootstrap = new PaddleOcrVlmCapacityBootstrapService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => bootstrap.PrepareCapacityAsync(CancellationToken.None)));
        Assert.Equal(2, factory.Created);
    }

    [NativeSqlServerFact]
    public async Task Failed_fresh_read_after_a_duplicate_claim_cannot_count_as_success()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var readFailure = new IOException("synthetic-fresh-read-unavailable");
        var factory = new ContextFactory(fixture.ConnectionString,
            new FailingInsert(new DbUpdateException("synthetic-duplicate", SqlFailure(2627))), readFailure);
        await using var provider = Provider(factory);
        var bootstrap = new PaddleOcrVlmCapacityBootstrapService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        Assert.Same(readFailure, await Assert.ThrowsAsync<IOException>(() => bootstrap.PrepareCapacityAsync(CancellationToken.None)));
        Assert.Equal(2, factory.Created);
    }

    [NativeSqlServerFact]
    public async Task Admission_before_bootstrap_fails_closed_then_the_same_pending_operation_admits_once()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var factory = SqlTestData.CreateFactory(fixture);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var store = new SqlGpuSchedulerStore(factory, timeProvider:clock, interactiveExecutionTimeout:TimeSpan.FromSeconds(20));
        var instance = Guid.NewGuid();
        var now = clock.GetUtcNow();
        var handoff = new GpuInteractiveHandoffRequest(Guid.NewGuid(), instance, $"retrieval-gpu:{instance:N}",
            "synthetic-retrieval-v1", "synthetic-settings-v1", 10, now.AddSeconds(2), now.AddSeconds(20), false);
        Assert.True((await store.HandoffInteractiveAsync(handoff, CancellationToken.None)).Committed);
        var options = new GpuSchedulerOptions(1, 1024, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10));
        var operation = Guid.NewGuid();
        ValueTask<GpuAdmissionDecision> Decide(GpuBatchCandidate candidate, CancellationToken ct) => ValueTask.FromResult(
            new GpuAdmissionDecision(GpuAdmissionDisposition.Admit, PaddleOcrVlmRuntimeContract.CapacitySlotKey,
                "startup-owner", null, candidate.RequiredExecutorKey));
        Task<GpuSchedulerAdmissionRoundResult> Admit() => store.RunAdmissionRoundAsync(operation,
            GpuSchedulerWakeReason.StartupRecovery, options, Decide, CancellationToken.None).AsTask();

        await Assert.ThrowsAsync<InvalidOperationException>(Admit);
        await using (var before = await factory.CreateDbContextAsync())
        {
            Assert.Empty(await before.GpuCapacitySlots.ToArrayAsync());
            Assert.Empty(await before.GpuBatches.ToArrayAsync());
            Assert.Empty(await before.GpuExecutorDispatches.ToArrayAsync());
        }
        await using var provider = Provider(factory);
        await new PaddleOcrVlmCapacityBootstrapService(provider.GetRequiredService<IServiceScopeFactory>(), clock)
            .PrepareCapacityAsync(CancellationToken.None);
        Assert.True((await Admit()).Committed);
        Assert.True((await Admit()).IsIdempotentReplay);
        await using var after = await factory.CreateDbContextAsync();
        Assert.Single(await after.GpuBatches.ToArrayAsync());
        Assert.Single(await after.GpuExecutorDispatches.ToArrayAsync());
        Assert.Equal((int)GpuCapacitySlotState.Reserved, (await after.GpuCapacitySlots.SingleAsync()).State);
    }

    [NativeSqlServerFact]
    public async Task Capacity_bootstrap_does_not_accept_a_non_duplicate_write_failure_as_success()
    {
        await SqlTestData.ClearPipelineAsync(fixture);
        var failure = new DbUpdateException("synthetic-permission-or-write-failure");
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<FluxKnowledgeDbContext>>(
            new ContextFactory(fixture.ConnectionString, new FailingInsert(failure)));
        await using var provider = services.BuildServiceProvider();
        var bootstrap = new PaddleOcrVlmCapacityBootstrapService(
            provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(
            () => bootstrap.PrepareCapacityAsync(CancellationToken.None)));
        await using var check = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        Assert.Empty(await check.GpuCapacitySlots.ToArrayAsync());
    }

    private static ServiceProvider Provider(IDbContextFactory<FluxKnowledgeDbContext> factory) =>
        new ServiceCollection().AddSingleton(factory).BuildServiceProvider();

    private sealed class ContextFactory(string connection, DbCommandInterceptor interceptor, Exception? freshReadFailure = null)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private int _created;
        public int Created => Volatile.Read(ref _created);
        public FluxKnowledgeDbContext CreateDbContext()
        {
            if (Interlocked.Increment(ref _created) > 1 && freshReadFailure is not null) throw freshReadFailure;
            return new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection)
                .AddInterceptors(interceptor).Options);
        }
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ConcurrentInsertBarrier : DbCommandInterceptor
    {
        private int _inserts;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Inserts => Volatile.Read(ref _inserts);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [GpuCapacitySlots]", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _inserts) == 2) _both.TrySetResult();
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            return result;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static SqlException SqlFailure(int number)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
            [number, (byte)0, (byte)14, "synthetic-server", "synthetic", string.Empty, 1, 0, null], null)!;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2)
            .Invoke(null, [errors, "synthetic-server"])!;
    }

    private sealed class FailingInsert(Exception failure) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO [GpuCapacitySlots]", StringComparison.Ordinal))
                throw failure;
            return ValueTask.FromResult(result);
        }
    }
}
