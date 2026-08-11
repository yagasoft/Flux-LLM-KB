using FluxKnowledge.Domain.Outlook;

namespace FluxKnowledge.OutlookHost.Tests;

internal sealed class FakeClassicOutlookAdapter : IClassicOutlookAdapter
{
    private readonly IReadOnlyList<OutlookItemEnvelope> _items;
    private readonly OutlookHint? _hintOnSubscribe;
    private readonly TimeSpan _enumerationDelay;
    private Func<OutlookHint, ValueTask>? _onHint;

    public FakeClassicOutlookAdapter(
        IReadOnlyList<OutlookItemEnvelope>? items = null,
        OutlookHint? hintOnSubscribe = null,
        TimeSpan? enumerationDelay = null)
    {
        _items = items ?? [];
        _hintOnSubscribe = hintOnSubscribe;
        _enumerationDelay = enumerationDelay ?? TimeSpan.Zero;
    }

    public int BrowseCount { get; private set; }
    public int EnumerateCount { get; private set; }
    public int ReadCount { get; private set; }
    public OutlookCursor? LastCursor { get; private set; }
    public string? LastReadStoreId { get; private set; }

    public ValueTask<IReadOnlyList<OutlookFolderDescriptor>> BrowseFoldersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BrowseCount++;
        return ValueTask.FromResult<IReadOnlyList<OutlookFolderDescriptor>>(
            [new(new OutlookCaptureFolderId(Guid.Parse("11111111-1111-1111-1111-111111111111")), new OutlookFolderIdentity("store", "folder", "Inbox"))]);
    }

    public async ValueTask<IAsyncDisposable> SubscribeHintsAsync(
        OutlookFolderIdentity folder,
        Func<OutlookHint, ValueTask> onHint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _onHint = onHint;
        if (_hintOnSubscribe is not null)
        {
            await onHint(_hintOnSubscribe);
        }

        return new Subscription();
    }

    public async IAsyncEnumerable<OutlookItemEnvelope> EnumerateAsync(
        OutlookFolderIdentity folder,
        OutlookCursor cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnumerateCount++;
        LastCursor = cursor;
        if (_enumerationDelay > TimeSpan.Zero)
        {
            await Task.Delay(_enumerationDelay, cancellationToken);
        }
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Timestamp(cursor.Basis) >= cursor.FromUtc)
            {
                yield return item;
            }

            await Task.Yield();
        }
    }

    public ValueTask<OutlookMessagePayload> ReadForExportAsync(OutlookItemEnvelope item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        LastReadStoreId = item.StoreId;
        return ValueTask.FromResult(new OutlookMessagePayload(
            "body"u8.ToArray(),
            "text/plain",
            []));
    }

    public ValueTask RaiseHintAsync(OutlookHint hint) =>
        _onHint is null ? ValueTask.CompletedTask : _onHint(hint);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Subscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class CountingAdapterFactory(FakeClassicOutlookAdapter adapter) : IClassicOutlookAdapterFactory
{
    public int ActivationCount { get; private set; }

    public ValueTask<IClassicOutlookAdapter> CreateAsync(
        OutlookComActivationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActivationCount++;
        return ValueTask.FromResult<IClassicOutlookAdapter>(adapter);
    }
}
