param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [string]$CommitMessage = "Complete feature",
    [switch]$DryRun,
    [switch]$SkipDeploy,
    [switch]$AllowNpmInstall,
    [switch]$RefreshNpmDependencies,
    [switch]$KeepWorktree,
    [int]$StepTimeoutSeconds = 600,
    [int]$PytestStepTimeoutSeconds = 1200,
    [int]$DeployStepTimeoutSeconds = 1800,
    [string]$PytestWorkers = "auto",
    [switch]$SkipWorkerStart,
    [switch]$AllowPipDownloads,
    [switch]$RefreshPipDependencies,
    [string]$NpmCachePath = $(if ($env:FLUX_KB_NPM_CACHE_PATH) { $env:FLUX_KB_NPM_CACHE_PATH } else { "D:\FluxLLMKB\package-cache\npm" }),
    [string]$PostDeployReclaimOutlookProfile = ""
)

$ErrorActionPreference = "Stop"

function Get-MainWorktreePath {
    param([string]$Worktree)
    $lines = git -C $Worktree worktree list --porcelain
    $currentPath = $null
    foreach ($line in $lines) {
        if ($line -like "worktree *") {
            $currentPath = $line.Substring("worktree ".Length)
        } elseif ($line -eq "branch refs/heads/main" -and $currentPath) {
            return $currentPath
        }
    }
    throw "Unable to locate main worktree from git worktree list."
}

function New-StepLogPath {
    param([string]$Name)
    $safeName = ($Name -replace "[^A-Za-z0-9_.-]", "-").Trim("-")
    return Join-Path $script:LogRoot ("{0:yyyyMMdd-HHmmss}-{1}.log" -f [DateTime]::UtcNow, $safeName)
}

function Stop-FeatureProcessTree {
    param([int]$ProcessId)
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        Stop-FeatureProcessTree -ProcessId ([int]$child.ProcessId)
    }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

function ConvertTo-FeatureCommandArgument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }
    return $Value
}

function Get-FeatureTaskResult {
    param($Task)
    if ($null -eq $Task) { return "" }
    if ($Task.Wait(5000)) { return $Task.Result }
    return "[log stream did not close within 5 seconds]"
}

function Write-FeatureStepOutput {
    param(
        [string]$LogPath,
        [string]$Stdout,
        [string]$Stderr
    )
    "" | Out-File -FilePath $LogPath -Encoding UTF8
    if ($Stdout) {
        $Stdout | Out-File -FilePath $LogPath -Append -Encoding UTF8
    }
    if ($Stderr) {
        "[stderr]" | Out-File -FilePath $LogPath -Append -Encoding UTF8
        $Stderr | Out-File -FilePath $LogPath -Append -Encoding UTF8
    }
}

