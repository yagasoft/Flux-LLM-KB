[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "C:\inetpub\FluxKnowledge",
    [string]$BackupRoot = "C:\FluxKnowledgeBackups",
    [switch]$ApplyMigrations,
    [switch]$ConfirmApplyMigrations,
    [int]$ReadinessTimeoutSeconds = 120,
    [switch]$PlanOnly,
    [switch]$PreflightOnly
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}

$SchedulerMigrationTargetId = "20260805112341_AddGpuExecutorDispatchAndReceipts"
$SchedulerMigrationIds = @(
    "20260729080641_AddGpuSchedulerDurability",
    "20260729094809_AddGpuSchedulerOperationReceipts",
    "20260729103104_CompleteGpuSchedulerOperationReceipts",
    "20260729120305_AddGpuSchedulerOperationReceiptRequestFingerprint",
    "20260802182703_AddGpuSchedulerBinaryFenceCollation",
    "20260802191240_AddGpuSchedulerOpaqueKeyCanonicality",
    $SchedulerMigrationTargetId
)
$Phase3AMigrationTargetId = "20260808191700_AddRetainedTextPipelineLink"
$Phase3AMigrationIds = @(
    "20260806120000_AddPhase3ALocalSources",
    $Phase3AMigrationTargetId
)
$Phase3BMigrationTargetId = "20260809110000_AddPhase3BWatcherCorpusEvents"
$Phase3BMigrationIds = @($Phase3BMigrationTargetId)
$NativeWorkerSupervisionMigrationTargetId = "20260810185641_AddNativeWorkerSupervision"
$NativeWorkerSupervisionMigrationIds = @($NativeWorkerSupervisionMigrationTargetId)
$NativeOutlookIngressBaselineMigrationId = "20260811093501_AddNativeOutlookIngress"
$NativeOutlookIngressMigrationIds = @(
    $NativeOutlookIngressBaselineMigrationId,
    "20260811094729_HardenNativeOutlookIngress",
    "20260811100247_FixOutlookPrivateIdentityColumns",
    "20260811101550_EnforceOutlookCaptureIdentityFences",
    "20260811105928_HardenOutlookCaptureReplay",
    "20260811112742_BindOutlookExportClaimIdentity",
    "20260811132655_BindOutlookProfileSourceRoot",
    "20260811133300_AlignDeferredCapabilityFingerprintCollation",
    "20260811143122_RecordOutlookExportBlockedReason",
    "20260811152249_AllowIdentitylessBlockedOutlookExports",
    "20260812102333_AddOutlookBrowseTargetPath"
)
$NativeOutlookIngressMigrationTargetId = $NativeOutlookIngressMigrationIds[-1]
$RequiredDeploymentMigrationIds = @(
    $SchedulerMigrationIds +
    $Phase3AMigrationIds +
    $Phase3BMigrationIds +
    $NativeWorkerSupervisionMigrationIds +
    $NativeOutlookIngressMigrationIds)
$RequiredBaselineMigrationIds = @(
    "20260726215521_InitialPhase1",
    "20260726221653_EnforceCanonicalSqlSafety",
    "20260726235718_AddIndexGenerationMembership",
    "20260727055755_DistinguishVectorIdentityAndPayloadChecksum"
)

if ($PreflightOnly -and $PlanOnly) {
    throw "-PreflightOnly cannot be combined with -PlanOnly."
}
if ($SiteName -cne "FluxKnowledge") {
    throw "Native deployment is restricted to the fixed FluxKnowledge IIS site."
}

if ($PlanOnly) {
    [ordered]@{
        mode = "plan-only"
        required_site = "FluxKnowledge"
        loopback_only = $true
        requires_explicit_migration_confirmation = $true
        required_baseline_migration_ids = $RequiredBaselineMigrationIds
        scheduler_migration_ids = $SchedulerMigrationIds
        scheduler_migration_target = $SchedulerMigrationTargetId
        phase3a_migration_ids = $Phase3AMigrationIds
        phase3b_migration_ids = $Phase3BMigrationIds
        native_worker_supervision_migration_ids = $NativeWorkerSupervisionMigrationIds
        native_outlook_ingress_migration_ids = $NativeOutlookIngressMigrationIds
        native_outlook_ingress_baseline_migration = $NativeOutlookIngressBaselineMigrationId
        deployment_migration_target = $NativeOutlookIngressMigrationTargetId
        post_deploy_validator = "validate-native-outlook-ingress.ps1"
        outlook_host_activation = $false
        windows_service_registration = $false
        iis_anonymous_authentication_required = $true
        iis_windows_authentication_prohibited = $true
        source_artifact_store_requires_app_pool_modify_access = $true
        source_artifact_store_acl_rejects_protected_root_overlap = $true
        required_endpoints = @(
            "/health/live",
            "/health/ready",
            "/api/index-health",
            "/api/gpu-status",
            "/api/search?query=native%20deployment"
        )
        prohibited_components = @("python", "docker", "rabbitmq", "vespa", "model", "gpu-runtime")
    } | ConvertTo-Json -Depth 5
    exit 0
}

