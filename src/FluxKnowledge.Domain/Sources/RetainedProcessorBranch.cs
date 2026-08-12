using System.Security.Cryptography;
using System.Text;

namespace FluxKnowledge.Domain.Sources;

public enum RetainedProcessorBranchState
{
    Pending,
    Running,
    Completed,
    Blocked
}

/// <summary>Opaque child identity derived from an immutable parent and a normalised archive member path.</summary>
public sealed record ArchiveMemberIdentity(string MemberFingerprint, string SyntheticLocator, string StableSourceIdentity)
{
    public static ArchiveMemberIdentity Create(string parentStableIdentity, string normalisedEntryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentStableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalisedEntryName);
        var memberFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalisedEntryName)));
        var stableSourceIdentity = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"archive-member:{parentStableIdentity.Length}:{parentStableIdentity}:{memberFingerprint}")));
        return new ArchiveMemberIdentity(memberFingerprint, $"retained-archive-member:{memberFingerprint}", stableSourceIdentity);
    }
}

public sealed record RetainedProcessorClaim(
    Guid BranchId,
    SourceRevisionId SourceRevisionId,
    string ParentStableIdentity,
    string InputSha256,
    string LeaseOwner,
    long LeaseGeneration,
    DateTimeOffset LeaseExpiresAtUtc);

/// <summary>
/// Immutable C# syntax-processing claim materialised from a persisted branch attempt.
/// The attempt identity is an ownership fence; it is never derived from a lease.
/// </summary>
public sealed record RetainedCsharpCodeClaim
{
    public RetainedCsharpCodeClaim(
        Guid branchId,
        SourceRevisionId sourceRevisionId,
        string parentStableIdentity,
        string inputSha256,
        string leaseOwner,
        long leaseGeneration,
        DateTimeOffset leaseExpiresAtUtc,
        Guid attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentStableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        if (branchId == Guid.Empty) throw new ArgumentException("A C# claim requires a branch id.", nameof(branchId));
        if (attemptId == Guid.Empty) throw new ArgumentException("A C# claim requires its persisted attempt id.", nameof(attemptId));
        if (leaseGeneration < 0) throw new ArgumentOutOfRangeException(nameof(leaseGeneration));
        BranchId = branchId;
        SourceRevisionId = sourceRevisionId;
        ParentStableIdentity = parentStableIdentity;
        InputSha256 = inputSha256;
        LeaseOwner = leaseOwner;
        LeaseGeneration = leaseGeneration;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        AttemptId = attemptId;
    }

    public Guid BranchId { get; }
    public SourceRevisionId SourceRevisionId { get; }
    public string ParentStableIdentity { get; }
    public string InputSha256 { get; }
    public string LeaseOwner { get; }
    public long LeaseGeneration { get; }
    public DateTimeOffset LeaseExpiresAtUtc { get; }
    public Guid AttemptId { get; }
}
