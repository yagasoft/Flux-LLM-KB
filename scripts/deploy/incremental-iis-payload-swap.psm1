Set-StrictMode -Version Latest

function Invoke-IncrementalApplicationPayloadSwap {
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string]$ApplicationRoot,
        [Parameter(Mandatory)]
        [string]$CandidateRoot,
        [Parameter(Mandatory)]
        [string]$PreviousRoot,
        [Parameter(Mandatory)]
        [string]$FailedRoot,
        [Parameter(Mandatory)]
        [scriptblock]$StopApplication,
        [Parameter(Mandatory)]
        [scriptblock]$StartApplication,
        [Parameter(Mandatory)]
        [scriptblock]$ValidateApplication
    )

    if (-not (Test-Path -LiteralPath $ApplicationRoot -PathType Container)) {
        throw "The active application payload is missing."
    }
    if (-not (Test-Path -LiteralPath $CandidateRoot -PathType Container)) {
        throw "The candidate application payload is missing."
    }
    if (Test-Path -LiteralPath $PreviousRoot) {
        throw "The previous application payload location already exists."
    }
    if (Test-Path -LiteralPath $FailedRoot) {
        throw "The failed application payload location already exists."
    }

    $poolStopped = $false
    $stopRequested = $false
    $previousMoved = $false
    $candidateMoved = $false
    try {
        $stopRequested = $true
        & $StopApplication
        $poolStopped = $true

        Move-Item -LiteralPath $ApplicationRoot -Destination $PreviousRoot -ErrorAction Stop
        $previousMoved = $true
        Move-Item -LiteralPath $CandidateRoot -Destination $ApplicationRoot -ErrorAction Stop
        $candidateMoved = $true

        & $StartApplication
        $poolStopped = $false
        & $ValidateApplication

        return [pscustomobject]@{
            RolledBack = $false
            PreviousPayload = $PreviousRoot
        }
    }
    catch {
        $updateFailure = $_
        try {
            if (-not $stopRequested -or $candidateMoved) {
                $stopRequested = $true
                & $StopApplication
                $poolStopped = $true
            }
            if ($candidateMoved -and (Test-Path -LiteralPath $ApplicationRoot -PathType Container)) {
                Move-Item -LiteralPath $ApplicationRoot -Destination $FailedRoot -ErrorAction Stop
            }
            if ($previousMoved -and (Test-Path -LiteralPath $PreviousRoot -PathType Container) -and
                -not (Test-Path -LiteralPath $ApplicationRoot -PathType Container)) {
                Move-Item -LiteralPath $PreviousRoot -Destination $ApplicationRoot -ErrorAction Stop
            }
            if (-not (Test-Path -LiteralPath $ApplicationRoot -PathType Container)) {
                throw "The original application payload is unavailable for rollback."
            }

            & $StartApplication
            $poolStopped = $false
            & $ValidateApplication
        }
        catch {
            throw "Incremental IIS deployment failed and rollback was unsuccessful. Update failure: $($updateFailure.Exception.Message) Rollback failure: $($_.Exception.Message)"
        }

        throw "Incremental IIS deployment failed and the prior application payload was restored. Update failure: $($updateFailure.Exception.Message)"
    }
}

Export-ModuleMember -Function Invoke-IncrementalApplicationPayloadSwap
