using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Integrations.Codex;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Codex;

public sealed class NativeCodexHookCacheRepairTests
{
    [Fact]
    public async Task Repair_plans_then_restores_only_missing_adapter_and_repeated_apply_preserves_all_bytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = fixture.CacheHashes();

        var plan = await fixture.RunAsync(apply: false);
        Assert.Equal(0, plan.ExitCode);
        Assert.False(File.Exists(fixture.Target));
        using (var json = JsonDocument.Parse(plan.Output))
            Assert.Equal("restore-missing-adapter", json.RootElement.GetProperty("action").GetString());

        var applied = await fixture.RunAsync(apply: true);
        Assert.True(applied.ExitCode == 0, applied.Error);
        Assert.Equal(await File.ReadAllBytesAsync(fixture.SourceAdapter), await File.ReadAllBytesAsync(fixture.Target));
        foreach (var file in before) Assert.Equal(file.Value, fixture.CacheHashes()[file.Key]);

        var after = fixture.CacheHashes();
        var replay = await fixture.RunAsync(apply: true);
        Assert.True(replay.ExitCode == 0, replay.Error);
        using var receipt = JsonDocument.Parse(replay.Output);
        Assert.Equal("unchanged", receipt.RootElement.GetProperty("action").GetString());
        Assert.Equal(after.OrderBy(x => x.Key), fixture.CacheHashes().OrderBy(x => x.Key));
    }

    [Theory]
    [InlineData("foreign-plugin")]
    [InlineData("different-hooks")]
    [InlineData("source-hash")]
    [InlineData("existing-adapter")]
    public async Task Repair_refuses_mismatched_material_without_changing_cached_files(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync();
        switch (mismatch)
        {
            case "foreign-plugin":
                await File.WriteAllTextAsync(Path.Combine(fixture.Cache, ".codex-plugin", "plugin.json"), "{\"name\":\"foreign\",\"version\":\"1.0.0\"}");
                break;
            case "different-hooks": await File.AppendAllTextAsync(Path.Combine(fixture.Cache, "hooks", "hooks.json"), " "); break;
            case "source-hash": fixture.ExpectedHash = new string('0', 64); break;
            case "existing-adapter": await File.WriteAllTextAsync(fixture.Target, "preserve-existing-adapter"); break;
        }
        var before = fixture.CacheHashes();

        var result = await fixture.RunAsync(apply: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before.OrderBy(x => x.Key), fixture.CacheHashes().OrderBy(x => x.Key));
    }

    [Fact]
    public async Task Repair_refuses_a_linked_cache_directory_without_writing_through_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        var alias = Path.Combine(fixture.Root, "linked", "1.0.0");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        Directory.CreateSymbolicLink(alias, fixture.Cache);
        var before = fixture.CacheHashes();
        try
        {
            var result = await fixture.RunAsync(apply: true, cacheOverride: alias);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("refuses reparse points", result.Error, StringComparison.Ordinal);
            Assert.Equal(before.OrderBy(x => x.Key), fixture.CacheHashes().OrderBy(x => x.Key));
        }
        finally { Directory.Delete(alias); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "FluxKnowledgeHookCacheRepairTests", Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "marketplace", "plugins", "fluxknowledge");
        public string Cache => Path.Combine(Root, "plugins", "cache", "fluxknowledge", "fluxknowledge", "1.0.0");
        public string SourceAdapter => Path.Combine(Source, "hooks", "invoke-native-hook.ps1");
        public string Target => Path.Combine(Cache, "hooks", "invoke-native-hook.ps1");
        public string ExpectedHash { get; set; } = string.Empty;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await new NativeCodexPluginManifestWriter().WriteAsync(Path.Combine(fixture.Root, "marketplace"));
            foreach (var relative in new[] { ".codex-plugin/plugin.json", ".mcp.json", "hooks/hooks.json" })
            {
                var destination = Path.Combine(fixture.Cache, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(fixture.Source, relative), destination);
            }
            fixture.ExpectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourceAdapter)));
            return fixture;
        }

        public Dictionary<string, string> CacheHashes() => Directory.GetFiles(Cache, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(Cache, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(bool apply, string? cacheOverride = null)
        {
            var directory = new DirectoryInfo(FluxKnowledge.Testing.RepositoryFiles.Root());
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FluxKnowledge.slnx"))) directory = directory.Parent;
            Assert.NotNull(directory);
            var script = Path.Combine(directory.FullName, "scripts", "deploy", "repair-native-codex-hook-cache.ps1");
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-File", script, "-SourcePluginRoot", Source, "-CachedPluginRoot", cacheOverride ?? Cache, "-ExpectedAdapterSha256", ExpectedHash }) start.ArgumentList.Add(argument);
            if (apply) start.ArgumentList.Add("-Apply");
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, await output.WaitAsync(timeout.Token), await error.WaitAsync(timeout.Token));
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
