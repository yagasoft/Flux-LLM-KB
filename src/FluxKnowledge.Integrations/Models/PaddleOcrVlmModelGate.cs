using System.Security.Cryptography;
using FluxKnowledge.Application.Models;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Models;

/// <summary>
/// Opens the one approved PaddleOCR-VL model set from the fixed J: model store.  This is a
/// verification boundary only: it has no downloader, provider loader, cache fallback, or
/// configurable paths.  Successful verification retains all model files read-only for this
/// process lifetime.
/// </summary>
public sealed class PaddleOcrVlmModelGate : IDisposable
{
    public const string VlmRevision = "c5630abae1d940eafe0697512a0325494b02ab42";
    public const string LayoutRevision = "7b48a7566925fa464281f930c58eee04fe2c862a";
    public const string OrientationRevision = "7330ab7039123e46af2dc03154b9969aa412c61d";

    public const string VlmManifestPath =
        @"J:\Models\manifests\paddleocr-vl-1.6-20260920\paddleocr-vl-1.6.json";
    public const string LayoutManifestPath =
        @"J:\Models\manifests\paddleocr-vl-1.6-20260920\pp-doclayoutv3.json";
    public const string OrientationManifestPath =
        @"J:\Models\manifests\document-ocr-20260919\page-orientation.json";
    public const string PythonExecutablePath =
        @"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\Scripts\python.exe";

    internal const string VlmBundlePath =
        @"J:\Models\bundles\paddleocr-vl-1.6\c5630abae1d940eafe0697512a0325494b02ab42";
    internal const string LayoutBundlePath =
        @"J:\Models\bundles\pp-doclayoutv3\7b48a7566925fa464281f930c58eee04fe2c862a";
    internal const string OrientationBundlePath =
        @"J:\Models\runtimes\ppstructurev3-3.7.0-ort-1.30.0-cp312\bundles\page_orientation";

    private static readonly IReadOnlyList<FixedBundle> FixedBundles =
    [
        new(VlmManifestPath, VlmRevision, VlmBundlePath),
        new(LayoutManifestPath, LayoutRevision, LayoutBundlePath),
        new(OrientationManifestPath, OrientationRevision, OrientationBundlePath)
    ];

    private readonly ILocalModelStore _store;
    private readonly Func<string, int, CancellationToken, ValueTask<byte[]>> _readManifest;
    private readonly Func<IReadOnlyList<ModelBundleSpecification>, CancellationToken, ValueTask<IDisposable>> _openBundleLease;
    private readonly object _sync = new();
    private Task<PaddleOcrVlmModelGateResult>? _verification;
    private IReadOnlyList<VerifiedLocalModelLease>? _modelLeases;
    private IDisposable? _bundleLease;
    private bool _disposed;

    public PaddleOcrVlmModelGate(ILocalModelStore store)
        : this(
            store,
            static (path, maximumBytes, cancellationToken) => new ValueTask<byte[]>(
                WindowsModelVerificationFiles.ReadLocalManifestAsync(path, maximumBytes, cancellationToken)),
            VerifyFixedBundleCopiesAsync)
    {
    }

