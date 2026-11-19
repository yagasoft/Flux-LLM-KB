[CmdletBinding()]
param([string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

$ErrorActionPreference = 'Stop'
$updater = Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($updater, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'The incremental updater must parse.' }
if ('ApplyOutlookWatchedSourceMigration' -cnotin $ast.ParamBlock.Parameters.Name.VariablePath.UserPath) {
    throw 'The incremental updater must expose the typed Outlook watched-source migration.'
}
if ('RecoverUnattemptedOutlookRelease' -cnotin $ast.ParamBlock.Parameters.Name.VariablePath.UserPath) {
    throw 'An Outlook release stopped before migration needs typed predecessor recovery.'
}

$node = $ast.Find({
    param($candidate)
    $candidate -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $candidate.Name -ceq 'Assert-PassageWindowEligibilityUpdateOptions'
}, $true)
. ([scriptblock]::Create('function Assert-PassageWindowEligibilityUpdateOptions ' + $node.Body.Extent.Text))

function Assert-Refused {
    param([hashtable]$Options)
    try { Assert-PassageWindowEligibilityUpdateOptions $Options }
    catch { return }
    throw 'The typed Outlook migration accepted incompatible or incomplete options.'
}

$reviewed = @{
    ApplyOutlookWatchedSourceMigration = $true
    ExpectedSourceJournalDatabaseIdentity = 'owned'
    ReviewedSourceJournalPayloads = @(('Web=' + ('A' * 64)), ('OutlookHost=' + ('B' * 64)), ('Cli=' + ('C' * 64)))
    Apply = $true
}
Assert-PassageWindowEligibilityUpdateOptions $reviewed
foreach ($flag in @('ApplyPassageWindowEligibilityMigration', 'ApplyMigrations', 'RecoverStoppedPool')) {
    $conflicting = $reviewed.Clone()
    $conflicting[$flag] = $true
    Assert-Refused $conflicting
}
$missing = $reviewed.Clone()
$missing.Remove('ExpectedSourceJournalDatabaseIdentity')
Assert-Refused $missing

$missing = $reviewed.Clone()
$missing.Remove('ReviewedSourceJournalPayloads')
Assert-Refused $missing

& (Join-Path $PSScriptRoot 'passage-window-eligibility-updater.ps1') -SourceRoot $SourceRoot -Outlook
