[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-False {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { throw $Message }
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeoutScript = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$rootGitignore = Join-Path $SourceRoot '.gitignore'
Assert-True (Test-Path -LiteralPath $closeoutScript -PathType Leaf) "Closeout script is missing: $closeoutScript"
Assert-True (Test-Path -LiteralPath $rootGitignore -PathType Leaf) "Root .gitignore is missing: $rootGitignore"

$closeoutText = Get-Content -LiteralPath $closeoutScript -Raw
$featureCommit = [regex]::Match(
    $closeoutText,
    'Invoke-FeatureStep\s+-Name\s+["'']feature-commit["'']\s+-Cwd\s+\$FeatureWorktree\s+-Command\s+"(?<command>(?:[^"]|"")*)"')
Assert-True $featureCommit.Success 'The closeout feature-commit step is missing.'
$featureCommitCommand = $featureCommit.Groups['command'].Value.
    Replace('`$null', '$null').
    Replace("'`$safeCommitMessage'", "'contract staging hygiene'")

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeSddScratch-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    & git init --initial-branch main $temporaryRoot | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Unable to create the temporary staging repository.'
    & git -C $temporaryRoot config user.email 'staging-contract@example.invalid'
    & git -C $temporaryRoot config user.name 'Staging Contract'

    Copy-Item -LiteralPath $rootGitignore -Destination (Join-Path $temporaryRoot '.gitignore')
    $sourcePath = Join-Path $temporaryRoot 'docs\normal-source.md'
    $scratchPath = Join-Path $temporaryRoot '.superpowers\sdd\contract\scratch.md'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sourcePath), (Split-Path -Parent $scratchPath) | Out-Null
    Set-Content -LiteralPath $sourcePath -Value 'normal source must remain staged'
    Set-Content -LiteralPath $scratchPath -Value 'scratch must remain untracked'
    & git -C $temporaryRoot check-ignore --quiet -- .superpowers/sdd/contract/scratch.md
    Assert-True ($LASTEXITCODE -eq 0) 'The root .gitignore does not ignore repository-local SDD scratch.'

    $fixtureGitignore = Get-Content -LiteralPath (Join-Path $temporaryRoot '.gitignore') -Raw
    $fixtureGitignore = [regex]::Replace($fixtureGitignore, '(?m)^\.superpowers/sdd/\r?\n?', '')
    Set-Content -LiteralPath (Join-Path $temporaryRoot '.gitignore') -Value $fixtureGitignore

    Push-Location $temporaryRoot
    try {
        & pwsh -NoProfile -Command $featureCommitCommand
        Assert-True ($LASTEXITCODE -eq 0) 'The closeout feature-commit command failed in its temporary repository.'
    } finally {
        Pop-Location
    }

    $committedPaths = @(& git -C $temporaryRoot show --format= --name-only HEAD)
    Assert-True ($committedPaths -contains 'docs/normal-source.md') `
        'The feature-commit command no longer stages ordinary source changes.'
    Assert-False ($committedPaths -contains '.superpowers/sdd/contract/scratch.md') `
        'The feature-commit command stages repository-local SDD scratch.'
    Assert-True ((@(& git -C $temporaryRoot status --porcelain) | Where-Object { $_ -ceq '?? .superpowers/' }).Count -eq 1) `
        'The feature-commit command does not exclude SDD scratch when the ignore rule is unavailable.'

    Write-Output 'Complete-feature SDD scratch contract passed.'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
