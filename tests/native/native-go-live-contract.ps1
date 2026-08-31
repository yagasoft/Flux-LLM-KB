[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$deploymentScript = Join-Path $SourceRoot 'scripts\deploy\update-native-windows.ps1'
$modulePath = Join-Path $SourceRoot 'scripts\deploy\native-go-live.psm1'
$closeoutPath = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$hostPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\GuardedNativeGoLiveHost.cs'
$portsPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLivePorts.cs'
$windowsPortsPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveWindowsHostPorts.cs'
$executorPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveExecutor.cs'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Pattern, [string]$Message)
    try { & $Action }
    catch {
        if ([string]$_.Exception.Message -notmatch $Pattern) {
            throw "$Message Unexpected failure: $($_.Exception.Message)"
        }
        return
    }
    throw $Message
}

function Import-CloseoutFunction {
    param(
        [Parameter(Mandatory)][System.Management.Automation.Language.Ast]$Ast,
        [Parameter(Mandatory)][string]$Name)

    $definition = $Ast.Find({
        param($candidate)
        $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $candidate.Name -ceq $Name
    }, $true)
    if ($null -eq $definition) { throw "Closeout function is missing: $Name" }
    $captured = & ([scriptblock]::Create(
        $definition.Extent.Text + "`n(Get-Item -LiteralPath 'Function:$Name').ScriptBlock"))
    Set-Item -LiteralPath "Function:script:$Name" -Value $captured
}

