using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
public static class WindowsSourceAccessAdministration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };

    // Preparation is observational. It cannot verify the IIS token; normal guarded
    // reads and broker AccessCheck perform that verification after approved activation.
    public static byte[] PlanRootReadGrant(byte[] request)
    {
        var draft = Parse<RootReadRequest>(request);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(draft.RootPath));
        var protectedRoots = MandatoryProtectedRoots(draft.ProtectedRoots).ToArray();
        _ = ValidateScope(path, new string('a', 64), draft.IndexerSid, protectedRoots);
        using var directory = WindowsSourcePathAccess.OpenDirectoryPath(path, readPermissions: true);
        EnsureFixedNtfs(path);
        var root = new ApprovedSourceAccessRoot(path, directory.Identity.IdentityFingerprint);
        var before = SourceReadGrant.Read(directory.Handle);
        RefuseReadDenies(before);
        var proposed = SourceReadGrant.Propose(before, new SecurityIdentifier(draft.IndexerSid), inherit: true);
        directory.ValidateCurrent();
        return JsonSerializer.SerializeToUtf8Bytes(new RootReadPlan(1, path, root.IdentityFingerprint, draft.IndexerSid,
            Convert.ToBase64String(before), Hash(proposed), protectedRoots, ActualIndexerReadVerified: false), JsonOptions);
    }

    // Operator-only one-root provisioning. Never exposed through the IIS repair pipe.
    // The supplied hash binds this action to the concrete plan approved by the operator.
    public static byte[] ApplyRootReadGrant(string brokerRoot, string approvedPlanSha256)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("SourceAccessAdministratorRequired");
        using var storage = new SourceAccessBrokerStorage(brokerRoot);
        var bytes = storage.ReadRootReadPlan();
        if (!string.Equals(Hash(bytes), approvedPlanSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SourceAccessRootPlanApprovalMismatch");
        var plan = Parse<RootReadPlan>(bytes);
        if (plan.Version != 1 || plan.ActualIndexerReadVerified) throw new InvalidDataException("SourceAccessRootPlanInvalid");
        var root = ValidateScope(plan.RootPath, plan.RootIdentityFingerprint, plan.IndexerSid,
            MandatoryProtectedRoots(plan.ProtectedRoots).Append(storage.RootPath).ToArray());
        var activePolicy = storage.ReadPolicy();
        if (activePolicy.IndexerSid != plan.IndexerSid || activePolicy.Resolve(new(plan.RootPath, plan.RootIdentityFingerprint, ".", true))
                is not { InheritableRead: true })
            throw new UnauthorizedAccessException("SourceAccessRootPlanNotApprovedByPolicy");
        EnsureFixedNtfs(root.Path);
        var before = Convert.FromBase64String(plan.BeforeDescriptor);
        RefuseReadDenies(before);
        var reader = new SecurityIdentifier(plan.IndexerSid);
        var proposed = SourceReadGrant.Propose(before, reader, inherit: true);
        if (Hash(proposed) != plan.ExpectedAfterSha256) throw new InvalidDataException("SourceAccessRootPlanInvalid");
        using var target = WindowsSourcePathAccess.OpenPermissionTarget(root, ".", true);
        if (!SourceReadGrant.Read(target.Handle).AsSpan().SequenceEqual(before))
            throw new InvalidDataException("SourceAccessRootPermissionsChangedSincePlan");
        var intentPath = Path.Combine(storage.RootPath, "root-read-grant.pending.json");
        var created = false;
        var attempted = false;
        try
        {
            using (var file = new FileStream(intentPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(file, new { plan.RootPath, plan.RootIdentityFingerprint, plan.IndexerSid,
                    PlanSha256 = Hash(bytes), plan.BeforeDescriptor, plan.ExpectedAfterSha256 });
                file.Flush(flushToDisk: true);
            }
            target.ValidateCurrent(); storage.ValidateCurrent();
            var after = SourceReadGrant.Apply(target.Handle, before, reader, inherit: true, () => attempted = true);
            target.ValidateCurrent(); storage.ValidateCurrent();
            File.Delete(intentPath);
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                Succeeded = true, Changed = !before.AsSpan().SequenceEqual(after), plan.RootIdentityFingerprint,
                AfterSha256 = Hash(after), ActualIndexerReadVerified = false,
                ExistingChildPermissionsRewritten = false
            });
        }
        catch (Exception exception) when (attempted)
        { throw new IOException("SourceAccessRootGrantUnconfirmed: preserve the pending intent and verify before another write.", exception); }
        finally
        {
            if (created && !attempted) { storage.ValidateCurrent(); File.Delete(intentPath); }
        }
    }

    public static async Task ServeAsync(string brokerRoot, CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) throw new UnauthorizedAccessException("SourceAccessBrokerRequiresLocalSystem");
        using var storage = new SourceAccessBrokerStorage(brokerRoot);
        var policy = storage.ReadPolicy();
        var broker = new WindowsSourceAccessBroker(policy, storage.JournalRoot, storage: storage);
        var journal = storage.ReadJournalPolicy();
        if (journal is null)
            await SourceAccessPipeServer.RunAsync(policy, broker, cancellationToken).ConfigureAwait(false);
        else
            await Task.WhenAll(SourceJournalPipeServer.RunAsync(journal, policy.IndexerSid, storage.ValidateCurrent, cancellationToken),
                journal.RepairEnabled ? SourceAccessPipeServer.RunAsync(policy, broker, cancellationToken) : Task.CompletedTask).ConfigureAwait(false);
    }

    private static ApprovedSourceAccessRoot ValidateScope(string path, string fingerprint, string sid, string[] protectedRoots)
    {
        var policy = new SourceAccessPolicy(1, "FluxKnowledge.SourceAccess.preparation", sid, [new(path, fingerprint)], protectedRoots);
        return SourceAccessPolicy.Parse(JsonSerializer.SerializeToUtf8Bytes(policy)).Roots[0];
    }
    private static IEnumerable<string> MandatoryProtectedRoots(IEnumerable<string> paths) => paths.Concat(
            [AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"J:\Models"])
        .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).Distinct(StringComparer.OrdinalIgnoreCase);
    private static void EnsureFixedNtfs(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("SourceAccessRequiresFixedNtfs");
    }
    private static void RefuseReadDenies(byte[] descriptor)
    {
        var dacl = new RawSecurityDescriptor(descriptor, 0).DiscretionaryAcl;
        if (dacl is not null && dacl.Cast<GenericAce>().OfType<CommonAce>().Any(ace =>
                ace.AceQualifier == AceQualifier.AccessDenied && (ace.AceFlags & AceFlags.InheritOnly) == 0 &&
                (SourceReadGrant.MapGenericFileRights(ace.AccessMask) & SourceReadGrant.ReadMask) != 0))
            throw new UnauthorizedAccessException("SourceAccessRootReadDenyRequiresReview");
    }
    private static T Parse<T>(byte[] bytes)
    {
        if (bytes.Length is 0 or > 2 * 1024 * 1024) throw new InvalidDataException("SourceAccessRootPlanInvalid");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        SourceAccessPipeProtocol.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("SourceAccessRootPlanInvalid");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record RootReadRequest(string RootPath, string IndexerSid, string[] ProtectedRoots);
    private sealed record RootReadPlan(int Version, string RootPath, string RootIdentityFingerprint, string IndexerSid,
        string BeforeDescriptor, string ExpectedAfterSha256, string[] ProtectedRoots, bool ActualIndexerReadVerified);
}
