using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Infrastructure.Inference.Search;

public enum ExpandedGpuRankSettlement { Settled, RetainedUncertain }

public sealed record ExpandedGpuRankOutcome(IReadOnlyDictionary<Guid, RerankResult> Results,
    ExpandedGpuRankSettlement Settlement, Exception? Failure);

/// <summary>The backend owns admission, the native lease and positive durable settlement.</summary>
public interface IExpandedGpuRankBackend
{
    string RerankerFingerprint { get; }
    Task<ExpandedGpuRankOutcome> RunAsync(Guid cohortId,
        Func<IPassageReranker, CancellationToken, ValueTask<IReadOnlyDictionary<Guid, RerankResult>>> score,
        CancellationToken cancellationToken);
}

/// <summary>One live cohort, at most two whole requests and four bounded model calls.</summary>
public sealed class ExpandedGpuRankCoordinator : IExpandedPassageRanker, IAsyncDisposable
{
    private readonly IExpandedGpuRankBackend _backend;
    private readonly TimeSpan _readinessTimeout;
    private readonly TimeProvider _clock;
    private readonly string _fingerprint;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Cohort? _active;
    private Task? _shutdownTask;
    private bool _closed, _uncertain;

    public ExpandedGpuRankCoordinator(IExpandedGpuRankBackend backend, TimeSpan? readinessTimeout = null,
        TimeProvider? clock = null)
    {
        _backend = backend;
        _fingerprint = backend.RerankerFingerprint;
        if (string.IsNullOrWhiteSpace(_fingerprint)) throw new ArgumentException("staging-rank-profile-missing", nameof(backend));
        _readinessTimeout = readinessTimeout ?? TimeSpan.FromSeconds(6);
        if (_readinessTimeout <= TimeSpan.Zero || _readinessTimeout > TimeSpan.FromSeconds(6))
            throw new ArgumentOutOfRangeException(nameof(readinessTimeout));
        _clock = clock ?? TimeProvider.System;
    }

    public string RerankerFingerprint => _fingerprint;

    public IExpandedPassageRankTicket Reserve(DateTimeOffset apiArrivalUtc, string traceId, string spanId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _clock.GetUtcNow();
        // The cutoff belongs to the original API request, never to a later submit or model load.
        if (apiArrivalUtc > now + TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(apiArrivalUtc));
        var readyBy = apiArrivalUtc + _readinessTimeout;
        if (readyBy <= now) throw ReadinessRefusal("payload-admission-expired");
        lock (_sync)
        {
            if (_closed || _uncertain) throw new PassageRetrievalRefusalException("unavailable");
            if (_active is { } current && (current.Sealed || current.Clients.Count == 2))
                throw new PassageRetrievalRefusalException("busy");
            var start = _active is null;
            var cohort = _active ??= new Cohort(now, _shutdown.Token, _clock);
            var client = new Client(cohort, readyBy < cohort.Deadline ? readyBy : cohort.Deadline,
                cancellationToken, traceId, spanId);
            cohort.Clients.Add(client);
            client.Registration = cancellationToken.Register(() => Cancel(client));
            Pulse(cohort);
            if (start) cohort.Work = Task.Run(() => RunAsync(cohort), CancellationToken.None);
            return new Ticket(this, client);
        }
    }

