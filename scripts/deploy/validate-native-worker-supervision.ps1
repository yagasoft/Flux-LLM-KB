[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "C:\inetpub\FluxKnowledge",
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{14}_AddNativeWorkerSupervision$')]
    [string]$ExpectedMigrationId,
    [string]$ValidationRecordPath = "docs\operations\native-windows-phase-2-native-worker-supervision-validation.md"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$DeployRoot = (Resolve-Path -LiteralPath $DeployRoot).Path
$recordPath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $ValidationRecordPath))
$operationsRoot = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot "docs\operations"))
if (-not $recordPath.StartsWith($operationsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The validation record must remain under docs\\operations."
}

$siteUri = [Uri]$SiteUrl
if (-not $siteUri.IsLoopback) {
    throw "Native worker validation is restricted to a loopback site URL."
}

$settingsPath = Join-Path $DeployRoot "appsettings.Production.json"
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "The deployed appsettings.Production.json file is missing."
}
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if ($null -ne $settings.NativeWorker -and [bool]$settings.NativeWorker.Enabled) {
    throw "Native worker supervision must remain disabled after deployment."
}

$migrationPath = Join-Path $SourceRoot ("src\FluxKnowledge.Infrastructure.SqlServer\Persistence\Migrations\{0}.cs" -f $ExpectedMigrationId)
if (-not (Test-Path -LiteralPath $migrationPath -PathType Leaf)) {
    throw "The expected native-worker supervision migration source is missing."
}

$connectionString = [string]$settings.ConnectionStrings.FluxKnowledge
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "The deployed settings do not contain the FluxKnowledge SQL connection."
}
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
$server = $connectionBuilder.DataSource.Trim() -replace '^(?i:tcp:)', ''
$serverHost = $server.Split(',')[0].Split('\')[0].Trim()
if ($serverHost -notin @("localhost", ".", "(local)", "127.0.0.1", "::1", "[::1]", "(localdb)") -or
    $connectionBuilder.InitialCatalog -ne "FluxKnowledge" -or
    -not $connectionBuilder.IntegratedSecurity -or $connectionBuilder.UserID -or $connectionBuilder.Password) {
    throw "Native worker validation requires the loopback FluxKnowledge catalog with integrated authentication."
}
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
try {
    $connection.Open()
    $migrationCommand = $connection.CreateCommand()
    $migrationCommand.CommandText = "SELECT COUNT(1) FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = @migrationId;"
    [void]$migrationCommand.Parameters.AddWithValue("@migrationId", $ExpectedMigrationId)
    if ([int]$migrationCommand.ExecuteScalar() -ne 1) {
        throw "The expected native-worker supervision migration is not present in __EFMigrationsHistory."
    }

    $schemaCommand = $connection.CreateCommand()
    $schemaCommand.CommandText = @'
IF OBJECT_ID(N'[dbo].[NativeWorkerInstances]', N'U') IS NULL
   OR OBJECT_ID(N'[dbo].[NativeWorkerLifecycleEvidence]', N'U') IS NULL
    THROW 50000, 'Native worker supervision tables are missing.', 1;
IF EXISTS (
    SELECT 1
    FROM sys.columns AS [column]
    INNER JOIN sys.tables AS [table] ON [table].[object_id] = [column].[object_id]
    WHERE [table].[name] IN (N'NativeWorkerInstances', N'NativeWorkerLifecycleEvidence')
      AND [column].[name] IN (N'PipeName', N'SessionNonce', N'CommandLine', N'RawDiagnostic', N'SourceText', N'ModelIdentity', N'Settings', N'Environment'))
    THROW 50000, 'Native worker supervision tables contain prohibited private data columns.', 1;
'@
    [void]$schemaCommand.ExecuteNonQuery()
} finally {
    $connection.Dispose()
}

foreach ($path in @("/health/live", "/health/ready", "/api/gpu-status")) {
    $response = Invoke-WebRequest -UseBasicParsing -Uri ("{0}{1}" -f $siteUri.GetLeftPart([System.UriPartial]::Authority), $path) -TimeoutSec 30
    if ($response.StatusCode -ne 200) {
        throw "The loopback endpoint $path did not return 200."
    }
}

$recordDirectory = Split-Path -Parent $recordPath
New-Item -ItemType Directory -Force -Path $recordDirectory | Out-Null
$validatedAt = [DateTime]::UtcNow.ToString("o")
@(
    "# Native worker supervision validation",
    "",
    "- Validated at (UTC): $validatedAt",
    "- Site: loopback",
    "- Required migration: $ExpectedMigrationId",
    "- Loopback endpoints: /health/live, /health/ready, /api/gpu-status returned 200",
    "- Native worker supervision: disabled in deployed configuration",
    "- Private worker tables: present; prohibited private-data columns absent"
) | Set-Content -LiteralPath $recordPath -Encoding utf8

[ordered]@{
    ok = $true
    validation_record = $ValidationRecordPath
    migration = $ExpectedMigrationId
    native_worker_enabled = $false
    endpoint_status = "200"
} | ConvertTo-Json -Depth 3
