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

    ValueTask<IReadOnlyList<OutlookFolderIdentity>> DiscoverSubfoldersAsync(
        OutlookFolderIdentity root,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This Outlook adapter does not support subfolder discovery.");

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

    public ValueTask<IReadOnlyList<OutlookFolderIdentity>> DiscoverSubfoldersAsync(
        OutlookFolderIdentity root,
        CancellationToken cancellationToken) =>
        dispatcher.InvokeAsync(() => inner.DiscoverSubfoldersAsync(root, cancellationToken), cancellationToken);

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
            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.ActivationSession);
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
    private const string AttachmentContentIdSchema = "http://schemas.microsoft.com/mapi/proptag/0x3712001F";
    private const string AttachmentHiddenSchema = "http://schemas.microsoft.com/mapi/proptag/0x7FFE000B";
    private const int MaximumDirectedFolderCandidates = 500;
    private readonly Outlook.Application _application;
    private readonly Outlook.NameSpace _session;
    private readonly Dictionary<OutlookFolderIdentity, OutlookFolderIdentity> _subfolderRoots = [];

    public ClassicOutlookComAdapter(Outlook.Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        try
        {
            _session = application.Session;
        }
        catch (COMException exception)
        {
            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.ActivationSession);
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
            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.FolderSubscription);
        }
        finally
        {
            Release(roots);
        }

    }

    public ValueTask<IReadOnlyList<OutlookFolderIdentity>> DiscoverSubfoldersAsync(
        OutlookFolderIdentity root,
        CancellationToken cancellationToken)
    {
        var descendants = new List<OutlookFolderIdentity>();
        var seen = new HashSet<(string StoreId, string EntryId)> { (root.StoreId, root.FolderEntryId) };
        var pending = new Queue<OutlookFolderIdentity>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Outlook.MAPIFolder? folder = null;
            Outlook.Folders? children = null;
            try
            {
                folder = _session.GetFolderFromID(parent.FolderEntryId, parent.StoreId);
                children = folder.Folders;
                var count = children.Count;
                for (var index = 1; index <= count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Outlook.MAPIFolder? child = null;
                    try
                    {
                        child = children[index];
                        var identity = new OutlookFolderIdentity(child.StoreID, child.EntryID, child.Name);
                        if (!string.Equals(identity.StoreId, root.StoreId, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied);
                        }
                        if (seen.Add((identity.StoreId, identity.FolderEntryId)))
                        {
                            descendants.Add(identity);
                            _subfolderRoots.TryAdd(identity, root);
                            pending.Enqueue(identity);
                        }
                    }
                    finally
                    {
                        ReleaseReference(child);
                    }
                }
            }
            catch (COMException exception)
            {
                throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.FolderSubscription);
            }
            finally
            {
                ReleaseReference(children);
                ReleaseReference(folder);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<OutlookFolderIdentity>>(descendants);
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
            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.FolderSubscription);
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
            RequireSelectedScope(mapiFolder, folder);
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
                        var envelope = Envelope(mail, folder.StoreId) with { EnumeratedFolder = folder };
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
            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.Enumeration);
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
            try
            {
                candidate = _session.GetItemFromID(item.EntryId, item.StoreId);
            }
            catch (COMException exception)
            {
                throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.MessageOpen);
            }

            if (candidate is not Outlook.MailItem mail)
            {
                throw new OutlookComHostException(
                    OutlookComFailureReason.OutlookUnavailable,
                    stage: OutlookComFailureStage.MessageOpen);
            }

            byte[] body;
            try
            {
                RequireMessageParent(mail, item.EnumeratedFolder);
                body = Encoding.UTF8.GetBytes(mail.Body ?? string.Empty);
            }
            catch (COMException exception)
            {
                throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.MessageBody);
            }

            Outlook.Attachments? attachmentCollection = null;
            try
            {
                int attachmentCount;
                try
                {
                    attachmentCollection = mail.Attachments;
                    attachmentCount = attachmentCollection.Count;
                }
                catch (COMException exception)
                {
                    throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.AttachmentEnumeration);
                }

                var signatureImages = attachmentCount == 0 ? [] :
                    OutlookSignatureImages.Find(ReadOptionalMailText(() => mail.HTMLBody));
                var attachments = new List<OutlookAttachmentPayload>(attachmentCount);
                var contentIds = new string?[attachmentCount];
                for (var index = 1; index <= attachmentCount; index++)
                {
                    Outlook.Attachment? attachment = null;
                    try
                    {
                        try
                        {
                            attachment = attachmentCollection[index];
                        }
                        catch (COMException exception)
                        {
                            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.AttachmentEnumeration);
                        }

                        Outlook.PropertyAccessor? accessor = null;
                        try
                        {
                            accessor = attachment.PropertyAccessor;
                            var bytes = accessor.GetProperty(AttachmentBytesSchema) as byte[]
                                ?? throw new OutlookComHostException(
                                    OutlookComFailureReason.OutlookUnavailable,
                                    stage: OutlookComFailureStage.AttachmentByteProperty);
                            var contentType = accessor.GetProperty(AttachmentMimeSchema) as string;
                            var mediaType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
                            var contentId = signatureImages.Count == 0 ? null : OutlookSignatureImages.NormalizeContentId(
                                ReadOptionalMailText(() => accessor.GetProperty(AttachmentContentIdSchema) as string));
                            contentIds[index - 1] = contentId;
                            attachments.Add(new OutlookAttachmentPayload(attachment.FileName, mediaType, bytes)
                            {
                                IsSignatureDecoration = contentId is not null &&
                                    signatureImages.Contains(contentId, StringComparer.OrdinalIgnoreCase) &&
                                    OutlookSignatureImages.IsRaster(mediaType, bytes) && ReadOptionalAttachmentHidden(accessor)
                            });
                        }
                        catch (COMException exception)
                        {
                            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.AttachmentByteProperty);
                        }
                        finally
                        {
                            Release(accessor);
                        }
                    }
                    finally
                    {
                        Release(attachment);
                    }
                }

                var duplicateContentIds = contentIds.OfType<string>().GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < attachments.Count; index++)
                {
                    if (contentIds[index] is { } contentId && duplicateContentIds.Contains(contentId))
                        attachments[index] = attachments[index] with { IsSignatureDecoration = false };
                }

                RequireMessageParent(mail, item.EnumeratedFolder);
                return ValueTask.FromResult(new OutlookMessagePayload(
                    body,
                    "text/plain",
                    attachments)
                {
                    Subject = ReadOptionalMailText(() => mail.Subject),
                    Sender = ReadOptionalMailText(() => mail.SenderName)
                });
            }
            finally
            {
                Release(attachmentCollection);
            }
        }
        finally
        {
            Release(candidate);
        }
    }

    private void RequireMessageParent(Outlook.MailItem mail, OutlookFolderIdentity? expected)
    {
        if (expected is null) return;
        object? parent = null;
        try
        {
            parent = mail.Parent;
            if (parent is not Outlook.MAPIFolder folder || !IsSameFolder(folder, expected))
            {
                throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied);
            }
            RequireSelectedScope(folder, expected);
        }
        catch (COMException exception)
        {
            throw OutlookComFailureClassifier.ClassifyCom(exception, OutlookComFailureStage.MessageOpen);
        }
        finally
        {
            ReleaseReference(parent);
        }
    }

    private void RequireSelectedScope(Outlook.MAPIFolder folder, OutlookFolderIdentity identity)
    {
        if (!_subfolderRoots.TryGetValue(identity, out var selectedRoot)) return;
        object? currentParent = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            currentParent = folder.Parent;
            while (currentParent is Outlook.MAPIFolder ancestor && seen.Add(ancestor.EntryID))
            {
                if (IsSameFolder(ancestor, selectedRoot)) return;
                var next = ancestor.Parent;
                ReleaseReference(currentParent);
                currentParent = next;
            }
        }
        finally
        {
            ReleaseReference(currentParent);
        }

        throw new OutlookComHostException(OutlookComFailureReason.FolderAccessDenied);
    }

    private static bool IsSameFolder(Outlook.MAPIFolder folder, OutlookFolderIdentity expected) =>
        string.Equals(folder.StoreID, expected.StoreId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(folder.EntryID, expected.FolderEntryId, StringComparison.OrdinalIgnoreCase);

    public ValueTask DisposeAsync()
    {
        Release(_session);
        Release(_application);
        return ValueTask.CompletedTask;
    }

    private static bool ReadOptionalAttachmentHidden(Outlook.PropertyAccessor accessor)
    {
        try { return accessor.GetProperty(AttachmentHiddenSchema) is true; }
        catch (COMException) { return false; }
    }

    private static string? ReadOptionalMailText(Func<string?> read)
    {
        try
        {
            var value = read();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (COMException)
        {
            return null;
        }
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

    internal static ValueTask DisposeHintSubscriptionAsync(
        Action removeItemAdd,
        Action removeItemChange,
        Action<object?> release,
        object? items,
        object? folder)
    {
        ArgumentNullException.ThrowIfNull(removeItemAdd);
        ArgumentNullException.ThrowIfNull(removeItemChange);
        ArgumentNullException.ThrowIfNull(release);
        COMException? failure = null;
        try
        {
            try
            {
                removeItemAdd();
            }
            catch (COMException exception)
            {
                failure = exception;
            }

            try
            {
                removeItemChange();
            }
            catch (COMException exception)
            {
                failure ??= exception;
            }
        }
        finally
        {
            release(items);
            release(folder);
        }

        if (failure is not null)
        {
            throw OutlookComFailureClassifier.ClassifyCom(failure, OutlookComFailureStage.FolderSubscription);
        }

        return ValueTask.CompletedTask;
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ReleaseReference(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

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
        public ValueTask DisposeAsync() => DisposeHintSubscriptionAsync(
            () => items.ItemAdd -= itemAdd,
            () => items.ItemChange -= itemChange,
            Release,
            items,
            folder);
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
