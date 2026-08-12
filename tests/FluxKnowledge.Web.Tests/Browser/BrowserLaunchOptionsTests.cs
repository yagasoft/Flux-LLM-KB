using Microsoft.Playwright;
using Xunit;

namespace FluxKnowledge.Web.Tests.Browser;

public sealed class BrowserLaunchOptionsTests
{
    [Fact]
    public void Every_browser_category_launch_uses_the_validated_browser_options()
    {
        var repositoryRoot = FindRepositoryRoot();
        var browserFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "tests", "FluxKnowledge.Web.Tests", "Browser"),
            "*.cs",
            SearchOption.TopDirectoryOnly)
            .Where(path => File.ReadAllText(path).Contains("[Trait(\"Category\", \"Browser\")]", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(browserFiles);
        foreach (var browserFile in browserFiles)
        {
            var source = File.ReadAllText(browserFile);
            Assert.DoesNotContain("new BrowserTypeLaunchOptions", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Browser_launch_options_require_the_validated_child_process_executable()
    {
        var original = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EXECUTABLE");
        Environment.SetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EXECUTABLE", null);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => BrowserLaunchOptions.Create());

            Assert.Contains("test-browser.ps1", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EXECUTABLE", original);
        }
    }

    [Fact]
    public void Browser_launch_options_use_the_explicitly_validated_executable()
    {
        var executable = Environment.ProcessPath!;
        var original = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EXECUTABLE");
        Environment.SetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EXECUTABLE", executable);
        try
        {
            BrowserTypeLaunchOptions options = BrowserLaunchOptions.Create();

            Assert.Equal(Path.GetFullPath(executable), options.ExecutablePath);
            Assert.True(options.Headless);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FLUXKNOWLEDGE_BROWSER_EXECUTABLE", original);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FluxKnowledge.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
