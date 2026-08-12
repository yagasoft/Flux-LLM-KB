using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>SQL-backed trusted-local projection that binds every disclosure to verified retained bytes.</summary>
public sealed class SqlLocalRetainedDetailReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    IRetainedSourceReader retainedSourceReader,
    ILocalPrivateContentDisclosure disclosure) : ILocalRetainedDetailReader
{
    private const int MaximumAttempts = 16;
    private const int MaximumDiagnosticCharacters = 4 * 1024;
    private const long MaximumExcerptBytes = 64 * 1024;

    public async ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var branch = await (
            from processorBranch in context.SourceProcessorBranches.AsNoTracking()
            join activity in context.SourceActivities.AsNoTracking() on processorBranch.SourceActivityId equals activity.Id
            join revision in context.SourceRevisions.AsNoTracking() on processorBranch.SourceRevisionId equals revision.Id
            join artifact in context.SourceArtifacts.AsNoTracking() on revision.Id equals artifact.SourceRevisionId
            where processorBranch.Id == branchId
            select new
            {
                processorBranch.Id,
                processorBranch.SourceActivityId,
                processorBranch.SourceRevisionId,
                processorBranch.InputSha256,
                RevisionHash = revision.ContentSha256,
                revision.CanonicalPath,
                RevisionByteLength = revision.ByteLength,
                ArtifactHash = artifact.ContentSha256,
                ArtifactByteLength = artifact.ByteLength
            }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (branch is null)
        {
            return null;
        }

        RequireImmutableBinding(branch.InputSha256, branch.RevisionHash, branch.ArtifactHash,
            branch.RevisionByteLength, branch.ArtifactByteLength);
        var retained = await retainedSourceReader.ReadBytesAsync(
            new SourceRevisionId(branch.SourceRevisionId), cancellationToken).ConfigureAwait(false);
        RequirePhysicalRetainedBinding(retained, branch.ArtifactHash, branch.ArtifactByteLength);

        var members = await context.SourceProcessorBranchMembers.AsNoTracking()
            .Where(member => member.BranchId == branchId)
            .OrderBy(member => member.MemberFingerprint)
            .ThenBy(member => member.Id)
            .Select(member => new LocalRetainedMemberProjection(
                member.Id, member.MemberFingerprint, member.ChildSourceRevisionId, member.ChildSourceActivityId,
                member.Disposition, member.ReasonCode, member.ByteLength))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var persistedAttempts = await context.SourceProcessorAttempts.AsNoTracking()
            .Where(attempt => attempt.BranchId == branchId)
            .OrderByDescending(attempt => attempt.StartedAtUtc)
            .ThenByDescending(attempt => attempt.Id)
            .Take(MaximumAttempts)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var attempts = persistedAttempts.Select(CreateAttemptProjection).ToArray();

        return new LocalRetainedDetailProjection(
            branch.Id,
            branch.SourceActivityId,
            new SourceRevisionId(branch.SourceRevisionId),
            branch.CanonicalPath,
            branch.ArtifactHash,
            branch.InputSha256,
            branch.ArtifactByteLength,
            new LocalRetainedContentHandle(branch.Id, new SourceRevisionId(branch.SourceRevisionId)),
            members,
            attempts);
    }

    public async ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var detail = await ReadAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            throw new FileNotFoundException("The retained processor branch does not exist.");
        }
        if (detail.ArtifactByteLength > MaximumExcerptBytes)
        {
            throw new InvalidDataException("The retained excerpt exceeds the trusted-local limit.");
        }

        var retained = await retainedSourceReader.ReadBytesAsync(detail.SourceRevisionId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(retained.ContentSha256, detail.ArtifactHash, StringComparison.Ordinal) ||
            retained.ByteLength != detail.ArtifactByteLength)
        {
            throw new InvalidDataException("The retained detail binding changed while reading its excerpt.");
        }

        try
        {
            var bytes = retained.Bytes.AsSpan();
            if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
            return disclosure.Evaluate(new UTF8Encoding(false, true).GetString(bytes), LocalDisclosureKind.RetainedDetail);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The retained excerpt is not valid UTF-8.", exception);
        }
    }

    private static void RequireImmutableBinding(
        string inputHash,
        string revisionHash,
        string artifactHash,
        long revisionByteLength,
        long artifactByteLength)
    {
        if (!string.Equals(inputHash, revisionHash, StringComparison.Ordinal) ||
            !string.Equals(revisionHash, artifactHash, StringComparison.Ordinal) ||
            revisionByteLength < 0 || artifactByteLength != revisionByteLength)
        {
            throw new InvalidDataException("The retained detail binding is invalid.");
        }
    }

    private static void RequirePhysicalRetainedBinding(
        RetainedSourceBytes retained,
        string artifactHash,
        long artifactByteLength)
    {
        if (retained.ByteLength != artifactByteLength || retained.Bytes.LongLength != artifactByteLength ||
            !string.Equals(retained.ContentSha256, artifactHash, StringComparison.Ordinal) ||
            !string.Equals(ComputeRetainedByteHash(retained.Bytes), artifactHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The retained detail binding is invalid.");
        }
    }

    private static string ComputeRetainedByteHash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < bytes.Length; offset += 81920)
        {
            hash.AppendData(bytes.AsSpan(offset, Math.Min(81920, bytes.Length - offset)));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private LocalRetainedAttemptProjection CreateAttemptProjection(Persistence.Entities.SourceProcessorAttemptEntity attempt)
    {
        var evidence = attempt.EvidenceJson;
        if (string.IsNullOrEmpty(evidence))
        {
            return new LocalRetainedAttemptProjection(
                attempt.Id, attempt.LeaseGeneration, attempt.StartedAtUtc, attempt.FinishedAtUtc,
                attempt.OutcomeCode, null, false, null);
        }
        if (evidence.Length > MaximumDiagnosticCharacters)
        {
            return new LocalRetainedAttemptProjection(
                attempt.Id, attempt.LeaseGeneration, attempt.StartedAtUtc, attempt.FinishedAtUtc,
                attempt.OutcomeCode, null, true, "diagnostic-too-large");
        }

        var result = disclosure.Evaluate(evidence, LocalDisclosureKind.AuditEvidence);
        return new LocalRetainedAttemptProjection(
            attempt.Id, attempt.LeaseGeneration, attempt.StartedAtUtc, attempt.FinishedAtUtc,
            attempt.OutcomeCode, result.Value, result.Withheld, result.ReasonCode);
    }
}
