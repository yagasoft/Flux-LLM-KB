using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using Microsoft.AspNetCore.DataProtection;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

public sealed class CorpusEvidenceCodec : ICorpusEvidenceCodec
{
    private const int MaximumReferenceLength = 2048;
    private const string Purpose = "FluxKnowledge.CorpusEvidence/v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtectionProvider _provider;

    public CorpusEvidenceCodec(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public string Encode(CorpusEvidenceBinding binding)
    {
        Validate(binding);
        try
        {
            var protectedBytes = _provider.CreateProtector(Purpose).Protect(JsonSerializer.SerializeToUtf8Bytes(binding, JsonOptions));
            var reference = Convert.ToBase64String(protectedBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (reference.Length > MaximumReferenceLength) throw Invalid();
            return reference;
        }
        catch (NativeOperationException) { throw; }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    public CorpusEvidenceBinding Decode(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > MaximumReferenceLength ||
            reference.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw Invalid();

        try
        {
            var base64 = reference.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var binding = JsonSerializer.Deserialize<CorpusEvidenceBinding>(
                _provider.CreateProtector(Purpose).Unprotect(Convert.FromBase64String(base64)), JsonOptions);
            Validate(binding);
            return binding!;
        }
        catch (NativeOperationException) { throw; }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    private static void Validate(CorpusEvidenceBinding? binding)
    {
        if (binding is null || binding.Version != 1 ||
            binding.PipelineRecordId == Guid.Empty || binding.PipelineRecordRevision < 1 ||
            binding.ArtifactId == Guid.Empty || binding.ChunkId < 1 ||
            binding.CitedStart < 0 || binding.CitedLength is < 1 or > 1024 ||
            binding.RootId.HasValue != binding.OwnerSourceRevisionId.HasValue ||
            !IsHash(binding.SourceIdentityHash) || !IsHash(binding.ArtifactHash) ||
            !IsHash(binding.ChunkHash))
            throw Invalid();
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static NativeOperationException Invalid() => new("evidence-invalid");
}
