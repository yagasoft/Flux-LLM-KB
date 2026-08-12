[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "C:\inetpub\FluxKnowledge",
    [string]$ValidationRecordPath = "docs\operations\native-windows-phase-5-retained-processors-validation.md",
    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "loopback-deployment-safety.ps1")
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$siteOrigin = (Get-FixedLoopbackOrigin -SiteUrl $SiteUrl).Origin

$planScript = Join-Path $SourceRoot "scripts\deploy\phase-5-deployment-plan.ps1"
$planOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $planScript `
    -SourceRoot $SourceRoot `
    -SiteUrl $siteOrigin `
    -PlanOnly 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "The authoritative Phase 5 deployment plan could not be read."
}
try {
    $plan = $planOutput | ConvertFrom-Json
}
catch {
    throw "The authoritative Phase 5 deployment plan returned invalid JSON."
}
if (-not [bool]$plan.read_only_validation -or [bool]$plan.outlook_host_activation) {
    throw "The authoritative Phase 5 deployment plan lost its read-only or Outlook-disabled boundary."
}
if ($PlanOnly) {
    $plan | ConvertTo-Json -Depth 7
    exit 0
}

$DeployRoot = (Resolve-Path -LiteralPath $DeployRoot).Path
$recordPath = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot $ValidationRecordPath))
$operationsRoot = [System.IO.Path]::GetFullPath((Join-Path $SourceRoot "docs\operations"))
if (-not $recordPath.StartsWith($operationsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The Phase 5 validation record must remain under docs\operations."
}
$settingsPath = Join-Path $DeployRoot "appsettings.Production.json"
if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw "The deployed appsettings.Production.json file is missing."
}
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$connectionString = [string]$settings.ConnectionStrings.FluxKnowledge
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw "The deployed settings do not contain the FluxKnowledge SQL connection."
}
$connectionBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
$server = $connectionBuilder.DataSource.Trim() -replace '^(?i:tcp:)', ''
$serverHost = $server.Split(',')[0].Split('\')[0].Trim()
if ($serverHost -notin @("localhost", ".", "(local)", "127.0.0.1", "::1", "[::1]", "(localdb)") -or
    $connectionBuilder.InitialCatalog -cne "FluxKnowledge" -or
    -not $connectionBuilder.IntegratedSecurity -or $connectionBuilder.UserID -or $connectionBuilder.Password) {
    throw "Phase 5 validation requires the loopback FluxKnowledge catalog with integrated authentication."
}

$connection = [System.Data.SqlClient.SqlConnection]::new($connectionBuilder.ConnectionString)
try {
    $connection.Open()
    foreach ($migrationId in @($plan.phase5_migration_ids)) {
        $migrationCommand = $connection.CreateCommand()
        $migrationCommand.CommandText = "SELECT COUNT(1) FROM [dbo].[__EFMigrationsHistory] WHERE [MigrationId] = @migrationId;"
        [void]$migrationCommand.Parameters.AddWithValue("@migrationId", [string]$migrationId)
        if ([int]$migrationCommand.ExecuteScalar() -ne 1) {
            throw "An expected Phase 5 migration is not present in __EFMigrationsHistory."
        }
    }

    foreach ($requiredTable in @($plan.required_schema_objects)) {
        $tableCommand = $connection.CreateCommand()
        try {
            $tableCommand.CommandText = @'
SELECT COUNT(1)
FROM sys.tables AS [table]
INNER JOIN sys.schemas AS [table_schema]
    ON [table_schema].[schema_id] = [table].[schema_id]
WHERE [table_schema].[name] = N'dbo'
  AND [table].[name] = @tableName;
'@
            [void]$tableCommand.Parameters.AddWithValue("@tableName", [string]$requiredTable)
            if ([int]$tableCommand.ExecuteScalar() -ne 1) {
                throw "The deployed Phase 5 schema is missing required dbo table $requiredTable."
            }
        }
        finally {
            $tableCommand.Dispose()
        }
    }

    foreach ($triggerBinding in @($plan.required_schema_trigger_bindings)) {
        if ([string]$triggerBinding.parent_schema -cne "dbo") {
            throw "The Phase 5 deployment plan has an unsafe trigger schema binding."
        }
        $triggerCommand = $connection.CreateCommand()
        try {
            $triggerCommand.CommandText = @'
SELECT COUNT(1)
FROM sys.triggers AS [trigger]
INNER JOIN sys.tables AS [parent_table]
    ON [parent_table].[object_id] = [trigger].[parent_id]
INNER JOIN sys.schemas AS [parent_schema]
    ON [parent_schema].[schema_id] = [parent_table].[schema_id]
WHERE [trigger].[name] = @triggerName
  AND [parent_schema].[name] = N'dbo'
  AND [parent_table].[name] = @parentTable
  AND [trigger].[is_disabled] = 0;
'@
            [void]$triggerCommand.Parameters.AddWithValue("@triggerName", [string]$triggerBinding.name)
            [void]$triggerCommand.Parameters.AddWithValue("@parentTable", [string]$triggerBinding.parent_table)
            if ([int]$triggerCommand.ExecuteScalar() -ne 1) {
                throw "The deployed Phase 5 schema is missing an enabled fencing trigger on its exact dbo parent table."
            }
        }
        finally {
            $triggerCommand.Dispose()
        }
    }
}
finally {
    $connection.Dispose()
}

$probe = New-FixedLoopbackProbeClient
$handler = $probe.Handler
$client = $probe.Client
$origin = $siteOrigin
$directStatuses = [ordered]@{}
$forwardedStatuses = [ordered]@{}
$noMatchToken = "phase5-validation-$([Guid]::NewGuid().ToString('N'))"
try {
    foreach ($endpointTemplate in @($plan.direct_get_endpoints)) {
        $endpoint = ([string]$endpointTemplate).Replace("{no-match-token}", $noMatchToken)
        $uri = [Uri]("{0}{1}" -f $origin, $endpoint)
        $response = Invoke-FixedLoopbackProbe -Uri $uri.AbsoluteUri
        try {
            if ([int]$response.StatusCode -ne 200) {
                throw "A required direct-loopback GET endpoint did not return 200."
            }
            if ([string]$endpointTemplate -eq "/api/local/retained-csharp-code?query={no-match-token}") {
                try {
                    $searchProjection = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                }
                catch {
                    throw "The retained C# no-match probe returned invalid JSON."
                }
                if (@($searchProjection.results).Count -ne 0 -or $null -ne $searchProjection.nextCursor) {
                    throw "The retained C# no-match probe unexpectedly returned a result or continuation."
                }
            }
            $directStatuses[[string]$endpointTemplate] = [int]$response.StatusCode
        }
        finally {
            $response.Dispose()
        }

        $headerStatuses = [ordered]@{}
        foreach ($header in @($plan.forwarded_proxy_headers)) {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $uri)
            try {
                [void]$request.Headers.TryAddWithoutValidation([string]$header, "203.0.113.7")
                $blockedResponse = $client.SendAsync($request).GetAwaiter().GetResult()
                try {
                    if ([int]$blockedResponse.StatusCode -ne 403) {
                        throw "A forwarded/proxy loopback request was not rejected with 403."
                    }
                    $headerStatuses[[string]$header] = [int]$blockedResponse.StatusCode
                }
                finally {
                    $blockedResponse.Dispose()
                }
            }
            finally {
                $request.Dispose()
            }
        }
        $forwardedStatuses[[string]$endpointTemplate] = $headerStatuses
    }
}
finally {
    $client.Dispose()
    $handler.Dispose()
}

$validatedAt = [DateTime]::UtcNow.ToString("o")
$recordDirectory = Split-Path -Parent $recordPath
New-Item -ItemType Directory -Force -Path $recordDirectory | Out-Null
@(
    "# Native Windows Phase 5 retained-processors validation",
    "",
    "- Validated at (UTC): $validatedAt",
    "- Required migrations: $(@($plan.phase5_migration_ids) -join '; ')",
    "- Schema contract: required tables and fencing triggers present",
    "- Direct loopback GET probes: all returned 200",
    "- Forwarded/proxy GET probes: all returned 403",
    "- Outlook host activation: false",
    "- Validation operations: SQL metadata SELECT and HTTP GET only"
) | Set-Content -LiteralPath $recordPath -Encoding utf8

[ordered]@{
    ok = $true
    validation_record = $ValidationRecordPath
    validated_at_utc = $validatedAt
    migration_ids = @($plan.phase5_migration_ids)
    schema_contract = "passed"
    direct_get_status_codes = $directStatuses
    forwarded_proxy_status_codes = $forwardedStatuses
    outlook_host_activation = $false
} | ConvertTo-Json -Depth 8
