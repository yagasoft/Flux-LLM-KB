using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Integrations.Codex;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Codex;

public sealed class NativeCodexHookLocationTests
{
    [Theory]
    [InlineData("powershell.exe", "commandWindows")]
    [InlineData("pwsh", "command")]
    public async Task Every_generated_event_uses_the_canonical_adapter_when_its_installed_cache_copy_is_missing(
        string shell, string property)
    {
        var root = Path.Combine(Path.GetTempPath(), "FluxHookLocation_" + Guid.NewGuid().ToString("N"), "space $dollar `tick 'quote & name");
        var canonical = Path.Combine(root, "canonical"); var cache = Path.Combine(root, "cache");
        var hooks = Path.Combine(canonical, "hooks"); Directory.CreateDirectory(hooks); Directory.CreateDirectory(cache);
        using var definitions = JsonDocument.Parse(NativeCodexPluginManifestWriter.RenderHooksUtf8(canonical));
        foreach (var entry in definitions.RootElement.GetProperty("hooks").EnumerateObject())
            Assert.DoesNotContain("PLUGIN_ROOT", entry.Value[0].GetProperty("hooks")[0].GetProperty(property).GetString());
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var received = new List<(string Event, byte[] Body)>();
        app.MapPost("/native/v1/codex/hooks/{eventName}", async (HttpContext context, string eventName) =>
        {
            using var body = new MemoryStream(); await context.Request.Body.CopyToAsync(body);
            received.Add((eventName, body.ToArray()));
            return Results.Json(new { @continue = true,
                hookSpecificOutput = new { hookEventName = eventName, additionalContext = "سياق 😀" } });
        });
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var adapter = Encoding.UTF8.GetString(NativeCodexPluginManifestWriter.RenderNativeHookAdapterUtf8())
                .Replace("http://127.0.0.1:5137", address, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(hooks, "invoke-native-hook.ps1"), adapter, new UTF8Encoding(false));
            Assert.False(File.Exists(Path.Combine(cache, "hooks", "invoke-native-hook.ps1")));
            var input = Encoding.UTF8.GetBytes("{\"prompt\":\"نعم 😀\",\"last_assistant_message\":\"اختبار\"}");
            foreach (var entry in definitions.RootElement.GetProperty("hooks").EnumerateObject())
            {
                var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = cache };
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
                start.ArgumentList.Add(entry.Value[0].GetProperty("hooks")[0].GetProperty(property).GetString()!);
                start.Environment["PLUGIN_ROOT"] = cache;
                using var child = Process.Start(start) ?? throw new InvalidOperationException("test-hook-process-not-started");
                try
                {
                    var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                    await child.StandardInput.BaseStream.WriteAsync(input); child.StandardInput.Close();
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(0, child.ExitCode); Assert.Equal("", await stderr);
                    using var response = JsonDocument.Parse(await stdout);
                    Assert.True(response.RootElement.GetProperty("continue").GetBoolean());
                    Assert.False(response.RootElement.TryGetProperty("systemMessage", out _));
                    if (entry.Name == "UserPromptSubmit")
                        Assert.Equal("سياق 😀", response.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
                    Assert.Equal(entry.Name, received[^1].Event); Assert.Equal(input, received[^1].Body);
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            }
            Assert.Equal(3, received.Count);
        }
        finally { await app.StopAsync(); if (Directory.Exists(Path.GetDirectoryName(root))) Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }
}
