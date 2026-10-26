using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;

namespace FluxKnowledge.Application.Sources;

public sealed class SourceWorkScanner(SourceScanWorker ordinary, SourceVerificationWorker verification) : ISourceScanner
{
    public ValueTask<SourceScanResult> ScanAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct) =>
        request.IsVerification ? verification.ScanAsync(root, request, ct) : ordinary.ScanAsync(root, request, ct);
}

public sealed class SourceVerificationWorker(ISourceVerificationExecutionStore store, ISourceVerificationReaderSlot slot,
    ISourceVerificationContentReader content, ISourceVerificationRetainedStore retained,
    ISourceRetainedObservationStore observations, TimeProvider time) : ISourceScanner
{
    public async ValueTask<SourceScanResult> ScanAsync(SourceRootConfiguration root, SourceScanRequest request, CancellationToken ct)
    {
        var result = new SourceScanResult(root.Id, request.Id, 0, 0, 0, 0) { IsCompleteInventory = false };
        if (!request.IsVerification || request.VerificationRunId is null) throw new InvalidOperationException("SourceVerificationRequestInvalid");
        var batch = await store.PrepareAsync(root, request, ct).ConfigureAwait(false);
        if (batch is null) return result;
        using var readerSlot = slot.TryAcquire();
        using var budget = new SourceVerificationReadBudget(batch.Policy.MaximumBytesPerSecond, time, async token =>
        { if (!await store.MayReadAsync(batch, token).ConfigureAwait(false)) throw new YieldException(); });
        var guarded = new GuardedBudget(budget, () => store.MayReadAsync(batch, ct));
        var cache = batch.PriorReceipts.GroupBy(x => (x.Source.Sha256, x.Source.Observation.ByteLength))
            .ToDictionary(group => group.Key, group => group.ToList());
        var started = time.GetTimestamp();
        string? reason = readerSlot is null ? "reader-busy" : null;
        try
        {
            if (readerSlot is null) return result;
            for (var index = 0; index < batch.Items.Count; index++)
            {
                if (index > 0)
                {
                    if (budget.SourceBytesRead + budget.RetainedBytesRead >= 64L * 1024 * 1024 ||
                        time.GetElapsedTime(started) >= TimeSpan.FromSeconds(30)) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(100), time, ct).ConfigureAwait(false);
                }
                if (!await store.MayReadAsync(batch, ct).ConfigureAwait(false)) throw new YieldException();
                var item = batch.Items[index];
                var binding = item.Binding;
                var sourceHashed = false;
                try
                {
                    if (binding.Source is null || binding.Sha256 is null || binding.StoreRelativePath is null ||
                        binding.RevisionId is null || binding.ArtifactId is null)
                    {
                        await store.DeferAsync(batch, item, "capture-proof-required", true, ct).ConfigureAwait(false);
                        continue;
                    }
                    var source = await content.ReadAsync(root, binding.RelativePath, guarded, ct).ConfigureAwait(false);
                    sourceHashed = true;
                    if (source.Sha256 != binding.Sha256 || source.BytesRead != binding.ByteLength ||
                        source.Observation.Identity != binding.Source.Identity || source.Observation.FileId != binding.FileId)
                    {
                        await store.DeferAsync(batch, item, "source-content-changed", true, ct).ConfigureAwait(false);
                        continue;
                    }
                    var checkedBlob = await ReadRetainedAsync(binding, cache, guarded, ct).ConfigureAwait(false);
                    await using var blob = checkedBlob.Lease;
                    if (!await store.CheckpointAsync(batch, item, source, blob, checkedBlob.HashedAt,
                        budget.SourceBytesRead, budget.RetainedBytesRead, ct).ConfigureAwait(false))
                    { reason = "candidate-changed"; break; }
                    var key = (source.Sha256, source.Observation.ByteLength);
                    if (!cache.TryGetValue(key, out var receipts)) cache[key] = receipts = [];
                    receipts.Add(new(source, blob.Observation, checkedBlob.HashedAt));
                }
                catch (YieldException) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (SourceJournalGapException)
                { await store.DeferAsync(batch, item, "journal-history-lost", false, ct).ConfigureAwait(false); }
                catch (SourceSnapshotChangedException)
                { await store.DeferAsync(batch, item, "source-observation-changed", true, ct).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { await store.DeferAsync(batch, item, sourceHashed ? "retained-or-journal-unavailable" : "source-unavailable", !sourceHashed, ct).ConfigureAwait(false); }
            }
        }
        catch (YieldException) { reason = "foreground-or-policy"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { reason = "cancelled"; throw; }
        finally
        {
            // Every reader awaits native terminal cleanup before unwinding here. The physical slot
            // remains held through this bounded checkpoint and is never released by SQL session loss.
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.FinishBatchAsync(batch, budget.SourceBytesRead, budget.RetainedBytesRead, reason, finish.Token).ConfigureAwait(false);
        }
        return result;
    }

    private async ValueTask<(IRetainedFileObservationLease Lease, DateTimeOffset HashedAt)> ReadRetainedAsync(
        SourceVerificationBinding binding, Dictionary<(string, long), List<SourceVerificationReceipt>> cache,
        ISourceVerificationReadBudget budget, CancellationToken ct)
    {
        if (cache.TryGetValue((binding.Sha256!, binding.ByteLength), out var receipts))
        {
            var metadata = await observations.OpenObservationAsync(binding.StoreRelativePath!, binding.Sha256!, binding.ByteLength, false, ct).ConfigureAwait(false);
            var cached = receipts.FirstOrDefault(x => x.Retained == metadata.Observation);
            if (cached is not null) return (metadata, cached.RetainedHashedAtUtc);
            await metadata.DisposeAsync().ConfigureAwait(false);
        }
        var verified = await retained.OpenVerificationObservationAsync(binding.StoreRelativePath!, binding.Sha256!, binding.ByteLength, budget, ct).ConfigureAwait(false);
        if (verified.ChecksumVerifiedAtUtc is { } hashedAt) return (verified, hashedAt);
        await verified.DisposeAsync().ConfigureAwait(false);
        throw new InvalidDataException("SourceVerificationRetainedHashTimeMissing");
    }

    private sealed class YieldException : Exception { }
    private sealed class GuardedBudget(SourceVerificationReadBudget budget, Func<ValueTask<bool>> mayRead) : ISourceVerificationReadBudget
    {
        public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
        {
            await budget.AcquireAsync(bytes, ct).ConfigureAwait(false);
            if (!await mayRead().ConfigureAwait(false)) throw new YieldException();
        }
        public void RecordRead(SourceVerificationReadKind kind, int bytes) => budget.RecordRead(kind, bytes);
    }
}
