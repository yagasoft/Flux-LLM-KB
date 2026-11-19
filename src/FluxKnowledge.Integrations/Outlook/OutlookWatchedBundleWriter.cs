using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Integrations.Files;

namespace FluxKnowledge.Integrations.Outlook;

public sealed record OutlookWatchedAttachment(
    string? FileName, Func<CancellationToken, Task<ReadOnlyMemory<byte>>> ReadContentAsync, string? ContentType = null)
{
    public OutlookWatchedAttachment(string? fileName, ReadOnlyMemory<byte> content, string? contentType = null)
        : this(fileName, _ => Task.FromResult(content), contentType)
    {
    }
}

public sealed record OutlookWatchedBundle(
    Guid ExportId,
    string? Subject,
    string? Sender,
    DateTimeOffset? ReceivedAtUtc,
    string Body,
    IReadOnlyList<OutlookWatchedAttachment> Attachments);

public sealed record OutlookWatchedBundleFile(string RelativePath, string Sha256, long ByteLength);
public sealed record OutlookWatchedBundleReceipt(Guid ExportId, Guid StageId, IReadOnlyList<OutlookWatchedBundleFile> Files);

/// <summary>Publishes one immutable bundle from a private sibling staging location.</summary>
public sealed class OutlookWatchedBundleWriter
{
    private readonly string _watchRoot;
    private readonly string _stageRoot;
    private readonly string? _watchIdentity;
    private readonly string? _stageIdentity;

    public OutlookWatchedBundleWriter(
        string watchRoot, string stageRoot, string? watchIdentity = null, string? stageIdentity = null)
    {
        _watchRoot = RequireLocalRoot(watchRoot);
        _stageRoot = RequireLocalRoot(stageRoot);
        _watchIdentity = watchIdentity;
        _stageIdentity = stageIdentity;
        if (!string.Equals(Path.GetPathRoot(_watchRoot), Path.GetPathRoot(_stageRoot), StringComparison.OrdinalIgnoreCase) ||
            IsAtOrUnder(_watchRoot, _stageRoot) || IsAtOrUnder(_stageRoot, _watchRoot))
        {
            throw new ArgumentException("Outlook export staging and the watched directory must be separate locations on the same local volume.");
        }
    }

