using FluxKnowledge.Web.Tests.Browser;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class BrowserTestRootTests
{
    [Fact]
    public void Safe_root_uses_a_non_I_drive_candidate_when_the_temp_root_is_on_I()
    {
        var root = BrowserTestRoots.Create(
            "FluxKnowledgeBrowserIngress_test",
            ["I:\\temporary", "C:\\temporary"]);

        Assert.StartsWith("C:\\temporary", root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Safe_root_rejects_I_drive_candidates()
    {
        Assert.Throws<InvalidOperationException>(
            () => BrowserTestRoots.Create("FluxKnowledgeBrowserIngress_test", ["I:\\temporary"]));
    }
}
