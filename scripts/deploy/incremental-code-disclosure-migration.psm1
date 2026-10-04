Set-StrictMode -Version Latest

function Get-CodeDisclosureMigrationContract {
    [pscustomobject]@{
        Baseline = '20260927202655_AddCorpusRebuildSupersession'
        Target = '20261003220411_AddCanonicalCodeDisclosureProof'
        UpSha256 = '59CA9DCB89E5A8B272BA2934D25792034C3C0447FCE534C4874F86086B33E741'
        SchemaSha256 = 'C269D0803C5074D17E19F91E70A35AE6D999240971C01139746612ED2C977CEB'
    }
}

function Get-CodeDisclosureDatabaseState {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $transaction = $connection.BeginTransaction([Data.IsolationLevel]::Serializable)
        try {
            $command = $connection.CreateCommand()
            try {
                $command.Transaction = $transaction
                $command.CommandTimeout = 120
                $command.CommandText = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER');"
                $canAlter = [int]$command.ExecuteScalar() -eq 1
                $command.CommandText = 'SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory] WITH (HOLDLOCK) ORDER BY [MigrationId];'
                $reader = $command.ExecuteReader()
                $history = [Collections.Generic.List[string]]::new()
                try { while ($reader.Read()) { $history.Add($reader.GetString(0)) } } finally { $reader.Dispose() }
                $command.CommandText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'code-disclosure-schema-contract.sql') -Raw
                $json = [string]$command.ExecuteScalar()
                if ([string]::IsNullOrWhiteSpace($json)) { throw 'SQL returned no code disclosure schema contract.' }
                $schemaHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json)))
                $tableCount = @($json | ConvertFrom-Json).Count
                $transaction.Commit()
                return [pscustomobject]@{
                    History = @($history); TableCount = $tableCount; SchemaSha256 = $schemaHash; CanAlter = $canAlter
                    SchemaValid = ($tableCount -eq 2 -and $schemaHash -ceq (Get-CodeDisclosureMigrationContract).SchemaSha256)
                }
            } finally { $command.Dispose() }
        } finally { $transaction.Dispose() }
    } finally { $connection.Dispose() }
}

function Test-CodeDisclosureHistory([string[]]$Actual, [string[]]$Expected) {
    return [string]::Equals(($Actual -join "`n"), ($Expected -join "`n"), [StringComparison]::Ordinal)
}

function Assert-CodeDisclosureMigrationBaseline {
    param([Parameter(Mandatory)]$DatabaseState)
    $contract = Get-CodeDisclosureMigrationContract
    $history = @($DatabaseState.History)
    $old = $history.Count -gt 0 -and $history[-1] -ceq $contract.Baseline -and
        $DatabaseState.TableCount -eq 0 -and $history -cnotcontains $contract.Target
    $new = $history.Count -gt 1 -and $history[-2] -ceq $contract.Baseline -and
        $history[-1] -ceq $contract.Target -and $DatabaseState.TableCount -eq 2 -and $DatabaseState.SchemaValid
    if (!$old -and !$new) { throw 'Code disclosure migration requires the exact reviewed prior or complete additive schema/history.' }
}

function New-CodeDisclosureMigrationState {
    param([Parameter(Mandatory)]$DatabaseState)
    Assert-CodeDisclosureMigrationBaseline $DatabaseState
    $original = @($DatabaseState.History)
    $target = (Get-CodeDisclosureMigrationContract).Target
    $alreadyApplied = $original[-1] -ceq $target
    @{
        OriginalHistory = $original
        ExpectedHistory = $(if ($alreadyApplied) { $original } else { $original + $target })
        AlreadyApplied = $alreadyApplied; Attempted = $false; Applied = $false
        SchemaRecoveryVerified = $false; RollbackVerified = $false
    }
}

function Invoke-CodeDisclosureMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$RunUp)
    # StopApplication also runs during payload recovery; never replay an attempt.
    if ($State.Attempted) { return }
    $before = & $ReadState
    Assert-CodeDisclosureMigrationBaseline $before
    if (!(Test-CodeDisclosureHistory $before.History $State.OriginalHistory)) { throw 'Code disclosure preflight history changed.' }
    $State.Attempted = $true; $State.RollbackVerified = $false; $State.SchemaRecoveryVerified = $false
    if (!$State.AlreadyApplied) { & $RunUp }
    $after = & $ReadState
    if (!(Test-CodeDisclosureHistory $after.History $State.ExpectedHistory) -or
        $after.TableCount -ne 2 -or !$after.SchemaValid) { throw 'Code disclosure migration did not establish the complete reviewed schema/history.' }
    $State.Applied = $true
}

function Confirm-CodeDisclosureSchemaRecovery {
    param([Parameter(Mandatory)][hashtable]$State, [Parameter(Mandatory)][scriptblock]$ReadState)
    $State.RollbackVerified = $false; $State.SchemaRecoveryVerified = $false
    $current = & $ReadState
    $old = !$State.AlreadyApplied -and (Test-CodeDisclosureHistory $current.History $State.OriginalHistory) -and $current.TableCount -eq 0
    $new = (Test-CodeDisclosureHistory $current.History $State.ExpectedHistory) -and $current.TableCount -eq 2 -and $current.SchemaValid
    if (!$old -and !$new) { throw 'Code disclosure schema recovery is uncertain; retain the validation hold and stopped application.' }
    $State.SchemaRecoveryVerified = $true
}

function Confirm-CodeDisclosureMigrationRecovery {
    param([Parameter(Mandatory)][hashtable]$State, [Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$ValidatePriorApplication)
    $State.RollbackVerified = $false
    if (!$State.SchemaRecoveryVerified) { throw 'Verify additive schema compatibility before starting the prior payload.' }
    Confirm-CodeDisclosureSchemaRecovery -State $State -ReadState $ReadState
    & $ValidatePriorApplication
    $State.RollbackVerified = $true
}

Export-ModuleMember -Function Get-CodeDisclosureMigrationContract, Get-CodeDisclosureDatabaseState,
    Assert-CodeDisclosureMigrationBaseline, New-CodeDisclosureMigrationState, Invoke-CodeDisclosureMigrationAttempt,
    Confirm-CodeDisclosureSchemaRecovery, Confirm-CodeDisclosureMigrationRecovery
