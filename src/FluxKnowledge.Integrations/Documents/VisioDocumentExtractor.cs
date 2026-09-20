using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Documents;

/// <summary>
/// Runs one installed, interactive-session Visio instance in a dedicated STA. It never attaches
/// to an existing instance and makes no document call until the newly-created process is in an
/// owned kill-on-close job.
/// </summary>
public sealed class VisioDocumentExtractor : IVisioDocumentExtractor
{
    // visOpenRO + visOpenDontList + visOpenHidden + visOpenMacrosDisabled +
    // visOpenNoWorkspace + visOpenDeclineAutoRefresh.
    private const short SafeOpenFlags = 2 + 8 + 64 + 128 + 256 + 1024;
    private const short VisSectionProp = 243;
    private const string VisioProgId = "Visio.InvisibleApp";
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private const string MutexName = "Global\\FluxKnowledge.VisioDocumentExtractor.v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? GetUnavailableReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "visio-windows-interactive-session-required";
        }
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
        {
            return "visio-interactive-user-session-required";
        }
        if (Type.GetTypeFromProgID(VisioProgId) is null)
        {
            return "visio-application-unavailable";
        }
        var sessions = Process.GetProcessesByName("VISIO");
        try
        {
            if (sessions.Any(static process => !process.HasExited))
            {
                return "visio-existing-session-refused";
            }
        }
        finally
        {
            foreach (var session in sessions) session.Dispose();
        }
        try
        {
            using var mutex = new Mutex(false, MutexName);
            if (!TryAcquire(mutex))
            {
                return "visio-extractor-already-running";
            }
            mutex.ReleaseMutex();
        }
        catch (UnauthorizedAccessException)
        {
            return "visio-extractor-unavailable";
        }
        return null;
    }

    public async ValueTask<VisioDocumentResult> ExtractAsync(RetainedSourceBytes retained, CancellationToken cancellationToken)
    {
        using var lease = VisioExecutionLease.Acquire(this);
        return await lease.ExtractAsync(retained, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<VisioDocumentResult> ExtractWithinLeaseAsync(RetainedSourceBytes retained, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var unavailable = GetUnavailableReason();
        if (unavailable is not null)
        {
            throw new RetainedProcessorException(unavailable);
        }
        await VisioDocumentPreflight.ValidateAsync(retained, cancellationToken).ConfigureAwait(false);

        var control = new ExtractionControl();
        var task = RunStaAsync(() => ExtractOnSta(retained, cancellationToken, control));
        var activated = await Task.WhenAny(task, control.Owned.Task, Task.Delay(ActivationTimeout, CancellationToken.None)).ConfigureAwait(false);
        if (activated == task)
        {
            var result = await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= VisioDocumentPreflight.MaximumRunTime) throw new RetainedProcessorException("visio-document-timeout");
            return result;
        }
        if (activated != control.Owned.Task)
        {
            control.Cancel();
            throw new RetainedProcessorException("visio-cleanup-unproven");
        }

        var remaining = VisioDocumentPreflight.MaximumRunTime - elapsed.Elapsed;
        var completed = await Task.WhenAny(task, Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, CancellationToken.None),
            cancellationToken.AsTask()).ConfigureAwait(false);
        if (completed == task)
        {
            var result = await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= VisioDocumentPreflight.MaximumRunTime) throw new RetainedProcessorException("visio-document-timeout");
            return result;
        }
        control.Cancel();
        if (!control.ConfirmOwnedExit(CleanupTimeout))
        {
            throw new RetainedProcessorException("visio-cleanup-unproven");
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new RetainedProcessorException("visio-document-timeout");
    }

    private static VisioDocumentResult ExtractOnSta(
        RetainedSourceBytes retained,
        CancellationToken cancellationToken,
        ExtractionControl control)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new RetainedProcessorException("visio-windows-interactive-session-required");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var mutex = new Mutex(false, MutexName);
        if (!TryAcquire(mutex))
        {
            throw new RetainedProcessorException("visio-extractor-already-running");
        }
        dynamic? application = null;
        OwnedVisioLifetime? lifetime = null;
        PrivateInput? input = null;
        Exception? failure = null;
        try
        {
            var existing = SnapshotVisioPids();
            if (existing.Count != 0)
            {
                throw new RetainedProcessorException("visio-existing-session-refused");
            }
            var applicationType = Type.GetTypeFromProgID(VisioProgId)
                ?? throw new RetainedProcessorException("visio-application-unavailable");
            application = Activator.CreateInstance(applicationType)
                ?? throw new RetainedProcessorException("visio-activation-failed");

            var applicationProcessId = GetWindowsProcessIdFromWindowHandle(Convert.ToInt32(application.WindowHandle32));
            var ownedProcess = FindExactlyOneNewVisioProcess(existing, applicationProcessId);
            try { lifetime = OwnedVisioLifetime.Attach(ownedProcess); } // Must happen before any document call.
            catch
            {
                ownedProcess.Dispose();
                throw;
            }
            control.SetLifetime(lifetime);
            if (!control.IsInputPermitted)
            {
                throw new RetainedProcessorException("visio-cleanup-unproven");
            }
            application.Visible = false;
            application.EventsEnabled = 0;
            if (Convert.ToBoolean(application.Visible) || Convert.ToInt16(application.EventsEnabled) != 0)
            {
                throw new RetainedProcessorException("visio-application-safety-setting-unconfirmed");
            }

            input = WritePrivateInput(retained.Bytes);
            dynamic? document = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!control.IsInputPermitted)
                {
                    throw new RetainedProcessorException("visio-cleanup-unproven");
                }
                document = application.Documents.OpenEx(input.Path, SafeOpenFlags);
                if (!Convert.ToBoolean(document.ReadOnly))
                {
                    throw new RetainedProcessorException("visio-document-not-readonly");
                }
                if (Convert.ToBoolean(document.MacrosEnabled))
                    throw new RetainedProcessorException("visio-document-macros-enabled");
                if (Count(document.DataRecordsets) != 0)
                {
                    throw new RetainedProcessorException("visio-document-data-connection-present");
                }

                var started = Stopwatch.StartNew();
                IReadOnlyList<VisioPageEvidence> pages = ReadPages(document, cancellationToken);
                var provenance = VisioDocumentProvenance.Create(pages);
                var peakPrivateBytes = CurrentPrivateBytes(lifetime);
                return new VisioDocumentResult(
                    provenance.Extraction,
                    provenance.MetadataJson,
                    provenance.ShapeCount,
                    provenance.ConnectionCount,
                    started.ElapsedMilliseconds,
                    peakPrivateBytes);
            }
            finally
            {
                if (document is not null)
                {
                    try
                    {
                        document.Saved = true;
                        document.Close();
                    }
                    finally { Release(document); }
                }
            }
        }
        catch (RetainedProcessorException exception)
        {
            failure = exception;
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
        {
            failure = new RetainedProcessorException("visio-document-extraction-failed", innerException: exception);
            throw failure;
        }
        finally
        {
            try
            {
                try
                {
                    if (application is not null)
                    {
                        try { application.Quit(); }
                        finally { Release(application); }
                        application = null;
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    failure ??= new RetainedProcessorException("visio-cleanup-unproven", innerException: exception);
                }
                finally
                {
                    var cleanupConfirmed = lifetime is not null && lifetime.CloseAndConfirmExit(CleanupTimeout);
                    lifetime?.Dispose();
                    if (!cleanupConfirmed)
                    {
                        throw new RetainedProcessorException("visio-cleanup-unproven", innerException: failure);
                    }
                    if (input is not null)
                    {
                        try { input.DisposeAndDelete(); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new RetainedProcessorException("visio-cleanup-unproven", innerException: exception);
                        }
                    }
                    if (failure is not null)
                    {
                        throw failure;
                    }
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static IReadOnlyList<VisioPageEvidence> ReadPages(dynamic document, CancellationToken cancellationToken)
    {
        var count = Count(document.Pages);
        if (count > VisioDocumentPreflight.MaximumPageCount)
        {
            throw new RetainedProcessorException("visio-document-page-limit-exceeded");
        }
        var pages = new List<VisioPageEvidence>(count);
        var textBudget = new TextBudget();
        for (var index = 1; index <= count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic? page = null;
            try
            {
                page = document.Pages.Item(index);
                var pageId = Convert.ToInt32(page.ID);
                var isBackground = Convert.ToBoolean(page.Background);
                var connections = ReadConnections(page);
                var shapes = new List<VisioShapeEvidence>();
                var topLevelCount = Count(page.Shapes);
                for (var shapeIndex = 1; shapeIndex <= topLevelCount; shapeIndex++)
                {
                    dynamic? shape = null;
                    try
                    {
                        shape = page.Shapes.Item(shapeIndex);
                        ReadShape(shape, null, 0, connections, shapes, textBudget, cancellationToken);
                    }
                    finally
                    {
                        Release(shape);
                    }
                }
                pages.Add(new VisioPageEvidence(pageId, isBackground, shapes, connections.Count));
            }
            finally
            {
                Release(page);
            }
        }
        return pages;
    }

    private static void ReadShape(
        dynamic shape,
        int? parentShapeId,
        int depth,
        VisioConnectionMap connections,
        List<VisioShapeEvidence> results,
        TextBudget textBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > VisioDocumentPreflight.MaximumGroupDepth || depth > VisioDocumentPreflight.MaximumTraversalDepth ||
            results.Count >= VisioDocumentPreflight.MaximumShapesPerPage)
        {
            throw new RetainedProcessorException("visio-document-shape-limit-exceeded");
        }
        int shapeId = Convert.ToInt32(shape.ID);
        dynamic? characters = null;
        dynamic? master = null;
        dynamic? masterShape = null;
        try
        {
            characters = shape.Characters;
            var text = Convert.ToString(characters.Text) ?? string.Empty; // Visio's effective field-expanded text.
            master = shape.Master;
            int? masterId = master is null ? null : Convert.ToInt32(master.ID);
            masterShape = master is null ? null : shape.MasterShape;
            var masterShapeId = masterShape is null ? null : Convert.ToString(masterShape.ID);
            (int? From, int? To) endpoint = (null, null);
            connections.TryGetValue(shapeId, out endpoint);
            var data = ReadShapeData(shape);
            text = AppendSearchableShapeData(text, data);
            textBudget.Add(text);
            results.Add(new VisioShapeEvidence(
                shapeId,
                parentShapeId,
                masterId,
                masterShapeId,
                text,
                data,
                endpoint.From,
                endpoint.To,
                ReadArrow(shape, "BeginArrow"),
                ReadArrow(shape, "EndArrow")));
        }
        finally
        {
            Release(masterShape);
            Release(master);
            Release(characters);
        }

        var children = Count(shape.Shapes);
        for (var childIndex = 1; childIndex <= children; childIndex++)
        {
            dynamic? child = null;
            try
            {
                child = shape.Shapes.Item(childIndex);
                ReadShape(child, shapeId, depth + 1, connections, results, textBudget, cancellationToken);
            }
            finally
            {
                Release(child);
            }
        }
    }

    private static VisioConnectionMap ReadConnections(dynamic page)
    {
        var endpointMap = new Dictionary<int, (int? From, int? To)>();
        var connects = Count(page.Connects);
        var endpointCount = 0;
        for (var index = 1; index <= connects; index++)
        {
            dynamic? connect = null;
            dynamic? from = null;
            dynamic? to = null;
            dynamic? fromCell = null;
            try
            {
                connect = page.Connects.Item(index);
                from = connect.FromSheet;
                to = connect.ToSheet;
                fromCell = connect.FromCell;
                if (from is null || to is null || fromCell is null)
                {
                    throw new RetainedProcessorException("visio-document-connection-invalid");
                }
                int fromId = Convert.ToInt32(from.ID);
                int toId = Convert.ToInt32(to.ID);
                string? cellName = Convert.ToString(fromCell.Name); // Cell.Name is universal; Cell has no NameU.
                if (TryRecordConnectorEndpoint(endpointMap, fromId, toId, cellName)) endpointCount++;
            }
            finally
            {
                Release(fromCell);
                Release(to);
                Release(from);
                Release(connect);
            }
        }
        return new VisioConnectionMap(endpointMap, endpointCount);
    }

    internal static bool TryRecordConnectorEndpoint(
        IDictionary<int, (int? From, int? To)> endpointMap,
        int fromId,
        int toId,
        string? cellName)
    {
        ArgumentNullException.ThrowIfNull(endpointMap);
        endpointMap.TryGetValue(fromId, out var endpoints);
        if (string.Equals(cellName, "BeginX", StringComparison.OrdinalIgnoreCase))
        {
            endpointMap[fromId] = (toId, endpoints.To);
            return true;
        }
        if (string.Equals(cellName, "EndX", StringComparison.OrdinalIgnoreCase))
        {
            endpointMap[fromId] = (endpoints.From, toId);
            return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, string>? ReadShapeData(dynamic shape)
    {
        if (Convert.ToInt16(shape.SectionExists(VisSectionProp, 0)) == 0)
        {
            return null;
        }
        int rows = Convert.ToInt32(shape.RowCount(VisSectionProp));
        var result = new Dictionary<string, string>(rows, StringComparer.Ordinal);
        for (var row = 0; row < rows; row++)
        {
            dynamic? cell = null;
            dynamic? labelCell = null;
            try
            {
                cell = shape.CellsSRC(VisSectionProp, row, 0);
                labelCell = shape.CellsSRC(VisSectionProp, row, 2);
                string? label = Convert.ToString(cell.RowNameU);
                string value = Convert.ToString(cell.ResultStrU(string.Empty)) ?? string.Empty;
                string? displayLabel = Convert.ToString(labelCell.ResultStrU(string.Empty));
                var key = string.IsNullOrWhiteSpace(displayLabel)
                    ? (string.IsNullOrEmpty(label) ? row.ToString(System.Globalization.CultureInfo.InvariantCulture) : label)
                    : displayLabel;
                if (result.ContainsKey(key))
                {
                    key = $"{key} ({(string.IsNullOrEmpty(label) ? row.ToString(System.Globalization.CultureInfo.InvariantCulture) : label)})";
                }
                result[key] = value;
            }
            finally
            {
                Release(labelCell);
                Release(cell);
            }
        }
        return result;
    }

    private static string AppendSearchableShapeData(string text, IReadOnlyDictionary<string, string>? data)
    {
        if (data is null || data.Count == 0)
        {
            return text;
        }
        var builder = new System.Text.StringBuilder(text);
        foreach (var (label, value) in data)
        {
            var item = string.IsNullOrWhiteSpace(label) ? value : $"{label}: {value}";
            if (string.IsNullOrWhiteSpace(item) || text.Contains(item, StringComparison.Ordinal))
            {
                continue;
            }
            if (builder.Length > 0) builder.Append(Environment.NewLine);
            builder.Append(item);
        }
        return builder.ToString();
    }

    private static string? ReadArrow(dynamic shape, string cellName)
    {
        if (Convert.ToInt16(shape.CellExistsU(cellName, 0)) == 0)
        {
            return null;
        }
        dynamic? cell = null;
        try
        {
            cell = shape.CellsU(cellName);
            return Convert.ToString(cell.ResultStrU(string.Empty));
        }
        finally
        {
            Release(cell);
        }
    }

    private static Process FindExactlyOneNewVisioProcess(IReadOnlySet<int> before, int applicationProcessId)
    {
        if (applicationProcessId <= 0)
        {
            throw new RetainedProcessorException("visio-process-ownership-unproven");
        }
        var deadline = Stopwatch.GetTimestamp() + (long)(ActivationTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var candidates = Process.GetProcessesByName("VISIO").Where(process => !before.Contains(process.Id)).ToArray();
            if (candidates.Length == 1 && candidates[0].Id == applicationProcessId)
            {
                if (candidates[0].SessionId != Process.GetCurrentProcess().SessionId)
                {
                    candidates[0].Dispose();
                    throw new RetainedProcessorException("visio-process-session-mismatch");
                }
                return candidates[0];
            }
            foreach (var candidate in candidates) candidate.Dispose();
            Thread.Sleep(50);
        }
        throw new RetainedProcessorException("visio-process-ownership-unproven");
    }

    internal static int GetWindowsProcessIdFromWindowHandle(int windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new RetainedProcessorException("visio-process-ownership-unproven");
        }
        var threadId = GetWindowThreadProcessId(new IntPtr(windowHandle), out var processId);
        if (threadId == 0 || processId == 0 || processId > int.MaxValue)
        {
            throw new RetainedProcessorException("visio-process-ownership-unproven");
        }
        return checked((int)processId);
    }

    private static HashSet<int> SnapshotVisioPids()
    {
        var processes = Process.GetProcessesByName("VISIO");
        try { return processes.Where(static process => !process.HasExited).Select(static process => process.Id).ToHashSet(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static long CurrentPrivateBytes(OwnedVisioLifetime lifetime)
    {
        try
        {
            return lifetime.GetPrivateBytes();
        }
        catch (InvalidOperationException)
        {
            throw new RetainedProcessorException("visio-cleanup-unproven");
        }
    }

    private static PrivateInput WritePrivateInput(byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "FluxKnowledge", "visio-input", Guid.NewGuid().ToString("N"));
        var fileSystem = new HandleRelativeNativeFileSystem();
        var parent = fileSystem.OpenOrCreateDirectory(directory);
        var name = $"{Guid.NewGuid():N}.vsdx";
        var path = Path.Combine(directory, name);
        FileStream? file = null;
        try
        {
            var mutation = fileSystem.ReplaceFileAsync(parent, name + ".tmp", name, bytes, null)
                .AsTask().GetAwaiter().GetResult();
            if (!mutation.Changed || mutation.Identity is null) throw new IOException("visio-private-input-write-refused");
            var noFollow = PhysicalFileIdentity.OpenReadNoFollow(path);
            file = new FileStream(noFollow, FileAccess.Read, 64 * 1024, isAsync: true);
            if (!string.Equals(PhysicalFileIdentity.GetFinalPath(noFollow), path, StringComparison.OrdinalIgnoreCase) ||
                file.Length != bytes.LongLength || !SHA256.HashData(file).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new IOException("visio-private-input-verification-failed");
            file.Position = 0;
            return new PrivateInput(path, parent, name, mutation.Identity.Value, file);
        }
        catch (Exception exception)
        {
            file?.Dispose();
            parent.Dispose();
            throw new RetainedProcessorException("visio-cleanup-unproven", innerException: exception);
        }
    }

    private static int Count(dynamic collection)
    {
        if (collection is null) return 0;
        try { return Convert.ToInt32(collection.Count); }
        finally { Release(collection); }
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static Task<T> RunStaAsync<T>(Func<T> work)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Visio automation requires Windows.");
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(work()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "FluxKnowledge Visio document extraction" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Release(object? value)
    {
        if (OperatingSystem.IsWindows() && value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private sealed class ExtractionControl
    {
        private OwnedVisioLifetime? _lifetime;
        private int _cancelled;
        public TaskCompletionSource Owned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsInputPermitted => Volatile.Read(ref _cancelled) == 0;

        public void SetLifetime(OwnedVisioLifetime lifetime)
        {
            _lifetime = lifetime;
            Owned.TrySetResult();
            if (!IsInputPermitted)
            {
                lifetime.CloseAndConfirmExit(CleanupTimeout);
            }
        }

        public void Cancel()
        {
            Interlocked.Exchange(ref _cancelled, 1);
            _lifetime?.CloseAndConfirmExit(CleanupTimeout);
        }

        public bool ConfirmOwnedExit(TimeSpan timeout) =>
            _lifetime is not null && _lifetime.CloseAndConfirmExit(timeout);
    }

    private sealed record VisioConnectionMap(IReadOnlyDictionary<int, (int? From, int? To)> Endpoints, int Count)
    {
        public bool TryGetValue(int shapeId, out (int? From, int? To) endpoints) => Endpoints.TryGetValue(shapeId, out endpoints);
    }

    private sealed class TextBudget
    {
        private int _bytes;

        public void Add(string value)
        {
            try
            {
                var bytes = StrictUtf8.GetByteCount(value);
                if (bytes > VisioDocumentPreflight.MaximumExtractedUtf8Bytes - _bytes)
                {
                    throw new RetainedProcessorException("visio-document-output-too-large");
                }
                _bytes += bytes;
            }
            catch (EncoderFallbackException exception)
            {
                throw new RetainedProcessorException("visio-document-text-not-utf8", innerException: exception);
            }
        }
    }

    private sealed class PrivateInput(string path, VerifiedNativeDirectory parent, string name,
        NativeFileIdentity identity, FileStream handle)
    {
        public string Path { get; } = path;

        public void DisposeAndDelete()
        {
            handle.Dispose();
            try
            {
                var deletion = new HandleRelativeNativeFileSystem().DeleteLiteralChildAsync(parent, name, identity)
                    .AsTask().GetAwaiter().GetResult();
                if (!deletion.Changed) throw new IOException("visio-private-input-cleanup-refused");
            }
            finally { parent.Dispose(); }
            Directory.Delete(parent.CanonicalPath);
        }
    }
}

internal static class CancellationTokenTaskExtensions
{
    public static Task AsTask(this CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled) return Task.Delay(Timeout.InfiniteTimeSpan);
        return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
