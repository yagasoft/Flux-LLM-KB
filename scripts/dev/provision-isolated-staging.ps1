[CmdletBinding(DefaultParameterSetName = 'Plan')]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$StageName,
    [Parameter(Mandatory)][ValidatePattern('^FluxKnowledge_Staging_[A-Za-z0-9_]{1,64}$')][string]$Catalog,
    [Parameter(Mandatory)][ValidatePattern('^fluxknowledge_staging_[a-z0-9_]{1,64}$')][string]$Login,
    [ValidateRange(1024,65535)][int]$Port = 5140,
    [Parameter(ParameterSetName = 'Plan')][switch]$PlanOnly,
    [Parameter(Mandatory, ParameterSetName = 'Apply')][switch]$Apply
)

$ErrorActionPreference = 'Stop'
$stageRoot = Join-Path 'E:\Codex Workspaces\Staging\FluxKnowledge' $StageName
$dataRoot = Join-Path $stageRoot 'Data\Sql\Data'
$logRoot = Join-Path $stageRoot 'Data\Sql\Log'
$dataFile = Join-Path $dataRoot 'FluxKnowledge.mdf'
$logFile = Join-Path $logRoot 'FluxKnowledge_log.ldf'
$settingsFile = Join-Path $stageRoot 'Config\staging.json'
if ($Port -eq 5137) { throw 'Staging cannot use the production port.' }

function Assert-NoLinkedPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $entry = Get-Item -LiteralPath $cursor -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging path contains a reparse point.' }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

Assert-NoLinkedPath $stageRoot
Add-Type -AssemblyName System.Data
function New-StagingRuntimeConnectionString([string]$RuntimePassword) {
    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new()
    # Use canonical SQL keywords: PowerShell treats this builder as a dictionary.
    $builder['Data Source'] = 'localhost'; $builder['Initial Catalog'] = $Catalog
    $builder['User ID'] = $Login; $builder['Password'] = $RuntimePassword
    $builder['Encrypt'] = $true; $builder['TrustServerCertificate'] = $true
    return $builder.ConnectionString
}
[void](New-StagingRuntimeConnectionString 'synthetic-plan-check')
$plan = [ordered]@{
    stage_root = $stageRoot; catalog = $Catalog; login = $Login
    origin = "http://127.0.0.1:$Port"; data_file = $dataFile; log_file = $logFile
    runtime_permissions = @('CONNECT to own staging database', 'db_datareader', 'db_datawriter', 'EXECUTE in own staging database')
    server_permissions = @(); production_actions = @()
    provisioner = 'Current Windows identity, localhost/master; create-only exact staging targets'
    migration = 'Separate StagingHost --migrate operation, only the verified staging catalogue'
    rollback = 'Stop only the exact staging process; preserve database, files and receipt'
}
if (-not $Apply) { $plan | ConvertTo-Json -Depth 5; return }