function Get-NormalisedPath {
    param([string]$Path)

    return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-LoopbackSqlServer {
    param([string]$DataSource)

    $value = $DataSource.Trim() -replace '^(?i:tcp:)', ''
    $server = $value.Split(',')[0].Split('\')[0].Trim()
    return $server -in @("localhost", ".", "(local)", "127.0.0.1", "::1", "[::1]", "(localdb)")
}

function Get-LocalProductionConnection {
    param([string]$ConfigurationPath)

    $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
    $connectionString = [string]$configuration.ConnectionStrings.FluxKnowledge
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "The production settings file does not contain ConnectionStrings:FluxKnowledge."
    }

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($connectionString)
    $server = $builder.DataSource
    $catalog = $builder.InitialCatalog

    if (-not (Test-LoopbackSqlServer -DataSource $server)) {
        throw "The production SQL Server target is not loopback-local."
    }
    if ($catalog -ne "FluxKnowledge") {
        throw "The production SQL catalog is not FluxKnowledge."
    }
    if (-not $builder.IntegratedSecurity -or $builder.UserID -or $builder.Password) {
        throw "The native deployment requires integrated local SQL authentication without stored credentials."
    }

    return [pscustomobject]@{
        ConnectionString = $connectionString
        Server = $server
        Catalog = $catalog
    }
}

function Get-SourceArtifactStoreRoot {
    param([string]$ConfigurationPath)

    $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
    $configuredRoot = [string]$configuration.SourceArtifactStore.Root
    if ([string]::IsNullOrWhiteSpace($configuredRoot)) {
        $configuredRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) "FluxKnowledge\source-artifacts"
    }

    return [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($configuredRoot))
}

function Assert-AnonymousIisAuthentication {
    param([string]$ApplicationName)

    $appCmd = Join-Path $env:windir "System32\inetsrv\appcmd.exe"
    if (-not (Test-Path -LiteralPath $appCmd -PathType Leaf)) {
        throw "IIS appcmd.exe is required to validate local Outlook operator authentication."
    }

    foreach ($path in @("", "/outlook", "/_blazor")) {
        $target = "$ApplicationName$path"
        $anonymous = (& $appCmd list config $target /section:anonymousAuthentication 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $anonymous -notmatch 'enabled="true"') {
            throw "IIS anonymous authentication must remain enabled for $target."
        }

        $windows = (& $appCmd list config $target /section:windowsAuthentication 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $windows -notmatch 'enabled="false"') {
            throw "IIS Windows authentication must remain disabled for $target."
        }
    }
}

