using System.Reflection;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class DerivedChildIdentityConflictTests
{
    private const string PathIndex = "IX_SourceRevisions_SourceRootId_CanonicalPathFingerprint_ContentSha256";
    private const string RevisionIndex = "IX_SourceRevisions_SourceRootId_StableSourceIdentity_Revision";

    public static IEnumerable<object[]> ExactConflicts =>
        from origin in new[] { 1, 2, 3 }
        from number in new[] { 2601, 2627 }
        from index in new[] { PathIndex, RevisionIndex }
        select new object[] { origin, number, index };

    [Theory]
    [MemberData(nameof(ExactConflicts))]
    public void Exact_named_identity_conflicts_for_added_derived_children_are_contained(int origin, int number, string index)
    {
        using var context = CreateContext();
        var failure = Failure(context, origin, Guid.NewGuid(), EntityState.Added,
            (number, $"Duplicate key for '{index}'."), (3621, "The statement has been terminated."));

        Assert.True(SqlRetainedProcessorBranchStore.IsDerivedChildIdentityConflict(failure));
    }

    [Theory]
    [InlineData(0, true, EntityState.Added)]
    [InlineData(4, true, EntityState.Added)]
    [InlineData(2, false, EntityState.Added)]
    [InlineData(2, true, EntityState.Modified)]
    [InlineData(3, true, EntityState.Unchanged)]
    public void A_named_duplicate_for_a_non_child_or_non_insert_is_not_contained(int origin, bool hasParent, EntityState state)
    {
        using var context = CreateContext();
        var failure = Failure(context, origin, hasParent ? Guid.NewGuid() : null, state, (2601, $"Duplicate key for '{PathIndex}'."));

        Assert.False(SqlRetainedProcessorBranchStore.IsDerivedChildIdentityConflict(failure));
    }

    [Theory]
    [InlineData(1205, "Deadlock victim.")]
    [InlineData(2601, "Duplicate key for 'IX_Unrelated'.")]
    [InlineData(2627, "Duplicate key for 'PK_SourceRevisions'.")]
    public void Mixed_errors_are_not_contained(int number, string message)
    {
        using var context = CreateContext();
        var failure = Failure(context, 2, Guid.NewGuid(), EntityState.Added,
            (2601, $"Duplicate key for '{PathIndex}'."), (number, message));

        Assert.False(SqlRetainedProcessorBranchStore.IsDerivedChildIdentityConflict(failure));
    }

    [Fact]
    public void Unrelated_unique_indexes_and_non_sql_failures_are_not_contained()
    {
        using var context = CreateContext();
        Assert.False(SqlRetainedProcessorBranchStore.IsDerivedChildIdentityConflict(
            Failure(context, 3, Guid.NewGuid(), EntityState.Added, (2601, "Duplicate key for 'IX_Unrelated'."))));
        Assert.False(SqlRetainedProcessorBranchStore.IsDerivedChildIdentityConflict(new DbUpdateException("Unexpected invariant failure.")));
    }

    private static FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
        .UseSqlServer("Server=unused;Database=FluxKnowledge_Phase1Tests_IdentityClassifier;Trusted_Connection=True;TrustServerCertificate=True").Options);

    private static DbUpdateException Failure(FluxKnowledgeDbContext context, int origin, Guid? parent, EntityState state,
        params (int Number, string Message)[] details)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), flags, null, null, null)!;
        foreach (var detail in details)
        {
            var error = (SqlError)Activator.CreateInstance(typeof(SqlError), flags, null,
                [detail.Number, (byte)0, (byte)14, "synthetic", detail.Message, string.Empty, 1, 0, null], null)!;
            typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        }
        var sql = (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2).Invoke(null, [errors, "synthetic"])!;
        var entry = context.Entry(new SourceRevisionEntity { Id = Guid.NewGuid(), OriginKind = origin, ParentSourceRevisionId = parent });
        entry.State = state;
        return new DbUpdateException("Synthetic provider failure.", sql, [entry]);
    }
}
