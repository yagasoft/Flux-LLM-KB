using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Domain.Outlook;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace FluxKnowledge.OutlookHost;

internal interface IClassicOutlookAdapterFactory
{
    ValueTask<IClassicOutlookAdapter> CreateAsync(
        OutlookComActivationContext context,
        CancellationToken cancellationToken);
}

internal interface IClassicOutlookComActivator
{
    IClassicOutlookAdapter Activate();
}

internal sealed record OutlookComActivationContext(
    bool IsWindows,
    bool IsInteractiveSession,
    bool HasSessionSingleton,
    FluxKnowledge.Application.Contracts.OutlookHostIdentity Host,
    OutlookHostCatchUpWork? DurableWork,
    FluxKnowledge.Application.Contracts.OutlookBrowseClaim? BrowseClaim,
    DateTimeOffset ObservedAtUtc);

internal interface IClassicOutlookAdapter : IAsyncDisposable
{
    ValueTask<IReadOnlyList<OutlookFolderDescriptor>> BrowseFoldersAsync(string targetPath, CancellationToken cancellationToken);

    ValueTask<IAsyncDisposable> SubscribeHintsAsync(
        OutlookFolderIdentity folder,
        Func<OutlookHint, ValueTask> onHint,
        CancellationToken cancellationToken);

    IAsyncEnumerable<OutlookItemEnvelope> EnumerateAsync(
        OutlookFolderIdentity folder,
        OutlookCursor cursor,
        CancellationToken cancellationToken);

    ValueTask<OutlookMessagePayload> ReadForExportAsync(
        OutlookItemEnvelope item,
        CancellationToken cancellationToken);
}

internal sealed class GatedClassicOutlookAdapterFactory(
    IClassicOutlookComActivator activator,
    OutlookStaDispatcher dispatcher)
    : IClassicOutlookAdapterFactory
{
    public ValueTask<IClassicOutlookAdapter> CreateAsync(
        OutlookComActivationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var work = context.DurableWork;
        var hasCatchUpClaim = work is not null &&
            work.IsDurablyEnabled &&
            work.Folders.Count > 0 &&
            work.Claim.LeaseOwner == context.Host &&
            work.Claim.LeaseExpiresAtUtc > context.ObservedAtUtc;
        var hasBrowseClaim = context.BrowseClaim is not null &&
            context.BrowseClaim.Host == context.Host &&
            context.BrowseClaim.LeaseExpiresAtUtc > context.ObservedAtUtc;
        if (!context.IsWindows ||
            !context.IsInteractiveSession ||
            !context.HasSessionSingleton ||
            (!hasCatchUpClaim && !hasBrowseClaim))
        {
            throw new InvalidOperationException("Classic Outlook COM activation requires an enabled fenced host claim in the current interactive session.");
        }

        return CreatePinnedAsync(cancellationToken);

        async ValueTask<IClassicOutlookAdapter> CreatePinnedAsync(CancellationToken token)
        {
            var inner = await dispatcher.InvokeAsync(
                () => ValueTask.FromResult(activator.Activate()),
                token).ConfigureAwait(false);
            return new DispatcherClassicOutlookAdapter(inner, dispatcher);
        }
    }
}

