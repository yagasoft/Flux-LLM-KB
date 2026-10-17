using System.Collections.Concurrent;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>
/// Owns the mimalloc-backed tokenizer's native calls for the process lifetime.
/// Native heaps created on departing caller threads retain committed arenas even
/// after tokenizer cleanup. One stable thread lets those arenas be reused.
/// </summary>
internal static class ProcessTokenizerNativeThread
{
    internal const int QueueCapacity = 16;
    private static readonly Lazy<Owner> Shared = new(() => new Owner());
    internal static int PendingOperations => Shared.Value.PendingOperations;

    internal static T Invoke<T>(Func<T> operation) => Shared.Value.Invoke(operation);

    internal static void Invoke(Action operation) => Invoke(() =>
    {
        operation();
        return true;
    });

    private sealed class Owner
    {
        // Backpressure bounds pending native work; cleanup is never dropped.
        private readonly BlockingCollection<Action> _requests = new(QueueCapacity);
        private readonly Thread _thread;
        internal int PendingOperations => _requests.Count;

        internal Owner()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FluxKnowledge tokenizer native owner"
            };
            if (ExecutionContext.IsFlowSuppressed()) _thread.Start();
            else
            {
                using var suppressed = ExecutionContext.SuppressFlow();
                _thread.Start();
            }
        }

        internal T Invoke<T>(Func<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (Thread.CurrentThread == _thread) return operation();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(() =>
            {
                try { completion.SetResult(operation()); }
                catch (Exception failure) { completion.SetException(failure); }
            });
            // The operation keeps its leases until completion, including when a
            // caller's request is cancelled while native work is in progress.
            return completion.Task.GetAwaiter().GetResult();
        }

        private void Run()
        {
            foreach (var operation in _requests.GetConsumingEnumerable()) operation();
        }
    }
}
