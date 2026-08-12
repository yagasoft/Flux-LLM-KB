param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [string]$CommitMessage = "Complete native feature",
    [switch]$DryRun,
    [switch]$SkipDeploy,
    [switch]$KeepWorktree,
    [switch]$ApplyMigrations,
    [switch]$ConfirmApplyMigrations,
    [switch]$KeepOutlookHostDisabled = $true,
    [switch]$ConfirmApprovedLegacyLocalSurfaceChanges,
    [switch]$ResumeStagedSquash,
    [string]$ExpectedMainHead = "",
    [string]$ExpectedStagedFeatureHead = "",
    [string]$ExpectedFeatureHead = "",
    [string]$ExpectedFeatureBranch = "",
    [int]$StepTimeoutSeconds = 600,
    [int]$TestStepTimeoutSeconds = 1800,
    [int]$DeployStepTimeoutSeconds = 1800,
    [string]$SiteName = "FluxKnowledge",
    [string]$SiteUrl = "http://127.0.0.1:5137",
    [string]$DeployRoot = "C:\inetpub\FluxKnowledge",
    [string]$BackupRoot = "C:\FluxKnowledgeBackups"
)

$ErrorActionPreference = "Stop"
$loopbackDeploymentSafetyScript = Join-Path (Split-Path -Parent $PSScriptRoot) "deploy\loopback-deployment-safety.ps1"
if (-not (Test-Path -LiteralPath $loopbackDeploymentSafetyScript -PathType Leaf)) {
    throw "The fixed-loopback deployment safety helper is missing."
}
. $loopbackDeploymentSafetyScript
$SiteUrl = (Get-FixedLoopbackOrigin -SiteUrl $SiteUrl).Origin
if (-not $KeepOutlookHostDisabled) {
    throw "Outlook host activation is not authorised; KeepOutlookHostDisabled must remain true."
}

