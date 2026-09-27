[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [ValidateSet('drain','schema')][string]$Mode,
    [string]$MigrationScript = '')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
$connectionString = [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_HYBRID_DISPOSABLE_SQL')
$connectionString = [regex]::Replace($connectionString, '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=')
$connectionString = [regex]::Replace($connectionString, '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount=')
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
if ($builder.InitialCatalog -notmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$') { throw 'Disposable catalogue required.' }
function Invoke-TestSql {
    param([string]$Sql)
    $connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
    try {
        $connection.Open(); $command = $connection.CreateCommand()
        try { $command.CommandText = $Sql; return $command.ExecuteScalar() } finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}
if ($Mode -eq 'drain') {
    $evidence = @{ Called=$false; Killed=$false }
    Invoke-WithHybridGpuDrain -ConnectionString $connectionString -TimeoutSeconds 2 -StopApplication {
        $evidence.Called = $true
        $lockResult = Invoke-TestSql @'
DECLARE @result int;
EXEC @result=sp_getapplock @Resource=N'FluxKnowledge.GpuScheduler.Admission', @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0;
SELECT @result;
'@
        if ($lockResult -ge 0) { throw 'Admission lock was not held during stop.' }
    }
    if (-not $evidence.Called) { throw 'Available capacity did not reach stop.' }
    try {
        Invoke-WithHybridGpuDrain -ConnectionString $connectionString -TimeoutSeconds 2 -StopApplication {
            param($sessionId)
            [void](Invoke-TestSql "KILL $sessionId")
            $evidence.Killed = $true
        }
        throw 'A killed owning session silently confirmed the stop.'
    }
    catch { if (-not $evidence.Killed -or $_.Exception.Message -eq 'A killed owning session silently confirmed the stop.') { throw } }
    foreach ($mutation in @('UPDATE GpuCapacitySlots SET State=2', 'DELETE FROM GpuCapacitySlots')) {
        [void](Invoke-TestSql $mutation)
        $evidence.Called = $false
        try {
            Invoke-WithHybridGpuDrain -ConnectionString $connectionString -TimeoutSeconds 1 -StopApplication { $evidence.Called=$true }
            throw 'Unsafe capacity reached stop.'
        }
        catch { if ($_.Exception.Message -cne 'hybrid-gpu-drain-timeout-or-uncertain-capacity' -or $evidence.Called) { throw } }
    }
    Write-Output 'PowerShell GPU drain verified against disposable SQL: held admission, session loss, uncertain and missing capacity.'
}
else {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw 'Updater parse failed.' }
    foreach ($name in @('Invoke-GeneratedSqlScript','Get-HybridPreservedInputFingerprint')) {
        $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
        if ($definition.Count -ne 1) { throw 'Updater function missing.' }
        . ([scriptblock]::Create($definition[0].Extent.Text))
    }
    function Get-DeploymentSqlConnectionString { return $connectionString }
    $contract=Get-HybridPassageMigrationContract
    if ((Get-FileHash -LiteralPath $MigrationScript -Algorithm SHA256).Hash -cne $contract.UpSha256) { throw 'Reviewed migration hash mismatch.' }
    Invoke-GeneratedSqlScript -Path $MigrationScript
    $before=Get-HybridPreservedInputFingerprint
    Invoke-GeneratedSqlScript -Path $MigrationScript
    if ((Get-HybridPreservedInputFingerprint) -cne $before) { throw 'Migration replay changed preserved inputs.' }
    if ((Invoke-TestSql 'SELECT TOP(1) MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC') -cne $contract.Target) { throw 'Schema target missing.' }
    Write-Output 'PowerShell reviewed idempotent schema script applied and replayed against disposable SQL; preserved-input query verified.'
}
