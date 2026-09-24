[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (!$SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-corpus-fulltext-migration.psm1') -Force
$contract = Get-CorpusFullTextMigrationContract

function New-TestState {
    @{
        History = @('earlier', $contract.Baseline)
        IndexPresent = $false; IndexValid = $false
        FullTextInstalled = $true; CataloguePresent = $true; KeyPresent = $true
        UpCalls = 0; DownCalls = 0
    }
}
function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed 'Expected refusal/failure was not observed.'
}

foreach ($failurePoint in @('before-index', 'after-index', 'after-history', 'none')) {
    $db = New-TestState
    $state = New-CorpusFullTextMigrationState -OriginalHistory $db.History
    $read = { [pscustomobject]$db }.GetNewClosure()
    $up = {
        $db.UpCalls++
        if ($failurePoint -eq 'before-index') { throw 'synthetic create failure' }
        $db.IndexPresent = $true; $db.IndexValid = $true
        if ($failurePoint -eq 'after-index') { throw 'synthetic history failure' }
        $db.History = @($db.History) + $contract.Target
        if ($failurePoint -eq 'after-history') { throw 'synthetic connection failure' }
    }.GetNewClosure()
    if ($failurePoint -eq 'none') {
        Invoke-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunUp $up
    } else {
        Expect-Failure { Invoke-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunUp $up }
    }
    Invoke-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunUp $up
    Assert-True ($db.UpCalls -eq 1) 'Recovery replayed the migration attempt.'
    Assert-True (!$state.RollbackVerified) 'An attempted migration must retain the hold.'
    $down = {
        $db.DownCalls++; $db.IndexPresent = $false; $db.IndexValid = $false
        $db.History = @($db.History | Where-Object { $_ -ne $contract.Target })
    }.GetNewClosure()
    Undo-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunDown $down
    Assert-True (!$state.RollbackVerified) 'Schema rollback released the hold before old application validation.'
    Expect-Failure { Confirm-CorpusFullTextMigrationRollback -State $state -ValidatePriorApplication { throw 'old app probe failed' } }
    Assert-True (!$state.RollbackVerified) 'Failed old application validation released the hold.'
    Confirm-CorpusFullTextMigrationRollback -State $state -ValidatePriorApplication { }
    Assert-True $state.RollbackVerified 'Verified schema and prior application did not complete rollback.'
}

$preSql = New-CorpusFullTextMigrationState -OriginalHistory @('earlier', $contract.Baseline)
Expect-Failure { Invoke-CorpusFullTextMigrationAttempt -State $preSql -ReadState { throw 'pre-SQL read failed' } -RunUp { throw 'must not run' } }
Undo-CorpusFullTextMigrationAttempt -State $preSql -ReadState { throw 'must not read' } -RunDown { throw 'must not run' }
Assert-True (!$preSql.RollbackVerified) 'Pre-SQL recovery released hold before prior application restart.'
Expect-Failure { Confirm-CorpusFullTextMigrationRollback -State $preSql -ValidatePriorApplication { throw 'old app failed' } }
Assert-True (!$preSql.RollbackVerified) 'Pre-SQL recovery released hold after failed prior application probe.'

$db = New-TestState
$state = New-CorpusFullTextMigrationState -OriginalHistory $db.History
$read = { [pscustomobject]$db }.GetNewClosure()
Invoke-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunUp {
    $db.IndexPresent = $true; $db.IndexValid = $true; $db.History += $contract.Target
}
Expect-Failure { Undo-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunDown { throw 'rollback SQL failed' } }
Assert-True (!$state.RollbackVerified) 'Failed rollback SQL released the hold.'
$db.History += 'unexpected-successor'
Expect-Failure { Undo-CorpusFullTextMigrationAttempt -State $state -ReadState $read -RunDown { throw 'must not execute unknown schema rollback' } }
Assert-True (!$state.RollbackVerified) 'Unknown schema allowed hold release.'
Write-Output 'Corpus Full-Text migration recovery passed: partial create/history, no replay, failed rollback and failed prior-app validation.'