function New-FeatureStepScript {
    param([string]$Command)
    return @"
`$ErrorActionPreference = "Stop"
try {
$Command
    if (`$global:LASTEXITCODE -is [int] -and `$global:LASTEXITCODE -ne 0) {
        exit `$global:LASTEXITCODE
    }
    exit 0
} catch {
    Write-Error `$_
    exit 1
}
"@
}

function Write-SummaryAndExit {
    param([int]$ExitCode)
    $summary = [ordered]@{
        ok = ($ExitCode -eq 0)
        failed_step = $script:FailedStep
        log_root = $script:LogRoot
        steps = $script:Steps
    }
    $summary | ConvertTo-Json -Depth 8
    exit $ExitCode
}

function Complete-FeatureStepRecord {
    param(
        [System.Collections.IDictionary]$Record,
        [DateTime]$StartedAt
    )
    if ($Record["finished_at"]) {
        return
    }
    $Record["finished_at"] = [DateTime]::UtcNow.ToString("o")
    $finishedAt = [DateTime]::Parse(
        $Record["finished_at"],
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind
    )
    $Record["duration_seconds"] = [Math]::Round(($finishedAt - $StartedAt).TotalSeconds, 3)
}

function Invoke-FeatureStep {
    param(
        [string]$Name,
        [string]$Command,
        [string]$Cwd,
        [int]$TimeoutSeconds = 0,
        [string]$FailureHint = ""
    )
    $logPath = New-StepLogPath -Name $Name
    $startedAt = [DateTime]::UtcNow
    $record = [ordered]@{
        name = $Name
        cwd = $Cwd
        command = $Command
        started_at = $startedAt.ToString("o")
        finished_at = $null
        duration_seconds = $null
        exit_code = 0
        log_path = $logPath
        skipped = [bool]$DryRun
    }
    $script:Steps += $record
    if ($DryRun) {
        "DRY RUN: $Command" | Out-File -FilePath $logPath -Encoding UTF8
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        return
    }
    $stdoutText = ""
    $stderrText = ""
    $stdoutTask = $null
    $stderrTask = $null
    $effectiveTimeoutSeconds = if ($TimeoutSeconds -gt 0) { $TimeoutSeconds } else { $StepTimeoutSeconds }
    try {
        $stepScript = New-FeatureStepScript -Command $Command
        $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($stepScript))
        # Process-level redirection avoids nested PowerShell stream parsing failures
        # such as ProcessStreamReader_CliXmlError from merging native stderr with *>.
        $processArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand)
        $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $processInfo.FileName = "powershell"
        $processInfo.Arguments = ($processArguments | ForEach-Object { ConvertTo-FeatureCommandArgument $_ }) -join " "
        $processInfo.WorkingDirectory = $Cwd
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $processInfo
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($effectiveTimeoutSeconds * 1000)) {
            $record.exit_code = 124
            Stop-FeatureProcessTree -ProcessId $process.Id
            $process.WaitForExit(5000) | Out-Null
            $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
            $stderrText = Get-FeatureTaskResult -Task $stderrTask
            Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
            "Step '$Name' timed out after $effectiveTimeoutSeconds seconds; process tree was stopped." | Out-File -FilePath $logPath -Append -Encoding UTF8
            Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
            throw "Step '$Name' timed out after $effectiveTimeoutSeconds seconds. See $logPath"
        }
        $process.WaitForExit()
        $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
        $stderrText = Get-FeatureTaskResult -Task $stderrTask
        Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
        $record.exit_code = $process.ExitCode
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        if ($process.ExitCode -ne 0) {
            throw "Step '$Name' failed with exit code $($process.ExitCode). See $logPath"
        }
    } catch {
        $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
        $stderrText = Get-FeatureTaskResult -Task $stderrTask
        Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
        if ($record.exit_code -eq 0) { $record.exit_code = 1 }
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        $script:FailedStep = $Name
        $errorText = $_.ToString()
        $errorText | Out-File -FilePath $logPath -Append -Encoding UTF8
        if ($FailureHint) {
            $FailureHint | Out-File -FilePath $logPath -Append -Encoding UTF8
            throw "$errorText`n$FailureHint"
        }
        throw
    }
}

function Invoke-FeatureStepOptional {
    param(
        [string]$Name,
        [string]$Command,
        [string]$Cwd
    )
    Invoke-FeatureStep -Name $Name -Command $Command -Cwd $Cwd
}

$RepairEditableInstallCommand = @'
$MainRoot = $env:FLUX_KB_REPAIR_MAIN_ROOT
$FeatureWorktree = $env:FLUX_KB_REPAIR_FEATURE_WORKTREE

function Normalize-FluxPath {
    param([string]$Path)
    if (-not $Path) { return "" }
    try {
        $fullPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    } catch {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
    }
    return $fullPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar).Replace([System.IO.Path]::AltDirectorySeparatorChar, [System.IO.Path]::DirectorySeparatorChar)
}

