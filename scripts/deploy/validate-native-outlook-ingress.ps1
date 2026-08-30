[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "I:\FluxKnowledge\App",
    [string]$ExpectedMigrationId = "",
    [string]$BaselineMigrationId = "",
    [string]$ValidationRecordPath = "docs\operations\native-windows-phase-4-outlook-ingress-validation.md",
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"
$CanonicalDeployRoot = "I:\FluxKnowledge\App"
$deploySegments = @($DeployRoot -split '[\\/]')
if ($deploySegments -contains '.' -or $deploySegments -contains '..') {
    throw "Native deployment validation requires the canonical I:\FluxKnowledge\App root without traversal."
}
try {
    $canonicalRequestedDeployRoot = [System.IO.Path]::GetFullPath($DeployRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}
catch {
    throw "Native deployment validation requires the canonical I:\FluxKnowledge\App root."
}
if (-not [string]::Equals($canonicalRequestedDeployRoot, $CanonicalDeployRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Native deployment validation requires the canonical I:\FluxKnowledge\App root."
}
. (Join-Path $PSScriptRoot "loopback-deployment-safety.ps1")
$siteOrigin = (Get-FixedLoopbackOrigin -SiteUrl $SiteUrl).Origin

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path

function Get-NativeOutlookIngressMigrationContract {
    param([string]$SourceRoot)

    $deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-windows.ps1"
    if (-not (Test-Path -LiteralPath $deploymentScript -PathType Leaf)) {
        throw "The authoritative native deployment plan is missing."
    }

    $planOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $deploymentScript `
        -SourceRoot $SourceRoot `
        -PlanOnly 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "The authoritative native deployment plan could not be read."
    }

    try {
        $plan = $planOutput | ConvertFrom-Json
        $migrationIds = @($plan.native_outlook_ingress_migration_ids)
    }
    catch {
        throw "The authoritative native deployment plan returned an invalid Outlook migration contract."
    }

    if ($migrationIds.Count -eq 0 -or
        $plan.native_outlook_ingress_baseline_migration -cne $migrationIds[0] -or
        $plan.native_outlook_ingress_migration_target -cne $migrationIds[-1] -or
        $plan.native_outlook_ingress_post_deploy_validator -cne (Split-Path -Leaf $PSCommandPath) -or
        -not [bool]$plan.keep_outlook_host_disabled -or
        [bool]$plan.outlook_host_activation) {
        throw "The authoritative native deployment plan returned an inconsistent Outlook migration contract."
    }

    foreach ($migrationId in $migrationIds) {
        if ([string]$migrationId -notmatch '^\d{14}_[A-Za-z0-9]+$') {
            throw "The authoritative native deployment plan returned an invalid Outlook migration identifier."
        }
    }
    if ([string]$migrationIds[0] -notmatch '^\d{14}_AddNativeOutlookIngress$') {
        throw "The authoritative native deployment plan returned an invalid Outlook baseline migration."
    }

    return [pscustomobject]@{
        BaselineMigrationId = [string]$migrationIds[0]
        ExpectedMigrationId = [string]$migrationIds[-1]
    }
}

$migrationContract = Get-NativeOutlookIngressMigrationContract -SourceRoot $SourceRoot
if (-not [string]::IsNullOrWhiteSpace($ExpectedMigrationId) -and $ExpectedMigrationId -cne $migrationContract.ExpectedMigrationId) {
    throw "The requested native Outlook migration target does not match the authoritative deployment plan."
}
if (-not [string]::IsNullOrWhiteSpace($BaselineMigrationId) -and $BaselineMigrationId -cne $migrationContract.BaselineMigrationId) {
    throw "The requested native Outlook baseline migration does not match the authoritative deployment plan."
}
$ExpectedMigrationId = $migrationContract.ExpectedMigrationId
$BaselineMigrationId = $migrationContract.BaselineMigrationId

if ($PlanOnly) {
    [ordered]@{
        mode = "plan-only"
        loopback_only = $true
        outlook_enabled = $false
        outlook_host_activation = $false
        effective_configuration_projection = $true
        configuration_projection_starts_host = $false
        native_outlook_ingress_baseline_migration = $BaselineMigrationId
        native_outlook_ingress_migration_target = $ExpectedMigrationId
        checks = @("migration", "loopback-health-readiness-status", "disabled-configuration", "private-schema-policy", "aggregate-counts")
        validation_record_fields = @(
            "started_at_utc",
            "completed_at_utc",
            "loopback_status_codes",
            "migration_ids",
            "outlook_enabled",
            "aggregate_counts",
            "private_schema_policy"
        )
    } | ConvertTo-Json -Depth 5
    exit 0
}

$DeployRoot = (Resolve-Path -LiteralPath $DeployRoot).Path
$recordPath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $ValidationRecordPath))
$operationsRoot = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot "docs\operations"))
if (-not $recordPath.StartsWith($operationsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The validation record must remain under docs\operations."
}

$productionSettingsPath = Join-Path $DeployRoot "appsettings.Production.json"
if (-not (Test-Path -LiteralPath (Join-Path $DeployRoot "appsettings.json") -PathType Leaf) -or
    -not (Test-Path -LiteralPath $productionSettingsPath -PathType Leaf)) {
    throw "The deployed default and production settings files are required."
}
$productionSettings = Get-Content -LiteralPath $productionSettingsPath -Raw | ConvertFrom-Json
$webAssembly = Join-Path $DeployRoot "FluxKnowledge.Web.dll"
if (-not (Test-Path -LiteralPath $webAssembly -PathType Leaf)) {
    throw "The deployed Web assembly is required for effective configuration projection."
}
$projectionArguments = @("--project-outlook-capture-configuration")
$webConfigPath = Join-Path $DeployRoot "web.config"
$webConfigEnvironment = @()
if (Test-Path -LiteralPath $webConfigPath -PathType Leaf) {
    [xml]$webConfig = Get-Content -LiteralPath $webConfigPath -Raw
    $aspNetCore = $webConfig.configuration.'system.webServer'.aspNetCore
    $deployedArguments = [string]$aspNetCore.arguments
    foreach ($match in [regex]::Matches(
        $deployedArguments,
        '(?i)(?:^|\s)--OutlookCapture:(?<key>Enabled|HintDebounceSeconds|RecoveryCadenceSeconds|StaleLeaseSeconds)(?:=(?<equal>[^\s"]+)|\s+(?<separate>[^\s"]+))(?=\s|$)')) {
        $value = if ($match.Groups['equal'].Success) { $match.Groups['equal'].Value } else { $match.Groups['separate'].Value }
        $projectionArguments += "--OutlookCapture:$($match.Groups['key'].Value)=$value"
    }
    foreach ($environmentNode in @($aspNetCore.environmentVariables.environmentVariable)) {
        if ($null -ne $environmentNode -and -not [string]::IsNullOrWhiteSpace([string]$environmentNode.name)) {
            $webConfigEnvironment += [pscustomobject]@{
                Name = [string]$environmentNode.name
                Value = [string]$environmentNode.value
            }
        }
    }
}
$previousEnvironment = @()
try {
    foreach ($environmentOverride in $webConfigEnvironment) {
        $previousEnvironment += [pscustomobject]@{
            Name = $environmentOverride.Name
            Value = [Environment]::GetEnvironmentVariable($environmentOverride.Name, "Process")
        }
        [Environment]::SetEnvironmentVariable(
            $environmentOverride.Name,
            $environmentOverride.Value,
            "Process")
    }
    Push-Location $DeployRoot
    try {
        $projectionOutput = & dotnet $webAssembly @projectionArguments 2>$null | Out-String
        $projectionExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($previous in $previousEnvironment) {
        [Environment]::SetEnvironmentVariable($previous.Name, $previous.Value, "Process")
    }
}
if ($projectionExitCode -ne 0) {
    throw "The deployed Web application could not produce its sanitised effective Outlook configuration projection."
}
try {
    $effectiveProjection = $projectionOutput | ConvertFrom-Json
    $projectionFields = @($effectiveProjection.PSObject.Properties.Name)
    if (($projectionFields -join '|') -ne 'outlook_enabled') {
        throw "Unexpected projection shape."
    }
    $outlookEnabled = [bool]$effectiveProjection.outlook_enabled
}
catch {
    throw "The deployed Web application returned an invalid sanitised Outlook configuration projection."
}
if ($outlookEnabled) {
    throw "Native Outlook recovery must remain disabled after deployment."
}

foreach ($migrationId in @($BaselineMigrationId, $ExpectedMigrationId)) {
    $migrationPath = Join-Path $SourceRoot ("src\FluxKnowledge.Infrastructure.SqlServer\Persistence\Migrations\{0}.cs" -f $migrationId)
    if (-not (Test-Path -LiteralPath $migrationPath -PathType Leaf)) {
        throw "An expected native Outlook migration source is missing."
    }
}

$connectionString = [string]$productionSettings.ConnectionStrings.FluxKnowledge
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "The deployed settings do not contain the FluxKnowledge SQL connection."
}
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
$server = $connectionBuilder.DataSource.Trim() -replace '^(?i:tcp:)', ''
$serverHost = $server.Split(',')[0].Split('\')[0].Trim()
if ($serverHost -notin @("localhost", ".", "(local)", "127.0.0.1", "::1", "[::1]", "(localdb)") -or
    $connectionBuilder.InitialCatalog -ne "FluxKnowledge" -or
    -not $connectionBuilder.IntegratedSecurity -or $connectionBuilder.UserID -or $connectionBuilder.Password) {
    throw "Native Outlook validation requires the loopback FluxKnowledge catalog with integrated authentication."
}

$startedAt = [DateTime]::UtcNow
$counts = [ordered]@{ profiles = 0; folders = 0; exports = 0; pending_catch_ups = 0 }
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
try {
    $connection.Open()
    foreach ($migrationId in @($BaselineMigrationId, $ExpectedMigrationId)) {
        $migrationCommand = $connection.CreateCommand()
        $migrationCommand.CommandText = "SELECT COUNT(1) FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = @migrationId;"
        [void]$migrationCommand.Parameters.AddWithValue("@migrationId", $migrationId)
        if ([int]$migrationCommand.ExecuteScalar() -ne 1) {
            throw "An expected native Outlook migration is not present in __EFMigrationsHistory."
        }
    }

    $schemaCommand = $connection.CreateCommand()
    $schemaCommand.CommandText = @'
IF OBJECT_ID(N'[dbo].[OutlookCaptureProfiles]', N'U') IS NULL
   OR OBJECT_ID(N'[dbo].[OutlookCaptureFolders]', N'U') IS NULL
   OR OBJECT_ID(N'[dbo].[OutlookCaptureExports]', N'U') IS NULL
   OR OBJECT_ID(N'[dbo].[OutlookCatchUps]', N'U') IS NULL
    THROW 50000, 'Native Outlook tables are missing.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[OutlookCaptureProfiles]') AND name = N'SpoolRoot')
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[OutlookCaptureFolders]') AND name = N'StoreId')
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[OutlookCaptureFolders]') AND name = N'FolderEntryId')
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[OutlookCaptureExports]') AND name = N'EntryId')
   OR NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[OutlookCaptureExports]') AND name = N'RelativeSpoolPath')
    THROW 50000, 'Native Outlook private columns are missing from their access-restricted tables.', 1;
IF EXISTS (
    SELECT 1
    FROM sys.columns AS [column]
    INNER JOIN sys.tables AS [table] ON [table].[object_id] = [column].[object_id]
    WHERE [column].[name] IN (N'SpoolRoot', N'StoreId', N'FolderEntryId', N'EntryId', N'RelativeSpoolPath')
      AND NOT (([table].[name] = N'OutlookCaptureProfiles' AND [column].[name] = N'SpoolRoot')
        OR ([table].[name] = N'OutlookCaptureFolders' AND [column].[name] IN (N'StoreId', N'FolderEntryId'))
        OR ([table].[name] = N'OutlookCaptureExports' AND [column].[name] IN (N'EntryId', N'RelativeSpoolPath'))))
    THROW 50000, 'Native Outlook private columns escaped their access-restricted tables.', 1;
'@
    [void]$schemaCommand.ExecuteNonQuery()

    $countCommand = $connection.CreateCommand()
    $countCommand.CommandText = @'
SELECT
    (SELECT COUNT_BIG(1) FROM [dbo].[OutlookCaptureProfiles]),
    (SELECT COUNT_BIG(1) FROM [dbo].[OutlookCaptureFolders]),
    (SELECT COUNT_BIG(1) FROM [dbo].[OutlookCaptureExports]),
    (SELECT COUNT_BIG(1) FROM [dbo].[OutlookCatchUps] WHERE [State] = 0);
'@
    $reader = $countCommand.ExecuteReader()
    try {
        if (-not $reader.Read()) {
            throw "Native Outlook aggregate counts were unavailable."
        }
        $counts.profiles = [long]$reader.GetInt64(0)
        $counts.folders = [long]$reader.GetInt64(1)
        $counts.exports = [long]$reader.GetInt64(2)
        $counts.pending_catch_ups = [long]$reader.GetInt64(3)
    } finally {
        $reader.Dispose()
    }
} finally {
    $connection.Dispose()
}

