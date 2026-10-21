namespace FluxKnowledge.Application.Indexing;

/// <summary>Identity and sealed file proof for one immutable, bounded ANN segment.</summary>
public sealed record IndexSegmentDescriptor(
    string Id, string ModelFingerprint, int Dimensions, string DirectoryPath,
    string MembershipChecksum, string FileChecksum, long FileLength, long VectorCount,
    string NativeFormat, int FormatVersion = 2,
    [property: System.Text.Json.Serialization.JsonIgnore] Guid PhysicalIncarnation = default)
{
    // The physical incarnation is already encoded in Id. SQL retains its producing
    // candidate, so manifests need not gain a second mutable producer reference.
    public static string ComputeId(string nativeFormat, string membershipChecksum, Guid incarnation = default) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"segment/v2|{nativeFormat}|{membershipChecksum}" + (incarnation == Guid.Empty ? "" : $"|{incarnation:N}"))));
}

/// <summary>Internal work units; these budgets never limit the number of source vectors.</summary>
public sealed record IndexSegmentBuildOptions(int MaximumVectors = 8192, long MaximumPayloadBytes = 64 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumVectors <= 0 || MaximumPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumVectors), "Segment work budgets must be positive.");
    }
}
