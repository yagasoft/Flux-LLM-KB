[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "I:\FluxKnowledge\App",
    [ValidateRange(10, 300)]
    [int]$ReadinessTimeoutSeconds = 120,
    [switch]$ApplyMigrations,
    [switch]$ApplyCorpusChunkFullTextMigration,
    [switch]$ApplyCodeDisclosureProofMigration,
    [switch]$ApplyHybridPassageRebuild,
    [string]$ResumeHybridRebuildRelease = '',
    [string]$ReplaceHybridRebuildRelease = '',
    [string]$PatchHybridRebuildRelease = '',
    [string]$ResumeHybridPatchRelease = '',
    [string]$ExpectedPatchCandidateHash = '',
    [string]$ExpectedPatchOperatorHash = '',
    [ValidateRange(30, 3600)][int]$RebuildTimeoutSeconds = 1800,
    [switch]$DeferReadinessForScopedRemediation,
    [switch]$RecoverStoppedPool,
    [switch]$EnableUnattendedDiscovery,
    [switch]$PlanOnly,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$CanonicalLiveRoot = "I:\FluxKnowledge"
$CanonicalDeployRoot = "$CanonicalLiveRoot\App"
$CanonicalRecoveryRoot = "$CanonicalLiveRoot\Recovery"
$IncrementalRecoveryRoot = "$CanonicalRecoveryRoot\IncrementalUpdates"
$ValidationHoldPath = "$CanonicalLiveRoot\Runtime\deployment-validation-hold.json"
$CpuSearchOwnerPath = "$CanonicalLiveRoot\Runtime\bge-cpu-search-owner.lock"
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
        [int]$TimeoutSeconds,
        [switch]$AfterHoldRelease
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
    if (-not $readinessWasUnavailable -and -not $AfterHoldRelease) {
        throw "Scoped readiness remediation requires readiness to return exact HTTP 503."
    }
    return $(if ($readinessWasUnavailable) { 'pending' } else { 'ready' })
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

function Get-CorpusFullTextDatabaseState {
    $history = @(Get-AppliedMigrationIds)
    $connection = [System.Data.SqlClient.SqlConnection]::new((Get-DeploymentSqlConnectionString))
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = @"
SELECT CONVERT(int, SERVERPROPERTY('IsFullTextInstalled')),
 CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name=N'FluxKnowledge') THEN 1 ELSE 0 END,
 CASE WHEN EXISTS (
   SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
   JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
   WHERE i.object_id=OBJECT_ID(N'dbo.TextChunks') AND i.name=N'PK_TextChunks' AND i.is_unique=1
     AND i.is_disabled=0 AND c.name=N'Id' AND c.system_type_id=127 AND c.is_nullable=0
     AND ic.key_ordinal=1 AND (SELECT COUNT(*) FROM sys.index_columns k WHERE k.object_id=i.object_id AND k.index_id=i.index_id)=1
 ) THEN 1 ELSE 0 END,
 CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id=OBJECT_ID(N'dbo.TextChunks')) THEN 1 ELSE 0 END,
 CASE WHEN EXISTS (
   SELECT 1 FROM sys.fulltext_indexes f
   JOIN sys.fulltext_catalogs fc ON fc.fulltext_catalog_id=f.fulltext_catalog_id
   JOIN sys.indexes i ON i.object_id=f.object_id AND i.index_id=f.unique_index_id
   JOIN sys.fulltext_index_columns c ON c.object_id=f.object_id
   WHERE f.object_id=OBJECT_ID(N'dbo.TextChunks') AND fc.name=N'FluxKnowledge' AND i.name=N'PK_TextChunks'
     AND f.is_enabled=1 AND f.change_tracking_state_desc=N'AUTO'
     AND c.column_id=COLUMNPROPERTY(f.object_id,N'Content','ColumnId') AND c.language_id=1033
     AND (SELECT COUNT(*) FROM sys.fulltext_index_columns x WHERE x.object_id=f.object_id)=1
 ) THEN 1 ELSE 0 END;
"@
            $reader = $command.ExecuteReader()
            try {
                if (!$reader.Read()) { throw 'Full-Text preflight returned no state.' }
                return [pscustomobject]@{
                    History=$history; FullTextInstalled=($reader.GetInt32(0) -eq 1)
                    CataloguePresent=($reader.GetInt32(1) -eq 1); KeyPresent=($reader.GetInt32(2) -eq 1)
                    IndexPresent=($reader.GetInt32(3) -eq 1); IndexValid=($reader.GetInt32(4) -eq 1)
                }
            } finally { $reader.Dispose() }
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function New-CorpusFullTextMigrationScript {
    param([string]$SourceRoot, [ValidateSet('up','down')][string]$Direction, [string]$OutputPath)
    $contract = Get-CorpusFullTextMigrationContract
    $from = if ($Direction -ceq 'up') { $contract.Baseline } else { $contract.Target }
    $to = if ($Direction -ceq 'up') { $contract.Target } else { $contract.Baseline }
    $project = Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj'
    $startup = Join-Path $SourceRoot 'src/FluxKnowledge.Web/FluxKnowledge.Web.csproj'
    & dotnet ef migrations script $from $to --configuration Release --project $project --startup-project $startup --no-build --output $OutputPath | Out-Host
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $OutputPath -PathType Leaf)) { throw 'Generating Corpus Full-Text migration SQL failed.' }
    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
    $expected = if ($Direction -ceq 'up') { $contract.UpSha256 } else { $contract.DownSha256 }
    if ($hash -cne $expected) { throw 'Generated Corpus Full-Text SQL does not match the reviewed SHA-256.' }
    return [pscustomobject]@{ From=$from; To=$to; Path=$OutputPath; Sha256=$hash }
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

function New-CodeDisclosureMigrationScript {
    param([string]$SourceRoot, [string]$OutputPath)
    $contract = Get-CodeDisclosureMigrationContract
    $project = Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj'
    & dotnet ef migrations script $contract.Baseline $contract.Target --idempotent --configuration Release `
        --project $project --startup-project $project --no-build --output $OutputPath | Out-Host
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $OutputPath -PathType Leaf)) { throw 'Generating code disclosure migration SQL failed.' }
    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
    if ($hash -cne $contract.UpSha256) { throw 'Generated code disclosure SQL differs from the reviewed SHA-256.' }
    return [pscustomobject]@{ From=$contract.Baseline; To=$contract.Target; Path=$OutputPath; Sha256=$hash }
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

function Assert-CpuSearchOwnerAcl {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $unsafeRights = [int]([Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership)
    $workerRead = $false
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        $identity = $rule.IdentityReference.Value
        if ($identity -ieq 'IIS APPPOOL\FluxKnowledge' -and
            (([int]$rule.FileSystemRights -band [int][Security.AccessControl.FileSystemRights]::ReadData) -ne 0)) {
            $workerRead = $true
        }
        if ($identity -notin @('NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators') -and
            (([int]$rule.FileSystemRights -band $unsafeRights) -ne 0)) {
            throw "The CPU search owner path grants untrusted write or delete access: $Path"
        }
    }
    if (-not $workerRead) { throw "The CPU search owner path does not grant the IIS worker read access: $Path" }
}

function Get-CpuSearchOwnerFilePlan {
    $runtimeRoot = Join-Path $CanonicalLiveRoot 'Runtime'
    foreach ($path in @($CanonicalLiveRoot, $runtimeRoot)) {
        Assert-NotReparsePoint -Path $path -Message 'cpu-search-owner-directory-unsafe'
        Assert-CpuSearchOwnerAcl -Path $path
    }
    if (Test-Path -LiteralPath $CpuSearchOwnerPath) {
        if (-not (Test-Path -LiteralPath $CpuSearchOwnerPath -PathType Leaf)) { throw 'cpu-search-owner-file-invalid' }
        Assert-NotReparsePoint -Path $CpuSearchOwnerPath -Message 'cpu-search-owner-file-unsafe'
        Assert-CpuSearchOwnerAcl -Path $CpuSearchOwnerPath
        return [ordered]@{ path=$CpuSearchOwnerPath; action='preserve-existing' }
    }
    return [ordered]@{ path=$CpuSearchOwnerPath; action='create-new-before-activation' }
}

function Ensure-CpuSearchOwnerFile {
    $plan = Get-CpuSearchOwnerFilePlan
    if ($plan.action -eq 'create-new-before-activation') {
        $stream = [IO.FileStream]::new($CpuSearchOwnerPath, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $stream.Dispose()
    }
    $verified = Get-CpuSearchOwnerFilePlan
    if ($verified.action -ne 'preserve-existing') { throw 'cpu-search-owner-file-unavailable' }
}

function Get-HybridIisWorkerIds {
    param([Parameter(Mandatory)][string]$AppCmdPath, [Parameter(Mandatory)][string]$PoolName)
    # Appcmd's filtered list returns exit 1 for an empty result. Obtain a successful
    # complete inventory, validate it, then select the exact application pool.
    $output = (& $AppCmdPath list wp /xml | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'hybrid-iis-worker-inventory-unavailable' }
    try {
        $inventory = [xml]$output
        if ($inventory.DocumentElement.Name -cne 'appcmd') { throw 'Invalid inventory root.' }
        foreach ($worker in $inventory.DocumentElement.ChildNodes) {
            if ($worker.NodeType -eq [System.Xml.XmlNodeType]::Whitespace) { continue }
            $id = 0
            if ($worker.Name -cne 'WP' -or
                -not [int]::TryParse($worker.GetAttribute('WP.NAME'), [ref]$id) -or $id -le 0 -or
                [string]::IsNullOrWhiteSpace($worker.GetAttribute('APPPOOL.NAME'))) { throw 'Invalid worker identity.' }
            if ([string]::Equals($worker.GetAttribute('APPPOOL.NAME'), $PoolName, [StringComparison]::OrdinalIgnoreCase)) { $id }
        }
    }
    catch { throw 'hybrid-iis-worker-inventory-unavailable' }
}

function Stop-HybridIisAfterGpuDrain {
    param([scriptblock]$OnStopRequested)
    Invoke-WithHybridGpuDrain -ConnectionString (Get-DeploymentSqlConnectionString) -TimeoutSeconds $RebuildTimeoutSeconds -StopApplication {
        param($sessionId)
        $appcmd = Join-Path $env:SystemRoot 'System32/inetsrv/appcmd.exe'
        $workerIds = @(Get-HybridIisWorkerIds -AppCmdPath $appcmd -PoolName $SiteName)
        $identities = [Collections.Generic.List[object]]::new()
        $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop)
        $pending = [Collections.Generic.Queue[int]]::new()
        foreach ($id in $workerIds) { $pending.Enqueue($id) }
        while ($pending.Count -gt 0) {
            $id = $pending.Dequeue()
            $process = @($processes | Where-Object ProcessId -eq $id)
            if ($process.Count -ne 1) { throw 'hybrid-iis-process-identity-ambiguous' }
            $identities.Add(@{ ProcessId=$id; Created=$process[0].CreationDate })
            foreach ($child in @($processes | Where-Object ParentProcessId -eq $id)) { $pending.Enqueue([int]$child.ProcessId) }
        }
        if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') {
            if ($OnStopRequested) { & $OnStopRequested }
            Stop-WebAppPool -Name $SiteName
        }
        Wait-IisAppPoolState -Name $SiteName -ExpectedState 'Stopped' -TimeoutSeconds $ReadinessTimeoutSeconds
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $remaining = @()
            foreach ($identity in $identities) {
                $current = @(Get-CimInstance Win32_Process -Filter "ProcessId = $($identity.ProcessId)" -ErrorAction Stop)
                $remaining += @($current | Where-Object CreationDate -eq $identity.Created)
            }
            $currentWorkers = @(Get-HybridIisWorkerIds -AppCmdPath $appcmd -PoolName $SiteName)
            if ($remaining.Count -eq 0 -and $currentWorkers.Count -eq 0) { break }
            if ($timer.Elapsed.TotalSeconds -ge $ReadinessTimeoutSeconds) { throw 'hybrid-iis-worker-exit-not-proven' }
            Start-Sleep -Milliseconds 250
        }
    }
}

function New-IncrementalIisServerManager {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $env:SystemRoot 'System32/inetsrv/Microsoft.Web.Administration.dll'))
    return [Microsoft.Web.Administration.ServerManager]::new()
}

function Get-IncrementalIisHostingSettings {
    param([object]$Manager = $null)
    $ownsManager = $null -eq $Manager
    if ($ownsManager) { $Manager = New-IncrementalIisServerManager }
    try {
        $pool = $Manager.ApplicationPools[$SiteName]
        $site = $Manager.Sites[$SiteName]
        if ($null -eq $pool -or $null -eq $site) { throw 'The canonical IIS pool/site is missing.' }
        $application = $site.Applications['/']
        if ($null -eq $application -or $application.ApplicationPoolName -cne $SiteName -or
            $null -eq $application.VirtualDirectories['/']) { throw 'The canonical IIS application binding changed.' }
        Assert-CanonicalPath -RequestedPath $application.VirtualDirectories['/'].PhysicalPath `
            -ExpectedPath $CanonicalDeployRoot -Message 'The canonical IIS application path changed.'
        $modules = @($Manager.GetWebConfiguration($SiteName).GetSection('system.webServer/modules').GetCollection() |
            Where-Object { $_.GetAttributeValue('name') -ceq 'ApplicationInitializationModule' })
        return [pscustomobject]@{
            StartMode = [string]$pool.StartMode
            IdleTimeout = $pool.ProcessModel.IdleTimeout.ToString()
            PreloadEnabled = [bool]$application.GetAttributeValue('preloadEnabled')
            ModuleEnabled = $modules.Count -eq 1
            PoolState = [string]$pool.State
        }
    }
    finally { if ($ownsManager) { $Manager.Dispose() } }
}

