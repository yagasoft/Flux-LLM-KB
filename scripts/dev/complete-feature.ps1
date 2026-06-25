param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [string]$CommitMessage = "Complete feature",
    [switch]$DryRun,
    [switch]$SkipDeploy,
    [switch]$KeepWorktree,
    [int]$StepTimeoutSeconds = 600
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

function Invoke-FeatureStep {
    param(
        [string]$Name,
        [string]$Command,
        [string]$Cwd
    )
    $logPath = New-StepLogPath -Name $Name
    $record = [ordered]@{
        name = $Name
        cwd = $Cwd
        command = $Command
        exit_code = 0
        log_path = $logPath
        skipped = [bool]$DryRun
    }
    $script:Steps += $record
    if ($DryRun) {
        "DRY RUN: $Command" | Out-File -FilePath $logPath -Encoding UTF8
        return
    }
    $stdoutText = ""
    $stderrText = ""
    $stdoutTask = $null
    $stderrTask = $null
    try {
        $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($Command))
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
        if (-not $process.WaitForExit($StepTimeoutSeconds * 1000)) {
            $record.exit_code = 124
            Stop-FeatureProcessTree -ProcessId $process.Id
            $process.WaitForExit(5000) | Out-Null
            $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
            $stderrText = Get-FeatureTaskResult -Task $stderrTask
            Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
            "Step '$Name' timed out after $StepTimeoutSeconds seconds; process tree was stopped." | Out-File -FilePath $logPath -Append -Encoding UTF8
            throw "Step '$Name' timed out after $StepTimeoutSeconds seconds. See $logPath"
        }
        $process.WaitForExit()
        $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
        $stderrText = Get-FeatureTaskResult -Task $stderrTask
        Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
        $record.exit_code = $process.ExitCode
        if ($process.ExitCode -ne 0) {
            throw "Step '$Name' failed with exit code $($process.ExitCode). See $logPath"
        }
    } catch {
        $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
        $stderrText = Get-FeatureTaskResult -Task $stderrTask
        Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
        if ($record.exit_code -eq 0) { $record.exit_code = 1 }
        $script:FailedStep = $Name
        $_ | Out-File -FilePath $logPath -Append -Encoding UTF8
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
    python -m pip install -e "$MainRoot[dev]"
}
'@

$CleanupWorktreeCommand = @'
$MainRoot = $env:FLUX_KB_CLEANUP_MAIN_ROOT
$FeatureWorktree = $env:FLUX_KB_CLEANUP_FEATURE_WORKTREE
$Branch = $env:FLUX_KB_CLEANUP_BRANCH

Set-Location $MainRoot
git worktree remove "$FeatureWorktree"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git worktree prune
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
git branch -D $Branch
exit $LASTEXITCODE
'@

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
Set-Location $MainRoot

try {
    Invoke-FeatureStep -Name "verify-main-clean" -Cwd $MainRoot -Command 'if ((git status --porcelain) -ne $null) { git status --short; exit 1 }'
    Invoke-FeatureStep -Name "pytest" -Cwd $FeatureWorktree -Command '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m pytest'
    Invoke-FeatureStep -Name "compileall" -Cwd $FeatureWorktree -Command '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m compileall -q src tests'
    Invoke-FeatureStep -Name "flux-lint" -Cwd $FeatureWorktree -Command '$env:PYTHONPATH = (Join-Path (Get-Location) "src"); python -m flux_llm_kb.cli lint'
    Invoke-FeatureStep -Name "dashboard-install" -Cwd $FeatureWorktree -Command 'npm --prefix dashboard ci'
    Invoke-FeatureStep -Name "dashboard-test" -Cwd $FeatureWorktree -Command 'npm --prefix dashboard test'
    Invoke-FeatureStep -Name "dashboard-build" -Cwd $FeatureWorktree -Command 'npm --prefix dashboard run build'
    Invoke-FeatureStep -Name "feature-commit" -Cwd $FeatureWorktree -Command "git add -A; if ((git status --porcelain) -ne `$null) { git commit -m '$CommitMessage' }"
    Invoke-FeatureStep -Name "sync-main" -Cwd $MainRoot -Command 'git pull --ff-only origin main'
    Invoke-FeatureStep -Name "squash-merge" -Cwd $MainRoot -Command "git merge --squash $Branch"
    Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command "if ((git status --porcelain) -ne `$null) { git commit -m '$CommitMessage' } else { 'No staged changes to commit.' }"
    Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command 'git push origin main'
    Invoke-FeatureStep -Name "verify-origin-main" -Cwd $MainRoot -Command 'git fetch origin main; if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { exit 1 }'
    if (-not $SkipDeploy) {
        Invoke-FeatureStep -Name "deploy-production" -Cwd $MainRoot -Command '.\scripts\deploy\update-flux.ps1'
        Invoke-FeatureStep -Name "probe-dashboard" -Cwd $MainRoot -Command 'Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8765/dashboard" -TimeoutSec 15 | Out-Null'
        Invoke-FeatureStep -Name "probe-dashboard-health" -Cwd $MainRoot -Command 'Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8765/api/dashboard/health" -TimeoutSec 15 | Out-Null'
    }
    $previousRepairMainRoot = $env:FLUX_KB_REPAIR_MAIN_ROOT
    $previousRepairFeatureWorktree = $env:FLUX_KB_REPAIR_FEATURE_WORKTREE
    try {
        $env:FLUX_KB_REPAIR_MAIN_ROOT = $MainRoot
        $env:FLUX_KB_REPAIR_FEATURE_WORKTREE = $FeatureWorktree
        Invoke-FeatureStep -Name "repair-python-editable-install" -Cwd $MainRoot -Command $RepairEditableInstallCommand
    } finally {
        $env:FLUX_KB_REPAIR_MAIN_ROOT = $previousRepairMainRoot
        $env:FLUX_KB_REPAIR_FEATURE_WORKTREE = $previousRepairFeatureWorktree
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
