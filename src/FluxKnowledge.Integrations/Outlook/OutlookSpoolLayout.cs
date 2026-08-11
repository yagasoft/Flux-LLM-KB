using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Integrations.Outlook;

/// <summary>Owns the only supported private spool transition: <c>_inflight/{id}</c> to <c>ready/{id}</c>.</summary>
public sealed class OutlookSpoolLayout
{
    private const string ManifestFileName = "manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;

    public OutlookSpoolLayout(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new ArgumentException("The Outlook spool root must be absolute.", nameof(configuredRoot));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        Directory.CreateDirectory(_root);
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(_root);
        Directory.CreateDirectory(Path.Combine(_root, "_inflight"));
        Directory.CreateDirectory(Path.Combine(_root, "ready"));
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.Combine(_root, "_inflight"));
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(Path.Combine(_root, "ready"));
    }

    public string CreateInflightExportDirectory(Guid exportId)
    {
        var path = GetInflightExportDirectory(exportId);
        Directory.CreateDirectory(path);
        EnsureContainedDirectory(path);
        return path;
    }

    public string GetInflightExportDirectory(Guid exportId) => ExportDirectory("_inflight", exportId);

    public string GetReadyExportDirectory(Guid exportId) => ExportDirectory("ready", exportId);

    public async Task WriteManifestAsync(string exportDirectory, OutlookExportManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        EnsureInflightDirectory(exportDirectory, manifest.ExportId);
        var hydrated = await HydrateAsync(exportDirectory, manifest, cancellationToken).ConfigureAwait(false);
        var manifestPath = Path.Combine(exportDirectory, ManifestFileName);
        await using var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, hydrated, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public async Task<string> PromoteAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var inflight = GetInflightExportDirectory(exportId);
        var ready = GetReadyExportDirectory(exportId);
        if (Directory.Exists(ready))
        {
            _ = await ReadReadyManifestAsync(exportId, cancellationToken).ConfigureAwait(false);
            return ready;
        }

        EnsureInflightDirectory(inflight, exportId);
        _ = await ReadManifestEnvelopeAsync(inflight, exportId, validateSidecars: true, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(ready))
        {
            throw new IOException("An Outlook ready export already exists.");
        }

        Directory.Move(inflight, ready);
        EnsureContainedDirectory(ready);
        return ready;
    }

    public async Task<OutlookExportManifest> ReadReadyManifestAsync(Guid exportId, CancellationToken cancellationToken) =>
        (await ReadReadyManifestEnvelopeAsync(exportId, cancellationToken).ConfigureAwait(false)).Manifest;

    public Task<VerifiedOutlookReadyManifest> ReadReadyManifestEnvelopeAsync(Guid exportId, CancellationToken cancellationToken) =>
        ReadManifestEnvelopeAsync(GetReadyExportDirectory(exportId), exportId, validateSidecars: true, cancellationToken);

    public Task<VerifiedOutlookReadyManifest> ReadReadyRecoveryEnvelopeAsync(Guid exportId, CancellationToken cancellationToken) =>
        ReadManifestEnvelopeAsync(GetReadyExportDirectory(exportId), exportId, validateSidecars: false, cancellationToken);

    public async Task<OutlookExportCommitRequest> ReadReadyIngestionRequestAsync(
        Guid exportId,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadReadyRecoveryEnvelopeAsync(exportId, cancellationToken).ConfigureAwait(false);
        return envelope.Manifest.Recovery.ToCommitRequest(exportId, envelope.ManifestHash);
    }

    /// <summary>
    /// Rebinds a verified promoted export to a fresh fenced catch-up claim without rewriting any
    /// content sidecar. This is the only supported recovery path after an owner dies post-promotion.
    /// </summary>
    public async Task RebindReadyRecoveryAsync(
        Guid exportId,
        OutlookReadyExportRecovery recovery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        _ = recovery.ToCommitRequest(exportId, new string('0', 64));
        var readyDirectory = GetReadyExportDirectory(exportId);
        var envelope = await ReadManifestEnvelopeAsync(
            readyDirectory,
            exportId,
            validateSidecars: true,
            cancellationToken).ConfigureAwait(false);
        if (envelope.Manifest.Recovery == recovery)
        {
            return;
        }

        var rebound = envelope.Manifest with { Recovery = recovery };
        var manifestPath = Path.Combine(readyDirectory, ManifestFileName);
        var temporaryPath = Path.Combine(readyDirectory, $".{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, rebound, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<VerifiedOutlookReadyManifest> ReadManifestEnvelopeAsync(
        string exportDirectory,
        Guid exportId,
        bool validateSidecars,
        CancellationToken cancellationToken)
    {
        EnsureContainedDirectory(exportDirectory);
        VerifiedContainedFile manifestFile;
        try
        {
            manifestFile = await ContainedFileReader.ReadAsync(
                exportDirectory,
                ManifestFileName,
                1024 * 1024,
                cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw new OutlookReadyExportValidationException("ready-manifest-missing", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new OutlookReadyExportValidationException("ready-manifest-path-invalid", exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new OutlookReadyExportValidationException("ready-manifest-invalid", exception);
        }

        OutlookExportManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<OutlookExportManifest>(manifestFile.Bytes, JsonOptions)
                ?? throw new JsonException("The manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new OutlookReadyExportValidationException("ready-manifest-invalid", exception);
        }
        if (manifest.ExportId != exportId || manifest.Attachments is null || manifest.Recovery is null)
        {
            throw new OutlookReadyExportValidationException("ready-manifest-identity-mismatch");
        }

        if (validateSidecars)
        {
            var hydrated = await HydrateAsync(exportDirectory, manifest, cancellationToken).ConfigureAwait(false);
            if (!Matches(manifest, hydrated))
            {
                throw new OutlookReadyExportValidationException("ready-sidecar-checksum-invalid");
            }
        }

        return new VerifiedOutlookReadyManifest(manifest, manifestFile.ContentSha256, manifestFile.ByteLength);
    }

    private static bool Matches(OutlookExportManifest expected, OutlookExportManifest actual) =>
        expected.ExportId == actual.ExportId &&
        expected.Recovery == actual.Recovery &&
        expected.Body == actual.Body &&
        expected.Attachments.Count == actual.Attachments.Count &&
        expected.Attachments.SequenceEqual(actual.Attachments);

    private static async Task<OutlookExportManifest> HydrateAsync(string exportDirectory, OutlookExportManifest manifest, CancellationToken cancellationToken)
    {
        var body = await OutlookExportSidecar.ReadAndHashAsync(exportDirectory, manifest.Body, cancellationToken).ConfigureAwait(false);
        var attachments = new List<OutlookExportSidecar>(manifest.Attachments.Count);
        foreach (var attachment in manifest.Attachments)
        {
            attachments.Add(await OutlookExportSidecar.ReadAndHashAsync(exportDirectory, attachment, cancellationToken).ConfigureAwait(false));
        }

        if (attachments.Select(value => value.RelativePath).Append(body.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachments.Count + 1)
        {
            throw new OutlookReadyExportValidationException("ready-sidecar-path-invalid");
        }

        return manifest with { Body = body, Attachments = attachments };
    }

    private string ExportDirectory(string state, Guid exportId)
    {
        if (exportId == Guid.Empty)
        {
            throw new ArgumentException("An Outlook export identifier is required.", nameof(exportId));
        }

        return Path.Combine(_root, state, exportId.ToString("N"));
    }

    private void EnsureInflightDirectory(string path, Guid exportId)
    {
        if (!string.Equals(Path.GetFullPath(path), GetInflightExportDirectory(exportId), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("An Outlook manifest can only be written to its inflight export directory.");
        }

        EnsureContainedDirectory(path);
    }

    private void EnsureContainedDirectory(string path)
    {
        var canonical = Path.GetFullPath(path);
        if (!canonical.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Outlook spool path escapes its configured root.");
        }

        PhysicalFileIdentity.EnsureNoReparsePointTraversal(canonical);
    }
}

public sealed record VerifiedOutlookReadyManifest(
    OutlookExportManifest Manifest,
    string ManifestHash,
    long ByteLength);

public sealed class OutlookReadyExportValidationException(string reasonCode, Exception? innerException = null)
    : Exception("The Outlook ready export failed bounded validation.", innerException)
{
    public string ReasonCode { get; } = reasonCode;
}
