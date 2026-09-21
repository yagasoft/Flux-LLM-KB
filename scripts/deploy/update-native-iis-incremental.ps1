[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "I:\FluxKnowledge\App",
    [ValidateRange(10, 300)]
    [int]$ReadinessTimeoutSeconds = 120,
    [switch]$ApplyMigrations,
    [switch]$DeferReadinessForScopedRemediation,
    [switch]$PlanOnly,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$CanonicalLiveRoot = "I:\FluxKnowledge"
$CanonicalDeployRoot = "$CanonicalLiveRoot\App"
$CanonicalRecoveryRoot = "$CanonicalLiveRoot\Recovery"
$IncrementalRecoveryRoot = "$CanonicalRecoveryRoot\IncrementalUpdates"
$ValidationHoldPath = "$CanonicalLiveRoot\Runtime\deployment-validation-hold.json"
$InteractiveHostRoot = "C:\inetpub\FluxKnowledge\outlook-host"
$InteractiveHostTaskName = "FluxKnowledge.OutlookHost"
$SourceDeletionMigrationBaseline = "20260826160702_AddEmptyCatalogueReadiness"
$SourceDeletionMigrationTarget = "20260918121829_AddSourceDeletionOperations"
$SourceDeletionMigrationScriptSha256 = "355F7B8499D0CE333E6FA50142B76C9F7B6CA92B3057A5F1A740C794756ABC90"

function Assert-CanonicalPath {
    param(
        [Parameter(Mandatory)]
        [string]$RequestedPath,
        [Parameter(Mandatory)]
        [string]$ExpectedPath,
        [Parameter(Mandatory)]
        [string]$Message
    )

    try {
        $canonicalRequestedPath = [IO.Path]::GetFullPath($RequestedPath).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
    }
    catch {
        throw $Message
    }

    if (-not [string]::Equals($canonicalRequestedPath, $ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw $Message
    }
}

function Wait-IisAppPoolState {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [ValidateSet("Started", "Stopped")]
        [string]$ExpectedState,
        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ((Get-WebAppPoolState -Name $Name).Value -eq $ExpectedState) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "IIS application pool $Name did not reach $ExpectedState within $TimeoutSeconds seconds."
}

function Test-ApplicationPayload {
    param([Parameter(Mandatory)][string]$Path)

    foreach ($requiredFile in @("FluxKnowledge.Web.dll", "FluxKnowledge.Web.runtimeconfig.json", "web.config")) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $requiredFile) -PathType Leaf)) {
            throw "The staged application payload is missing $requiredFile."
        }
    }
}

function Assert-ApplicationPayloadReadAccess {
    param([Parameter(Mandatory)][string]$Path)

    $requiredRights = [int][Security.AccessControl.FileSystemRights]::ReadAndExecute
    foreach ($payloadPath in @($Path, (Join-Path $Path "web.config"))) {
        $rules = @((Get-Acl -LiteralPath $payloadPath -ErrorAction Stop).Access | Where-Object {
            $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $_.IdentityReference.Value -ieq "IIS APPPOOL\FluxKnowledge" -and
            (([int]$_.FileSystemRights -band $requiredRights) -eq $requiredRights)
        })
        if ($rules.Count -lt 1) {
            throw "The activated application payload does not grant IIS APPPOOL\FluxKnowledge read and execute access: $payloadPath"
        }
    }
}

function Invoke-CandidatePayloadActivation {
    param(
        [Parameter(Mandatory)]
        [string]$CandidateRoot,
        [Parameter(Mandatory)]
        [string]$ApplicationRoot
    )

    New-Item -ItemType Directory -Path $ApplicationRoot -ErrorAction Stop | Out-Null
    Assert-NotReparsePoint `
        -Path $ApplicationRoot `
        -Message "The activated application payload root cannot be a reparse point."
    $robocopyOutput = @(& robocopy $CandidateRoot $ApplicationRoot /E /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NFL /NDL /NP 2>&1)
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -gt 7) {
        throw "Copying the staged application payload into the live root failed with robocopy exit code $robocopyExitCode."
    }
    Test-ApplicationPayload -Path $ApplicationRoot
    Assert-ApplicationPayloadReadAccess -Path $ApplicationRoot
}

