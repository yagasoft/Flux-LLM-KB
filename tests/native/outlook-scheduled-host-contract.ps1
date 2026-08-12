[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $SourceRoot 'scripts\deploy\run-outlook-host.ps1'
$text = Get-Content -LiteralPath $launcher -Raw

if ($text -match '(?m)^\s*param\s*\(') {
    throw 'The Outlook launcher must not accept task arguments.'
}
if ($text -notmatch 'ConnectionStrings__FluxKnowledge' -or
    $text -notmatch '(?s)try\s*\{.*finally\s*\{') {
    throw 'The launcher must scope and clear the SQL connection value.'
}
if ($text -notmatch '--run-once' -or
    $text -match '--verbose-com-errors|spool|mailbox|credential|https?://') {
    throw 'The launcher action is not the fixed non-diagnostic local host invocation.'
}
if ($text -notmatch '\$PSScriptRoot' -or
    $text -notmatch 'appsettings\.Production\.json') {
    throw 'The launcher must resolve the local production settings from its installed directory.'
}

$deploymentScript = Join-Path $SourceRoot 'scripts\deploy\update-native-windows.ps1'
$deploymentText = Get-Content -LiteralPath $deploymentScript -Raw
foreach ($helper in @('Get-OutlookHostScheduledTask', 'DisableAndDrain-OutlookHostTask')) {
    if ($deploymentText -notmatch ("(?m)^function\s+{0}\b" -f [regex]::Escape($helper))) {
        throw "The native deployment script must define the fail-closed Outlook helper $helper."
    }
}
foreach ($forbiddenHelper in @(
    'New-OutlookHostTaskTriggers', 'Register-OutlookHostTask',
    'Install-OutlookHostTask', 'Assert-OutlookHostTask')) {
    if ($deploymentText -match ("(?m)^function\s+{0}\b" -f [regex]::Escape($forbiddenHelper))) {
        throw "The native deployment script retains an Outlook activation helper: $forbiddenHelper"
    }
}
foreach ($forbiddenCommand in @(
    'New-ScheduledTaskTrigger', 'New-ScheduledTaskAction', 'New-ScheduledTaskPrincipal',
    'New-ScheduledTaskSettingsSet', 'Register-ScheduledTask', 'Enable-ScheduledTask',
    'Start-ScheduledTask')) {
    if ($deploymentText -match ("\b{0}\b" -f [regex]::Escape($forbiddenCommand))) {
        throw "The native deployment script retains an Outlook activation command: $forbiddenCommand"
    }
}
if ($deploymentText -notmatch '\[switch\]\$KeepOutlookHostDisabled\s*=\s*\$true' -or
    $deploymentText -notmatch 'if\s*\(\s*-not\s+\$KeepOutlookHostDisabled\s*\)\s*\{\s*throw\s+"Outlook host activation is not authorised') {
    throw 'The native deployment script does not default to and enforce Outlook-disabled mode.'
}

function Get-DeploymentFunctionText {
    param([Parameter(Mandatory = $true)][string]$Name)

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $deploymentScript,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw 'The native deployment script could not be parsed for behavioural testing.'
    }

    $definition = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq $Name
    }, $true)
    if ($null -eq $definition) {
        throw "The native deployment script does not define $Name."
    }

    return $definition.Extent.Text
}

& {
    $OutlookHostTaskName = 'FluxKnowledge.OutlookHost'
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Get-OutlookHostScheduledTask')

    function Get-ScheduledTask {
        param([string]$TaskPath, [object]$ErrorAction)
        if ([string]$ErrorAction -ne 'Stop') {
            throw 'The Outlook task query did not request terminating errors.'
        }
        throw 'simulated scheduler query failure'
    }

    $failure = $null
    try {
        Get-OutlookHostScheduledTask | Out-Null
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure -or
        $failure.Exception.Message -notmatch 'simulated scheduler query failure') {
        throw 'The Outlook task lookup must propagate scheduler query failures instead of treating the task as absent.'
    }
}

& {
    $OutlookHostTaskName = 'FluxKnowledge.OutlookHost'
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Get-OutlookHostScheduledTask')
    Invoke-Expression (Get-DeploymentFunctionText -Name 'DisableAndDrain-OutlookHostTask')

    $script:disableCount = 0
    $script:activationCount = 0
    $script:stateReads = 0
    function Get-ScheduledTask {
        param([string]$TaskPath, [string]$TaskName, [object]$ErrorAction)
        if (-not [string]::IsNullOrEmpty($TaskPath)) {
            return [pscustomobject]@{
                TaskName = $OutlookHostTaskName
                Settings = [pscustomobject]@{ Enabled = $true }
            }
        }
        $script:stateReads++
        return [pscustomobject]@{ State = if ($script:stateReads -eq 1) { 'Running' } else { 'Ready' } }
    }
    function Disable-ScheduledTask {
        param([string]$TaskName, [object]$ErrorAction)
        $script:disableCount++
    }
    function Enable-ScheduledTask { $script:activationCount++; throw 'activation must not run' }
    function Start-ScheduledTask { $script:activationCount++; throw 'activation must not run' }
    function Register-ScheduledTask { $script:activationCount++; throw 'activation must not run' }
    function Start-Sleep { param([int]$Seconds) }

    $wasEnabled = DisableAndDrain-OutlookHostTask
    if (-not $wasEnabled -or $script:disableCount -ne 1 -or $script:activationCount -ne 0 -or $script:stateReads -ne 2) {
        throw 'The Outlook drain must disable once, wait until quiescent, and never activate the task.'
    }
}

& {
    $OutlookHostTaskName = 'FluxKnowledge.OutlookHost'
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Get-OutlookHostScheduledTask')
    Invoke-Expression (Get-DeploymentFunctionText -Name 'DisableAndDrain-OutlookHostTask')

    $script:activationCount = 0
    function Get-ScheduledTask {
        param([string]$TaskPath, [string]$TaskName, [object]$ErrorAction)
        return [pscustomobject]@{
            TaskName = $OutlookHostTaskName
            Settings = [pscustomobject]@{ Enabled = $true }
        }
    }
    function Disable-ScheduledTask { throw 'simulated disable failure' }
    function Enable-ScheduledTask { $script:activationCount++; throw 'activation must not run' }
    function Start-ScheduledTask { $script:activationCount++; throw 'activation must not run' }
    function Register-ScheduledTask { $script:activationCount++; throw 'activation must not run' }

    $failure = $null
    try {
        DisableAndDrain-OutlookHostTask | Out-Null
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure -or
        $failure.Exception.Message -notmatch 'simulated disable failure' -or
        $script:activationCount -ne 0) {
        throw 'A failed Outlook drain must propagate without activating or restoring the task.'
    }
}

Write-Output 'Outlook scheduled host disabled-only contract passed.'