function Get-SourceArtifactStoreProtectedRoots {
    param(
        [string]$ConfigurationPath,
        [string]$DeploymentRoot
    )

    $configuration = Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
    $configuredRoots = @(
        $configuration.SourceRootPolicy.ProtectedRoots,
        $configuration.SourceRootPolicy.SecretRoots,
        $configuration.SourceRootPolicy.CacheRoots,
        $configuration.SourceArtifactStore.ProtectedRoots
    ) | ForEach-Object { @($_) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }

    return @(
        $DeploymentRoot,
        "I:\FluxKnowledge\Sql\Data",
        "I:\FluxKnowledge\Sql\Log",
        [string]$configuration.Usearch.RootPath,
        $configuredRoots
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$_)).TrimEnd([char[]]@('\', '/')) }
}

function Test-PathOverlap {
    param(
        [string]$FirstPath,
        [string]$SecondPath
    )

    $first = [System.IO.Path]::GetFullPath($FirstPath).TrimEnd([char[]]@('\', '/'))
    $second = [System.IO.Path]::GetFullPath($SecondPath).TrimEnd([char[]]@('\', '/'))
    return $first.Equals($second, [System.StringComparison]::OrdinalIgnoreCase) -or
        $first.StartsWith("$second$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase) -or
        $second.StartsWith("$first$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase)
}

function Grant-ApplicationPoolModifyAccess {
    param(
        [string]$Path,
        [string]$ApplicationPoolName,
        [string[]]$ProtectedRoots
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
    if ([string]::IsNullOrWhiteSpace((Split-Path -Parent $fullPath))) {
        throw "The source artifact store root must not be a filesystem root."
    }
    foreach ($protectedRoot in $ProtectedRoots) {
        if (Test-PathOverlap -FirstPath $fullPath -SecondPath $protectedRoot) {
            throw "The source artifact store root must not overlap a protected root."
        }
    }

    if (-not (Test-Path -LiteralPath $fullPath)) {
        New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
    }

    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The source artifact store root must not be a reparse point."
    }

    $identity = "IIS AppPool\$ApplicationPoolName"
    $icacls = (Get-Command icacls.exe -ErrorAction Stop).Source
    & $icacls $item.FullName "/grant:r" "${identity}:(OI)(CI)(M,DC)" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to grant the IIS application pool Modify access to the source artifact store."
    }
}

function Assert-LoopbackIisTarget {
    param(
        [string]$Name,
        [string]$ExpectedUrl,
        [string]$ExpectedDeployRoot
    )

    $expectedUri = [Uri]$ExpectedUrl
    if (-not $expectedUri.IsLoopback) {
        throw "The requested IIS URL is not loopback-only."
    }

    Import-Module WebAdministration -ErrorAction Stop
    $site = Get-Website -Name $Name -ErrorAction Stop
    if ($site.applicationPool -ne $Name) {
        throw "The IIS site does not use its dedicated $Name application pool."
    }

    $siteRoot = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)
    if ((Get-NormalisedPath -Path $siteRoot) -ne (Get-NormalisedPath -Path $ExpectedDeployRoot)) {
        throw "The IIS site physical path does not match the requested deployment directory."
    }

    $foundExpectedBinding = $false
    $bindings = @(Get-WebBinding -Name $Name -ErrorAction Stop)
    if ($bindings.Count -eq 0) {
        throw "The IIS site has no bindings."
    }

    foreach ($binding in $bindings) {
        if ($binding.protocol -notin @("http", "https")) {
            throw "The IIS site has a non-HTTP binding."
        }

        $information = [string]$binding.bindingInformation
        if ($information -notmatch '^(?<address>127\.0\.0\.1|\[::1\]):(?<port>\d+):(?<host>.*)$') {
            throw "The IIS site has a wildcard or external binding."
        }

        $hostHeader = $Matches.host
        $bindingMatchesUrl =
            $binding.protocol -eq $expectedUri.Scheme -and
            [int]$Matches.port -eq $expectedUri.Port -and
            (($expectedUri.Host -eq "127.0.0.1" -and $Matches.address -eq "127.0.0.1") -or
             ($expectedUri.Host -eq "::1" -and $Matches.address -eq "[::1]")) -and
            ([string]::IsNullOrWhiteSpace($hostHeader) -or
             $hostHeader.Equals($expectedUri.Host, [System.StringComparison]::OrdinalIgnoreCase))
        if ($bindingMatchesUrl) {
            $foundExpectedBinding = $true
        }
    }

    if (-not $foundExpectedBinding) {
        throw "The IIS site has no binding for the requested loopback URL."
    }

    $poolState = (Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value
    if ($poolState -ne "Started") {
        throw "The dedicated IIS application pool must be started before deployment."
    }
}

function Wait-ForAppPoolState {
    param(
        [string]$Name,
        [string]$ExpectedState,
        [int]$TimeoutSeconds = 60
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ((Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value -eq $ExpectedState) {
            return
        }

        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The $Name application pool did not reach $ExpectedState within $TimeoutSeconds seconds."
}

function Invoke-LocalSqlCommand {
    param(
        [string]$Sql,
        [string]$Server,
        [string]$Database
    )

    $sqlcmd = (Get-Command sqlcmd -ErrorAction Stop).Source
    $output = & $sqlcmd -S $Server -E -d $Database -b -l 30 -Q $Sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The local SQL command failed: $($output | Out-String)"
    }

    return @($output)
}

function Get-AppliedMigrationIds {
    param(
        [string]$Server,
        [string]$Database
    )

    $output = Invoke-LocalSqlCommand -Server $Server -Database $Database -Sql @'
SET NOCOUNT ON;
SELECT [MigrationId]
FROM [dbo].[__EFMigrationsHistory]
ORDER BY [MigrationId];
'@
    return @($output | Where-Object { $_ -match '^\d{14}_.+$' } | ForEach-Object { $_.Trim() })
}

function Invoke-EndpointProbe {
    param(
        [string]$Uri,
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastFailure = $null
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -TimeoutSec 15
            if ($response.StatusCode -eq 200) {
                return
            }

            $lastFailure = "HTTP $($response.StatusCode)"
        } catch {
            $lastFailure = $_.Exception.Message
        }

        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The loopback endpoint did not return 200 within $TimeoutSeconds seconds: $Uri. Last failure: $lastFailure"
}

function Remove-UnplacedStagingDirectory {
    param(
        [string]$Path,
        [string]$Parent,
        [string]$Leaf
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $normalisedPath = Get-NormalisedPath -Path $Path
    $normalisedParent = Get-NormalisedPath -Path $Parent
    $prefix = "$normalisedParent$([System.IO.Path]::DirectorySeparatorChar)$Leaf.staging-"
    if (-not $normalisedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected staging path."
    }

    Remove-Item -LiteralPath $normalisedPath -Recurse -Force
}

if ($ConfirmApplyMigrations -and -not $ApplyMigrations) {
    throw "-ConfirmApplyMigrations requires -ApplyMigrations."
}
if ($ApplyMigrations -and -not $ConfirmApplyMigrations) {
    throw "-ApplyMigrations requires -ConfirmApplyMigrations."
}
if ($ReadinessTimeoutSeconds -lt 10) {
    throw "Readiness timeout must be at least 10 seconds."
}

$SourceRoot = Get-NormalisedPath -Path $SourceRoot
$DeployRoot = Get-NormalisedPath -Path $DeployRoot
$deployParent = Split-Path -Parent $DeployRoot
$deployLeaf = Split-Path -Leaf $DeployRoot
$webProject = Join-Path $SourceRoot "src\FluxKnowledge.Web\FluxKnowledge.Web.csproj"
$cliProject = Join-Path $SourceRoot "src\FluxKnowledge.Cli\FluxKnowledge.Cli.csproj"
if (-not (Test-Path -LiteralPath $webProject) -or -not (Test-Path -LiteralPath $cliProject)) {
    throw "The native web and CLI projects must exist under SourceRoot."
}
$sha256Helper = Join-Path $PSScriptRoot "get-sha256.ps1"
if (-not (Test-Path -LiteralPath $sha256Helper -PathType Leaf)) {
    throw "The native deployment SHA-256 helper is missing."
}

Assert-LoopbackIisTarget -Name $SiteName -ExpectedUrl $SiteUrl -ExpectedDeployRoot $DeployRoot
$targetSettings = @(Get-ChildItem -LiteralPath $DeployRoot -File -Filter "appsettings*.json" -ErrorAction Stop)
$productionSettings = $targetSettings | Where-Object { $_.Name -eq "appsettings.Production.json" } | Select-Object -First 1
if ($null -eq $productionSettings) {
    throw "The deployment directory has no target-only appsettings.Production.json file."
}
$productionConnection = Get-LocalProductionConnection -ConfigurationPath $productionSettings.FullName
$sourceArtifactStoreRoot = Get-SourceArtifactStoreRoot -ConfigurationPath $productionSettings.FullName
$sourceArtifactStoreProtectedRoots = Get-SourceArtifactStoreProtectedRoots -ConfigurationPath $productionSettings.FullName -DeploymentRoot $DeployRoot
$preflightMigrationIds = @()
if ($ApplyMigrations) {
    $preflightMigrationIds = Get-AppliedMigrationIds -Server $productionConnection.Server -Database $productionConnection.Catalog
    $missingPreflightBaselineMigrations = @($RequiredBaselineMigrationIds | Where-Object { $_ -notin $preflightMigrationIds })
    if ($missingPreflightBaselineMigrations.Count -gt 0) {
        throw "The local SQL catalog is missing required baseline migrations: $($missingPreflightBaselineMigrations -join ', ')."
    }
}

if ($PreflightOnly) {
    [ordered]@{
        ok = $true
        mode = "preflight-only"
        site = $SiteName
        site_url = $SiteUrl
        deployment_root = $DeployRoot
        preserved_settings_file_count = $targetSettings.Count
        scheduler_migrations_expected = $SchedulerMigrationIds
        phase3a_migrations_expected = $Phase3AMigrationIds
        phase3b_migrations_expected = $Phase3BMigrationIds
        native_worker_supervision_migrations_expected = $NativeWorkerSupervisionMigrationIds
        native_outlook_ingress_migrations_expected = $NativeOutlookIngressMigrationIds
        deployment_migration_target = $NativeOutlookIngressMigrationTargetId
        migration_update_requested = [bool]$ApplyMigrations
        baseline_migrations_present = @($RequiredBaselineMigrationIds | Where-Object { $_ -in $preflightMigrationIds })
    } | ConvertTo-Json -Depth 5
    exit 0
}

$deploymentId = "{0:yyyyMMdd-HHmmss}-{1}" -f [DateTime]::UtcNow, [Guid]::NewGuid().ToString("N")
$stagingRoot = Join-Path $deployParent "$deployLeaf.staging-$deploymentId"
$rollbackRoot = Join-Path $deployParent "$deployLeaf.rollback-$deploymentId"
$backupPath = $null
$poolStopped = $false
$payloadSwapped = $false
$oldPayloadMoved = $false
$migrationIdsBefore = @()
$migrationIdsAfter = @()
$locationPushed = $false

Push-Location $SourceRoot
$locationPushed = $true
try {
    New-Item -ItemType Directory -Path $stagingRoot -ErrorAction Stop | Out-Null
    & dotnet publish $webProject -c Release --no-restore -o $stagingRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Native web publish failed with exit code $LASTEXITCODE."
    }

    $stagedAssembly = Join-Path $stagingRoot "FluxKnowledge.Web.dll"
    if (-not (Test-Path -LiteralPath $stagedAssembly) -or -not (Test-Path -LiteralPath (Join-Path $stagingRoot "web.config"))) {
        throw "The staged native publish does not contain the expected web assembly and IIS configuration."
    }
    foreach ($settingsFile in $targetSettings) {
        Copy-Item -LiteralPath $settingsFile.FullName -Destination (Join-Path $stagingRoot $settingsFile.Name) -Force
    }

    $stagedAssemblyHash = (& $sha256Helper -LiteralPath $stagedAssembly).Trim()
    $migrationIdsBefore = Get-AppliedMigrationIds -Server $productionConnection.Server -Database $productionConnection.Catalog

    if ($ApplyMigrations) {
        $missingBaselineMigrations = @($RequiredBaselineMigrationIds | Where-Object { $_ -notin $migrationIdsBefore })
        if ($missingBaselineMigrations.Count -gt 0) {
            throw "The local SQL catalog is missing required baseline migrations: $($missingBaselineMigrations -join ', ')."
        }
        New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
        $backupPath = Join-Path $BackupRoot "FluxKnowledge-$deploymentId.bak"
        $escapedBackupPath = $backupPath.Replace("'", "''")
        Invoke-LocalSqlCommand -Server $productionConnection.Server -Database "master" -Sql (
            "BACKUP DATABASE [FluxKnowledge] TO DISK = N'$escapedBackupPath' WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;") | Out-Host
        Invoke-LocalSqlCommand -Server $productionConnection.Server -Database "master" -Sql (
            "RESTORE VERIFYONLY FROM DISK = N'$escapedBackupPath' WITH CHECKSUM;") | Out-Host
    }

    Stop-WebAppPool -Name $SiteName -ErrorAction Stop
    $poolStopped = $true
    Wait-ForAppPoolState -Name $SiteName -ExpectedState "Stopped"

    Grant-ApplicationPoolModifyAccess -Path $sourceArtifactStoreRoot -ApplicationPoolName $SiteName -ProtectedRoots $sourceArtifactStoreProtectedRoots

    if ($ApplyMigrations) {
        $previousConnection = $env:ConnectionStrings__FluxKnowledge
        try {
            $env:ConnectionStrings__FluxKnowledge = $productionConnection.ConnectionString
        & dotnet tool run dotnet-ef -- database update $NativeOutlookIngressMigrationTargetId --project "src/FluxKnowledge.Infrastructure.SqlServer/FluxKnowledge.Infrastructure.SqlServer.csproj" --configuration Release --no-build --connection $productionConnection.ConnectionString
            if ($LASTEXITCODE -ne 0) {
                throw "The explicitly confirmed native SQL migration update failed with exit code $LASTEXITCODE."
            }
        } finally {
            if ($null -eq $previousConnection) {
                Remove-Item Env:\ConnectionStrings__FluxKnowledge -ErrorAction SilentlyContinue
            } else {
                $env:ConnectionStrings__FluxKnowledge = $previousConnection
            }
        }

        $migrationIdsAfter = Get-AppliedMigrationIds -Server $productionConnection.Server -Database $productionConnection.Catalog
        $missingMigrations = @($RequiredDeploymentMigrationIds | Where-Object { $_ -notin $migrationIdsAfter })
        if ($missingMigrations.Count -gt 0) {
            throw "The migration update completed without all required deployment migrations: $($missingMigrations -join ', ')."
        }
    }

    Move-Item -LiteralPath $DeployRoot -Destination $rollbackRoot -ErrorAction Stop
    $oldPayloadMoved = $true
    try {
        Move-Item -LiteralPath $stagingRoot -Destination $DeployRoot -ErrorAction Stop
        $payloadSwapped = $true
    } catch {
        Move-Item -LiteralPath $rollbackRoot -Destination $DeployRoot -ErrorAction Stop
        $oldPayloadMoved = $false
        throw
    }

    Assert-AnonymousIisAuthentication -ApplicationName $SiteName
    Start-WebAppPool -Name $SiteName -ErrorAction Stop
    Wait-ForAppPoolState -Name $SiteName -ExpectedState "Started"
    $poolStopped = $false

    $deployedAssemblyHash = (& $sha256Helper -LiteralPath (Join-Path $DeployRoot "FluxKnowledge.Web.dll")).Trim()
    if ($deployedAssemblyHash -ne $stagedAssemblyHash) {
        throw "The deployed application assembly does not match the verified staged payload."
    }

    Invoke-EndpointProbe -Uri "$SiteUrl/health/live" -TimeoutSeconds $ReadinessTimeoutSeconds
    Invoke-EndpointProbe -Uri "$SiteUrl/health/ready" -TimeoutSeconds $ReadinessTimeoutSeconds
    Invoke-EndpointProbe -Uri "$SiteUrl/api/index-health" -TimeoutSeconds 30
    Invoke-EndpointProbe -Uri "$SiteUrl/api/gpu-status" -TimeoutSeconds 30
    Invoke-EndpointProbe -Uri "$SiteUrl/api/search?query=native%20deployment" -TimeoutSeconds 30

    $previousConnection = $env:ConnectionStrings__FluxKnowledge
    try {
        $env:ConnectionStrings__FluxKnowledge = $productionConnection.ConnectionString
        & dotnet run --project $cliProject -c Release --no-build -- validate-sql
        if ($LASTEXITCODE -ne 0) {
            throw "Native SQL readiness validation failed with exit code $LASTEXITCODE."
        }
    } finally {
        if ($null -eq $previousConnection) {
            Remove-Item Env:\ConnectionStrings__FluxKnowledge -ErrorAction SilentlyContinue
        } else {
            $env:ConnectionStrings__FluxKnowledge = $previousConnection
        }
    }

    [ordered]@{
        ok = $true
        site = $SiteName
        site_url = $SiteUrl
        backup_path = $backupPath
        rollback_payload_path = $rollbackRoot
        scheduler_migrations_applied = @($SchedulerMigrationIds | Where-Object { $_ -in $migrationIdsAfter })
        phase3a_migrations_applied = @($Phase3AMigrationIds | Where-Object { $_ -in $migrationIdsAfter })
        phase3b_migrations_applied = @($Phase3BMigrationIds | Where-Object { $_ -in $migrationIdsAfter })
        native_worker_supervision_migrations_applied = @($NativeWorkerSupervisionMigrationIds | Where-Object { $_ -in $migrationIdsAfter })
        native_outlook_ingress_migrations_applied = @($NativeOutlookIngressMigrationIds | Where-Object { $_ -in $migrationIdsAfter })
        deployment_migration_target = $NativeOutlookIngressMigrationTargetId
        deployed_assembly_sha256 = $deployedAssemblyHash
        endpoint_status = "200"
    } | ConvertTo-Json -Depth 5
} catch {
    if (-not $payloadSwapped -and $oldPayloadMoved -and (Test-Path -LiteralPath $rollbackRoot) -and -not (Test-Path -LiteralPath $DeployRoot)) {
        Move-Item -LiteralPath $rollbackRoot -Destination $DeployRoot -ErrorAction SilentlyContinue
        $oldPayloadMoved = $false
    }
    if ($poolStopped) {
        Start-WebAppPool -Name $SiteName -ErrorAction SilentlyContinue
    }
    throw
} finally {
    if (-not $payloadSwapped) {
        Remove-UnplacedStagingDirectory -Path $stagingRoot -Parent $deployParent -Leaf $deployLeaf
    }
    if ($locationPushed) {
        Pop-Location
    }
}
