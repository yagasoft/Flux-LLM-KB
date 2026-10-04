[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (!$SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-code-disclosure-migration.psm1') -Force
$contract = Get-CodeDisclosureMigrationContract

function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed 'Expected refusal was not observed.'
}
function New-TestDatabase {
    @{ History = @('earlier', $contract.Baseline); TableCount = 0; SchemaValid = $false; UpCalls = 0 }
}

foreach ($failurePoint in @('before-sql', 'partial-schema', 'after-history', 'post-validation', 'none')) {
    $db = New-TestDatabase
    $state = New-CodeDisclosureMigrationState -DatabaseState ([pscustomobject]$db)
    $read = { [pscustomobject]$db }.GetNewClosure()
    $up = {
        $db.UpCalls++
        if ($failurePoint -eq 'before-sql') { throw 'synthetic SQL failure' }
        $db.TableCount = 1
        if ($failurePoint -eq 'partial-schema') { throw 'synthetic early batch failure' }
        $db.TableCount = 2; $db.SchemaValid = $true; $db.History += $contract.Target
        if ($failurePoint -eq 'after-history') { throw 'synthetic lost connection after commit' }
        if ($failurePoint -eq 'post-validation') { $db.SchemaValid = $false }
    }.GetNewClosure()
    if ($failurePoint -eq 'none') { Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState $read -RunUp $up }
    else { Expect-Failure { Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState $read -RunUp $up } }
    Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState $read -RunUp $up
    Assert-True ($db.UpCalls -eq 1) 'StopApplication replayed a migration attempt.'
    Assert-True (!$state.RollbackVerified) 'A migration attempt released the validation hold.'
    if ($failurePoint -in @('partial-schema', 'post-validation')) {
        Expect-Failure { Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState $read }
        Assert-True (!$state.SchemaRecoveryVerified -and !$state.RollbackVerified) 'Uncertain schema permitted prior-payload start/hold release.'
    } else {
        Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState $read
        Assert-True $state.SchemaRecoveryVerified 'Wholly old/new additive schema was not recognised.'
        Assert-True (!$state.RollbackVerified) 'Schema recognition released the hold before prior-payload probes.'
        Expect-Failure { Confirm-CodeDisclosureMigrationRecovery -State $state -ReadState $read -ValidatePriorApplication { throw 'prior payload probe failed' } }
        Assert-True (!$state.RollbackVerified) 'Failed prior-payload probes released the hold.'
        Confirm-CodeDisclosureMigrationRecovery -State $state -ReadState $read -ValidatePriorApplication { }
        Assert-True $state.RollbackVerified 'Complete schema and prior-payload verification did not permit recovery.'
    }
}

$db = New-TestDatabase
$state = New-CodeDisclosureMigrationState -DatabaseState ([pscustomobject]$db)
$db.History += 'unexpected-successor'
Expect-Failure { Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState { [pscustomobject]$db } -RunUp { throw 'must not execute' } }
Assert-True (!$state.Attempted) 'Changed preflight history reached SQL execution.'
Expect-Failure { Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState { [pscustomobject]$db } }

$db = New-TestDatabase
$db.History += $contract.Target; $db.TableCount = 2; $db.SchemaValid = $true
$state = New-CodeDisclosureMigrationState -DatabaseState ([pscustomobject]$db)
Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState { [pscustomobject]$db } -RunUp { throw 'already applied schema must not replay SQL' }
Assert-True $state.Applied 'Verified already-applied schema was not accepted.'
Expect-Failure { Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState { throw 'SQL state unavailable' } }
Assert-True (!$state.RollbackVerified -and !$state.SchemaRecoveryVerified) 'SQL uncertainty permitted hold release.'

Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
foreach ($failurePoint in @('candidate-probes', 'prior-probes', 'uncertain-schema')) {
    $testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('flux-code-recovery-' + [guid]::NewGuid().ToString('N'))))
    try {
        $live = Join-Path $testRoot 'live'; $candidate = Join-Path $testRoot 'candidate'
        New-Item -ItemType Directory -Path $live, $candidate -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $live 'version'), 'old')
        [IO.File]::WriteAllText((Join-Path $candidate 'version'), 'new')
        $db = New-TestDatabase; $state = New-CodeDisclosureMigrationState -DatabaseState ([pscustomobject]$db)
        $calls = @{ OldStarts=0; CandidateStarts=0 }
        $read = { [pscustomobject]$db }.GetNewClosure()
        Expect-Failure {
            Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $live -CandidateRoot $candidate `
                -PreviousRoot (Join-Path $testRoot 'previous') -FailedRoot (Join-Path $testRoot 'failed') `
                -ActivateCandidate { Move-Item -LiteralPath $candidate -Destination $live } `
                -StopApplication {
                    Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState $read -RunUp {
                        $db.UpCalls++; $db.TableCount=2; $db.SchemaValid=$true; $db.History += $contract.Target
                    }
                } -StartApplication {
                    if ([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'old') {
                        Assert-True $state.SchemaRecoveryVerified 'Prior payload started before schema compatibility was verified.'
                        $calls.OldStarts++
                    } else { $calls.CandidateStarts++ }
                } -ValidateApplication {
                    if ($failurePoint -ceq 'uncertain-schema') { $db.SchemaValid=$false }
                    throw 'synthetic candidate probe failure'
                } -PrepareRollbackApplication { Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState $read } `
                -ValidateRollbackApplication {
                    Confirm-CodeDisclosureMigrationRecovery -State $state -ReadState $read -ValidatePriorApplication {
                        if ($failurePoint -ceq 'prior-probes') { throw 'synthetic prior-payload probe failure' }
                    }
                }
        }
        Assert-True ($db.UpCalls -eq 1 -and $db.TableCount -eq 2) 'Payload recovery replayed/reversed the additive SQL.'
        Assert-True ([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'old') 'Prior payload was not restored.'
        Assert-True ($calls.CandidateStarts -eq 1) 'The candidate probe fault did not execute through the actual swap.'
        Assert-True ($state.RollbackVerified -eq ($failurePoint -ceq 'candidate-probes')) 'Probe/schema failure incorrectly released the hold.'
        Assert-True ($calls.OldStarts -eq $(if ($failurePoint -ceq 'uncertain-schema') { 0 } else { 1 })) 'Uncertain schema allowed prior-payload restart.'
    } finally {
        if (Test-Path -LiteralPath $testRoot) {
            $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).Path
            if (![string]::Equals($resolvedTestRoot, $testRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected recovery-test cleanup path.' }
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}

$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'The incremental updater does not parse.'
$gate = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-IncrementalRollbackHoldRelease' }, $true)
Invoke-Expression $gate.Extent.Text
$validation = @{ HoldCreated=$true; PayloadRollbackVerified=$true; RollbackVerified=$true }
Assert-True (!(Test-IncrementalRollbackHoldRelease -Validation $validation -ApplyCodeDisclosureProofMigration $true -CodeDisclosureRecoveryVerified $false)) 'The updater hold gate ignored uncertain proof-schema recovery.'
Assert-True (Test-IncrementalRollbackHoldRelease -Validation $validation -ApplyCodeDisclosureProofMigration $true -CodeDisclosureRecoveryVerified $true) 'The updater hold gate rejected verified additive recovery.'
$validation.PayloadRollbackVerified=$false
Assert-True (!(Test-IncrementalRollbackHoldRelease -Validation $validation -ApplyCodeDisclosureProofMigration $true -CodeDisclosureRecoveryVerified $true)) 'Schema recovery alone released the updater hold.'

Write-Output 'Code disclosure migration recovery passed: atomic/partial failure, no replay/down, exact history/schema and prior-payload probe gates.'
