using System.Security.Cryptography;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class SourceVerificationOplockNativeTests
{
    [Fact]
    public async Task Retained_hash_time_is_historical_and_metadata_checks_cannot_refresh_it()
    {
        using var fixture = new Fixture();
        var content = new byte[64];
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var retained = Path.Combine(fixture.Root, "Retained");
        var clock = new RetainedClock();
        using var store = new ContentAddressedSourceArtifactStore(retained, timeProvider: clock);
        var relative = Seed(retained, hash, content);
        var hashedAt = clock.Now;
        await using (var verified = await store.OpenVerificationObservationAsync(relative, hash, content.Length, new ProbeBudget(), default))
        {
            Assert.Equal(hashedAt, verified.ChecksumVerifiedAtUtc);
            clock.Now += TimeSpan.FromDays(1);
            verified.ValidateCurrent();
            Assert.Equal(hashedAt, verified.ChecksumVerifiedAtUtc);
        }
        await using var metadata = await store.OpenObservationAsync(relative, hash, content.Length, false, default);
        Assert.Null(metadata.ChecksumVerifiedAtUtc);
    }

    private sealed class RetainedClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Real_budget_reports_completed_source_and_cancelled_retained_reads_separately_from_credit()
    {
        using var fixture = new Fixture();
        var content = new byte[256 * 1024];
        await File.WriteAllBytesAsync(fixture.Path, content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var retained = Path.Combine(fixture.Root, "Retained");
        using var cancellation = new CancellationTokenSource();
        using var store = new ContentAddressedSourceArtifactStore(retained) { RetainedBytesRead = _ => cancellation.Cancel() };
        var relative = Seed(retained, hash, content);
        using var budget = new SourceVerificationReadBudget(2 * 1024 * 1024);
        _ = await new WindowsSourceVerificationContentReader(TimeProvider.System)
            .ReadAsync(RootFor(fixture), "ancestor/file.txt", budget, default);
        Assert.Equal(content.Length, budget.SourceBytesRead);
        Assert.Equal(0, budget.RetainedBytesRead);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.OpenVerificationObservationAsync(relative, hash, content.Length, budget, cancellation.Token).AsTask());
        Assert.Equal(content.Length, budget.SourceBytesRead);
        Assert.Equal(128 * 1024, budget.RetainedBytesRead);
        using var exclusive = File.Open(Path.Combine(retained, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_empty_blob_never_returns_a_verified_observation(bool paced)
    {
        using var fixture = new Fixture();
        var hash = Convert.ToHexStringLower(SHA256.HashData([]));
        var retained = Path.Combine(fixture.Root, "Retained");
        using var store = new ContentAddressedSourceArtifactStore(retained);
        var relative = Seed(retained, hash, []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var observation = paced
                ? await store.OpenVerificationObservationAsync(relative, hash, 0, new ProbeBudget(), cancellation.Token)
                : await store.OpenObservationAsync(relative, hash, 0, true, cancellation.Token);
        });
        using var exclusive = File.Open(Path.Combine(retained, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_the_final_blob_read_never_returns_a_verified_observation(bool paced)
    {
        using var fixture = new Fixture();
        var content = new byte[64];
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var retained = Path.Combine(fixture.Root, "Retained");
        using var cancellation = new CancellationTokenSource();
        using var store = new ContentAddressedSourceArtifactStore(retained) { RetainedBytesRead = _ => cancellation.Cancel() };
        var relative = Seed(retained, hash, content);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var observation = paced
                ? await store.OpenVerificationObservationAsync(relative, hash, content.Length, new ProbeBudget(), cancellation.Token)
                : await store.OpenObservationAsync(relative, hash, content.Length, true, cancellation.Token);
        });
        using var exclusive = File.Open(Path.Combine(retained, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Source_and_retained_hashes_spend_the_same_read_budget_and_release_source_guards_between_them()
    {
        using var fixture = new Fixture();
        var content = RandomNumberGenerator.GetBytes(256 * 1024 + 9);
        await File.WriteAllBytesAsync(fixture.Path, content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var retained = Path.Combine(fixture.Root, "Retained");
        using var store = new ContentAddressedSourceArtifactStore(retained);
        var relative = Seed(retained, hash, content);
        var sourceBytes = 0; var retainedBytes = 0;
        using var counted = new ContentAddressedSourceArtifactStore(retained) { RetainedBytesRead = n => retainedBytes += n };
        var budget = new ProbeBudget();
        var result = await new WindowsSourceVerificationContentReader(TimeProvider.System) { SourceBytesRead = n => sourceBytes += n }
            .ReadAsync(RootFor(fixture), "ancestor/file.txt", budget, default);
        using (var sourceWriter = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(content.Length, sourceWriter.Length);
        await using (var observation = await counted.OpenVerificationObservationAsync(relative, hash, content.Length, budget, default))
            observation.ValidateCurrent();
        Assert.Equal(hash, result.Sha256);
        Assert.Equal(content.Length, sourceBytes);
        Assert.Equal(content.Length, retainedBytes);
        Assert.Equal(2 * content.Length, budget.Chunks.Sum());
        Assert.Equal(content.Length, budget.Reads.Where(x => x.Kind == SourceVerificationReadKind.Source).Sum(x => x.Bytes));
        Assert.Equal(content.Length, budget.Reads.Where(x => x.Kind == SourceVerificationReadKind.Retained).Sum(x => x.Bytes));
        Assert.All(budget.Chunks, size => Assert.InRange(size, 1, 128 * 1024));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("wrong-length")]
    public async Task Invalid_retained_content_never_returns_an_observation_or_repairs_the_blob(string failure)
    {
        using var fixture = new Fixture();
        var original = new byte[256 * 1024];
        var hash = Convert.ToHexStringLower(SHA256.HashData(original));
        var retained = Path.Combine(fixture.Root, "Retained");
        using var store = new ContentAddressedSourceArtifactStore(retained);
        var relative = Seed(retained, hash, original);
        var blob = Path.Combine(retained, relative);
        if (failure == "missing") File.Delete(blob);
        else if (failure == "corrupt")
        { var corrupt = original.ToArray(); corrupt[1] = 99; await File.WriteAllBytesAsync(blob, corrupt); }
        var before = File.Exists(blob) ? await File.ReadAllBytesAsync(blob) : null;
        var length = failure == "wrong-length" ? original.Length + 1 : original.Length;
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var observation = await store.OpenVerificationObservationAsync(relative, hash, length, new ProbeBudget(), default);
            observation.ValidateCurrent();
        });
        if (failure == "missing") Assert.IsType<FileNotFoundException>(error);
        else Assert.IsType<InvalidDataException>(error);
        if (before is null) Assert.False(File.Exists(blob));
        else Assert.Equal(before, await File.ReadAllBytesAsync(blob));
    }

    [Fact]
    public async Task Retained_hash_cancellation_releases_its_file_and_keeps_source_writable_during_credit_wait()
    {
        using var fixture = new Fixture();
        var content = new byte[256 * 1024];
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var retained = Path.Combine(fixture.Root, "Retained");
        using var store = new ContentAddressedSourceArtifactStore(retained);
        var relative = Seed(retained, hash, content);
        using var cancellation = new CancellationTokenSource();
        var budget = new ProbeBudget((turn, _) =>
        {
            if (turn == 2)
            {
                using var sourceWriter = File.Open(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                cancellation.Cancel();
            }
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.OpenVerificationObservationAsync(relative, hash, content.Length, budget, cancellation.Token).AsTask());
        Assert.Equal(128 * 1024, budget.Reads.Sum(x => x.Bytes));
        Assert.All(budget.Reads, read => Assert.Equal(SourceVerificationReadKind.Retained, read.Kind));
        using var exclusive = File.Open(Path.Combine(retained, relative), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static string Seed(string root, string hash, byte[] content)
    {
        var relative = Path.Combine("sha256", hash[..2], hash + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
        File.WriteAllBytes(Path.Combine(root, relative), content);
        return relative;
    }
}