function Test-UnderPath {
    param([string]$Path, [string]$Root)
    $normalizedPath = Normalize-FluxPath -Path $Path
    $normalizedRoot = Normalize-FluxPath -Path $Root
    if (-not $normalizedPath -or -not $normalizedRoot) { return $false }
    if ($normalizedPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    $prefix = "$normalizedRoot$([System.IO.Path]::DirectorySeparatorChar)"
    return $normalizedPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
}

$editableLocation = $null
$pipShow = python -m pip show flux-llm-kb 2>$null
if ($LASTEXITCODE -eq 0) {
    foreach ($line in $pipShow) {
        if ($line -like "Editable project location:*") {
            $editableLocation = $line.Substring("Editable project location:".Length).Trim()
            break
        }
    }
}

$needsRepair = $false
if (-not $editableLocation) {
    $needsRepair = $true
} elseif (-not (Test-Path -LiteralPath $editableLocation)) {
    $needsRepair = $true
} elseif (Test-UnderPath -Path $editableLocation -Root $FeatureWorktree) {
    $needsRepair = $true
} elseif (Test-UnderPath -Path $editableLocation -Root $MainRoot) {
    $needsRepair = $false
}

if ($needsRepair) {
    if ($env:FLUX_KB_ALLOW_PIP_DOWNLOADS -ne "1") {
        throw "Python editable install repair is offline-first; seed the shared environment or rerun with -AllowPipDownloads."
    }
    python -m pip install -e "$MainRoot[dev]"
}
'@

$CleanupWorktreeCommand = @'
$MainRoot = $env:FLUX_KB_CLEANUP_MAIN_ROOT
$FeatureWorktree = $env:FLUX_KB_CLEANUP_FEATURE_WORKTREE
$Branch = $env:FLUX_KB_CLEANUP_BRANCH

function Normalize-CleanupPath {
    param([string]$Path)
    if (-not $Path) { return "" }
    try {
        $fullPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    } catch {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
    }
    return $fullPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar).Replace([System.IO.Path]::AltDirectorySeparatorChar, [System.IO.Path]::DirectorySeparatorChar)
}

