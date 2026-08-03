using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class NativeSqlServerFactAttribute : FactAttribute
{
    public NativeSqlServerFactAttribute()
    {
        Skip = NativeSqlServerTestSkip.Reason;
    }
}

public sealed class NativeSqlServerTheoryAttribute : TheoryAttribute
{
    public NativeSqlServerTheoryAttribute()
    {
        Skip = NativeSqlServerTestSkip.Reason;
    }
}

internal static class NativeSqlServerTestSkip
{
    public static string? Reason => string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(NativeSqlServerFixture.ConnectionEnvironmentVariable))
        ? $"Native SQL Server test skipped: set {NativeSqlServerFixture.ConnectionEnvironmentVariable} " +
          "to an explicit disposable server-level connection string."
        : null;
}
