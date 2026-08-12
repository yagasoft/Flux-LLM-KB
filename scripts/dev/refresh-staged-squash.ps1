[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$MainWorktree,
    [Parameter(Mandatory)] [string]$FeatureWorktree,
    [Parameter(Mandatory)] [string]$ExpectedMainHead,
    [Parameter(Mandatory)] [string]$ExpectedStagedFeatureHead,
    [Parameter(Mandatory)] [string]$ExpectedFeatureHead,
    [Parameter(Mandatory)] [string]$ExpectedFeatureBranch,
    [Parameter(Mandatory)] [string]$ExpectedOriginUrl,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$boundaryModule = Join-Path $PSScriptRoot 'ResumeGitBoundary.psm1'
if (-not (Test-Path -LiteralPath $boundaryModule -PathType Leaf)) { throw 'The authenticated resume Git boundary module is missing.' }
Import-Module $boundaryModule -Force

function Assert-ResumeSha { param([string]$Value)
    if ($Value -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected commit identities must be canonical full SHA-1 values.' }
}
function Get-ResumeGitValue { param($Boundary, [string[]]$Arguments)
    $result = Invoke-ResumeGit -Boundary $Boundary -Arguments $Arguments
    if ($result.ExitCode -ne 0) { throw "Authenticated Git $($Arguments[0]) failed: $($result.StdErr.Trim())" }
    return $result.StdOut.Trim()
}
function Test-ResumeGitSuccess { param($Boundary, [string[]]$Arguments)
    return (Invoke-ResumeGit -Boundary $Boundary -Arguments $Arguments).ExitCode -eq 0
}
function Assert-ResumeClean { param($Boundary, [switch]$RequireStagedClean)
    if (-not (Test-ResumeGitSuccess $Boundary @('diff-files', '--quiet', '--no-ext-diff', '--ignore-submodules=none'))) { throw ("The worktree has unstaged changes: " + $Boundary.Worktree + '; index=' + $Boundary.IndexPath + '; cached=' + (Get-ResumeGitValue $Boundary @('ls-files', '--cached')) + '; changed=' + (Get-ResumeGitValue $Boundary @('diff-files', '--name-only', '--no-ext-diff', '--ignore-submodules=none'))) }
    $untracked = Get-ResumeGitValue $Boundary @('ls-files', '--others', '--exclude-standard')
    if ($untracked) { throw ("The worktree has untracked files: " + $Boundary.Worktree + ': ' + $untracked) }
    if ($RequireStagedClean -and -not (Test-ResumeGitSuccess $Boundary @('diff-index', '--cached', '--quiet', 'HEAD', '--'))) { throw 'The feature worktree has staged changes.' }
    if ((Get-ResumeGitValue $Boundary @('ls-files', '-u'))) { throw 'The worktree has unmerged index entries.' }
    foreach ($line in @((Get-ResumeGitValue $Boundary @('ls-files', '-v')) -split "`r?`n")) {
        if ($line -and -not $line.StartsWith('H ', [System.StringComparison]::Ordinal)) { throw 'The worktree uses unsupported index flags.' }
    }
}
function Assert-ResumeWorktreePair { param($MainBoundary, $FeatureBoundary)
    if (-not $MainBoundary.CommonDirectory.Equals($FeatureBoundary.CommonDirectory, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'The requested worktrees do not share an authenticated Git common directory.' }
    $worktreeLines = (Get-ResumeGitValue $MainBoundary @('worktree', 'list', '--porcelain')) -split "`r?`n"
    $registered = @($worktreeLines |
        Where-Object { $_.StartsWith('worktree ', [System.StringComparison]::Ordinal) } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_.Substring('worktree '.Length).Replace('/', [System.IO.Path]::DirectorySeparatorChar)) })
    foreach ($path in @($MainBoundary.Worktree, $FeatureBoundary.Worktree)) {
        if (-not (@($registered | Where-Object { $_.Equals($path, [System.StringComparison]::OrdinalIgnoreCase) }).Count -eq 1)) { throw 'The requested worktrees are not registered authenticated siblings.' }
    }
}
function Assert-ResumeRemoteHead { param($Boundary, [string]$Origin, [string]$ExpectedHead)
    $lines = @(Get-ResumeGitValue $Boundary @('ls-remote', '--refs', $Origin, 'refs/heads/main') -split "`r?`n" | Where-Object { $_ })
    if ($lines.Count -ne 1 -or $lines[0] -cnotmatch ('^' + [regex]::Escape($ExpectedHead) + "`trefs/heads/main$")) { throw 'The authenticated origin/main head advanced or is ambiguous.' }
}

foreach ($value in @($ExpectedMainHead, $ExpectedStagedFeatureHead, $ExpectedFeatureHead)) { Assert-ResumeSha $value }
if (-not $ExpectedFeatureBranch.StartsWith('codex/', [System.StringComparison]::Ordinal)) { throw 'Expected feature branch is invalid.' }
if ([string]::IsNullOrWhiteSpace($ExpectedOriginUrl)) { throw 'Expected origin URL is mandatory.' }
$mainBoundary = $null
$featureBoundary = $null
try {
    $mainBoundary = New-ResumeGitBoundary -Worktree $MainWorktree -ExpectedOriginUrl $ExpectedOriginUrl -ExpectedHead $ExpectedMainHead -ExpectedBranch 'main'
    $featureBoundary = New-ResumeGitBoundary -Worktree $FeatureWorktree -ExpectedOriginUrl $ExpectedOriginUrl -ExpectedHead $ExpectedFeatureHead -ExpectedBranch $ExpectedFeatureBranch
    Assert-ResumeWorktreePair $mainBoundary $featureBoundary
    Assert-ResumeGitPairNoInProgressOperation $mainBoundary $featureBoundary
    foreach ($commit in @($ExpectedMainHead, $ExpectedStagedFeatureHead, $ExpectedFeatureHead)) {
        if ((Get-ResumeGitValue $featureBoundary @('rev-parse', '--verify', ($commit + '^{commit}'))) -cne $commit) { throw 'Expected commit identity is not present in the authenticated feature repository.' }
    }
    if (-not (Test-ResumeGitSuccess $featureBoundary @('merge-base', '--is-ancestor', $ExpectedMainHead, $ExpectedStagedFeatureHead)) -or -not (Test-ResumeGitSuccess $featureBoundary @('merge-base', '--is-ancestor', $ExpectedStagedFeatureHead, $ExpectedFeatureHead))) { throw 'Expected resume commits do not form the required ancestry chain.' }
    Assert-ResumeClean $featureBoundary -RequireStagedClean
    Assert-ResumeClean $mainBoundary
    $oldTree = Get-ResumeGitValue $featureBoundary @('rev-parse', ($ExpectedStagedFeatureHead + '^{tree}'))
    $newTree = Get-ResumeGitValue $featureBoundary @('rev-parse', ($ExpectedFeatureHead + '^{tree}'))
    if (-not (Test-ResumeGitSuccess $mainBoundary @('diff-index', '--cached', '--quiet', $oldTree, '--'))) { throw 'The staged main index does not exactly equal the approved staged feature tree.' }
    Assert-ResumeRemoteHead $mainBoundary $ExpectedOriginUrl $ExpectedMainHead
    $preview = Invoke-ResumeGit -Boundary $mainBoundary -Arguments @('read-tree', '-n', '-m', '-u', $oldTree, $newTree)
    if ($preview.ExitCode -ne 0) { throw "Authenticated staged-squash preview failed: $($preview.StdErr.Trim())" }
    if ($DryRun) { Write-Output 'Staged squash refresh authenticated and previewed; mutating read-tree skipped for dry run.'; return }
    $refresh = Invoke-ResumeGit -Boundary $mainBoundary -Arguments @('read-tree', '-m', '-u', $oldTree, $newTree) -RequireNoInProgressOperation -PeerBoundary $featureBoundary
    if ($refresh.ExitCode -ne 0) { throw "Authenticated staged-squash refresh failed: $($refresh.StdErr.Trim())" }
    if (-not (Test-ResumeGitSuccess $mainBoundary @('diff-index', '--cached', '--quiet', $newTree, '--'))) { throw 'The refreshed main index does not exactly equal the reviewed feature tree.' }
    Assert-ResumeClean $mainBoundary
    Write-Output 'Staged squash refresh authenticated and applied.'
}
finally {
    if ($null -ne $featureBoundary) { Remove-ResumeGitBoundary -Boundary $featureBoundary }
    if ($null -ne $mainBoundary) { Remove-ResumeGitBoundary -Boundary $mainBoundary }
}
