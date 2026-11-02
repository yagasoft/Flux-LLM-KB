using System.Data;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed partial class SegmentedSelectionScaleTests
{
    private static async Task SeedFullManifestMembershipAsync(FluxKnowledgeDbContext db,
        Guid candidateId, string payloadChecksum, IReadOnlyList<IndexSegmentDescriptor> segments)
    {
        // Preserve the original single SaveChanges transaction around candidate,
        // segment headers and all membership rows. These are ordinary inserts.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        var mapping = JsonSerializer.Serialize(segments.Select((segment, ordinal) => new { Ordinal = ordinal, segment.Id }));
        var parameters = new List<object>
        {
            new SqlParameter("@candidate", SqlDbType.UniqueIdentifier) { Value = candidateId },
            new SqlParameter("@checksum", SqlDbType.NVarChar, 64) { Value = payloadChecksum },
            new SqlParameter("@segmentCount", SqlDbType.Int) { Value = segments.Count },
            new SqlParameter("@segmentsJson", SqlDbType.NVarChar, -1) { Value = mapping }
        };
        await db.Database.ExecuteSqlRawAsync("""
            SET NOCOUNT ON;
            DECLARE @segments TABLE (Ordinal int PRIMARY KEY, Id char(64) NOT NULL UNIQUE);
            INSERT @segments (Ordinal, Id)
            SELECT Ordinal, Id FROM OPENJSON(@segmentsJson) WITH (Ordinal int '$.Ordinal', Id char(64) '$.Id');
            INSERT IndexCandidateMembers
                (CandidateId, Phase, VectorId, PayloadChecksum, SourceIdentityId, OwnerSourceRevisionId, SourceRootId)
            SELECT @candidate, 1, VectorId, @checksum, SourceIdentityId, OwnerSourceRevisionId, SourceRootId
            FROM CurrentIndexMembers;
            WITH members AS (
                SELECT VectorId, (ROW_NUMBER() OVER (ORDER BY VectorId)-1)%@segmentCount AS Ordinal
                FROM CurrentIndexMembers
            )
            INSERT IndexSegmentVectors (SegmentId, VectorId)
            SELECT segments.Id, members.VectorId FROM members JOIN @segments AS segments ON segments.Ordinal=members.Ordinal;
            """, parameters);
        await transaction.CommitAsync();
    }

    private static async Task AssertFullManifestMembershipAsync(FluxKnowledgeDbContext db, Guid candidateId,
        string payloadChecksum, CurrentIndexMemberEntity[] members, IReadOnlyList<IndexSegmentDescriptor> segments)
    {
        var candidateMembers = await db.IndexCandidateMembers.AsNoTracking()
            .Where(value => value.CandidateId == candidateId).OrderBy(value => value.VectorId).ToArrayAsync();
        Assert.Equal(members.Select(value => (candidateId, 1, value.VectorId, payloadChecksum,
                value.SourceIdentityId, value.OwnerSourceRevisionId, value.SourceRootId)),
            candidateMembers.Select(value => (value.CandidateId, value.Phase, value.VectorId, value.PayloadChecksum,
                value.SourceIdentityId, value.OwnerSourceRevisionId, value.SourceRootId)));

        var ids = segments.Select(value => value.Id).ToArray();
        var links = await db.IndexSegmentVectors.AsNoTracking().Where(value => ids.Contains(value.SegmentId))
            .OrderBy(value => value.VectorId).ToArrayAsync();
        Assert.Equal(members.Select((member, ordinal) => (segments[ordinal % segments.Count].Id, member.VectorId)),
            links.Select(value => (value.SegmentId, value.VectorId)));
        var counts = links.GroupBy(value => value.SegmentId).ToDictionary(group => group.Key, group => group.LongCount());
        Assert.All(segments, segment => Assert.Equal(segment.VectorCount, counts.GetValueOrDefault(segment.Id)));
        Assert.Equal(segments[0].Id, links[0].SegmentId);
        Assert.Equal(members[0].VectorId, links[0].VectorId);
    }
}