    private ValueTask<RerankResult> Submit(Client client, string query, IReadOnlyList<RerankPassage> passages)
    {
        ArgumentNullException.ThrowIfNull(passages);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2048 || passages.Count is < 1 or > 100)
            throw new ArgumentException("staging-rank-payload-invalid");
        var frozen = passages.ToArray();
        if (frozen.Length is < 1 or > 100 ||
            frozen.Any(p => p is null || p.PassageId <= 0 || string.IsNullOrWhiteSpace(p.SearchText) || p.SearchText.Length > 16384) ||
            frozen.Select(p => p.PassageId).Distinct().Count() != frozen.Length)
            throw new ArgumentException("staging-rank-payload-invalid");
        lock (_sync)
        {
            if (client.Submitted) throw new InvalidOperationException("staging-rank-already-submitted");
            client.Submitted = true;
            if (client.Cancelled || client.Result.Task.IsCompleted) return new(client.Result.Task);
            if (_clock.GetUtcNow() >= client.ReadyBy)
            {
                client.Failure = ReadinessRefusal("payload-readiness");
                client.Done = true;
            }
            else { client.Query = query; client.Pairs = frozen; }
            Pulse(client.Cohort);
            return new(client.Result.Task);
        }
    }

    private void Cancel(Client client)
    {
        lock (_sync)
        {
            client.Cancelled = true;
            Pulse(client.Cohort);
            // A client never cancels the shared executor. The entered callback skips cancelled
            // clients and disposes normally, including when all clients have cancelled.
        }
    }

    private async Task RunAsync(Cohort cohort)
    {
        ExpandedGpuRankOutcome outcome;
        try
        {
            outcome = await _backend.RunAsync(cohort.Id, (ranker, ct) => ScoreOnceAsync(cohort, ranker, ct), cohort.Owner.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // An unexpected backend exit is not proof that queued cleanup or native ownership settled.
            outcome = new(new Dictionary<Guid, RerankResult>(), ExpandedGpuRankSettlement.RetainedUncertain, exception);
        }
        Client[] clients;
        lock (_sync)
        {
            cohort.Sealed = true;
            cohort.CallbackClosed = true;
            _uncertain |= outcome.Settlement == ExpandedGpuRankSettlement.RetainedUncertain;
            clients = cohort.Clients.ToArray();
            foreach (var client in clients)
            {
                client.Query = null;
                client.Pairs = null;
                if (_uncertain) client.Result.TrySetException(new PassageInferenceOwnershipException(outcome.Failure));
                else if (client.Cancelled) client.Result.TrySetCanceled(client.CallerToken.IsCancellationRequested
                    ? client.CallerToken : new CancellationToken(true));
                else if (client.Failure is { } failure) client.Result.TrySetException(failure);
                else if (outcome.Failure is { } ownerFailure) client.Result.TrySetException(ownerFailure);
                else if (outcome.Results.TryGetValue(client.Id, out var result)) client.Result.TrySetResult(result);
                else client.Result.TrySetException(new PassageRetrievalRefusalException("unavailable"));
            }
        }
        foreach (var client in clients) client.Registration.Dispose();
        cohort.Owner.Dispose();
        cohort.DeadlineSource.Dispose();
        // This is reached only after actual backend settlement and local registration cleanup.
        lock (_sync) { _active = null; }
    }

    private async ValueTask<IReadOnlyDictionary<Guid, RerankResult>> ScoreOnceAsync(Cohort cohort,
        IPassageReranker ranker, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (cohort.CallbackEntered || cohort.CallbackClosed)
                throw new InvalidOperationException("staging-rank-callback-closed");
            cohort.CallbackEntered = true;
        }
        try { return await ScoreAsync(cohort, ranker, cancellationToken).ConfigureAwait(false); }
        finally { lock (_sync) { cohort.CallbackClosed = true; } }
    }

    private async ValueTask<IReadOnlyDictionary<Guid, RerankResult>> ScoreAsync(Cohort cohort,
        IPassageReranker ranker, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, RerankResult>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Client? next;
            Task changed;
            TimeSpan wait;
            lock (_sync)
            {
                var now = _clock.GetUtcNow();
                foreach (var client in cohort.Clients.Where(c => !c.Done && c.Pairs is null && !c.Cancelled && now >= c.ReadyBy))
                { client.Done = true; client.Failure = ReadinessRefusal("payload-readiness"); }
                next = cohort.Clients.FirstOrDefault(c => !c.Cancelled && !c.Done && c.Pairs is not null);
                if (next is null && cohort.Clients.All(c => c.Done || c.Cancelled))
                { cohort.Sealed = true; break; }
                changed = cohort.Changed.Task;
                wait = cohort.Clients.Where(c => !c.Done && !c.Cancelled && c.Pairs is null)
                    .Select(c => c.ReadyBy - now).DefaultIfEmpty(TimeSpan.FromMilliseconds(1)).Min();
            }
            if (next is null)
            {
                try { await changed.WaitAsync(wait, _clock, cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
                continue;
            }
            var scores = new List<RerankScore>(next.Pairs!.Length);
            foreach (var batch in next.Pairs.Chunk(50))
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync) { if (next.Cancelled) break; }
                var result = await ranker.RerankAsync(next.Query!, batch, cancellationToken).ConfigureAwait(false);
                if (result.ModelFingerprint != RerankerFingerprint || result.Scores.Count != batch.Length ||
                    !result.Scores.Select(s => s.PassageId).SequenceEqual(batch.Select(p => p.PassageId)) ||
                    result.Scores.Any(s => !float.IsFinite(s.Logit)))
                    throw new InvalidOperationException("staging-rank-output-invalid");
                scores.AddRange(result.Scores);
            }
            lock (_sync)
            {
                if (!next.Cancelled) results.Add(next.Id, new(scores.ToArray(), RerankerFingerprint));
                next.Query = null;
                next.Pairs = null;
                next.Done = true;
                // Joining is allowed through the first complete logical request, not after it.
                cohort.Sealed = true;
                Pulse(cohort);
            }
        }
        return results;
    }

    private static void Pulse(Cohort cohort)
    {
        var old = cohort.Changed;
        cohort.Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    private static PassageRetrievalRefusalException ReadinessRefusal(string phase)
    {
        var refusal=new PassageRetrievalRefusalException("timeout");
        refusal.Data["staging.rank.phase"]=phase;
        return refusal;
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _closed = true;
            return new(_shutdownTask ??= ShutdownAsync(_active?.Work));
        }
    }

    private async Task ShutdownAsync(Task? work)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (work is not null) await work.ConfigureAwait(false);
        _shutdown.Dispose();
        if (_uncertain) throw new PassageInferenceOwnershipException();
    }

    private sealed class Ticket(ExpandedGpuRankCoordinator owner, Client client) : IExpandedPassageRankTicket
    {
        public Guid ClientId => client.Id;
        public Guid CohortId => client.Cohort.Id;
        public ValueTask<RerankResult> SubmitAsync(string query, IReadOnlyList<RerankPassage> passages) => owner.Submit(client, query, passages);
        public async ValueTask DisposeAsync()
        {
            owner.Cancel(client);
            try { await client.Result.Task.ConfigureAwait(false); }
            catch (PassageInferenceOwnershipException) { throw; }
            catch (Exception) { /* The submitted result reports the original outcome; closing still joins settlement. */ }
        }
    }

    private sealed class Cohort
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly DateTimeOffset Deadline;
        internal readonly CancellationTokenSource DeadlineSource, Owner;
        internal readonly List<Client> Clients = [];
        internal TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Work;
        internal bool Sealed, CallbackEntered, CallbackClosed;
        internal Cohort(DateTimeOffset start, CancellationToken shutdown, TimeProvider clock)
        {
            // Timer is absolute to cohort creation and never reset by a joining client.
            Deadline = start + TimeSpan.FromSeconds(20);
            DeadlineSource = new(TimeSpan.FromSeconds(20), clock);
            Owner = CancellationTokenSource.CreateLinkedTokenSource(shutdown, DeadlineSource.Token);
        }
    }

    private sealed class Client(Cohort cohort, DateTimeOffset readyBy, CancellationToken callerToken, string traceId, string spanId)
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly Cohort Cohort = cohort;
        internal readonly DateTimeOffset ReadyBy = readyBy;
        internal readonly CancellationToken CallerToken = callerToken;
        internal readonly string TraceId = traceId, SpanId = spanId;
        internal readonly TaskCompletionSource<RerankResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal bool Submitted, Cancelled, Done;
        internal string? Query;
        internal RerankPassage[]? Pairs;
        internal Exception? Failure;
    }
}
