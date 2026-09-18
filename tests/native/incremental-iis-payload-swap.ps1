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
$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-iis-incremental.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript -PathType Leaf)) {
    throw "The incremental IIS deployment script is missing."
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

function Import-DeploymentFunction {
    param(
        [Parameter(Mandatory)][System.Management.Automation.Language.Ast]$Ast,
        [Parameter(Mandatory)][string]$Name
    )

    $definitions = @($Ast.FindAll({
        param($candidate)
        $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $candidate.Name -ceq $Name
    }, $true))
    if ($definitions.Count -ne 1) {
        throw "The deployment function is missing or ambiguous: $Name"
    }
    $captured = & ([scriptblock]::Create(
        $definitions[0].Extent.Text + "`n(Get-Item -LiteralPath 'Function:$Name').ScriptBlock"))
    Set-Item -LiteralPath "Function:script:$Name" -Value $captured
}

function Assert-ActionFails {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessage,
        [Parameter(Mandatory)][string]$AssertionMessage
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -match $ExpectedMessage) {
            return
        }
        throw
    }
    throw $AssertionMessage
}

$tokens = $null
$parseErrors = $null
$deploymentAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $deploymentScript,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "The incremental IIS deployment script does not parse."
}
foreach ($functionName in @(
    "Assert-NotReparsePoint",
    "New-DeploymentValidationHold",
    "Remove-DeploymentValidationHold")) {
    Import-DeploymentFunction -Ast $deploymentAst -Name $functionName
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
        -ActivateCandidate { Copy-Item -LiteralPath $candidateRoot -Destination $activeRoot -Recurse -Force } `
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
    $runtimeRoot = Join-Path $temporaryRoot "runtime"
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    $validationHoldPath = Join-Path $runtimeRoot "deployment-validation-hold.json"
    $releaseId = "candidate-validation-release"
    $rollbackState = [ordered]@{
        Stops = 0
        Starts = 0
        Validations = 0
        HoldCreationResults = [System.Collections.ArrayList]::new()
        Baseline = $null
        FirstBaseline = $null
    }
    $validationFailureSeen = $false
    try {
        Invoke-IncrementalApplicationPayloadSwap `
            -ApplicationRoot $activeRoot `
            -CandidateRoot $candidateRoot `
            -PreviousRoot $previousRoot `
            -FailedRoot $failedRoot `
            -ActivateCandidate { Copy-Item -LiteralPath $candidateRoot -Destination $activeRoot -Recurse -Force } `
            -StopApplication {
                $rollbackState.Stops++
                $holdCreationResult = New-DeploymentValidationHold `
                    -Path $validationHoldPath -ReleaseId $releaseId
                [void]$rollbackState.HoldCreationResults.Add($holdCreationResult)
                if ($null -eq $rollbackState.Baseline) {
                    $rollbackState.FirstBaseline = [pscustomobject]@{ CapturedAtStop = $rollbackState.Stops }
                    $rollbackState.Baseline = $rollbackState.FirstBaseline
                }
            } `
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
        (Read-TestPayloadMarker -Path $candidateRoot) -ne "candidate" -or
        (Test-Path -LiteralPath $previousRoot) -or
        $rollbackState.Stops -ne 2 -or $rollbackState.Starts -ne 2 -or $rollbackState.Validations -ne 2) {
        throw "A failed candidate validation did not restore and validate the previous application payload."
    }
    if ($rollbackState.HoldCreationResults.Count -ne 2 -or
        -not $rollbackState.HoldCreationResults[0] -or
        $rollbackState.HoldCreationResults[1] -or
        -not [object]::ReferenceEquals($rollbackState.Baseline, $rollbackState.FirstBaseline) -or
        -not (Test-Path -LiteralPath $validationHoldPath -PathType Leaf)) {
        throw "Candidate-validation rollback did not retain its first baseline and validation hold."
    }
    Remove-DeploymentValidationHold -Path $validationHoldPath -ReleaseId $releaseId
    if (Test-Path -LiteralPath $validationHoldPath) {
        throw "The candidate-validation hold was not removed by explicit cleanup."
    }

    $foreignHoldPath = Join-Path $runtimeRoot "foreign-hold.json"
    $foreignPayload = '"foreign-release"'
    [IO.File]::WriteAllText($foreignHoldPath, $foreignPayload, [Text.UTF8Encoding]::new($false))
    Assert-ActionFails `
        -Action { New-DeploymentValidationHold -Path $foreignHoldPath -ReleaseId $releaseId } `
        -ExpectedMessage "already exists" `
        -AssertionMessage "A foreign deployment-validation hold was accepted."
    if ([IO.File]::ReadAllText($foreignHoldPath, [Text.Encoding]::UTF8) -cne $foreignPayload) {
        throw "A foreign deployment-validation hold was modified."
    }

    $malformedHoldPath = Join-Path $runtimeRoot "malformed-hold.json"
    $malformedPayload = "not-json"
    [IO.File]::WriteAllText($malformedHoldPath, $malformedPayload, [Text.UTF8Encoding]::new($false))
    Assert-ActionFails `
        -Action { New-DeploymentValidationHold -Path $malformedHoldPath -ReleaseId $releaseId } `
        -ExpectedMessage "already exists" `
        -AssertionMessage "A malformed deployment-validation hold was accepted."
    if ([IO.File]::ReadAllText($malformedHoldPath, [Text.Encoding]::UTF8) -cne $malformedPayload) {
        throw "A malformed deployment-validation hold was modified."
    }

    $reparseTargetPath = Join-Path $runtimeRoot "reparse-target.json"
    $reparsePayload = '"foreign-release"'
    [IO.File]::WriteAllText($reparseTargetPath, $reparsePayload, [Text.UTF8Encoding]::new($false))
    $reparseHoldPath = Join-Path $runtimeRoot "reparse-hold.json"
    [IO.File]::CreateSymbolicLink($reparseHoldPath, $reparseTargetPath) | Out-Null
    Assert-ActionFails `
        -Action { New-DeploymentValidationHold -Path $reparseHoldPath -ReleaseId $releaseId } `
        -ExpectedMessage "reparse point" `
        -AssertionMessage "A reparse-point deployment-validation hold was accepted."
    if ([IO.File]::ReadAllText($reparseTargetPath, [Text.Encoding]::UTF8) -cne $reparsePayload) {
        throw "A reparse-point deployment-validation hold target was modified."
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
            -ActivateCandidate { Copy-Item -LiteralPath $candidateRoot -Destination $activeRoot -Recurse -Force } `
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
            -ActivateCandidate { Copy-Item -LiteralPath $candidateRoot -Destination $activeRoot -Recurse -Force } `
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
