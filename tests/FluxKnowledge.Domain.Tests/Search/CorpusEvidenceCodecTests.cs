using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Search;

public sealed class CorpusEvidenceCodecTests
{
    [Fact]
    public void Reference_round_trips_an_immutable_binding_without_exposing_identity()
    {
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var binding = Binding();

        var reference = codec.Encode(binding);

        Assert.Equal(binding, codec.Decode(reference));
        Assert.DoesNotContain(binding.PipelineRecordId.ToString("D"), reference, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(reference.Length, 1, 2048);
    }

    [Fact]
    public void Tampered_or_oversized_reference_is_rejected()
    {
        var codec = new CorpusEvidenceCodec(new EphemeralDataProtectionProvider());
        var reference = codec.Encode(Binding());

        var middle = reference.Length / 2;
        var tampered = reference[..middle] + (reference[middle] == 'A' ? "B" : "A") + reference[(middle + 1)..];
        Assert.Equal("evidence-invalid", Assert.Throws<NativeOperationException>(() => codec.Decode(tampered)).ReasonCode);
        Assert.Equal("evidence-invalid", Assert.Throws<NativeOperationException>(() => codec.Decode(new string('x', 2049))).ReasonCode);
    }

    private static CorpusEvidenceBinding Binding() => new(
        1, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), 1,
        Guid.NewGuid(), new string('b', 64), 41, new string('c', 64), 25, 32);
}
