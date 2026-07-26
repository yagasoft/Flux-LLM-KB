using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class SqlToUsearchRebuildTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Disposable_native_sql_fixture_is_available_for_sql_to_usearch_rebuild_evidence()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(fixture.DatabaseName, connection.Database);
    }
}
