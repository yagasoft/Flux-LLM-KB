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
$deploymentScript = Join-Path $SourceRoot "scripts\deploy\update-native-windows.ps1"
if (-not (Test-Path -LiteralPath $deploymentScript)) {
    throw "The native deployment script is missing."
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
        "native-outlook-scheduled-host-contract",
        "native-outlook-host-composition",
        "native-deployment-contract",
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
    $deployIndex = [Array]::IndexOf($actualSteps, "deploy-native-windows")
    if ($scheduledHostContractIndex -lt 0 -or $hostCompositionIndex -le $scheduledHostContractIndex -or
        $deploymentContractIndex -le $hostCompositionIndex -or $deployIndex -le $deploymentContractIndex) {
        throw "The native Outlook scheduler contracts must run in order before deployment."
    }

    $scheduledHostContractCommand = [string]$summary.steps[$scheduledHostContractIndex].command
    $hostCompositionCommand = [string]$summary.steps[$hostCompositionIndex].command
    $deploymentContractCommand = [string]$summary.steps[$deploymentContractIndex].command
    if ($scheduledHostContractCommand -notmatch 'tests\\native\\outlook-scheduled-host-contract\.ps1' -or
        $hostCompositionCommand -notmatch 'tests\\native\\outlook-host-composition\.ps1' -or
        $deploymentContractCommand -notmatch 'tests\\native\\native-deployment-plan\.ps1') {
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
    $validationCommitIndex = [Array]::IndexOf($actualSteps, "post-deploy-validation-record-commit")
    $validationPushIndex = [Array]::IndexOf($actualSteps, "post-deploy-validation-record-push")
    $cleanupIndex = [Array]::IndexOf($actualSteps, "cleanup-worktree")
    if ($deployIndex -lt 0 -or $validationIndex -le $deployIndex -or
        $outlookValidationIndex -le $validationIndex -or
        $validationCommitIndex -le $outlookValidationIndex -or $validationPushIndex -le $validationCommitIndex -or
        $cleanupIndex -le $validationPushIndex) {
        throw "The native closeout plan must validate, commit and push fresh sanitised evidence only after deployment and before cleanup."
    }

    $closeoutText = Get-Content -LiteralPath $closeoutScript -Raw
    $deploymentText = Get-Content -LiteralPath $deploymentScript -Raw
    $registrationHelper = [regex]::Match(
        $deploymentText,
        '(?s)function\s+Register-OutlookHostTask\b.*?(?=\r?\nfunction\s+|\z)').Value
    if ([string]::IsNullOrWhiteSpace($registrationHelper) -or
        $closeoutText -match 'Register-ScheduledTask' -or
        $registrationHelper -match '--verbose-com-errors') {
        throw "The closeout path must not register a task directly or enable verbose Outlook diagnostics."
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
        $guardOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
            -FeatureWorktree $featureRoot `
            -MainRoot $mainRoot `
            -DryRun `
            -ApplyMigrations `
            -ConfirmApplyMigrations 2>&1 | Out-String
        $guardExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $guardSummary = $guardOutput | ConvertFrom-Json
    if ($guardExitCode -eq 0 -or $guardSummary.failed_step -ne "legacy-gmail-preservation-diff-guard") {
        throw "The native closeout diff guard did not stop a legacy Gmail-owned file change."
    }

    Write-Output "Native closeout dry-run contract passed."
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