$resumeExpectedInputs = @($ExpectedMainHead, $ExpectedStagedFeatureHead, $ExpectedFeatureHead, $ExpectedFeatureBranch)
$resumeExpectedOriginUrl = 'https://github.com/yagasoft/Flux-LLM-KB.git'
$hasResumeExpectedInput = @($resumeExpectedInputs | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
if ($ResumeStagedSquash -and @($resumeExpectedInputs | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw "-ResumeStagedSquash requires all expected commit and branch identities."
}
if (-not $ResumeStagedSquash -and $hasResumeExpectedInput) {
    throw "Expected resume identities require -ResumeStagedSquash."
}
if ($ResumeStagedSquash -and (
        $ExpectedMainHead -cnotmatch '^[0-9a-f]{40}$' -or
        $ExpectedStagedFeatureHead -cnotmatch '^[0-9a-f]{40}$' -or
        $ExpectedFeatureHead -cnotmatch '^[0-9a-f]{40}$')) {
    throw "Expected resume commits must be canonical full SHA-1 values."
}

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
        [string]$FailureHint = "",
        [hashtable]$Environment = @{},
        [switch]$RunInDryRun
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
        skipped = [bool]($DryRun -and -not $RunInDryRun)
    }
    $script:Steps += $record

    if ($DryRun -and -not $RunInDryRun) {
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
        foreach ($entry in $Environment.GetEnumerator()) {
            $processInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
        }
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
if ($ResumeStagedSquash -and -not $MainRoot) {
    throw '-ResumeStagedSquash requires the explicit main worktree path.'
}
if (-not $ResumeStagedSquash -and -not $MainRoot) {
    $MainRoot = Get-MainWorktreePath -Worktree $FeatureWorktree
}
$MainRoot = (Resolve-Path -LiteralPath $MainRoot).Path
$Branch = if ($ResumeStagedSquash) { $ExpectedFeatureBranch } else { (git -C $FeatureWorktree branch --show-current).Trim() }
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

$gmailGuardScript = Join-Path $PSScriptRoot "assert-legacy-gmail-unchanged.ps1"
if (-not (Test-Path -LiteralPath $gmailGuardScript -PathType Leaf)) {
    throw "The legacy Gmail preservation guard is missing."
}
$refreshStagedSquashScript = Join-Path $FeatureWorktree "scripts\dev\refresh-staged-squash.ps1"
if ($ResumeStagedSquash -and -not (Test-Path -LiteralPath $refreshStagedSquashScript -PathType Leaf)) {
    throw "The staged-squash refresh helper is missing from the feature worktree."
}
$script:LogRoot = if ($DryRun) {
    Join-Path ([System.IO.Path]::GetTempPath()) ('FluxKnowledge-CloseoutDryRun-' + [Guid]::NewGuid().ToString('N'))
} else {
    Join-Path $MainRoot ".agents\run-logs"
}
New-Item -ItemType Directory -Force -Path $script:LogRoot | Out-Null
$script:Steps = @()
$script:FailedStep = $null
$safeGmailGuardScript = $gmailGuardScript.Replace("'", "''")
$safeCommitMessage = $CommitMessage.Replace("'", "''")
$safeBranch = $Branch.Replace("'", "''")
$gmailGuardConfirmationArgument = if ($ConfirmApprovedLegacyLocalSurfaceChanges) {
    " -ConfirmApprovedLegacyLocalSurfaceChanges"
} else {
    ""
}
$gmailRegressionCommand = 'python -m pytest -q tests\test_mail_ingestion.py tests\test_mail_oauth.py tests\test_mail_post_process.py tests\test_mail_scheduler.py tests\test_mail_cli_rest.py tests\test_background_jobs.py; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; python -m pytest -q tests\test_worker.py -k imap'
$nativeCommandEnvironment = @{
    FLUXKNOWLEDGE_CLOSEOUT_SITE_NAME = $SiteName
    FLUXKNOWLEDGE_CLOSEOUT_SITE_URL = $SiteUrl
    FLUXKNOWLEDGE_CLOSEOUT_DEPLOY_ROOT = $DeployRoot
    FLUXKNOWLEDGE_CLOSEOUT_BACKUP_ROOT = $BackupRoot
    FLUXKNOWLEDGE_CLOSEOUT_APPLY_MIGRATIONS = if ($ApplyMigrations) { "1" } else { "0" }
    FLUXKNOWLEDGE_CLOSEOUT_KEEP_OUTLOOK_HOST_DISABLED = "1"
    FLUXKNOWLEDGE_CLOSEOUT_REFRESH_SCRIPT = $refreshStagedSquashScript
    FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD = $ExpectedMainHead
    FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_STAGED_FEATURE_HEAD = $ExpectedStagedFeatureHead
    FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD = $ExpectedFeatureHead
    FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH = $ExpectedFeatureBranch
    FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL = $resumeExpectedOriginUrl
    FLUXKNOWLEDGE_CLOSEOUT_COMMIT_MESSAGE = $CommitMessage
    FLUXKNOWLEDGE_CLOSEOUT_DRY_RUN = if ($DryRun) { '1' } else { '0' }
    FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $MainRoot
    FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $FeatureWorktree
    FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT = $gmailGuardScript
    FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_CONFIRM = if ($ConfirmApprovedLegacyLocalSurfaceChanges) { '1' } else { '0' }
}
$resumeFeatureGmailGuardCommand = @'
$gmailGuardArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT, '-RepositoryRoot', '.', '-BaselineRef', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD, '-ResumeBoundary', '-ExpectedOriginUrl', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, '-ExpectedHead', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD, '-ExpectedBranch', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH)
if ($env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_CONFIRM -ceq '1') { $gmailGuardArguments += '-ConfirmApprovedLegacyLocalSurfaceChanges' }
& powershell.exe @gmailGuardArguments
'@
$resumeMainGmailGuardCommand = @'
$gmailGuardArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT, '-RepositoryRoot', '.', '-BaselineRef', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD, '-ResumeBoundary', '-ExpectedOriginUrl', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, '-ExpectedHead', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD, '-ExpectedBranch', 'main')
if ($env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_CONFIRM -ceq '1') { $gmailGuardArguments += '-ConfirmApprovedLegacyLocalSurfaceChanges' }
& powershell.exe @gmailGuardArguments
'@
$refreshStagedSquashCommand = @'
$refreshScript = $env:FLUXKNOWLEDGE_CLOSEOUT_REFRESH_SCRIPT
& $refreshScript `
    -MainWorktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT `
    -FeatureWorktree $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE `
    -ExpectedMainHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD `
    -ExpectedStagedFeatureHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_STAGED_FEATURE_HEAD `
    -ExpectedFeatureHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD `
    -ExpectedFeatureBranch $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH `
    -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL `
    -DryRun:($env:FLUXKNOWLEDGE_CLOSEOUT_DRY_RUN -ceq '1')
