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

function Invoke-CloseoutChild {
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string]$FeatureRoot,
        [Parameter(Mandatory)][string]$MainRoot,
        [string[]]$Arguments = @(),
        [hashtable]$Environment = @{})

    $saved = @{}
    foreach ($entry in $Environment.GetEnumerator()) {
        $saved[[string]$entry.Key] = [Environment]::GetEnvironmentVariable(
            [string]$entry.Key,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            [string]$entry.Key,
            [string]$entry.Value,
            [EnvironmentVariableTarget]::Process)
    }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $commandArguments = @(
            '-NoProfile', '-File', $Script,
            '-FeatureWorktree', $FeatureRoot,
            '-MainRoot', $MainRoot) + $Arguments
        $output = & pwsh @commandArguments 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    } finally {
        $ErrorActionPreference = $previousPreference
        foreach ($entry in $saved.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable(
                [string]$entry.Key,
                $entry.Value,
                [EnvironmentVariableTarget]::Process)
        }
    }
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeoutScript = Join-Path $SourceRoot "scripts\dev\complete-feature.ps1"
$developerEntrypoint = Join-Path $SourceRoot "scripts\dev\update-native-windows.ps1"
$goLiveModule = Join-Path $SourceRoot "scripts\deploy\native-go-live.psm1"
foreach ($path in @($closeoutScript, $developerEntrypoint, $goLiveModule)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required closeout file is missing: $path"
}

$text = Get-Content -LiteralPath $closeoutScript -Raw
Assert-True ($text -match '\[switch\]\$GoLive') 'Closeout must require GoLive.'
Assert-True ($text -match 'Invoke-NativeGoLive') 'GoLive must be in-process, not Invoke-FeatureStep.'
Assert-True ($text.IndexOf('Invoke-NativeGoLive') -lt $text.IndexOf('push-main')) 'GoLive must precede push.'
Assert-False ($text -match 'BackupRoot') 'No backup-root contract may remain.'
Assert-False ($text -match 'post-deploy-validation-record-commit') 'Live evidence must not create a second commit.'
foreach ($switch in @('ConfirmCleanSlate', 'ConfirmConfigureVss', 'ConfirmDestroySql', 'ConfirmRegisterCodex', 'ConfirmRemoveLegacyPlugin')) {
    Assert-True ($text -match ("\[switch\]\`$$switch\b")) "Closeout must require $switch."
}
Assert-True (([regex]::Matches($text, 'git push origin main')).Count -eq 1) 'Closeout must have one final main push.'
Assert-False ($text -match 'Invoke-FeatureStep\s+-Name\s+["'']native-go-live["'']') 'GoLive must not use the child step runner.'
Assert-False ($text -match 'FLUXKNOWLEDGE_CLOSEOUT_(?:DEPLOY|BACKUP|APPLY_MIGRATIONS)') 'Obsolete deployment transport remains.'
Assert-True ($text -match '\$bootstrapEnvironmentName\s*=\s*["'']FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP["'']' -and
    $text -match 'EnvironmentVariables\.Remove\(\s*\$bootstrapEnvironmentName\s*\)') `
    'Closeout child processes must explicitly strip the native SQL bootstrap environment.'
Assert-True ($text -match 'bootstrap must not be visible to a closeout child process') `
    'Closeout child scripts must fail closed if the bootstrap reaches their process environment.'
Assert-False ($text -match 'nativeGoLiveSqlBootstrap|ExistingJournal|RecoverAsync') `
    'The closeout script must not reconstruct a bootstrap, execution, or authority from closeout.json.'
Assert-True ($text -match 'Clear-NativeGoLiveBootstrapEnvironment' -and
    $text -match 'finally\s*\{\s*Clear-NativeGoLiveBootstrapEnvironment') `
    'The direct host bridge must clear bootstrap data after the guarded host consumes it.'