foreach ($path in @($deploymentScript, $modulePath, $hostPath, $portsPath, $windowsPortsPath, $executorPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required native go-live contract file is missing: $path"
}

$tokens = $null
$errors = $null
$closeoutAst = [System.Management.Automation.Language.Parser]::ParseFile($closeoutPath, [ref]$tokens, [ref]$errors)
Assert-True ($errors.Count -eq 0) 'Closeout script does not parse.'
$compositionFunction = $closeoutAst.Find({
    param($candidate)
    $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $candidate.Name -ceq 'Invoke-NativeGoLiveComposition'
}, $true)
Assert-True ($null -ne $compositionFunction) 'Native go-live composition function is missing.'
$compositionText = $compositionFunction.Extent.Text
Assert-True (-not $compositionText.Contains('Clear-NativeGoLiveBootstrapEnvironment', [StringComparison]::Ordinal)) `
    'Native go-live composition must leave bootstrap state for the guarded host to consume once.'
Import-CloseoutFunction -Ast $closeoutAst -Name 'Record-NativeGoLiveFailure'
Import-CloseoutFunction -Ast $closeoutAst -Name 'Clear-NativeGoLiveBootstrapEnvironment'
Import-CloseoutFunction -Ast $closeoutAst -Name 'Invoke-NativeGoLive'

$script:FailedStep = $null
$safeFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
Record-NativeGoLiveFailure -Record $safeFailureRecord -Exception ([InvalidOperationException]::new(
    "Native go-live failed with safe reason code 'clean-slate-incomplete'."))
Assert-True ($safeFailureRecord.reason_code -ceq 'clean-slate-incomplete') `
    'A safe native go-live result failure did not retain its exact reason code.'
Assert-True ($script:FailedStep -ceq 'native-go-live') `
    'A safe native go-live result failure did not identify the native go-live step.'

$script:FailedStep = $null
$admissionFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
Record-NativeGoLiveFailure -Record $admissionFailureRecord -Exception ([InvalidOperationException]::new(
    "Native go-live failed with safe reason code 'clean-slate-admission-failed'."))
Assert-True ($admissionFailureRecord.reason_code -ceq 'clean-slate-admission-failed') `
    'A safe native go-live admission failure did not retain its fixed reason code.'
Assert-True ($script:FailedStep -ceq 'native-go-live') `
    'A safe native go-live admission failure did not identify the native go-live step.'

$script:FailedStep = $null
$vssFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
Record-NativeGoLiveFailure -Record $vssFailureRecord -Exception ([InvalidOperationException]::new(
    "Native go-live failed with safe reason code 'vss-exact-action-not-proved'."))
Assert-True ($vssFailureRecord.reason_code -ceq 'vss-exact-action-not-proved') `
    'A safe native go-live VSS failure did not retain its existing fixed reason code.'
Assert-True ($script:FailedStep -ceq 'native-go-live') `
    'A safe native go-live VSS failure did not identify the native go-live step.'

$script:FailedStep = $null
$vssAddFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
Record-NativeGoLiveFailure -Record $vssAddFailureRecord -Exception ([InvalidOperationException]::new(
    "Native go-live failed with safe reason code 'vss-add-diff-area-failed'."))
Assert-True ($vssAddFailureRecord.reason_code -ceq 'vss-add-diff-area-failed') `
    'A safe native go-live VSS add failure did not retain its fixed reason code.'

$script:FailedStep = $null
$bootstrapFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
Record-NativeGoLiveFailure -Record $bootstrapFailureRecord -Exception ([InvalidOperationException]::new(
    "Native go-live failed with safe reason code 'native-go-live-bootstrap-install-sql-batch-1-failed'."))
Assert-True ($bootstrapFailureRecord.reason_code -ceq 'native-go-live-bootstrap-install-sql-batch-1-failed') `
    'A safe native go-live bootstrap failure did not retain its exact reason code.'
Assert-True ($script:FailedStep -ceq 'native-go-live') `
    'A safe native go-live bootstrap failure did not identify the native go-live step.'

foreach ($bridgeReasonCode in @(
    'native-go-live-bridge-composition-failed',
    'native-go-live-bridge-invocation-failed',
    'native-go-live-bridge-discovery-failed',
    'native-go-live-bridge-call-failed',
    'native-go-live-bridge-result-failed')) {
    $script:FailedStep = $null
    $bridgeFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
    Record-NativeGoLiveFailure -Record $bridgeFailureRecord -Exception ([InvalidOperationException]::new($bridgeReasonCode))
    Assert-True ($bridgeFailureRecord.reason_code -ceq $bridgeReasonCode) `
        'A fixed native go-live bridge failure did not retain its exact reason code.'
    Assert-True ($script:FailedStep -ceq 'native-go-live') `
        'A fixed native go-live bridge failure did not identify the native go-live step.'
}

foreach ($unsafeMessage in @(
    "Native go-live failed with safe reason code 'Server=localhost;Password=secret'.",
    'Native go-live failed with safe reason code clean-slate-incomplete.',
    'unrelated exception text')) {
    $script:FailedStep = $null
    $unsafeFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
    Record-NativeGoLiveFailure -Record $unsafeFailureRecord -Exception ([InvalidOperationException]::new($unsafeMessage))
    Assert-True ($null -eq $unsafeFailureRecord.reason_code) `
        'Malformed or unrelated native go-live exception text was surfaced as a reason code.'
    Assert-True ($script:FailedStep -ceq 'native-go-live') `
        'A native go-live failure did not identify the native go-live step.'
}

function Invoke-NativeGoLiveComposition {
    throw 'malformed-reflected-composition'
}

function Invoke-NativeGoLiveModuleBridge {
    throw 'module-bridge-should-not-run-after-composition-failure'
}

Assert-Throws -Action {
    Invoke-NativeGoLive -MergedMainRoot $SourceRoot -CommittedSha ('a' * 40) `
        -Acknowledgements @{ ConfirmCleanSlate = $true; ConfirmConfigureVss = $true; ConfirmDestroySql = $true; ConfirmRegisterCodex = $true } `
        -ModulePath $modulePath -BootstrapScript (Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql')
} -Pattern '^native-go-live-bridge-composition-failed$' `
    -Message 'Malformed reflection composition did not map to its fixed bridge failure code.'

function Invoke-NativeGoLiveComposition {
    return [pscustomobject]@{ Completed = $true }
}

function Invoke-NativeGoLiveModuleBridge {
    throw 'module-invocation-failure'
}

Assert-Throws -Action {
    Invoke-NativeGoLive -MergedMainRoot $SourceRoot -CommittedSha ('a' * 40) `
        -Acknowledgements @{ ConfirmCleanSlate = $true; ConfirmConfigureVss = $true; ConfirmDestroySql = $true; ConfirmRegisterCodex = $true } `
        -ModulePath $modulePath -BootstrapScript (Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql')
} -Pattern '^native-go-live-bridge-invocation-failed$' `
    -Message 'Module bridge invocation failure did not map to its fixed bridge failure code.'

foreach ($moduleStageReasonCode in @(
    'native-go-live-bridge-discovery-failed',
    'native-go-live-bridge-call-failed',
    'native-go-live-bridge-result-failed')) {
    function Invoke-NativeGoLiveModuleBridge {
        throw $moduleStageReasonCode
    }

    Assert-Throws -Action {
        Invoke-NativeGoLive -MergedMainRoot $SourceRoot -CommittedSha ('a' * 40) `
            -Acknowledgements @{ ConfirmCleanSlate = $true; ConfirmConfigureVss = $true; ConfirmDestroySql = $true; ConfirmRegisterCodex = $true } `
            -ModulePath $modulePath -BootstrapScript (Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql')
    } -Pattern ("^" + [regex]::Escape($moduleStageReasonCode) + "$") `
        -Message 'A fixed module bridge stage failure did not retain its exact reason code.'
}

function Invoke-NativeGoLiveModuleBridge {
    return [pscustomobject]@{ Succeeded = $false; ReasonCode = 'clean-slate-incomplete' }
}

Assert-Throws -Action {
    Invoke-NativeGoLive -MergedMainRoot $SourceRoot -CommittedSha ('a' * 40) `
        -Acknowledgements @{ ConfirmCleanSlate = $true; ConfirmConfigureVss = $true; ConfirmDestroySql = $true; ConfirmRegisterCodex = $true } `
        -ModulePath $modulePath -BootstrapScript (Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql')
} -Pattern "^Native go-live failed with safe reason code 'clean-slate-incomplete'\.$" `
    -Message 'Returned NativeGoLiveResult failures must remain outside bridge-invocation mapping.'

$planJson = & pwsh -NoProfile -File $deploymentScript -PlanOnly
Assert-True ($LASTEXITCODE -eq 0) 'PlanOnly failed.'
$plan = $planJson | ConvertFrom-Json
Assert-True ($plan.executionAvailable -eq $false) 'PlanOnly must not expose execution.'
Assert-True ($plan.root -eq 'I:\FluxKnowledge') 'PlanOnly must use the canonical root.'
Assert-True ($plan.siteName -eq 'FluxKnowledge' -and $plan.loopbackPort -eq 5137) 'PlanOnly must use canonical IIS.'
Assert-True ($plan.vss.volume -eq 'I:' -and $plan.vss.maximumStorageFraction -eq 0.10) 'PlanOnly VSS is not exact.'
Assert-True (@($plan.validation.mcpTools).Count -eq 9) 'PlanOnly must advertise the nine-tool MCP contract.'

Assert-Throws -Action {
    & $deploymentScript -GoLive -ConfirmCleanSlate -ConfirmConfigureVss -ConfirmDestroySql -ConfirmRegisterCodex
} -Pattern 'claimed in-process authority' -Message 'Direct -GoLive execution must be refused.'

$deploymentText = Get-Content -LiteralPath $deploymentScript -Raw
$closeoutText = Get-Content -LiteralPath $closeoutPath -Raw
$moduleText = Get-Content -LiteralPath $modulePath -Raw
$hostText = Get-Content -LiteralPath $hostPath -Raw
$portsText = Get-Content -LiteralPath $portsPath -Raw
$windowsPortsText = Get-Content -LiteralPath $windowsPortsPath -Raw
$executorText = Get-Content -LiteralPath $executorPath -Raw

Assert-True ($deploymentText -notmatch '(?i)vssadmin') 'The public boundary must not use vssadmin.'
Assert-True ($closeoutText -match '(?s)Assert-NativeGoLiveBootstrapConnection.*?Invoke-NativeGoLiveBootstrap' -and
    $closeoutText -match '(?s)\$bootstrapInstaller\s*=\s*\[System\.Func.*?Task\]::CompletedTask') `
    'The SQL bootstrap must complete synchronously before the CLR one-shot bridge.'
$nativeSniLoad = $closeoutText.IndexOf('Load-NativeGoLiveWindowsSqlClientNativeSniAsset', [StringComparison]::Ordinal)
$sqlClientImport = $closeoutText.IndexOf('Import-NativeGoLiveWindowsSqlClientAssembly', [StringComparison]::Ordinal)
Assert-True ($nativeSniLoad -ge 0 -and $sqlClientImport -gt $nativeSniLoad) `
    'The CLR bridge must load the exact native SQL client SNI asset before importing SqlClient.'
Assert-True ($moduleText -notmatch '(?i)vssadmin') 'The private lifecycle must not use vssadmin.'
Assert-True ($moduleText -notmatch 'HostOperations|\[hashtable\]|\[scriptblock\]') 'Caller-supplied host callbacks remain.'
Assert-True ($moduleText -notmatch 'DbConnectionStringBuilder') 'The PowerShell module must not parse SQL generically.'
Assert-True ($moduleText -match 'NativeGoLiveCloseoutBridge') 'The private module must enter the CLR closeout bridge.'
foreach ($moduleStageReasonCode in @(
    'native-go-live-bridge-discovery-failed',
    'native-go-live-bridge-call-failed',
    'native-go-live-bridge-result-failed')) {
    Assert-True ($moduleText -match [regex]::Escape($moduleStageReasonCode)) `
        'The private module is missing a fixed bridge-stage diagnostic code.'
}

$escapedModulePath = $modulePath.Replace("'", "''")
$moduleEnvironmentProbe = @"
`$env:FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP = 'sentinel'
`$module = Import-Module -Name '$escapedModulePath' -Force -PassThru
try {
    & `$module {
        Invoke-NativeGoLive -CapabilityIssuer ([pscustomobject]@{}) -Capability ([pscustomobject]@{}) -Request ([pscustomobject]@{}) -NativeGoLiveHost ([pscustomobject]@{})
    }
} catch {
    [Console]::WriteLine(`$_.Exception.Message)
} finally {
    Remove-Module `$module -Force -ErrorAction SilentlyContinue
}
if (`$null -ne [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP', [EnvironmentVariableTarget]::Process)) {
    [Console]::WriteLine('native-go-live-bootstrap-environment-retained')
}
"@
$moduleEnvironmentProbeOutput = & pwsh -NoProfile -Command $moduleEnvironmentProbe
Assert-True ($LASTEXITCODE -eq 0) 'The isolated module environment probe did not run.'
Assert-True ([string]::Join("`n", @($moduleEnvironmentProbeOutput)) -match '(?m)^native-go-live-bridge-discovery-failed$') `
    'The isolated module discovery failure did not use its fixed stage code.'
Assert-True ('native-go-live-bootstrap-environment-retained' -notin @($moduleEnvironmentProbeOutput)) `
    'A failed module discovery retained the bootstrap environment value.'

Assert-True ($hostText -match 'host is not GuardedNativeGoLiveHost') 'The CLR bridge must require the concrete guarded host.'
Assert-True ($hostText -match 'capability is not NativeGoLiveCloseoutCapability') 'The CLR bridge must require the opaque closeout capability.'

$module = Import-Module $modulePath -Force -PassThru
try {
    Assert-True ('Invoke-NativeGoLive' -notin @($module.ExportedCommands.Keys)) 'Invoke-NativeGoLive must remain private.'
    Assert-True ($null -eq (& $module { Get-Command New-NativeGoLiveTestRequest -ErrorAction SilentlyContinue })) 'A test bypass remains.'
    $integrationsAssembly = Join-Path $SourceRoot 'artifacts\bin\FluxKnowledge.Integrations\release\FluxKnowledge.Integrations.dll'
    if (-not (Test-Path -LiteralPath $integrationsAssembly -PathType Leaf)) {
        $buildOutput = & dotnet build (Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\FluxKnowledge.Integrations.csproj') `
            -c Release --no-restore 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $integrationsAssembly -PathType Leaf)) {
            throw "Unable to build the guarded CLR bridge required by this isolated test: $buildOutput"
        }
    }
    Add-Type -Path $integrationsAssembly
    Assert-Throws -Action {
        & $module {
            Invoke-NativeGoLive -CapabilityIssuer ([pscustomobject]@{}) -Capability ([pscustomobject]@{}) `
                -Request ([pscustomobject]@{}) -NativeGoLiveHost ([pscustomobject]@{})
        }
    } -Pattern 'go-live-closeout-capability-unrecognised' -Message 'Forged closeout values reached execution.'
}
finally {
    Remove-Module $module -Force -ErrorAction SilentlyContinue
}

Assert-True ($hostText -match 'SqlConnectionStringBuilder') 'Canonical SqlClient parsing is missing.'
Assert-True ($hostText -match 'NativeGoLivePayloadHasher\.Compute\(_mergedMainRoot\)') 'Merged-main hashing is missing.'
Assert-True ($hostText -match 'NativeGoLivePayloadHasher\.Compute\(_applicationRoot\)') 'Published payload hashing is missing.'
Assert-True ($hostText -notmatch 'NativeGoLiveJournal|NativeGoLiveRootMarker|NativeGoLiveRootAdmission|INativeGoLiveJournalSession') `
    'The guarded host still exposes deployment recovery state.'
Assert-True ($hostText -match 'NativeGoLiveLoopbackContract\.RequiredMcpTools') 'Exact MCP validation is missing.'
Assert-True ($windowsPortsText -match 'HASHBYTES\(''SHA2_256'',CONVERT\(varbinary\(max\),REPLACE\(sm\.definition,CHAR\(13\)\+CHAR\(10\),CHAR\(10\)\)\)\)') `
    'SQL bootstrap procedure evidence must normalise SQL Server CRLF definitions before hashing.'
Assert-True ($hostText -match 'ForwardedDenial') 'Forwarded denial validation is missing.'
Assert-True ($hostText -match 'NonLoopbackDenial') 'Non-loopback denial validation is missing.'
Assert-True ($hostText -match 'FfmpegEnabled' -and $hostText -match 'NetworkParsingEnabled') 'Runtime exclusions are incomplete.'
Assert-True ($portsText -notmatch 'NativeGoLiveJournal|RecoverAsync|ResumeAsync|ReplayAsync') `
    'The public native go-live host contract still exposes recovery operations.'
Assert-True ($executorText -notmatch 'ReadJournalAsync|CompareAndSwapJournalAsync|DestroyOwnedStateAsync') `
    'The one-shot executor still enters deployment recovery operations.'

Write-Output 'Native go-live contract passed.'
