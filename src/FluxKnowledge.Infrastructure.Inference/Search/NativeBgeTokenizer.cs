using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Models;

namespace FluxKnowledge.Infrastructure.Inference.Search;

internal sealed class NativeBgeTokenizer : IBgeTokenizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _sync = new();
    private readonly VerifiedLocalModelLease _tokenizerLease;
    private readonly VerifiedLocalModelLease _runtimeLease;
    private readonly AssemblyLoadContext _context;
    private readonly IDisposable _tokenizer;
    private readonly Func<string, uint[]> _encode;
    private nint _nativeLibrary;
    private bool _disposed;

    private NativeBgeTokenizer(VerifiedLocalModelLease tokenizerLease, VerifiedLocalModelLease runtimeLease, string fingerprint, string tokenizerFilename = "tokenizer.json")
    {
        _tokenizerLease = tokenizerLease;
        _runtimeLease = runtimeLease;
        Fingerprint = fingerprint;
        _context = new AssemblyLoadContext("bge-tokenizer-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            var assembly = _context.LoadFromAssemblyPath(runtimeLease.GetVerifiedLocalPath("Tokenizers.DotNet.dll"));
            _nativeLibrary = NativeLibrary.Load(runtimeLease.GetVerifiedLocalPath("hf_tokenizers.dll"), assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            NativeLibrary.SetDllImportResolver(assembly, (name, owner, _) => name == "hf_tokenizers"
                ? _nativeLibrary
                : throw new DllNotFoundException("bge-tokenizer-native-dependency-refused"));
            var type = assembly.GetType("Tokenizers.DotNet.Tokenizer", throwOnError: true)!;
            _tokenizer = (IDisposable)Activator.CreateInstance(type, tokenizerLease.GetVerifiedLocalPath(tokenizerFilename))!;
            _encode = type.GetMethod("Encode", [typeof(string)])!.CreateDelegate<Func<string, uint[]>>(_tokenizer);
        }
        catch
        {
            try { _tokenizer?.Dispose(); }
            finally
            {
                if (_tokenizer is not null) GC.SuppressFinalize(_tokenizer);
                _context.Unload();
                ReleaseNativeLibrary();
            }
            throw;
        }
    }

    internal static IBgeTokenizer OpenForTest(VerifiedLocalModelLease tokenizer, VerifiedLocalModelLease runtime, string filename) =>
        new NativeBgeTokenizer(tokenizer, runtime, "synthetic", filename);

    internal static async ValueTask<IBgeTokenizer> CreateAsync(VerifiedLocalModelLease tokenizer, VerifiedLocalModelLease runtime,
        string fingerprint, CancellationToken cancellationToken)
    {
        var file = tokenizer.Files.Single(f => f.Filename == "tokenizer.json");
        if (file.ByteLength is < 1 or > 32 * 1024 * 1024) throw new BgeInferenceException("bge-tokenizer-file-invalid");
        var bytes = new byte[checked((int)file.ByteLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await tokenizer.ReadAsync(file.Filename, offset, bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new BgeInferenceException("bge-tokenizer-file-invalid");
            offset += count;
        }
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.GetProperty("truncation").ValueKind != JsonValueKind.Null ||
            json.RootElement.GetProperty("padding").ValueKind != JsonValueKind.Null)
            throw new BgeInferenceException("bge-tokenizer-truncation-refused");
        cancellationToken.ThrowIfCancellationRequested();
        return new NativeBgeTokenizer(tokenizer, runtime, fingerprint);
    }

    public string Fingerprint { get; }
    public int CountTokens(string text) => EncodeUntruncated(text).Length;
    public long[] EncodeUntruncated(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 16384) throw new BgeInferenceException("bge-tokenizer-input-too-long");
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw new BgeInferenceException("bge-tokenizer-unicode-invalid"); }
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _encode(text).Select(static id => (long)id).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try { _tokenizer.Dispose(); }
            finally
            {
                // The managed binding caches P/Invoke targets. No finalizer may call those
                // targets after our explicit load reference is balanced.
                GC.SuppressFinalize(_tokenizer);
                _context.Unload();
                ReleaseNativeLibrary();
                _runtimeLease.Dispose();
                _tokenizerLease.Dispose();
            }
        }
    }

    private void ReleaseNativeLibrary()
    {
        var handle = Interlocked.Exchange(ref _nativeLibrary, 0);
        if (handle != 0) NativeLibrary.Free(handle);
    }
}
