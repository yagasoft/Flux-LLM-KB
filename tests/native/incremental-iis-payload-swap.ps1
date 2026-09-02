[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$modulePath = Join-Path $SourceRoot "scripts\deploy\incremental-iis-payload-swap.psm1"
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "The incremental IIS payload-swap module is missing."
}

function New-TestPayload {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Marker)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path "marker.txt"), $Marker, [Text.UTF8Encoding]::new($false))
}

function Read-TestPayloadMarker {
    param([Parameter(Mandatory)][string]$Path)

    return [IO.File]::ReadAllText((Join-Path $Path "marker.txt"), [Text.UTF8Encoding]::new($false))
}

$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryParent ("FluxKnowledge-IncrementalSwap-" + [Guid]::NewGuid().ToString("N"))))
if (-not $temporaryRoot.StartsWith($temporaryParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The temporary payload-swap test root is outside the system temporary directory."
}

$module = Import-Module $modulePath -Force -PassThru
try {
    $activeRoot = Join-Path $temporaryRoot "active"
    $candidateRoot = Join-Path $temporaryRoot "candidate"
    $previousRoot = Join-Path $temporaryRoot "previous"
    $failedRoot = Join-Path $temporaryRoot "failed"
    New-TestPayload -Path $activeRoot -Marker "previous"
    New-TestPayload -Path $candidateRoot -Marker "candidate"
    $successfulState = [ordered]@{ Stops = 0; Starts = 0; Validations = 0 }

    $success = Invoke-IncrementalApplicationPayloadSwap `
        -ApplicationRoot $activeRoot `
        -CandidateRoot $candidateRoot `
        -PreviousRoot $previousRoot `
        -FailedRoot $failedRoot `
        -StopApplication { $successfulState.Stops++ } `
        -StartApplication { $successfulState.Starts++ } `
        -ValidateApplication {
            $successfulState.Validations++
            if ((Read-TestPayloadMarker -Path $activeRoot) -ne "candidate") {
                throw "The successful payload swap did not activate the candidate."
            }
        }

    if ($success.RolledBack -or
        (Read-TestPayloadMarker -Path $activeRoot) -ne "candidate" -or
        (Read-TestPayloadMarker -Path $previousRoot) -ne "previous" -or
        $successfulState.Stops -ne 1 -or $successfulState.Starts -ne 1 -or $successfulState.Validations -ne 1) {
        throw "The healthy incremental payload swap did not retain the previous payload and activate the candidate."
    }

    Remove-Item -LiteralPath $activeRoot -Recurse -Force
    Remove-Item -LiteralPath $previousRoot -Recurse -Force
    New-TestPayload -Path $activeRoot -Marker "previous"
    New-TestPayload -Path $candidateRoot -Marker "candidate"
    $rollbackState = [ordered]@{ Stops = 0; Starts = 0; Validations = 0 }
    $validationFailureSeen = $false
    try {
        Invoke-IncrementalApplicationPayloadSwap `
            -ApplicationRoot $activeRoot `
            -CandidateRoot $candidateRoot `
            -PreviousRoot $previousRoot `
            -FailedRoot $failedRoot `
            -StopApplication { $rollbackState.Stops++ } `
            -StartApplication { $rollbackState.Starts++ } `
            -ValidateApplication {
                $rollbackState.Validations++
                if ($rollbackState.Validations -eq 1) {
                    throw "candidate validation failed"
                }
                if ((Read-TestPayloadMarker -Path $activeRoot) -ne "previous") {
                    throw "The rollback validation did not receive the original payload."
                }
            }
    }
    catch {
        if ($_.Exception.Message -match "prior application payload was restored") {
            $validationFailureSeen = $true
        }
        else {
            throw
        }
    }

    if (-not $validationFailureSeen -or
        (Read-TestPayloadMarker -Path $activeRoot) -ne "previous" -or
        (Read-TestPayloadMarker -Path $failedRoot) -ne "candidate" -or
        (Test-Path -LiteralPath $previousRoot) -or
        $rollbackState.Stops -ne 2 -or $rollbackState.Starts -ne 2 -or $rollbackState.Validations -ne 2) {
        throw "A failed candidate validation did not restore and validate the previous application payload."
    }

    Remove-Item -LiteralPath $activeRoot -Recurse -Force
    Remove-Item -LiteralPath $failedRoot -Recurse -Force
    New-TestPayload -Path $activeRoot -Marker "previous"
    New-TestPayload -Path $candidateRoot -Marker "candidate"
    $blockedParent = Join-Path $temporaryRoot "blocked-parent"
    [IO.File]::WriteAllText($blockedParent, "not-a-directory", [Text.UTF8Encoding]::new($false))
    $moveFailureState = [ordered]@{ Stops = 0; Starts = 0; Validations = 0 }
    $moveFailureSeen = $false
    try {
        Invoke-IncrementalApplicationPayloadSwap `
            -ApplicationRoot $activeRoot `
            -CandidateRoot $candidateRoot `
            -PreviousRoot (Join-Path $blockedParent "previous") `
            -FailedRoot $failedRoot `
            -StopApplication { $moveFailureState.Stops++ } `
            -StartApplication { $moveFailureState.Starts++ } `
            -ValidateApplication {
                $moveFailureState.Validations++
                if ((Read-TestPayloadMarker -Path $activeRoot) -ne "previous") {
                    throw "The move-failure recovery did not leave the original payload active."
                }
            }
    }
    catch {
        if ($_.Exception.Message -match "prior application payload was restored") {
            $moveFailureSeen = $true
        }
        else {
            throw
        }
    }

    if (-not $moveFailureSeen -or
        (Read-TestPayloadMarker -Path $activeRoot) -ne "previous" -or
        (Read-TestPayloadMarker -Path $candidateRoot) -ne "candidate" -or
        $moveFailureState.Stops -ne 1 -or $moveFailureState.Starts -ne 1 -or $moveFailureState.Validations -ne 1) {
        throw "A pre-swap move failure did not restart and validate the still-active original payload."
    }

    $stopFailureState = [ordered]@{ Stops = 0; Starts = 0; Validations = 0 }
    $stopFailureSeen = $false
    try {
        Invoke-IncrementalApplicationPayloadSwap `
            -ApplicationRoot $activeRoot `
            -CandidateRoot $candidateRoot `
            -PreviousRoot $previousRoot `
            -FailedRoot $failedRoot `
            -StopApplication {
                $stopFailureState.Stops++
                throw "stop status confirmation failed"
            } `
            -StartApplication { $stopFailureState.Starts++ } `
            -ValidateApplication {
                $stopFailureState.Validations++
                if ((Read-TestPayloadMarker -Path $activeRoot) -ne "previous") {
                    throw "The stop-failure recovery did not leave the original payload active."
                }
            }
    }
    catch {
        if ($_.Exception.Message -match "prior application payload was restored") {
            $stopFailureSeen = $true
        }
        else {
            throw
        }
    }

    if (-not $stopFailureSeen -or
        (Read-TestPayloadMarker -Path $activeRoot) -ne "previous" -or
        (Read-TestPayloadMarker -Path $candidateRoot) -ne "candidate" -or
        $stopFailureState.Stops -ne 1 -or $stopFailureState.Starts -ne 1 -or $stopFailureState.Validations -ne 1) {
        throw "A stop-confirmation failure did not restart and validate the original payload without a second stop."
    }
}
finally {
    Remove-Module $module -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "Incremental IIS payload-swap contract passed."
