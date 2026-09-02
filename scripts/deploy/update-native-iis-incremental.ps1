[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "I:\FluxKnowledge\App",
    [ValidateRange(10, 300)]
    [int]$ReadinessTimeoutSeconds = 120,
    [switch]$PlanOnly,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"
$CanonicalLiveRoot = "I:\FluxKnowledge"
$CanonicalDeployRoot = "$CanonicalLiveRoot\App"
$CanonicalRecoveryRoot = "$CanonicalLiveRoot\Recovery"
$IncrementalRecoveryRoot = "$CanonicalRecoveryRoot\IncrementalUpdates"

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
    Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
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

. (Join-Path $PSScriptRoot "loopback-deployment-safety.ps1")
Import-Module (Join-Path $PSScriptRoot "incremental-iis-payload-swap.psm1") -Force -ErrorAction Stop
$loopbackOrigin = Get-FixedLoopbackOrigin -SiteUrl $SiteUrl
if ($loopbackOrigin.Origin -cne "http://127.0.0.1:5137") {
    throw "Incremental IIS deployment requires the fixed http://127.0.0.1:5137 origin."
}

if ($PlanOnly) {
    [ordered]@{
        mode = "plan-only"
        site_name = "FluxKnowledge"
        site_url = "http://127.0.0.1:5137"
        application_root = $CanonicalDeployRoot
        recovery_root = $CanonicalRecoveryRoot
        migrations = $false
        clean_slate = $false
        preserved = @("Config", "Data", "Runtime", "Recovery", "CodexPlugin")
        rollback = "automatic-application-payload-restore"
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

$sourceStatus = (& git -C $SourceRoot status --porcelain 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "SourceRoot must be a readable Git checkout."
}
if (-not [string]::IsNullOrWhiteSpace($sourceStatus)) {
    throw "SourceRoot has uncommitted changes; incremental IIS deployment requires an immutable committed payload."
}
$commit = (& git -C $SourceRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch "^[0-9a-f]{40}$") {
    throw "SourceRoot does not resolve to an immutable Git commit."
}

Import-Module WebAdministration -ErrorAction Stop
$mutex = [Threading.Mutex]::new($false, "Global\FluxKnowledge.IncrementalIisUpdate.v1")
$leaseAcquired = $false
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

    & dotnet publish $webProject -c Release --no-restore --nologo -o $candidateRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing the incremental IIS candidate failed."
    }
    Test-ApplicationPayload -Path $candidateRoot

    $manifest = [ordered]@{
        commit = $commit
        staged_at_utc = [DateTime]::UtcNow.ToString("O")
        application_root = $CanonicalDeployRoot
        migrations = $false
        clean_slate = $false
    } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $releaseRoot "manifest.json"), $manifest, [Text.UTF8Encoding]::new($false))

    $swap = Invoke-IncrementalApplicationPayloadSwap `
        -ApplicationRoot $CanonicalDeployRoot `
        -CandidateRoot $candidateRoot `
        -PreviousRoot $previousRoot `
        -FailedRoot $failedRoot `
        -StopApplication {
            Stop-WebAppPool -Name $SiteName
            Wait-IisAppPoolState -Name $SiteName -ExpectedState "Stopped" -TimeoutSeconds $ReadinessTimeoutSeconds
        } `
        -StartApplication {
            Start-WebAppPool -Name $SiteName
            Wait-IisAppPoolState -Name $SiteName -ExpectedState "Started" -TimeoutSeconds $ReadinessTimeoutSeconds
        } `
        -ValidateApplication {
            Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
        }

    [ordered]@{
        ok = $true
        mode = "applied"
        commit = $commit
        release_root = $releaseRoot
        rollback_payload = $swap.PreviousPayload
        migrations = $false
        clean_slate = $false
    } | ConvertTo-Json -Depth 3
}
finally {
    if ($leaseAcquired) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
