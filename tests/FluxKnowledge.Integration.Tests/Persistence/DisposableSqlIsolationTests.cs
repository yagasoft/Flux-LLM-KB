using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

public sealed class DisposableSqlIsolationTests
{
    private const string Root = @"E:\Codex Workspaces\Staging\FluxKnowledge\SqlTests-GuardTests\Data\Sql\Disposable";
    private const string Name = "FluxKnowledge_Phase1Tests_0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Server=localhost;Integrated Security=True")]
    [InlineData("Server=localhost;User ID=test;Password=synthetic;Database=FluxKnowledge")]
    [InlineData("Server=localhost;User ID=test;Password=synthetic;Failover Partner=elsewhere")]
    public void Refuses_missing_admin_or_redirected_test_credentials(string? connection) =>
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.ValidateRestrictedTestConnection(connection));

    [Theory]
    [InlineData(null)]
    [InlineData(@"I:\FluxKnowledge\Data\Sql")]
    [InlineData(@"J:\Models")]
    [InlineData(@"E:\Codex Workspaces\Staging\FluxKnowledge\Release1-20261014\Data\Sql\Disposable")]
    [InlineData(@"E:\Codex Workspaces\Staging\FluxKnowledge\SqlTests-GuardTests\Data\Sql\..\Disposable")]
    public void Refuses_unowned_or_ambiguous_test_file_roots(string? root) =>
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.ValidateTestFileRoot(root));

    [Fact]
    public void Allocates_only_the_generated_catalogue_files_under_the_owned_root()
    {
        var sql = NativeSqlServerFixture.BuildCreateDatabaseSql(Name, Root);
        Assert.Contains(Path.Combine(Root, Name + ".mdf"), sql, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(Root, Name + "_log.ldf"), sql, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.BuildCreateDatabaseSql("FluxKnowledge", Root));
    }

    [Fact]
    public void Refuses_foreign_and_extra_files_before_migration_or_drop()
    {
        NativeSqlServerFixture.ValidateOwnedDatabaseFiles(Name, Root,
            [Path.Combine(Root, Name + ".mdf"), Path.Combine(Root, Name + "_log.ldf")]);
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.ValidateOwnedDatabaseFiles(Name, Root,
            [@"E:\foreign.mdf", @"E:\foreign.ldf"]));
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.ValidateOwnedDatabaseFiles(Name, Root,
            [Path.Combine(Root, Name + ".mdf"), Path.Combine(Root, Name + "_log.ldf"), Path.Combine(Root, "extra.ndf")]));
    }

    [Fact]
    public void Session_interruption_is_guarded_by_the_exact_owned_catalogue_and_files()
    {
        var sql = NativeSqlServerFixture.BuildSessionInterruptionSql(Name, Root);
        Assert.Contains("DB_NAME() <> N'master'", sql, StringComparison.Ordinal);
        Assert.Contains("owner_sid=SUSER_SID()", sql, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(Root, Name + ".mdf"), sql, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(Root, Name + "_log.ldf"), sql, StringComparison.Ordinal);
        Assert.DoesNotContain("KILL ", sql, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.BuildSessionInterruptionSql("FluxKnowledge", Root));
        Assert.Throws<InvalidOperationException>(() => NativeSqlServerFixture.BuildSessionInterruptionSql(Name, @"I:\FluxKnowledge"));
    }
}