Assert-False ($text -match 'validate-native-worker-supervision|validate-native-outlook-ingress|post-deploy') 'Obsolete worker/Outlook deployment validation remains.'
Assert-False ($text -match '(?m)^\s*Invoke-FeatureStep\b.*(?:python|docker|rabbitmq|vespa)') 'The native closeout invokes a legacy runtime command.'

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $closeoutScript,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
Assert-True ($parseErrors.Count -eq 0) 'The native closeout script does not parse.'

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeCloseout-$([Guid]::NewGuid().ToString('N'))"
$mainRoot = Join-Path $temporaryRoot "main"
$featureRoot = Join-Path $temporaryRoot "feature"

try {
    New-Item -ItemType Directory -Path $mainRoot | Out-Null
    & git init --initial-branch main $mainRoot | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Unable to create temporary main worktree.'
    & git -C $mainRoot config user.email "native-closeout@example.invalid"
    & git -C $mainRoot config user.name "Native Closeout Test"
    Set-Content -LiteralPath (Join-Path $mainRoot ".gitignore") -Value ".agents/"
    Set-Content -LiteralPath (Join-Path $mainRoot "README.md") -Value "temporary native closeout contract repository"
    & git -C $mainRoot add .
    & git -C $mainRoot commit -m "initial" | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Unable to commit temporary main worktree.'
    & git -C $mainRoot worktree add -b "codex/native-closeout-contract" $featureRoot | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Unable to create temporary feature worktree.'

    $incomplete = Invoke-CloseoutChild -Script $closeoutScript -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @('-DryRun', '-GoLive', '-ConfirmCleanSlate')
    Assert-True ($incomplete.ExitCode -ne 0 -and
        $incomplete.Output -match '-GoLive requires -ConfirmCleanSlate, -ConfirmConfigureVss, -ConfirmDestroySql, -ConfirmRegisterCodex' -and
        $incomplete.Output -match '-ConfirmRemoveLegacyPlugin') `
        'Every acknowledgement is required.'

    $orphanedConfirmation = Invoke-CloseoutChild -Script $closeoutScript -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @('-DryRun', '-ConfirmConfigureVss')
    Assert-True ($orphanedConfirmation.ExitCode -ne 0 -and
        $orphanedConfirmation.Output -match 'acknowledgement switches require -GoLive') `
        'An acknowledgement without GoLive was accepted.'

    $missingBootstrap = Invoke-CloseoutChild -Script $closeoutScript -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @(
            '-GoLive', '-ConfirmCleanSlate', '-ConfirmConfigureVss',
            '-ConfirmDestroySql', '-ConfirmRegisterCodex', '-ConfirmRemoveLegacyPlugin') `
        -Environment @{ FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP = $null }
    $missingBootstrapSummary = $missingBootstrap.Output | ConvertFrom-Json
    Assert-True ($missingBootstrap.ExitCode -ne 0 -and
        -not $missingBootstrapSummary.ok -and
        @($missingBootstrapSummary.steps).Count -eq 0) `
        'A missing non-dry GoLive bootstrap environment did not fail before any closeout step.'

    $ordinary = Invoke-CloseoutChild -Script $closeoutScript -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @('-DryRun')
    Assert-True ($ordinary.ExitCode -eq 0) "The native closeout dry-run failed: $($ordinary.Output)"
    $ordinarySummary = $ordinary.Output | ConvertFrom-Json
    Assert-True ($ordinarySummary.ok) 'The native closeout dry-run did not report success.'
    $ordinarySteps = @($ordinarySummary.steps | ForEach-Object { $_.name })
    $expectedOrdinarySteps = @(
        'verify-main-clean',
        'dotnet-tool-restore',
        'dotnet-restore-locked',
        'dotnet-build-release',
        'dotnet-test-native',
        'native-closeout-contract',
        'native-go-live-bootstrap-nondryrun-contract',
        'native-go-live-contract',
        'native-go-live-one-shot-admission-contract',
        'native-go-live-recovery-removal-contract',
        'native-deployment-contract',
        'feature-commit',
        'sync-main',
        'squash-merge',
        'dotnet-tool-restore-main',
        'dotnet-restore-locked-main',
        'dotnet-build-release-main',
        'dotnet-test-native-main',
        'main-commit',
        'push-main',
        'cleanup-worktree')
    Assert-True (($ordinarySteps -join '|') -ceq ($expectedOrdinarySteps -join '|')) `
        "The ordinary closeout sequence is unexpected: $($ordinarySteps -join ', ')."

    $developer = Invoke-CloseoutChild -Script $developerEntrypoint -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @('-DryRun')
    $developerSummary = $developer.Output | ConvertFrom-Json
    Assert-True ($developer.ExitCode -eq 0 -and $developerSummary.ok -and
        @($developerSummary.steps | ForEach-Object { $_.name }) -contains 'native-go-live-contract') `
        'The native Windows developer entrypoint did not delegate to the guarded closeout path.'

    $developerGoLive = Invoke-CloseoutChild -Script $developerEntrypoint -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @(
            '-DryRun', '-GoLive', '-ConfirmCleanSlate', '-ConfirmConfigureVss',
            '-ConfirmDestroySql', '-ConfirmRegisterCodex', '-ConfirmRemoveLegacyPlugin')
    $developerGoLiveSummary = $developerGoLive.Output | ConvertFrom-Json
    Assert-True ($developerGoLive.ExitCode -eq 0 -and $developerGoLiveSummary.ok -and
        @($developerGoLiveSummary.steps | ForEach-Object { $_.name }) -contains 'native-go-live') `
        'The developer GoLive wrapper did not forward ConfirmRemoveLegacyPlugin to the guarded closeout.'

    Set-Content -LiteralPath (Join-Path $mainRoot 'dirty-main.txt') -Value 'ordinary closeout must reject dirty main'
    try {
        $dirty = Invoke-CloseoutChild -Script $closeoutScript -FeatureRoot $featureRoot -MainRoot $mainRoot `
            -Arguments @('-DryRun')
    } finally {
        Remove-Item -LiteralPath (Join-Path $mainRoot 'dirty-main.txt') -Force -ErrorAction SilentlyContinue
    }
    $dirtySummary = $dirty.Output | ConvertFrom-Json
    Assert-True ($dirty.ExitCode -ne 0 -and $dirtySummary.failed_step -ceq 'verify-main-clean') `
        'The ordinary closeout route did not reject a dirty main worktree.'

    $goLive = Invoke-CloseoutChild -Script $closeoutScript -FeatureRoot $featureRoot -MainRoot $mainRoot `
        -Arguments @(
            '-DryRun', '-GoLive', '-ConfirmCleanSlate', '-ConfirmConfigureVss',
            '-ConfirmDestroySql', '-ConfirmRegisterCodex', '-ConfirmRemoveLegacyPlugin') `
        -Environment @{ FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP = 'must-not-be-read-in-dry-run' }
    Assert-True ($goLive.ExitCode -eq 0) "The acknowledged go-live dry-run failed: $($goLive.Output)"
    $goLiveSummary = $goLive.Output | ConvertFrom-Json
    $goLiveSteps = @($goLiveSummary.steps | ForEach-Object { $_.name })
    $mainCommitIndex = [Array]::IndexOf($goLiveSteps, 'main-commit')
    $publishIndex = [Array]::IndexOf($goLiveSteps, 'publish-merged-main')
    $bootstrapIndex = [Array]::IndexOf($goLiveSteps, 'native-go-live-bootstrap')
    $goLiveIndex = [Array]::IndexOf($goLiveSteps, 'native-go-live')
    $pushIndex = [Array]::IndexOf($goLiveSteps, 'push-main')
    Assert-True ($mainCommitIndex -ge 0 -and $publishIndex -gt $mainCommitIndex -and
        $goLiveIndex -gt $publishIndex -and $bootstrapIndex -gt $goLiveIndex -and $pushIndex -gt $bootstrapIndex) `
        'GoLive does not enter the guarded host before bootstrapping its named SQL prerequisites.'
    Assert-True ([bool]$goLiveSummary.steps[$goLiveIndex].skipped) 'DryRun attempted the native go-live host.'
    Assert-False ($goLive.Output.Contains('must-not-be-read-in-dry-run', [StringComparison]::Ordinal)) `
        'DryRun exposed the native SQL bootstrap environment value.'

    Write-Output "Native closeout dry-run contract passed."
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