    public async Task<bool> PublishAsync(
        OutlookWatchedBundle bundle,
        CancellationToken cancellationToken,
        Func<OutlookWatchedBundleReceipt, CancellationToken, Task<OutlookWatchedBundleReceipt>>? beforePromotion = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentOutOfRangeException.ThrowIfEqual(bundle.ExportId, Guid.Empty);
        using var watchLease = OpenBoundDirectory(_watchRoot, _watchIdentity);
        using var stageLease = OpenBoundDirectory(_stageRoot, _stageIdentity);
        var stageId = Guid.NewGuid();
        var target = Path.Combine(watchLease.Identity.CanonicalPath, bundle.ExportId.ToString("N"));
        var staging = Path.Combine(stageLease.Identity.CanonicalPath, $"{bundle.ExportId:N}-{stageId:N}");
        var retainStaging = false;
        Directory.CreateDirectory(staging);
        try
        {
            var proof = await WriteStageAsync(bundle, stageId, staging, cancellationToken).ConfigureAwait(false);
            if (beforePromotion is null)
                return await PromoteAsync(staging, target, proof, cancellationToken).ConfigureAwait(false);

            // The SQL commit may succeed even if the caller never receives its result.
            // Keep this private stage until SQL proves a different staged bundle won.
            retainStaging = true;
            var selected = await beforePromotion(proof, cancellationToken).ConfigureAwait(false);
            if (selected.ExportId != bundle.ExportId)
                throw new InvalidDataException("The committed Outlook bundle belongs to a different export.");
            retainStaging = selected.StageId == proof.StageId;
            if (retainStaging)
                return await PromoteAsync(staging, target, selected, cancellationToken).ConfigureAwait(false);

            return await ResumeWithLeasesAsync(selected, watchLease, stageLease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!retainStaging && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<bool> ResumeAsync(OutlookWatchedBundleReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var watchLease = OpenBoundDirectory(_watchRoot, _watchIdentity);
        using var stageLease = OpenBoundDirectory(_stageRoot, _stageIdentity);
        return await ResumeWithLeasesAsync(receipt, watchLease, stageLease, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ResumeWithLeasesAsync(
        OutlookWatchedBundleReceipt receipt, PhysicalDirectoryLease watchLease, PhysicalDirectoryLease stageLease,
        CancellationToken cancellationToken)
    {
        var target = Destination(watchLease, receipt);
        if (Directory.Exists(target))
        {
            await VerifyBundleAsync(target, receipt, cancellationToken).ConfigureAwait(false);
            return false;
        }
        var staging = Staging(stageLease, receipt);
        try
        {
            await VerifyBundleAsync(staging, receipt, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (Directory.Exists(target))
        {
            await VerifyBundleAsync(target, receipt, cancellationToken).ConfigureAwait(false);
            return false;
        }
        return await PromoteAsync(staging, target, receipt, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> PromoteAsync(
        string staging, string target, OutlookWatchedBundleReceipt receipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.Move(staging, target);
            return true;
        }
        catch (IOException) when (Directory.Exists(target))
        {
            await VerifyBundleAsync(target, receipt, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    private static PhysicalDirectoryLease OpenBoundDirectory(string path, string? expected)
    {
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(path);
        var lease = PhysicalFileIdentity.OpenDirectoryLease(path);
        if (expected is null || string.Equals(lease.Identity.IdentityFingerprint, expected, StringComparison.Ordinal))
            return lease;
        lease.Dispose();
        throw new InvalidDataException("The physical Outlook export directory changed since it was bound.");
    }

    private static string Destination(PhysicalDirectoryLease watchLease, OutlookWatchedBundleReceipt receipt) =>
        Path.Combine(watchLease.Identity.CanonicalPath, receipt.ExportId.ToString("N"));

    private static string Staging(PhysicalDirectoryLease stageLease, OutlookWatchedBundleReceipt receipt) =>
        Path.Combine(stageLease.Identity.CanonicalPath, $"{receipt.ExportId:N}-{receipt.StageId:N}");

    private static string Fingerprint(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<OutlookWatchedBundleReceipt> WriteStageAsync(
        OutlookWatchedBundle bundle, Guid stageId, string staging, CancellationToken cancellationToken)
    {
        var files = new List<OutlookWatchedBundleFile>(bundle.Attachments.Count + 1);
        var text = new StringBuilder();
        AddHeader(text, "Subject", bundle.Subject);
        AddHeader(text, "Sender", bundle.Sender);
        AddHeader(text, "Received", bundle.ReceivedAtUtc?.ToUniversalTime().ToString("O"));
        if (text.Length != 0) text.Append('\n');
        using var body = new StringReader(bundle.Body);
        while (body.ReadLine() is { } line)
            text.Append("    ").Append(line).Append('\n');

        if (bundle.Attachments.Count > 0)
        {
            Directory.CreateDirectory(Path.Combine(staging, "attachments"));
            text.Append("\n## Attachments\n\n");
            for (var index = 0; index < bundle.Attachments.Count; index++)
            {
                var attachment = bundle.Attachments[index];
                var bytes = await attachment.ReadContentAsync(cancellationToken).ConfigureAwait(false);
                var fileName = $"{index + 1:D3}-{SafeFileName(attachment, bytes.Span)}";
                files.Add(await WriteFileAsync(staging, Path.Combine("attachments", fileName), bytes,
                    cancellationToken).ConfigureAwait(false));
                text.Append("- [").Append(EscapeMarkdown(fileName)).Append("](attachments/")
                    .Append(Uri.EscapeDataString(fileName)).Append(")\n");
            }
        }
        files.Add(await WriteFileAsync(staging, "message.md", Encoding.UTF8.GetBytes(text.ToString()),
            cancellationToken).ConfigureAwait(false));
        return new OutlookWatchedBundleReceipt(bundle.ExportId, stageId, files);
    }

    private static async Task<OutlookWatchedBundleFile> WriteFileAsync(
        string staging, string relativePath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path.Combine(staging, relativePath), FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        return new OutlookWatchedBundleFile(relativePath, Fingerprint(bytes.Span), bytes.Length);
    }

    private static async Task VerifyBundleAsync(
        string target,
        OutlookWatchedBundleReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.ExportId == Guid.Empty || receipt.StageId == Guid.Empty || receipt.Files is not { Count: > 0 } files ||
            files.Count(file => file.RelativePath == "message.md") != 1 ||
            files.Select(file => file.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("The persisted Outlook bundle receipt is invalid.");
        PhysicalFileIdentity.EnsureNoReparsePointTraversal(target);
        using var targetLease = PhysicalFileIdentity.OpenDirectoryLease(target);
        var canonicalTarget = targetLease.Identity.CanonicalPath;
        var expectedEntries = files.Count == 1 ? 1 : 2;
        if (Directory.EnumerateFileSystemEntries(canonicalTarget).Take(expectedEntries + 1).Count() != expectedEntries)
            throw new InvalidDataException("The existing Outlook export does not match the committed bundle.");

        if (files.Count > 1)
        {
            var attachments = Path.Combine(canonicalTarget, "attachments");
            PhysicalFileIdentity.EnsureNoReparsePointTraversal(attachments);
            if (Directory.EnumerateFileSystemEntries(attachments).Take(files.Count).Count() != files.Count - 1)
                throw new InvalidDataException("The existing Outlook export attachment set does not match the committed bundle.");
        }
        foreach (var file in files)
        {
            if (file.RelativePath != "message.md" &&
                !string.Equals(Path.GetDirectoryName(file.RelativePath), "attachments", StringComparison.Ordinal))
                throw new InvalidDataException("The persisted Outlook bundle contains an invalid file path.");
            _ = await ContainedFileReader.ReadAsync(canonicalTarget, file.RelativePath, file.ByteLength, cancellationToken,
                file.Sha256, file.ByteLength).ConfigureAwait(false);
        }
    }

    private static string RequireLocalRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Outlook export requires an absolute local directory.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsAtOrUnder(string root, string candidate) =>
        string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(OutlookWatchedAttachment attachment, ReadOnlySpan<byte> bytes)
    {
        var leaf = attachment.FileName?.Split('/', '\\').LastOrDefault()?.Trim(' ', '.') ?? string.Empty;
        var name = new string(leaf.Select(value =>
            char.IsControl(value) || char.GetUnicodeCategory(value) == System.Globalization.UnicodeCategory.Format || value is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : value).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) return "attachment" + HistoricalExtension(attachment.ContentType, bytes);
        if (name.Length <= 100) return name;
        var extension = Path.GetExtension(name);
        if (extension.Length > 16) extension = string.Empty;
        return name[..Math.Min(name.Length - extension.Length, 100 - extension.Length)].TrimEnd(' ', '.') + extension;
    }

    private static string HistoricalExtension(string? contentType, ReadOnlySpan<byte> bytes) =>
        contentType?.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
            "application/vnd.ms-visio.drawing" => ".vsdx",
            "application/zip" => ".zip",
            "text/plain" => ".txt",
            "text/csv" => ".csv",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/tiff" => ".tif",
            _ when bytes.StartsWith("%PDF-"u8) => ".pdf",
            _ => ".bin"
        };

    private static void AddHeader(StringBuilder text, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var line = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        if (line.Length != 0) text.Append("**").Append(label).Append("** ").Append(EscapeMarkdown(line)).Append('\n');
    }

    private static string EscapeMarkdown(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '[' or ']' or '*' or '_' or '`' or '<' or '>' or '&')
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }
}
