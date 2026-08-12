[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [ValidateSet('All', 'GmailSwitch', 'PostRefreshMarker', 'RefreshMutationFence', 'Lifecycle')]
    [string]$Case = 'All'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeoutScript = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$refreshScript = Join-Path $SourceRoot 'scripts\dev\refresh-staged-squash.ps1'
$boundaryModule = Join-Path $SourceRoot 'scripts\dev\ResumeGitBoundary.psm1'
foreach ($requiredPath in @($closeoutScript, $refreshScript, $boundaryModule)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required resume lifecycle contract input is missing: $requiredPath"
    }
}
$closeoutText = Get-Content -LiteralPath $closeoutScript -Raw
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('FluxKnowledgeResumeLifecycle-' + [Guid]::NewGuid().ToString('N'))

function Get-ResumeChildCommand {
    param([Parameter(Mandatory)] [string]$Name)

    $match = [regex]::Match($closeoutText, ('(?s)\$' + [regex]::Escape($Name) + "\s*=\s+@'\r?\n(.*?)\r?\n'@"))
    if (-not $match.Success) {
        throw "Unable to extract the exact production resume child command: $Name"
    }
    return $match.Groups[1].Value
}

function Invoke-ExactResumeChild {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [hashtable]$Environment
    )

    $previous = @{}
    foreach ($name in $Environment.Keys) {
        $entry = Get-Item -Path ('Env:' + $name) -ErrorAction SilentlyContinue
        $previous[$name] = if ($null -eq $entry) { $null } else { [string]$entry.Value }
        Set-Item -Path ('Env:' + $name) -Value ([string]$Environment[$name])
    }
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        try {
            $output = & ([scriptblock]::Create($Command)) 2>&1 | Out-String
            return [pscustomobject]@{ Succeeded = $true; NativeExitCode = $LASTEXITCODE; Output = $output; Error = '' }
        }
        catch {
            return [pscustomobject]@{ Succeeded = $false; NativeExitCode = $LASTEXITCODE; Output = ''; Error = $_.Exception.Message }
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        foreach ($name in $previous.Keys) {
            if ($null -eq $previous[$name]) {
                Remove-Item -Path ('Env:' + $name) -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -Path ('Env:' + $name) -Value $previous[$name]
            }
        }
    }
}

function Invoke-ContractGit {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & git @Arguments 2>$null | Out-Null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Disposable Git fixture command failed: git $($Arguments -join ' ')"
    }
}

