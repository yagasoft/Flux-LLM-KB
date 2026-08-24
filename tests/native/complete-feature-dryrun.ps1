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
    foreach ($recoverySymbol in @(
        'ResumeStagedSquash',
        'PushVerifiedMainCommit',
        'ResumeGitBoundary',
        'refresh-staged-squash'
    )) {
        if ($closeoutText -match [regex]::Escape($recoverySymbol)) {
            throw "The ordinary closeout route still contains incident recovery symbol $recoverySymbol."
        }
    }
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

    Set-Content -LiteralPath (Join-Path $mainRoot 'dirty-main.txt') -Value 'ordinary closeout must reject dirty main'
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $dirtyMainOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $closeoutScript `
            -FeatureWorktree $featureRoot `
            -MainRoot $mainRoot `
            -DryRun `
            -ApplyMigrations `
            -ConfirmApplyMigrations `
            -ConfirmApprovedLegacyLocalSurfaceChanges 2>&1 | Out-String
        $dirtyMainExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Remove-Item -LiteralPath (Join-Path $mainRoot 'dirty-main.txt') -Force -ErrorAction SilentlyContinue
    }
    $dirtyMainSummary = $dirtyMainOutput | ConvertFrom-Json
    if ($dirtyMainExitCode -eq 0 -or $dirtyMainSummary.failed_step -ne 'verify-main-clean') {
        throw "The ordinary closeout route did not reject a dirty main worktree."
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
            -ConfirmApplyMigrations `
            -ConfirmApprovedLegacyLocalSurfaceChanges 2>&1 | Out-String
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
