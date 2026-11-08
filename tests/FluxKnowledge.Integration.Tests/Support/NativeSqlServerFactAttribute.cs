using Xunit;

namespace FluxKnowledge.Integration.Tests.Support;

public sealed class NativeSqlServerFactAttribute : FactAttribute
{
}

public sealed class NativeSqlServerTheoryAttribute : TheoryAttribute
{
}

public sealed class SqlServerScaleFactAttribute : FactAttribute
{
    public SqlServerScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SCALE") != "1")
            Skip = "Explicit scale qualification: set FLUXKNOWLEDGE_TEST_SCALE=1 and filter Category=Scale.";
    }
}

public sealed class SqlServerScaleTheoryAttribute : TheoryAttribute
{
    public SqlServerScaleTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SCALE") != "1")
            Skip = "Explicit scale qualification: set FLUXKNOWLEDGE_TEST_SCALE=1 and filter Category=Scale.";
    }
}