'@
$verifyOriginMainCommand = @'
$headSha = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve HEAD before origin/main verification.' }
git fetch origin main
if ($LASTEXITCODE -ne 0) { throw 'Unable to fetch origin/main for verification.' }
$fetchedSha = (git rev-parse --verify 'FETCH_HEAD^{commit}').Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the explicitly fetched origin/main commit.' }
if ($headSha -ne $fetchedSha) { Write-Host "HEAD $headSha differs from fetched origin/main $fetchedSha"; exit 1 }
'@
$resumeBoundaryModule = Join-Path $PSScriptRoot 'ResumeGitBoundary.psm1'
$resumeCommitStatePath = Join-Path $script:LogRoot 'resume-main-commit.json'
$nativeCommandEnvironment['FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE'] = $resumeBoundaryModule
$nativeCommandEnvironment['FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH'] = $resumeCommitStatePath
$resumeBoundaryPrecommitCommand = @'
Import-Module $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE -Force
$boundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD -ExpectedBranch 'main'
try {
    $remote = Invoke-ResumeGit -Boundary $boundary -Arguments @('ls-remote', '--refs', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main')
    if ($remote.ExitCode -ne 0 -or $remote.StdOut.Trim() -cnotmatch ('^' + [regex]::Escape($env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD) + "`trefs/heads/main$")) { throw 'Authenticated origin/main did not match the expected main head.' }
} finally { Remove-ResumeGitBoundary -Boundary $boundary }
'@
$resumeBoundaryCommitCommand = @'
Import-Module $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE -Force
function Get-BoundaryValue { param($Boundary, [string[]]$Arguments)
    $result = Invoke-ResumeGit -Boundary $Boundary -Arguments $Arguments
    if ($result.ExitCode -ne 0) { throw "Authenticated Git $($Arguments[0]) failed." }
    return $result.StdOut.Trim()
}
function Test-BoundarySuccess { param($Boundary, [string[]]$Arguments)
    return (Invoke-ResumeGit -Boundary $Boundary -Arguments $Arguments).ExitCode -eq 0
}
$boundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD -ExpectedBranch 'main'
$featureBoundary = $null
try {
    $featureBoundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD -ExpectedBranch $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH
    $expectedTree = Get-BoundaryValue $boundary @('rev-parse', ($env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD + '^{tree}'))
    if (-not (Test-BoundarySuccess $boundary @('diff-index', '--cached', '--quiet', $expectedTree, '--'))) { throw 'Main index does not exactly match the reviewed feature tree.' }
    if (-not (Test-BoundarySuccess $boundary @('diff-files', '--quiet', '--no-ext-diff', '--ignore-submodules=none'))) { throw 'Main worktree is not clean before resume commit.' }
    if ((Get-BoundaryValue $boundary @('ls-files', '--others', '--exclude-standard')) -or (Get-BoundaryValue $boundary @('ls-files', '-u'))) { throw 'Main worktree has untracked or unmerged content before resume commit.' }
    $remote = Get-BoundaryValue $boundary @('ls-remote', '--refs', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main')
    if ($remote -cnotmatch ('^' + [regex]::Escape($env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD) + "`trefs/heads/main$")) { throw 'Authenticated origin/main advanced before resume commit.' }
    $headers = Get-BoundaryValue $boundary @('cat-file', '-p', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD)
    $author = [regex]::Match($headers, '(?m)^author (.+) <([^<>\r\n]+)> (\d+ [+-]\d{4})$')
    $committer = [regex]::Match($headers, '(?m)^committer (.+) <([^<>\r\n]+)> (\d+ [+-]\d{4})$')
    if (-not $author.Success -or -not $committer.Success) { throw 'Expected-main immutable author or committer headers are invalid.' }
    $identity = @{ GIT_AUTHOR_NAME = $author.Groups[1].Value; GIT_AUTHOR_EMAIL = $author.Groups[2].Value; GIT_AUTHOR_DATE = '@' + $author.Groups[3].Value; GIT_COMMITTER_NAME = $committer.Groups[1].Value; GIT_COMMITTER_EMAIL = $committer.Groups[2].Value; GIT_COMMITTER_DATE = '@' + $committer.Groups[3].Value }
    $created = Invoke-ResumeGit -Boundary $boundary -Arguments @('commit-tree', $expectedTree, '-p', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD) -StandardInput ($env:FLUXKNOWLEDGE_CLOSEOUT_COMMIT_MESSAGE + "`n") -Identity $identity -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($created.ExitCode -ne 0 -or $created.StdOut.Trim() -notmatch '^[0-9a-f]{40}$') { throw 'Authenticated commit-tree did not create the resume commit.' }
    $newCommit = $created.StdOut.Trim()
    $cas = Invoke-ResumeGit -Boundary $boundary -Arguments @('update-ref', 'refs/heads/main', $newCommit, $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD) -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($cas.ExitCode -ne 0) { throw 'Authenticated main ref compare-and-swap rejected the resume commit.' }
    @{ commit = $newCommit; expected_main = $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH -Encoding utf8
} finally {
    if ($null -ne $featureBoundary) { Remove-ResumeGitBoundary -Boundary $featureBoundary }
    Remove-ResumeGitBoundary -Boundary $boundary
}
'@
$resumeBoundaryPushCommand = @'
Import-Module $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE -Force
$state = Get-Content -Raw -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH | ConvertFrom-Json
if ($state.commit -notmatch '^[0-9a-f]{40}$' -or $state.expected_main -cne $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD) { throw 'Authenticated resume commit state is invalid.' }
$boundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $state.commit -ExpectedBranch 'main'
$featureBoundary = $null
try {
    $featureBoundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD -ExpectedBranch $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH
    $push = Invoke-ResumeGit -Boundary $boundary -Arguments @('push', '--porcelain', ('--force-with-lease=refs/heads/main:' + $state.expected_main), $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main:refs/heads/main') -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($push.ExitCode -ne 0) { throw 'Authenticated expected-old lease push rejected the resume commit.' }
    $remote = Invoke-ResumeGit -Boundary $boundary -Arguments @('ls-remote', '--refs', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main')
    if ($remote.ExitCode -ne 0 -or $remote.StdOut.Trim() -cnotmatch ('^' + [regex]::Escape($state.commit) + "`trefs/heads/main$")) { throw 'Authenticated origin/main did not equal the lease-pushed resume commit.' }
} finally {
    if ($null -ne $featureBoundary) { Remove-ResumeGitBoundary -Boundary $featureBoundary }
    Remove-ResumeGitBoundary -Boundary $boundary
}
'@
$resumeValidationRecordCommand = @'
Import-Module $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE -Force
function Get-ResumeBoundaryValue { param($Boundary, [string[]]$Arguments, [switch]$RequireNoInProgressOperation, $PeerBoundary = $null)
    $result = Invoke-ResumeGit -Boundary $Boundary -Arguments $Arguments -RequireNoInProgressOperation:$RequireNoInProgressOperation -PeerBoundary $PeerBoundary
    if ($result.ExitCode -ne 0) { throw "Authenticated validation-record Git $($Arguments[0]) failed." }
    return $result.StdOut.Trim()
}
$records = @('docs/operations/native-windows-phase-2-native-worker-supervision-validation.md', 'docs/operations/native-windows-phase-4-outlook-ingress-validation.md', 'docs/operations/native-windows-phase-5-retained-processors-validation.md')
$state = Get-Content -Raw -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH | ConvertFrom-Json
if ($state.commit -notmatch '^[0-9a-f]{40}$') { throw 'Authenticated resume validation-record state is invalid.' }
$boundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $state.commit -ExpectedBranch 'main'
$featureBoundary = $null
try {
    $featureBoundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD -ExpectedBranch $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH
    $cached = Invoke-ResumeGit -Boundary $boundary -Arguments @('diff-index', '--cached', '--quiet', $state.commit, '--')
    if ($cached.ExitCode -ne 0) { throw 'Post-deployment main index contains a pre-existing staged diff.' }
    $changed = @(Get-ResumeBoundaryValue $boundary (@('diff', '--name-only', $state.commit, '--') + $records) -split "`r?`n" | Where-Object { $_ })
    $allChanged = @(Get-ResumeBoundaryValue $boundary @('diff', '--name-only', $state.commit) -split "`r?`n" | Where-Object { $_ })
    $untracked = @(Get-ResumeBoundaryValue $boundary @('ls-files', '--others', '--exclude-standard') -split "`r?`n" | Where-Object { $_ })
    if (@($allChanged | Where-Object { $_ -notin $records }).Count -gt 0 -or @($changed | Where-Object { $_ -notin $records }).Count -gt 0 -or @($untracked | Where-Object { $_ -notin $records }).Count -gt 0) { throw 'Post-deployment changed, staged or untracked a path outside the authenticated validation-record allowlist.' }
    $index = Invoke-ResumeGit -Boundary $boundary -Arguments (@('update-index', '--add', '--remove', '--') + $records) -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($index.ExitCode -ne 0) { throw 'Authenticated validation-record index update failed.' }
    Assert-ResumeGitPairNoInProgressOperation $boundary $featureBoundary
    $tree = Get-ResumeBoundaryValue $boundary @('write-tree') -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    $parentTree = Get-ResumeBoundaryValue $boundary @('rev-parse', ($state.commit + '^{tree}'))
    if ($tree -ceq $parentTree) {
        Assert-ResumeGitPairNoInProgressOperation $boundary $featureBoundary
        @{ commit = $state.commit; expected_main = $state.commit; no_op = $true } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH -Encoding utf8
        return
    }
    $headers = Get-ResumeBoundaryValue $boundary @('cat-file', '-p', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD)
    $author = [regex]::Match($headers, '(?m)^author (.+) <([^<>\r\n]+)> (\d+ [+-]\d{4})$'); $committer = [regex]::Match($headers, '(?m)^committer (.+) <([^<>\r\n]+)> (\d+ [+-]\d{4})$')
    if (-not $author.Success -or -not $committer.Success) { throw 'Expected-main immutable identity headers are invalid for validation records.' }
    $identity = @{ GIT_AUTHOR_NAME = $author.Groups[1].Value; GIT_AUTHOR_EMAIL = $author.Groups[2].Value; GIT_AUTHOR_DATE = '@' + $author.Groups[3].Value; GIT_COMMITTER_NAME = $committer.Groups[1].Value; GIT_COMMITTER_EMAIL = $committer.Groups[2].Value; GIT_COMMITTER_DATE = '@' + $committer.Groups[3].Value }
    $created = Invoke-ResumeGit -Boundary $boundary -Arguments @('commit-tree', $tree, '-p', $state.commit) -StandardInput "docs: record native ingress validation`n" -Identity $identity -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($created.ExitCode -ne 0 -or $created.StdOut.Trim() -notmatch '^[0-9a-f]{40}$') { throw 'Authenticated validation-record commit-tree failed.' }
    $newCommit = $created.StdOut.Trim()
    if ((Invoke-ResumeGit -Boundary $boundary -Arguments @('update-ref', 'refs/heads/main', $newCommit, $state.commit) -RequireNoInProgressOperation -PeerBoundary $featureBoundary).ExitCode -ne 0) { throw 'Authenticated validation-record ref CAS rejected.' }
    @{ commit = $newCommit; expected_main = $state.commit } | ConvertTo-Json -Compress | Set-Content -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH -Encoding utf8
} finally {
    if ($null -ne $featureBoundary) { Remove-ResumeGitBoundary -Boundary $featureBoundary }
    Remove-ResumeGitBoundary -Boundary $boundary
}
'@
$resumeValidationRecordPushCommand = @'
Import-Module $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE -Force
$state = Get-Content -Raw -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH | ConvertFrom-Json
if ($state.commit -notmatch '^[0-9a-f]{40}$' -or $state.expected_main -notmatch '^[0-9a-f]{40}$') { throw 'Authenticated validation-record push state is invalid.' }
$boundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $state.commit -ExpectedBranch 'main'
$featureBoundary = $null
try {
    $featureBoundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD -ExpectedBranch $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH
    if ($state.no_op) {
        $remote = Invoke-ResumeGit -Boundary $boundary -Arguments @('ls-remote', '--refs', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main')
        if ($remote.ExitCode -ne 0 -or $remote.StdOut.Trim() -cnotmatch ('^' + [regex]::Escape($state.commit) + "`trefs/heads/main$")) { throw 'Authenticated no-op validation-record remote recheck failed.' }
        return
    }
    $push = Invoke-ResumeGit -Boundary $boundary -Arguments @('push', '--porcelain', ('--force-with-lease=refs/heads/main:' + $state.expected_main), $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main:refs/heads/main') -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($push.ExitCode -ne 0) { throw 'Authenticated validation-record expected-old lease push rejected.' }
    $remote = Invoke-ResumeGit -Boundary $boundary -Arguments @('ls-remote', '--refs', $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL, 'refs/heads/main')
    if ($remote.ExitCode -ne 0 -or $remote.StdOut.Trim() -cnotmatch ('^' + [regex]::Escape($state.commit) + "`trefs/heads/main$")) { throw 'Authenticated validation-record remote recheck failed.' }
} finally {
    if ($null -ne $featureBoundary) { Remove-ResumeGitBoundary -Boundary $featureBoundary }
    Remove-ResumeGitBoundary -Boundary $boundary
}
'@
$resumeCleanupCommand = @'
Import-Module $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE -Force
$state = Get-Content -Raw -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH | ConvertFrom-Json
$mainBoundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $state.commit -ExpectedBranch 'main'
$featureBoundary = $null
try {
    $featureBoundary = New-ResumeGitBoundary -Worktree $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE -ExpectedOriginUrl $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL -ExpectedHead $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD -ExpectedBranch $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH
    foreach ($arguments in @(@('diff-files', '--quiet', '--no-ext-diff', '--ignore-submodules=none'), @('diff-index', '--cached', '--quiet', 'HEAD', '--'))) {
        if ((Invoke-ResumeGit -Boundary $featureBoundary -Arguments $arguments).ExitCode -ne 0) { throw 'Authenticated cleanup requires a clean feature worktree and index.' }
    }
    if ((Invoke-ResumeGit -Boundary $featureBoundary -Arguments @('ls-files', '--others', '--exclude-standard')).StdOut.Trim() -or
        (Invoke-ResumeGit -Boundary $featureBoundary -Arguments @('ls-files', '-u')).StdOut.Trim()) { throw 'Authenticated cleanup requires no untracked or unmerged feature content.' }
    $worktreeLines = @( (Invoke-ResumeGit -Boundary $mainBoundary -Arguments @('worktree', 'list', '--porcelain')).StdOut -split "`r?`n" )
    $registeredFeature = $false
    $currentPath = ''
    foreach ($line in $worktreeLines) {
        if ($line.StartsWith('worktree ')) { $currentPath = [System.IO.Path]::GetFullPath($line.Substring('worktree '.Length).Replace('/', [System.IO.Path]::DirectorySeparatorChar)); continue }
        if ($line -ceq ('branch refs/heads/' + $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH) -and $currentPath.Equals((Resolve-Path -LiteralPath $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE).Path, [System.StringComparison]::OrdinalIgnoreCase)) { $registeredFeature = $true }
    }
    if (-not $registeredFeature) { throw 'Authenticated cleanup could not verify the registered expected feature worktree and branch.' }
    $result = Invoke-ResumeGit -Boundary $mainBoundary -Arguments @('worktree', 'remove', $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE) -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($result.ExitCode -ne 0) { throw 'Authenticated cleanup Git worktree remove failed.' }
    $result = Invoke-ResumeGit -Boundary $mainBoundary -Arguments @('branch', '-D', $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH) -RequireNoInProgressOperation
    if ($result.ExitCode -ne 0) { throw 'Authenticated cleanup Git branch deletion failed.' }
} finally {
    if ($null -ne $featureBoundary) { Remove-ResumeGitBoundary -Boundary $featureBoundary }
    Remove-ResumeGitBoundary -Boundary $mainBoundary
}
'@
$nativeDeployCommand = @'
$applyMigrations = $env:FLUXKNOWLEDGE_CLOSEOUT_APPLY_MIGRATIONS -ceq "1"
if ($env:FLUXKNOWLEDGE_CLOSEOUT_KEEP_OUTLOOK_HOST_DISABLED -cne "1") {
    throw "Outlook host activation is not authorised; the closeout child must keep it disabled."
}
& .\scripts\deploy\update-native-windows.ps1 `
    -SiteName $env:FLUXKNOWLEDGE_CLOSEOUT_SITE_NAME `
    -SiteUrl $env:FLUXKNOWLEDGE_CLOSEOUT_SITE_URL `
    -DeployRoot $env:FLUXKNOWLEDGE_CLOSEOUT_DEPLOY_ROOT `
    -BackupRoot $env:FLUXKNOWLEDGE_CLOSEOUT_BACKUP_ROOT `
    -ApplyMigrations:$applyMigrations `
    -ConfirmApplyMigrations:$applyMigrations `
    -KeepOutlookHostDisabled
'@
$nativeWorkerValidationRecord = "docs\operations\native-windows-phase-2-native-worker-supervision-validation.md"
$nativeWorkerValidationCommand = @'
& .\scripts\deploy\validate-native-worker-supervision.ps1 `
    -SiteUrl $env:FLUXKNOWLEDGE_CLOSEOUT_SITE_URL `
    -DeployRoot $env:FLUXKNOWLEDGE_CLOSEOUT_DEPLOY_ROOT `
    -ExpectedMigrationId '20260810185641_AddNativeWorkerSupervision' `
    -ValidationRecordPath 'docs\operations\native-windows-phase-2-native-worker-supervision-validation.md'
'@
$nativeOutlookValidationRecord = "docs\operations\native-windows-phase-4-outlook-ingress-validation.md"
$nativeOutlookValidationCommand = @'
& .\scripts\deploy\validate-native-outlook-ingress.ps1 `
    -SiteUrl $env:FLUXKNOWLEDGE_CLOSEOUT_SITE_URL `
    -DeployRoot $env:FLUXKNOWLEDGE_CLOSEOUT_DEPLOY_ROOT `
    -ValidationRecordPath 'docs\operations\native-windows-phase-4-outlook-ingress-validation.md'
'@
$phase5ValidationRecord = "docs\operations\native-windows-phase-5-retained-processors-validation.md"
$phase5ValidationCommand = @'
& .\scripts\deploy\validate-phase-5-deployment.ps1 `
    -SiteUrl $env:FLUXKNOWLEDGE_CLOSEOUT_SITE_URL `
    -DeployRoot $env:FLUXKNOWLEDGE_CLOSEOUT_DEPLOY_ROOT `
    -ValidationRecordPath 'docs\operations\native-windows-phase-5-retained-processors-validation.md'
'@

try {
    if (-not $ResumeStagedSquash) {
        Invoke-FeatureStep -Name "verify-main-clean" -Cwd $MainRoot -Command 'if ((git status --porcelain) -ne $null) { git status --short; exit 1 }' -RunInDryRun
    }
    Invoke-FeatureStep -Name "dotnet-tool-restore" -Cwd $FeatureWorktree -Command 'dotnet tool restore'
    Invoke-FeatureStep -Name "dotnet-restore-locked" -Cwd $FeatureWorktree -Command 'dotnet restore FluxKnowledge.slnx --locked-mode'
    Invoke-FeatureStep -Name "dotnet-build-release" -Cwd $FeatureWorktree -Command 'dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror'
    Invoke-FeatureStep -Name "dotnet-test-native" -Cwd $FeatureWorktree -Command 'dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"' -TimeoutSeconds $TestStepTimeoutSeconds
    Invoke-FeatureStep -Name "native-closeout-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\complete-feature-dryrun.ps1'
    Invoke-FeatureStep -Name "native-outlook-scheduled-host-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\outlook-scheduled-host-contract.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "native-outlook-host-composition" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\outlook-host-composition.ps1'
    Invoke-FeatureStep -Name "native-deployment-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\native-deployment-plan.ps1'
    Invoke-FeatureStep -Name "phase-5-deployment-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\phase-5-deployment-safety.ps1'
    Invoke-FeatureStep -Name "approved-local-surface-gmail-guard-contract" -Cwd $FeatureWorktree -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native\legacy-gmail-approved-local-surfaces.ps1'
    Invoke-FeatureStep -Name "legacy-gmail-regression" -Cwd $FeatureWorktree -Command $gmailRegressionCommand -TimeoutSeconds $TestStepTimeoutSeconds
    if ($ResumeStagedSquash) {
        Invoke-FeatureStep -Name "legacy-gmail-preservation-diff-guard" -Cwd $FeatureWorktree -Command $resumeFeatureGmailGuardCommand -Environment $nativeCommandEnvironment
    } else {
        Invoke-FeatureStep -Name "legacy-gmail-preservation-diff-guard" -Cwd $FeatureWorktree -Command "powershell -NoProfile -ExecutionPolicy Bypass -File '$safeGmailGuardScript' -RepositoryRoot . -BaselineRef main$gmailGuardConfirmationArgument" -RunInDryRun
    }
    if ($ResumeStagedSquash) {
        Invoke-FeatureStep -Name "refresh-staged-squash" -Cwd $MainRoot -Command $refreshStagedSquashCommand -Environment $nativeCommandEnvironment -RunInDryRun
    } else {
        Invoke-FeatureStep -Name "feature-commit" -Cwd $FeatureWorktree -Command "git add -A; if ((git status --porcelain) -ne `$null) { git commit -m '$safeCommitMessage' }"
        Invoke-FeatureStep -Name "sync-main" -Cwd $MainRoot -Command 'git pull --ff-only origin main'
        Invoke-FeatureStep -Name "squash-merge" -Cwd $MainRoot -Command "git merge --squash '$safeBranch'"
    }
    Invoke-FeatureStep -Name "dotnet-tool-restore-main" -Cwd $MainRoot -Command 'dotnet tool restore'
    Invoke-FeatureStep -Name "dotnet-restore-locked-main" -Cwd $MainRoot -Command 'dotnet restore FluxKnowledge.slnx --locked-mode'
    Invoke-FeatureStep -Name "dotnet-build-release-main" -Cwd $MainRoot -Command 'dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror'
    Invoke-FeatureStep -Name "dotnet-test-native-main" -Cwd $MainRoot -Command 'dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"' -TimeoutSeconds $TestStepTimeoutSeconds
    Invoke-FeatureStep -Name "legacy-gmail-regression-main" -Cwd $MainRoot -Command $gmailRegressionCommand -TimeoutSeconds $TestStepTimeoutSeconds
    if ($ResumeStagedSquash) {
        Invoke-FeatureStep -Name "legacy-gmail-preservation-diff-guard-main" -Cwd $MainRoot -Command $resumeMainGmailGuardCommand -Environment $nativeCommandEnvironment
    } else {
        Invoke-FeatureStep -Name "legacy-gmail-preservation-diff-guard-main" -Cwd $MainRoot -Command "powershell -NoProfile -ExecutionPolicy Bypass -File '$safeGmailGuardScript' -RepositoryRoot . -BaselineRef HEAD$gmailGuardConfirmationArgument" -RunInDryRun
    }
    if ($ResumeStagedSquash) {
        Invoke-FeatureStep -Name "verify-resume-main-precommit" -Cwd $MainRoot -Command $resumeBoundaryPrecommitCommand -Environment $nativeCommandEnvironment
        Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command $resumeBoundaryCommitCommand -Environment $nativeCommandEnvironment
    } else {
        Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command "if ((git status --porcelain) -ne `$null) { git commit -m '$safeCommitMessage' } else { 'No staged changes to commit.' }"
    }
    if ($ResumeStagedSquash) {
        Invoke-FeatureStep -Name "verify-resume-main-commit" -Cwd $MainRoot -Command $resumeBoundaryPushCommand -Environment $nativeCommandEnvironment
        Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command '$null = "authenticated expected-old lease push completed by verify-resume-main-commit"' -Environment $nativeCommandEnvironment
    } else {
        Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command 'git push origin main'
    }
    if (-not $ResumeStagedSquash) {
        Invoke-FeatureStep -Name "verify-origin-main" -Cwd $MainRoot -Command $verifyOriginMainCommand
    }

    if (-not $SkipDeploy) {
        Invoke-FeatureStep -Name "deploy-native-windows" -Cwd $MainRoot -Command $nativeDeployCommand -Environment $nativeCommandEnvironment -TimeoutSeconds $DeployStepTimeoutSeconds
        Invoke-FeatureStep -Name "post-deploy-native-worker-supervision-validation" -Cwd $MainRoot -Command $nativeWorkerValidationCommand -Environment $nativeCommandEnvironment -TimeoutSeconds $DeployStepTimeoutSeconds
        Invoke-FeatureStep -Name "post-deploy-native-outlook-ingress-validation" -Cwd $MainRoot -Command $nativeOutlookValidationCommand -Environment $nativeCommandEnvironment -TimeoutSeconds $DeployStepTimeoutSeconds
        Invoke-FeatureStep -Name "post-deploy-phase-5-validation" -Cwd $MainRoot -Command $phase5ValidationCommand -Environment $nativeCommandEnvironment -TimeoutSeconds $DeployStepTimeoutSeconds
        if ($ResumeStagedSquash) {
            Invoke-FeatureStep -Name "post-deploy-validation-record-commit" -Cwd $MainRoot -Command $resumeValidationRecordCommand -Environment $nativeCommandEnvironment
            Invoke-FeatureStep -Name "post-deploy-validation-record-push" -Cwd $MainRoot -Command $resumeValidationRecordPushCommand -Environment $nativeCommandEnvironment
        } else {
            Invoke-FeatureStep -Name "post-deploy-validation-record-commit" -Cwd $MainRoot -Command "git add -- '$nativeWorkerValidationRecord' '$nativeOutlookValidationRecord' '$phase5ValidationRecord'; if ((git status --porcelain) -ne `$null) { git commit -m 'docs: record native ingress validation' }"
            Invoke-FeatureStep -Name "post-deploy-validation-record-push" -Cwd $MainRoot -Command 'git push origin main'
        }
    }

    if (-not $KeepWorktree) {
        $previousMainRoot = $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT
        $previousFeatureWorktree = $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE
        $previousBranch = $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH
        try {
            $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $MainRoot
            $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $FeatureWorktree
            $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH = $Branch
            if ($ResumeStagedSquash) {
                Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command $resumeCleanupCommand -Environment $nativeCommandEnvironment
            } else {
                Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command $CleanupWorktreeCommand
            }
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
