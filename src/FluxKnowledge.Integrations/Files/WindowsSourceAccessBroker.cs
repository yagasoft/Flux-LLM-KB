using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal sealed class WindowsSourceAccessBroker(SourceAccessPolicy policy, string journalRoot,
    Action<SourceAccessRepairPhase>? beforeVerification = null, SourceAccessBrokerStorage? storage = null)
{
    private readonly object _gate = new();

    internal SourceAccessRepairOutcome Repair(SourceAccessRepairRequest request, WindowsIdentity indexer, CancellationToken cancellationToken,
        long? deadlineUtcTicks = null)
    {
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineUtcTicks is not null && DateTime.UtcNow.Ticks >= deadlineUtcTicks)
                throw new OperationCanceledException("SourceAccessRequestDeadlineExpired", cancellationToken);
        }
        CheckBudget();
        lock (_gate)
        {
            CheckBudget();
            if (storage is not null && !string.Equals(Path.GetFullPath(journalRoot), storage.JournalRoot, StringComparison.OrdinalIgnoreCase))
                return SourceAccessRepairOutcome.Unavailable;
            if (indexer.User?.Value != policy.IndexerSid || policy.Version != 1) return SourceAccessRepairOutcome.NotApproved;
            var writeAttempted = false;
            string? createdIntent = null;
            try
            {
                storage?.ValidateCurrent();
                var root = policy.Resolve(request);
                if (root is null || !RootPermitted(root)) return SourceAccessRepairOutcome.NotApproved;
                using var target = WindowsSourcePathAccess.OpenPermissionTarget(root, request.RelativePath, request.IsDirectory);
                var before = SourceReadGrant.Read(target.Handle);
                var journalPath = JournalPath(root.IdentityFingerprint, target.Identity);
                if (SourceAccessTokenPermissions.CanRead(before, indexer))
                {
                    RetireExactVerifiedIntent(journalPath, before);
                    return SourceAccessRepairOutcome.AlreadyReadable;
                }
                if (SourceAccessTokenPermissions.HasApplicableDeny(before, indexer)) return SourceAccessRepairOutcome.ExplicitDeny;
                var inherited = request.IsDirectory && root.InheritableRead;
                var proposed = SourceReadGrant.Propose(before, new SecurityIdentifier(policy.IndexerSid), inherited);
                if (!SourceAccessTokenPermissions.CanRead(proposed, indexer)) return SourceAccessRepairOutcome.UnsupportedPermissions;
                CheckBudget();
                Directory.CreateDirectory(journalRoot);
                var intent = new RepairIntent(root.IdentityFingerprint, target.Identity, request.RelativePath,
                    Convert.ToBase64String(before), Hash(proposed));
                // An unresolved intent is not authority to replay an ACL write.
                using (var file = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    createdIntent = journalPath;
                    JsonSerializer.Serialize(file, intent);
                    file.Flush(flushToDisk: true);
                }
                beforeVerification?.Invoke(SourceAccessRepairPhase.IntentPersisted);
                storage?.ValidateCurrent();
                target.ValidateCurrent();
                CheckBudget();
                var after = SourceReadGrant.Apply(target.Handle, before, new SecurityIdentifier(policy.IndexerSid), inherited, () =>
                {
                    CheckBudget();
                    writeAttempted = true;
                });
                // Once the write has started, finish verification even if the request is cancelled.
                beforeVerification?.Invoke(SourceAccessRepairPhase.DescriptorWritten);
                target.ValidateCurrent();
                storage?.ValidateCurrent();
                if (!SourceAccessTokenPermissions.CanRead(after, indexer)) return SourceAccessRepairOutcome.Unconfirmed;
                storage?.ValidateJournalFile(journalPath);
                File.Delete(journalPath);
                return SourceAccessRepairOutcome.Repaired;
            }
            catch (Exception exception) when (writeAttempted && exception is not OperationCanceledException)
            { return SourceAccessRepairOutcome.Unconfirmed; }
            catch (SourcePathSecurityException) { return SourceAccessRepairOutcome.OutsideScope; }
            catch (SourceSnapshotChangedException) { return SourceAccessRepairOutcome.IdentityChanged; }
            catch (SourceContentUnavailableException) { return SourceAccessRepairOutcome.ContentUnavailable; }
            catch (UnauthorizedAccessException) { return SourceAccessRepairOutcome.UnsupportedPermissions; }
            catch (InvalidDataException exception)
            { return exception.Message == "SourceRepairUnconfirmed" ? SourceAccessRepairOutcome.Unconfirmed : SourceAccessRepairOutcome.UnsupportedPermissions; }
            catch (Win32Exception) { return SourceAccessRepairOutcome.UnsupportedPermissions; }
            catch (IOException) { return SourceAccessRepairOutcome.Unconfirmed; }
            finally
            {
                // Known pre-write failures have no unknown permission mutation to retain.
                if (!writeAttempted && createdIntent is not null)
                {
                    storage?.ValidateJournalFile(createdIntent);
                    File.Delete(createdIntent);
                }
            }
        }
    }

    private bool RootPermitted(ApprovedSourceAccessRoot root)
    {
        if (!Path.IsPathFullyQualified(root.Path) || root.Path != Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)) ||
            root.Path.StartsWith(@"\\", StringComparison.Ordinal) || root.IdentityFingerprint.Length != 64 ||
            !root.IdentityFingerprint.All(char.IsAsciiHexDigit)) return false;
        var protectedRoots = policy.ProtectedRoots.Concat([AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"J:\Models", storage?.RootPath ?? Path.GetFullPath(journalRoot)]);
        if (protectedRoots.Any(path => WindowsSourcePathAccess.IsWithin(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), root.Path) ||
                WindowsSourcePathAccess.IsWithin(root.Path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))))) return false;
        var drive = new DriveInfo(Path.GetPathRoot(root.Path)!);
        return drive.DriveType == DriveType.Fixed && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
    }

    private string JournalPath(string rootIdentity, string targetIdentity) => Path.Combine(journalRoot,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rootIdentity + "\n" + targetIdentity))) + ".json");
    private static string Hash(byte[] descriptor) => Convert.ToHexStringLower(SHA256.HashData(descriptor));
    private void RetireExactVerifiedIntent(string path, byte[] current)
    {
        if (!File.Exists(path)) return;
        storage?.ValidateJournalFile(path);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length > 2 * 1024 * 1024) return;
            var intent = JsonSerializer.Deserialize<RepairIntent>(file);
            if (intent?.ExpectedAfterSha256 != Hash(current)) return;
        }
        File.Delete(path);
    }
    private sealed record RepairIntent(string RootIdentity, string TargetIdentity, string RelativePath, string BeforeDescriptor, string ExpectedAfterSha256);
}

internal enum SourceAccessRepairPhase { IntentPersisted, DescriptorWritten }
