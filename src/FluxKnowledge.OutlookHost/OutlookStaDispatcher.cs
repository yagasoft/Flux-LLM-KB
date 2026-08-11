using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FluxKnowledge.OutlookHost;

/// <summary>
/// Owns one dedicated STA thread and pumps Windows messages between queued operations. All Outlook
/// RCW activation, use, event delivery and final release are dispatched through this owner.
/// </summary>
internal sealed class OutlookStaDispatcher : IAsyncDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _signal = new(initialState: false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _disposed;
    private int _ownerThreadId;

    public OutlookStaDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "FluxKnowledge Outlook STA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async ValueTask<T> InvokeAsync<T>(
        Func<ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            return await operation();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() => ExecuteAsync(operation, completion, cancellationToken));
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask InvokeAsync(
        Func<ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        await InvokeAsync(
            async () =>
            {
                await operation();
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _ready.Task.ConfigureAwait(false);
        _stopping = true;
        _signal.Set();
        await _completed.Task.ConfigureAwait(false);
        _signal.Dispose();
    }

    internal void Post(Action callback) => Enqueue(callback);

    private void Enqueue(Action callback)
    {
        if (_stopping)
        {
            throw new ObjectDisposedException(nameof(OutlookStaDispatcher));
        }

        _queue.Enqueue(callback);
        _signal.Set();
    }

    private async void ExecuteAsync<T>(
        Func<ValueTask<T>> operation,
        TaskCompletionSource<T> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            completion.TrySetResult(await operation());
        }
        catch (OperationCanceledException exception) when (exception.CancellationToken == cancellationToken)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void Run()
    {
        try
        {
            _ownerThreadId = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(new StaSynchronizationContext(this, _ownerThreadId));
            _ready.TrySetResult();
            while (!_stopping)
            {
                DrainQueue();
                PumpMessages();
                _signal.WaitOne(TimeSpan.FromMilliseconds(10));
            }

            DrainQueue();
            PumpMessages();
            _completed.TrySetResult();
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            _completed.TrySetException(exception);
        }
    }

    private void DrainQueue()
    {
        while (_queue.TryDequeue(out var callback))
        {
            callback();
        }
    }

    private static void PumpMessages()
    {
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
        {
            _ = TranslateMessage(in message);
            _ = DispatchMessage(in message);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimum,
        uint maximum,
        uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(in NativeMessage message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private sealed class StaSynchronizationContext(
        OutlookStaDispatcher dispatcher,
        int ownerThreadId) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            dispatcher.Post(() => callback(state));

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Environment.CurrentManagedThreadId == ownerThreadId)
            {
                callback(state);
                return;
            }

            using var completed = new ManualResetEventSlim();
            Exception? failure = null;
            dispatcher.Post(() =>
            {
                try
                {
                    callback(state);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    completed.Set();
                }
            });
            completed.Wait();
            if (failure is not null)
            {
                throw new InvalidOperationException("The Outlook STA operation failed.", failure);
            }
        }
    }
}
