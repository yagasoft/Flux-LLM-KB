using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set FLUXKNOWLEDGE_BROWSER_TESTS=1 to run browser tests.";
        }
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SQL_CONNECTION")))
        {
            Skip = "Set FLUXKNOWLEDGE_TEST_SQL_CONNECTION to a disposable SQL test server to run browser tests.";
        }
    }
}

[Trait("Category", "Browser")]
public sealed class PhaseOneVerticalSliceBrowserTests
{
    [BrowserFact]
    public void Sql_backed_utf8_registration_is_visible_in_the_interactive_search_slice()
    {
        throw new NotImplementedException("The disposable SQL/Kestrel browser fixture has not been implemented.");
    }
}
