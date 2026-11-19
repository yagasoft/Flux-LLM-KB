using System.Diagnostics;
using System.Text.Json;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Domain.Gpu;
using FluxKnowledge.Infrastructure.Inference.Search;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;

/// <summary>Explicit staging command. Runs real DirectML inference through the normal scheduler and physical slot.</summary>
internal static class StagingModelWarmingTrial
{
    internal static void Add(IServiceCollection services, StagingHostSettings settings)
    {
        StagingNormalGpuComposition.Add(services, settings);
        foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) &&
                     service.ImplementationType == typeof(BgeCpuWarmupService)).ToArray())
            services.Remove(service);
    }

    internal static async Task RunAsync(WebApplication app, bool warming)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = bound.Token;
        var factory = app.Services.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
        var slotKey = PaddleOcrVlmRuntimeContract.CapacitySlotKey;
        await using (var seed = await factory.CreateDbContextAsync(ct))
        {
            if (await seed.GpuModelResidencies.AnyAsync(row => row.ReleasedAtUtc == null, ct) ||
                await seed.GpuCapacitySlots.AnyAsync(row => row.State != (int)GpuCapacitySlotState.Available, ct))
                throw new InvalidOperationException("The staging GPU slot is already owned.");
            if (!await seed.GpuCapacitySlots.AnyAsync(row => row.SlotKey == slotKey, ct))
            {
                seed.GpuCapacitySlots.Add(new() { SlotKey = slotKey, State = (int)GpuCapacitySlotState.Available,
                    UpdatedAtUtc = DateTimeOffset.UtcNow });
                await seed.SaveChangesAsync(ct);
            }
        }
        await app.StartAsync(ct);
        try
        {
            var executor = app.Services.GetRequiredService<GpuInteractiveExecutor>();
            var inference = app.Services.GetRequiredService<BgeGpuInferenceSession>();
            GpuInteractiveOperation<FluxKnowledge.Application.Ports.EmbeddingResult> StartEmbedding() =>
                executor.StartOwned(owner => inference.ExecuteSearchAsync(owner,
                    (embed, _, token) => embed.CreateEmbeddingAsync("Staging GPU model lifetime check.", token)),
                    ct, TimeSpan.FromSeconds(6));
            if (!warming)
            {
                var existing = await factory.CreateDbContextAsync(ct);
                await using (existing)
                {
                    Require(await existing.GpuModelDetachmentReceipts.AnyAsync(ct),
                        "Cold compatibility requires retained results from a previous warm trial.");
                }
                var operation = StartEmbedding();
                Require((await operation.Result.WaitAsync(ct)).Values.Count == 1024, "The cold embedding was invalid.");
                var ended = await operation.Settlement.WaitAsync(ct);
                Require(ended is { Kind: GpuInteractiveSettlementKind.Released, NativeCapacityReleased: true },
                    "The disabled warming path did not physically release the allocation.");
                Console.WriteLine(JsonSerializer.Serialize(new { phase = "warming-disabled", settlement = ended.Kind.ToString() }));
                return;
            }
            async Task<TrialResult> EmbedAsync()
            {
                var elapsed = Stopwatch.StartNew();
                var query = StartEmbedding();
                var result = await query.Result.WaitAsync(ct);
                Require(result.Values.Count == 1024, "The native embedding was invalid.");
                return await ReadDetachedAsync(factory, query, elapsed.Elapsed, ct);
            }

            var first = await EmbedAsync();
            Write("first-embedding", first);
            var second = await EmbedAsync();
            Write("warm-embedding", second);
            Require(first.ModelResidency == second.ModelResidency && second.Version > first.Version,
                "Compatible work did not use the same resident model epoch.");

            var different = app.Services.GetRequiredKeyedService<IBgeGpuModelFactory>("expanded-ranking");
            var timer = Stopwatch.StartNew();
            var ranking = executor.StartOwned(async owner =>
            {
                using var lease = await different.OpenRerankerAsync(owner);
                var score = await lease.Model.RerankAsync("staging", [new(1, "staging GPU model")], owner.CancellationToken);
                return new GpuInteractiveNativeResult<int>(score.Scores.Count, owner.NativeCapacityReleased);
            }, ct, TimeSpan.FromSeconds(6), BgeExpandedGpuRankBackend.Profile);
            Require(await ranking.Result.WaitAsync(ct) == 1, "The native reranker result was invalid.");
            var third = await ReadDetachedAsync(factory, ranking, timer.Elapsed, ct);
            Write("incompatible-reranker", third);
            Require(first.ModelResidency != third.ModelResidency, "An incompatible phase reused the old model epoch.");
            await using (var compare = await factory.CreateDbContextAsync(ct))
            {
                var old = await compare.GpuModelResidencies.SingleAsync(row => row.ResidencyId == first.ModelResidency, ct);
                Require(old.ReleaseKind == (int)GpuResidentReleaseKind.NativeSessionDisposed &&
                    old.ReleasedAtUtc <= third.DetachedAtUtc, "Admission raced native disposal.");
            }
            while (true)
            {
                await using var check = await factory.CreateDbContextAsync(ct);
                var row = await check.GpuModelResidencies.SingleAsync(value => value.ResidencyId == third.ModelResidency, ct);
                if (row.ReleasedAtUtc is { } released)
                {
                    var slot = await check.GpuCapacitySlots.SingleAsync(value => value.SlotKey == slotKey, ct);
                    Require(released >= third.DetachedAtUtc.AddMinutes(2) && released <= third.DetachedAtUtc.AddSeconds(122) &&
                        row.ReleaseKind == (int)GpuResidentReleaseKind.NativeSessionDisposed &&
                        slot.State == (int)GpuCapacitySlotState.Available && slot.ActiveBatchId is null,
                        "Expiry did not preserve the two-minute idle lifetime or positively release the slot.");
                    Console.WriteLine(JsonSerializer.Serialize(new { phase = "idle-expired", released }));
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            var beforeFailure = await EmbedAsync();
            var failsBeforeUse = executor.StartOwned<int>(_ => throw new InvalidOperationException("staging-callback-refusal"),
                ct, TimeSpan.FromSeconds(6));
            try { await failsBeforeUse.Result.WaitAsync(ct); }
            catch (InvalidOperationException error) when (error.Message == "interactive-execution-or-release-unconfirmed") { }
            var finished = await failsBeforeUse.Settlement.WaitAsync(ct);
            await using (var check = await factory.CreateDbContextAsync(ct))
            {
                var original = await check.GpuModelResidencies.SingleAsync(value => value.ResidencyId == beforeFailure.ModelResidency, ct);
                Require(original.ReleaseKind == (int)GpuResidentReleaseKind.NativeSessionDisposed &&
                    finished is { Kind: GpuInteractiveSettlementKind.Released, NativeCapacityReleased: true },
                    "A callback that failed before using its warm model left capacity held.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { phase = "callback-refusal", settlement = finished.Kind.ToString() }));
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private static async Task<TrialResult> ReadDetachedAsync<T>(IDbContextFactory<FluxKnowledgeDbContext> factory,
        GpuInteractiveOperation<T> operation, TimeSpan elapsed, CancellationToken ct)
    {
        var settled = await operation.Settlement.WaitAsync(ct);
        if (settled is not { Kind: GpuInteractiveSettlementKind.Detached, NativeCapacityReleased: false, Handle: { } handle })
            throw new InvalidOperationException("The native dispatch did not detach from its retained model.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var receipt = await db.GpuModelDetachmentReceipts.SingleAsync(value => value.DispatchId == handle.DispatchId, ct);
        var occupied = await db.GpuCapacitySlots.AnyAsync(slot => slot.SlotKey == handle.CapacitySlotKey &&
            slot.State == (int)GpuCapacitySlotState.ResidentIdle && slot.ActiveBatchId == null, ct);
        Require(occupied, "The scheduler freed physical capacity with a live model.");
        return new(receipt.ResidencyId, receipt.Version, receipt.DetachedAtUtc, elapsed.TotalMilliseconds);
    }

    private static void Write(string phase, TrialResult evidence) =>
        Console.WriteLine(JsonSerializer.Serialize(new { phase, evidence }));

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private sealed record TrialResult(Guid ModelResidency, long Version, DateTimeOffset DetachedAtUtc, double Milliseconds);
}
