Set-StrictMode -Version Latest

function Invoke-SourceJournalWebBootstrap {
    param([Parameter(Mandatory)][hashtable]$Ports, [switch]$ResumeInstalled)

    $releaseAttempted = $false
    $holdAcquired = $false
    try {
        if ($ResumeInstalled) {
            & $Ports.AssertHold
            $holdAcquired = $true
            & $Ports.PrepareInstalled
        } else {
            & $Ports.WritePhase 'Prepared'
            & $Ports.CreateHold
            $holdAcquired = $true
            & $Ports.AssertHold
            & $Ports.StopWeb
            & $Ports.AssertHold
            & $Ports.WritePhase 'InstallIntent'
            & $Ports.InstallWebAndOperator
        }
        & $Ports.AssertHold
        & $Ports.StartWeb
        & $Ports.VerifyHeld
        & $Ports.AssertHold
        & $Ports.WritePhase 'ReleaseIntent'
        $releaseAttempted = $true
        & $Ports.ReleaseHold
        & $Ports.VerifyReleased
        & $Ports.WritePhase 'Completed'
    }
    catch {
        if ($releaseAttempted) {
            throw "web-bootstrap-release-uncertain: reconcile the observed state without restoring a predecessor. $($_.Exception.Message)"
        }
        if ($holdAcquired) {
            throw "web-bootstrap-failed-held: preserve the hold and installed evidence for separately reviewed recovery. $($_.Exception.Message)"
        }
        throw
    }
}

Export-ModuleMember -Function Invoke-SourceJournalWebBootstrap
