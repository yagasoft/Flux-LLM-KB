using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(262153)]
    public async Task Stable_chunked_hash_attests_exact_bytes_and_accounts_for_each_budgeted_read(int length)
    {
        using var fixture = new Fixture();
        var content = new byte[length]; RandomNumberGenerator.Fill(content);
        await File.WriteAllBytesAsync(fixture.Path, content);
        var budget = new ProbeBudget();
        var before = DateTimeOffset.UtcNow;
        var diagnostic = "No interval rejection";
        SourceVerificationDigest digest;
        try
        {
            digest = await new WindowsSourceVerificationContentReader(TimeProvider.System)
                { IntervalRejected = evidence => diagnostic = evidence?.ToString() ?? "Native completion not yet observed" }
                .ReadAsync(RootFor(fixture), "ancestor/file.txt", budget, default);
        }
        catch (SourceSnapshotChangedException error)
        { throw new Xunit.Sdk.XunitException(error + Environment.NewLine + diagnostic); }
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), digest.Sha256);
        Assert.Equal(length, digest.BytesRead);
        Assert.Equal(length, budget.Chunks.Sum());
        Assert.Equal(length, budget.Reads.Sum(x => x.Bytes));
        Assert.All(budget.Reads, read => Assert.Equal(SourceVerificationReadKind.Source, read.Kind));
        Assert.All(budget.Chunks, n => Assert.InRange(n, 1, 128 * 1024));
        Assert.InRange(digest.AttestedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(PhysicalFileIdentity.Get(fixture.Path), digest.Observation.Identity);
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Repeated_timestamp_restored_write_during_credit_wait_has_no_successful_digest()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Path, new byte[256 * 1024]);
        using var persistent = File.Open(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var stamp = File.GetLastWriteTimeUtc(fixture.Path);
        var budget = new ProbeBudget(async (turn, ct) =>
        {
            if (turn != 2) return;
            await Task.Run(() =>
            {
                File.WriteAllBytes(fixture.Path, Enumerable.Repeat((byte)'z', 256 * 1024).ToArray());
                File.SetLastWriteTimeUtc(fixture.Path, stamp);
            }, ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
        });
        await Assert.ThrowsAsync<SourceSnapshotChangedException>(() =>
            new WindowsSourceVerificationContentReader(TimeProvider.System)
                .ReadAsync(RootFor(fixture), "ancestor/file.txt", budget, default).AsTask());
        Assert.Equal(2, budget.Chunks.Count);
    }

    [Fact]
    public async Task Ancestor_move_during_credit_wait_completes_without_waiting_for_hash_continuation()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Path, new byte[256 * 1024]);
        var moved = fixture.Directory + "-moved";
        var budget = new ProbeBudget(async (turn, ct) =>
        {
            if (turn == 2)
                await Task.Run(() => Directory.Move(fixture.Directory, moved), ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
        });
        try
        {
            await Assert.ThrowsAsync<SourceSnapshotChangedException>(() =>
                new WindowsSourceVerificationContentReader(TimeProvider.System)
                    .ReadAsync(RootFor(fixture), "ancestor/file.txt", budget, default).AsTask());
            Assert.True(Directory.Exists(moved));
        }
        finally { if (Directory.Exists(moved)) Directory.Move(moved, fixture.Directory); }
    }

    [Fact]
    public async Task Cancellation_at_chunk_boundary_retires_the_request_and_releases_source_handles()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.Path, new byte[256 * 1024]);
        using var cancellation = new CancellationTokenSource();
        var budget = new ProbeBudget((turn, _) =>
        { if (turn == 2) cancellation.Cancel(); return ValueTask.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsSourceVerificationContentReader(TimeProvider.System)
                .ReadAsync(RootFor(fixture), "ancestor/file.txt", budget, cancellation.Token).AsTask());
        Assert.Equal(128 * 1024, budget.Reads.Sum(x => x.Bytes));
        Assert.Equal(2, budget.Chunks.Count);
        using var exclusive = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static SourceRootConfiguration RootFor(Fixture fixture) =>
        SourceRootConfiguration.Restore(SourceRootId.New(), fixture.Root, "Verification fixture", true, false,
            1024 * 1024, [], [], [], TimeSpan.FromMinutes(15), SourceRootState.Enabled, 1,
            physicalIdentityFingerprint: PhysicalFileIdentity.GetDirectory(fixture.Root).IdentityFingerprint,
            requiresPhysicalIdentityValidation: true);

    private sealed class ProbeBudget(Func<int, CancellationToken, ValueTask>? before = null) : ISourceVerificationReadBudget
    {
        internal List<int> Chunks { get; } = [];
        internal List<(SourceVerificationReadKind Kind, int Bytes)> Reads { get; } = [];
        public void RecordRead(SourceVerificationReadKind kind, int bytes) => Reads.Add((kind, bytes));
        public async ValueTask AcquireAsync(int bytes, CancellationToken cancellationToken)
        {
            Chunks.Add(bytes);
            if (before is not null) await before(Chunks.Count, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
