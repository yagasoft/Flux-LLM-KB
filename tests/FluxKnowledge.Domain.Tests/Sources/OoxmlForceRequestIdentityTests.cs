using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class OoxmlForceRequestIdentityTests
{
    [Fact]
    public void Action_and_request_fingerprints_are_opaque_version_bound_identities()
    {
        var branchId = Guid.Parse("7b168522-0e7d-4ce0-a370-01f72f40d274");
        var descriptorId = Guid.Parse("3d72bf21-5358-482d-a6a9-576ff23012a3");
        const string descriptorFingerprint = "phase-5-ooxml-retained-structural-v1";
        var firstRowVersion = Convert.FromHexString("0102030405060708");
        var secondRowVersion = Convert.FromHexString("0102030405060709");

        var first = OoxmlForceRequestIdentity.CreateActionId(branchId, descriptorId, descriptorFingerprint, firstRowVersion);
        var same = OoxmlForceRequestIdentity.CreateActionId(branchId, descriptorId, descriptorFingerprint, firstRowVersion);
        var changed = OoxmlForceRequestIdentity.CreateActionId(branchId, descriptorId, descriptorFingerprint, secondRowVersion);
        var expectedToken = OoxmlForceRequestIdentity.EncodeBlockedRowVersion(firstRowVersion);
        var request = OoxmlForceRequestIdentity.CreateRequestFingerprint(first, expectedToken);

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Matches("^[0-9a-f]{64}$", request);
        Assert.Equal(firstRowVersion, OoxmlForceRequestIdentity.DecodeBlockedRowVersion(expectedToken));
    }
}
