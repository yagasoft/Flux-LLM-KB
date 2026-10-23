using System.Diagnostics;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Visibility;

[Collection("sql-full-text")]
public sealed class DashboardSnapshotActivationDeploymentTests(NativeSqlServerFixture fixture, ITestOutputHelper output)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Snapshot_activation_executes_on_owned_SQL_without_changing_schema_or_other_options()
    {
        var source = FluxKnowledge.Testing.RepositoryFiles.Root();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(source, "tests/native/dashboard-snapshot-activation-sql.ps1"),
            "-SourceRoot", source, "-ExpectedDatabase", fixture.DatabaseName }) start.ArgumentList.Add(argument);
        start.Environment["FLUXKNOWLEDGE_DASHBOARD_DISPOSABLE_SQL"] = fixture.ConnectionString;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
        var transcript = await stdout + await stderr;
        output.WriteLine(transcript);
        Assert.True(child.ExitCode == 0, transcript);
    }
}