    internal PaddleOcrVlmModelGate(
        ILocalModelStore store,
        Func<string, int, CancellationToken, ValueTask<byte[]>> readManifest,
        Func<IReadOnlyList<ModelBundleSpecification>, CancellationToken, ValueTask<IDisposable>> openBundleLease)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _readManifest = readManifest ?? throw new ArgumentNullException(nameof(readManifest));
        _openBundleLease = openBundleLease ?? throw new ArgumentNullException(nameof(openBundleLease));
    }

    public ValueTask<PaddleOcrVlmModelGateResult> EnsureVerifiedAsync(CancellationToken cancellationToken)
    {
        Task<PaddleOcrVlmModelGateResult> verification;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            verification = _verification ??= VerifyAsync();
        }

        return new ValueTask<PaddleOcrVlmModelGateResult>(verification.WaitAsync(cancellationToken));
    }

    public void Dispose()
    {
        IReadOnlyList<VerifiedLocalModelLease>? modelLeases;
        IDisposable? bundleLease;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            modelLeases = _modelLeases;
            _modelLeases = null;
            bundleLease = _bundleLease;
            _bundleLease = null;
        }

        bundleLease?.Dispose();
        if (modelLeases is not null)
        {
            foreach (var lease in modelLeases) lease.Dispose();
        }
    }

    private async Task<PaddleOcrVlmModelGateResult> VerifyAsync()
    {
        var leases = new List<VerifiedLocalModelLease>(FixedBundles.Count);
        IDisposable? bundleLease = null;
        var succeeded = false;
        try
        {
            var specifications = new List<ModelBundleSpecification>(FixedBundles.Count);
            foreach (var bundle in FixedBundles)
            {
                ModelBundleSpecification specification;
                try
                {
                    var bytes = await _readManifest(
                            bundle.ManifestPath,
                            ModelManifestCodec.MaximumBytes,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    specification = ModelManifestCodec.Parse(bytes);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ModelManifestException or
                    ArgumentException or NotSupportedException)
                {
                    return Refused("document-ocr-model-manifest-invalid");
                }

                if (specification.Files.Any(file => !string.Equals(file.Revision, bundle.Revision, StringComparison.Ordinal)))
                {
                    return Refused("document-ocr-model-manifest-invalid");
                }

                ModelResolutionResult resolution;
                try
                {
                    resolution = await _store.ResolveAsync(specification, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return Refused("document-ocr-model-store-unavailable");
                }

                if (!resolution.Succeeded || !resolution.ReceiptPersisted || resolution.Lease is null)
                {
                    return Refused(ToOcrReason(resolution.ReasonCode));
                }

                leases.Add(resolution.Lease);
                specifications.Add(specification);
            }

            try
            {
                bundleLease = await _openBundleLease(specifications, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PaddleOcrModelGateException exception)
            {
                return Refused(exception.ReasonCode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Refused("document-ocr-model-bundle-unavailable");
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return Refused("document-ocr-model-gate-disposed");
                }

                _modelLeases = leases.ToArray();
                _bundleLease = bundleLease;
                bundleLease = null;
                succeeded = true;
            }

            return new PaddleOcrVlmModelGateResult(true, "document-ocr-models-verified");
        }
        finally
        {
            if (!succeeded)
            {
                bundleLease?.Dispose();
                foreach (var lease in leases) lease.Dispose();
            }
        }
    }

    private static async ValueTask<IDisposable> VerifyFixedBundleCopiesAsync(
        IReadOnlyList<ModelBundleSpecification> specifications,
        CancellationToken cancellationToken)
    {
        if (specifications.Count != FixedBundles.Count)
        {
            throw new PaddleOcrModelGateException("document-ocr-model-manifest-invalid");
        }

        var heldFiles = new List<NativeModelHeldReadFile>();
        try
        {
            for (var index = 0; index < FixedBundles.Count; index++)
            {
                var bundle = FixedBundles[index];
                var specification = specifications[index];
                if (specification.Files.Any(file => !string.Equals(file.Revision, bundle.Revision, StringComparison.Ordinal)))
                {
                    throw new PaddleOcrModelGateException("document-ocr-model-manifest-invalid");
                }

                foreach (var file in specification.Files)
                {
                    var held = await OpenAndVerifyAsync(bundle.BundlePath, file, cancellationToken).ConfigureAwait(false);
                    heldFiles.Add(held);
                }
            }

            heldFiles.Add(OpenPythonExecutable());
            return new PaddleOcrVlmBundleLease(heldFiles);
        }
        catch
        {
            foreach (var file in heldFiles) file.Dispose();
            throw;
        }
    }

    private static async ValueTask<NativeModelHeldReadFile> OpenAndVerifyAsync(
        string bundlePath,
        ModelArtifactSpecification specification,
        CancellationToken cancellationToken)
    {
        NativeModelDirectoryChain? parent = null;
        NativeModelHeldReadFile? file = null;
        try
        {
            var fileSystem = new HandleRelativeNativeFileSystem();
            parent = fileSystem.OpenModelDirectoryChain(bundlePath);
            file = fileSystem.TryOpenModelReadFile(parent.Leaf, specification.Filename, parent);
            if (file is null)
            {
                throw new PaddleOcrModelGateException("document-ocr-model-bundle-missing");
            }

            parent = null;
            if (file.ByteLength != specification.ByteLength)
            {
                throw new PaddleOcrModelGateException("document-ocr-model-bundle-integrity-failed");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            for (long offset = 0; offset < file.ByteLength;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = (int)Math.Min(buffer.Length, file.ByteLength - offset);
                var read = await file.ReadAsync(offset, buffer.AsMemory(0, expected), cancellationToken).ConfigureAwait(false);
                if (read != expected)
                {
                    throw new PaddleOcrModelGateException("document-ocr-model-bundle-integrity-failed");
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            var observed = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(observed, specification.Sha256, StringComparison.Ordinal))
            {
                throw new PaddleOcrModelGateException("document-ocr-model-bundle-integrity-failed");
            }

            var result = file;
            file = null;
            return result;
        }
        catch (NativeModelPathException exception)
        {
            throw new PaddleOcrModelGateException("document-ocr-model-path-unsafe", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new PaddleOcrModelGateException("document-ocr-model-bundle-missing", exception);
        }
        finally
        {
            file?.Dispose();
            parent?.Dispose();
        }
    }

    private static NativeModelHeldReadFile OpenPythonExecutable()
    {
        NativeModelDirectoryChain? parent = null;
        try
        {
            var directory = Path.GetDirectoryName(PythonExecutablePath);
            var filename = Path.GetFileName(PythonExecutablePath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filename))
            {
                throw new PaddleOcrModelGateException("document-ocr-model-path-unsafe");
            }

            var fileSystem = new HandleRelativeNativeFileSystem();
            parent = fileSystem.OpenModelDirectoryChain(directory);
            var file = fileSystem.TryOpenModelReadFile(parent.Leaf, filename, parent);
            if (file is null || file.ByteLength == 0)
            {
                file?.Dispose();
                throw new PaddleOcrModelGateException("document-ocr-model-runtime-missing");
            }

            parent = null;
            return file;
        }
        catch (NativeModelPathException exception)
        {
            throw new PaddleOcrModelGateException("document-ocr-model-path-unsafe", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new PaddleOcrModelGateException("document-ocr-model-runtime-missing", exception);
        }
        finally
        {
            parent?.Dispose();
        }
    }

    private static PaddleOcrVlmModelGateResult Refused(string reasonCode) =>
        new(false, string.IsNullOrWhiteSpace(reasonCode) ? "document-ocr-model-store-unavailable" : reasonCode);

    private static string ToOcrReason(string? reasonCode) =>
        reasonCode is { Length: > 0 and <= 100 } && reasonCode.StartsWith("model-", StringComparison.Ordinal)
            ? $"document-ocr-{reasonCode}"
            : "document-ocr-model-store-unavailable";

    private sealed record FixedBundle(string ManifestPath, string Revision, string BundlePath);

    private sealed class PaddleOcrVlmBundleLease(IReadOnlyList<NativeModelHeldReadFile> files) : IDisposable
    {
        private readonly IReadOnlyList<NativeModelHeldReadFile> _files = files;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var file in _files) file.Dispose();
        }
    }
}

public sealed record PaddleOcrVlmModelGateResult(bool Succeeded, string ReasonCode);

internal sealed class PaddleOcrModelGateException(string reasonCode, Exception? innerException = null)
    : InvalidOperationException(reasonCode, innerException)
{
    public string ReasonCode { get; } = reasonCode;
}
