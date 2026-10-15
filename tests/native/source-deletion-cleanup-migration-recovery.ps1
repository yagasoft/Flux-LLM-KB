[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (!$SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
$contract = Get-SourceDeletionCleanupMigrationContract
function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed 'Expected refusal was not observed.'
}
function New-TestDatabase {
    @{ History=@('earlier', $contract.Baseline); Identity='owned-database'; ColumnCount=0; CheckCount=0;
        SchemaValid=$false; RunnableCount=0; LegacySha256=('A'*64); ProgressIsInitial=$true; CanAlter=$true }
}
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('flux-source-migration-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    foreach ($failurePoint in @('receipt', 'before-sql', 'partial-schema', 'after-commit', 'candidate-probes', 'none')) {
        $release = Join-Path $testRoot $failurePoint
        New-Item -ItemType Directory -Path $release | Out-Null
        $db = New-TestDatabase
        $state = New-SourceDeletionCleanupMigrationState -DatabaseState ([pscustomobject]$db) -ReceiptDirectory $release `
            -Binding @{ ReleaseId=$failurePoint; Commit=('b'*40); WebPayloadSha256=('C'*64); CompanionPayloadSha256=('D'*64); SqlSha256=$contract.UpSha256 }
        $calls = @{ Up=0; OldStarts=0; CandidateStarts=0; Release=0 }
        $read = { [pscustomobject]$db }.GetNewClosure()
        $up = {
            Assert-True (Test-Path -LiteralPath (Join-Path $release 'source-deletion-migration-attempt.json')) 'SQL ran without a durable attempt receipt.'
            $receipt = Get-Content -LiteralPath (Join-Path $release 'source-deletion-migration-attempt.json') -Raw | ConvertFrom-Json
            Assert-True ($receipt.Binding.DatabaseIdentity -ceq $db.Identity -and $receipt.Binding.SqlSha256 -ceq $contract.UpSha256) 'Attempt receipt lost its database/SQL binding.'
            $calls.Up++
            if ($failurePoint -ceq 'before-sql') { throw 'SQL refused' }
            $db.ColumnCount=1
            if ($failurePoint -ceq 'partial-schema') { throw 'partial schema' }
            $db.ColumnCount=5; $db.CheckCount=2; $db.SchemaValid=$true; $db.History += $contract.Target
            if ($failurePoint -ceq 'after-commit') { throw 'lost commit acknowledgement' }
        }.GetNewClosure()
        if ($failurePoint -ceq 'receipt') {
            [IO.File]::WriteAllText((Join-Path $release 'source-deletion-migration-attempt.json'), 'existing receipt')
        }
        if ($failurePoint -ceq 'candidate-probes') {
            $live=Join-Path $release 'live'; $candidate=Join-Path $release 'candidate'
            New-Item -ItemType Directory -Path $live,$candidate | Out-Null
            [IO.File]::WriteAllText((Join-Path $live 'version'), 'old')
            [IO.File]::WriteAllText((Join-Path $candidate 'version'), 'new')
            Expect-Failure {
                Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $live -CandidateRoot $candidate `
                    -PreviousRoot (Join-Path $release 'previous') -FailedRoot (Join-Path $release 'failed') `
                    -RestartPreviousApplication:$false `
                    -ActivateCandidate { Move-Item -LiteralPath $candidate -Destination $live } `
                    -StopApplication { Invoke-SourceDeletionCleanupMigrationAttempt -State $state -ReadState $read -RunUp $up } `
                    -StartApplication {
                        if ([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'old') { $calls.OldStarts++ } else { $calls.CandidateStarts++ }
                    } -ValidateApplication { throw 'candidate probes failed' } `
                    -ValidateRollbackApplication { Assert-True ([IO.File]::ReadAllText((Join-Path $live 'version')) -ceq 'old') 'Prior bytes were not restored.' }
            }
            Assert-True ($calls.CandidateStarts -eq 1 -and $calls.OldStarts -eq 0) 'Payload recovery restarted the predecessor.'
        } elseif ($failurePoint -ceq 'none') {
            Invoke-SourceDeletionCleanupMigrationAttempt -State $state -ReadState $read -RunUp $up
        } else {
            Expect-Failure { Invoke-SourceDeletionCleanupMigrationAttempt -State $state -ReadState $read -RunUp $up }
        }
        Invoke-SourceDeletionCleanupMigrationAttempt -State $state -ReadState $read -RunUp $up
        Assert-True ($calls.Up -eq $(if ($failurePoint -ceq 'receipt') { 0 } else { 1 })) 'StopApplication replayed SQL or ignored a failed receipt.'
        Expect-Failure {
            New-SourceDeletionCleanupMigrationState -DatabaseState ([pscustomobject](New-TestDatabase)) -ReceiptDirectory $release `
                -Binding @{ ReleaseId=$failurePoint; Commit=('b'*40); WebPayloadSha256=('C'*64); CompanionPayloadSha256=('D'*64); SqlSha256=$contract.UpSha256 }
        }
        if ($failurePoint -ceq 'none') {
            Assert-SourceDeletionCleanupActivation -State $state -ReadState $read
            foreach ($drift in @('Identity','LegacySha256','RunnableCount','SchemaValid','ProgressIsInitial','History')) {
                $old=$db[$drift]
                $db[$drift]=switch ($drift) { 'RunnableCount' { 1 } 'SchemaValid' { $false } 'ProgressIsInitial' { $false } 'History' { @('unexpected') } default { 'changed' } }
                Expect-Failure { Assert-SourceDeletionCleanupActivation -State $state -ReadState $read }
                $db[$drift]=$old
            }
            Expect-Failure {
                Invoke-SourceDeletionCleanupHoldRelease -State $state -ReleaseHold {
                    Assert-True (Test-Path -LiteralPath (Join-Path $release 'source-deletion-hold-release-intent.json')) 'Hold deletion preceded its durable intent.'
                    $calls.Release++; throw 'ambiguous hold deletion'
                }
            }
            Assert-True $state.ForwardRecoveryRequired 'Ambiguous hold deletion allowed predecessor rollback.'
            Expect-Failure { Invoke-SourceDeletionCleanupHoldRelease -State $state -ReleaseHold { $calls.Release++ } }
            Assert-True ($calls.Release -eq 1) 'Hold release was replayed after ambiguous completion.'
        }
    }
    foreach ($drift in @('ColumnCount','CheckCount','RunnableCount','History')) {
        $db=New-TestDatabase
        $db[$drift]=if ($drift -ceq 'History') { @('unexpected') } else { 1 }
        Expect-Failure { Assert-SourceDeletionCleanupMigrationBaseline ([pscustomobject]$db) }
    }
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
    Assert-True ($errors.Count -eq 0) 'The updater does not parse.'
    $gate=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-IncrementalRollbackHoldRelease' },$true)
    Invoke-Expression $gate.Extent.Text
    Assert-True (!(Test-IncrementalRollbackHoldRelease -Validation @{ HoldCreated=$true; PayloadRollbackVerified=$true; RollbackVerified=$true } `
        -ApplyResumableSourceDeletionCleanupMigration $true -InteractiveHostRollbackVerified $true)) 'Generic rollback released the source migration hold.'
} finally {
    $resolved=(Resolve-Path -LiteralPath $testRoot).Path
    if (![string]::Equals($resolved,$testRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output 'Source deletion migration recovery passed: durable attempt, no replay, held predecessor, exact activation and ambiguous release gates.'
