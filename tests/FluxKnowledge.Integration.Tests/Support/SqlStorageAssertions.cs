using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

internal static class SqlStorageAssertions
{
    internal static async Task HasDisposableLayoutAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var policy = new SqlCommand("""
            SELECT CONVERT(int, [type]), CONVERT(bigint, [size])*8192, CONVERT(bigint, growth)*8192, is_percent_growth
            FROM sys.database_files ORDER BY file_id;
            """, connection);
        await using var reader = await policy.ExecuteReaderAsync();
        for (var type = 0; type < 2; type++)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(type, reader.GetInt32(0));
            Assert.True(reader.GetInt64(1) > 0);
            Assert.Equal(8L * 1024 * 1024, reader.GetInt64(2));
            Assert.False(reader.GetBoolean(3));
        }
        Assert.False(await reader.ReadAsync());
    }
}