internal sealed class DispatcherClassicOutlookAdapter(
    IClassicOutlookAdapter inner,
    OutlookStaDispatcher dispatcher) : IClassicOutlookAdapter
{
    public ValueTask<IReadOnlyList<OutlookFolderDescriptor>> BrowseFoldersAsync(string targetPath, CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => inner.BrowseFoldersAsync(targetPath, cancellationToken),
            cancellationToken);

    public async ValueTask<IAsyncDisposable> SubscribeHintsAsync(
        OutlookFolderIdentity folder,
        Func<OutlookHint, ValueTask> onHint,
        CancellationToken cancellationToken)
    {
        var subscription = await dispatcher.InvokeAsync(
            () => inner.SubscribeHintsAsync(folder, onHint, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return new DispatcherSubscription(subscription, dispatcher);
    }

    public async IAsyncEnumerable<OutlookItemEnvelope> EnumerateAsync(
        OutlookFolderIdentity folder,
        OutlookCursor cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var items = await dispatcher.InvokeAsync(
            async () =>
            {
                var collected = new List<OutlookItemEnvelope>();
                await foreach (var item in inner.EnumerateAsync(folder, cursor, cancellationToken))
                {
                    collected.Add(item);
                }

                return collected;
            },
            cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
        {
            yield return item;
        }
    }

    public ValueTask<OutlookMessagePayload> ReadForExportAsync(
        OutlookItemEnvelope item,
        CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(
            () => inner.ReadForExportAsync(item, cancellationToken),
            cancellationToken);

    public ValueTask DisposeAsync() => dispatcher.InvokeAsync(inner.DisposeAsync);

    private sealed class DispatcherSubscription(
        IAsyncDisposable innerSubscription,
        OutlookStaDispatcher dispatcher) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispatcher.InvokeAsync(innerSubscription.DisposeAsync);
    }
}

internal sealed class ClassicOutlookComActivator : IClassicOutlookComActivator
{
    public IClassicOutlookAdapter Activate()
    {
        Outlook.Application? application = null;
        try
        {
            application = new Outlook.Application();
            var adapter = new ClassicOutlookComAdapter(application);
            application = null;
            return adapter;
        }
        catch (COMException exception)
        {
            throw new OutlookComHostException(OutlookComFailureReason.OutlookUnavailable, exception);
        }
        catch (Exception exception) when (exception is TypeLoadException or FileNotFoundException)
        {
            throw new OutlookComHostException(OutlookComFailureReason.DependencyMissing, exception);
        }
        finally
        {
            if (application is not null && Marshal.IsComObject(application))
            {
                _ = Marshal.FinalReleaseComObject(application);
            }
        }
    }
}

/// <summary>
/// The only classic Outlook COM boundary. Its public seam contains browse, subscribe, enumerate and
/// read-for-export operations only; no mailbox mutation member is available to callers.
/// </summary>
internal sealed class ClassicOutlookComAdapter : IClassicOutlookAdapter
{
    private const string AttachmentBytesSchema = "http://schemas.microsoft.com/mapi/proptag/0x37010102";
    private const string AttachmentMimeSchema = "http://schemas.microsoft.com/mapi/proptag/0x370E001F";
    private const int MaximumDirectedFolderCandidates = 500;
    private readonly Outlook.Application _application;
    private readonly Outlook.NameSpace _session;

    public ClassicOutlookComAdapter(Outlook.Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        try
        {
            _session = application.Session;
        }
        catch (COMException exception)
        {
            throw new OutlookComHostException(OutlookComFailureReason.OutlookUnavailable, exception);
        }
    }

    public ValueTask<IReadOnlyList<OutlookFolderDescriptor>> BrowseFoldersAsync(string targetPath, CancellationToken cancellationToken)
    {
        var segments = ParseTargetPath(targetPath);
        cancellationToken.ThrowIfCancellationRequested();
        Outlook.Folders? roots = null;
        try
        {
            roots = _session.Folders;
            var target = DirectedOutlookFolderTraversal.Resolve(
                roots,
                segments,
                MaximumDirectedFolderCandidates,
                static folders => folders.Count,
                static (folders, index) => folders[index],
                static folder => folder.Name,
                static folder => folder.Folders,
                static folder => new BrowseFolderIdentity(folder.StoreID, folder.EntryID, folder.Name),
                Release,
                cancellationToken);
            return ValueTask.FromResult<IReadOnlyList<OutlookFolderDescriptor>>(
                [new OutlookFolderDescriptor(
                    new OutlookCaptureFolderId(StableGuid($"{target.StoreId}\n{target.EntryId}")),
                    new OutlookFolderIdentity(target.StoreId, target.EntryId, target.DisplayName))]);
        }
        catch (OutlookBrowseTargetException)
        {
            throw;
        }
        catch (COMException exception)
        {
            throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied, exception);
        }
        finally
        {
            Release(roots);
        }

    }

    public ValueTask<IAsyncDisposable> SubscribeHintsAsync(
        OutlookFolderIdentity folder,
        Func<OutlookHint, ValueTask> onHint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(onHint);
        cancellationToken.ThrowIfCancellationRequested();
        Outlook.MAPIFolder? mapiFolder = null;
        Outlook.Items? items = null;
        var transferred = false;
        try
        {
            mapiFolder = _session.GetFolderFromID(folder.FolderEntryId, folder.StoreId);
            items = mapiFolder.Items;
            Outlook.ItemsEvents_ItemAddEventHandler itemAdd = item => ObserveHint(item, onHint, Release);
            Outlook.ItemsEvents_ItemChangeEventHandler itemChange = item => ObserveHint(item, onHint, Release);
            items.ItemAdd += itemAdd;
            items.ItemChange += itemChange;
            var subscription = ValueTask.FromResult<IAsyncDisposable>(
                new OutlookHintSubscription(mapiFolder, items, itemAdd, itemChange));
            transferred = true;
            return subscription;
        }
        catch (COMException exception)
        {
            throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied, exception);
        }
        finally
        {
            if (!transferred)
            {
                Release(items);
                Release(mapiFolder);
            }
        }
    }

    public async IAsyncEnumerable<OutlookItemEnvelope> EnumerateAsync(
        OutlookFolderIdentity folder,
        OutlookCursor cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var envelopes = EnumerateCore(folder, cursor, cancellationToken);
        foreach (var envelope in envelopes)
        {
            yield return envelope;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private IReadOnlyList<OutlookItemEnvelope> EnumerateCore(
        OutlookFolderIdentity folder,
        OutlookCursor cursor,
        CancellationToken cancellationToken)
    {
        Outlook.MAPIFolder? mapiFolder = null;
        Outlook.Items? items = null;
        var results = new List<OutlookItemEnvelope>();
        try
        {
            mapiFolder = _session.GetFolderFromID(folder.FolderEntryId, folder.StoreId);
            items = mapiFolder.Items;
            for (var index = 1; index <= items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? candidate = null;
                try
                {
                    candidate = items[index];
                    if (candidate is Outlook.MailItem mail)
                    {
                        var envelope = Envelope(mail, folder.StoreId);
                        if (envelope.Timestamp(cursor.Basis) >= cursor.FromUtc)
                        {
                            results.Add(envelope);
                        }
                    }
                }
                finally
                {
                    Release(candidate);
                }
            }
        }
        catch (COMException exception)
        {
            throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied, exception);
        }
        finally
        {
            Release(items);
            Release(mapiFolder);
        }

        return results;
    }

    public ValueTask<OutlookMessagePayload> ReadForExportAsync(
        OutlookItemEnvelope item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        object? candidate = null;
        try
        {
            candidate = _session.GetItemFromID(item.EntryId, item.StoreId);
            if (candidate is not Outlook.MailItem mail)
            {
                throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied);
            }

            Outlook.Attachments? attachmentCollection = null;
            try
            {
                attachmentCollection = mail.Attachments;
                var attachments = new List<OutlookAttachmentPayload>(attachmentCollection.Count);
                for (var index = 1; index <= attachmentCollection.Count; index++)
                {
                    Outlook.Attachment? attachment = null;
                    Outlook.PropertyAccessor? accessor = null;
                    try
                    {
                        attachment = attachmentCollection[index];
                        accessor = attachment.PropertyAccessor;
                        var bytes = accessor.GetProperty(AttachmentBytesSchema) as byte[]
                            ?? throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied);
                        var contentType = accessor.GetProperty(AttachmentMimeSchema) as string;
                        attachments.Add(new OutlookAttachmentPayload(
                            attachment.FileName,
                            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                            bytes));
                    }
                    finally
                    {
                        Release(accessor);
                        Release(attachment);
                    }
                }

                return ValueTask.FromResult(new OutlookMessagePayload(
                    Encoding.UTF8.GetBytes(mail.Body ?? string.Empty),
                    "text/plain",
                    attachments));
            }
            finally
            {
                Release(attachmentCollection);
            }
        }
        catch (COMException exception)
        {
            throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied, exception);
        }
        finally
        {
            Release(candidate);
        }
    }

    public ValueTask DisposeAsync()
    {
        Release(_session);
        Release(_application);
        return ValueTask.CompletedTask;
    }

    private static string[] ParseTargetPath(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (targetPath.Length > 512 ||
            targetPath.Any(char.IsControl) ||
            !string.Equals(targetPath, targetPath.Trim(), StringComparison.Ordinal))
        {
            throw new OutlookBrowseTargetException();
        }

        var segments = targetPath.Split('/');
        if (segments.Length < 2 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new OutlookBrowseTargetException();
        }

        return segments;
    }


    private static OutlookItemEnvelope Envelope(Outlook.MailItem mail, string storeId)
    {
        var modified = new DateTimeOffset(mail.LastModificationTime.ToUniversalTime(), TimeSpan.Zero);
        var received = new DateTimeOffset(mail.ReceivedTime.ToUniversalTime(), TimeSpan.Zero);
        var fingerprint = Sha256($"{mail.EntryID}\n{modified:O}\n{received:O}\n{mail.Size}");
        return new OutlookItemEnvelope(storeId, mail.EntryID, modified, received, fingerprint);
    }

    internal static void ObserveHint(
        object? transientItem,
        Func<OutlookHint, ValueTask> callback,
        Action<object?> release)
    {
        try
        {
            var pending = callback(new OutlookHint("folder-change"));
            if (!pending.IsCompletedSuccessfully)
            {
                _ = pending.AsTask().ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            release(transientItem);
        }
    }

    private static Guid StableGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));

    internal sealed record BrowseFolderIdentity(string StoreId, string EntryId, string DisplayName);

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private sealed class OutlookHintSubscription(
        Outlook.MAPIFolder folder,
        Outlook.Items items,
        Outlook.ItemsEvents_ItemAddEventHandler itemAdd,
        Outlook.ItemsEvents_ItemChangeEventHandler itemChange) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            try
            {
                items.ItemAdd -= itemAdd;
                items.ItemChange -= itemChange;
            }
            finally
            {
                Release(items);
                Release(folder);
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Bounded host-only outcome for an absent or ambiguous requested folder path.</summary>
internal sealed class OutlookBrowseTargetException : Exception
{
    public OutlookBrowseTargetException() : base("The exact Outlook browse target is unavailable.")
    {
    }
}

/// <summary>Production-directed traversal extracted from the COM boundary so its bounds and releases are testable without Outlook.</summary>
internal static class DirectedOutlookFolderTraversal
{
    internal static ClassicOutlookComAdapter.BrowseFolderIdentity Resolve<TCollection, TFolder>(
        TCollection root,
        IReadOnlyList<string> segments,
        int maximumCandidates,
        Func<TCollection, int> count,
        Func<TCollection, int, TFolder> item,
        Func<TFolder, string> name,
        Func<TFolder, TCollection> children,
        Func<TFolder, ClassicOutlookComAdapter.BrowseFolderIdentity> identity,
        Action<object?> release,
        CancellationToken cancellationToken)
        where TCollection : class
        where TFolder : class
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(segments);
        var remaining = maximumCandidates;
        return ResolveSegment(root, 0);

        ClassicOutlookComAdapter.BrowseFolderIdentity ResolveSegment(TCollection folders, int segmentIndex)
        {
            TFolder? match = null;
            try
            {
                for (var index = 1; index <= count(folders); index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remaining-- <= 0)
                    {
                        throw new OutlookBrowseTargetException();
                    }

                    TFolder? candidate = null;
                    try
                    {
                        candidate = item(folders, index);
                        if (!string.Equals(name(candidate), segments[segmentIndex], StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (match is not null)
                        {
                            throw new OutlookBrowseTargetException();
                        }

                        match = candidate;
                        candidate = null;
                    }
                    finally
                    {
                        release(candidate);
                    }
                }

                if (match is null)
                {
                    throw new OutlookBrowseTargetException();
                }

                if (segmentIndex == segments.Count - 1)
                {
                    return identity(match);
                }

                TCollection? childFolders = null;
                try
                {
                    childFolders = children(match);
                    return ResolveSegment(childFolders, segmentIndex + 1);
                }
                finally
                {
                    release(childFolders);
                }
            }
            finally
            {
                release(match);
            }
        }
    }
}
