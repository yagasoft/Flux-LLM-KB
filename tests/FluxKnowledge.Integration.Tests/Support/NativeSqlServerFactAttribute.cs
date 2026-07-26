using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class NativeSqlServerFactAttribute : FactAttribute
{
    public NativeSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(NativeSqlServerFixture.ConnectionEnvironmentVariable)))
        {
            Skip =
                $"Native SQL Server test skipped: set {NativeSqlServerFixture.ConnectionEnvironmentVariable} " +
                "to an explicit disposable server-level connection string.";
        }
    }
}