function Test-InteractiveHostPayload {
    param([Parameter(Mandatory)][string]$Path)

    foreach ($requiredFile in @("FluxKnowledge.OutlookHost.exe", "FluxKnowledge.OutlookHost.runtimeconfig.json", "run-outlook-host.ps1")) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $requiredFile) -PathType Leaf)) {
            throw "The staged interactive-host payload is missing $requiredFile."
        }
    }
}

function Publish-InteractiveHostCandidate {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$CandidateRoot
    )

    $project = Join-Path $SourceRoot "src\FluxKnowledge.OutlookHost\FluxKnowledge.OutlookHost.csproj"
    & dotnet publish $project -c Release --no-restore --nologo -o $CandidateRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing the interactive-host candidate failed."
    }
    Copy-Item -LiteralPath (Join-Path $SourceRoot "scripts\deploy\run-outlook-host.ps1") `
        -Destination (Join-Path $CandidateRoot "run-outlook-host.ps1") -Force
    Test-InteractiveHostPayload -Path $CandidateRoot
}

function Wait-InteractiveHostStopped {
    param(
        [Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        if ($task.State -ne 'Running' -and
            @(Get-Process -Name "FluxKnowledge.OutlookHost" -ErrorAction SilentlyContinue).Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The interactive host did not stop within $TimeoutSeconds seconds."
}

function Copy-InteractiveHostPayload {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $output = @(& robocopy $Source $Destination /MIR /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NFL /NDL /NP 2>&1)
    if ($LASTEXITCODE -gt 7) {
        throw "Copying the interactive-host payload failed with robocopy exit code $LASTEXITCODE."
    }
    Test-InteractiveHostPayload -Path $Destination
}

function Restore-InteractiveHostPayload {
    param(
        [Parameter(Mandatory)][string]$PreviousRoot,
        [Parameter(Mandatory)][string]$LiveRoot
    )

    Copy-InteractiveHostPayload -Source $PreviousRoot -Destination $LiveRoot
}

function Invoke-RequiredLoopbackProbes {
    param(
        [Parameter(Mandatory)]
        [string]$Origin,
        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    foreach ($path in @("/health/live", "/health/ready", "/api/index-health")) {
        $response = Invoke-FixedLoopbackProbe -Uri "$Origin$path" -TimeoutSeconds $TimeoutSeconds
        $response.Dispose()
    }
}

function Invoke-ScopedReadinessRemediationProbes {
    param(
        [Parameter(Mandatory)]
        [string]$Origin,
        [Parameter(Mandatory)]
        [int]$TimeoutSeconds
    )

    foreach ($path in @("/health/live", "/api/index-health")) {
        $response = Invoke-FixedLoopbackProbe -Uri "$Origin$path" -TimeoutSeconds $TimeoutSeconds
        $response.Dispose()
    }

    $readinessWasUnavailable = $false
    try {
        $response = Invoke-FixedLoopbackProbe -Uri "$Origin/health/ready" -TimeoutSeconds $TimeoutSeconds
        $response.Dispose()
    }
    catch {
        if ($_.Exception.Message -ceq "The fixed-loopback endpoint returned HTTP 503; exact HTTP 200 is required.") {
            $readinessWasUnavailable = $true
        }
        else {
            throw
        }
    }
    if (-not $readinessWasUnavailable) {
        throw "Scoped readiness remediation requires readiness to return exact HTTP 503."
    }
}

function New-DeploymentValidationHold {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ReleaseId
    )

    $runtimeRoot = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $runtimeRoot -PathType Container)) {
        throw "The deployment-validation runtime root is missing."
    }
    Assert-NotReparsePoint -Path $runtimeRoot -Message "The deployment-validation runtime root cannot be a reparse point."
    $payload = [Text.Encoding]::UTF8.GetBytes(($ReleaseId | ConvertTo-Json -Compress))
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    }
    catch [IO.IOException] {
        Assert-NotReparsePoint `
            -Path $Path `
            -Message "The deployment-validation hold cannot be a reparse point."
        $expected = $ReleaseId | ConvertTo-Json -Compress
        $actual = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
        if ([string]::Equals($actual, $expected, [StringComparison]::Ordinal)) {
            return $false
        }
        throw "A deployment-validation hold already exists; inspect and remove it through the recovery procedure before retrying."
    }
    try {
        $stream.Write($payload, 0, $payload.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
    return $true
}

function Remove-DeploymentValidationHold {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ReleaseId
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }
    $expected = $ReleaseId | ConvertTo-Json -Compress
    $actual = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8)
    if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) {
        throw "The deployment-validation hold is not owned by this release."
    }
    Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
}

function ConvertTo-DeploymentValidationConnectionString {
    param([Parameter(Mandatory)][string]$ConnectionString)

    $normalised = [regex]::Replace(
        $ConnectionString,
        '(?i)(^|;)\s*Trust Server Certificate\s*=',
        '$1TrustServerCertificate=')
    return [regex]::Replace(
        $normalised,
        '(?i)(^|;)\s*Connect Retry Count\s*=',
        '$1ConnectRetryCount=')
}

function Get-DeploymentSqlConnectionString {
    $configurationPath = "$CanonicalLiveRoot\Config\appsettings.Production.json"
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "The production configuration required for SQL deployment validation is missing."
    }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $connectionString = $configuration.ConnectionStrings.FluxKnowledge
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "The production connection string required for SQL deployment validation is missing."
    }
    return ConvertTo-DeploymentValidationConnectionString -ConnectionString $connectionString
}

function Get-AppliedMigrationIds {
    $connection = [System.Data.SqlClient.SqlConnection]::new((Get-DeploymentSqlConnectionString))
    try {
        $connection.Open()
        $permission = $connection.CreateCommand()
        try {
            $permission.CommandText = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER');"
            if ($permission.ExecuteScalar() -ne 1) {
                throw "The production SQL principal lacks ALTER permission required for the reviewed source-deletion migration."
            }
        }
        finally {
            $permission.Dispose()
        }
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = "SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory] ORDER BY [MigrationId];"
            $reader = $command.ExecuteReader()
            try {
                $ids = [System.Collections.Generic.List[string]]::new()
                while ($reader.Read()) { $ids.Add($reader.GetString(0)) }
                return @($ids)
            }
            finally { $reader.Dispose() }
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Assert-SourceDeletionMigrationBaseline {
    param([Parameter(Mandatory)][string[]]$AppliedMigrationIds)

    if ($AppliedMigrationIds.Count -eq 0 -or $AppliedMigrationIds[-1] -cne $SourceDeletionMigrationBaseline -or
        $AppliedMigrationIds -contains $SourceDeletionMigrationTarget) {
        throw "The production migration history is not at the reviewed source-deletion baseline."
    }
}

function New-SourceDeletionMigrationScript {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][ValidateSet("up", "down")][string]$Direction,
        [Parameter(Mandatory)][string]$OutputPath
    )

    $project = Join-Path $SourceRoot "src\FluxKnowledge.Infrastructure.SqlServer\FluxKnowledge.Infrastructure.SqlServer.csproj"
    $startup = Join-Path $SourceRoot "src\FluxKnowledge.Web\FluxKnowledge.Web.csproj"
    $from = if ($Direction -ceq "up") { $SourceDeletionMigrationBaseline } else { $SourceDeletionMigrationTarget }
    $to = if ($Direction -ceq "up") { $SourceDeletionMigrationTarget } else { $SourceDeletionMigrationBaseline }
    & dotnet ef migrations script $from $to --configuration Release --project $project --startup-project $startup --no-build --output $OutputPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Generating the reviewed source-deletion migration script failed."
    }
    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
    if ($Direction -ceq "up" -and $hash -cne $SourceDeletionMigrationScriptSha256) {
        throw "The generated source-deletion migration script does not match the reviewed SHA-256."
    }
    return [pscustomobject]@{ From = $from; To = $to; Path = $OutputPath; Sha256 = $hash }
}

function Invoke-GeneratedSqlScript {
    param([Parameter(Mandatory)][string]$Path)

    $script = Get-Content -LiteralPath $Path -Raw
    $connection = [System.Data.SqlClient.SqlConnection]::new((Get-DeploymentSqlConnectionString))
    try {
        $connection.Open()
        foreach ($batch in [regex]::Split($script, "(?im)^\s*GO\s*(?:--.*)?$|(?=^\s*ALTER\s+TRIGGER\b)|(?<=END;)(?=\s*(?:INSERT\s+INTO\s+\[__EFMigrationsHistory\]|COMMIT;))") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
            $command = $connection.CreateCommand()
            try {
                $command.CommandTimeout = 120
                $command.CommandText = $batch
                [void]$command.ExecuteNonQuery()
            }
            finally { $command.Dispose() }
        }
    }
    finally { $connection.Dispose() }
}

function Assert-SourceDeletionMigrationRollbackSafe {
    $connection = [System.Data.SqlClient.SqlConnection]::new((Get-DeploymentSqlConnectionString))
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = @"
SELECT CONVERT(int, CASE WHEN
    NOT EXISTS (SELECT 1 FROM [dbo].[SourceDeletionOperations])
    AND NOT EXISTS (SELECT 1 FROM [dbo].[SourceDeletionCleanupItems])
    AND NOT EXISTS (SELECT 1 FROM [dbo].[IndexGenerations] WHERE [RetiredAtUtc] IS NOT NULL)
THEN 1 ELSE 0 END);
"@
            if ($command.ExecuteScalar() -ne 1) {
                throw "The source-deletion migration cannot be reversed after lifecycle state has been created."
            }
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Get-RetainedPipelineStateBaseline {
    $configurationPath = "$CanonicalLiveRoot\Config\appsettings.Production.json"
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "The production configuration required for read-only deployment validation is missing."
    }
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    $connectionString = $configuration.ConnectionStrings.FluxKnowledge
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "The production connection string required for read-only deployment validation is missing."
    }
    $connectionStringBuilder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new(
        (ConvertTo-DeploymentValidationConnectionString -ConnectionString $connectionString))
    $connection = [System.Data.SqlClient.SqlConnection]::new($connectionStringBuilder.ConnectionString)
    try {
        $connection.Open()
        $tables = @("SourceActivities", "SourceProcessorBranches", "SourceProcessorAttempts", "PipelineRecords", "Jobs", "OutboxMessages")
        $baseline = [ordered]@{}
        foreach ($table in $tables) {
            $command = $connection.CreateCommand()
            try {
                $command.CommandText = @"
SET NOCOUNT ON;
SELECT COUNT_BIG(1) AS [RowCount],
    CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max),
        COALESCE((SELECT * FROM [$table] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]'))), 2) AS [Fingerprint]
FROM [$table];
"@
                $reader = $command.ExecuteReader()
                try {
                    if (-not $reader.Read()) {
                        throw "The read-only deployment-validation query returned no result for $table."
                    }
                    $baseline[$table] = [pscustomobject]@{
                        RowCount = $reader.GetInt64(0)
                        Fingerprint = $reader.GetString(1)
                    }
                }
                finally {
                    $reader.Dispose()
                }
            }
            finally {
                $command.Dispose()
            }
        }
        return $baseline
    }
    finally {
        $connection.Dispose()
    }
}

function Assert-RetainedPipelineStateUnchanged {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Baseline,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Current
    )

    foreach ($table in $Baseline.Keys) {
        $before = $Baseline[$table]
        $after = $Current[$table]
        if ($null -eq $after -or $before.RowCount -ne $after.RowCount -or
            -not [string]::Equals($before.Fingerprint, $after.Fingerprint, [StringComparison]::Ordinal)) {
            throw "Candidate validation changed retained or pipeline state in $table."
        }
    }
}

function Assert-NotReparsePoint {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Message
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw $Message
    }
}

function Assert-IncrementalIisPreflight {
    $site = Get-Website -Name $SiteName -ErrorAction Stop
    Assert-CanonicalPath `
        -RequestedPath $site.physicalPath `
        -ExpectedPath $CanonicalDeployRoot `
        -Message "The fixed FluxKnowledge IIS site is not bound to the canonical I:\FluxKnowledge\App root."
    if ($site.applicationPool -cne "FluxKnowledge") {
        throw "The fixed FluxKnowledge IIS site is not assigned to its canonical application pool."
    }
    if ($site.State -ne "Started") {
        throw "The fixed FluxKnowledge IIS site must be started before an incremental update."
    }
    if ((Get-WebAppPoolState -Name $SiteName).Value -ne "Started") {
        throw "The fixed FluxKnowledge IIS application pool must be started before an incremental update."
    }
    $binding = @(Get-WebBinding -Name $SiteName -Protocol "http" | Where-Object { $_.bindingInformation -ceq "127.0.0.1:5137:" })
    if ($binding.Count -ne 1) {
        throw "The fixed FluxKnowledge IIS site must have exactly one http/127.0.0.1:5137 binding."
    }
    if (-not (Test-Path -LiteralPath $CanonicalDeployRoot -PathType Container)) {
        throw "The canonical application payload root is missing."
    }
    if (-not (Test-Path -LiteralPath $CanonicalRecoveryRoot -PathType Container)) {
        throw "The canonical recovery root is missing."
    }
    Assert-NotReparsePoint `
        -Path $CanonicalDeployRoot `
        -Message "The canonical application payload root cannot be a reparse point."
    Assert-NotReparsePoint `
        -Path $CanonicalRecoveryRoot `
        -Message "The canonical recovery root cannot be a reparse point."
    Test-ApplicationPayload -Path $CanonicalDeployRoot
}

