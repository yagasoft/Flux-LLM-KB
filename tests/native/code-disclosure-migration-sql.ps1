[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$SqlPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-code-disclosure-migration.psm1') -Force
$connectionText = [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_CODE_PROOF_DISPOSABLE_SQL')
$connectionText = [regex]::Replace($connectionText, '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=')
$connectionText = [regex]::Replace($connectionText, '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount=')
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionText)
if ($connectionBuilder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$') {
    throw 'SQL migration tests require a generated disposable catalogue.'
}
$connectionBuilder.Pooling = $false
$connectionText = $connectionBuilder.ConnectionString
$contract = Get-CodeDisclosureMigrationContract
if ((Get-FileHash -LiteralPath $SqlPath -Algorithm SHA256).Hash -cne $contract.UpSha256) { throw 'Generated migration SQL differs from the reviewed hash.' }

function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed 'Expected SQL refusal was not observed.'
}
function Invoke-TestSql([string]$Sql, [switch]$FailAfterHeader) {
    $connection = [System.Data.SqlClient.SqlConnection]::new($connectionText)
    try {
        $connection.Open()
        if ($FailAfterHeader) {
            $secondCreate = $Sql.IndexOf('CREATE TABLE [CanonicalCodeDisclosureSpans]', [StringComparison]::Ordinal)
            $secondGuard = $Sql.LastIndexOf('IF NOT EXISTS (', $secondCreate, [StringComparison]::Ordinal)
            Assert-True ($secondCreate -gt 0 -and $secondGuard -gt 0) 'Reviewed SQL has no header/span transaction boundary.'
            $command = $connection.CreateCommand()
            try {
                $command.CommandText = $Sql.Substring(0, $secondGuard) + "`nSELECT CASE WHEN OBJECT_ID(N'dbo.CanonicalCodeDisclosureProofs', N'U') IS NOT NULL THEN 1 ELSE 0 END;"
                Assert-True ([int]$command.ExecuteScalar() -eq 1) 'Fault injection did not execute header creation inside the open SQL transaction.'
            } finally { $command.Dispose() }
            throw 'synthetic connection failure after header creation before spans/history/commit'
        }
        foreach ($batch in [regex]::Split($Sql, '(?im)^\s*GO\s*(?:--.*)?$') | Where-Object { ![string]::IsNullOrWhiteSpace($_) }) {
            $command = $connection.CreateCommand()
            try { $command.CommandText = $batch; [void]$command.ExecuteNonQuery() } finally { $command.Dispose() }
        }
    } finally { $connection.Dispose() }
}

$sql = Get-Content -LiteralPath $SqlPath -Raw
$before = Get-CodeDisclosureDatabaseState -ConnectionString $connectionText
Write-Output "Disposable baseline: last=$($before.History[-1]); tables=$($before.TableCount); schema=$($before.SchemaSha256)."
$state = New-CodeDisclosureMigrationState -DatabaseState $before
$read = { Get-CodeDisclosureDatabaseState -ConnectionString $connectionText }
Expect-Failure { Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState $read -RunUp { Invoke-TestSql $sql -FailAfterHeader } }
$rolledBack = & $read
Write-Output "After early fault: last=$($rolledBack.History[-1]); tables=$($rolledBack.TableCount); history_match=$(($rolledBack.History -join ',') -ceq ($before.History -join ',')); schema=$($rolledBack.SchemaSha256)."
Assert-True ($rolledBack.TableCount -eq 0 -and ($rolledBack.History -join ',') -ceq ($before.History -join ',')) 'Early SQL failure did not atomically preserve the prior schema/history.'
Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState $read
Confirm-CodeDisclosureMigrationRecovery -State $state -ReadState $read -ValidatePriorApplication { }

$state = New-CodeDisclosureMigrationState -DatabaseState $rolledBack
try { Invoke-CodeDisclosureMigrationAttempt -State $state -ReadState $read -RunUp { Invoke-TestSql $sql } }
catch { throw "Reviewed-schema validation failed; observed fingerprint $((& $read).SchemaSha256): $($_.Exception.Message)" }
$after = & $read
Assert-True $after.SchemaValid "Complete schema fingerprint is $($after.SchemaSha256); expected $($contract.SchemaSha256)."
Invoke-TestSql $sql
$repeat = & $read
Assert-True ($repeat.SchemaValid -and ($repeat.History -join ',') -ceq ($after.History -join ',')) 'Repeated idempotent SQL changed the contract/history.'

Invoke-TestSql 'ALTER TABLE [dbo].[CanonicalCodeDisclosureProofs] NOCHECK CONSTRAINT [CK_CodeDisclosureProof_State];'
$invalid = & $read
Assert-True (!$invalid.SchemaValid -and $invalid.TableCount -eq 2) 'A disabled constraint was mistaken for a complete schema.'
Expect-Failure { Assert-CodeDisclosureMigrationBaseline $invalid }
Expect-Failure { Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState $read }
Assert-True (!$state.RollbackVerified) 'Changed schema released the recovery hold.'
Invoke-TestSql 'ALTER TABLE [dbo].[CanonicalCodeDisclosureProofs] WITH CHECK CHECK CONSTRAINT [CK_CodeDisclosureProof_State];'
Assert-True ((& $read).SchemaValid) 'Restored trusted constraint did not restore the reviewed contract.'
Invoke-TestSql 'DROP TABLE [dbo].[CanonicalCodeDisclosureSpans];'
Expect-Failure { Confirm-CodeDisclosureSchemaRecovery -State $state -ReadState $read }
Assert-True (!$state.SchemaRecoveryVerified -and !$state.RollbackVerified) 'A partial schema permitted hold release.'
Write-Output 'Disposable SQL verified: early batch rollback, complete contract, idempotence, disabled checks and partial-schema refusal.'
