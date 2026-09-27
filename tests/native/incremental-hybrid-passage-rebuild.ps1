[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force

foreach ($failure in @('schema', 'reset', 'activate', 'finish', 'validate', 'release', 'none')) {
    $state = @{ SchemaAttempted = $false; ResetCommitted = $false; HoldReleased = $false }
    $evidence = @{ SavedBoundary = $false; Released = $false; Events = [Collections.Generic.List[string]]::new() }
    $parameters = @{
        State = $state
        SaveState = { if ($state.SchemaAttempted) { $evidence.SavedBoundary = $true }; $evidence.Events.Add('saved') }
        Quiesce = { $evidence.Events.Add('quiesce') }
        ApplySchema = {
            if (-not $evidence.SavedBoundary) { throw 'schema ran before durable recovery boundary' }
            $evidence.Events.Add('schema'); if ($failure -eq 'schema') { throw 'injected-schema' }
        }
        ResetAndPrepare = { $evidence.Events.Add('reset'); if ($failure -eq 'reset') { throw 'injected-reset-response-lost' }; $state.ResetCommitted = $true }
        ActivateAndStart = { $evidence.Events.Add('activate'); if ($failure -eq 'activate') { throw 'injected-activate' } }
        RunAndFinish = { $evidence.Events.Add('finish'); if ($failure -eq 'finish') { throw 'injected-finish' } }
        Validate = { $evidence.Events.Add('validate'); if ($failure -eq 'validate') { throw 'injected-validate' } }
        ReleaseHold = { if ($failure -eq 'release') { throw 'injected-release' }; $evidence.Events.Add('release'); $evidence.Released = $true }
    }
    try {
        Invoke-HybridPassageRebuildFlow @parameters
        if ($failure -ne 'none') { throw "Expected failure was not raised: $failure" }
    }
    catch {
        if ($failure -eq 'none' -or $_.Exception.Message -notmatch 'injected-') { throw }
    }
    if (-not $state.SchemaAttempted -or $evidence.Released -ne ($failure -eq 'none') -or $state.HoldReleased -ne ($failure -eq 'none')) {
        throw "The recovery boundary or hold release is incorrect after $failure."
    }
    if ($failure -ne 'none') {
        $failure = 'none'
        Invoke-HybridPassageRebuildFlow @parameters
        if (-not $state.HoldReleased -or -not $evidence.Released) { throw 'A retained operation did not resume through validation.' }
    }
}
Write-Output 'Incremental hybrid rebuild recovery flow passed (seven failures/success paths and six resumptions).'
