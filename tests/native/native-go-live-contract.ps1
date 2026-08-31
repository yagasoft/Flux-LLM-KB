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

foreach ($path in @($deploymentScript, $modulePath, $hostPath, $portsPath, $executorPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required native go-live contract file is missing: $path"
}

$tokens = $null
$errors = $null
$closeoutAst = [System.Management.Automation.Language.Parser]::ParseFile($closeoutPath, [ref]$tokens, [ref]$errors)
Assert-True ($errors.Count -eq 0) 'Closeout script does not parse.'
Import-CloseoutFunction -Ast $closeoutAst -Name 'Record-NativeGoLiveFailure'

$script:FailedStep = $null
$safeFailureRecord = [ordered]@{ name = 'native-go-live'; reason_code = $null }
Record-NativeGoLiveFailure -Record $safeFailureRecord -Exception ([InvalidOperationException]::new(
    "Native go-live failed with safe reason code 'clean-slate-incomplete'."))
Assert-True ($safeFailureRecord.reason_code -ceq 'clean-slate-incomplete') `
    'A safe native go-live result failure did not retain its exact reason code.'
Assert-True ($script:FailedStep -ceq 'native-go-live') `
    'A safe native go-live result failure did not identify the native go-live step.'

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
$moduleText = Get-Content -LiteralPath $modulePath -Raw
$hostText = Get-Content -LiteralPath $hostPath -Raw
$portsText = Get-Content -LiteralPath $portsPath -Raw
$executorText = Get-Content -LiteralPath $executorPath -Raw

Assert-True ($deploymentText -notmatch '(?i)vssadmin') 'The public boundary must not use vssadmin.'
Assert-True ($moduleText -notmatch '(?i)vssadmin') 'The private lifecycle must not use vssadmin.'
Assert-True ($moduleText -notmatch 'HostOperations|\[hashtable\]|\[scriptblock\]') 'Caller-supplied host callbacks remain.'
Assert-True ($moduleText -notmatch 'DbConnectionStringBuilder') 'The PowerShell module must not parse SQL generically.'
Assert-True ($moduleText -match 'NativeGoLiveCloseoutBridge') 'The private module must enter the CLR closeout bridge.'
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
Assert-True ($hostText -match 'ForwardedDenial') 'Forwarded denial validation is missing.'
Assert-True ($hostText -match 'NonLoopbackDenial') 'Non-loopback denial validation is missing.'
Assert-True ($hostText -match 'FfmpegEnabled' -and $hostText -match 'NetworkParsingEnabled') 'Runtime exclusions are incomplete.'
Assert-True ($portsText -notmatch 'NativeGoLiveJournal|RecoverAsync|ResumeAsync|ReplayAsync') `
    'The public native go-live host contract still exposes recovery operations.'
Assert-True ($executorText -notmatch 'ReadJournalAsync|CompareAndSwapJournalAsync|DestroyOwnedStateAsync') `
    'The one-shot executor still enters deployment recovery operations.'

Write-Output 'Native go-live contract passed.'
