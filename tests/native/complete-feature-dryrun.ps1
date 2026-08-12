[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeoutScript = Join-Path $SourceRoot "scripts\dev\complete-feature.ps1"
if (-not (Test-Path -LiteralPath $closeoutScript)) {
    throw "The native closeout script is missing."
}
$refreshScript = Join-Path $SourceRoot "scripts\dev\refresh-staged-squash.ps1"
$resumeGitBoundaryModule = Join-Path $SourceRoot "scripts\dev\ResumeGitBoundary.psm1"
$resumeLifecycleContract = Join-Path $SourceRoot "tests\native\resume-lifecycle-contract.ps1"
if (-not (Test-Path -LiteralPath $resumeGitBoundaryModule -PathType Leaf)) {
    throw "The authenticated resume Git boundary module is missing."
}
if (-not (Test-Path -LiteralPath $resumeLifecycleContract -PathType Leaf)) {
    throw "The executable resume lifecycle contract is missing."
}
Import-Module $resumeGitBoundaryModule -Force
$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-windows.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript)) {
    throw "The native deployment script is missing."
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FluxKnowledgeCloseout-$([Guid]::NewGuid().ToString('N'))"
$mainRoot = Join-Path $temporaryRoot "main"
$featureRoot = Join-Path $temporaryRoot "feature"
$remoteRoot = Join-Path $temporaryRoot "origin.git"
$submoduleSourceRoot = Join-Path $temporaryRoot "submodule-source"

function Get-ResumeFileIdentity {
    param([Parameter(Mandatory)] [string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Get-ResumeBoundarySnapshot {
    param([Parameter(Mandatory)] [string]$Worktree)

    $ambientGitEnvironment = @{}
    foreach ($entry in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^GIT_' })) {
        $ambientGitEnvironment[$entry.Name] = [string]$entry.Value
        Remove-Item -Path ("Env:" + $entry.Name) -ErrorAction Stop
    }
    try {
        $commonDirectory = (& git -C $Worktree rev-parse --git-common-dir).Trim()
        if (-not [System.IO.Path]::IsPathRooted($commonDirectory)) {
            $commonDirectory = Join-Path $Worktree $commonDirectory
        }
        $objectDirectory = Join-Path $commonDirectory "objects"
        $objectEntries = @(
            Get-ChildItem -LiteralPath $objectDirectory -Recurse -File -ErrorAction Stop |
                Sort-Object FullName |
                ForEach-Object { "{0}|{1}|{2}" -f $_.FullName, $_.Length, (Get-ResumeFileIdentity -Path $_.FullName) })
        return [pscustomobject]@{
            Head = (& git -C $Worktree rev-parse HEAD).Trim()
            Tree = (& git -C $Worktree write-tree).Trim()
            Refs = @(& git -C $Worktree for-each-ref --format="%(refname)|%(objectname)|%(*objectname)" | Sort-Object)
            ObjectEntries = $objectEntries
            WorktreeEntries = @(Get-ChildItem -LiteralPath $Worktree -Recurse -File -Force |
                Where-Object { $_.FullName -notmatch '\\.git(\\|$)' } |
                Sort-Object FullName |
                ForEach-Object { "{0}|{1}|{2}" -f $_.FullName, $_.Length, (Get-ResumeFileIdentity -Path $_.FullName) })
        }
    }
    finally {
        foreach ($name in $ambientGitEnvironment.Keys) {
            Set-Item -Path ("Env:" + $name) -Value $ambientGitEnvironment[$name]
        }
    }
}

function Assert-ResumeBoundarySnapshotUnchanged {
    param(
        [Parameter(Mandatory)] $Before,
        [Parameter(Mandatory)] $After,
        [Parameter(Mandatory)] [string]$Context
    )

    if ($Before.Head -cne $After.Head -or
        $Before.Tree -cne $After.Tree -or
        (@($Before.Refs) -join "`n") -cne (@($After.Refs) -join "`n") -or
        (@($Before.ObjectEntries) -join "`n") -cne (@($After.ObjectEntries) -join "`n") -or
        (@($Before.WorktreeEntries) -join "`n") -cne (@($After.WorktreeEntries) -join "`n")) {
        throw "The authenticated resume Git boundary changed protected repository state while $Context."
    }
}

function Assert-ResumeBoundaryRejects {
    param(
        [Parameter(Mandatory)] [string]$Worktree,
        [Parameter(Mandatory)] [string]$ExpectedOriginUrl,
        [Parameter(Mandatory)] [string]$ExpectedHead,
        [Parameter(Mandatory)] [string]$ExpectedBranch,
        [Parameter(Mandatory)] [string]$Context,
        [string]$MarkerPath = "",
        $Before = $null
    )

    if ($null -eq $Before) {
        $Before = Get-ResumeBoundarySnapshot -Worktree $Worktree
    }
    $rejected = $false
    try {
        $acceptedBoundary = New-ResumeGitBoundary -Worktree $Worktree -ExpectedOriginUrl $ExpectedOriginUrl -ExpectedHead $ExpectedHead -ExpectedBranch $ExpectedBranch
        Remove-ResumeGitBoundary -Boundary $acceptedBoundary
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "The authenticated resume Git boundary accepted $Context."
    }
    if ($MarkerPath -and (Test-Path -LiteralPath $MarkerPath)) {
        throw "The authenticated resume Git boundary executed a malicious helper while $Context."
    }
    return $Before
}

function Invoke-ResumeGitBoundaryContract {
    param([Parameter(Mandatory)] [string]$Root)

    $boundaryMain = Join-Path $Root "boundary-main"
    $boundaryFeature = Join-Path $Root "boundary-feature"
    $boundaryOrigin = Join-Path $Root "boundary-origin.git"
    $hostileRoot = Join-Path $Root "boundary-hostile"
    $markerPath = Join-Path $Root "boundary-malicious-helper.marker"
    New-Item -ItemType Directory -Path $boundaryMain, $hostileRoot | Out-Null
    & git init --initial-branch main $boundaryMain | Out-Null
    Set-Content -LiteralPath (Join-Path $boundaryMain "boundary.txt") -Value "boundary base"
    & git -C $boundaryMain add boundary.txt
    & git -C $boundaryMain -c user.name="Boundary contract" -c user.email="boundary@example.invalid" commit -m "boundary base" | Out-Null
    & git init --bare $boundaryOrigin | Out-Null
    & git -C $boundaryMain remote add origin $boundaryOrigin
    & git -C $boundaryMain push -u origin main | Out-Null
    & git -C $boundaryMain worktree add -b "codex/resume-boundary-contract" $boundaryFeature | Out-Null
    & git -C $boundaryMain config --local branch.main.vscode-merge-base "origin/main"
    & git -C $boundaryMain config --local branch.codex/resume-boundary-contract.remote "origin"
    & git -C $boundaryMain config --local branch.codex/resume-boundary-contract.merge "refs/heads/codex/resume-boundary-contract"
    & git -C $boundaryMain config --local branch.codex/resume-boundary-contract.vscode-merge-base "origin/main"
    & git -C $boundaryFeature -c user.name="Boundary contract" -c user.email="boundary@example.invalid" commit --allow-empty -m "feature branch identity" | Out-Null
    & git init --initial-branch hostile $hostileRoot | Out-Null
    & git -C $hostileRoot -c user.name="Hostile fixture" -c user.email="hostile@example.invalid" commit --allow-empty -m "hostile base" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create the authenticated resume Git boundary fixture."
    }

    $mainHead = (& git -C $boundaryMain rev-parse HEAD).Trim()
    $featureHead = (& git -C $boundaryFeature rev-parse HEAD).Trim()
    $hostileGitDirectory = Join-Path $hostileRoot ".git"
    $hostileConfig = Join-Path $hostileRoot "hostile.gitconfig"
    Set-Content -LiteralPath $hostileConfig -Value "[alias]`nresume = !echo hostile"
    $identityBoundary = New-ResumeGitBoundary -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main"
    try {
        $mainCommitHeaders = (Invoke-ResumeGit -Boundary $identityBoundary -Arguments @('cat-file', '-p', $mainHead)).StdOut
        $authorMatch = [regex]::Match($mainCommitHeaders, '(?m)^author (.+) <([^<>\r\n]+)> (\d+ [+-]\d{4})$')
        $committerMatch = [regex]::Match($mainCommitHeaders, '(?m)^committer (.+) <([^<>\r\n]+)> (\d+ [+-]\d{4})$')
        if (-not $authorMatch.Success -or -not $committerMatch.Success) { throw 'Unable to parse immutable expected-main author and committer headers.' }
        $identity = @{
            GIT_AUTHOR_NAME = $authorMatch.Groups[1].Value; GIT_AUTHOR_EMAIL = $authorMatch.Groups[2].Value; GIT_AUTHOR_DATE = '@' + $authorMatch.Groups[3].Value
            GIT_COMMITTER_NAME = $committerMatch.Groups[1].Value; GIT_COMMITTER_EMAIL = $committerMatch.Groups[2].Value; GIT_COMMITTER_DATE = '@' + $committerMatch.Groups[3].Value
        }
        $tree = (Invoke-ResumeGit -Boundary $identityBoundary -Arguments @('rev-parse', ($mainHead + '^{tree}'))).StdOut.Trim()
        $created = Invoke-ResumeGit -Boundary $identityBoundary -Arguments @('commit-tree', $tree, '-p', $mainHead) -StandardInput "boundary identity contract`n" -Identity $identity
        if ($created.ExitCode -ne 0 -or $created.StdOut.Trim() -notmatch '^[0-9a-f]{40}$') { throw 'The authenticated resume Git boundary could not create an explicitly identified commit object.' }
        $createdHeaders = (Invoke-ResumeGit -Boundary $identityBoundary -Arguments @('cat-file', '-p', $created.StdOut.Trim())).StdOut
        if ($createdHeaders -notmatch ('(?m)^author ' + [regex]::Escape($authorMatch.Groups[1].Value + ' <' + $authorMatch.Groups[2].Value + '> ' + $authorMatch.Groups[3].Value) + '$') -or
            $createdHeaders -notmatch ('(?m)^committer ' + [regex]::Escape($committerMatch.Groups[1].Value + ' <' + $committerMatch.Groups[2].Value + '> ' + $committerMatch.Groups[3].Value) + '$')) {
            throw 'The authenticated resume Git boundary did not preserve immutable expected-main identity headers for commit-tree.'
        }
    }
    finally { Remove-ResumeGitBoundary -Boundary $identityBoundary }
    $environmentCases = [ordered]@{
        GIT_DIR = $hostileGitDirectory
        GIT_WORK_TREE = $hostileRoot
        GIT_INDEX_FILE = (Join-Path $hostileGitDirectory "hostile.index")
        GIT_COMMON_DIR = $hostileGitDirectory
        GIT_OBJECT_DIRECTORY = (Join-Path $hostileGitDirectory "objects")
        GIT_ALTERNATE_OBJECT_DIRECTORIES = (Join-Path $hostileGitDirectory "objects")
        GIT_NAMESPACE = "hostile"
        GIT_SHALLOW_FILE = (Join-Path $hostileGitDirectory "shallow")
        GIT_NO_REPLACE_OBJECTS = "0"
        GIT_REPLACE_REF_BASE = "refs/hostile-replace/"
        GIT_CONFIG_GLOBAL = $hostileConfig
        GIT_CONFIG_SYSTEM = $hostileConfig
        GIT_CONFIG_NOSYSTEM = "0"
        GIT_CONFIG_COUNT = "1"
        GIT_CONFIG_KEY_0 = "alias.resume"
        GIT_CONFIG_VALUE_0 = "!powershell -NoProfile -Command Set-Content -LiteralPath '$markerPath' -Value executed"
        GIT_CONFIG_PARAMETERS = "'alias.resume=!echo hostile'"
        GIT_EXTERNAL_DIFF = (Join-Path $hostileRoot "malicious-diff.cmd")
        GIT_DIFF_OPTS = "--output=$markerPath"
    }
    $originalEnvironment = @{}
    foreach ($name in $environmentCases.Keys) {
        $existing = Get-Item -Path ("Env:" + $name) -ErrorAction SilentlyContinue
        $originalEnvironment[$name] = if ($null -eq $existing) { $null } else { [string]$existing.Value }
    }
    try {
        foreach ($name in $environmentCases.Keys) {
            Set-Item -Path ("Env:" + $name) -Value $environmentCases[$name]
            $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
            $boundary = New-ResumeGitBoundary -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main"
            try {
                $result = Invoke-ResumeGit -Boundary $boundary -Arguments @("rev-parse", "HEAD")
                if ($result.ExitCode -ne 0 -or $result.StdOut.Trim() -cne $mainHead) {
                    throw "The authenticated resume Git boundary accepted redirected $name instead of the authenticated repository."
                }
            }
            finally {
                Remove-ResumeGitBoundary -Boundary $boundary
            }
            Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "ignoring inherited $name"
        }

        foreach ($name in $environmentCases.Keys) {
            Set-Item -Path ("Env:" + $name) -Value $environmentCases[$name]
        }
        $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
        $boundary = New-ResumeGitBoundary -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main"
        try {
            $expectedIndexPath = (Resolve-Path -LiteralPath (Join-Path $boundaryMain '.git\index')).Path
            if (-not $boundary.IndexPath.Equals($expectedIndexPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "The authenticated resume Git boundary did not pin the main worktree's real index path."
            }
            $cached = Invoke-ResumeGit -Boundary $boundary -Arguments @("ls-files", "--cached")
            if ($cached.ExitCode -ne 0 -or $cached.StdOut.Trim() -cne "boundary.txt") {
                throw "The authenticated resume Git boundary did not use the authenticated main worktree index."
            }
            $result = Invoke-ResumeGit -Boundary $boundary -Arguments @("rev-parse", "HEAD")
            if ($result.ExitCode -ne 0 -or $result.StdOut.Trim() -cne $mainHead) {
                throw "The authenticated resume Git boundary accepted combined inherited Git redirection."
            }
        }
        finally {
            Remove-ResumeGitBoundary -Boundary $boundary
        }
        Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "ignoring combined inherited Git redirection"
    }
    finally {
        foreach ($name in $originalEnvironment.Keys) {
            if ($null -eq $originalEnvironment[$name]) {
                Remove-Item -Path ("Env:" + $name) -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -Path ("Env:" + $name) -Value $originalEnvironment[$name]
            }
        }
    }

    $defaultHook = Join-Path $boundaryMain ".git\\hooks\\reference-transaction"
    $defaultHookMarkerPath = Join-Path $Root "boundary-default-hook.marker"
    $defaultHookMarkerForShell = $defaultHookMarkerPath.Replace('\\', '/')
    Set-Content -LiteralPath $defaultHook -NoNewline -Value ("#!/bin/sh`ntouch '" + $defaultHookMarkerForShell + "'`n")
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    $boundary = New-ResumeGitBoundary -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main"
    try {
        $hookRef = "refs/tests/resume-boundary-hook"
        $createHookRef = Invoke-ResumeGit -Boundary $boundary -Arguments @("update-ref", $hookRef, $mainHead)
        $deleteHookRef = Invoke-ResumeGit -Boundary $boundary -Arguments @("update-ref", "-d", $hookRef, $mainHead)
        if ($createHookRef.ExitCode -ne 0 -or $deleteHookRef.ExitCode -ne 0 -or (Test-Path -LiteralPath $defaultHookMarkerPath)) {
            throw "The authenticated resume Git boundary allowed a default reference-transaction hook during a mutation-capable command."
        }
    }
    finally {
        Remove-ResumeGitBoundary -Boundary $boundary
    }
    Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "blocking default Git hooks"
    Remove-Item -LiteralPath $defaultHook -Force

    $configuredHookDirectory = Join-Path $hostileRoot "configured-hooks"
    $configuredHookMarkerPath = Join-Path $Root "boundary-configured-hook.marker"
    New-Item -ItemType Directory -Path $configuredHookDirectory | Out-Null
    Set-Content -LiteralPath (Join-Path $configuredHookDirectory "reference-transaction") -NoNewline -Value ("#!/bin/sh`ntouch '" + $configuredHookMarkerPath.Replace('\\', '/') + "'`n")
    Set-Item -Path Env:GIT_CONFIG_COUNT -Value "1"
    Set-Item -Path Env:GIT_CONFIG_KEY_0 -Value "core.hooksPath"
    Set-Item -Path Env:GIT_CONFIG_VALUE_0 -Value $configuredHookDirectory
    try {
        $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
        $boundary = New-ResumeGitBoundary -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main"
        try {
            $hookRef = "refs/tests/resume-boundary-configured-hook"
            $createHookRef = Invoke-ResumeGit -Boundary $boundary -Arguments @("update-ref", $hookRef, $mainHead)
            $deleteHookRef = Invoke-ResumeGit -Boundary $boundary -Arguments @("update-ref", "-d", $hookRef, $mainHead)
            if ($createHookRef.ExitCode -ne 0 -or $deleteHookRef.ExitCode -ne 0 -or (Test-Path -LiteralPath $configuredHookMarkerPath)) {
                throw "The authenticated resume Git boundary allowed an inherited core.hooksPath hook during a mutation-capable command."
            }
        }
        finally {
            Remove-ResumeGitBoundary -Boundary $boundary
        }
        Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "blocking inherited core.hooksPath"
    }
    finally {
        foreach ($name in @("GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0")) {
            if ($null -eq $originalEnvironment[$name]) {
                Remove-Item -Path ("Env:" + $name) -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -Path ("Env:" + $name) -Value $originalEnvironment[$name]
            }
        }
    }

    $alternateRoot = Join-Path $Root "boundary-alternate-source"
    New-Item -ItemType Directory -Path $alternateRoot | Out-Null
    & git init --initial-branch alternate $alternateRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $alternateRoot "alternate.txt") -Value "alternate only object"
    & git -C $alternateRoot add alternate.txt
    & git -C $alternateRoot -c user.name="Boundary contract" -c user.email="boundary@example.invalid" commit -m "alternate object" | Out-Null
    $alternateHead = (& git -C $alternateRoot rev-parse HEAD).Trim()
    $commonDirectory = (& git -C $boundaryMain rev-parse --git-common-dir).Trim()
    if (-not [System.IO.Path]::IsPathRooted($commonDirectory)) { $commonDirectory = Join-Path $boundaryMain $commonDirectory }
    $alternatesPath = Join-Path $commonDirectory "objects\\info\\alternates"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $alternatesPath) | Out-Null
    Set-Content -LiteralPath $alternatesPath -NoNewline -Value ((Join-Path $alternateRoot ".git\\objects").Replace('\\', '/'))
    & git -C $boundaryMain update-ref refs/heads/main $alternateHead $mainHead
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $alternateHead -ExpectedBranch "main" -Context "persistent alternate object directory" -Before $before
    Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "rejecting persistent alternate object directory"
    & git -C $boundaryMain update-ref refs/heads/main $mainHead $alternateHead
    Remove-Item -LiteralPath $alternatesPath -Force

    $snapshotRef = "refs/notes/resume-boundary-snapshot"
    & git -C $boundaryMain update-ref $snapshotRef $mainHead
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    & git -C $boundaryMain update-ref $snapshotRef $featureHead $mainHead
    $refMutationDetected = $false
    try {
        Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "detecting a same-length non-HEAD ref replacement"
    }
    catch {
        $refMutationDetected = $true
    }
    if (-not $refMutationDetected) {
        throw "The authenticated resume Git boundary snapshot did not detect a same-length non-HEAD ref replacement."
    }
    & git -C $boundaryMain update-ref -d $snapshotRef $featureHead

    foreach ($forbiddenBranchConfig in @(
            @{ Key = "branch.main.rebase"; Value = "true" },
            @{ Key = "branch.main.remotee"; Value = "origin" },
            @{ Key = "branch.main.vscode-merge-basex"; Value = "origin/main" },
            @{ Key = "branch.hostile.vscode-merge-base"; Value = "origin/main" })) {
        $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
        & git -C $boundaryMain config --local $forbiddenBranchConfig['Key'] $forbiddenBranchConfig['Value']
        $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main" -Context ("malicious branch config " + $forbiddenBranchConfig['Key']) -MarkerPath $markerPath -Before $before
        & git -C $boundaryMain config --local --unset-all $forbiddenBranchConfig['Key']
        Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context ("rejecting malicious branch config " + $forbiddenBranchConfig['Key'])
    }

    $featureBoundary = New-ResumeGitBoundary -Worktree $boundaryFeature -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $featureHead -ExpectedBranch "codex/resume-boundary-contract"
    Remove-ResumeGitBoundary -Boundary $featureBoundary

    foreach ($forbiddenConfig in @(
            @{ Key = "alias.resume"; Value = "!powershell -NoProfile -Command Set-Content -LiteralPath '$markerPath' -Value executed" },
            @{ Key = "diff.external"; Value = (Join-Path $hostileRoot "malicious-diff.cmd") },
            @{ Key = "diff.resume.textconv"; Value = (Join-Path $hostileRoot "malicious-textconv.cmd") },
            @{ Key = "status.showUntrackedFiles"; Value = "no" },
            @{ Key = "submodule.recurse"; Value = "true" },
            @{ Key = "filter.hostile.clean"; Value = (Join-Path $hostileRoot "malicious-filter.cmd") },
            @{ Key = "core.hooksPath"; Value = $hostileRoot },
            @{ Key = "core.fsmonitor"; Value = "false" },
            @{ Key = "commit.gpgSign"; Value = "true" },
            @{ Key = "credential.helper"; Value = (Join-Path $hostileRoot "malicious-credential.cmd") },
            @{ Key = "remote.origin.uploadpack"; Value = (Join-Path $hostileRoot "malicious-upload-pack.cmd") },
            @{ Key = "maintenance.auto"; Value = "1" },
            @{ Key = "url.https://hostile.invalid/.insteadOf"; Value = $boundaryOrigin })) {
        $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
        & git -C $boundaryMain config --local $forbiddenConfig['Key'] $forbiddenConfig['Value']
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to create the forbidden local-config fixture for $($forbiddenConfig['Key'])."
        }
        $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main" -Context ("forbidden local config " + $forbiddenConfig['Key']) -MarkerPath $markerPath -Before $before
        & git -C $boundaryMain config --local --unset-all $forbiddenConfig['Key']
        Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context ("rejecting forbidden local config " + $forbiddenConfig['Key'])
    }
    $fsmonitorMarkerCommand = "powershell.exe -NoProfile -NonInteractive -Command `"New-Item -ItemType File -Force -Path '$markerPath' | Out-Null; exit 0`""
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    Add-Content -LiteralPath (Join-Path $boundaryMain ".git\config") -Value ("`n[core]`nfsmonitor = " + $fsmonitorMarkerCommand)
    $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main" -Context "marker fsmonitor configuration" -MarkerPath $markerPath -Before $before
    $configurationLines = @(Get-Content -LiteralPath (Join-Path $boundaryMain ".git\config"))
    Set-Content -LiteralPath (Join-Path $boundaryMain ".git\config") -Value $configurationLines[0..($configurationLines.Count - 3)]
    Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "rejecting marker fsmonitor configuration"
    $includePath = Join-Path $hostileRoot "included.gitconfig"
    Set-Content -LiteralPath $includePath -Value "[credential]`nhelper = hostile"
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    & git -C $boundaryMain config --local include.path $includePath
    $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main" -Context "local configuration include" -MarkerPath $markerPath -Before $before
    & git -C $boundaryMain config --local --unset-all include.path
    Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "rejecting local configuration include"

    $replacementRoot = Join-Path $Root "boundary-replacement"
    & git -C $boundaryMain worktree add -b "codex/resume-boundary-replacement" $replacementRoot | Out-Null
    & git -C $replacementRoot -c user.name="Boundary contract" -c user.email="boundary@example.invalid" commit --allow-empty -m "replacement" | Out-Null
    $replacementHead = (& git -C $replacementRoot rev-parse HEAD).Trim()
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    & git -C $boundaryMain replace $mainHead $replacementHead
    $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main" -Context "replacement refs" -MarkerPath $markerPath -Before $before
    & git -C $boundaryMain replace -d $mainHead | Out-Null
    Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "rejecting replacement refs"
    $commonDirectory = (& git -C $boundaryMain rev-parse --git-common-dir).Trim()
    if (-not [System.IO.Path]::IsPathRooted($commonDirectory)) { $commonDirectory = Join-Path $boundaryMain $commonDirectory }
    $graftsPath = Join-Path $commonDirectory "info\grafts"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $graftsPath) | Out-Null
    $before = Get-ResumeBoundarySnapshot -Worktree $boundaryMain
    Set-Content -LiteralPath $graftsPath -Value "$mainHead $replacementHead"
    $before = Assert-ResumeBoundaryRejects -Worktree $boundaryMain -ExpectedOriginUrl $boundaryOrigin -ExpectedHead $mainHead -ExpectedBranch "main" -Context "graft metadata" -MarkerPath $markerPath -Before $before
    Remove-Item -LiteralPath $graftsPath -Force
    Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $boundaryMain) -Context "rejecting graft metadata"

    $boundaryModuleText = Get-Content -LiteralPath $resumeGitBoundaryModule -Raw
    if ($boundaryModuleText -match '(?im)&\s*git(?:\.exe)?\b' -or
        $boundaryModuleText -match '(?im)\bgit(?:\.exe)?\s+-C\b' -or
        $boundaryModuleText -notmatch '\[System\.Diagnostics\.ProcessStartInfo\]' -or
        $boundaryModuleText -notmatch 'ConvertTo-ResumeGitArgumentString' -or
        $boundaryModuleText -notmatch 'RedirectStandardInput\s*=\s*\$true') {
        throw "The future resume Git boundary still permits raw or unsafe Git command construction."
    }
}

