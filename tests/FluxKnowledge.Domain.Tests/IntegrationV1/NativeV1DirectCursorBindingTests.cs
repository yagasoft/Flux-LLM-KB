using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.IntegrationV1;

public sealed class NativeV1DirectCursorBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Previously_issued_protected_cursors_keep_query_and_scope_binding(bool codeQuery)
    {
        var protection = new EphemeralDataProtectionProvider();
        var branch = Guid.NewGuid();
        var query = new NativeCodeQuery("matches", "PreviousQuery", branch, 20, null);
        var binding = codeQuery ? NativeV1CursorBindings.Code(query) :
            new NativeV1CursorBinding("corpus", "roots", "{\"rootId\":null,\"branchId\":null,\"jobId\":null}", "id:asc", 20);
        var legacyFilters = codeQuery ? System.Text.Json.JsonSerializer.Serialize(new
        {
            queryFingerprint = Hash(query.Query!), branchId = branch.ToString("D").ToLowerInvariant()
        }) : binding.CanonicalFilters;
        var position = new NativeV1CursorPosition(Id: Guid.NewGuid(), Ordinal: codeQuery ? 17 : null);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1,
            bindingFingerprint = Hash(string.Join("\n", binding.Family, binding.View, legacyFilters, binding.Ordering, "20")),
            pageLimit = 20, position
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var token = Convert.ToBase64String(protection.CreateProtector("FluxKnowledge.NativeV1ProjectionCursor/v1").Protect(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var codec = new NativeV1ProjectionCursorCodec(protection);
        Assert.Equal(position, codec.Decode(binding, token));
        Assert.Throws<NativeOperationException>(() => codec.Decode(binding with { CanonicalFilters = "different" }, token));
        Assert.Throws<NativeOperationException>(() => codec.Decode(binding with { PageLimit = 21 }, token));
        if (codeQuery) Assert.Throws<NativeOperationException>(() => codec.Decode(binding with { CanonicalFilters = "{}" }, token));
    }

    private static string Hash(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void Cursor_compares_each_query_field_without_delimiter_aliases()
    {
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var original = new NativeV1CursorBinding("a\nb", "c", "{}", "id:asc", 1);
        var different = new NativeV1CursorBinding("a", "b\nc", "{}", "id:asc", 1);
        var token = codec.Encode(original, new(Id: Guid.NewGuid()));
        Assert.Throws<NativeOperationException>(() => codec.Decode(different, token));
    }

    [Fact]
    public void Protected_pagination_round_trips_the_maximum_unicode_query_and_rejects_a_different_query()
    {
        var codec = new NativeV1ProjectionCursorCodec(new EphemeralDataProtectionProvider());
        var query = new string('文', NativeV1ContractLimits.MaximumCodeQueryCharacters);
        var binding = NativeV1CursorBindings.Code(new NativeCodeQuery("matches", query, Guid.NewGuid(), 100, null));
        var position = new NativeV1CursorPosition(Id: Guid.NewGuid(), Ordinal: 17);
        var token = codec.Encode(binding, position);
        Assert.Equal(position, codec.Decode(binding, token));
        Assert.InRange(token.Length, 1, 65536);
        Assert.Throws<NativeOperationException>(() => codec.Decode(binding with { CanonicalFilters = "different" }, token));
    }
}
