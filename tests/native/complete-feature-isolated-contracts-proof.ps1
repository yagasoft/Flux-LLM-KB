[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$dispatcher = Join-Path $SourceRoot 'tests/native/complete-feature-isolated-contracts.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeContractPairProof-$([Guid]::NewGuid().ToString('N'))"
$savedMode = $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_MODE
$savedRunner = $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_RUNNER
$passed = $false
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_RUNNER = Join-Path $SourceRoot 'tests/native/WorkflowScenarioRunner.psm1'
    foreach ($mode in @('success', 'fail-closeout', 'fail-deployment', 'cancel')) {
        $fixture = Join-Path $root $mode
        New-Item -ItemType Directory -Path (Join-Path $fixture 'tests/native'), (Join-Path $fixture 'scripts/dev') | Out-Null
        Set-Content -LiteralPath (Join-Path $fixture 'scripts/dev/complete-feature.ps1') -Value '# Disposable proof only.'
        @'
param([string]$SourceRoot, [string]$CloseoutScript)
$ErrorActionPreference='Stop'
$role=if($MyInvocation.MyCommand.Name -eq 'complete-feature-integration.ps1'){'closeout'}else{'deployment'}
Set-Content -LiteralPath (Join-Path $SourceRoot "$role.started") -Value $PID
if($env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_MODE -eq 'cancel'){
    if($role -eq 'closeout'){
        Import-Module $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_RUNNER -Force
        Invoke-WorkflowScenarios -ScriptPath (Join-Path $SourceRoot 'nested.ps1') -SourceRoot $SourceRoot -CloseoutScript $CloseoutScript -Scenario @('nested') -OutputRoot (Join-Path $SourceRoot 'nested-results')|Out-Null
    }else{Start-Sleep -Seconds 90}
    exit 0
}
$peer=if($role -eq 'closeout'){'deployment'}else{'closeout'}
$deadline=[DateTimeOffset]::UtcNow.AddSeconds(15)
while(-not(Test-Path -LiteralPath (Join-Path $SourceRoot "$peer.started"))){
    if([DateTimeOffset]::UtcNow -ge $deadline){throw 'The two contracts did not overlap.'}
    Start-Sleep -Milliseconds 20
}
if($env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_MODE -eq "fail-$role"){[Console]::Error.WriteLine("Injected $role failure.");exit 7}
Write-Output "Disposable $role passed."
'@ | ForEach-Object {
            $fake = $_
            foreach ($name in @('complete-feature-integration.ps1', 'native-deployment-plan.ps1')) {
                $fake | Set-Content -LiteralPath (Join-Path $fixture "tests/native/$name")
            }
        }
        @'
param([string]$SourceRoot,[string]$CloseoutScript,[string]$Scenario,[int]$MaxParallelScenarios)
$hang=Join-Path $SourceRoot 'hang.ps1'
Set-Content -LiteralPath $hang -Value 'Start-Sleep -Seconds 90'
$start=[Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
foreach($argument in @('-NoProfile','-File',$hang)){$start.ArgumentList.Add($argument)}
$child=[Diagnostics.Process]::Start($start)
Set-Content -LiteralPath (Join-Path $SourceRoot 'descendant.pid') -Value $child.Id
Start-Sleep -Seconds 90
'@ | Set-Content -LiteralPath (Join-Path $fixture 'nested.ps1')
        $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_MODE = $mode
        $output = Join-Path $fixture 'results'
        if ($mode -eq 'cancel') {
            $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(10))
            $failure = $null
            try { & $dispatcher -SourceRoot $fixture -OutputRoot $output -CancellationToken $cancellation.Token | Out-Null }
            catch { $failure = $_ }
            finally { $cancellation.Dispose() }
            if ($null -eq $failure) { throw 'Cancellation unexpectedly passed.' }
            $pidPath = Join-Path $fixture 'descendant.pid'
            if (-not (Test-Path -LiteralPath $pidPath)) { throw 'Cancellation did not exercise the nested job descendant.' }
            $descendant = [int](Get-Content -LiteralPath $pidPath)
            if (Get-Process -Id $descendant -ErrorAction SilentlyContinue) { throw 'Nested cancellation left its owned descendant running.' }
        } else {
            & pwsh -NoProfile -File $dispatcher -SourceRoot $fixture -OutputRoot $output *> (Join-Path $fixture 'dispatcher.log')
            $exitCode = $LASTEXITCODE
            if (($exitCode -eq 0) -ne ($mode -eq 'success')) { throw "Unexpected aggregate exit for $mode`: $exitCode" }
            $results = @(Get-Content -LiteralPath (Join-Path $output 'scenario-results.json') -Raw | ConvertFrom-Json)
            if ($results.Count -ne 2 -or @($results.ProcessId | Select-Object -Unique).Count -ne 2) { throw 'The pair lost a child or shared its process.' }
            foreach ($result in $results) {
                $expected = if ($mode -eq "fail-$($result.Scenario)") { 7 } else { 0 }
                if ($result.ExitCode -ne $expected -or
                    -not (Test-Path -LiteralPath (Join-Path $output "$($result.Scenario).stdout.log")) -or
                    -not (Test-Path -LiteralPath (Join-Path $output "$($result.Scenario).stderr.log"))) {
                    throw "Child failure or durable diagnostics were lost for $mode."
                }
            }
        }
    }
    $passed = $true
    Write-Output 'Isolated contract pair proof passed: overlap, each child failure and nested-job cancellation.'
} finally {
    $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_MODE = $savedMode
    $env:FLUXKNOWLEDGE_CONTRACT_PAIR_PROOF_RUNNER = $savedRunner
    if ($passed) {
        $resolved = (Resolve-Path -LiteralPath $root).Path
        if ((Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeContractPairProof-*' -or
            -not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe contract-pair proof cleanup path.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    } else { Write-Warning "Contract-pair proof evidence preserved at $root" }
}
