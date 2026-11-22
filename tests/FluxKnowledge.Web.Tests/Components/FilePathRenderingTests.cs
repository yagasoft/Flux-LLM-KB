using FluxKnowledge.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class FilePathRenderingTests
{
    [Theory]
    [InlineData(@"C:\retained-archive-members\78c27751e0dc434d98da3acb4dd9109d\c282ade051eceddc2d15ed2e9d196ce2201d69784a25c7f95861529f4111a092")]
    [InlineData("retained-archive-member:c282ade051eceddc2d15ed2e9d196ce2201d69784a25c7f95861529f4111a092")]
    public async Task Opaque_member_identity_appears_only_in_details(string locator)
    {
        var rendered = await RenderAsync(locator, @"I:\retained\mail.zip");
        var beforeDetails = rendered[..rendered.IndexOf("<details>", StringComparison.Ordinal)];
        Assert.Contains("Retained archive member", beforeDetails);
        Assert.Contains("mail.zip", beforeDetails);
        Assert.DoesNotContain("c282ade051eceddc", beforeDetails);
        Assert.Contains("c282ade051eceddc", rendered);
    }

    [Fact]
    public async Task Available_member_path_displays_filename_and_archive()
    {
        var rendered = await RenderAsync("folder/source-notes.txt", @"I:\retained\mail.zip");
        var beforeDetails = rendered[..rendered.IndexOf("<details>", StringComparison.Ordinal)];
        Assert.Contains(">source-notes.txt</strong>", beforeDetails);
        Assert.Contains("mail.zip", beforeDetails);
    }

    private static async Task<string> RenderAsync(string locator, string archive)
    {
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IJSRuntime, UnusedJavaScript>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<FilePath>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(FilePath.FullPath)] = locator, [nameof(FilePath.ArchivePath)] = archive }));
            return component.ToHtmlString();
        });
    }

    private sealed class UnusedJavaScript : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new NotSupportedException();
    }
}
