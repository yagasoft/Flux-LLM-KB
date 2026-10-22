[CmdletBinding()]
param(
    [string]$SourceRoot = '',
    [string]$CloseoutScript = '',
    [string[]]$Scenario = @('success', 'suite-failure', 'contract-failure', 'unstaged-drift',
        'staged-drift', 'main-drift', 'main-dirty', 'feature-drift', 'commit-hook-drift',
        'merge-conflict', 'push-failure', 'lock-contention', 'git-timeout', 'cleanup-branch-switch',
        'candidate-cleanup-head-drift', 'candidate-cleanup-branch-switch', 'timeout'),
    [ValidateRange(1, 4)][int]$MaxParallelScenarios = 4
)

$ErrorActionPreference = 'Stop'
if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeout = if ($CloseoutScript) { (Resolve-Path -LiteralPath $CloseoutScript).Path }
    else { Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1' }
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeWorkflow-$([Guid]::NewGuid().ToString('N'))"
$pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
if ($MaxParallelScenarios -gt 1 -and $Scenario.Count -gt 1) {
    Import-Module (Join-Path $PSScriptRoot 'WorkflowScenarioRunner.psm1') -Force
    $passed = $false
    try {
        Invoke-WorkflowScenarios -ScriptPath $PSCommandPath -SourceRoot $SourceRoot -CloseoutScript $closeout `
            -Scenario $Scenario -MaxParallelScenarios $MaxParallelScenarios -OutputRoot $temporaryRoot |
            ForEach-Object { Write-Output $_.Stdout.TrimEnd() }
        $passed = $true
    } finally {
        if ($passed -and (Test-Path -LiteralPath $temporaryRoot)) {
            $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
            if ($resolved -ne [IO.Path]::GetFullPath($temporaryRoot) -or
                (Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeWorkflow-*') { throw 'Unsafe aggregate cleanup path.' }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        } elseif (Test-Path -LiteralPath $temporaryRoot) {
            Write-Warning "Workflow scenario evidence preserved at $temporaryRoot"
        }
    }
    return
}
$savedEnvironment = @{}
foreach ($name in @('PATH', 'FLUXKNOWLEDGE_WORKFLOW_CASE', 'FLUXKNOWLEDGE_WORKFLOW_LOG',
    'FLUXKNOWLEDGE_WORKFLOW_MAIN', 'FLUXKNOWLEDGE_WORKFLOW_FEATURE',
    'FLUXKNOWLEDGE_WORKFLOW_CHILD', 'FLUXKNOWLEDGE_WORKFLOW_GIT_LOG',
    'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Git([string]$Root, [string[]]$Arguments) {
    $output = & git -C $Root @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git failed: $($output -join [Environment]::NewLine)" }
    return $output
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $bin = New-Item -ItemType Directory -Path (Join-Path $temporaryRoot 'bin')
    $fakeDotnet = Join-Path $bin 'dotnet.ps1'
    @'
$ErrorActionPreference = 'Stop'
$record = @{ arguments = @($args); cwd = (Get-Location).Path; artifacts = $env:FluxKnowledgeArtifactsPath
    bootstrap_present = -not [string]::IsNullOrEmpty($env:FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP) }
$record | ConvertTo-Json -Compress | Add-Content -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_LOG
if ($args[0] -ne 'test') { exit 0 }
$case = $env:FLUXKNOWLEDGE_WORKFLOW_CASE
switch ($case) {
    'suite-failure' { exit 23 }
    'unstaged-drift' { Add-Content -LiteralPath 'README.md' -Value 'unverified mutation' }
    'staged-drift' {
        Add-Content -LiteralPath 'README.md' -Value 'unverified staged mutation'
        git add README.md
    }
    'main-drift' {
        Set-Content -LiteralPath (Join-Path $env:FLUXKNOWLEDGE_WORKFLOW_MAIN 'concurrent.txt') -Value 'another owner'
        git -C $env:FLUXKNOWLEDGE_WORKFLOW_MAIN add concurrent.txt
        git -C $env:FLUXKNOWLEDGE_WORKFLOW_MAIN commit -m 'concurrent main work' | Out-Null
    }
    'main-dirty' {
        Set-Content -LiteralPath (Join-Path $env:FLUXKNOWLEDGE_WORKFLOW_MAIN 'concurrent.txt') -Value 'uncommitted owner'
    }
    'feature-drift' {
        Add-Content -LiteralPath (Join-Path $env:FLUXKNOWLEDGE_WORKFLOW_FEATURE 'README.md') -Value 'new feature work'
    }
    'commit-hook-drift' {
        $hookRoot = Join-Path $env:FLUXKNOWLEDGE_WORKFLOW_MAIN '.agents\hooks'
        New-Item -ItemType Directory -Force -Path $hookRoot | Out-Null
        [IO.File]::WriteAllText((Join-Path $hookRoot 'pre-commit'),
            ("#!/bin/sh", "echo unverified-hook-change >> README.md", "git add README.md", "" -join [char]10),
            [Text.UTF8Encoding]::new($false))
        git config core.hooksPath ($hookRoot.Replace('\', '/'))
    }
    'timeout' {
        $childScript = Join-Path $env:FLUXKNOWLEDGE_WORKFLOW_MAIN '.agents\hang.ps1'
        Set-Content -LiteralPath $childScript -Value 'Start-Sleep -Seconds 90'
        $childArguments = @('-NoProfile', '-File', ('"' + $childScript + '"'))
        $child = Start-Process -FilePath (Get-Command pwsh).Source -WindowStyle Hidden -PassThru -ArgumentList $childArguments
        Set-Content -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_CHILD -Value $child.Id
        Start-Sleep -Seconds 90
    }
}
exit 0
'@ | Set-Content -LiteralPath $fakeDotnet
    # Only the .NET workload is substituted. Git, commits, worktrees, hooks, pushes
    # and all process/failure handling execute through the real closeout script.
    @('@echo off', ('"{0}" -NoProfile -File "{1}" %*' -f $pwsh, $fakeDotnet),
        'exit /b %errorlevel%') | Set-Content -LiteralPath (Join-Path $bin 'dotnet.cmd')
    $env:PATH = $bin.FullName + [IO.Path]::PathSeparator + $savedEnvironment.PATH

    foreach ($case in $Scenario) {
        $caseRoot = New-Item -ItemType Directory -Path (Join-Path $temporaryRoot $case)
        $main = Join-Path $caseRoot 'main'
        $feature = Join-Path $caseRoot 'feature'
        $remote = Join-Path $caseRoot 'origin.git'
        & git init --bare --initial-branch main $remote 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) 'Cannot create disposable bare origin.'
        & git clone $remote $main 2>&1 | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) 'Cannot clone disposable origin.'
        Invoke-Git $main @('config', 'user.name', 'Closeout contract') | Out-Null
        Invoke-Git $main @('config', 'user.email', 'closeout@example.invalid') | Out-Null
        Invoke-Git $main @('config', 'core.longpaths', 'true') | Out-Null
        Set-Content -LiteralPath (Join-Path $main '.gitignore') -Value @('.agents/', 'artifacts/')
        Set-Content -LiteralPath (Join-Path $main 'README.md') -Value 'base content'
        $nativeRoot = New-Item -ItemType Directory -Path (Join-Path $main 'tests\native')
        foreach ($contract in @('repository-contract', 'complete-feature-dryrun',
            'complete-feature-integration', 'complete-feature-parallel-contract', 'complete-feature-bootstrap-nondryrun', 'native-go-live-contract',
            'native-go-live-recovery-removal-contract', 'native-deployment-plan', 'test-timing-summary-contract')) {
            @'
param([string]$SourceRoot)
if ($env:FLUXKNOWLEDGE_WORKFLOW_CASE -eq 'contract-failure') { exit 24 }
Write-Output 'Disposable native contract passed.'
'@ | Set-Content -LiteralPath (Join-Path $nativeRoot "$contract.ps1")
        }
        Invoke-Git $main @('add', '.') | Out-Null
        Invoke-Git $main @('commit', '-m', 'base') | Out-Null
        Invoke-Git $main @('push', '-u', 'origin', 'main') | Out-Null
        Invoke-Git $main @('worktree', 'add', '-b', 'codex/contract', $feature) | Out-Null
        Set-Content -LiteralPath (Join-Path $feature 'README.md') -Value 'feature content'
        if ($case -eq 'merge-conflict') {
            Set-Content -LiteralPath (Join-Path $main 'README.md') -Value 'independent main content'
            Invoke-Git $main @('add', '.') | Out-Null
            Invoke-Git $main @('commit', '-m', 'independent main') | Out-Null
            Invoke-Git $main @('push', 'origin', 'main') | Out-Null
        }
        if ($case -eq 'push-failure') {
            [IO.File]::WriteAllText((Join-Path $remote 'hooks\pre-receive'),
                ("#!/bin/sh", "exit 1", "" -join [char]10), [Text.UTF8Encoding]::new($false))
        }
        $gitHookGuard = @(
            '#!/bin/sh',
            'if [ -n "$FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP" ]; then',
            '  echo inherited >> "$FLUXKNOWLEDGE_WORKFLOW_GIT_LOG"',
            '  exit 1',
            'fi'
        )
        [IO.File]::WriteAllText((Join-Path $main '.git\hooks\post-merge'),
            (($gitHookGuard + @('')) -join [char]10), [Text.UTF8Encoding]::new($false))
        $prePush = $gitHookGuard
        if ($case -eq 'cleanup-branch-switch') {
            $prePush += 'git -C "$FLUXKNOWLEDGE_WORKFLOW_FEATURE" switch -c other-owner'
        }
        if ($case -in @('candidate-cleanup-head-drift', 'candidate-cleanup-branch-switch')) {
            $mutationRoot = New-Item -ItemType Directory -Force -Path (Join-Path $main '.agents')
            $mutation = Join-Path $mutationRoot 'mutate-candidate.ps1'
            @'
$ErrorActionPreference = 'Stop'
$candidates = @(& git -C $env:FLUXKNOWLEDGE_WORKFLOW_MAIN worktree list --porcelain |
    Where-Object { $_.StartsWith('worktree ') -and (Split-Path -Leaf $_.Substring(9)) -like 'candidate-*' } |
    ForEach-Object { $_.Substring(9) })
if ($LASTEXITCODE -ne 0 -or $candidates.Count -ne 1) { throw 'Expected one disposable candidate.' }
$candidate = $candidates[0]
if ($env:FLUXKNOWLEDGE_WORKFLOW_CASE -eq 'candidate-cleanup-branch-switch') {
    & git -C $candidate switch -c other-candidate-owner
    if ($LASTEXITCODE -ne 0) { throw 'Cannot switch candidate ownership.' }
} else {
    Add-Content -LiteralPath (Join-Path $candidate 'README.md') -Value 'new candidate owner work'
    & git -C $candidate add README.md
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stage candidate owner work.' }
    & git -C $candidate commit -m 'new candidate owner work'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit candidate owner work.' }
}
'@ | Set-Content -LiteralPath $mutation
            $prePush += ('"{0}" -NoProfile -File "{1}"' -f $pwsh.Replace('\', '/'), $mutation.Replace('\', '/'))
        }
        [IO.File]::WriteAllText((Join-Path $main '.git\hooks\pre-push'),
            (($prePush + @('')) -join [char]10), [Text.UTF8Encoding]::new($false))
        if ($case -eq 'git-timeout') {
            $hangRoot = New-Item -ItemType Directory -Force -Path (Join-Path $main '.agents')
            $hang = Join-Path $hangRoot 'hang-git.ps1'
            Set-Content -LiteralPath $hang -Value 'Set-Content -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_CHILD -Value $PID; Start-Sleep -Seconds 90'
            $hangHook = $gitHookGuard + @(
                'if [ "$(git symbolic-ref -q --short HEAD)" = "main" ]; then',
                ('"{0}" -NoProfile -File "{1}"' -f $pwsh.Replace('\', '/'), $hang.Replace('\', '/')),
                'fi', '')
            [IO.File]::WriteAllText((Join-Path $main '.git\hooks\post-merge'),
                ($hangHook -join [char]10), [Text.UTF8Encoding]::new($false))
        }
        $initialMain = (Invoke-Git $main @('rev-parse', 'HEAD') | Out-String).Trim()
        $env:FLUXKNOWLEDGE_WORKFLOW_CASE = $case
        $env:FLUXKNOWLEDGE_WORKFLOW_LOG = Join-Path $caseRoot 'dotnet.jsonl'
        $env:FLUXKNOWLEDGE_WORKFLOW_MAIN = $main
        $env:FLUXKNOWLEDGE_WORKFLOW_FEATURE = $feature
        $env:FLUXKNOWLEDGE_WORKFLOW_CHILD = Join-Path $caseRoot 'child.pid'
        $env:FLUXKNOWLEDGE_WORKFLOW_GIT_LOG = Join-Path $caseRoot 'git-inherited-secret.txt'
        $env:FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP = 'synthetic-secret-must-not-reach-children'
        $arguments = @('-NoProfile', '-File', $closeout, '-FeatureWorktree', $feature,
            '-MainRoot', $main, '-CommitMessage', 'verified candidate')
        if ($case -eq 'timeout') { $arguments += @('-TestStepTimeoutSeconds', '3') }
        if ($case -eq 'git-timeout') { $arguments += @('-StepTimeoutSeconds', '5') }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $heldLock = $null
        try {
            if ($case -eq 'lock-contention') {
                $heldLock = [IO.File]::Open((Join-Path $main '.git\fluxknowledge-closeout.lock'),
                    [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            }
            $output = & $pwsh @arguments 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
        } finally {
            if ($heldLock) { $heldLock.Dispose() }
        }
        try { $summary = $output | ConvertFrom-Json }
        catch { throw "Case '$case' returned invalid JSON: $output" }
        $calls = if (Test-Path -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_LOG) {
            @(Get-Content -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_LOG | ConvertFrom-Json)
        } else { @() }
        Assert-True (@($calls | Where-Object bootstrap_present).Count -eq 0) 'A child inherited bootstrap data.'
        Assert-True (-not (Test-Path -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_GIT_LOG)) 'A Git hook inherited bootstrap data.'
        Assert-True (-not $output.Contains('synthetic-secret-must-not-reach-children')) 'Summary exposed bootstrap data.'
        $tests = @($calls | Where-Object { $_.arguments[0] -eq 'test' })
        if ($case -in @('merge-conflict', 'lock-contention')) {
            Assert-True ($tests.Count -eq 0) 'A conflicted candidate or busy repository must fail before expensive tests.'
        }
        if ($case -notin @('merge-conflict', 'lock-contention')) {
            $failureDetail = if ($summary.log_path -and (Test-Path -LiteralPath $summary.log_path)) {
                Get-Content -LiteralPath $summary.log_path -Tail 8 | Out-String
            } else { '' }
            Assert-True ($tests.Count -eq 1) "Expected one full suite, observed $($tests.Count) in $case. Failed step: $($summary.failed_step). $failureDetail"
            Assert-True ($tests[0].arguments -contains 'FluxKnowledge.slnx' -and
                $tests[0].arguments -notcontains '--filter') 'Closeout must verify the entire solution.'
            Assert-True ($tests[0].arguments -contains '-m:1' -or
                (($tests[0].arguments -join ' ') -match '(?:^| )-m:?\s+1(?: |$)')) "Test projects must remain sequential: $($tests[0].arguments -join ' | ')"
            Assert-True ($tests[0].cwd -ne $main -and $tests[0].cwd -ne $feature) 'Checks must run on the merged candidate.'
            $expectedArtifacts = (Join-Path $tests[0].cwd 'artifacts') + [IO.Path]::DirectorySeparatorChar
            Assert-True ($tests[0].artifacts -ceq $expectedArtifacts) 'Candidate artifacts require their own root and a trailing separator for worker payload copying.'
        }
        $mainHead = (Invoke-Git $main @('rev-parse', 'HEAD') | Out-String).Trim()
        $remoteHead = (Invoke-Git $main @('ls-remote', 'origin', 'refs/heads/main') | Out-String).Trim().Split([char]9)[0]
        if ($case -eq 'success') {
            Assert-True ($exitCode -eq 0 -and $summary.ok) "Successful closeout failed: $output"
            Assert-True ($mainHead -eq $remoteHead -and $mainHead -eq $summary.verified_commit) 'Published SHA differs from verified commit.'
            $tree = (Invoke-Git $main @('rev-parse', 'HEAD^{tree}') | Out-String).Trim()
            Assert-True ($tree -eq $summary.candidate_tree) 'Integrated tree differs from tested tree.'
            Assert-True (-not (Test-Path -LiteralPath $feature)) 'Successful closeout did not clean the feature checkout.'
            Assert-True (-not (Test-Path -LiteralPath $summary.candidate_root)) 'Successful closeout did not clean the candidate.'
        } else {
            Assert-True ($exitCode -ne 0 -and -not $summary.ok) "Failure case succeeded: $case"
            Assert-True (-not [string]::IsNullOrWhiteSpace($summary.failed_step)) 'Failure lost its step.'
            Assert-True (Test-Path -LiteralPath $summary.log_path) 'Failure lost its log.'
            Assert-True (Test-Path -LiteralPath $feature) 'Failure removed feature work.'
            if ($case -in @('cleanup-branch-switch', 'candidate-cleanup-head-drift', 'candidate-cleanup-branch-switch')) {
                Assert-True ($remoteHead -eq $mainHead -and $remoteHead -eq $summary.verified_commit) 'Cleanup failure changed the pinned push.'
                if ($case -eq 'cleanup-branch-switch') {
                    $otherBranch = (Invoke-Git $feature @('branch', '--show-current') | Out-String).Trim()
                    Assert-True ($otherBranch -eq 'other-owner') "Cleanup altered another owner's branch."
                } else {
                    Assert-True ($summary.failed_step -eq 'cleanup-candidate') 'Candidate drift must refuse its cleanup.'
                    Assert-True (Test-Path -LiteralPath $summary.candidate_root) "Cleanup removed another owner's candidate."
                    if ($case -eq 'candidate-cleanup-head-drift') {
                        $otherHead = (Invoke-Git $summary.candidate_root @('rev-parse', 'HEAD') | Out-String).Trim()
                        Assert-True ($otherHead -ne $summary.verified_commit) 'Candidate owner commit was lost.'
                    } else {
                        $otherBranch = (Invoke-Git $summary.candidate_root @('branch', '--show-current') | Out-String).Trim()
                        Assert-True ($otherBranch -eq 'other-candidate-owner') 'Candidate ownership was lost.'
                    }
                }
            } else {
                Assert-True ($remoteHead -eq $initialMain) "Failure published unverified content: $case"
            }
            if ($case -notin @('main-drift', 'push-failure', 'git-timeout', 'cleanup-branch-switch',
                'candidate-cleanup-head-drift', 'candidate-cleanup-branch-switch')) {
                Assert-True ($mainHead -eq $initialMain) "Failed candidate moved canonical main: $case"
            }
            if ($case -in @('timeout', 'git-timeout')) {
                $timeoutStep = if ($case -eq 'timeout') { 'dotnet-test-native-candidate' } else { 'promote-candidate' }
                Assert-True ($summary.failed_step -eq $timeoutStep) 'Timeout was not attributed to its step.'
                $childPid = [int](Get-Content -LiteralPath $env:FLUXKNOWLEDGE_WORKFLOW_CHILD)
                Assert-True ($null -eq (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) 'Timeout left a descendant process running.'
            }
            if ($case -eq 'push-failure') {
                # Retry the actual script after the bare remote becomes available.
                # An empty squash must reuse the promoted commit and preserve evidence.
                Remove-Item -LiteralPath (Join-Path $remote 'hooks\pre-receive')
                $env:FLUXKNOWLEDGE_WORKFLOW_CASE = 'success'
                $retryOutput = & $pwsh @arguments 2>&1 | Out-String
                $retryExit = $LASTEXITCODE
                $retry = $retryOutput | ConvertFrom-Json
                Assert-True ($retryExit -eq 0 -and $retry.ok) "Push retry failed: $retryOutput"
                Assert-True ($retry.verified_commit -eq $mainHead) 'Empty-squash retry invented another commit.'
                $retryRemote = (Invoke-Git $main @('ls-remote', 'origin', 'refs/heads/main') | Out-String).Trim().Split([char]9)[0]
                Assert-True ($retryRemote -eq $mainHead) 'Retry failed to publish the already verified local commit.'
                Assert-True (Test-Path -LiteralPath $summary.log_root) 'Retry discarded the first failure evidence.'
            }
        }
        Write-Output ("Workflow {0} passed ({1:N2}s)." -f $case, $watch.Elapsed.TotalSeconds)
    }
} finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolved = (Resolve-Path -LiteralPath $temporaryRoot).Path
        if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notlike 'FluxKnowledgeWorkflow-*') { throw 'Unsafe disposable cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