function Test-IisHostingSettingsMatch {
    param([object]$Left, [object]$Right)
    return [bool]($Left.StartMode -ceq $Right.StartMode -and $Left.IdleTimeout -ceq $Right.IdleTimeout -and
        $Left.PreloadEnabled -eq $Right.PreloadEnabled)
}

function Set-IncrementalIisHostingSettings {
    param([Parameter(Mandatory)][object]$Expected, [Parameter(Mandatory)][object]$Desired)
    $manager = New-IncrementalIisServerManager
    try {
        $current = Get-IncrementalIisHostingSettings -Manager $manager
        if ($current.PoolState -cne 'Stopped') { throw 'Hosting changes require the IIS pool to be stopped.' }
        if (!(Test-IisHostingSettingsMatch $current $Expected)) { throw 'IIS hosting settings changed after capture.' }
        if ($Desired.PreloadEnabled -and !$current.ModuleEnabled) { throw 'Application Initialization must be enabled.' }
        $manager.ApplicationPools[$SiteName].StartMode = $Desired.StartMode
        $manager.ApplicationPools[$SiteName].ProcessModel.IdleTimeout = [TimeSpan]::Parse(
            $Desired.IdleTimeout, [Globalization.CultureInfo]::InvariantCulture)
        $manager.Sites[$SiteName].Applications['/'].SetAttributeValue('preloadEnabled', [bool]$Desired.PreloadEnabled)
        $manager.CommitChanges()
    }
    finally { $manager.Dispose() }
    $actual = Get-IncrementalIisHostingSettings
    if ($actual.PoolState -cne 'Stopped' -or !(Test-IisHostingSettingsMatch $actual $Desired)) {
        throw 'The committed IIS hosting settings or stopped-pool state could not be verified.'
    }
}

function Restore-IncrementalIisHostingSettings {
    param([Parameter(Mandatory)][object]$Original, [Parameter(Mandatory)][object]$Desired)
    $current = Get-IncrementalIisHostingSettings
    if ($current.PoolState -cne 'Stopped') { throw 'Hosting rollback requires the IIS pool to be stopped.' }
    if (Test-IisHostingSettingsMatch $current $Original) { return }
    if (!(Test-IisHostingSettingsMatch $current $Desired)) { throw 'IIS hosting settings changed outside this release; hold retained.' }
    Set-IncrementalIisHostingSettings -Expected $current -Desired $Original
}