Assert-CanonicalPath `
    -RequestedPath $DeployRoot `
    -ExpectedPath $CanonicalDeployRoot `
    -Message "Incremental IIS deployment requires the canonical I:\FluxKnowledge\App root."
if ($SiteName -cne "FluxKnowledge") {
    throw "Incremental IIS deployment is restricted to the fixed FluxKnowledge IIS site."
}
if ($PlanOnly -and $Apply) {
    throw "-PlanOnly cannot be combined with -Apply."
}
if ($DeferReadinessForScopedRemediation -and $ApplyMigrations) {
    throw "-DeferReadinessForScopedRemediation cannot be combined with -ApplyMigrations."
}

. (Join-Path $PSScriptRoot "loopback-deployment-safety.ps1")
Import-Module (Join-Path $PSScriptRoot "incremental-iis-payload-swap.psm1") -Force -ErrorAction Stop
$loopbackOrigin = Get-FixedLoopbackOrigin -SiteUrl $SiteUrl
if ($loopbackOrigin.Origin -cne "http://127.0.0.1:5137") {
    throw "Incremental IIS deployment requires the fixed http://127.0.0.1:5137 origin."
}

if ($PlanOnly) {
    $migrationPlan = $null
    if ($ApplyMigrations) {
        if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
            $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        }
        $SourceRoot = (Resolve-Path -LiteralPath $SourceRoot -ErrorAction Stop).Path
        $migrationPath = Join-Path $SourceRoot ("src\FluxKnowledge.Infrastructure.SqlServer\Persistence\Migrations\{0}.cs" -f $SourceDeletionMigrationTarget)
        if (-not (Test-Path -LiteralPath $migrationPath -PathType Leaf)) {
            throw "The reviewed source-deletion migration is missing from SourceRoot."
        }
        $history = Get-AppliedMigrationIds
        Assert-SourceDeletionMigrationBaseline -AppliedMigrationIds $history
        $migrationPlan = [ordered]@{
            baseline = $SourceDeletionMigrationBaseline
            target = $SourceDeletionMigrationTarget
            current_history = @($history)
            migration_file_sha256 = (Get-FileHash -LiteralPath $migrationPath -Algorithm SHA256).Hash
            generated_script_sha256 = $SourceDeletionMigrationScriptSha256
            required_permission = "DATABASE ALTER"
            rollback = "before validation-hold release only; only when no lifecycle or retired-generation state exists"
        }
    }
    [ordered]@{
        mode = "plan-only"
        site_name = "FluxKnowledge"
        site_url = "http://127.0.0.1:5137"
        application_root = $CanonicalDeployRoot
        interactive_host_root = $InteractiveHostRoot
        interactive_host_task = $InteractiveHostTaskName
        interactive_host_activation = "next ordinary scheduled run; never triggered by deployment"
        recovery_root = $CanonicalRecoveryRoot
        migrations = [bool]$ApplyMigrations
        migration_plan = $migrationPlan
        clean_slate = $false
        preserved = @("Config", "Data", "Runtime", "Recovery", "CodexPlugin")
        payload_acl = "inherit-from-live-root"
        rollback = "automatic-application-and-interactive-host-payload-restore"
        deployment_validation_hold = $true
        candidate_validation = if ($DeferReadinessForScopedRemediation) {
            "held-live-and-index-health probes, exact-ready-503 and unchanged-retained-pipeline-state"
        }
        else {
            "held-loopback-probes-and-unchanged-retained-pipeline-state"
        }
        readiness_remediation = if ($DeferReadinessForScopedRemediation) {
            "requires exact readiness HTTP 503; post-activation readiness remains pending"
        }
        else {
            $null
        }
    } | ConvertTo-Json -Depth 3
    exit 0
}
if (-not $Apply) {
    throw "Incremental IIS deployment requires -Apply after reviewing the plan."
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot -ErrorAction Stop).Path
$webProject = Join-Path $SourceRoot "src\FluxKnowledge.Web\FluxKnowledge.Web.csproj"
if (-not (Test-Path -LiteralPath $webProject -PathType Leaf)) {
    throw "The FluxKnowledge Web project is missing from SourceRoot."
}
$interactiveHostProject = Join-Path $SourceRoot "src\FluxKnowledge.OutlookHost\FluxKnowledge.OutlookHost.csproj"
if (-not (Test-Path -LiteralPath $interactiveHostProject -PathType Leaf)) {
    throw "The FluxKnowledge interactive-host project is missing from SourceRoot."
}

$sourceStatus = (& git -C $SourceRoot status --porcelain 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "SourceRoot must be a readable Git checkout."
}
if (-not [string]::IsNullOrWhiteSpace($sourceStatus)) {
    throw "SourceRoot has uncommitted changes; incremental IIS deployment requires an immutable committed payload."
}
$migrationPlan = $null
if ($ApplyMigrations) {
    $migrationPath = Join-Path $SourceRoot ("src\FluxKnowledge.Infrastructure.SqlServer\Persistence\Migrations\{0}.cs" -f $SourceDeletionMigrationTarget)
    if (-not (Test-Path -LiteralPath $migrationPath -PathType Leaf)) {
        throw "The reviewed source-deletion migration is missing from SourceRoot."
    }
    $history = Get-AppliedMigrationIds
    Assert-SourceDeletionMigrationBaseline -AppliedMigrationIds $history
    $migrationPlan = [ordered]@{ Applied = $false; RollbackVerified = $true; Up = $null; Down = $null }
}
$commit = (& git -C $SourceRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch "^[0-9a-f]{40}$") {
    throw "SourceRoot does not resolve to an immutable Git commit."
}

Import-Module WebAdministration -ErrorAction Stop
$mutex = [Threading.Mutex]::new($false, "Global\FluxKnowledge.IncrementalIisUpdate.v1")
$leaseAcquired = $false
$deploymentValidation = $null
$releaseId = $null
$interactiveHostMutationStarted = $false
$interactiveHostTaskWasEnabled = $false
$interactiveHostPreviousRoot = $null
try {
    try {
        $leaseAcquired = $mutex.WaitOne([TimeSpan]::FromMinutes(1))
    }
    catch [Threading.AbandonedMutexException] {
        $leaseAcquired = $true
    }
    if (-not $leaseAcquired) {
        throw "Another incremental IIS deployment is already in progress."
    }

    Assert-IncrementalIisPreflight
    if (-not (Test-Path -LiteralPath $IncrementalRecoveryRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $IncrementalRecoveryRoot -Force | Out-Null
    }
    Assert-NotReparsePoint `
        -Path $IncrementalRecoveryRoot `
        -Message "The incremental recovery root cannot be a reparse point."

    $releaseId = "{0}-{1}" -f [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ"), $commit.Substring(0, 12)
    $releaseRoot = Join-Path $IncrementalRecoveryRoot $releaseId
    if (Test-Path -LiteralPath $releaseRoot) {
        throw "The incremental recovery release directory already exists."
    }
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    Assert-NotReparsePoint `
        -Path $releaseRoot `
        -Message "The incremental recovery release directory cannot be a reparse point."
    $candidateRoot = Join-Path $releaseRoot "candidate"
    $previousRoot = Join-Path $releaseRoot "previous"
    $failedRoot = Join-Path $releaseRoot "failed"
    $interactiveHostCandidateRoot = Join-Path $releaseRoot "candidate-interactive-host"
    $interactiveHostPreviousRoot = Join-Path $releaseRoot "previous-interactive-host"
    $deploymentValidation = @{ HoldCreated = $false; Baseline = $null; MigrationsApplied = $false; RollbackVerified = $true }

    & dotnet publish $webProject -c Release --no-restore --nologo -o $candidateRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing the incremental IIS candidate failed."
    }
    Test-ApplicationPayload -Path $candidateRoot
    Publish-InteractiveHostCandidate -SourceRoot $SourceRoot -CandidateRoot $interactiveHostCandidateRoot
    if (-not (Test-Path -LiteralPath $InteractiveHostRoot -PathType Container)) {
        throw "The installed interactive-host payload root is missing."
    }
    Assert-NotReparsePoint -Path $InteractiveHostRoot -Message "The installed interactive-host payload root cannot be a reparse point."
    Test-InteractiveHostPayload -Path $InteractiveHostRoot
    $interactiveHostTask = Get-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction Stop
    $interactiveHostTaskWasEnabled = [bool]$interactiveHostTask.Settings.Enabled
    if ($interactiveHostTaskWasEnabled) {
        Disable-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction Stop | Out-Null
    }
    Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds $ReadinessTimeoutSeconds
    Copy-InteractiveHostPayload -Source $InteractiveHostRoot -Destination $interactiveHostPreviousRoot
    $interactiveHostMutationStarted = $true
    Copy-InteractiveHostPayload -Source $interactiveHostCandidateRoot -Destination $InteractiveHostRoot
    if ($ApplyMigrations) {
        $migrationPlan.Up = New-SourceDeletionMigrationScript -SourceRoot $SourceRoot -Direction up -OutputPath (Join-Path $releaseRoot "source-deletion-up.sql")
        $migrationPlan.Down = New-SourceDeletionMigrationScript -SourceRoot $SourceRoot -Direction down -OutputPath (Join-Path $releaseRoot "source-deletion-down.sql")
    }

    $manifest = [ordered]@{
        commit = $commit
        staged_at_utc = [DateTime]::UtcNow.ToString("O")
        application_root = $CanonicalDeployRoot
        migrations = [bool]$ApplyMigrations
        clean_slate = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $releaseRoot "manifest.json"), $manifest, [Text.UTF8Encoding]::new($false))

    $swap = Invoke-IncrementalApplicationPayloadSwap `
        -ApplicationRoot $CanonicalDeployRoot `
        -CandidateRoot $candidateRoot `
        -PreviousRoot $previousRoot `
        -FailedRoot $failedRoot `
        -ActivateCandidate {
            Invoke-CandidatePayloadActivation -CandidateRoot $candidateRoot -ApplicationRoot $CanonicalDeployRoot
        } `
        -StopApplication {
            Stop-WebAppPool -Name $SiteName
            Wait-IisAppPoolState -Name $SiteName -ExpectedState "Stopped" -TimeoutSeconds $ReadinessTimeoutSeconds
            [void](New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId)
            if (-not $deploymentValidation.HoldCreated) {
                $deploymentValidation.HoldCreated = $true
            }
            if ($null -eq $deploymentValidation.Baseline) {
                $deploymentValidation.Baseline = Get-RetainedPipelineStateBaseline
            }
            if ($ApplyMigrations -and -not $deploymentValidation.MigrationsApplied) {
                Assert-SourceDeletionMigrationBaseline -AppliedMigrationIds (Get-AppliedMigrationIds)
                $deploymentValidation.RollbackVerified = $false
                Invoke-GeneratedSqlScript -Path $migrationPlan.Up.Path
                $postMigrationHistory = Get-AppliedMigrationIds
                if ($postMigrationHistory[-1] -cne $SourceDeletionMigrationTarget) {
                    throw "The reviewed source-deletion migration was not recorded as the active schema target."
                }
                $deploymentValidation.MigrationsApplied = $true
            }
        } `
        -StartApplication {
            Start-WebAppPool -Name $SiteName
            Wait-IisAppPoolState -Name $SiteName -ExpectedState "Started" -TimeoutSeconds $ReadinessTimeoutSeconds
        } `
        -ValidateApplication {
            if ($DeferReadinessForScopedRemediation) {
                Invoke-ScopedReadinessRemediationProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            else {
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            Assert-RetainedPipelineStateUnchanged `
                -Baseline $deploymentValidation.Baseline `
                -Current (Get-RetainedPipelineStateBaseline)
        } `
        -ValidateRollbackApplication {
            if ($DeferReadinessForScopedRemediation) {
                Invoke-ScopedReadinessRemediationProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            else {
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            }
        } `
        -PrepareRollbackApplication {
            if ($ApplyMigrations -and $deploymentValidation.MigrationsApplied) {
                Assert-SourceDeletionMigrationRollbackSafe
                Invoke-GeneratedSqlScript -Path $migrationPlan.Down.Path
                Assert-SourceDeletionMigrationBaseline -AppliedMigrationIds (Get-AppliedMigrationIds)
                $deploymentValidation.MigrationsApplied = $false
                $deploymentValidation.RollbackVerified = $true
            }
        }

    Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId
    $deploymentValidation.HoldCreated = $false
    if ($DeferReadinessForScopedRemediation) {
        Invoke-ScopedReadinessRemediationProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
    }
    else {
        Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
    }
    if ($interactiveHostTaskWasEnabled) {
        Enable-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction Stop | Out-Null
    }

    [ordered]@{
        ok = $true
        mode = "applied"
        commit = $commit
        release_root = $releaseRoot
        rollback_payload = $swap.PreviousPayload
        migrations = [bool]$ApplyMigrations
        migration = if ($ApplyMigrations) { [ordered]@{ target = $SourceDeletionMigrationTarget; script_sha256 = $migrationPlan.Up.Sha256 } } else { $null }
        clean_slate = $false
        deployment_validation_hold = if ($DeferReadinessForScopedRemediation) {
            "released-after-unchanged-state-validation; readiness remediation pending"
        }
        else {
            "released-after-unchanged-state-validation"
        }
        interactive_host = [ordered]@{
            root = $InteractiveHostRoot
            task = $InteractiveHostTaskName
            activation = "next ordinary scheduled run; not triggered by deployment"
            rollback_payload = $interactiveHostPreviousRoot
        }
        readiness_remediation = if ($DeferReadinessForScopedRemediation) {
            "pending scoped source remediation"
        }
        else {
            $null
        }
    } | ConvertTo-Json -Depth 3
}
catch {
    if ($interactiveHostMutationStarted) {
        if ($null -eq $interactiveHostPreviousRoot -or
            -not (Test-Path -LiteralPath $interactiveHostPreviousRoot -PathType Container)) {
            throw "Deployment failed after the interactive-host payload was mutated, and no rollback payload is available. The scheduled task remains disabled."
        }
        Test-InteractiveHostPayload -Path $interactiveHostPreviousRoot
        Restore-InteractiveHostPayload -PreviousRoot $interactiveHostPreviousRoot -LiveRoot $InteractiveHostRoot
        Test-InteractiveHostPayload -Path $InteractiveHostRoot
        throw "Deployment failed after the interactive-host payload was mutated. The prior payload was restored and the scheduled task remains disabled for operator review."
    }
    if ($interactiveHostTaskWasEnabled -and -not $interactiveHostMutationStarted) {
        Enable-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction SilentlyContinue | Out-Null
    }
    throw
}
finally {
    if ($leaseAcquired -and $null -ne $deploymentValidation -and $deploymentValidation.HoldCreated -and
        (-not $ApplyMigrations -or $deploymentValidation.RollbackVerified)) {
        Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId
    }
    if ($leaseAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