$sourceBoundaryBranch = (& git -C $SourceRoot branch --show-current).Trim()
$sourceBoundaryHead = (& git -C $SourceRoot rev-parse HEAD).Trim()
$sourceBoundaryOrigin = (& git -C $SourceRoot config --local --get remote.origin.url).Trim()
if ([string]::IsNullOrWhiteSpace($sourceBoundaryBranch) -or
    [string]::IsNullOrWhiteSpace($sourceBoundaryOrigin)) {
    throw "The native boundary contract requires the current feature worktree's normal branch tracking configuration."
}
$sourceBoundary = New-ResumeGitBoundary -Worktree $SourceRoot -ExpectedOriginUrl $sourceBoundaryOrigin -ExpectedHead $sourceBoundaryHead -ExpectedBranch $sourceBoundaryBranch
Remove-ResumeGitBoundary -Boundary $sourceBoundary

function Invoke-ResumeCleanupLateHeadContract {
    param([Parameter(Mandatory)] [string]$Root)

    $cleanupMain = Join-Path $Root 'cleanup-main'
    $cleanupFeature = Join-Path $Root 'cleanup-feature'
    $cleanupOrigin = Join-Path $Root 'cleanup-origin.git'
    New-Item -ItemType Directory -Path $cleanupMain | Out-Null
    & git init --initial-branch main $cleanupMain | Out-Null
    Set-Content -LiteralPath (Join-Path $cleanupMain '.gitattributes') -NoNewline -Value "* -text`n"
    Set-Content -LiteralPath (Join-Path $cleanupMain 'cleanup.txt') -Value 'base'
    & git -C $cleanupMain add .
    & git -C $cleanupMain -c user.name='Cleanup contract' -c user.email='cleanup@example.invalid' commit -m 'cleanup base' | Out-Null
    & git init --bare $cleanupOrigin | Out-Null
    & git -C $cleanupMain remote add origin $cleanupOrigin
    & git -C $cleanupMain push -u origin main | Out-Null
    & git -C $cleanupMain worktree add -b codex/cleanup-contract $cleanupFeature | Out-Null
    & git -C $cleanupFeature -c user.name='Cleanup contract' -c user.email='cleanup@example.invalid' commit --allow-empty -m 'expected cleanup feature' | Out-Null
    $expectedFeatureHead = (& git -C $cleanupFeature rev-parse HEAD).Trim()
    & git -C $cleanupFeature -c user.name='Cleanup contract' -c user.email='cleanup@example.invalid' commit --allow-empty -m 'late clean feature advancement' | Out-Null
    $mainHead = (& git -C $cleanupMain rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create the late clean cleanup fixture.' }

    $cleanupMatch = [regex]::Match((Get-Content -LiteralPath $closeoutScript -Raw), '(?s)\$resumeCleanupCommand\s*=\s+@''\r?\n(.*?)\r?\n''@')
    if (-not $cleanupMatch.Success) { throw 'Unable to extract the authenticated resume cleanup child for its disposable contract.' }
    $statePath = Join-Path $Root 'cleanup-state.json'
    @{ commit = $mainHead; expected_main = $mainHead } | ConvertTo-Json -Compress | Set-Content -LiteralPath $statePath -Encoding utf8
    $environment = @{
        FLUXKNOWLEDGE_CLOSEOUT_RESUME_BOUNDARY_MODULE = $resumeGitBoundaryModule
        FLUXKNOWLEDGE_CLOSEOUT_RESUME_COMMIT_STATE_PATH = $statePath
        FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $cleanupMain
        FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $cleanupFeature
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_ORIGIN_URL = $cleanupOrigin
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_HEAD = $expectedFeatureHead
        FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH = 'codex/cleanup-contract'
        FLUXKNOWLEDGE_CLOSEOUT_BRANCH = 'codex/cleanup-contract'
    }
    $previous = @{}
    foreach ($name in $environment.Keys) {
        $entry = Get-Item -Path ('Env:' + $name) -ErrorAction SilentlyContinue
        $previous[$name] = if ($null -eq $entry) { $null } else { [string]$entry.Value }
        Set-Item -Path ('Env:' + $name) -Value $environment[$name]
    }
    $before = Get-ResumeBoundarySnapshot -Worktree $cleanupMain
    try {
        $rejected = $false
        try { & ([scriptblock]::Create($cleanupMatch.Groups[1].Value)) } catch { $rejected = $true }
        if (-not $rejected -or -not (Test-Path -LiteralPath $cleanupFeature -PathType Container)) {
            throw 'Authenticated cleanup accepted a late clean feature head or removed its worktree.'
        }
        Assert-ResumeBoundarySnapshotUnchanged -Before $before -After (Get-ResumeBoundarySnapshot -Worktree $cleanupMain) -Context 'late clean feature cleanup rejection'
    } finally {
        foreach ($name in $previous.Keys) {
            if ($null -eq $previous[$name]) { Remove-Item -Path ('Env:' + $name) -ErrorAction SilentlyContinue } else { Set-Item -Path ('Env:' + $name) -Value $previous[$name] }
        }
    }
}

try {
    New-Item -ItemType Directory -Path $mainRoot | Out-Null
    & git init --initial-branch main $mainRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create temporary main worktree."
    }
    # Make the disposable fixture byte-stable when the host has a global
    # core.autocrlf setting; the boundary intentionally ignores global config.
    Set-Content -LiteralPath (Join-Path $mainRoot ".gitattributes") -NoNewline -Value "* -text`n"
    Set-Content -LiteralPath (Join-Path $mainRoot ".gitignore") -Value ".agents/"
    Set-Content -LiteralPath (Join-Path $mainRoot "README.md") -Value "temporary closeout contract repository"
    Set-Content -LiteralPath (Join-Path $mainRoot "delete-me.txt") -Value "delete from reviewed feature tree"
    & git init --initial-branch main $submoduleSourceRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create the submodule fixture repository."
    }
    Set-Content -LiteralPath (Join-Path $submoduleSourceRoot ".gitattributes") -NoNewline -Value "* -text`n"
    Set-Content -LiteralPath (Join-Path $submoduleSourceRoot "tracked.txt") -Value "clean submodule content"
    & git -C $submoduleSourceRoot add .gitattributes tracked.txt
    & git -C $submoduleSourceRoot -c user.name="Native Closeout Test" -c user.email="native-closeout@example.invalid" commit -m "initial submodule" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to commit the submodule fixture."
    }
    & git -C $mainRoot -c protocol.file.allow=always submodule add $submoduleSourceRoot retained-submodule | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to add the submodule fixture."
    }
    New-Item -ItemType Directory -Path (Join-Path $mainRoot "tests") | Out-Null
    Set-Content -LiteralPath (Join-Path $mainRoot "tests\test_mail_oauth.py") -Value "# preserved Gmail regression fixture"
    $gmailSchedulingPaths = @(
        "src\flux_llm_kb\service.py",
        "src\flux_llm_kb\event_scheduler.py",
        "src\flux_llm_kb\event_worker.py",
        "src\flux_llm_kb\messaging.py",
        "src\flux_llm_kb\sql\0009_imap_scheduler_state_machine.sql",
        "tests\test_background_jobs.py",
        "tests\test_worker.py"
    )
    foreach ($relativePath in $gmailSchedulingPaths) {
        $fullPath = Join-Path $mainRoot $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fullPath) | Out-Null
        Set-Content -LiteralPath $fullPath -Value "# preserved Gmail scheduling fixture"
    }
    $mainRefreshScript = Join-Path $mainRoot "scripts\dev\refresh-staged-squash.ps1"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $mainRefreshScript) | Out-Null
    Copy-Item -LiteralPath $refreshScript -Destination $mainRefreshScript
    & git -C $mainRoot add .
    & git -C $mainRoot -c user.name="Native Closeout Test" -c user.email="native-closeout@example.invalid" commit -m "initial" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to commit temporary main worktree."
    }
    & git init --bare $remoteRoot | Out-Null
    & git -C $mainRoot remote add origin $remoteRoot
    & git -C $mainRoot push -u origin main | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to configure the temporary origin/main tracking branch."
    }
    & git -C $mainRoot worktree add -b "codex/native-closeout-contract" $featureRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create temporary feature worktree."
    }

    Invoke-ResumeGitBoundaryContract -Root $temporaryRoot
    $resumeLifecycleOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $resumeLifecycleContract -SourceRoot $SourceRoot 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "The executable exact-child resume lifecycle contract failed: $resumeLifecycleOutput"
    }

    $siteUrlInjectionMarker = Join-Path $temporaryRoot "site-url-injection-marker.txt"
    $maliciousSiteUrl = "http://127.0.0.1:5137'; `$null = `$(Set-Content -LiteralPath '$siteUrlInjectionMarker' -Value 'executed'); '"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $unsafeSiteUrlOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
            -FeatureWorktree $featureRoot `
            -MainRoot $mainRoot `
            -DryRun `
            -SiteUrl $maliciousSiteUrl 2>&1 | Out-String
        $unsafeSiteUrlExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($unsafeSiteUrlExitCode -eq 0) {
        throw "The native closeout wrapper accepted an executable SiteUrl payload."
    }
    if (Test-Path -LiteralPath $siteUrlInjectionMarker) {
        throw "The native closeout wrapper executed caller-controlled SiteUrl content."
    }
    if ($unsafeSiteUrlOutput -notmatch 'A fixed HTTP loopback origin is required') {
        throw "The native closeout wrapper did not reject SiteUrl through the fixed-loopback authority gate: $unsafeSiteUrlOutput"
    }

    $closeoutText = Get-Content -LiteralPath $closeoutScript -Raw
    if ($closeoutText -match '-SiteUrl\s+''\$SiteUrl''' -or
        $closeoutText -match '-SiteUrl\s+"\$SiteUrl"') {
        throw "The native closeout wrapper still interpolates SiteUrl into executable child command text."
    }
    $siteUrlGateIndex = $closeoutText.IndexOf('$SiteUrl = (Get-FixedLoopbackOrigin -SiteUrl $SiteUrl).Origin')
    $worktreeResolutionIndex = $closeoutText.IndexOf('$FeatureWorktree = (Resolve-Path -LiteralPath $FeatureWorktree).Path')
    $commandConstructionIndex = $closeoutText.IndexOf('$nativeDeployCommand =')
    if ($siteUrlGateIndex -lt 0 -or
        $worktreeResolutionIndex -le $siteUrlGateIndex -or
        $commandConstructionIndex -le $siteUrlGateIndex) {
        throw "The native closeout wrapper does not validate SiteUrl before worktree side effects and child-command construction."
    }
    if ($closeoutText -notmatch '\[hashtable\]\$Environment\s*=\s*@\{\}' -or
        $closeoutText -notmatch 'EnvironmentVariables\[\[string\]\$entry\.Key\]' -or
        $closeoutText -notmatch '-Environment\s+\$nativeCommandEnvironment') {
        throw "The native closeout wrapper does not transport child arguments as non-code process data."
    }

    $explicitFalseCommand = "& '$($closeoutScript.Replace("'", "''"))' -FeatureWorktree '$($featureRoot.Replace("'", "''"))' -MainRoot '$($mainRoot.Replace("'", "''"))' -DryRun -KeepOutlookHostDisabled:`$false"
    $explicitFalseEncoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($explicitFalseCommand))
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $explicitFalseOutput = & powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $explicitFalseEncoded 2>&1 | Out-String
        $explicitFalseExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($explicitFalseExitCode -eq 0 -or $explicitFalseOutput -notmatch "Outlook host activation is not authorised") {
        throw "The native closeout wrapper accepts an explicit request to activate the Outlook host."
    }

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
        -FeatureWorktree $featureRoot `
        -MainRoot $mainRoot `
        -DryRun `
        -SiteUrl "http://127.0.0.1:5137" `
        -ApplyMigrations `
        -ConfirmApplyMigrations `
        -ConfirmApprovedLegacyLocalSurfaceChanges 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "The native closeout dry-run failed: $output"
    }

    $summary = $output | ConvertFrom-Json
    if (-not $summary.ok) {
        throw "The native closeout dry-run did not report success."
    }

    $normalDirtyMainPath = Join-Path $mainRoot "normal-route-dirty-main.txt"
    Set-Content -LiteralPath $normalDirtyMainPath -Value "normal closeout must fail closed"
    $normalDirtyMainOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
        -FeatureWorktree $featureRoot `
        -MainRoot $mainRoot `
        -DryRun 2>&1 | Out-String
    $normalDirtyMainSummary = $normalDirtyMainOutput | ConvertFrom-Json
    if ($LASTEXITCODE -eq 0 -or $normalDirtyMainSummary.failed_step -cne "verify-main-clean") {
        throw "The normal closeout route did not reject a dirty main worktree."
    }
    Remove-Item -LiteralPath $normalDirtyMainPath

    $expectedSteps = @(
        "verify-main-clean",
        "dotnet-tool-restore",
        "dotnet-restore-locked",
        "dotnet-build-release",
        "dotnet-test-native",
        "native-closeout-contract",
        "native-outlook-scheduled-host-contract",
        "native-outlook-host-composition",
        "native-deployment-contract",
        "phase-5-deployment-contract",
        "approved-local-surface-gmail-guard-contract",
        "legacy-gmail-regression",
        "legacy-gmail-preservation-diff-guard",
        "feature-commit",
        "sync-main",
        "squash-merge",
        "dotnet-build-release-main",
        "dotnet-test-native-main",
        "legacy-gmail-regression-main",
        "legacy-gmail-preservation-diff-guard-main",
        "main-commit",
        "push-main",
        "verify-origin-main",
        "deploy-native-windows",
        "post-deploy-native-worker-supervision-validation",
        "post-deploy-native-outlook-ingress-validation",
        "post-deploy-phase-5-validation",
        "post-deploy-validation-record-commit",
        "post-deploy-validation-record-push",
        "cleanup-worktree"
    )
    $actualSteps = @($summary.steps | ForEach-Object { $_.name })
    foreach ($expectedStep in $expectedSteps) {
        if ($expectedStep -notin $actualSteps) {
            throw "The native closeout plan is missing step $expectedStep."
        }
    }

    $scheduledHostContractIndex = [Array]::IndexOf($actualSteps, "native-outlook-scheduled-host-contract")
    $hostCompositionIndex = [Array]::IndexOf($actualSteps, "native-outlook-host-composition")
    $deploymentContractIndex = [Array]::IndexOf($actualSteps, "native-deployment-contract")
    $phase5DeploymentContractIndex = [Array]::IndexOf($actualSteps, "phase-5-deployment-contract")
    $deployIndex = [Array]::IndexOf($actualSteps, "deploy-native-windows")
    if ($scheduledHostContractIndex -lt 0 -or $hostCompositionIndex -le $scheduledHostContractIndex -or
        $deploymentContractIndex -le $hostCompositionIndex -or
        $phase5DeploymentContractIndex -le $deploymentContractIndex -or
        $deployIndex -le $phase5DeploymentContractIndex) {
        throw "The native Outlook scheduler contracts must run in order before deployment."
    }

    $scheduledHostContractCommand = [string]$summary.steps[$scheduledHostContractIndex].command
    $hostCompositionCommand = [string]$summary.steps[$hostCompositionIndex].command
    $deploymentContractCommand = [string]$summary.steps[$deploymentContractIndex].command
    $phase5DeploymentContractCommand = [string]$summary.steps[$phase5DeploymentContractIndex].command
    if ($scheduledHostContractCommand -notmatch 'tests\\native\\outlook-scheduled-host-contract\.ps1' -or
        $scheduledHostContractCommand -notmatch '-SourceRoot\s+\.' -or
        $hostCompositionCommand -notmatch 'tests\\native\\outlook-host-composition\.ps1' -or
        $deploymentContractCommand -notmatch 'tests\\native\\native-deployment-plan\.ps1' -or
        $phase5DeploymentContractCommand -notmatch 'tests\\native\\phase-5-deployment-safety\.ps1') {
        throw "The native closeout plan is missing the required Outlook scheduler verification commands."
    }

    $commands = @($summary.steps | ForEach-Object { $_.command }) -join "`n"
    $forbiddenCommands = @(
        "docker",
        "npm --prefix",
        "update-flux.ps1",
        "flux_llm_kb",
        "rabbitmq",
        "vespa"
    )
    $foundForbidden = @($forbiddenCommands | Where-Object { $commands -match [regex]::Escape($_) })
    if ($foundForbidden.Count -gt 0) {
        throw "The native closeout plan contains forbidden active commands: $($foundForbidden -join ', ')."
    }

    $validationIndex = [Array]::IndexOf($actualSteps, "post-deploy-native-worker-supervision-validation")
    $outlookValidationIndex = [Array]::IndexOf($actualSteps, "post-deploy-native-outlook-ingress-validation")
    $phase5ValidationIndex = [Array]::IndexOf($actualSteps, "post-deploy-phase-5-validation")
    $validationCommitIndex = [Array]::IndexOf($actualSteps, "post-deploy-validation-record-commit")
    $validationPushIndex = [Array]::IndexOf($actualSteps, "post-deploy-validation-record-push")
    $cleanupIndex = [Array]::IndexOf($actualSteps, "cleanup-worktree")
    if ($deployIndex -lt 0 -or $validationIndex -le $deployIndex -or
        $outlookValidationIndex -le $validationIndex -or
        $phase5ValidationIndex -le $outlookValidationIndex -or
        $validationCommitIndex -le $phase5ValidationIndex -or $validationPushIndex -le $validationCommitIndex -or
        $cleanupIndex -le $validationPushIndex) {
        throw "The native closeout plan must validate, commit and push fresh sanitised evidence only after deployment and before cleanup."
    }

    $deploymentText = Get-Content -LiteralPath $deploymentScript -Raw
    foreach ($activationCommand in @(
        'Register-ScheduledTask', 'Enable-ScheduledTask', 'Start-ScheduledTask',
        'Register-OutlookHostTask', 'Install-OutlookHostTask')) {
        if ($closeoutText -match ("\b{0}\b" -f [regex]::Escape($activationCommand)) -or
            $deploymentText -match ("\b{0}\b" -f [regex]::Escape($activationCommand))) {
            throw "The closeout deployment path retains an Outlook activation command: $activationCommand"
        }
    }
    if ($closeoutText -notmatch '(?s)if\s*\(\s*-not\s+\$SkipDeploy\s*\)\s*\{.*Invoke-FeatureStep\s+-Name\s+"deploy-native-windows"' -or
        $closeoutText -match '(?m)Invoke-FeatureStep\s+-Name\s+"deploy-native-windows".*-RunInDryRun') {
        throw "The native closeout path has lost its explicit deployment gate."
    }
    $validationCommand = [string]$summary.steps[$validationIndex].command
    if ($validationCommand -notmatch 'validate-native-worker-supervision\.ps1' -or
        $validationCommand -notmatch "-ExpectedMigrationId '20260810185641_AddNativeWorkerSupervision'" -or
        $validationCommand -notmatch '-ValidationRecordPath') {
        throw "The native closeout plan does not invoke the narrowly parameterised native-worker validation hook."
    }
    $outlookValidationCommand = [string]$summary.steps[$outlookValidationIndex].command
    if ($outlookValidationCommand -notmatch 'validate-native-outlook-ingress\.ps1' -or
        $outlookValidationCommand -match '-ExpectedMigrationId' -or
        $outlookValidationCommand -match '-BaselineMigrationId' -or
        $outlookValidationCommand -notmatch '-ValidationRecordPath') {
        throw "The native closeout plan must let the Outlook validator derive its migration contract from the authoritative deployment plan."
    }
    $phase5ValidationCommand = [string]$summary.steps[$phase5ValidationIndex].command
    if ($phase5ValidationCommand -notmatch 'validate-phase-5-deployment\.ps1' -or
        $phase5ValidationCommand -notmatch '-ValidationRecordPath') {
        throw "The native closeout plan does not invoke the read-only Phase 5 deployment validator."
    }
    $deployCommand = [string]$summary.steps[$deployIndex].command
    if ($deployCommand -notmatch '-KeepOutlookHostDisabled') {
        throw "The native closeout plan does not keep the Outlook host disabled during deployment."
    }

    $gmailRegressionIndex = [Array]::IndexOf($actualSteps, "legacy-gmail-regression")
    $gmailGuardIndex = [Array]::IndexOf($actualSteps, "legacy-gmail-preservation-diff-guard")
    $featureCommitIndex = [Array]::IndexOf($actualSteps, "feature-commit")
    $gmailRegressionMainIndex = [Array]::IndexOf($actualSteps, "legacy-gmail-regression-main")
    $gmailGuardMainIndex = [Array]::IndexOf($actualSteps, "legacy-gmail-preservation-diff-guard-main")
    $mainCommitIndex = [Array]::IndexOf($actualSteps, "main-commit")
    if ($gmailGuardIndex -ne ($featureCommitIndex - 1) -or $gmailRegressionIndex -ge $gmailGuardIndex -or
        $gmailGuardMainIndex -ne ($mainCommitIndex - 1) -or $gmailRegressionMainIndex -ge $gmailGuardMainIndex) {
        throw "The legacy Gmail regression and diff guard must run immediately before each feature/main commit boundary."
    }
    foreach ($guardIndex in @($gmailGuardIndex, $gmailGuardMainIndex)) {
        if ([string]$summary.steps[$guardIndex].command -notmatch '-ConfirmApprovedLegacyLocalSurfaceChanges') {
            throw "The native closeout plan did not propagate the explicit approved local-surface confirmation."
        }
    }
    $gmailRegressionCommand = [string]$summary.steps[$gmailRegressionIndex].command
    $gmailRegressionMainCommand = [string]$summary.steps[$gmailRegressionMainIndex].command
    foreach ($testPath in @("test_mail_ingestion.py", "test_mail_oauth.py", "test_mail_post_process.py", "test_mail_scheduler.py", "test_mail_cli_rest.py", "test_background_jobs.py", "test_worker.py")) {
        if ($gmailRegressionCommand -notmatch [regex]::Escape($testPath)) {
            throw "The native closeout plan is missing focused legacy Gmail regression $testPath."
        }
        if ($gmailRegressionMainCommand -notmatch [regex]::Escape($testPath)) {
            throw "The squashed-main closeout plan is missing focused legacy Gmail regression $testPath."
        }
    }

    if (-not (Test-Path -LiteralPath $refreshScript -PathType Leaf)) {
        throw "The staged-squash refresh helper is missing."
    }
    $refreshText = Get-Content -LiteralPath $refreshScript -Raw
    if ($refreshText -notmatch 'ResumeGitBoundary\.psm1' -or
        $refreshText -notmatch 'Import-Module' -or
        $refreshText -notmatch 'read-tree.*-n.*-m.*-u' -or
        $refreshText -notmatch 'read-tree.*-m.*-u' -or
        $refreshText -match '(?m)^\s*(?:&\s*)?git\b' -or
        $refreshText -match '(?m)^\s*(?:&\s*)?git\b.*\bapply\b' -or
        $refreshText -match '\b(?:diff\s+--binary|fetch\s+origin)\b') {
        throw "The staged-squash refresh helper must use the authenticated boundary read-tree lifecycle rather than raw Git or patches."
    }
    if ($closeoutText -notmatch '\$resumeBoundaryCommitCommand' -or
        $closeoutText -notmatch 'Invoke-FeatureStep\s+-Name\s+"refresh-staged-squash"[^\r\n]*-RunInDryRun' -or
        $closeoutText -notmatch '\$resumeCleanupCommand' -or
        $closeoutText -notmatch 'registered expected feature worktree and branch' -or
        $closeoutText -match '\$resumeGitProvenanceSafetyCommand|\$resumeMainPrecommitCommand|\$resumeMainCommitCommand' -or
        $closeoutText -match '(?m)^\s*if\s*\(\$ResumeStagedSquash\)\s*\{\s*Invoke-FeatureStep[^\r\n]*\$(?:resumeMainPrecommitCommand|resumeMainCommitCommand|verifyOriginMainCommand)' -or
        $closeoutText -match 'ResumeStagedSquash\)\s*\{\s*.*Command\s+''git\s+(?:commit|push|fetch|add|merge|apply|checkout|reset|restore)') {
        throw "The staged-squash resume closeout route must delegate to the executable authenticated lifecycle and boundary cleanup children."
    }
    if ($closeoutText -match 'if\s*\(\$ResumeStagedSquash\)\s*\{\s*Invoke-FeatureStep\s+-Name\s+"legacy-gmail-preservation-diff-guard(?:-main)?"[^\r\n]*-RunInDryRun') {
        throw "Resume dry-run must run only boundary authentication, remote read and read-tree preview; Gmail guard Git reads must be skipped."
    }
    if ($closeoutText -match '-ExpectedBranch ''\$ExpectedFeatureBranch''' -or
        $closeoutText -notmatch 'ExpectedBranch \$env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH' -or
        $closeoutText -notmatch '(?s)\$resumeCleanupCommand\s*=\s+@''.*New-ResumeGitBoundary\s+-Worktree\s+\$env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE.*diff-files.*worktree.*remove') {
        throw "Resume child commands must take the expected feature branch from ProcessStartInfo environment and re-authenticate a clean feature worktree before cleanup."
    }
    $gmailGuardText = Get-Content -LiteralPath (Join-Path $SourceRoot 'scripts\dev\assert-legacy-gmail-unchanged.ps1') -Raw
    if ($gmailGuardText -notmatch '\[switch\]\$ResumeBoundary' -or
        $gmailGuardText -notmatch 'Invoke-ResumeGit\s+-Boundary' -or
        $gmailGuardText -notmatch '(?s)try\s*\{.*finally\s*\{\s*if\s*\(\$null\s+-ne\s+\$resumeBoundaryObject\)\s*\{\s*Remove-ResumeGitBoundary') {
        throw "Resume Gmail guard must route authenticated reads through the boundary and dispose it in finally."
    }

    function Invoke-StagedSquashRefresh {
        param(
            [string]$ExpectedMainHead,
            [string]$ExpectedStagedFeatureHead,
            [string]$ExpectedFeatureHead,
            [string]$ExpectedFeatureBranch,
            [switch]$DryRun
        )

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $refreshArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $refreshScript,
                '-MainWorktree', $mainRoot, '-FeatureWorktree', $featureRoot,
                '-ExpectedMainHead', $ExpectedMainHead, '-ExpectedStagedFeatureHead', $ExpectedStagedFeatureHead,
                '-ExpectedFeatureHead', $ExpectedFeatureHead, '-ExpectedFeatureBranch', $ExpectedFeatureBranch,
                '-ExpectedOriginUrl', $remoteRoot)
            if ($DryRun) { $refreshArguments += '-DryRun' }
            $output = & powershell @refreshArguments 2>&1 | Out-String
            return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
    }

    function Assert-RefreshRejectsWithoutMainMutation {
        param([scriptblock]$Action)

        $beforeHead = (& git -C $mainRoot rev-parse HEAD).Trim()
        $beforeTree = (& git -C $mainRoot write-tree).Trim()
        $result = & $Action
        $afterHead = (& git -C $mainRoot rev-parse HEAD).Trim()
        $afterTree = (& git -C $mainRoot write-tree).Trim()
        if ($result.ExitCode -eq 0 -or $beforeHead -cne $afterHead -or $beforeTree -cne $afterTree) {
            throw "The staged-squash refresh accepted hostile state or mutated main before rejecting it: $($result.Output)"
        }
    }

    function Assert-RefreshRejectsWithoutMainHeadMutation {
        param([scriptblock]$Action)

        $beforeHead = (& git -C $mainRoot rev-parse HEAD).Trim()
        $result = & $Action
        $afterHead = (& git -C $mainRoot rev-parse HEAD).Trim()
        if ($result.ExitCode -eq 0 -or $beforeHead -cne $afterHead) {
            throw "The staged-squash refresh accepted an unmerged index or changed main HEAD before rejecting it: $($result.Output)"
        }
    }

    Set-Content -LiteralPath (Join-Path $featureRoot "reviewed.txt") -Value "old reviewed feature content"
    Set-Content -LiteralPath (Join-Path $featureRoot "old-added.txt") -Value "old staged addition"
    & git -C $featureRoot add reviewed.txt old-added.txt
    & git -C $featureRoot -c user.name="Native Closeout Test" -c user.email="native-closeout@example.invalid" commit -m "old reviewed feature" | Out-Null
    $oldFeatureHead = (& git -C $featureRoot rev-parse HEAD).Trim()

    Set-Content -LiteralPath (Join-Path $featureRoot "reviewed.txt") -Value "new reviewed feature content"
    Remove-Item -LiteralPath (Join-Path $featureRoot "delete-me.txt")
    $binaryBytes = [byte[]](0, 1, 2, 255, 17, 42, 0)
    [System.IO.File]::WriteAllBytes((Join-Path $featureRoot "binary-delta.bin"), $binaryBytes)
    & git -C $featureRoot add -A
    & git -C $featureRoot -c user.name="Native Closeout Test" -c user.email="native-closeout@example.invalid" commit -m "new reviewed feature" | Out-Null
    $newFeatureHead = (& git -C $featureRoot rev-parse HEAD).Trim()
    $mainHead = (& git -C $mainRoot rev-parse HEAD).Trim()
    $featureBranch = (& git -C $featureRoot branch --show-current).Trim()
    & git -C $mainRoot merge --squash $oldFeatureHead | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to stage the reviewed old feature tree for the resume contract."
    }
    if ((& git -C $mainRoot write-tree).Trim() -cne (& git -C $featureRoot rev-parse "$oldFeatureHead^{tree}").Trim()) {
        throw "The resume contract fixture did not stage the exact old feature tree."
    }
    & git -C $mainRoot config --local --remove-section submodule.retained-submodule

    # The helper's dry-run performs real boundary authentication, remote read
    # and read-tree preview, but must preserve every protected byte/ref/object.
    $helperDryRunSnapshot = Get-ResumeBoundarySnapshot -Worktree $mainRoot
    $helperDryRun = Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch -DryRun
    if ($helperDryRun.ExitCode -ne 0) { throw "The boundary staged-squash helper dry-run failed: $($helperDryRun.Output)" }
    Assert-ResumeBoundarySnapshotUnchanged -Before $helperDryRunSnapshot -After (Get-ResumeBoundarySnapshot -Worktree $mainRoot) -Context 'boundary staged-squash dry-run preview'

    # Production closeout keeps its immutable GitHub origin. A local disposable
    # entrypoint must therefore fail closed after attempting its RunInDryRun
    # boundary refresh, while still preserving main state.
    $resumeLogDirectory = Join-Path $mainRoot '.agents'
    if (Test-Path -LiteralPath $resumeLogDirectory) {
        Remove-Item -LiteralPath $resumeLogDirectory -Recurse -Force
    }
    $wholeResumeDryRunSnapshot = Get-ResumeBoundarySnapshot -Worktree $mainRoot
    $dryRunStagedTree = (& git -C $mainRoot write-tree).Trim()
    $dryRunHead = (& git -C $mainRoot rev-parse HEAD).Trim()
    $dryRunResumeOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
        -FeatureWorktree $featureRoot `
        -MainRoot $mainRoot `
        -DryRun `
        -ResumeStagedSquash `
        -ExpectedMainHead $mainHead `
        -ExpectedStagedFeatureHead $oldFeatureHead `
        -ExpectedFeatureHead $newFeatureHead `
        -ExpectedFeatureBranch $featureBranch 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        throw "The staged-squash closeout dry-run accepted a disposable non-GitHub origin."
    }
    if ((& git -C $mainRoot rev-parse HEAD).Trim() -cne $dryRunHead -or
        (& git -C $mainRoot write-tree).Trim() -cne $dryRunStagedTree) {
        throw "The staged-squash closeout dry-run changed main before failing its immutable-origin check."
    }
    Assert-ResumeBoundarySnapshotUnchanged -Before $wholeResumeDryRunSnapshot -After (Get-ResumeBoundarySnapshot -Worktree $mainRoot) -Context 'whole complete-feature resume dry-run invocation'

    # Replacing the reviewed current feature commit with a same-parent malicious
    # commit must fail before the staged old tree can be refreshed.
    $maliciousRoot = Join-Path $temporaryRoot "malicious-replacement"
    & git -C $mainRoot worktree add -b "codex/malicious-replacement" $maliciousRoot $oldFeatureHead | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create the hostile replacement worktree."
    }
    Set-Content -LiteralPath (Join-Path $maliciousRoot "replacement-injected.txt") -Value "must never reach main"
    & git -C $maliciousRoot add replacement-injected.txt
    & git -C $maliciousRoot commit -m "malicious same-parent replacement" | Out-Null
    $maliciousReplacementHead = (& git -C $maliciousRoot rev-parse HEAD).Trim()
    & git -C $featureRoot replace $newFeatureHead $maliciousReplacementHead
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    & git -C $featureRoot replace -d $newFeatureHead | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to remove the hostile replacement fixture."
    }

    $commonGitDirectory = (& git -C $mainRoot rev-parse --git-common-dir).Trim()
    if (-not [System.IO.Path]::IsPathRooted($commonGitDirectory)) {
        $commonGitDirectory = Join-Path $mainRoot $commonGitDirectory
    }
    $graftsPath = Join-Path $commonGitDirectory "info\grafts"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $graftsPath) | Out-Null
    [System.IO.File]::WriteAllText($graftsPath, "$newFeatureHead $mainHead`n", (New-Object System.Text.UTF8Encoding($false)))
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    Remove-Item -LiteralPath $graftsPath -Force

    foreach ($operationMarker in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-apply', 'rebase-merge', 'sequencer')) {
        foreach ($operationWorktree in @($mainRoot, $featureRoot)) {
            $operationGitDirectory = (& git -C $operationWorktree rev-parse --absolute-git-dir).Trim()
            $operationPath = Join-Path $operationGitDirectory $operationMarker
            if ($operationMarker -in @('rebase-apply', 'rebase-merge', 'sequencer')) {
                New-Item -ItemType Directory -Force -Path $operationPath | Out-Null
            } else {
                Set-Content -LiteralPath $operationPath -Value 'resolved operation marker must fail resume authentication'
            }
            Assert-RefreshRejectsWithoutMainMutation {
                Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
            }
            Remove-Item -LiteralPath $operationPath -Recurse -Force
        }
    }

    Set-Content -LiteralPath (Join-Path $mainRoot "old-added.txt") -Value "arbitrary staged content"
    & git -C $mainRoot add old-added.txt
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    & git -C $mainRoot read-tree $oldFeatureHead
    Set-Content -LiteralPath (Join-Path $mainRoot "old-added.txt") -Value "old staged addition"
    $oldAddedBlob = (& git -C $featureRoot rev-parse "${oldFeatureHead}:old-added.txt").Trim()
    if ($oldAddedBlob -notmatch '^[0-9a-f]{40}$') {
        throw "Unable to identify the unmerged-index fixture blob."
    }
    & git -C $mainRoot read-tree --empty
    $unmergedIndexInfo = @(
        "100644 ${oldAddedBlob} 1$([char]9)old-added.txt",
        "100644 ${oldAddedBlob} 2$([char]9)old-added.txt"
    ) -join "`n"
    $indexInfoPath = Join-Path $temporaryRoot "unmerged-index-info.txt"
    [System.IO.File]::WriteAllBytes($indexInfoPath, [System.Text.Encoding]::ASCII.GetBytes($unmergedIndexInfo + "`n"))
    $indexInfoProcess = [System.Diagnostics.Process]::new()
    $indexInfoProcess.StartInfo.FileName = "cmd.exe"
    $indexInfoProcess.StartInfo.Arguments = ('/d /s /c ""git" -C "{0}" update-index --add --index-info < "{1}""' -f $mainRoot, $indexInfoPath)
    $indexInfoProcess.StartInfo.UseShellExecute = $false
    [void]$indexInfoProcess.Start()
    $indexInfoProcess.WaitForExit()
    Remove-Item -LiteralPath $indexInfoPath -Force
    if ($indexInfoProcess.ExitCode -ne 0) {
        throw "Unable to populate the unmerged-index resume contract fixture."
    }
    if (-not ((& git -C $mainRoot ls-files -u | Out-String).Trim())) {
        throw "Unable to create the unmerged-index resume contract fixture."
    }
    Assert-RefreshRejectsWithoutMainHeadMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    & git -C $mainRoot read-tree $oldFeatureHead

    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch "codex/wrong-branch"
    }
    $untrackedMainPath = Join-Path $mainRoot "hostile-untracked.txt"
    Set-Content -LiteralPath $untrackedMainPath -Value "hostile"
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    Remove-Item -LiteralPath $untrackedMainPath
    $submoduleDirtyPath = Join-Path $mainRoot "retained-submodule\tracked.txt"
    Set-Content -LiteralPath $submoduleDirtyPath -Value "unstaged hostile submodule content"
    & git -C $mainRoot config diff.ignoreSubmodules all
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    & git -C $mainRoot config --unset diff.ignoreSubmodules
    Set-Content -LiteralPath $submoduleDirtyPath -Value "clean submodule content"
    Set-Content -LiteralPath (Join-Path $mainRoot "reviewed.txt") -Value "unstaged hostile main content"
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    Set-Content -LiteralPath (Join-Path $mainRoot "reviewed.txt") -Value "old reviewed feature content"
    $dirtyFeaturePath = Join-Path $featureRoot "hostile-untracked.txt"
    Set-Content -LiteralPath $dirtyFeaturePath -Value "hostile"
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    Remove-Item -LiteralPath $dirtyFeaturePath
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead "ABCDEF0123456789ABCDEF0123456789ABCDEF01" -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $oldFeatureHead -ExpectedFeatureBranch $featureBranch
    }

    # Fixture commits use only per-command identities. Production resume derives
    # its identity from immutable authenticated expected-main commit headers.
    & git -C $mainRoot read-tree --reset -u $oldFeatureHead
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to restore the disposable main fixture to the approved staged feature tree."
    }

    $refreshResult = Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $oldFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    if ($refreshResult.ExitCode -ne 0 -or
        (& git -C $mainRoot rev-parse HEAD).Trim() -cne $mainHead -or
        (& git -C $mainRoot write-tree).Trim() -cne (& git -C $featureRoot rev-parse "$newFeatureHead^{tree}").Trim() -or
        (Test-Path -LiteralPath (Join-Path $mainRoot "delete-me.txt")) -or
        -not [System.Linq.Enumerable]::SequenceEqual([System.IO.File]::ReadAllBytes((Join-Path $mainRoot "binary-delta.bin")), $binaryBytes)) {
        throw "The staged-squash refresh did not apply the exact binary reviewed delta: $($refreshResult.Output)"
    }
    $noOpRefreshResult = Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $newFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    if ($noOpRefreshResult.ExitCode -ne 0) {
        throw "The staged-squash refresh did not authenticate an unchanged reviewed stage: $($noOpRefreshResult.Output)"
    }

    $resumeOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
        -FeatureWorktree $featureRoot `
        -MainRoot $mainRoot `
        -DryRun `
        -ResumeStagedSquash `
        -ExpectedMainHead $mainHead `
        -ExpectedStagedFeatureHead $newFeatureHead `
        -ExpectedFeatureHead $newFeatureHead `
        -ExpectedFeatureBranch $featureBranch 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        throw "The staged-squash resume route did not fail closed through its authenticated immutable-origin refresh."
    }
    $resumeExplicitFalseCommand = "& '$($closeoutScript.Replace("'", "''"))' -FeatureWorktree '$($featureRoot.Replace("'", "''"))' -MainRoot '$($mainRoot.Replace("'", "''"))' -DryRun -ResumeStagedSquash -ExpectedMainHead '$mainHead' -ExpectedStagedFeatureHead '$newFeatureHead' -ExpectedFeatureHead '$newFeatureHead' -ExpectedFeatureBranch '$featureBranch' -KeepOutlookHostDisabled:`$false"
    $resumeExplicitFalseEncoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($resumeExplicitFalseCommand))
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $resumeExplicitFalseOutput = & powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $resumeExplicitFalseEncoded 2>&1 | Out-String
        $resumeExplicitFalseExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($resumeExplicitFalseExitCode -eq 0 -or $resumeExplicitFalseOutput -notmatch "Outlook host activation is not authorised") {
        throw "The staged-squash resume route accepts an explicit request to activate the Outlook host."
    }
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $partialResumeOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript -FeatureWorktree $featureRoot -MainRoot $mainRoot -DryRun -ResumeStagedSquash -ExpectedMainHead $mainHead 2>&1 | Out-String
        $partialResumeExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($partialResumeExitCode -eq 0 -or $partialResumeOutput -notmatch "requires all expected") {
        throw "The staged-squash resume route accepts partial expected identity input."
    }

    # Keep origin/main deliberately stale after an explicit fetch: only FETCH_HEAD
    # can reliably report the fetched main commit under this fetch mapping.
    & git -C $mainRoot config remote.origin.fetch "+refs/heads/not-main:refs/remotes/origin/not-main"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to configure the nonstandard fetch mapping fixture."
    }
    & git clone -b main $remoteRoot (Join-Path $temporaryRoot "remote-advance") | Out-Null
    $remoteAdvanceRoot = Join-Path $temporaryRoot "remote-advance"
    Set-Content -LiteralPath (Join-Path $remoteAdvanceRoot "remote-advance.txt") -Value "advance origin/main"
    & git -C $remoteAdvanceRoot add remote-advance.txt
    & git -C $remoteAdvanceRoot -c user.name="Native Closeout Test" -c user.email="native-closeout@example.invalid" commit -m "advance origin" | Out-Null
    & git -C $remoteAdvanceRoot push origin main | Out-Null
    Assert-RefreshRejectsWithoutMainMutation {
        Invoke-StagedSquashRefresh -ExpectedMainHead $mainHead -ExpectedStagedFeatureHead $newFeatureHead -ExpectedFeatureHead $newFeatureHead -ExpectedFeatureBranch $featureBranch
    }

    $gmailGuardScript = Join-Path $SourceRoot "scripts\dev\assert-legacy-gmail-unchanged.ps1"
    foreach ($relativePath in $gmailSchedulingPaths) {
        $featurePath = Join-Path $featureRoot $relativePath
        Set-Content -LiteralPath $featurePath -Value "# prohibited Gmail scheduling change"
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $ownedPathGuardOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $gmailGuardScript `
                -RepositoryRoot $featureRoot `
                -BaselineRef main 2>&1 | Out-String
            $ownedPathGuardExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($ownedPathGuardExitCode -eq 0) {
            throw "The native closeout diff guard did not protect Gmail-owned path $relativePath."
        }
        Set-Content -LiteralPath $featurePath -Value "# preserved Gmail scheduling fixture"
    }

    Set-Content -LiteralPath (Join-Path $featureRoot "tests\test_mail_oauth.py") -Value "# prohibited Gmail change"
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $guardOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $gmailGuardScript `
            -RepositoryRoot $featureRoot `
            -BaselineRef main `
            -ConfirmApprovedLegacyLocalSurfaceChanges 2>&1 | Out-String
        $guardExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($guardExitCode -eq 0 -or $guardOutput -notmatch "Closeout stopped because") {
        throw "The normal raw Gmail guard did not stop a legacy Gmail-owned file change."
    }

    $branchCommandMatch = [regex]::Match($closeoutText, '(?s)\$resumeFeatureGmailGuardCommand\s*=\s+@''\r?\n(.*?)\r?\n''@')
    if (-not $branchCommandMatch.Success) { throw 'The resume Gmail child command must be a non-interpolated environment-backed script.' }
    $branchInjectionMarker = Join-Path $temporaryRoot 'expected-feature-branch-injection.marker'
    $maliciousBranch = "codex/branch'; [System.IO.File]::WriteAllText('$($branchInjectionMarker.Replace("'", "''"))', 'executed'); #"
    $previousExpectedBranch = $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH
    $previousGuardScript = $env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT
    try {
        $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH = $maliciousBranch
        $env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT = $gmailGuardScript
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & ([scriptblock]::Create($branchCommandMatch.Groups[1].Value)) 2>$null | Out-Null
        } finally { $ErrorActionPreference = $previousErrorActionPreference }
        if (Test-Path -LiteralPath $branchInjectionMarker) { throw 'Expected feature branch text escaped the ProcessStartInfo environment boundary and executed.' }
    } finally {
        if ($null -eq $previousExpectedBranch) { Remove-Item Env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH -ErrorAction SilentlyContinue } else { $env:FLUXKNOWLEDGE_CLOSEOUT_EXPECTED_FEATURE_BRANCH = $previousExpectedBranch }
        if ($null -eq $previousGuardScript) { Remove-Item Env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT -ErrorAction SilentlyContinue } else { $env:FLUXKNOWLEDGE_CLOSEOUT_GMAIL_GUARD_SCRIPT = $previousGuardScript }
    }

    Invoke-ResumeCleanupLateHeadContract -Root (Join-Path $temporaryRoot 'late-clean-cleanup')

    Write-Output "Native closeout dry-run contract passed."
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
