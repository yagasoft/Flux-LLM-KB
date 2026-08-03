param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [string]$CommitMessage = "Complete native feature",
    [switch]$DryRun,
    [switch]$SkipDeploy,
    [switch]$KeepWorktree,
    [switch]$ApplyMigrations,
    [switch]$ConfirmApplyMigrations,
    [int]$StepTimeoutSeconds = 600,
    [int]$TestStepTimeoutSeconds = 1800,
    [int]$DeployStepTimeoutSeconds = 1800,
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "C:\inetpub\FluxKnowledge",
    [string]$BackupRoot = "C:\FluxKnowledgeBackups"
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

    if ($null -eq $Task) {
        return ""
    }

    if ($Task.Wait(5000)) {
        return $Task.Result
    }

    return "[log stream did not close within 5 seconds]"
}

function Write-FeatureStepOutput {
    param(
        [string]$LogPath,
        [string]$Stdout,
        [string]$Stderr
    )

    "" | Out-File -FilePath $LogPath -Encoding utf8
    if ($Stdout) {
        $Stdout | Out-File -FilePath $LogPath -Append -Encoding utf8
    }

    if ($Stderr) {
        "[stderr]" | Out-File -FilePath $LogPath -Append -Encoding utf8
        $Stderr | Out-File -FilePath $LogPath -Append -Encoding utf8
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
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    $Record["duration_seconds"] = [Math]::Round(($finishedAt - $StartedAt).TotalSeconds, 3)
}

function Write-SummaryAndExit {
    param([int]$ExitCode)

    [ordered]@{
        ok = ($ExitCode -eq 0)
        failed_step = $script:FailedStep
        log_root = $script:LogRoot
        steps = $script:Steps
    } | ConvertTo-Json -Depth 8

    exit $ExitCode
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
        "DRY RUN: $Command" | Out-File -FilePath $logPath -Encoding utf8
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
        $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $processInfo.FileName = "powershell"
        $processInfo.Arguments = (@("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) |
                ForEach-Object { ConvertTo-FeatureCommandArgument $_ }) -join " "
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
            "Step '$Name' timed out after $effectiveTimeoutSeconds seconds; process tree was stopped." |
                Out-File -FilePath $logPath -Append -Encoding utf8
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
        if ($record.exit_code -eq 0) {
            $record.exit_code = 1
        }
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        $script:FailedStep = $Name
        $errorText = $_.ToString()
        $errorText | Out-File -FilePath $logPath -Append -Encoding utf8
        if ($FailureHint) {
            $FailureHint | Out-File -FilePath $logPath -Append -Encoding utf8
            throw "$errorText`n$FailureHint"
        }

        throw
    }
}

$CleanupWorktreeCommand = @'
$MainRoot = $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT
$FeatureWorktree = $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE
$Branch = $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH

function Normalize-CleanupPath {
    param([string]$Path)

    if (-not $Path) {
        return ""
    }

    try {
        $fullPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    } catch {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
    }

    return $fullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar).Replace(
            [System.IO.Path]::AltDirectorySeparatorChar,
            [System.IO.Path]::DirectorySeparatorChar)
}