function Wait-IncrementalIisPreloadStartup {
    param([Parameter(Mandatory)][DateTime]$AfterUtc, [Parameter(Mandatory)][int]$TimeoutSeconds)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $appcmd = Join-Path $env:SystemRoot 'System32/inetsrv/appcmd.exe'
    $quotedRoot = "'" + $CanonicalDeployRoot.TrimEnd('\') + "\'"
    do {
        $workers = @(foreach ($id in @(Get-HybridIisWorkerIds -AppCmdPath $appcmd -PoolName $SiteName)) {
            Get-CimInstance Win32_Process -Filter "ProcessId = $id" -ErrorAction Stop
        })
        $events = @()
        try {
            $events = @(Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='IIS AspNetCore Module V2'
                Id=1032; StartTime=$AfterUtc } -ErrorAction Stop)
        }
        catch { if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') { throw } }
        foreach ($event in $events) {
            $worker = @($workers | Where-Object { $_.ProcessId -eq $event.ProcessId })
            if ($worker.Count -ne 1) { continue }
            $created = $worker[0].CreationDate.ToUniversalTime()
            $eventTime = $event.TimeCreated.ToUniversalTime()
            if ($created -lt $AfterUtc -or $eventTime -lt $AfterUtc -or $eventTime -lt $created -or
                ([string]$event.Properties[0].Value).IndexOf($quotedRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            return [pscustomobject]@{ WorkerId=$worker[0].ProcessId; WorkerCreatedUtc=$created.ToString('O')
                EventRecordId=$event.RecordId; EventTimeUtc=$eventTime.ToString('O') }
        }
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
        Start-Sleep -Milliseconds 250
    } while ($true)
    throw 'Unattended managed-host startup evidence was not observed before HTTP probes.'
}

function Test-IncrementalRollbackHoldRelease {
    param(
        [Parameter(Mandatory)][hashtable]$Validation,
        [bool]$ApplyMigrations,
        [bool]$ApplyCorpusChunkFullTextMigration,
        [bool]$CorpusRollbackVerified,
        [bool]$ApplyCodeDisclosureProofMigration = $false,
        [bool]$CodeDisclosureRecoveryVerified = $false,
        [bool]$InteractiveHostMutationStarted,
        [bool]$InteractiveHostRollbackVerified,
        [bool]$RecoverStoppedPool = $false,
        [bool]$HostingRollbackVerified = $true
    )
    return [bool](-not $RecoverStoppedPool -and $Validation.HoldCreated -and $Validation.PayloadRollbackVerified -and
        (-not $ApplyMigrations -or $Validation.RollbackVerified) -and
        (-not $ApplyCorpusChunkFullTextMigration -or $CorpusRollbackVerified) -and
        (-not $ApplyCodeDisclosureProofMigration -or $CodeDisclosureRecoveryVerified) -and
        (-not $InteractiveHostMutationStarted -or $InteractiveHostRollbackVerified) -and $HostingRollbackVerified)
}

function Get-HybridPreservedInputFingerprint {
    $connection = [System.Data.SqlClient.SqlConnection]::new((Get-DeploymentSqlConnectionString))
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = @'
SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONCAT(
    (SELECT * FROM SourceRootConfigurations ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM SourceIdentities ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM SourceRevisions ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM SourceActivities ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM SourceProcessorBranches ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM SourceProcessorBranchMembers ORDER BY BranchId, ChildSourceRevisionId FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM DocumentPublications ORDER BY OwnerSourceRevisionId FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT Id, SourceIdentityId, SourceRevisionId, Revision, ContentHash, RootLineageRecordId,
            ParentRevisionRecordId, IsDeleted, RegisteredAtUtc FROM PipelineRecords ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES),
    (SELECT * FROM Artifacts WHERE Stage <= 3 ORDER BY Id FOR JSON PATH, INCLUDE_NULL_VALUES)
))), 2);
'@
            return [string]$command.ExecuteScalar()
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

function Activate-HybridCompatiblePayload {
    param([string]$CandidateRoot, [string]$PreviousRoot, [string]$ReleaseRoot, [string]$ConfigurationCandidate,
        [string]$ConfigurationPath, [System.Collections.IDictionary]$State)
    $alreadyCompatible = (Test-Path -LiteralPath $CanonicalDeployRoot -PathType Container) -and
        (Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -ceq $State.CandidateHash
    if (-not $alreadyCompatible) {
        if (Test-Path -LiteralPath $CanonicalDeployRoot) {
            $destination = if (-not (Test-Path -LiteralPath $PreviousRoot)) { $PreviousRoot } else {
                Join-Path $ReleaseRoot ('interrupted-activation-' + [Guid]::NewGuid().ToString('N'))
            }
            Assert-CanonicalPath -RequestedPath $CanonicalDeployRoot -ExpectedPath 'I:\FluxKnowledge\App' -Message 'hybrid-activation-path-unsafe'
            Move-Item -LiteralPath $CanonicalDeployRoot -Destination $destination
        }
        Invoke-CandidatePayloadActivation -CandidateRoot $CandidateRoot -ApplicationRoot $CanonicalDeployRoot
    }
    if ((Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $State.CandidateHash) { throw 'hybrid-activated-payload-hash-mismatch' }
    Write-HybridRebuildJson -Path $ConfigurationPath -Value (Get-Content -LiteralPath $ConfigurationCandidate -Raw | ConvertFrom-Json -AsHashtable)
    if ((Get-FileHash -LiteralPath $ConfigurationPath -Algorithm SHA256).Hash -cne $State.ActivatedConfigHash) { throw 'hybrid-configuration-activation-mismatch' }
}

function Invoke-HybridPassageIisUpdate {
    param([string]$SourceRoot, [string]$Commit, [string]$ResumeRelease, [string]$ReplaceRelease = '')
    $contract = Get-HybridPassageMigrationContract
    $configurationPath = Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    Assert-NotReparsePoint -Path $CanonicalLiveRoot -Message 'hybrid-live-root-unsafe'
    Assert-NotReparsePoint -Path $configurationPath -Message 'hybrid-configuration-unsafe'
    if (-not (Test-Path -LiteralPath $IncrementalRecoveryRoot)) { New-Item -ItemType Directory -Path $IncrementalRecoveryRoot | Out-Null }
    Assert-NotReparsePoint -Path $IncrementalRecoveryRoot -Message 'hybrid-recovery-root-unsafe'
    $releaseId = if ($ResumeRelease) { $ResumeRelease } else { '{0}-{1}-hybrid' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), $Commit.Substring(0,12) }
    $releaseRoot = Join-Path $IncrementalRecoveryRoot $releaseId
    $statePath = Join-Path $releaseRoot 'hybrid-state.json'
    $candidateRoot = Join-Path $releaseRoot 'candidate'
    $operatorRoot = Join-Path $releaseRoot 'operator'
    $previousRoot = Join-Path $releaseRoot 'previous'
    $configurationCandidate = Join-Path $releaseRoot 'candidate-config.json'
    $upPath = Join-Path $releaseRoot 'hybrid-idempotent-up.sql'
    $manifestPath = Join-Path $releaseRoot 'corpus-rebuild-manifest.json'
    $connectionString = Get-DeploymentSqlConnectionString
    $database = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
    if ($ResumeRelease) {
        Assert-NotReparsePoint -Path $releaseRoot -Message 'hybrid-release-root-unsafe'
        foreach ($file in @($statePath,$configurationCandidate,$upPath)) {
            Assert-NotReparsePoint -Path $file -Message 'hybrid-resume-file-unsafe'
        }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($required in @('Version','ReleaseId','Commit','OperationId','DatabaseServer','DatabaseName','OriginalHistory',
            'SchemaAttempted','ResetCommitted','HoldReleased','HoldReleaseAttempted','CandidateHash','OperatorHash',
            'OriginalConfigHash','ActivatedConfigHash','PreservedInputFingerprint','InteractiveHostWasEnabled')) {
            if (-not $state.ContainsKey($required)) { throw 'hybrid-resume-state-incomplete' }
        }
        foreach ($flag in @('SchemaAttempted','ResetCommitted','HoldReleased','HoldReleaseAttempted','InteractiveHostWasEnabled')) {
            if ($state[$flag] -isnot [bool]) { throw 'hybrid-resume-state-ambiguous' }
        }
        $operationId = [Guid]::Empty
        if ($state.Version -ne 1 -or $state.ReleaseId -cne $releaseId -or $state.Commit -cne $Commit -or
            $state.DatabaseServer -cne $database.DataSource -or $state.DatabaseName -cne $database.InitialCatalog -or
            -not [Guid]::TryParseExact($state.OperationId, 'D', [ref]$operationId) -or $operationId -eq [Guid]::Empty) { throw 'hybrid-resume-identity-mismatch' }
        foreach ($payload in @(@{ Path=$candidateRoot; Hash=$state.CandidateHash }, @{ Path=$operatorRoot; Hash=$state.OperatorHash })) {
            if ((Get-HybridPayloadFingerprint -Path $payload.Path) -cne $payload.Hash) { throw 'hybrid-resume-payload-changed' }
        }
        if ((Get-FileHash -LiteralPath $configurationCandidate -Algorithm SHA256).Hash -cne $state.ActivatedConfigHash) { throw 'hybrid-resume-configuration-candidate-changed' }
        if ((Get-FileHash -LiteralPath $upPath -Algorithm SHA256).Hash -cne $contract.UpSha256) { throw 'hybrid-resume-schema-script-changed' }
    }
    else {
        $history = @(Get-AppliedMigrationIds)
        $replacement = $null
        if ($ReplaceRelease) {
            $replacement = Get-HybridReplacementBinding -RecoveryRoot $IncrementalRecoveryRoot -ReleaseId $ReplaceRelease -ConnectionString $connectionString
            Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $replacement.ReleaseId -OperationId $replacement.OperationId
            if ((Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $replacement.CandidateHash -or
                (Get-FileHash -LiteralPath $configurationPath).Hash -cne $replacement.ActivatedConfigHash -or
                (Get-HybridPreservedInputFingerprint) -cne $replacement.PreservedInputFingerprint) { throw 'hybrid-replacement-live-binding-changed' }
            Assert-HybridMigrationHistory -OriginalHistory $replacement.OriginalHistory -CurrentHistory $history
        }
        else { Assert-HybridMigrationHistory -OriginalHistory $history -CurrentHistory $history }
        if (Test-Path -LiteralPath $releaseRoot) { throw 'hybrid-release-already-exists' }
        New-Item -ItemType Directory -Path $releaseRoot | Out-Null
        $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($provider in @(@{ Name='Model'; Flat='ModelRuntimeEnabled' }, @{ Name='Gpu'; Flat='GpuEnabled' }, @{ Name='Ocr'; Flat='OcrEnabled' })) {
            $runtime = $configuration.Runtime
            $flat = $false; $nested = $false
            if ($runtime.ContainsKey($provider.Flat)) { if (-not [bool]::TryParse([string]$runtime[$provider.Flat], [ref]$flat)) { throw 'hybrid-shared-ocr-runtime-invalid' } }
            if ($runtime.ContainsKey($provider.Name) -and $runtime[$provider.Name].ContainsKey('Enabled')) {
                if (-not [bool]::TryParse([string]$runtime[$provider.Name].Enabled, [ref]$nested)) { throw 'hybrid-shared-ocr-runtime-invalid' }
            }
            if (-not ($flat -or $nested)) { throw 'hybrid-shared-ocr-runtime-required' }
        }
        Copy-Item -LiteralPath $configurationPath -Destination (Join-Path $releaseRoot 'original-config.json')
        if (-not $configuration.ContainsKey('Search')) { $configuration.Search = @{} }
        $configuration.Search.HybridPassagesEnabled = $true
        Write-HybridRebuildJson -Path $configurationCandidate -Value $configuration
        & dotnet publish (Join-Path $SourceRoot 'src/FluxKnowledge.Web/FluxKnowledge.Web.csproj') -c Release --no-restore --nologo -o $candidateRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'hybrid-web-publish-failed' }
        & dotnet publish (Join-Path $SourceRoot 'src/FluxKnowledge.Cli/FluxKnowledge.Cli.csproj') -c Release --no-restore --nologo -o $operatorRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'hybrid-operator-publish-failed' }
        Test-ApplicationPayload -Path $candidateRoot
        & dotnet ef migrations script $contract.Baseline $contract.Target --idempotent --configuration Release `
            --project (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer') `
            --startup-project (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer') --no-build --output $upPath | Out-Host
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $upPath -Algorithm SHA256).Hash -cne $contract.UpSha256) { throw 'hybrid-schema-script-hash-mismatch' }
        $state = @{
            Version=1; ReleaseId=$releaseId; Commit=$Commit; OperationId=[Guid]::NewGuid().ToString('D')
            DatabaseServer=$database.DataSource; DatabaseName=$database.InitialCatalog
            OriginalHistory=$(if ($replacement) { $replacement.OriginalHistory } else { $history }); InitialHistory=$history
            SchemaAttempted=$false; ResetCommitted=$false; HoldReleased=$false; HoldReleaseAttempted=$false
            CandidateHash=(Get-HybridPayloadFingerprint -Path $candidateRoot); OperatorHash=(Get-HybridPayloadFingerprint -Path $operatorRoot)
            OriginalConfigHash=(Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash
            ActivatedConfigHash=(Get-FileHash -LiteralPath $configurationCandidate -Algorithm SHA256).Hash
            PreservedInputFingerprint=$null; InteractiveHostWasEnabled=[bool](Get-ScheduledTask -TaskName $InteractiveHostTaskName).Settings.Enabled
        }
        if ($replacement) {
            $state.Replacement = $replacement; $state.PreservedInputFingerprint = $replacement.PreservedInputFingerprint
            $state.InteractiveHostWasEnabled = $replacement.InteractiveHostWasEnabled
        }
        Write-HybridRebuildJson -Path $statePath -Value $state
    }
    $configurationHash = (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash
    if ($configurationHash -cne $state.OriginalConfigHash -and $configurationHash -cne $state.ActivatedConfigHash) { throw 'hybrid-live-configuration-changed' }
    $currentHistory = @(Get-AppliedMigrationIds)
    Assert-HybridMigrationHistory -OriginalHistory $state.OriginalHistory -CurrentHistory $currentHistory
    $initialHistory = if ($state.ContainsKey('InitialHistory')) { @($state.InitialHistory) } else { @($state.OriginalHistory) }
    if ($ResumeRelease -and -not $state.SchemaAttempted -and $currentHistory.Count -ne $initialHistory.Count) {
        throw 'hybrid-resume-schema-boundary-ambiguous'
    }
    # This check opens verified file leases only. It performs no native inference or acquisition.
    $models = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('verify-models')
    if (-not $models.Verified -or -not $models.Offline) { throw 'hybrid-offline-model-verification-failed' }
    $holdOwner = @{ ReleaseId=$releaseId; OperationId=$state.OperationId }
    if ($state.ContainsKey('Replacement')) {
        Assert-HybridReplacementPacket -RecoveryRoot $IncrementalRecoveryRoot -Binding $state.Replacement
        # SQL is authoritative if the process lost its commit response or journal write.
        $receipt = Get-HybridReplacementSqlReceipt -ConnectionString $connectionString -OperationId $state.OperationId
        if ($receipt.Committed) {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
            Assert-HybridReplacementReceipt -Receipt $receipt -Manifest $manifest -Binding $state.Replacement
            $state.ResetCommitted = $true; $state.ResetManifestHash = $receipt.ManifestHash
            Write-HybridRebuildJson -Path $statePath -Value $state
            if (-not $state.HoldReleased -and -not ($state.HoldReleaseAttempted -and -not (Test-Path -LiteralPath $ValidationHoldPath))) {
                Move-HybridReplacementHold -Path $ValidationHoldPath -Binding $state.Replacement -ReleaseId $releaseId -OperationId $state.OperationId
            }
        }
        else {
            if ($state.ResetCommitted) { throw 'hybrid-replacement-reset-receipt-missing' }
            $holdOwner.ReleaseId = $state.Replacement.ReleaseId; $holdOwner.OperationId = $state.Replacement.OperationId
        }
    }
    if ($state.HoldReleased -or ($state.HoldReleaseAttempted -and -not (Test-Path -LiteralPath $ValidationHoldPath))) {
        if (Test-Path -LiteralPath $ValidationHoldPath) { throw 'hybrid-release-outcome-ambiguous' }
        $receipt = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$state.OperationId)
        if (-not $receipt.Committed -or -not $receipt.Completed -or $currentHistory[-1] -cne $contract.Target -or
            ($state.ContainsKey('ResetManifestHash') -and $state.ResetManifestHash -cne $receipt.ManifestHash) -or
            $configurationHash -cne $state.ActivatedConfigHash -or
            (Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $state.CandidateHash) { throw 'hybrid-release-outcome-ambiguous' }
        Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
        $state.HoldReleased = $true
        Write-HybridRebuildJson -Path $statePath -Value $state
    }
    elseif (-not $state.HoldReleased) {
        $attempt = @{ OwnsHold=$false; StoppedApplication=$false; DisabledInteractiveHost=$false }
        try {
            if (-not (Test-Path -LiteralPath $ValidationHoldPath)) { [void](New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId) }
            Invoke-HybridPassageRebuildFlow -State $state -SaveState { Write-HybridRebuildJson -Path $statePath -Value $state } -Quiesce {
                Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $holdOwner.ReleaseId -OperationId $holdOwner.OperationId -Permit $false
                $attempt.OwnsHold = $true
                Disable-ScheduledTask -TaskName $InteractiveHostTaskName | Out-Null
                $attempt.DisabledInteractiveHost = $true
                Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds $ReadinessTimeoutSeconds
                if ($ResumeRelease -and $state.SchemaAttempted -and (Get-WebAppPoolState -Name $SiteName).Value -eq 'Stopped' -and
                    @(Get-AppliedMigrationIds)[-1] -ceq $contract.Target) {
                    $receipt = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$state.OperationId)
                    if ($receipt.Committed) {
                        if ($state.ContainsKey('ResetManifestHash') -and $state.ResetManifestHash -cne $receipt.ManifestHash) { throw 'hybrid-reset-receipt-mismatch' }
                        # Only the verified compatible payload starts here. Its existing recovery loop
                        # may prove dead owners and settle reservations; deny-all admission stays in place.
                        Activate-HybridCompatiblePayload -CandidateRoot $candidateRoot -PreviousRoot $previousRoot -ReleaseRoot $releaseRoot `
                            -ConfigurationCandidate $configurationCandidate -ConfigurationPath $configurationPath -State $state
                        Start-WebAppPool -Name $SiteName
                        Wait-IisAppPoolState -Name $SiteName -ExpectedState 'Started' -TimeoutSeconds $ReadinessTimeoutSeconds
                        $live = Invoke-FixedLoopbackProbe -Uri "$($loopbackOrigin.Origin)/health/live" -TimeoutSeconds $ReadinessTimeoutSeconds
                        $live.Dispose()
                    }
                }
                Stop-HybridIisAfterGpuDrain -OnStopRequested { $attempt.StoppedApplication = $true }
                if ($null -eq $state.PreservedInputFingerprint) { $state.PreservedInputFingerprint = Get-HybridPreservedInputFingerprint }
                elseif ((Get-HybridPreservedInputFingerprint) -cne $state.PreservedInputFingerprint) { throw 'hybrid-preserved-inputs-changed' }
            } -ApplySchema {
                Assert-HybridMigrationHistory -OriginalHistory $state.OriginalHistory -CurrentHistory @(Get-AppliedMigrationIds)
                Invoke-GeneratedSqlScript -Path $upPath
                $current = @(Get-AppliedMigrationIds)
                Assert-HybridMigrationHistory -OriginalHistory $state.OriginalHistory -CurrentHistory $current
                if ($current[-1] -cne $contract.Target) { throw 'hybrid-schema-target-not-recorded' }
            } -ResetAndPrepare {
                $receipt = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$state.OperationId)
                if (-not $receipt.Committed) {
                    if (-not (Test-Path -LiteralPath $manifestPath)) {
                        $planArgs = @('plan','--operation',$state.OperationId)
                        if ($state.ContainsKey('Replacement')) { $planArgs += @('--replace',$state.Replacement.OperationId,'--epoch',$state.Replacement.TargetEpoch,'--manifest-hash',$state.Replacement.ManifestHash) }
                        $plan = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments $planArgs
                        Write-HybridRebuildJson -Path $manifestPath -Value $plan
                    }
                    Assert-NotReparsePoint -Path $manifestPath -Message 'hybrid-manifest-file-unsafe'
                    try { $receipt = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('commit','--manifest',$manifestPath) }
                    catch {
                        $receipt = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$state.OperationId)
                        if (-not $receipt.Committed) { throw }
                    }
                    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
                    if ($receipt.ManifestHash -cne $manifest.ManifestHash -or [string]$receipt.TargetEpoch -cne [string]$manifest.TargetEpoch) { throw 'hybrid-reset-receipt-mismatch' }
                    if ($state.ContainsKey('Replacement')) {
                        $receipt = Get-HybridReplacementSqlReceipt -ConnectionString $connectionString -OperationId $state.OperationId
                        Assert-HybridReplacementReceipt -Receipt $receipt -Manifest $manifest -Binding $state.Replacement
                    }
                    $state.ResetManifestHash = $receipt.ManifestHash
                }
                elseif ($state.ContainsKey('ResetManifestHash') -and $state.ResetManifestHash -cne $receipt.ManifestHash) { throw 'hybrid-reset-receipt-mismatch' }
                else { $state.ResetManifestHash = $receipt.ManifestHash }
                $state.ResetCommitted = $true
                Write-HybridRebuildJson -Path $statePath -Value $state
                if ($state.ContainsKey('Replacement')) {
                    Move-HybridReplacementHold -Path $ValidationHoldPath -Binding $state.Replacement -ReleaseId $releaseId -OperationId $state.OperationId
                    $holdOwner.ReleaseId = $releaseId; $holdOwner.OperationId = $state.OperationId
                }
                [void](Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('prepare','--operation',$state.OperationId))
            } -ActivateAndStart {
                Activate-HybridCompatiblePayload -CandidateRoot $candidateRoot -PreviousRoot $previousRoot -ReleaseRoot $releaseRoot `
                    -ConfigurationCandidate $configurationCandidate -ConfigurationPath $configurationPath -State $state
                Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $releaseId -OperationId $state.OperationId -Permit $true
                Start-WebAppPool -Name $SiteName
                Wait-IisAppPoolState -Name $SiteName -ExpectedState 'Started' -TimeoutSeconds $ReadinessTimeoutSeconds
                $live = Invoke-FixedLoopbackProbe -Uri "$($loopbackOrigin.Origin)/health/live" -TimeoutSeconds $ReadinessTimeoutSeconds
                $live.Dispose()
            } -RunAndFinish {
                $timer = [Diagnostics.Stopwatch]::StartNew()
                while ($true) {
                    $status = Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$state.OperationId)
                    if ($status.Completed) { break }
                    if ($status.FailedEmbeddingJobs -gt 0 -or $status.FailedPublishJobs -gt 0) { throw 'hybrid-rebuild-job-failed-hold-retained' }
                    if ($status.PendingItems -eq 0 -and $status.RunningItems -eq 0) {
                        try { [void](Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('finish','--operation',$state.OperationId)); break }
                        catch { if ($_.Exception.Message -cne 'corpus-rebuild-full-text-not-ready') { throw } }
                    }
                    if ($timer.Elapsed.TotalSeconds -ge $RebuildTimeoutSeconds) { throw 'hybrid-rebuild-wait-timeout-hold-retained' }
                    Start-Sleep -Milliseconds 1000
                }
            } -Validate {
                if ((Get-HybridPreservedInputFingerprint) -cne $state.PreservedInputFingerprint) { throw 'hybrid-preserved-inputs-changed' }
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            } -ReleaseHold {
                $state.HoldReleaseAttempted = $true
                Write-HybridRebuildJson -Path $statePath -Value $state
                Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId -OperationId $state.OperationId
                Remove-Item -LiteralPath $ValidationHoldPath -Force
            }
        }
        catch {
            if ($state.SchemaAttempted -or $state.ContainsKey('Replacement')) {
                if ($attempt.OwnsHold -and (Test-Path -LiteralPath $ValidationHoldPath)) { Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $holdOwner.ReleaseId -OperationId $holdOwner.OperationId -Permit $false }
                throw "Hybrid deployment requires forward recovery. Current payload/schema/configuration and the hold are retained; never restore old binaries or downgrade automatically. Resume release $releaseId. Failure: $($_.Exception.Message)"
            }
            # No schema command, reset, configuration or payload change has occurred.
            if ($attempt.OwnsHold) {
                if ($attempt.StoppedApplication -and (Get-WebAppPoolState -Name $SiteName).Value -ne 'Started') { Start-WebAppPool -Name $SiteName }
                Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId
                if ($attempt.DisabledInteractiveHost -and $state.InteractiveHostWasEnabled) { Enable-ScheduledTask -TaskName $InteractiveHostTaskName | Out-Null }
            }
            throw
        }
    }
    Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
    if ($state.InteractiveHostWasEnabled) { Enable-ScheduledTask -TaskName $InteractiveHostTaskName | Out-Null }
    [ordered]@{ ok=$true; mode='hybrid-rebuild-applied'; commit=$Commit; release_root=$releaseRoot; operation_id=$state.OperationId;
        reset_manifest_hash=$state.ResetManifestHash; schema_target=$contract.Target; deployment_validation_hold='released-after-rebuild-and-preserved-input-validation';
        runtime='offline-BGE-shared-scheduler-load-run-unload'; rollback='forward-only; original payload/configuration retained for inspection' } | ConvertTo-Json
}

function Invoke-HybridPassageIisPatch {
    param([string]$SourceRoot, [string]$Commit, [string]$PredecessorRelease, [string]$ResumeRelease,
        [string]$ExpectedCandidateHash='', [string]$ExpectedOperatorHash='')
    $connectionString=Get-DeploymentSqlConnectionString
    $configurationPath=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    $releaseId=if ($ResumeRelease) { $ResumeRelease } else { '{0}-{1}-hybridpatch' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'),$Commit.Substring(0,12) }
    $releaseRoot=Join-Path $IncrementalRecoveryRoot $releaseId
    $bindingPath=Join-Path $releaseRoot 'patch-binding.json'
    $progressPath=Join-Path $releaseRoot 'patch-progress.json'
    $candidateRoot=Join-Path $releaseRoot 'candidate'
    $operatorRoot=Join-Path $releaseRoot 'operator'
    $previousRoot=Join-Path $releaseRoot 'previous'
    $candidateConfig=Join-Path $releaseRoot 'candidate-config.json'
    $contract=Get-HybridPassageMigrationContract
    Assert-NotReparsePoint -Path $CanonicalLiveRoot -Message 'hybrid-patch-live-root-unsafe'
    Assert-NotReparsePoint -Path $IncrementalRecoveryRoot -Message 'hybrid-patch-recovery-root-unsafe'
    Assert-NotReparsePoint -Path $configurationPath -Message 'hybrid-patch-configuration-unsafe'
    if ($ResumeRelease) {
        if ($ResumeRelease -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybridpatch$') { throw 'hybrid-patch-resume-release-invalid' }
        foreach ($path in @($releaseRoot,$bindingPath,$progressPath,$operatorRoot,$candidateConfig)) {
            Assert-NotReparsePoint -Path $path -Message 'hybrid-patch-resume-path-unsafe'
        }
        foreach ($path in @($candidateRoot,$previousRoot,$CanonicalDeployRoot)) {
            if (Test-Path -LiteralPath $path) { Assert-NotReparsePoint -Path $path -Message 'hybrid-patch-payload-root-unsafe' }
        }
        $binding=Get-Content -LiteralPath $bindingPath -Raw | ConvertFrom-Json -AsHashtable
        $progress=Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($field in @('Version','ReleaseId','Commit','OperationId','TargetEpoch','ManifestHash','Predecessor',
            'CandidateHash','OperatorHash','ConfigurationHash','PreservedInputFingerprint')) {
            if (-not $binding.ContainsKey($field)) { throw 'hybrid-patch-binding-incomplete' }
        }
        foreach ($field in @('ActivationIntent','HoldReleaseAttempted','HoldReleased')) {
            if (-not $progress.ContainsKey($field) -or $progress[$field] -isnot [bool]) { throw 'hybrid-patch-progress-ambiguous' }
        }
        if ($binding.Version -ne 1 -or $binding.ReleaseId -cne $releaseId -or $binding.Commit -cne $Commit -or
            $binding.Commit -cnotmatch ('^' + [Regex]::Escape(($releaseId -split '-')[1]) + '[0-9a-f]{28}$')) {
            throw 'hybrid-patch-resume-identity-mismatch'
        }
        $predecessor=$binding.Predecessor
        Assert-HybridPatchPacket -RecoveryRoot $IncrementalRecoveryRoot -Binding $predecessor -ConnectionString $connectionString
        if ($binding.OperationId -cne $predecessor.OperationId -or $binding.TargetEpoch -cne $predecessor.TargetEpoch -or
            $binding.ManifestHash -cne $predecessor.ManifestHash -or
            $binding.PreservedInputFingerprint -cne $predecessor.PreservedInputFingerprint -or
            $binding.ConfigurationHash -cne $predecessor.ActivatedConfigHash) { throw 'hybrid-patch-resume-binding-mismatch' }
        if ((Get-HybridPayloadFingerprint -Path $operatorRoot) -cne $binding.OperatorHash -or
            (Get-FileHash -LiteralPath $candidateConfig).Hash -cne $binding.ConfigurationHash) { throw 'hybrid-patch-resume-payload-changed' }
        if (Test-Path -LiteralPath $candidateRoot) {
            if ((Get-HybridPayloadFingerprint -Path $candidateRoot) -cne $binding.CandidateHash) { throw 'hybrid-patch-candidate-changed' }
        }
    }
    else {
        if (Test-Path -LiteralPath $releaseRoot) { throw 'hybrid-patch-release-already-exists' }
        $predecessor=Get-HybridPatchBinding -RecoveryRoot $IncrementalRecoveryRoot -ReleaseId $PredecessorRelease -ConnectionString $connectionString
        Assert-HybridMigrationHistory -OriginalHistory $predecessor.OriginalHistory -CurrentHistory @(Get-AppliedMigrationIds)
        if (@(Get-AppliedMigrationIds)[-1] -cne $contract.Target) { throw 'hybrid-patch-schema-mismatch' }
        Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $predecessor.ReleaseId -OperationId $predecessor.OperationId
        if ((Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $predecessor.CandidateHash -or
            (Get-FileHash -LiteralPath $configurationPath).Hash -cne $predecessor.ActivatedConfigHash -or
            (Get-HybridPreservedInputFingerprint) -cne $predecessor.PreservedInputFingerprint) { throw 'hybrid-patch-live-binding-changed' }
        New-Item -ItemType Directory -Path $releaseRoot | Out-Null
        Copy-Item -LiteralPath $configurationPath -Destination $candidateConfig
        & dotnet publish (Join-Path $SourceRoot 'src/FluxKnowledge.Web/FluxKnowledge.Web.csproj') -c Release --no-restore --nologo -o $candidateRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'hybrid-patch-web-publish-failed' }
        & dotnet publish (Join-Path $SourceRoot 'src/FluxKnowledge.Cli/FluxKnowledge.Cli.csproj') -c Release --no-restore --nologo -o $operatorRoot | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'hybrid-patch-operator-publish-failed' }
        Test-ApplicationPayload -Path $candidateRoot
        Assert-NotReparsePoint -Path $candidateRoot -Message 'hybrid-patch-candidate-root-unsafe'
        $candidateHash=Get-HybridPayloadFingerprint -Path $candidateRoot
        $operatorHash=Get-HybridPayloadFingerprint -Path $operatorRoot
        if ($candidateHash -cne $ExpectedCandidateHash -or $operatorHash -cne $ExpectedOperatorHash) {
            throw 'hybrid-patch-reviewed-payload-hash-mismatch'
        }
        $binding=@{
            Version=1; ReleaseId=$releaseId; Commit=$Commit; OperationId=$predecessor.OperationId
            TargetEpoch=$predecessor.TargetEpoch; ManifestHash=$predecessor.ManifestHash; Predecessor=$predecessor
            CandidateHash=$candidateHash; OperatorHash=$operatorHash
            ConfigurationHash=(Get-FileHash -LiteralPath $candidateConfig).Hash
            PreservedInputFingerprint=$predecessor.PreservedInputFingerprint
        }
        Write-HybridRebuildJson -Path $bindingPath -Value $binding
        $progress=@{ ActivationIntent=$false; HoldReleaseAttempted=$false; HoldReleased=$false }
        Write-HybridRebuildJson -Path $progressPath -Value $progress
    }
    $predecessor=$binding.Predecessor
    Assert-HybridMigrationHistory -OriginalHistory $predecessor.OriginalHistory -CurrentHistory @(Get-AppliedMigrationIds)
    if (@(Get-AppliedMigrationIds)[-1] -cne $contract.Target -or
        (Get-FileHash -LiteralPath $configurationPath).Hash -cne $binding.ConfigurationHash) { throw 'hybrid-patch-runtime-binding-changed' }
    $holdPresent=Test-Path -LiteralPath $ValidationHoldPath
    if (-not $holdPresent -and -not $progress.HoldReleaseAttempted) { throw 'hybrid-patch-hold-missing' }
    $oldOwner=@{ ReleaseId=$predecessor.ReleaseId; OperationId=$binding.OperationId }
    $newOwner=@{ ReleaseId=$releaseId; OperationId=$binding.OperationId }
    if ($holdPresent) {
        $value=Get-Content -LiteralPath $ValidationHoldPath -Raw | ConvertFrom-Json -AsHashtable
        if ($value -is [string] -and $value -ceq $predecessor.ReleaseId) {
            Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $oldOwner.ReleaseId -OperationId $oldOwner.OperationId -Permit $false
        }
        else { Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $newOwner.ReleaseId -OperationId $newOwner.OperationId -Permit $false }
        if ((Get-HybridPreservedInputFingerprint) -cne $binding.PreservedInputFingerprint) { throw 'hybrid-patch-preserved-inputs-changed' }
    }
    $models=Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('verify-models')
    if (-not $models.Verified -or -not $models.Offline) { throw 'hybrid-patch-offline-model-verification-failed' }
    $status=Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$binding.OperationId)
    if (-not $status.Committed -or $status.ManifestHash -cne $binding.ManifestHash -or
        [string]$status.TargetEpoch -cne [string]$binding.TargetEpoch) { throw 'hybrid-patch-operation-receipt-mismatch' }
    if ($status.FailedEmbeddingJobs -gt 0 -or $status.FailedPublishJobs -gt 0) { throw 'hybrid-patch-existing-job-failure' }
    if (-not $holdPresent) {
        if (-not $progress.HoldReleaseAttempted -or -not $status.Completed -or
            (Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $binding.CandidateHash) { throw 'hybrid-patch-hold-missing' }
        Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
        $progress.HoldReleased=$true; Write-HybridRebuildJson -Path $progressPath -Value $progress
    }
    else {
        try {
            Disable-ScheduledTask -TaskName $InteractiveHostTaskName | Out-Null
            Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds $ReadinessTimeoutSeconds
            if ((Get-WebAppPoolState -Name $SiteName).Value -eq 'Stopped' -and (Test-Path -LiteralPath $CanonicalDeployRoot)) {
                Assert-NotReparsePoint -Path $CanonicalDeployRoot -Message 'hybrid-patch-recovery-payload-root-unsafe'
                $compatibleHash='partial-candidate'
                if (@(Get-ChildItem -LiteralPath $CanonicalDeployRoot -File -Recurse -Force).Count -gt 0) {
                    $compatibleHash=Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot
                }
                if ($compatibleHash -eq $predecessor.CandidateHash -or $compatibleHash -eq $binding.CandidateHash) {
                    # The committed worker may have died while holding a native batch.
                    # Start only this verified payload under deny-all so its existing
                    # owner-recovery loop can release capacity, then drain it normally.
                    Start-WebAppPool -Name $SiteName
                    Wait-IisAppPoolState -Name $SiteName -ExpectedState 'Started' -TimeoutSeconds $ReadinessTimeoutSeconds
                    $recoveryLive=Invoke-FixedLoopbackProbe -Uri "$($loopbackOrigin.Origin)/health/live" -TimeoutSeconds $ReadinessTimeoutSeconds
                    $recoveryLive.Dispose()
                }
            }
            Stop-HybridIisAfterGpuDrain
            if ((Get-HybridPreservedInputFingerprint) -cne $binding.PreservedInputFingerprint) { throw 'hybrid-patch-preserved-inputs-changed' }
            if (-not $progress.ActivationIntent) {
                $progress.ActivationIntent=$true
                Write-HybridRebuildJson -Path $progressPath -Value $progress
            }
            if (Test-Path -LiteralPath $previousRoot) {
                Assert-NotReparsePoint -Path $previousRoot -Message 'hybrid-patch-previous-root-unsafe'
                if ((Get-HybridPayloadFingerprint -Path $previousRoot) -cne $predecessor.CandidateHash) { throw 'hybrid-patch-previous-payload-changed' }
            }
            if (Test-Path -LiteralPath $candidateRoot) { Assert-NotReparsePoint -Path $candidateRoot -Message 'hybrid-patch-candidate-root-unsafe' }
            if (Test-Path -LiteralPath $CanonicalDeployRoot) { Assert-NotReparsePoint -Path $CanonicalDeployRoot -Message 'hybrid-patch-live-root-unsafe' }
            $liveHash=if (Test-Path -LiteralPath $CanonicalDeployRoot) {
                if (@(Get-ChildItem -LiteralPath $CanonicalDeployRoot -Recurse -File -Force).Count -eq 0) { 'partial-candidate' }
                else { Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot }
            } else { $null }
            if ($liveHash -cne $binding.CandidateHash) {
                $partial=$liveHash -and $liveHash -cne $predecessor.CandidateHash
                if ($partial -and (-not $progress.ActivationIntent -or -not (Test-Path -LiteralPath $previousRoot) -or
                    -not (Test-Path -LiteralPath $candidateRoot))) {
                    throw 'hybrid-patch-unrecognised-live-payload'
                }
                if ($liveHash -ceq $predecessor.CandidateHash -and (Test-Path -LiteralPath $previousRoot)) { throw 'hybrid-patch-duplicate-old-payload' }
                if (-not $liveHash -and -not (Test-Path -LiteralPath $previousRoot)) { throw 'hybrid-patch-live-payload-missing' }
                if (-not (Test-Path -LiteralPath $candidateRoot) -or
                    (Get-HybridPayloadFingerprint -Path $candidateRoot) -cne $binding.CandidateHash) { throw 'hybrid-patch-candidate-missing-or-changed' }
                if ($liveHash -ceq $predecessor.CandidateHash) { Move-Item -LiteralPath $CanonicalDeployRoot -Destination $previousRoot }
                if ($partial) {
                    Assert-CanonicalPath -RequestedPath $CanonicalDeployRoot -ExpectedPath 'I:\FluxKnowledge\App' -Message 'hybrid-patch-live-path-unsafe'
                    Assert-NotReparsePoint -Path $CanonicalLiveRoot -Message 'hybrid-patch-live-root-unsafe'
                    Assert-NotReparsePoint -Path $CanonicalDeployRoot -Message 'hybrid-patch-live-root-unsafe'
                    foreach ($item in @(Get-ChildItem -LiteralPath $CanonicalDeployRoot -Recurse -Force)) {
                        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'hybrid-patch-partial-payload-reparse-point' }
                    }
                    # Replay must copy every candidate byte, even if an interrupted
                    # file has the expected size and timestamp.
                    $copyOutput=@(& robocopy $candidateRoot $CanonicalDeployRoot /MIR /IS /IT /IM /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NFL /NDL /NP 2>&1)
                    if ($LASTEXITCODE -gt 7) { throw 'hybrid-patch-partial-copy-failed' }
                    Test-ApplicationPayload -Path $CanonicalDeployRoot
                    Assert-ApplicationPayloadReadAccess -Path $CanonicalDeployRoot
                }
                else { Invoke-CandidatePayloadActivation -CandidateRoot $candidateRoot -ApplicationRoot $CanonicalDeployRoot }
            }
            if ((Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $binding.CandidateHash -or
                (Get-HybridPayloadFingerprint -Path $previousRoot) -cne $predecessor.CandidateHash) { throw 'hybrid-patch-activation-unproven' }
            Move-HybridReplacementHold -Path $ValidationHoldPath -Binding $predecessor -ReleaseId $releaseId -OperationId $binding.OperationId
            Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $newOwner.ReleaseId -OperationId $newOwner.OperationId -Permit $true
            Start-WebAppPool -Name $SiteName
            Wait-IisAppPoolState -Name $SiteName -ExpectedState 'Started' -TimeoutSeconds $ReadinessTimeoutSeconds
            $live=Invoke-FixedLoopbackProbe -Uri "$($loopbackOrigin.Origin)/health/live" -TimeoutSeconds $ReadinessTimeoutSeconds
            $live.Dispose()
            $timer=[Diagnostics.Stopwatch]::StartNew()
            while ($true) {
                $status=Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('status','--operation',$binding.OperationId)
                if ($status.Completed) { break }
                if ($status.FailedEmbeddingJobs -gt 0 -or $status.FailedPublishJobs -gt 0) { throw 'hybrid-patch-rebuild-job-failed-hold-retained' }
                if ($status.PendingItems -eq 0 -and $status.RunningItems -eq 0) {
                    try { [void](Invoke-HybridRebuildOperator -OperatorRoot $operatorRoot -ConnectionString $connectionString -Arguments @('finish','--operation',$binding.OperationId)); break }
                    catch { if ($_.Exception.Message -cne 'corpus-rebuild-full-text-not-ready') { throw } }
                }
                if ($timer.Elapsed.TotalSeconds -ge $RebuildTimeoutSeconds) { throw 'hybrid-patch-wait-timeout-hold-retained' }
                Start-Sleep -Milliseconds 1000
            }
            Assert-HybridPatchPacket -RecoveryRoot $IncrementalRecoveryRoot -Binding $predecessor -ConnectionString $connectionString
            if ((Get-HybridPreservedInputFingerprint) -cne $binding.PreservedInputFingerprint) { throw 'hybrid-patch-preserved-inputs-changed' }
            Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            $progress.HoldReleaseAttempted=$true; Write-HybridRebuildJson -Path $progressPath -Value $progress
            Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $newOwner.ReleaseId -OperationId $newOwner.OperationId
            Remove-Item -LiteralPath $ValidationHoldPath -Force
            $progress.HoldReleased=$true; Write-HybridRebuildJson -Path $progressPath -Value $progress
        }
        catch {
            if (Test-Path -LiteralPath $ValidationHoldPath) {
                $value=Get-Content -LiteralPath $ValidationHoldPath -Raw | ConvertFrom-Json -AsHashtable
                if ($value -is [string] -and $value -ceq $predecessor.ReleaseId) {
                    Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $oldOwner.ReleaseId -OperationId $oldOwner.OperationId -Permit $false
                }
                else { Set-HybridDeploymentHold -Path $ValidationHoldPath -ReleaseId $newOwner.ReleaseId -OperationId $newOwner.OperationId -Permit $false }
            }
            throw "Hybrid forward patch requires exact-release recovery $releaseId; no schema/reset/checkpoint change or old-payload restore. Failure: $($_.Exception.Message)"
        }
    }
    if ($predecessor.InteractiveHostWasEnabled) { Enable-ScheduledTask -TaskName $InteractiveHostTaskName | Out-Null }
    [ordered]@{ ok=$true; mode='hybrid-forward-patch-applied'; commit=$Commit; release_root=$releaseRoot;
        operation_id=$binding.OperationId; target_epoch=$binding.TargetEpoch; manifest_hash=$binding.ManifestHash;
        deployment_validation_hold='released-after-same-operation-rebuild-and-input-validation' } | ConvertTo-Json
}

function Assert-StoppedPoolRecoveryBaseline {
    if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') {
        throw 'The FluxKnowledge pool must be stopped for explicit stopped-pool recovery.'
    }
    $appcmd = Join-Path $env:SystemRoot 'System32/inetsrv/appcmd.exe'
    if (@(Get-HybridIisWorkerIds -AppCmdPath $appcmd -PoolName $SiteName).Count -ne 0) {
        throw 'Stopped-pool recovery requires proof that no FluxKnowledge worker remains.'
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
    if ($RecoverStoppedPool) {
        Assert-StoppedPoolRecoveryBaseline
    }
    elseif ((Get-WebAppPoolState -Name $SiteName).Value -ne "Started" -and [string]::IsNullOrWhiteSpace($ResumeHybridRebuildRelease) -and
        [string]::IsNullOrWhiteSpace($ResumeHybridPatchRelease)) {
        throw "The fixed FluxKnowledge IIS application pool must be started before an incremental update."
    }
    $binding = @(Get-WebBinding -Name $SiteName -Protocol "http" | Where-Object { $_.bindingInformation -ceq "127.0.0.1:5137:" })
    if ($binding.Count -ne 1) {
        throw "The fixed FluxKnowledge IIS site must have exactly one http/127.0.0.1:5137 binding."
    }
    if (-not (Test-Path -LiteralPath $CanonicalDeployRoot -PathType Container) -and [string]::IsNullOrWhiteSpace($ResumeHybridRebuildRelease) -and
        [string]::IsNullOrWhiteSpace($ResumeHybridPatchRelease)) {
        throw "The canonical application payload root is missing."
    }
    if (-not (Test-Path -LiteralPath $CanonicalRecoveryRoot -PathType Container)) {
        throw "The canonical recovery root is missing."
    }
    if (Test-Path -LiteralPath $CanonicalDeployRoot) {
        Assert-NotReparsePoint -Path $CanonicalDeployRoot -Message "The canonical application payload root cannot be a reparse point."
    }
    Assert-NotReparsePoint `
        -Path $CanonicalRecoveryRoot `
        -Message "The canonical recovery root cannot be a reparse point."
    if ([string]::IsNullOrWhiteSpace($ResumeHybridRebuildRelease) -and [string]::IsNullOrWhiteSpace($ResumeHybridPatchRelease)) {
        Test-ApplicationPayload -Path $CanonicalDeployRoot
    }
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
if ($EnableUnattendedDiscovery -and ($ApplyMigrations -or $ApplyCorpusChunkFullTextMigration -or
    $ApplyCodeDisclosureProofMigration -or $ApplyHybridPassageRebuild -or $RecoverStoppedPool -or
    $DeferReadinessForScopedRemediation -or $ResumeHybridRebuildRelease -or $ReplaceHybridRebuildRelease -or
    $PatchHybridRebuildRelease -or $ResumeHybridPatchRelease)) {
    throw 'Unattended discovery hosting changes require an ordinary incremental update without migration, rebuild or recovery.'
}
if ($ApplyCodeDisclosureProofMigration -and ($ApplyMigrations -or $ApplyCorpusChunkFullTextMigration -or
    $ApplyHybridPassageRebuild -or $RecoverStoppedPool -or $DeferReadinessForScopedRemediation -or
    $ResumeHybridRebuildRelease -or $ReplaceHybridRebuildRelease -or $PatchHybridRebuildRelease -or $ResumeHybridPatchRelease)) {
    throw 'Code disclosure migration cannot combine with another migration, rebuild, recovery or readiness deferral.'
}
if ($RecoverStoppedPool -and ($ApplyMigrations -or $ApplyCorpusChunkFullTextMigration -or $ApplyHybridPassageRebuild -or
    $ResumeHybridRebuildRelease -or $ReplaceHybridRebuildRelease -or $PatchHybridRebuildRelease -or $ResumeHybridPatchRelease -or
    $DeferReadinessForScopedRemediation)) {
    throw 'Stopped-pool recovery cannot combine with migration, rebuild, hybrid recovery or readiness deferral.'
}
if ($DeferReadinessForScopedRemediation -and $ApplyMigrations) {
    throw "-DeferReadinessForScopedRemediation cannot be combined with -ApplyMigrations."
}
if ($ApplyCorpusChunkFullTextMigration -and ($ApplyMigrations -or $DeferReadinessForScopedRemediation)) {
    throw '-ApplyCorpusChunkFullTextMigration cannot be combined with another migration or readiness deferral.'
}
if (($ApplyHybridPassageRebuild -or $ResumeHybridRebuildRelease) -and ($ApplyMigrations -or $ApplyCorpusChunkFullTextMigration -or $DeferReadinessForScopedRemediation)) {
    throw 'Hybrid passage rebuild cannot be combined with another migration or readiness deferral.'
}
if ($ResumeHybridRebuildRelease -and -not $ApplyHybridPassageRebuild) { throw 'Hybrid resume requires -ApplyHybridPassageRebuild.' }
if ($ReplaceHybridRebuildRelease -and (-not $ApplyHybridPassageRebuild -or $ResumeHybridRebuildRelease)) { throw 'Hybrid replacement requires -ApplyHybridPassageRebuild and cannot combine with resume.' }
if ($ReplaceHybridRebuildRelease -and $ReplaceHybridRebuildRelease -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$') { throw 'Hybrid replacement requires an exact recovery release name.' }
if ($ResumeHybridRebuildRelease -and $ResumeHybridRebuildRelease -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$') { throw 'Hybrid resume requires an exact recovery release name.' }
if (($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease) -and
    (-not $ApplyHybridPassageRebuild -or $ReplaceHybridRebuildRelease -or $ResumeHybridRebuildRelease -or
     ($PatchHybridRebuildRelease -and $ResumeHybridPatchRelease))) { throw 'Hybrid patch requires -ApplyHybridPassageRebuild and one exact patch or patch-resume release.' }
if ($PatchHybridRebuildRelease -and $PatchHybridRebuildRelease -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybrid$') { throw 'Hybrid patch predecessor release invalid.' }
if ($ResumeHybridPatchRelease -and $ResumeHybridPatchRelease -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-hybridpatch$') { throw 'Hybrid patch resume release invalid.' }
if ($PatchHybridRebuildRelease -and ($ExpectedPatchCandidateHash -cnotmatch '^[0-9A-F]{64}$' -or $ExpectedPatchOperatorHash -cnotmatch '^[0-9A-F]{64}$')) {
    throw 'Hybrid patch requires exact reviewed candidate and operator SHA-256 fingerprints.'
}
if (-not $PatchHybridRebuildRelease -and ($ExpectedPatchCandidateHash -or $ExpectedPatchOperatorHash)) { throw 'Reviewed patch hashes only apply to initial patch.' }
if (($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease) -and $RebuildTimeoutSeconds -gt 1800) { throw 'Hybrid patch drain timeout maximum is 1800 seconds.' }
$applyAnyMigration = $ApplyMigrations -or $ApplyCorpusChunkFullTextMigration -or $ApplyCodeDisclosureProofMigration -or ($ApplyHybridPassageRebuild -and -not ($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease))

. (Join-Path $PSScriptRoot "loopback-deployment-safety.ps1")
Import-Module (Join-Path $PSScriptRoot "incremental-iis-payload-swap.psm1") -Force -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'incremental-corpus-fulltext-migration.psm1') -Force -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'incremental-code-disclosure-migration.psm1') -Force -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'incremental-hybrid-passage-rebuild.psm1') -Force -ErrorAction Stop
$loopbackOrigin = Get-FixedLoopbackOrigin -SiteUrl $SiteUrl
if ($loopbackOrigin.Origin -cne "http://127.0.0.1:5137") {
    throw "Incremental IIS deployment requires the fixed http://127.0.0.1:5137 origin."
}

if ($PlanOnly) {
    $migrationPlan = $null
    $hostingPlan = $null
    if ($EnableUnattendedDiscovery) {
        $hostingBefore = Get-IncrementalIisHostingSettings
        if (!$hostingBefore.ModuleEnabled) { throw 'Application Initialization must be enabled before unattended discovery.' }
        $hostingPlan = [ordered]@{
            before = $hostingBefore
            after = @{ StartMode='AlwaysRunning'; IdleTimeout='00:00:00'; PreloadEnabled=$true }
            activation = 'held GPU drain and exact worker exit, then scoped atomic settings before application start'
            rollback = 'restore and verify exact captured settings before predecessor start; drift or uncertainty retains hold'
        }
    }
    if ($ApplyHybridPassageRebuild) {
        $contract = Get-HybridPassageMigrationContract
        $history = @(Get-AppliedMigrationIds)
        if ($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease) {
            $patchRelease=if ($PatchHybridRebuildRelease) { $PatchHybridRebuildRelease } else {
                $patchRoot=Join-Path $IncrementalRecoveryRoot $ResumeHybridPatchRelease
                Assert-NotReparsePoint -Path $patchRoot -Message 'hybrid-patch-plan-root-unsafe'
                $patchBindingPath=Join-Path $patchRoot 'patch-binding.json'
                Assert-NotReparsePoint -Path $patchBindingPath -Message 'hybrid-patch-plan-binding-unsafe'
                (Get-Content -LiteralPath $patchBindingPath -Raw | ConvertFrom-Json -AsHashtable).Predecessor.ReleaseId
            }
            $patch=Get-HybridPatchBinding -RecoveryRoot $IncrementalRecoveryRoot -ReleaseId $patchRelease -ConnectionString (Get-DeploymentSqlConnectionString)
            Assert-HybridMigrationHistory -OriginalHistory $patch.OriginalHistory -CurrentHistory $history
            if ($history[-1] -cne $contract.Target) { throw 'hybrid-patch-plan-schema-mismatch' }
            if ($PatchHybridRebuildRelease) {
                Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $patch.ReleaseId -OperationId $patch.OperationId
            }
            $migrationPlan=[ordered]@{
                kind='same-operation-forward-patch'; migrations=$false; reset=$false; prepare=$false
                predecessor=$patch; resume_patch_release=$ResumeHybridPatchRelease; patch_predecessor_release=$PatchHybridRebuildRelease
                expected_candidate_hash=$ExpectedPatchCandidateHash; expected_operator_hash=$ExpectedPatchOperatorHash
                source_commit=$(if ($SourceRoot) { (& git -C $SourceRoot rev-parse HEAD 2>&1 | Out-String).Trim() } else { $null })
                current_history=$history; schema_target=$contract.Target
                gpu_drain='deny admissions; allow current OCR page; prove capacity release and exact IIS worker exit'
                activation='new verified native-tokenizer-runtime-lifetime payload; same model files and unchanged configuration'
                recovery='immutable predecessor and patch binding; forward-only exact patch release replay; same operation, epoch and checkpoints'
                completion='same-operation finish, preserved inputs, held loopback probes, then hold release'
            }
        }
        elseif ($ResumeHybridRebuildRelease) {
            $resumePath = Join-Path (Join-Path $IncrementalRecoveryRoot $ResumeHybridRebuildRelease) 'hybrid-state.json'
            $resume = Get-Content -LiteralPath $resumePath -Raw | ConvertFrom-Json -AsHashtable
            Assert-HybridMigrationHistory -OriginalHistory $resume.OriginalHistory -CurrentHistory $history
        }
        elseif ($ReplaceHybridRebuildRelease) {
            $replacement = Get-HybridReplacementBinding -RecoveryRoot $IncrementalRecoveryRoot -ReleaseId $ReplaceHybridRebuildRelease -ConnectionString (Get-DeploymentSqlConnectionString)
            Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $replacement.ReleaseId -OperationId $replacement.OperationId
            Assert-HybridMigrationHistory -OriginalHistory $replacement.OriginalHistory -CurrentHistory $history
        }
        else { Assert-HybridMigrationHistory -OriginalHistory $history -CurrentHistory $history }
        if (-not ($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease)) { $migrationPlan = [ordered]@{
            baseline=$contract.Baseline; target=$contract.Target; current_history=$history; generated_up_sha256=$contract.UpSha256
            rebuild='replace disposable chunks, vectors and ANN generations; preserve canonical inputs and published document winners'
            gpu_drain='hold canonical admission; allow active OCR page/native cleanup to finish; prove IIS worker exit before reset'
            admission='only the exact captured rebuild worklist runs while the deployment hold remains'
            recovery='persist forward-only held recovery boundary before first schema command; resume same release, database and operation'
            activation='Search:HybridPassagesEnabled=true; pinned offline BGE-M3 and BGE-reranker-v2-m3; load/run/unload'
            completion='verify SQL/native membership and Full-Text population, preserved input/publication fingerprints and loopback readiness before hold release'
            resume_release=$ResumeHybridRebuildRelease
            replace_release=$ReplaceHybridRebuildRelease
            replacement=$(if ($ReplaceHybridRebuildRelease) { $replacement } else { $null })
        } }
    }
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
    if ($ApplyCorpusChunkFullTextMigration) {
        $contract = Get-CorpusFullTextMigrationContract
        $databaseState = Get-CorpusFullTextDatabaseState
        Assert-CorpusFullTextMigrationBaseline $databaseState
        $migrationPlan = [ordered]@{
            baseline=$contract.Baseline; target=$contract.Target; current_history=@($databaseState.History)
            generated_up_sha256=$contract.UpSha256; generated_down_sha256=$contract.DownSha256
            required_permission='DATABASE ALTER'; prerequisites='Full-Text installed, FluxKnowledge catalogue, unique bigint PK_TextChunks, no existing chunk Full-Text index'
            rollback='before hold release: exact original history, absent chunk Full-Text index, prior payload and probes verified; otherwise hold retained'
            population='asynchronous; verify full population after deployment before claiming Full-Text readiness'
        }
    }
    if ($ApplyCodeDisclosureProofMigration) {
        $contract = Get-CodeDisclosureMigrationContract
        if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
        $SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
        $migrationPath = Join-Path $SourceRoot ("src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/{0}.cs" -f $contract.Target)
        if (!(Test-Path -LiteralPath $migrationPath -PathType Leaf)) { throw 'The reviewed code disclosure migration is missing from SourceRoot.' }
        $databaseState = Get-CodeDisclosureDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
        Assert-CodeDisclosureMigrationBaseline $databaseState
        if ($databaseState.History[-1] -ceq $contract.Baseline -and !$databaseState.CanAlter) { throw 'Code disclosure migration requires DATABASE ALTER permission.' }
        $migrationPlan = [ordered]@{
            baseline=$contract.Baseline; target=$contract.Target; current_history=@($databaseState.History)
            generated_up_sha256=$contract.UpSha256; schema_sha256=$contract.SchemaSha256
            migration_file_sha256=(Get-FileHash -LiteralPath $migrationPath -Algorithm SHA256).Hash
            already_applied=($databaseState.History[-1] -ceq $contract.Target)
            required_permission='DATABASE ALTER for missing schema'
            backfill='automatic model-free derived proofs after validation-hold release; no Publish replay or ANN rebuild'
            rollback='retain additive schema; restore compatible prior payload only after exact old/new schema and prior probes; uncertainty retains hold'
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
        cpu_search_owner_file = Get-CpuSearchOwnerFilePlan
        migrations = [bool]$applyAnyMigration
        migration_plan = $migrationPlan
        clean_slate = $false
        preserved = @("Config", "Data", "Runtime", "Recovery", "CodexPlugin")
        recover_stopped_pool = [bool]$RecoverStoppedPool
        unattended_discovery_hosting = $hostingPlan
        payload_acl = "inherit-from-live-root"
        rollback = if ($RecoverStoppedPool) { 'restore-prior-payloads-with-pool-stopped-and-hold-retained' } elseif ($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease) { 'forward-only after activation intent; predecessor payload/packet retained, hold denied on failure' } elseif ($ApplyHybridPassageRebuild) { 'before-schema: original payload; after-schema: held forward recovery with retained original payload/configuration' } else { "automatic-application-and-interactive-host-payload-restore" }
        gpu_drain = 'deny admissions; allow active OCR page and native cleanup to finish; prove exact IIS worker exit before each payload swap stop'
        deployment_validation_hold = $true
        candidate_validation = if ($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease) { 'same-operation-epoch-checkpoint-continuity-preserved-inputs-and-loopback-probes' } elseif ($ApplyHybridPassageRebuild) { 'held-exact-rebuild-finalisation-preserved-inputs-and-loopback-probes' } elseif ($DeferReadinessForScopedRemediation) {
            "held-live-and-index-health probes, exact-ready-503 and unchanged-retained-pipeline-state"
        }
        else {
            "held-loopback-probes-and-unchanged-retained-pipeline-state"
        }
        readiness_remediation = if ($DeferReadinessForScopedRemediation) {
            "requires exact readiness HTTP 503 under hold; after release accepts HTTP 200 ready or HTTP 503 pending"
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
$corpusMigrationState = $null
$codeDisclosureMigrationState = $null
if ($ApplyMigrations) {
    $migrationPath = Join-Path $SourceRoot ("src\FluxKnowledge.Infrastructure.SqlServer\Persistence\Migrations\{0}.cs" -f $SourceDeletionMigrationTarget)
    if (-not (Test-Path -LiteralPath $migrationPath -PathType Leaf)) {
        throw "The reviewed source-deletion migration is missing from SourceRoot."
    }
    $history = Get-AppliedMigrationIds
    Assert-SourceDeletionMigrationBaseline -AppliedMigrationIds $history
    $migrationPlan = [ordered]@{ Applied = $false; RollbackVerified = $true; Up = $null; Down = $null }
}
if ($ApplyCorpusChunkFullTextMigration) {
    $databaseState = Get-CorpusFullTextDatabaseState
    Assert-CorpusFullTextMigrationBaseline $databaseState
    $corpusMigrationState = New-CorpusFullTextMigrationState -OriginalHistory $databaseState.History
    $migrationPlan = @{ Up=$null; Down=$null }
}
if ($ApplyCodeDisclosureProofMigration) {
    $migrationPath = Join-Path $SourceRoot ("src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations/{0}.cs" -f (Get-CodeDisclosureMigrationContract).Target)
    if (!(Test-Path -LiteralPath $migrationPath -PathType Leaf)) { throw 'The reviewed code disclosure migration is missing from SourceRoot.' }
    $databaseState = Get-CodeDisclosureDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
    Assert-CodeDisclosureMigrationBaseline $databaseState
    if ($databaseState.History[-1] -ceq (Get-CodeDisclosureMigrationContract).Baseline -and !$databaseState.CanAlter) { throw 'Code disclosure migration requires DATABASE ALTER permission.' }
    $codeDisclosureMigrationState = New-CodeDisclosureMigrationState -DatabaseState $databaseState
    $migrationPlan = @{ Up=$null }
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
$interactiveHostRollbackVerified = $true
$holdReleased = $false
$hostingChange = @{ Original=$null; Desired=@{ StartMode='AlwaysRunning'; IdleTimeout='00:00:00'; PreloadEnabled=$true }
    Attempted=$false; RollbackVerified=$true }
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
    if ($EnableUnattendedDiscovery) {
        $hostingChange.Original = Get-IncrementalIisHostingSettings
        if (!$hostingChange.Original.ModuleEnabled) { throw 'Application Initialization must be enabled before unattended discovery.' }
    }
    Ensure-CpuSearchOwnerFile
    if ($ApplyHybridPassageRebuild) {
        if ($PatchHybridRebuildRelease -or $ResumeHybridPatchRelease) {
            Invoke-HybridPassageIisPatch -SourceRoot $SourceRoot -Commit $commit -PredecessorRelease $PatchHybridRebuildRelease -ResumeRelease $ResumeHybridPatchRelease `
                -ExpectedCandidateHash $ExpectedPatchCandidateHash -ExpectedOperatorHash $ExpectedPatchOperatorHash
            return
        }
        Invoke-HybridPassageIisUpdate -SourceRoot $SourceRoot -Commit $commit -ResumeRelease $ResumeHybridRebuildRelease -ReplaceRelease $ReplaceHybridRebuildRelease
        return
    }
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
    if ($EnableUnattendedDiscovery) {
        [ordered]@{ Original=$hostingChange.Original; Desired=$hostingChange.Desired } | ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath (Join-Path $releaseRoot 'iis-hosting-settings.json') -Encoding utf8
    }
    $candidateRoot = Join-Path $releaseRoot "candidate"
    $previousRoot = Join-Path $releaseRoot "previous"
    $failedRoot = Join-Path $releaseRoot "failed"
    $interactiveHostCandidateRoot = Join-Path $releaseRoot "candidate-interactive-host"
    $interactiveHostPreviousRoot = Join-Path $releaseRoot "previous-interactive-host"
    $deploymentValidation = @{ HoldCreated = $false; Baseline = $null; MigrationsApplied = $false; RollbackVerified = $true; PayloadRollbackVerified = $false }

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
    if ($RecoverStoppedPool) {
        # Recovery never starts the failed predecessor just to admit deployment.
        # Deny work and capture exact rollback bytes before either payload changes.
        Assert-StoppedPoolRecoveryBaseline
        $recoveryOriginalPayloadHash = Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot
        $recoveryOriginalInteractiveHostHash = Get-HybridPayloadFingerprint -Path $InteractiveHostRoot
        Stop-HybridIisAfterGpuDrain
        [void](New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId)
        $deploymentValidation.HoldCreated = $true
        $deploymentValidation.Baseline = Get-RetainedPipelineStateBaseline
        [ordered]@{ observed_at_utc=[DateTime]::UtcNow.ToString('O'); pool='Stopped';
            application_hash=$recoveryOriginalPayloadHash; interactive_host_hash=$recoveryOriginalInteractiveHostHash } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseRoot 'stopped-pool-recovery-baseline.json') -Encoding utf8
    }
    $interactiveHostTask = Get-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction Stop
    $interactiveHostTaskWasEnabled = [bool]$interactiveHostTask.Settings.Enabled
    if ($interactiveHostTaskWasEnabled) {
        Disable-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction Stop | Out-Null
    }
    Wait-InteractiveHostStopped -TaskName $InteractiveHostTaskName -TimeoutSeconds $ReadinessTimeoutSeconds
    Copy-InteractiveHostPayload -Source $InteractiveHostRoot -Destination $interactiveHostPreviousRoot
    $interactiveHostMutationStarted = $true
    $interactiveHostRollbackVerified = $false
    Copy-InteractiveHostPayload -Source $interactiveHostCandidateRoot -Destination $InteractiveHostRoot
    if ($ApplyMigrations) {
        $migrationPlan.Up = New-SourceDeletionMigrationScript -SourceRoot $SourceRoot -Direction up -OutputPath (Join-Path $releaseRoot "source-deletion-up.sql")
        $migrationPlan.Down = New-SourceDeletionMigrationScript -SourceRoot $SourceRoot -Direction down -OutputPath (Join-Path $releaseRoot "source-deletion-down.sql")
    }
    if ($ApplyCorpusChunkFullTextMigration) {
        $migrationPlan.Up = New-CorpusFullTextMigrationScript -SourceRoot $SourceRoot -Direction up -OutputPath (Join-Path $releaseRoot 'corpus-fulltext-up.sql')
        $migrationPlan.Down = New-CorpusFullTextMigrationScript -SourceRoot $SourceRoot -Direction down -OutputPath (Join-Path $releaseRoot 'corpus-fulltext-down.sql')
    }
    if ($ApplyCodeDisclosureProofMigration) {
        $migrationPlan.Up = New-CodeDisclosureMigrationScript -SourceRoot $SourceRoot -OutputPath (Join-Path $releaseRoot 'code-disclosure-up.sql')
    }

    $manifest = [ordered]@{
        commit = $commit
        staged_at_utc = [DateTime]::UtcNow.ToString("O")
        application_root = $CanonicalDeployRoot
        migrations = [bool]$applyAnyMigration
        clean_slate = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $releaseRoot "manifest.json"), $manifest, [Text.UTF8Encoding]::new($false))

    $swap = Invoke-IncrementalApplicationPayloadSwap `
        -ApplicationRoot $CanonicalDeployRoot `
        -CandidateRoot $candidateRoot `
        -PreviousRoot $previousRoot `
        -FailedRoot $failedRoot `
        -RestartPreviousApplication:(-not $RecoverStoppedPool) `
        -ActivateCandidate {
            Invoke-CandidatePayloadActivation -CandidateRoot $candidateRoot -ApplicationRoot $CanonicalDeployRoot
            if ($EnableUnattendedDiscovery) {
                $hostingChange.Attempted = $true
                $hostingChange.RollbackVerified = $false
                Set-IncrementalIisHostingSettings -Expected $hostingChange.Original -Desired $hostingChange.Desired
            }
        } `
        -StopApplication {
            Stop-HybridIisAfterGpuDrain
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
            if ($ApplyCorpusChunkFullTextMigration) {
                Invoke-CorpusFullTextMigrationAttempt -State $corpusMigrationState `
                    -ReadState { Get-CorpusFullTextDatabaseState } `
                    -RunUp { Invoke-GeneratedSqlScript -Path $migrationPlan.Up.Path }
            }
            if ($ApplyCodeDisclosureProofMigration) {
                Invoke-CodeDisclosureMigrationAttempt -State $codeDisclosureMigrationState `
                    -ReadState { Get-CodeDisclosureDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString) } `
                    -RunUp { Invoke-GeneratedSqlScript -Path $migrationPlan.Up.Path }
            }
        } `
        -StartApplication {
            $startAttemptUtc = [DateTime]::UtcNow
            Start-WebAppPool -Name $SiteName
            Wait-IisAppPoolState -Name $SiteName -ExpectedState "Started" -TimeoutSeconds $ReadinessTimeoutSeconds
            if ($hostingChange.Attempted -and !$hostingChange.RollbackVerified) {
                $hostingChange.PreloadReceipt = Wait-IncrementalIisPreloadStartup -AfterUtc $startAttemptUtc `
                    -TimeoutSeconds $ReadinessTimeoutSeconds
                $hostingChange.PreloadReceipt | ConvertTo-Json |
                    Set-Content -LiteralPath (Join-Path $releaseRoot 'iis-preload-startup.json') -Encoding utf8
            }
        } `
        -ValidateApplication {
            if ($EnableUnattendedDiscovery -and
                !(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $hostingChange.Desired)) {
                throw 'The activated unattended discovery hosting settings changed.'
            }
            if ($DeferReadinessForScopedRemediation) {
                [void](Invoke-ScopedReadinessRemediationProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds)
            }
            else {
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            Assert-RetainedPipelineStateUnchanged `
                -Baseline $deploymentValidation.Baseline `
                -Current (Get-RetainedPipelineStateBaseline)
        } `
        -ValidateRollbackApplication {
            if ($hostingChange.Attempted -and
                !(Test-IisHostingSettingsMatch (Get-IncrementalIisHostingSettings) $hostingChange.Original)) {
                throw 'The prior IIS hosting settings were not restored; hold retained.'
            }
            if ($RecoverStoppedPool) {
                Assert-StoppedPoolRecoveryBaseline
                if ((Get-HybridPayloadFingerprint -Path $CanonicalDeployRoot) -cne $recoveryOriginalPayloadHash) {
                    throw 'Stopped-pool recovery did not restore the exact prior application bytes.'
                }
            }
            elseif ($ApplyCodeDisclosureProofMigration) {
                Confirm-CodeDisclosureMigrationRecovery -State $codeDisclosureMigrationState `
                    -ReadState { Get-CodeDisclosureDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString) } `
                    -ValidatePriorApplication {
                        Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
                        Assert-RetainedPipelineStateUnchanged -Baseline $deploymentValidation.Baseline -Current (Get-RetainedPipelineStateBaseline)
                    }
            }
            elseif ($ApplyCorpusChunkFullTextMigration) {
                Confirm-CorpusFullTextMigrationRollback -State $corpusMigrationState -ValidatePriorApplication {
                    Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
                    Assert-RetainedPipelineStateUnchanged -Baseline $deploymentValidation.Baseline -Current (Get-RetainedPipelineStateBaseline)
                }
            }
            elseif ($DeferReadinessForScopedRemediation) {
                [void](Invoke-ScopedReadinessRemediationProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds)
            }
            else {
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            Assert-RetainedPipelineStateUnchanged `
                -Baseline $deploymentValidation.Baseline `
                -Current (Get-RetainedPipelineStateBaseline)
            $deploymentValidation.PayloadRollbackVerified = $true
        } `
        -PrepareRollbackApplication {
            if ($hostingChange.Attempted) {
                Restore-IncrementalIisHostingSettings -Original $hostingChange.Original -Desired $hostingChange.Desired
                $hostingChange.RollbackVerified = $true
            }
            if ($ApplyCodeDisclosureProofMigration) {
                Confirm-CodeDisclosureSchemaRecovery -State $codeDisclosureMigrationState `
                    -ReadState { Get-CodeDisclosureDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString) }
            }
            if ($ApplyCorpusChunkFullTextMigration) {
                Undo-CorpusFullTextMigrationAttempt -State $corpusMigrationState `
                    -ReadState { Get-CorpusFullTextDatabaseState } `
                    -RunDown { Invoke-GeneratedSqlScript -Path $migrationPlan.Down.Path }
            }
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
    # Commit boundary: workers may now write. No automatic schema or mixed-payload rollback after this point.
    $holdReleased = $true
    if ($DeferReadinessForScopedRemediation) {
        $postHoldReadiness = Invoke-ScopedReadinessRemediationProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds -AfterHoldRelease
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
        unattended_discovery_hosting = if ($EnableUnattendedDiscovery) { Get-IncrementalIisHostingSettings } else { $null }
        unattended_discovery_startup = $hostingChange.PreloadReceipt
        migrations = [bool]$applyAnyMigration
        migration = if ($applyAnyMigration) { [ordered]@{ target = $migrationPlan.Up.To; script_sha256 = $migrationPlan.Up.Sha256 } } else { $null }
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
            $postHoldReadiness
        }
        else {
            $null
        }
    } | ConvertTo-Json -Depth 3
}
catch {
    $deploymentFailure = $_
    if ($holdReleased) {
        throw "Deployment activated and the validation hold was released, but a post-activation check failed. Current application and schema are retained; do not replay the one-time migration. Inspect recovery release $releaseRoot. Failure: $($_.Exception.Message)"
    }
    if ($interactiveHostMutationStarted) {
        try {
            if ($null -eq $interactiveHostPreviousRoot -or
                -not (Test-Path -LiteralPath $interactiveHostPreviousRoot -PathType Container)) {
                throw "Deployment failed after the interactive-host payload was mutated, and no rollback payload is available. The scheduled task remains disabled."
            }
            if ($RecoverStoppedPool) { Assert-StoppedPoolRecoveryBaseline }
            Test-InteractiveHostPayload -Path $interactiveHostPreviousRoot
            Restore-InteractiveHostPayload -PreviousRoot $interactiveHostPreviousRoot -LiveRoot $InteractiveHostRoot
            Test-InteractiveHostPayload -Path $InteractiveHostRoot
            if ($RecoverStoppedPool -and
                (Get-HybridPayloadFingerprint -Path $InteractiveHostRoot) -cne $recoveryOriginalInteractiveHostHash) {
                throw 'Stopped-pool recovery did not restore the exact prior interactive-host bytes.'
            }
        }
        catch {
            if ($RecoverStoppedPool) {
                throw "Stopped-pool recovery failed; application rollback verified=$($deploymentValidation.PayloadRollbackVerified); companion restored=False; hold retained; recovery=$releaseRoot. Original failure: $($deploymentFailure.Exception.Message) Companion restoration failure: $($_.Exception.Message)"
            }
            throw
        }
        $interactiveHostRollbackVerified = $true
        if ($RecoverStoppedPool) {
            throw "Stopped-pool recovery failed; application rollback verified=$($deploymentValidation.PayloadRollbackVerified); companion restored=True; hold retained; recovery=$releaseRoot. Original failure: $($deploymentFailure.Exception.Message)"
        }
        throw "Deployment failed after the interactive-host payload was mutated. The prior payload was restored and the scheduled task remains disabled for operator review. Original failure: $($deploymentFailure.Exception.Message)"
    }
    if ($interactiveHostTaskWasEnabled -and -not $interactiveHostMutationStarted) {
        Enable-ScheduledTask -TaskName $InteractiveHostTaskName -ErrorAction SilentlyContinue | Out-Null
    }
    throw
}
finally {
    if ($leaseAcquired -and $null -ne $deploymentValidation -and
        (Test-IncrementalRollbackHoldRelease -Validation $deploymentValidation `
            -ApplyMigrations ([bool]$ApplyMigrations) `
            -ApplyCorpusChunkFullTextMigration ([bool]$ApplyCorpusChunkFullTextMigration) `
            -CorpusRollbackVerified $(if ($ApplyCorpusChunkFullTextMigration) { [bool]$corpusMigrationState.RollbackVerified } else { $true }) `
            -ApplyCodeDisclosureProofMigration ([bool]$ApplyCodeDisclosureProofMigration) `
            -CodeDisclosureRecoveryVerified $(if ($ApplyCodeDisclosureProofMigration) { [bool]$codeDisclosureMigrationState.RollbackVerified } else { $true }) `
            -InteractiveHostMutationStarted $interactiveHostMutationStarted `
            -InteractiveHostRollbackVerified $interactiveHostRollbackVerified `
            -RecoverStoppedPool ([bool]$RecoverStoppedPool) `
            -HostingRollbackVerified ([bool]$hostingChange.RollbackVerified))) {
        Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId
    }
    if ($leaseAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
