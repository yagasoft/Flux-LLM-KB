using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal sealed partial class SourceAccessBrokerStorage
{
    private static readonly JsonSerializerOptions GrantJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    internal FileInheritanceGrantLease? OpenFileInheritanceGrant(Guid sourceId)
    {
        ValidateCurrent();
        var name = GrantName(sourceId);
        SafeFileHandle handle;
        try { handle = OpenTrustedFile(_root, name, readData: true); }
        catch (FileNotFoundException) { return null; }

        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length is <= 0 or > 8192) throw new InvalidDataException("SourceInheritanceGrantInvalid");
            var data = new byte[checked((int)length)];
            var offset = 0;
            while (offset < data.Length)
            {
                var count = RandomAccess.Read(handle, data.AsSpan(offset), offset);
                if (count == 0) throw new EndOfStreamException();
                offset += count;
            }
            using var json = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 4 });
            SourceAccessPipeProtocol.RejectDuplicateProperties(json.RootElement);
            var grant = json.RootElement.Deserialize<FileInheritanceGrant>(GrantJson);
            if (grant is null || grant.SourceId != sourceId || grant.Generation == Guid.Empty || grant.Version != 1)
                throw new InvalidDataException("SourceInheritanceGrantInvalid");
            return new(handle, grant);
        }
        catch { handle.Dispose(); throw; }
    }

    internal FileInheritanceGrant ChangeFileInheritanceGrant(Guid sourceId, Guid expectedGeneration, bool enabled,
        string? sourcePath, string? fingerprint, IReadOnlyList<string> additionalProtectedRoots, string? changedBy = null)
    {
        var name = GrantName(sourceId);
        var policy = ReadPolicy();
        ValidateCurrent();
        using var writer = OpenGrantWriter();
        FileInheritanceGrant? current;
        using (var prior = OpenFileInheritanceGrant(sourceId)) current = prior?.Grant;
        if ((current?.Generation ?? Guid.Empty) != expectedGeneration)
            throw new InvalidDataException("SourceInheritanceGrantChanged");

        FileInheritanceGrant next;
        WindowsSourcePathAccess.DirectoryPathLease? source = null;
        try
        {
            if (enabled)
            {
                if (sourcePath is null || fingerprint is null || current is { Enabled: true })
                    throw new InvalidDataException("SourceInheritanceGrantInvalid");
                var proposed = new ApprovedSourceAccessRoot(sourcePath, fingerprint);
                if (policy.Roots.Any(root => root.RepairFileInheritance &&
                        string.Equals(root.Path, sourcePath, StringComparison.OrdinalIgnoreCase)) ||
                    !WindowsSourceAccessBroker.RootPermitted(proposed, policy, RootPath, additionalProtectedRoots))
                    throw new UnauthorizedAccessException("SourceInheritanceRootNotEligible");
                source = WindowsSourcePathAccess.OpenDirectoryPath(sourcePath);
                if (!string.Equals(source.Identity.IdentityFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("SourceInheritanceRootIdentityChanged");
                next = new(1, sourceId, Guid.NewGuid(), true, sourcePath, fingerprint, policy.IndexerSid);
            }
            else
            {
                if (current is not { Enabled: true } || sourcePath is not null || fingerprint is not null)
                    throw new InvalidDataException("SourceInheritanceGrantInvalid");
                next = current with { Generation = Guid.NewGuid(), Enabled = false };
            }
            next = next with { PreviousGeneration = expectedGeneration, ChangedBy = changedBy, ChangedAtUtc = DateTimeOffset.UtcNow };
            var temporaryName = "inheritance-pending-" + Guid.NewGuid().ToString("N") + ".json";
            var temporary = Path.Combine(RootPath, temporaryName);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, next, GrantJson);
                file.Flush(flushToDisk: true);
            }
            try
            {
                using (OpenTrustedFile(_root, temporaryName, readData: false)) { }
                ValidateCurrent();
                source?.ValidateCurrent();
                // The broker keeps its snapshot open without delete sharing until
                // the ACL result is verified. Publication fails while a repair holds it.
                File.Move(temporary, Path.Combine(RootPath, name), overwrite: current is not null);
                return next;
            }
            finally { File.Delete(temporary); }
        }
        finally { source?.Dispose(); }
    }

    private SafeFileHandle OpenGrantWriter()
    {
        const string name = "inheritance-writer.lock";
        var path = Path.Combine(RootPath, name);
        try
        {
            using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path)) { }

        var handle = WindowsSourcePathAccess.OpenRelative(_root.Handle, name, 0x120081u, 0, 0x60, path);
        try { ValidateFile(handle, path); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static string GrantName(Guid sourceId) => sourceId != Guid.Empty
        ? $"inheritance-{sourceId:N}.json" : throw new InvalidDataException("SourceInheritanceRegistrationInvalid");
}

internal sealed record FileInheritanceGrant(int Version, Guid SourceId, Guid Generation, bool Enabled, string Path,
    string IdentityFingerprint, string IndexerSid)
{
    public Guid PreviousGeneration { get; init; }
    public string? ChangedBy { get; init; }
    public DateTimeOffset ChangedAtUtc { get; init; }

    internal bool Matches(SourceAccessRepairRequest request, SourceAccessPolicy policy) =>
        string.Equals(Path, request.RootPath, StringComparison.OrdinalIgnoreCase) &&
        IdentityFingerprint == request.RootIdentityFingerprint && IndexerSid == policy.IndexerSid;
}

internal sealed class FileInheritanceGrantLease(SafeFileHandle handle, FileInheritanceGrant grant) : IDisposable
{
    internal FileInheritanceGrant Grant { get; } = grant;
    public void Dispose() => handle.Dispose();
}
