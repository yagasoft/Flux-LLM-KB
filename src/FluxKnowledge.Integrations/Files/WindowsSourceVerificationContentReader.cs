using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
public sealed class WindowsSourceVerificationContentReader(TimeProvider time) : ISourceVerificationContentReader
{
    internal Action<int>? SourceBytesRead { get; set; }
    internal Action<WindowsSourceVerificationOplock.NativeCompletion?>? IntervalRejected { get; init; }
    internal Action<WindowsSourceVerificationOplock>? ConfigureNativeLease { get; init; }

    public async ValueTask<SourceVerificationDigest> ReadAsync(SourceRootConfiguration root, string relativePath,
        ISourceVerificationReadBudget budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root.State != SourceRootState.Enabled || string.IsNullOrWhiteSpace(root.PhysicalIdentityFingerprint))
            throw new SourceSnapshotChangedException("SourceVerificationRootNotAdmitted");
        var path = Path.GetFullPath(relativePath, root.CanonicalPath);
        WindowsSourceVerificationOplock? lease = null;
        try
        {
            SourceFileObservation observation;
            using (var initial = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path, root.PhysicalIdentityFingerprint))
            {
                observation = WindowsSourceObservationReader.Describe(initial);
            }
            lease = WindowsSourceVerificationOplock.Acquire(root.CanonicalPath, path,
                root.PhysicalIdentityFingerprint, observation.Identity, cancellationToken, ConfigureNativeLease);
            using (var afterGrant = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path,
                root.PhysicalIdentityFingerprint, observation.Identity))
            {
                observation = WindowsSourceObservationReader.Describe(afterGrant);
                RequireUnbroken(lease);
                if (observation.ByteLength > root.MaximumFileBytes)
                    throw new IOException("SourceVerificationFileTooLarge");
            }

            var buffer = new byte[128 * 1024];
            using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (offset < observation.ByteLength)
            {
                var count = (int)Math.Min(buffer.Length, observation.ByteLength - offset);
                // No restrictive source or ancestor handle survives into token-credit waits.
                await budget.AcquireAsync(count, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                RequireUnbroken(lease);
                using (var current = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path,
                    root.PhysicalIdentityFingerprint, observation.Identity))
                {
                    Validate(current, observation);
                    // No read-ahead beyond this bounded chunk; this is not the oplock's handle.
                    await using var input = new FileStream(current.Handle, FileAccess.Read, 1, isAsync: true);
                    input.Position = offset;
                    var filled = 0;
                    while (filled < count)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(filled, count - filled), cancellationToken).ConfigureAwait(false);
                        if (read == 0) throw new SourceSnapshotChangedException("SourceVerificationUnexpectedEnd");
                        budget.RecordRead(SourceVerificationReadKind.Source, read);
                        SourceBytesRead?.Invoke(read);
                        filled += read;
                    }
                    Validate(current, observation);
                }
                checksum.AppendData(buffer, 0, count);
                offset += count;
                RequireUnbroken(lease);
            }

            DateTimeOffset attestedAt;
            cancellationToken.ThrowIfCancellationRequested();
            using (var final = WindowsSourcePathAccess.OpenRead(root.CanonicalPath, path,
                root.PhysicalIdentityFingerprint, observation.Identity))
            {
                Validate(final, observation);
                if (!lease.TryAttest()) throw new SourceSnapshotChangedException("SourceVerificationIntervalBroken");
                attestedAt = time.GetUtcNow();
            }
            // Local cancellation/terminal completion never holds source guards or a SQL transaction.
            if (!await lease.RetireAsync().ConfigureAwait(false))
                throw new SourceSnapshotChangedException("SourceVerificationIntervalBroken");
            cancellationToken.ThrowIfCancellationRequested();
            return new(Convert.ToHexStringLower(checksum.GetHashAndReset()), offset, observation, attestedAt);
        }
        finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
    }

    private void RequireUnbroken(WindowsSourceVerificationOplock lease)
    {
        if (lease.IsUnbroken()) return;
        IntervalRejected?.Invoke(lease.CompletionEvidence);
        throw new SourceSnapshotChangedException("SourceVerificationIntervalBroken");
    }

    private static void Validate(WindowsSourcePathAccess.SourceReadLease lease, SourceFileObservation expected)
    {
        lease.Parent.ValidateCurrent();
        WindowsSourcePathAccess.EnsureContentAvailable(WindowsSourcePathAccess.ReadMetadata(lease.Handle));
        if (WindowsSourceObservationReader.Describe(lease) != expected)
            throw new SourceSnapshotChangedException("SourceVerificationObservationChanged");
    }
}