if (Test-Path -LiteralPath $stageRoot) { throw 'Create-only staging provisioning refuses an existing root. Inspect the receipt instead of overwriting.' }
$admin = [System.Data.SqlClient.SqlConnection]::new('Server=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;Application Name=FluxKnowledge.StagingProvisioner')
try {
    $admin.Open()
    $preflight = $admin.CreateCommand()
    $preflight.CommandText = "SELECT CASE WHEN DB_ID(@catalog) IS NULL AND SUSER_ID(@login) IS NULL AND SERVERPROPERTY('IsFullTextInstalled')=1 AND SERVERPROPERTY('IsIntegratedSecurityOnly')=0 THEN 1 ELSE 0 END;"
    [void]$preflight.Parameters.AddWithValue('@catalog', $Catalog)
    [void]$preflight.Parameters.AddWithValue('@login', $Login)
    if ($preflight.ExecuteScalar() -ne 1) { throw 'Staging target already exists or SQL prerequisites are absent. No changes applied.' }
    $preflight.Dispose()

    [void](New-Item -ItemType Directory -Path $stageRoot)
    Assert-NoLinkedPath $stageRoot
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($sid in @($currentSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $stageRoot -AclObject $acl
    foreach ($relative in @('App','Config','Data\Sql\Data','Data\Sql\Log','Data\Index','Data\Retained','Runtime\Spool','Runtime\Temp','Runtime\Logs','Recovery','Sources')) {
        $directory = Join-Path $stageRoot $relative
        Assert-NoLinkedPath $directory
        [void](New-Item -ItemType Directory -Path $directory -Force)
    }
    foreach ($directory in @($dataRoot, $logRoot)) {
        $sqlAcl = Get-Acl -LiteralPath $directory
        $sqlAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\MSSQLSERVER', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $directory -AclObject $sqlAcl
    }

    $password = 'FkStage!' + [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    @{SqlConnectionString=(New-StagingRuntimeConnectionString $password); Origin="http://127.0.0.1:$Port"} | ConvertTo-Json |
        Set-Content -LiteralPath $settingsFile -Encoding utf8
    $receipt = Join-Path $stageRoot 'Config\provisioning-receipt.json'
    $plan['settings_file'] = $settingsFile
    $plan['state'] = 'prepared-before-sql-create'
    $plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt -Encoding utf8
    $command = $admin.CreateCommand()
    # Identifiers are restricted by parameter validation. Password and all string values
    # are quoted by SQL itself; no credential is written to command output or evidence.
    $command.CommandText = @'
DECLARE @sql nvarchar(max) =
    N'CREATE DATABASE ' + QUOTENAME(@catalog) +
    N' ON PRIMARY (NAME=N''FluxKnowledge'', FILENAME=' + QUOTENAME(@dataFile, '''') + N', SIZE=64MB, FILEGROWTH=64MB)' +
    N' LOG ON (NAME=N''FluxKnowledge_log'', FILENAME=' + QUOTENAME(@logFile, '''') + N', SIZE=32MB, FILEGROWTH=32MB);';
EXEC sys.sp_executesql @sql;
SET @sql = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD=' + QUOTENAME(@password, '''') +
    N', DEFAULT_DATABASE=' + QUOTENAME(@catalog) + N', CHECK_POLICY=ON, CHECK_EXPIRATION=OFF;';
EXEC sys.sp_executesql @sql;
SET @sql = N'USE ' + QUOTENAME(@catalog) + N'; CREATE USER ' + QUOTENAME(@login) + N' FOR LOGIN ' + QUOTENAME(@login) + N';' +
    N' ALTER ROLE [db_datareader] ADD MEMBER ' + QUOTENAME(@login) + N'; ALTER ROLE [db_datawriter] ADD MEMBER ' + QUOTENAME(@login) + N';' +
    N' GRANT EXECUTE TO ' + QUOTENAME(@login) + N';';
EXEC sys.sp_executesql @sql;
'@
    foreach ($value in @{catalog=$Catalog; login=$Login; dataFile=$dataFile; logFile=$logFile; password=$password}.GetEnumerator()) {
        [void]$command.Parameters.AddWithValue('@' + $value.Key, $value.Value)
    }
    $command.CommandTimeout = 30
    [void]$command.ExecuteNonQuery()
    $command.Dispose()
    $password = $null
    $plan['provisioned_utc'] = [DateTimeOffset]::UtcNow.ToString('o')
    $plan['state'] = 'provisioned'
    $plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt -Encoding utf8
    [ordered]@{status='provisioned'; receipt=$receipt; settings_file=$settingsFile; origin=$plan.origin; catalog=$Catalog} | ConvertTo-Json
}
catch {
    if ($receipt -and (Test-Path -LiteralPath $receipt)) {
        $plan['state'] = 'failed-preserve-for-inspection'
        $plan['failure_type'] = $_.Exception.GetType().FullName
        $plan | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $receipt -Encoding utf8
    }
    throw
}
finally {
    $admin.Dispose()
}
