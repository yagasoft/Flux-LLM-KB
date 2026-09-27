using System.Runtime.InteropServices;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Search;

/// <summary>
/// Keeps only the verified tokenizer DLL and its protected files alive for this process.
/// Unloading this mimalloc-backed DLL after every tokenizer abandons native arenas.
/// Tokenizer instances and ONNX models still have ordinary per-request disposal.
/// </summary>
internal sealed class ProcessTokenizerRuntimePin(Func<string, nint>? loadLibrary = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, nint> _loadLibrary = loadLibrary ?? (path => NativeLibrary.Load(path,
        typeof(ProcessTokenizerRuntimePin).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32));
    private PinnedRuntime? _pinned;

    internal async ValueTask EnsureAsync(string identity, string verifiedPath,
        Func<CancellationToken, ValueTask<VerifiedLocalModelLease>> acquireLease, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pinned is not null)
            {
                if (_pinned.Identity != identity || !SamePath(_pinned.Path, verifiedPath))
                    throw new BgeInferenceException("bge-tokenizer-runtime-identity-mismatch");
                return;
            }
            VerifiedLocalModelLease? lease = await acquireLease(cancellationToken).ConfigureAwait(false);
            try
            {
                var path = lease.GetVerifiedLocalPath("hf_tokenizers.dll");
                if (!SamePath(path, verifiedPath)) throw new BgeInferenceException("bge-tokenizer-runtime-identity-mismatch");
                cancellationToken.ThrowIfCancellationRequested();
                // Publication transfers this separate lease to the process owner. Never
                // free this handle during managed shutdown: callers/finalisers may remain.
                var pinned = new PinnedRuntime(identity, path, lease);
                pinned.Handle = _loadLibrary(path);
                if (pinned.Handle == 0) throw new DllNotFoundException("bge-tokenizer-runtime-load-failed");
                _pinned = pinned;
                lease = null;
            }
            finally { lease?.Dispose(); }
        }
        finally { _gate.Release(); }
    }

    private static bool SamePath(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private sealed class PinnedRuntime(string identity, string path, VerifiedLocalModelLease lease)
    {
        internal string Identity { get; } = identity;
        internal string Path { get; } = path;
        internal VerifiedLocalModelLease Lease { get; } = lease;
        internal nint Handle { get; set; } // Set once, before publication under _gate.
    }
}
