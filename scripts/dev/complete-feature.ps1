param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [string]$CommitMessage = "Complete feature",
    [switch]$DryRun,
    [switch]$SkipDeploy,
    [switch]$KeepWorktree
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
    Push-Location $Cwd
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        powershell -NoProfile -ExecutionPolicy Bypass -Command $Command *> $logPath
        $ErrorActionPreference = $previousErrorActionPreference
        $record.exit_code = $LASTEXITCODE
        if ($LASTEXITCODE -ne 0) {
            throw "Step '$Name' failed with exit code $LASTEXITCODE. See $logPath"
        }
    } catch {
        if ($null -ne $previousErrorActionPreference) {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($record.exit_code -eq 0) { $record.exit_code = 1 }
        $script:FailedStep = $Name
        $_ | Out-File -FilePath $logPath -Append -Encoding UTF8
        throw
    } finally {
        Pop-Location
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
    Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command "git commit -m '$CommitMessage'"
    Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command 'git push origin main'
    Invoke-FeatureStep -Name "verify-origin-main" -Cwd $MainRoot -Command 'git fetch origin main; if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { exit 1 }'
    if (-not $SkipDeploy) {
        Invoke-FeatureStep -Name "deploy-production" -Cwd $MainRoot -Command '.\scripts\deploy\update-flux.ps1'
        Invoke-FeatureStep -Name "probe-dashboard" -Cwd $MainRoot -Command 'Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8765/dashboard" -TimeoutSec 15 | Out-Null'
        Invoke-FeatureStep -Name "probe-dashboard-health" -Cwd $MainRoot -Command 'Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:8765/api/dashboard/health" -TimeoutSec 15 | Out-Null'
    }
    if (-not $KeepWorktree) {
        Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command "git worktree remove '$FeatureWorktree'; git worktree prune; git branch -D $Branch"
    }
    Write-SummaryAndExit -ExitCode 0
} catch {
    Write-SummaryAndExit -ExitCode 1
}
