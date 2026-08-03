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

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FluxKnowledgeCloseout-$([Guid]::NewGuid().ToString('N'))"
$mainRoot = Join-Path $temporaryRoot "main"
$featureRoot = Join-Path $temporaryRoot "feature"

try {
    New-Item -ItemType Directory -Path $mainRoot | Out-Null
    & git init --initial-branch main $mainRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create temporary main worktree."
    }
    & git -C $mainRoot config user.email "native-closeout@example.invalid"
    & git -C $mainRoot config user.name "Native Closeout Test"
    Set-Content -LiteralPath (Join-Path $mainRoot ".gitignore") -Value ".agents/"
    Set-Content -LiteralPath (Join-Path $mainRoot "README.md") -Value "temporary closeout contract repository"
    & git -C $mainRoot add .
    & git -C $mainRoot commit -m "initial" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to commit temporary main worktree."
    }
    & git -C $mainRoot worktree add -b "codex/native-closeout-contract" $featureRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create temporary feature worktree."
    }

    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
        -FeatureWorktree $featureRoot `
        -MainRoot $mainRoot `
        -DryRun `
        -ApplyMigrations `
        -ConfirmApplyMigrations 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "The native closeout dry-run failed: $output"
    }

    $summary = $output | ConvertFrom-Json
    if (-not $summary.ok) {
        throw "The native closeout dry-run did not report success."
    }

    $expectedSteps = @(
        "verify-main-clean",
        "dotnet-tool-restore",
        "dotnet-restore-locked",
        "dotnet-build-release",
        "dotnet-test-native",
        "native-closeout-contract",
        "native-deployment-contract",
        "feature-commit",
        "sync-main",
        "squash-merge",
        "dotnet-build-release-main",
        "dotnet-test-native-main",
        "main-commit",
        "push-main",
        "verify-origin-main",
        "deploy-native-windows",
        "cleanup-worktree"
    )
    $actualSteps = @($summary.steps | ForEach-Object { $_.name })
    foreach ($expectedStep in $expectedSteps) {
        if ($expectedStep -notin $actualSteps) {
            throw "The native closeout plan is missing step $expectedStep."
        }
    }

    $commands = @($summary.steps | ForEach-Object { $_.command }) -join "`n"
    $forbiddenCommands = @(
        "python -m pytest",
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

    Write-Output "Native closeout dry-run contract passed."
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