function Get-ContractGitValue {
    param([Parameter(Mandatory)] [string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $value = (& git @Arguments 2>&1 | Out-String).Trim()
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Disposable Git fixture query failed: git $($Arguments -join ' '): $value"
    }
    return $value
}

function Get-ContractFileSha256 {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function New-ResumeLifecycleFixture {
    param([Parameter(Mandatory)] [string]$Root)

    $main = Join-Path $Root 'main'
    $feature = Join-Path $Root 'feature'
    $origin = Join-Path $Root 'origin.git'
    New-Item -ItemType Directory -Path $main -Force | Out-Null
    Invoke-ContractGit @('init', '--initial-branch', 'main', $main)
    Set-Content -LiteralPath (Join-Path $main '.gitattributes') -NoNewline -Value "* -text`n"
    Set-Content -LiteralPath (Join-Path $main '.gitignore') -Value '.agents/'
    Set-Content -LiteralPath (Join-Path $main 'README.md') -Value 'resume lifecycle fixture base'
    foreach ($relativeRecord in @(
        'docs/operations/native-windows-phase-2-native-worker-supervision-validation.md',
        'docs/operations/native-windows-phase-4-outlook-ingress-validation.md',
        'docs/operations/native-windows-phase-5-retained-processors-validation.md')) {
        $recordPath = Join-Path $main $relativeRecord
        New-Item -ItemType Directory -Path (Split-Path -Parent $recordPath) -Force | Out-Null
        Set-Content -LiteralPath $recordPath -Value 'baseline validation record'
    }
    Invoke-ContractGit @('-C', $main, 'add', '.')
    Invoke-ContractGit @('-C', $main, '-c', 'user.name=Resume lifecycle contract', '-c', 'user.email=resume-lifecycle@example.invalid', 'commit', '-m', 'fixture base')
    $mainHead = Get-ContractGitValue @('-C', $main, 'rev-parse', 'HEAD')
    Invoke-ContractGit @('init', '--bare', $origin)
    Invoke-ContractGit @('-C', $main, 'remote', 'add', 'origin', $origin)
    Invoke-ContractGit @('-C', $main, 'push', '-u', 'origin', 'main')
    New-Item -ItemType Directory -Path (Join-Path $main '.agents') -Force | Out-Null
    Invoke-ContractGit @('-C', $main, 'worktree', 'add', '-b', 'codex/resume-lifecycle-contract', $feature)
    return [pscustomobject]@{ Main = $main; Feature = $feature; Origin = $origin; MainHead = $mainHead; Branch = 'codex/resume-lifecycle-contract' }
}

function New-ResumeChildEnvironment {
    param(
        [Parameter(Mandatory)] $Fixture,
        [Parameter(Mandatory)] [string]$FeatureHead,
        [Parameter(Mandatory)] [string]$StatePath
    )

    return @{
        FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE = $boundaryModule
        FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH = $StatePath
        FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $Fixture.Main
        FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $Fixture.Feature
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL = $Fixture.Origin
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD = $Fixture.MainHead
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD = $FeatureHead
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH = $Fixture.Branch
        FLUXKNOWLEDGE_CLOSEOUT_COMMIT_MESSAGE = 'test: resume lifecycle commit'
    }
}

function Get-ContractRemoteMainHead {
    param([Parameter(Mandatory)] $Fixture)

    $line = Get-ContractGitValue @('-C', $Fixture.Main, 'ls-remote', '--refs', $Fixture.Origin, 'refs/heads/main')
    if ($line -notmatch '^([0-9a-f]{40})\trefs/heads/main$') {
        throw "Disposable origin/main is missing or ambiguous: $line"
    }
    return $matches[1]
}

function Assert-ChildSucceeded {
    param([Parameter(Mandatory)] $Result, [Parameter(Mandatory)] [string]$Context)

    if (-not $Result.Succeeded -or $Result.NativeExitCode -ne 0) {
        throw "The exact production $Context child did not complete: $($Result.Error) $($Result.Output)"
    }
}

function Invoke-GmailSwitchContract {
    $command = Get-ResumeChildCommand -Name 'resumeFeatureGmailGuardCommand'
    $probeScript = Join-Path $temporaryRoot 'gmail-switch-probe.ps1'
    @'
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$BaselineRef,
    [switch]$ResumeBoundary,
    [string]$ExpectedOriginUrl,
    [string]$ExpectedHead,
    [string]$ExpectedBranch,
    [switch]$ConfirmApprovedLegacyLocalSurfaceChanges
)
if ($ConfirmApprovedLegacyLocalSurfaceChanges) { Write-Output 'switch=true' } else { Write-Output 'switch=false' }
'@ | Set-Content -LiteralPath $probeScript -Encoding utf8
    $marker = Join-Path $temporaryRoot 'gmail-branch-injection.marker'
    $maliciousBranch = "codex/x';Write-Output('INJECTED');#"
    foreach ($expected in @(
        [pscustomobject]@{ Confirmation = '0'; Output = 'switch=false' },
        [pscustomobject]@{ Confirmation = '1'; Output = 'switch=true' })) {
        $result = Invoke-ExactResumeChild -Command $command -Environment @{
            FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT = $probeScript
            FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_CONFIRM = $expected.Confirmation
            FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_MAIN_HEAD = ('a' * 40)
            FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL = 'https://github.com/yagasoft/Flux-LLM-KB.git'
            FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD = ('b' * 40)
            FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH = $maliciousBranch
        }
        if (-not $result.Succeeded -or $result.NativeExitCode -ne 0 -or $result.Output -notmatch [regex]::Escape($expected.Output)) {
            throw "The exact resume Gmail guard child did not bind confirmation $($expected.Confirmation) as the expected switch value: $($result.Error) $($result.Output)"
        }
        if ($result.Output -match 'INJECTED' -or (Test-Path -LiteralPath $marker)) {
            throw 'Expected feature branch text escaped its data-only resume Gmail child transport.'
        }
    }
}

function Invoke-PostRefreshMarkerContract {
    $fixture = New-ResumeLifecycleFixture -Root (Join-Path $temporaryRoot 'marker')
    Set-Content -LiteralPath (Join-Path $fixture.Feature 'reviewed.txt') -Value 'old reviewed feature'
    Invoke-ContractGit @('-C', $fixture.Feature, 'add', 'reviewed.txt')
    Invoke-ContractGit @('-C', $fixture.Feature, '-c', 'user.name=Resume lifecycle contract', '-c', 'user.email=resume-lifecycle@example.invalid', 'commit', '-m', 'old reviewed feature')
    $oldFeatureHead = Get-ContractGitValue @('-C', $fixture.Feature, 'rev-parse', 'HEAD')
    Set-Content -LiteralPath (Join-Path $fixture.Feature 'reviewed.txt') -Value 'new reviewed feature'
    Invoke-ContractGit @('-C', $fixture.Feature, 'add', 'reviewed.txt')
    Invoke-ContractGit @('-C', $fixture.Feature, '-c', 'user.name=Resume lifecycle contract', '-c', 'user.email=resume-lifecycle@example.invalid', 'commit', '-m', 'new reviewed feature')
    $newFeatureHead = Get-ContractGitValue @('-C', $fixture.Feature, 'rev-parse', 'HEAD')
    Invoke-ContractGit @('-C', $fixture.Main, 'read-tree', '--reset', '-u', $oldFeatureHead)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $refreshScript `
        -MainWorktree $fixture.Main `
        -FeatureWorktree $fixture.Feature `
        -ExpectedMainHead $fixture.MainHead `
        -ExpectedStagedFeatureHead $oldFeatureHead `
        -ExpectedFeatureHead $newFeatureHead `
        -ExpectedFeatureBranch $fixture.Branch `
        -ExpectedOriginUrl $fixture.Origin | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The disposable real staged-squash refresh did not complete before marker injection.' }
    $gitDirectory = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', '--absolute-git-dir')
    $markerPath = Join-Path $gitDirectory 'MERGE_HEAD'
    Set-Content -LiteralPath $markerPath -Value 'injected after refresh'
    $statePath = Join-Path $fixture.Main '.agents\resume-main-commit.json'
    $beforeHead = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', 'HEAD')
    $beforeTree = Get-ContractGitValue @('-C', $fixture.Main, 'write-tree')
    $commitResult = Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeBoundaryCommitCommand') -Environment (New-ResumeChildEnvironment -Fixture $fixture -FeatureHead $newFeatureHead -StatePath $statePath)
    $afterHead = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', 'HEAD')
    $afterTree = Get-ContractGitValue @('-C', $fixture.Main, 'write-tree')
    if ($commitResult.Succeeded -or $commitResult.NativeExitCode -ne 0 -or $beforeHead -cne $afterHead -or $beforeTree -cne $afterTree -or (Test-Path -LiteralPath $statePath)) {
        throw "The exact resume commit child accepted a post-refresh operation marker or changed head/tree/state before rejecting it: $($commitResult.Error) $($commitResult.Output)"
    }
}

function Invoke-RefreshMutationFenceContract {
    $fixture = New-ResumeLifecycleFixture -Root (Join-Path $temporaryRoot 'refresh-mutation-fence')
    Set-Content -LiteralPath (Join-Path $fixture.Feature 'reviewed.txt') -Value 'old reviewed feature'
    Invoke-ContractGit @('-C', $fixture.Feature, 'add', 'reviewed.txt')
    Invoke-ContractGit @('-C', $fixture.Feature, '-c', 'user.name=Resume lifecycle contract', '-c', 'user.email=resume-lifecycle@example.invalid', 'commit', '-m', 'old reviewed feature')
    $oldFeatureHead = Get-ContractGitValue @('-C', $fixture.Feature, 'rev-parse', 'HEAD')
    Set-Content -LiteralPath (Join-Path $fixture.Feature 'reviewed.txt') -Value 'new reviewed feature'
    Invoke-ContractGit @('-C', $fixture.Feature, 'add', 'reviewed.txt')
    Invoke-ContractGit @('-C', $fixture.Feature, '-c', 'user.name=Resume lifecycle contract', '-c', 'user.email=resume-lifecycle@example.invalid', 'commit', '-m', 'new reviewed feature')
    $newFeatureHead = Get-ContractGitValue @('-C', $fixture.Feature, 'rev-parse', 'HEAD')
    Invoke-ContractGit @('-C', $fixture.Main, 'read-tree', '--reset', '-u', $oldFeatureHead)
    $beforeTree = Get-ContractGitValue @('-C', $fixture.Main, 'write-tree')
    $indexReference = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', '--git-path', 'index')
    $indexPath = if ([System.IO.Path]::IsPathRooted($indexReference)) { $indexReference } else { Join-Path $fixture.Main $indexReference }
    $beforeIndexHash = Get-ContractFileSha256 -Path $indexPath
    $mainGitDirectory = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', '--absolute-git-dir')
    $markerPath = Join-Path $mainGitDirectory 'MERGE_HEAD'
    $processFenceLine = @(Select-String -LiteralPath $boundaryModule -Pattern '^\s*Assert-ResumeGitNoInProgressOperation \$OperationBoundary$' | Select-Object -First 1).LineNumber
    if ($null -eq $processFenceLine) { throw 'The resume Git boundary must check operation markers inside the process-launch path.' }
    $previousMarkerEnvironment = [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_REFRESH_FENCE_MARKER', 'Process')
    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_REFRESH_FENCE_MARKER', $markerPath, 'Process')
    $breakpoint = Set-PSBreakpoint -Script $boundaryModule -Line $processFenceLine -Action {
        Set-Content -LiteralPath $env:FLUXKNOWLEDGE_REFRESH_FENCE_MARKER -Value 'injected inside the authenticated process-launch path'
    }
    $refreshRejected = $false
    try {
        & $refreshScript `
            -MainWorktree $fixture.Main `
            -FeatureWorktree $fixture.Feature `
            -ExpectedMainHead $fixture.MainHead `
            -ExpectedStagedFeatureHead $oldFeatureHead `
            -ExpectedFeatureHead $newFeatureHead `
            -ExpectedFeatureBranch $fixture.Branch `
            -ExpectedOriginUrl $fixture.Origin | Out-Null
    }
    catch {
        $refreshRejected = $true
    }
    finally {
        Remove-PSBreakpoint -Breakpoint $breakpoint -ErrorAction SilentlyContinue
        [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_REFRESH_FENCE_MARKER', $previousMarkerEnvironment, 'Process')
    }
    $afterTree = Get-ContractGitValue @('-C', $fixture.Main, 'write-tree')
    $afterIndexHash = Get-ContractFileSha256 -Path $indexPath
    if (-not (Test-Path -LiteralPath $markerPath) -or -not $refreshRejected -or $beforeTree -cne $afterTree -or $beforeIndexHash -cne $afterIndexHash) {
        throw 'A marker injected after staged-squash preview must reject refresh before the authenticated read-tree mutation changes the index tree or index bytes.'
    }

    $refreshText = Get-Content -LiteralPath $refreshScript -Raw
    $preview = [regex]::Match($refreshText, '(?s)\$preview\s*=\s*Invoke-ResumeGit.*?if \(\$preview\.ExitCode -ne 0\).*?\r?\n')
    if (-not $preview.Success) {
        throw 'The refresh contract could not locate the authenticated read-tree preview.'
    }
    $mutation = @(Select-String -LiteralPath $refreshScript -Pattern '^\s*\$refresh\s*=\s*Invoke-ResumeGit.*''read-tree'', ''-m'', ''-u''.*-RequireNoInProgressOperation.*-PeerBoundary\s+\$featureBoundary' | Select-Object -First 1)
    if ($mutation.Count -ne 1) {
        throw 'The staged-squash refresh must bind the peer marker fence into the mutating authenticated Git invocation.'
    }

    $cleanup = Get-ResumeChildCommand -Name 'resumeCleanupCommand'
    if ($cleanup -match "worktree', 'prune" -or $cleanup -notmatch '(?s)worktree'', ''remove''.*?-RequireNoInProgressOperation\s+-PeerBoundary\s+\$featureBoundary.*?branch'', ''-D''.*?-RequireNoInProgressOperation') {
        throw 'Authenticated cleanup must bind the pair marker fence to worktree removal, omit redundant prune, then bind the main fence to branch deletion after the feature worktree no longer exists.'
    }

    $validationRecord = Get-ResumeChildCommand -Name 'resumeValidationRecordCommand'
    if ($validationRecord -notmatch '(?s)function Get-ResumeBoundaryValue.*?\[switch\]\$RequireNoInProgressOperation.*?\$PeerBoundary.*?Invoke-ResumeGit.*?-RequireNoInProgressOperation.*?-PeerBoundary' -or
        $validationRecord -notmatch '(?s)Get-ResumeBoundaryValue \$boundary @\(''write-tree''\) -RequireNoInProgressOperation -PeerBoundary \$featureBoundary') {
        throw 'Authenticated validation-record write-tree must bind the marker fence into its process-launch invocation.'
    }
}

function Invoke-ResumeLifecycleContract {
    $fixture = New-ResumeLifecycleFixture -Root (Join-Path $temporaryRoot 'lifecycle')
    Set-Content -LiteralPath (Join-Path $fixture.Feature 'reviewed.txt') -Value 'reviewed feature payload'
    Invoke-ContractGit @('-C', $fixture.Feature, 'add', 'reviewed.txt')
    Invoke-ContractGit @('-C', $fixture.Feature, '-c', 'user.name=Resume lifecycle contract', '-c', 'user.email=resume-lifecycle@example.invalid', 'commit', '-m', 'reviewed feature')
    $featureHead = Get-ContractGitValue @('-C', $fixture.Feature, 'rev-parse', 'HEAD')
    Invoke-ContractGit @('-C', $fixture.Main, 'read-tree', '--reset', '-u', $featureHead)
    $statePath = Join-Path $fixture.Main '.agents\resume-main-commit.json'
    $environment = New-ResumeChildEnvironment -Fixture $fixture -FeatureHead $featureHead -StatePath $statePath
    $commitResult = Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeBoundaryCommitCommand') -Environment $environment
    Assert-ChildSucceeded -Result $commitResult -Context 'resume commit-tree and CAS'
    $resumeCommit = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', 'HEAD')
    if ($resumeCommit -ceq $fixture.MainHead -or -not (Test-Path -LiteralPath $statePath)) { throw 'The exact resume commit child did not advance local main and write its state record.' }
    $commitState = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    if ($commitState.commit -cne $resumeCommit -or $commitState.expected_main -cne $fixture.MainHead) { throw 'The exact resume commit child wrote an invalid commit state record.' }
    Assert-ChildSucceeded -Result (Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeBoundaryPushCommand') -Environment $environment) -Context 'resume expected-old lease push'
    if ((Get-ContractRemoteMainHead -Fixture $fixture) -cne $resumeCommit) { throw 'The exact resume expected-old lease push did not advance disposable origin/main.' }
    $validationRecord = Join-Path $fixture.Main 'docs\operations\native-windows-phase-5-retained-processors-validation.md'
    Set-Content -LiteralPath $validationRecord -Value 'validated after disposable lifecycle'
    Assert-ChildSucceeded -Result (Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeValidationRecordCommand') -Environment $environment) -Context 'validation-record write'
    $validationCommit = Get-ContractGitValue @('-C', $fixture.Main, 'rev-parse', 'HEAD')
    if ($validationCommit -ceq $resumeCommit) { throw 'The exact validation-record child did not create its authenticated commit.' }
    Assert-ChildSucceeded -Result (Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeValidationRecordPushCommand') -Environment $environment) -Context 'validation-record expected-old lease push'
    if ((Get-ContractRemoteMainHead -Fixture $fixture) -cne $validationCommit) { throw 'The exact validation-record expected-old lease push did not advance disposable origin/main.' }
    Assert-ChildSucceeded -Result (Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeValidationRecordCommand') -Environment $environment) -Context 'validation-record no-op write check'
    $noOpState = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
    if (-not $noOpState.no_op -or $noOpState.commit -cne $validationCommit) { throw 'The exact validation-record child did not record its no-op remote recheck state.' }
    Assert-ChildSucceeded -Result (Invoke-ExactResumeChild -Command (Get-ResumeChildCommand -Name 'resumeValidationRecordPushCommand') -Environment $environment) -Context 'validation-record no-op remote recheck'
    if ((Get-ContractRemoteMainHead -Fixture $fixture) -cne $validationCommit) { throw 'The exact validation-record no-op remote recheck changed disposable origin/main.' }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    if ($Case -in @('All', 'GmailSwitch')) { Invoke-GmailSwitchContract }
    if ($Case -in @('All', 'PostRefreshMarker')) { Invoke-PostRefreshMarkerContract }
    if ($Case -in @('All', 'RefreshMutationFence')) { Invoke-RefreshMutationFenceContract }
    if ($Case -in @('All', 'Lifecycle')) { Invoke-ResumeLifecycleContract }
    Write-Output 'Resume lifecycle contract passed.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