$statusCodes = [ordered]@{}
foreach ($path in @("/health/live", "/health/ready", "/api/index-health")) {
    $response = Invoke-FixedLoopbackProbe -Uri ("{0}{1}" -f $siteOrigin, $path) -TimeoutSeconds 30
    try {
        if ([int]$response.StatusCode -ne 200) {
            throw "A required loopback endpoint did not return 200."
        }
        $statusCodes[$path] = [int]$response.StatusCode
    }
    finally {
        $response.Dispose()
    }
}

$completedAt = [DateTime]::UtcNow
$recordDirectory = Split-Path -Parent $recordPath
New-Item -ItemType Directory -Force -Path $recordDirectory | Out-Null
@(
    "# Native Outlook ingress validation",
    "",
    "- Started at (UTC): $($startedAt.ToString('o'))",
    "- Completed at (UTC): $($completedAt.ToString('o'))",
    "- Loopback status codes: live=$($statusCodes['/health/live']); ready=$($statusCodes['/health/ready']); status=$($statusCodes['/api/index-health'])",
    "- Required migrations: $BaselineMigrationId; $ExpectedMigrationId",
    "- Outlook recovery enabled: false",
    "- Aggregate counts: profiles=$($counts.profiles); folders=$($counts.folders); exports=$($counts.exports); pending catch-ups=$($counts.pending_catch_ups)",
    "- Private schema policy: passed"
) | Set-Content -LiteralPath $recordPath -Encoding utf8

[ordered]@{
    ok = $true
    validation_record = $ValidationRecordPath
    started_at_utc = $startedAt.ToString("o")
    completed_at_utc = $completedAt.ToString("o")
    loopback_status_codes = $statusCodes
    migration_ids = @($BaselineMigrationId, $ExpectedMigrationId)
    outlook_enabled = $false
    aggregate_counts = $counts
    private_schema_policy = "passed"
} | ConvertTo-Json -Depth 5