function Test-WorktreeRegistered {
    param([string]$Worktree)

    $target = Normalize-CleanupPath -Path $Worktree
    foreach ($line in (git worktree list --porcelain)) {
        if ($line -like "worktree *") {
            $current = Normalize-CleanupPath -Path $line.Substring("worktree ".Length)
            if ($current.Equals($target, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }

    return $false
}

function Test-DirectoryEmpty {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    return -not [bool](Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1)
}

Set-Location $MainRoot
git worktree remove "$FeatureWorktree"
$removeExit = $LASTEXITCODE
if ($removeExit -ne 0) {
    if ((-not (Test-WorktreeRegistered -Worktree $FeatureWorktree)) -and
        (Test-DirectoryEmpty -Path $FeatureWorktree)) {
        "git worktree remove left an empty directory; continuing cleanup."
    } else {
        exit $removeExit
    }
}

git worktree prune
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

git branch -D $Branch
exit $LASTEXITCODE
'@

$FeatureWorktree = (Resolve-Path -LiteralPath $FeatureWorktree).Path
if (-not $MainRoot) {
    $MainRoot = Get-MainWorktreePath -Worktree $FeatureWorktree
}
$MainRoot = (Resolve-Path -LiteralPath $MainRoot).Path
$Branch = (git -C $FeatureWorktree branch --show-current).Trim()
if (-not $Branch.StartsWith("codex/", [System.StringComparison]::Ordinal)) {
    throw "Refusing to complete non-codex branch '$Branch'."
}

if ($ConfirmApplyMigrations -and -not $ApplyMigrations) {
    throw "-ConfirmApplyMigrations requires -ApplyMigrations."
}
if ($ApplyMigrations -and -not $ConfirmApplyMigrations) {
    throw "-ApplyMigrations requires -ConfirmApplyMigrations."
}
if (-not $DryRun -and [string]::IsNullOrWhiteSpace($env:FLUXKNOWLEDGE_TEST_SQL_CONNECTION)) {
    throw "Native closeout requires FLUXKNOWLEDGE_TEST_SQL_CONNECTION for the disposable SQL integration suite."
}

$script:LogRoot = Join-Path $MainRoot ".agents\run-logs"
New-Item -ItemType Directory -Force -Path $script:LogRoot | Out-Null
$script:Steps = @()
$script:FailedStep = $null
$safeCommitMessage = $CommitMessage.Replace("'", "''")
$safeBranch = $Branch.Replace("'", "''")
$nativeDeployCommand = ".\scripts\deploy\update-native-windows.ps1 -SiteName '$SiteName' -SiteUrl '$SiteUrl' -DeployRoot '$DeployRoot' -BackupRoot '$BackupRoot'"
if ($ApplyMigrations) {
    $nativeDeployCommand += " -ApplyMigrations -ConfirmApplyMigrations"
}

try {
    Invoke-FeatureStep -Name "verify-main-clean" -Cwd $MainRoot -Command 'if ((git status --porcelain) -ne $null) { git status --short; exit 1 }'
    Invoke-FeatureStep -Name "dotnet-tool-restore" -Cwd $FeatureWorktree -Command 'dotnet tool restore'
    Invoke-FeatureStep -Name "dotnet-restore-locked" -Cwd $FeatureWorktree -Command 'dotnet restore FluxKnowledge.slnx --locked-mode'
    Invoke-FeatureStep -Name "dotnet-build-release" -Cwd $FeatureWorktree -Command 'dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror'
    Invoke-FeatureStep -Name "dotnet-test-native" -Cwd $FeatureWorktree -Command 'dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"' -TimeoutSeconds $TestStepTimeoutSeconds
    Invoke-FeatureStep -Name "native-closeout-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\complete-feature-dryrun.ps1'
    Invoke-FeatureStep -Name "native-deployment-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\native-deployment-plan.ps1'
    Invoke-FeatureStep -Name "feature-commit" -Cwd $FeatureWorktree -Command "git add -A; if ((git status --porcelain) -ne `$null) { git commit -m '$safeCommitMessage' }"
    Invoke-FeatureStep -Name "sync-main" -Cwd $MainRoot -Command 'git pull --ff-only origin main'
    Invoke-FeatureStep -Name "squash-merge" -Cwd $MainRoot -Command "git merge --squash '$safeBranch'"
    Invoke-FeatureStep -Name "dotnet-tool-restore-main" -Cwd $MainRoot -Command 'dotnet tool restore'
    Invoke-FeatureStep -Name "dotnet-restore-locked-main" -Cwd $MainRoot -Command 'dotnet restore FluxKnowledge.slnx --locked-mode'
    Invoke-FeatureStep -Name "dotnet-build-release-main" -Cwd $MainRoot -Command 'dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror'
    Invoke-FeatureStep -Name "dotnet-test-native-main" -Cwd $MainRoot -Command 'dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"' -TimeoutSeconds $TestStepTimeoutSeconds
    Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command "if ((git status --porcelain) -ne `$null) { git commit -m '$safeCommitMessage' } else { 'No staged changes to commit.' }"
    Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command 'git push origin main'
    Invoke-FeatureStep -Name "verify-origin-main" -Cwd $MainRoot -Command '$headSha = (git rev-parse HEAD).Trim(); git fetch origin main; $originSha = (git rev-parse origin/main).Trim(); if ($headSha -ne $originSha) { Write-Host "HEAD $headSha differs from origin/main $originSha"; exit 1 }'

    if (-not $SkipDeploy) {
        Invoke-FeatureStep -Name "deploy-native-windows" -Cwd $MainRoot -Command $nativeDeployCommand -TimeoutSeconds $DeployStepTimeoutSeconds
    }

    if (-not $KeepWorktree) {
        $previousMainRoot = $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT
        $previousFeatureWorktree = $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE
        $previousBranch = $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH
        try {
            $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $MainRoot
            $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $FeatureWorktree
            $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH = $Branch
            Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command $CleanupWorktreeCommand
        } finally {
            $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $previousMainRoot
            $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $previousFeatureWorktree
            $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH = $previousBranch
        }
    }

    Write-SummaryAndExit -ExitCode 0
} catch {
    Write-SummaryAndExit -ExitCode 1
}
