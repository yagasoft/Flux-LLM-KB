using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Fresh, bounded OS reference checks while native archive retirement owns its exclusion locks.</summary>
internal static class RecoveryArchiveConsumers
{
    internal static async Task AssertAbsentAsync(RecoveryArchiveRetirementPlan plan, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("index-recovery-retirement-windows-required");
        var paths = plan.Moves.SelectMany(move => new[] { Path.Combine(plan.RecoveryRoot, move.RelativePath),
            Path.Combine(plan.QuarantineRoot, move.RelativePath) }).ToArray();
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)));
        start.Environment.Remove("ConnectionStrings__FluxKnowledge");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("index-recovery-retirement-consumer-probe-unavailable");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var output = child.StandardOutput.ReadToEndAsync(deadline.Token); var error = child.StandardError.ReadToEndAsync(deadline.Token);
            await child.StandardInput.BaseStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(paths), deadline.Token); child.StandardInput.Close();
            await child.WaitForExitAsync(deadline.Token);
            if (child.ExitCode != 0 || !string.IsNullOrWhiteSpace(await error))
                throw new InvalidOperationException("index-recovery-retirement-live-consumer-or-observation-failed");
            using var response = JsonDocument.Parse(await output);
            if (!response.RootElement.GetProperty("Passed").GetBoolean())
                throw new InvalidOperationException("index-recovery-retirement-live-consumer-or-observation-failed");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private const string Script = """
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$reader=[IO.StreamReader]::new([Console]::OpenStandardInput(),[Text.UTF8Encoding]::new($false,$true))
try{$roots=@($reader.ReadToEnd()|ConvertFrom-Json)}finally{$reader.Dispose()}
if($roots.Count -gt 1024){throw 'Reference root budget exceeded'}
$roots=@($roots|ForEach-Object{[IO.Path]::GetFullPath($_).TrimEnd('\').ToLowerInvariant()})
function References([string]$Value){
    if(!$Value){return $false}
    $value=[Environment]::ExpandEnvironmentVariables($Value).Replace('/','\').Replace('\\?\','').ToLowerInvariant()
    foreach($root in $roots){
        $offset=$value.IndexOf($root,[StringComparison]::Ordinal)
        while($offset -ge 0){
            $end=$offset+$root.Length
            if($end -eq $value.Length -or $value[$end] -in @('\','"',"'",' ')){return $true}
            $offset=$value.IndexOf($root,$offset+1,[StringComparison]::Ordinal)
        }
    }
    return $false
}
$processes=@(Get-CimInstance Win32_Process -OperationTimeoutSec 5)
$services=@(Get-CimInstance Win32_Service -OperationTimeoutSec 5)
$tasks=@(Get-ScheduledTask)
if($processes.Count+$services.Count+$tasks.Count -gt 8192){throw 'Reference observation budget exceeded'}
foreach($item in $processes){if((References $item.ExecutablePath) -or (References $item.CommandLine)){throw 'Archive process consumer present'}}
foreach($item in $services){if(References $item.PathName){throw 'Archive service consumer present'}}
foreach($item in $tasks){
    if($item.State.ToString() -eq 'Disabled'){continue}
    foreach($action in $item.Actions){
        if((References $action.Execute) -or (References $action.Arguments) -or (References $action.WorkingDirectory)){throw 'Archive task consumer present'}
    }
}
[ordered]@{Passed=$true;Processes=$processes.Count;Services=$services.Count;Tasks=$tasks.Count}|ConvertTo-Json -Compress
""";
}
