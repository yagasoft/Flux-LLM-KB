using FluxKnowledge.Application.Models;
using FluxKnowledge.Infrastructure.Inference.Search;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Models;

public sealed class TokenizerRuntimePinTests
{
    private const string RuntimePath = @"J:\Models\runtimes\synthetic\hf_tokenizers.dll";

    [Fact]
    public async Task Concurrent_first_requests_publish_one_handle_and_retain_only_its_lease()
    {
        var loads = 0;
        var opens = 0;
        var file = new SyntheticFile(RuntimePath);
        var owner = new SyntheticOwner();
        var pin = new ProcessTokenizerRuntimePin(_ => { Interlocked.Increment(ref loads); return 1; });
        async ValueTask<VerifiedLocalModelLease> Open(CancellationToken ct)
        {
            Interlocked.Increment(ref opens);
            await Task.Delay(10, ct);
            return Lease(file, owner);
        }
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => pin.EnsureAsync("immutable-runtime", RuntimePath, Open, CancellationToken.None).AsTask()));
        Assert.Equal(1, opens);
        Assert.Equal(1, loads);
        Assert.False(file.Disposed);
        Assert.False(owner.Disposed);
    }

    [Fact]
    public async Task Load_failure_disposes_the_unpublished_lease_and_allows_a_later_attempt()
    {
        var loads = 0;
        var pin = new ProcessTokenizerRuntimePin(_ => ++loads == 1 ? throw new DllNotFoundException("synthetic load failure") : 1);
        var failedFile = new SyntheticFile(RuntimePath);
        var failedOwner = new SyntheticOwner();
        await Assert.ThrowsAsync<DllNotFoundException>(() => pin.EnsureAsync("immutable-runtime", RuntimePath,
            _ => ValueTask.FromResult(Lease(failedFile, failedOwner)), CancellationToken.None).AsTask());
        Assert.True(failedFile.Disposed);
        Assert.True(failedOwner.Disposed);
        var retainedFile = new SyntheticFile(RuntimePath);
        var retainedOwner = new SyntheticOwner();
        await pin.EnsureAsync("immutable-runtime", RuntimePath, _ => ValueTask.FromResult(Lease(retainedFile, retainedOwner)), CancellationToken.None);
        Assert.Equal(2, loads);
        Assert.False(retainedFile.Disposed);
        Assert.False(retainedOwner.Disposed);
    }

    [Theory]
    [InlineData("different-runtime", RuntimePath)]
    [InlineData("immutable-runtime", @"J:\Models\runtimes\other\hf_tokenizers.dll")]
    public async Task Published_pin_refuses_another_identity_or_path_without_loading_it(string identity, string path)
    {
        var pin = new ProcessTokenizerRuntimePin(_ => 1);
        await pin.EnsureAsync("immutable-runtime", RuntimePath,
            _ => ValueTask.FromResult(Lease(new(RuntimePath), new())), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<BgeInferenceException>(() => pin.EnsureAsync(identity, path,
            _ => throw new InvalidOperationException("Must not acquire another runtime."), CancellationToken.None).AsTask());
        Assert.Equal("bge-tokenizer-runtime-identity-mismatch", exception.ReasonCode);
    }

    [Fact]
    public async Task Acquired_lease_must_match_the_already_verified_request_path()
    {
        var loads = 0;
        var file = new SyntheticFile(@"J:\Models\runtimes\other\hf_tokenizers.dll");
        var owner = new SyntheticOwner();
        var pin = new ProcessTokenizerRuntimePin(_ => { loads++; return 1; });
        var exception = await Assert.ThrowsAsync<BgeInferenceException>(() => pin.EnsureAsync("immutable-runtime", RuntimePath,
            _ => ValueTask.FromResult(Lease(file, owner)), CancellationToken.None).AsTask());
        Assert.Equal("bge-tokenizer-runtime-identity-mismatch", exception.ReasonCode);
        Assert.Equal(0, loads);
        Assert.True(file.Disposed);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public async Task Cancellation_of_a_waiting_request_does_not_remove_the_first_pin()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var pin = new ProcessTokenizerRuntimePin(_ => { loads++; return 1; });
        async ValueTask<VerifiedLocalModelLease> Open(CancellationToken ct)
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return Lease(new(RuntimePath), new());
        }
        var first = pin.EnsureAsync("immutable-runtime", RuntimePath, Open, CancellationToken.None).AsTask();
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiting = pin.EnsureAsync("immutable-runtime", RuntimePath, Open, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await first;
        await pin.EnsureAsync("immutable-runtime", RuntimePath, _ => throw new InvalidOperationException("Already pinned."), CancellationToken.None);
        Assert.Equal(1, loads);
    }

    private static VerifiedLocalModelLease Lease(SyntheticFile file, SyntheticOwner owner) =>
        new(new Dictionary<string, IModelVerificationFile> { ["hf_tokenizers.dll"] = file }, owner);

    private sealed class SyntheticFile(string path) : IModelVerificationFile
    {
        public bool Disposed { get; private set; }
        public long ByteLength => 1;
        public string ProtectedLocalPath => path;
        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
    private sealed class SyntheticOwner : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