function Test-WorktreeRegistered {
    param([string]$Worktree)
    $target = Normalize-CleanupPath -Path $Worktree
    $currentPath = $null
    foreach ($line in (git worktree list --porcelain)) {
        if ($line -like "worktree *") {
            $currentPath = Normalize-CleanupPath -Path $line.Substring("worktree ".Length)
            if ($currentPath.Equals($target, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }
    return $false
}

function Test-DirectoryEmpty {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    return -not [bool](Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1)
}

Set-Location $MainRoot
git worktree remove "$FeatureWorktree"
$removeExit = $LASTEXITCODE
if ($removeExit -ne 0) {
    if ((-not (Test-WorktreeRegistered -Worktree $FeatureWorktree)) -and (Test-DirectoryEmpty -Path $FeatureWorktree)) {
        "git worktree remove left an empty directory; continuing cleanup."
    } else {
        exit $removeExit
    }
}
git worktree prune
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git branch -D $Branch
exit $LASTEXITCODE
'@

$DashboardHealthProbeCommand = @'
$dashboardHealthUri = "http://127.0.0.1:8765" + "/api/dashboard/health"
$health = Invoke-RestMethod -Uri $dashboardHealthUri -TimeoutSec 15
$blocked = @()
if (-not $health.database -or -not $health.database.checks) {
    $blocked += "database.checks missing"
} else {
    foreach ($checkProperty in $health.database.checks.PSObject.Properties) {
        $check = $checkProperty.Value
        if ($check.required -ne $false -and $check.ok -ne $true) {
            $message = if ($check.message) { $check.message } else { "blocked" }
            $blocked += "$($checkProperty.Name): $message"
        }
    }
}
if ($blocked.Count -gt 0) {
    throw "Dashboard health reported blocked required checks: $($blocked -join '; ')"
}
'@

$McpReadinessProbeCommand = '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m flux_llm_kb.cli codex mcp-readiness --json'

$DashboardNpmInstallCommand = @'
$NpmCachePath = $env:FLUX_KB_NPM_CACHE_PATH
New-Item -ItemType Directory -Force -Path "$NpmCachePath" | Out-Null
npm --prefix dashboard ci --include=dev --cache "$NpmCachePath" --prefer-offline
'@
$DashboardCacheCheckCommand = @'
$NpmCachePath = $env:FLUX_KB_NPM_CACHE_PATH
$requiredTools = @(
    @{ Name = "vitest CLI"; Path = Join-Path (Get-Location) "dashboard\node_modules\vitest\dist\cli.js" },
    @{ Name = "vite CLI"; Path = Join-Path (Get-Location) "dashboard\node_modules\vite\bin\vite.js" }
)
$missing = @()
foreach ($tool in $requiredTools) {
    if (-not (Test-Path -LiteralPath $tool.Path)) {
        $missing += "$($tool.Name): $($tool.Path)"
    }
}
if ($missing.Count -gt 0) {
    throw "Dashboard dependency cache is incomplete. Missing $($missing -join '; '). Seed dashboard dependencies once with: npm --prefix dashboard ci --include=dev --cache `"$NpmCachePath`" --prefer-offline. Rerun closeout without npm flags after seeding, or rerun with -AllowNpmInstall only when intentionally refreshing npm dependencies."
}
"Skipped dashboard package install; existing dashboard node_modules verified."
'@

$PipOfflineFailureHint = "If deploy pip dependencies are missing from cache, rerun this closeout with -AllowPipDownloads only."

$FeatureWorktree = (Resolve-Path $FeatureWorktree).Path
if (-not $MainRoot) {
    $MainRoot = Get-MainWorktreePath -Worktree $FeatureWorktree
}
$MainRoot = (Resolve-Path $MainRoot).Path
$Branch = (git -C $FeatureWorktree branch --show-current).Trim()
if (-not $Branch.StartsWith("codex/")) {
    throw "Refusing to complete non-codex branch '$Branch'."
}

$script:LogRoot = Join-Path $MainRoot ".agents\run-logs"
New-Item -ItemType Directory -Force -Path $script:LogRoot | Out-Null
$script:Steps = @()
$script:FailedStep = $null
$env:FLUX_KB_NPM_CACHE_PATH = [System.IO.Path]::GetFullPath($NpmCachePath)
Set-Location $MainRoot
$pytestCommand = '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m pytest'
if ([string]::IsNullOrWhiteSpace($PytestWorkers) -or $PytestWorkers -eq "0") {
    $pytestCommand = '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m pytest'
} else {
    $safePytestWorkers = $PytestWorkers.Replace("'", "''")
    $pytestCommand = '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); $PytestWorkers = ''' + $safePytestWorkers + '''; python -m pytest -n $PytestWorkers --dist loadfile'
}

try {
    Invoke-FeatureStep -Name "verify-main-clean" -Cwd $MainRoot -Command 'if ((git status --porcelain) -ne $null) { git status --short; exit 1 }'
    Invoke-FeatureStep -Name "pytest" -Cwd $FeatureWorktree -Command $pytestCommand -TimeoutSeconds $PytestStepTimeoutSeconds
    Invoke-FeatureStep -Name "compileall" -Cwd $FeatureWorktree -Command '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m compileall -q src tests'
    Invoke-FeatureStep -Name "flux-lint" -Cwd $FeatureWorktree -Command '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m flux_llm_kb.cli lint'
    if (-not ($AllowNpmInstall -or $RefreshNpmDependencies)) {
        Invoke-FeatureStep -Name "dashboard-install" -Cwd $FeatureWorktree -Command $DashboardCacheCheckCommand
    } else {
        Invoke-FeatureStep -Name "dashboard-install" -Cwd $FeatureWorktree -Command $DashboardNpmInstallCommand
    }
    Invoke-FeatureStep -Name "dashboard-test" -Cwd $FeatureWorktree -Command 'Push-Location dashboard; try { node node_modules/vitest/dist/cli.js run } finally { Pop-Location }'
    Invoke-FeatureStep -Name "dashboard-build" -Cwd $FeatureWorktree -Command 'Push-Location dashboard; try { node node_modules/vite/bin/vite.js build } finally { Pop-Location }'
    Invoke-FeatureStep -Name "feature-commit" -Cwd $FeatureWorktree -Command "git add -A; if ((git status --porcelain) -ne `$null) { git commit -m '$CommitMessage' }"
    Invoke-FeatureStep -Name "sync-main" -Cwd $MainRoot -Command 'git pull --ff-only origin main'
    Invoke-FeatureStep -Name "squash-merge" -Cwd $MainRoot -Command "git merge --squash $Branch"
    Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command "if ((git status --porcelain) -ne `$null) { git commit -m '$CommitMessage' } else { 'No staged changes to commit.' }"
    Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command 'git push origin main'
    Invoke-FeatureStep -Name "verify-origin-main" -Cwd $MainRoot -Command '$headSha = (git rev-parse HEAD).Trim(); git fetch origin main; $originSha = (git rev-parse origin/main).Trim(); if ($headSha -ne $originSha) { Write-Host "HEAD $headSha differs from origin/main $originSha"; exit 1 }'
    if (-not $SkipDeploy) {
        $pipOffline = -not ($AllowPipDownloads -or $RefreshPipDependencies)
        $deployPipOfflineValue = if ($pipOffline) { '$true' } else { '$false' }
        $deployCommand = ".\scripts\deploy\update-flux.ps1 -GpuMode on -SkipDashboardBuild -PipOffline:$deployPipOfflineValue"
        if ($SkipWorkerStart) {
            $deployCommand += ' -SkipWorkerStart'
        }
        $deployFailureHint = if ($pipOffline) { $PipOfflineFailureHint } else { "" }
        Invoke-FeatureStep -Name "deploy-production" -Cwd $MainRoot -Command $deployCommand -TimeoutSeconds $DeployStepTimeoutSeconds -FailureHint $deployFailureHint
        Invoke-FeatureStep -Name "probe-dashboard" -Cwd $MainRoot -Command 'Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8765/dashboard" -TimeoutSec 15 | Out-Null'
        # Probe http://127.0.0.1:8765/api/dashboard/health and fail if required DB paths are blocked.
        Invoke-FeatureStep -Name "probe-dashboard-health" -Cwd $MainRoot -Command $DashboardHealthProbeCommand
        Invoke-FeatureStep -Name "probe-mcp-readiness" -Cwd $MainRoot -Command $McpReadinessProbeCommand
        if ($PostDeployReclaimOutlookProfile) {
            $safeReclaimProfile = $PostDeployReclaimOutlookProfile.Replace("'", "''")
            $reclaimCommand = 'docker exec flux-llm-kb-api python -m flux_llm_kb.cli mail spool-dedupe --profile ''' + $safeReclaimProfile + ''' --apply --purge --json'
            Invoke-FeatureStep -Name "post-deploy-outlook-spool-reclaim" -Cwd $MainRoot -Command $reclaimCommand
        }
    }
    $previousRepairMainRoot = $env:FLUX_KB_REPAIR_MAIN_ROOT
    $previousRepairFeatureWorktree = $env:FLUX_KB_REPAIR_FEATURE_WORKTREE
    $previousAllowPipDownloads = $env:FLUX_KB_ALLOW_PIP_DOWNLOADS
    try {
        $env:FLUX_KB_REPAIR_MAIN_ROOT = $MainRoot
        $env:FLUX_KB_REPAIR_FEATURE_WORKTREE = $FeatureWorktree
        $env:FLUX_KB_ALLOW_PIP_DOWNLOADS = if ($AllowPipDownloads -or $RefreshPipDependencies) { "1" } else { "0" }
        Invoke-FeatureStep -Name "repair-python-editable-install" -Cwd $MainRoot -Command $RepairEditableInstallCommand
    } finally {
        $env:FLUX_KB_REPAIR_MAIN_ROOT = $previousRepairMainRoot
        $env:FLUX_KB_REPAIR_FEATURE_WORKTREE = $previousRepairFeatureWorktree
        if ($null -eq $previousAllowPipDownloads) {
            Remove-Item Env:\FLUX_KB_ALLOW_PIP_DOWNLOADS -ErrorAction SilentlyContinue
        } else {
            $env:FLUX_KB_ALLOW_PIP_DOWNLOADS = $previousAllowPipDownloads
        }
    }
    if (-not $KeepWorktree) {
        $previousCleanupMainRoot = $env:FLUX_KB_CLEANUP_MAIN_ROOT
        $previousCleanupFeatureWorktree = $env:FLUX_KB_CLEANUP_FEATURE_WORKTREE
        $previousCleanupBranch = $env:FLUX_KB_CLEANUP_BRANCH
        try {
            $env:FLUX_KB_CLEANUP_MAIN_ROOT = $MainRoot
            $env:FLUX_KB_CLEANUP_FEATURE_WORKTREE = $FeatureWorktree
            $env:FLUX_KB_CLEANUP_BRANCH = $Branch
            Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command $CleanupWorktreeCommand
        } finally {
            $env:FLUX_KB_CLEANUP_MAIN_ROOT = $previousCleanupMainRoot
            $env:FLUX_KB_CLEANUP_FEATURE_WORKTREE = $previousCleanupFeatureWorktree
            $env:FLUX_KB_CLEANUP_BRANCH = $previousCleanupBranch
        }
    }
    Write-SummaryAndExit -ExitCode 0
} catch {
    Write-SummaryAndExit -ExitCode 1
}
