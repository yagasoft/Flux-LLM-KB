using System.Data;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlCodeDisclosureProofStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory, IDeploymentValidationHold? deploymentHold = null)
    : ICodeDisclosureProofStore
{
    public async ValueTask<CodeDisclosureArtifact?> ReadNextAsync(CancellationToken cancellationToken)
    {
        if (deploymentHold?.IsHeld == true) return null;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ReadEligibleAsync(context, null, pendingOnly: true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> CommitAsync(CodeDisclosureArtifact artifact, CodeDisclosureProof proof,
        CancellationToken cancellationToken)
    {
        await using var executionContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await executionContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await SqlPublishedPassageSelection.AcquireFenceAsync(context, cancellationToken).ConfigureAwait(false);
            if (deploymentHold?.IsHeld == true) return false;
            var current = await ReadEligibleAsync(context, artifact.ArtifactId, pendingOnly: false, cancellationToken).ConfigureAwait(false);
            if (current is null || current.PipelineRecordId != artifact.PipelineRecordId ||
                current.SourceRevision != artifact.SourceRevision || current.CanonicalHash != artifact.CanonicalHash ||
                current.CanonicalLength != artifact.CanonicalLength) return false;
            Validate(proof, current.ArtifactId, current.CanonicalHash, current.CanonicalLength, current.Text);
            var existing = await context.CanonicalCodeDisclosureProofs.AsNoTracking().SingleOrDefaultAsync(
                value => value.ArtifactId == proof.ArtifactId && value.Fingerprint == proof.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Checksum != proof.Checksum) throw new InvalidOperationException("code-disclosure-proof-conflict");
                return true;
            }
            Append(context, proof);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (deploymentHold?.IsHeld == true) return false;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    internal static void Append(FluxKnowledgeDbContext context, CodeDisclosureProof proof)
    {
        context.CanonicalCodeDisclosureProofs.Add(new()
        {
            ArtifactId = proof.ArtifactId, CanonicalHash = proof.CanonicalHash,
            CanonicalLength = proof.CanonicalLength, Fingerprint = proof.Fingerprint,
            State = (int)proof.State, SpanCount = proof.Spans.Count, Checksum = proof.Checksum
        });
        context.CanonicalCodeDisclosureSpans.AddRange(proof.Spans.Select(span => new CanonicalCodeDisclosureSpanEntity
        {
            ArtifactId = proof.ArtifactId, Fingerprint = proof.Fingerprint, Start = span.Start,
            End = span.End, Kind = (int)span.Kind, Checksum = span.Checksum
        }));
    }

    internal static void Validate(CodeDisclosureProof proof, Guid artifact, string hash, int length, string? text)
    {
        if (proof.ArtifactId != artifact || proof.CanonicalHash != hash || proof.CanonicalLength != length ||
            proof.Fingerprint != CodeDisclosureIntegrity.Fingerprint || !Enum.IsDefined(proof.State) ||
            proof.Checksum != CodeDisclosureIntegrity.ProofChecksum(artifact, hash, proof.Fingerprint, proof.State, length, proof.Spans) ||
            (proof.State != CodeDisclosureProofState.Ready && proof.Spans.Count != 0) ||
            proof.State == CodeDisclosureProofState.Ready &&
                (text is null || CodeDisclosureIntegrity.Hash(text) != hash || !proof.Window(0, length).IsValid(text, 0)))
            throw new InvalidOperationException("code-disclosure-proof-invalid");
    }

    private static async Task<CodeDisclosureArtifact?> ReadEligibleAsync(
        FluxKnowledgeDbContext context, Guid? artifactId, bool pendingOnly, CancellationToken cancellationToken)
    {
        var row = await context.Database.SqlQuery<ArtifactRow>(SqlPublishedPassageSelection.Bind($"""
            SELECT TOP (1) [artifact].[Id] AS [ArtifactId], [record].[Id] AS [PipelineRecordId],
                [artifact].[SourceRevision], [artifact].[ContentHash] AS [CanonicalHash],
                CAST(DATALENGTH([artifact].[SearchText]) / 2 AS int) AS [CanonicalLength],
                CASE WHEN DATALENGTH([artifact].[SearchText]) / 2 <= 4000000
                     THEN [artifact].[SearchText] ELSE NULL END AS [Text]
            FROM [Artifacts] AS [artifact]
            INNER JOIN [PipelineRecords] AS [record] ON [artifact].[PipelineRecordId] = [record].[Id]
            INNER JOIN [SourceIdentities] AS [source] ON [record].[SourceIdentityId] = [source].[Id]
            OUTER APPLY (SELECT TOP (1) [SourceRevision] FROM [TextChunks]
                         WHERE [ArtifactId] = [artifact].[Id]) AS [chunk]
            /*publication-bindings*/
            WHERE /*publication-eligibility*/
              AND ({artifactId} IS NULL OR [artifact].[Id] = {artifactId})
              AND RIGHT(COALESCE([owner].[CanonicalPath] COLLATE Latin1_General_100_CI_AS,
                                [retained].[CanonicalPath] COLLATE Latin1_General_100_CI_AS,
                                [source].[StableKey] COLLATE Latin1_General_100_CI_AS), 3) = N'.cs'
              AND ({pendingOnly} = 0 OR NOT EXISTS (
                  SELECT 1 FROM [CanonicalCodeDisclosureProofs] AS [proof]
                  WHERE [proof].[ArtifactId] = [artifact].[Id] AND [proof].[Fingerprint] = {CodeDisclosureIntegrity.Fingerprint}))
            ORDER BY [artifact].[Id]
            """)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new(row.ArtifactId, row.PipelineRecordId, row.SourceRevision,
            row.CanonicalHash, row.CanonicalLength, row.Text);
    }

    private sealed class ArtifactRow
    {
        public Guid ArtifactId { get; init; }
        public Guid PipelineRecordId { get; init; }
        public long SourceRevision { get; init; }
        public string CanonicalHash { get; init; } = "";
        public int CanonicalLength { get; init; }
        public string? Text { get; init; }
    }
}
