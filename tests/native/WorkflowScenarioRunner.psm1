function Invoke-WorkflowScenarios {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$CloseoutScript,
        [Parameter(Mandatory)][string[]]$Scenario,
        [ValidateRange(1, 4)][int]$MaxParallelScenarios = 4,
        [string[]]$SerialScenario = @('git-timeout', 'timeout'),
        [Parameter(Mandatory)][string]$OutputRoot,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None,
        [scriptblock]$ProcessLauncher = { param($StartInfo, $Name) [Diagnostics.Process]::Start($StartInfo) }
    )

    $ErrorActionPreference = 'Stop'
    if (-not $IsWindows) { throw 'Parallel workflow scenarios require Windows process jobs. Use the sequential harness on other platforms.' }
    if (@($Scenario | Select-Object -Unique).Count -ne $Scenario.Count -or
        @($Scenario | Where-Object { $_ -notmatch '\A[a-z][a-z0-9-]*\z' }).Count) {
        throw 'Scenarios require distinct, simple names.'
    }
    if (-not ('WorkflowScenarioJob' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'WorkflowScenarioProcess.cs') }
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    $OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path
    $script = (Resolve-Path -LiteralPath $ScriptPath).Path
    $worker = Join-Path $PSScriptRoot 'WorkflowScenarioWorker.ps1'
    $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    $running = [Collections.Generic.List[object]]::new()
    $results = [Collections.Generic.List[object]]::new()
    $reportPath = Join-Path $OutputRoot 'scenario-results.json'
    $primaryError = $null

    function Start-Scenario([string]$Name) {
        $gate = Join-Path $OutputRoot ($Name + '.gate')
        if (Test-Path -LiteralPath $gate) { throw "Scenario start gate already exists: $Name" }
        $start = [Diagnostics.ProcessStartInfo]::new($pwsh)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-File', $worker, '-StartGate', $gate,
            '-ScriptPath', $script, '-SourceRoot', $SourceRoot, '-CloseoutScript', $CloseoutScript, '-Scenario', $Name)) {
            $start.ArgumentList.Add($argument)
        }
        $start.Environment.Remove('FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP') | Out-Null
        $container = [WorkflowScenarioJob]::new()
        $job = $null
        $started = [DateTimeOffset]::UtcNow
        try {
            $process = & $ProcessLauncher $start $Name
            $job = [pscustomobject]@{
                Scenario=$Name; Process=$process; Container=$container; StartedAtUtc=$started
                Output=$null; Error=$null; Interrupted=$false; Errors=[Collections.Generic.List[string]]::new()
            }
            $running.Add($job)
            $job.Output = $process.StandardOutput.ReadToEndAsync()
            $job.Error = $process.StandardError.ReadToEndAsync()
            $container.Assign($process)
            # Assignment precedes all scenario code, including descendant creation.
            [IO.File]::WriteAllText($gate, 'owned')
        } catch {
            if ($null -ne $job) { $job.Errors.Add("Launcher failed: $($_.Exception.Message)") }
            else {
                $container.Dispose()
                $results.Add([pscustomobject]@{
                    Scenario=$Name; ProcessId=$null; StartedAtUtc=$started; FinishedAtUtc=[DateTimeOffset]::UtcNow
                    ExitCode=$null; Interrupted=$true; Errors=@("Launcher failed: $($_.Exception.Message)"); Stdout=''
                })
            }
            throw
        }
    }

    function Stop-Scenario($Job) {
        $Job.Interrupted = $true
        try { $Job.Container.Terminate() }
        catch { $Job.Errors.Add("Job termination failed: $($_.Exception.Message)") }
        finally {
            # Closing the owned job also terminates descendants after worker exit.
            try { $Job.Container.Dispose() }
            catch { $Job.Errors.Add("Job disposal failed: $($_.Exception.Message)") }
        }
        try { if (-not $Job.Process.HasExited) { $Job.Process.Kill($true) } }
        catch { $Job.Errors.Add("Worker termination failed: $($_.Exception.Message)") }
    }

    function Read-Output($Job, $Task, [string]$Name) {
        if ($null -eq $Task) { return '' }
        try {
            if ($Task.Wait(3000)) { return $Task.GetAwaiter().GetResult() }
            $Job.Errors.Add("$Name pipe drain timed out.")
        } catch { $Job.Errors.Add("$Name pipe drain failed: $($_.Exception.Message)") }
        return ''
    }

    function Complete-Scenario($Job) {
        $process = $Job.Process
        $stdout = ''; $stderr = ''; $exitCode = $null; $processId = $process.Id
        $stdoutPath = Join-Path $OutputRoot ($Job.Scenario + '.stdout.log')
        $stderrPath = Join-Path $OutputRoot ($Job.Scenario + '.stderr.log')
        try {
            if (-not $process.WaitForExit(3000)) { $Job.Errors.Add('Worker exit timed out.') }
            else { $exitCode = $process.ExitCode }
            $stdout = Read-Output $Job $Job.Output 'stdout'
            $stderr = Read-Output $Job $Job.Error 'stderr'
            try { [IO.File]::WriteAllText($stdoutPath, $stdout) }
            catch { $Job.Errors.Add("stdout log failed: $($_.Exception.Message)") }
            try { [IO.File]::WriteAllText($stderrPath, $stderr) }
            catch { $Job.Errors.Add("stderr log failed: $($_.Exception.Message)") }
        } catch { $Job.Errors.Add("Completion failed: $($_.Exception.Message)") }
        finally {
            try { $Job.Container.Dispose() }
            catch { $Job.Errors.Add("Job disposal failed: $($_.Exception.Message)") }
            try { $process.Dispose() }
            catch { $Job.Errors.Add("Worker disposal failed: $($_.Exception.Message)") }
            $running.Remove($Job) | Out-Null
            $results.Add([pscustomobject]@{
                Scenario=$Job.Scenario; ProcessId=$processId; StartedAtUtc=$Job.StartedAtUtc; FinishedAtUtc=[DateTimeOffset]::UtcNow
                ExitCode=$exitCode; Interrupted=$Job.Interrupted; Errors=$Job.Errors.ToArray()
                StdoutPath=$stdoutPath; StderrPath=$stderrPath; Stdout=$stdout
            })
        }
    }

    try {
        foreach ($group in @(@{ Names=@($Scenario | Where-Object { $_ -notin $SerialScenario }); Limit=$MaxParallelScenarios },
            @{ Names=@($Scenario | Where-Object { $_ -in $SerialScenario }); Limit=1 })) {
            $queue = [Collections.Generic.Queue[string]]::new()
            foreach ($name in $group.Names) { $queue.Enqueue($name) }
            while ($queue.Count -or $running.Count) {
                $CancellationToken.ThrowIfCancellationRequested()
                while ($queue.Count -and $running.Count -lt $group.Limit) { Start-Scenario $queue.Dequeue() }
                foreach ($job in $running.ToArray()) {
                    if ($job.Process.HasExited) {
                        # Windows can signal the process handle just before job
                        # accounting removes that worker. Allow that notification
                        # to settle, with a bounded wait that still checks cancellation.
                        $accountingDeadline = [DateTimeOffset]::UtcNow.AddMilliseconds(250)
                        while ($job.Container.HasActiveProcesses -and [DateTimeOffset]::UtcNow -lt $accountingDeadline) {
                            $CancellationToken.ThrowIfCancellationRequested()
                            [Threading.Thread]::Sleep(10)
                        }
                        if ($job.Container.HasActiveProcesses) {
                            $job.Errors.Add('Worker exited with an active descendant.')
                            Stop-Scenario $job
                        }
                        Complete-Scenario $job
                    }
                }
                if ($running.Count) { Start-Sleep -Milliseconds 20 }
            }
        }
    } catch { $primaryError = $_ }
    finally {
        # Terminate every owned job first. A drain/log failure cannot strand siblings.
        foreach ($job in $running.ToArray()) { Stop-Scenario $job }
        foreach ($job in $running.ToArray()) { Complete-Scenario $job }
        try { [IO.File]::WriteAllText($reportPath, (ConvertTo-Json -InputObject $results.ToArray() -Depth 5)) }
        catch { if ($null -eq $primaryError) { $primaryError = $_ } }
    }
    if ($null -ne $primaryError) { throw $primaryError }
    $failed = @($results | Where-Object { $_.ExitCode -ne 0 -or $_.Interrupted -or $_.Errors.Count })
    if ($failed.Count) {
        $names = $failed | ForEach-Object { "$($_.Scenario) (exit $($_.ExitCode)): $($_.Errors -join '; ')" }
        throw "Workflow scenarios failed: $($names -join ', '). Results: $reportPath"
    }
    return $results.ToArray()
}

Export-ModuleMember -Function Invoke-WorkflowScenarios
