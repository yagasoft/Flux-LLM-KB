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
        "post-deploy-native-worker-supervision-validation",
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

    $deployIndex = [Array]::IndexOf($actualSteps, "deploy-native-windows")
    $validationIndex = [Array]::IndexOf($actualSteps, "post-deploy-native-worker-supervision-validation")
    $validationCommitIndex = [Array]::IndexOf($actualSteps, "post-deploy-validation-record-commit")
    $validationPushIndex = [Array]::IndexOf($actualSteps, "post-deploy-validation-record-push")
    $cleanupIndex = [Array]::IndexOf($actualSteps, "cleanup-worktree")
    if ($deployIndex -lt 0 -or $validationIndex -le $deployIndex -or
        $validationCommitIndex -le $validationIndex -or $validationPushIndex -le $validationCommitIndex -or
        $cleanupIndex -le $validationPushIndex) {
        throw "The native closeout plan must validate, commit and push fresh sanitised evidence only after deployment and before cleanup."
    }
    $validationCommand = [string]$summary.steps[$validationIndex].command
    if ($validationCommand -notmatch 'validate-native-worker-supervision\.ps1' -or
        $validationCommand -notmatch "-ExpectedMigrationId '20260810185641_AddNativeWorkerSupervision'" -or
        $validationCommand -notmatch '-ValidationRecordPath') {
        throw "The native closeout plan does not invoke the narrowly parameterised native-worker validation hook."
    }

    Write-Output "Native closeout dry-run contract passed."
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
