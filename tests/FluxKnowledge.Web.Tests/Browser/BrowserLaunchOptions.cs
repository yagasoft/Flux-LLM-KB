using Microsoft.Playwright;

namespace FluxKnowledge.Web.Tests.Browser;

internal static class BrowserLaunchOptions
{
    private const string BrowserExecutableEnvironmentVariable = "FLUXKNOWLEDGE_BROWSER_EXECUTABLE";

    public static BrowserTypeLaunchOptions Create()
    {
        var configuredPath = Environment.GetEnvironmentVariable(BrowserExecutableEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException(
                "Browser tests require the validated executable from scripts/dev/test-browser.ps1.");
        }

        var executablePath = Path.GetFullPath(configuredPath);
        if (!File.Exists(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Browser tests require an existing validated browser executable from scripts/dev/test-browser.ps1.");
        }

        return new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = executablePath
        };
    }
}
