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

foreach ($helper in @(
    'New-OutlookHostTaskTriggers',
    'Get-OutlookHostScheduledTask',
    'Register-OutlookHostTask',
    'Install-OutlookHostTask',
    'DisableAndDrain-OutlookHostTask',
    'Assert-OutlookHostTask'
)) {
    if ($deploymentText -notmatch ("(?m)^function\s+{0}\b" -f [regex]::Escape($helper))) {
        throw "The native deployment script must define $helper for the Outlook scheduled-task lifecycle."
    }
}

if ($deploymentText -notmatch 'New-ScheduledTaskPrincipal[\s\S]*?-LogonType\s+Interactive[\s\S]*?-RunLevel\s+Limited') {
    throw 'The Outlook task must use the limited interactive user token.'
}
if ($deploymentText -notmatch 'New-ScheduledTaskSettingsSet[\s\S]*?-Hidden[\s\S]*?-MultipleInstances\s+IgnoreNew' -or
    $deploymentText -notmatch 'ExecutionTimeLimit') {
    throw 'The Outlook task must be hidden, ignore overlaps and have a bounded execution limit.'
}

if ($deploymentText -notmatch '(?s)function\s+DisableAndDrain-OutlookHostTask.*?\$wasEnabled.*?catch\s*\{.*?if\s*\(\$wasEnabled\).*?Enable-ScheduledTask.*?throw') {
    throw 'A failed Outlook task drain must restore an enabled task before rethrowing.'
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
    $OutlookHostPayloadDirectory = 'outlook-host'
    $OutlookHostIntervalMinutes = 15
    $OutlookHostExecutionLimit = New-TimeSpan -Minutes 14
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Assert-OutlookHostTask')

    $deployRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'FluxKnowledge scheduler identity test'
    $launcher = Join-Path (Join-Path $deployRoot $OutlookHostPayloadDirectory) 'run-outlook-host.ps1'
    $powershell = Join-Path $PSHOME 'powershell.exe'
    $action = New-ScheduledTaskAction -Execute $powershell -Argument (
        '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $launcher)
    $principal = New-ScheduledTaskPrincipal `
        -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) `
        -LogonType Interactive `
        -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -Hidden -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit $OutlookHostExecutionLimit -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    $logon = New-ScheduledTaskTrigger -AtLogOn
    $repeat = New-ScheduledTaskTrigger -Once -At ([DateTime]::Now.AddMinutes(1)) `
        -RepetitionInterval (New-TimeSpan -Minutes $OutlookHostIntervalMinutes) `
        -RepetitionDuration (New-TimeSpan -Days 3650)
    $script:scheduledTaskFixture = [pscustomobject]@{
        Actions = @($action)
        Principal = $principal
        Settings = $settings
        Triggers = @($logon, $repeat)
    }
    function Get-ScheduledTask {
        param([string]$TaskName, [object]$ErrorAction)
        return $script:scheduledTaskFixture
    }

    Assert-OutlookHostTask -DeployRoot $deployRoot
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
    } catch {
        $failure = $_
    }
    if ($null -eq $failure -or
        $failure.Exception.Message -notmatch 'simulated scheduler query failure') {
        throw 'The Outlook task lookup must propagate scheduler query failures instead of treating the task as absent.'
    }
}

& {
    $OutlookHostTaskName = 'FluxKnowledge.OutlookHost'
    $OutlookHostPayloadDirectory = 'outlook-host'
    $OutlookHostIntervalMinutes = 15
    $OutlookHostExecutionLimit = New-TimeSpan -Minutes 14
    Invoke-Expression (Get-DeploymentFunctionText -Name 'New-OutlookHostTaskTriggers')
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Register-OutlookHostTask')
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Assert-OutlookHostTask')
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Install-OutlookHostTask')

    $tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $deployRoot = Join-Path $tempParent ("FluxKnowledge-scheduler-install-test-{0}" -f [Guid]::NewGuid().ToString('N'))
    $payloadRoot = Join-Path $deployRoot $OutlookHostPayloadDirectory
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $payloadRoot 'run-outlook-host.ps1') -Force | Out-Null
    try {
        $script:scheduledTaskFixture = $null
        $script:registeredEnabled = $null
        $script:enableCount = 0
        $script:disableCount = 0
        $script:disableShouldFail = $false
        $script:invalidateAfterEnable = $false
        function Register-ScheduledTask {
            param(
                [string]$TaskName,
                [object]$Action,
                [object[]]$Trigger,
                [object]$Principal,
                [object]$Settings,
                [string]$Description,
                [switch]$Force)
            $script:registeredEnabled = [bool]$Settings.Enabled
            $script:scheduledTaskFixture = [pscustomobject]@{
                Actions = @($Action)
                Principal = $Principal
                Settings = $Settings
                Triggers = @($Trigger)
            }
        }
        function Get-ScheduledTask {
            param([string]$TaskName, [object]$ErrorAction)
            if ($script:invalidateAfterEnable -and $script:enableCount -gt 0) {
                $script:scheduledTaskFixture.Principal = [pscustomobject]@{
                    UserId = 'invalid scheduler identity'
                    LogonType = 'Interactive'
                    RunLevel = 'Limited'
                }
            }
            return $script:scheduledTaskFixture
        }
        function Enable-ScheduledTask {
            param([string]$TaskName, [object]$ErrorAction)
            $script:enableCount++
            $script:scheduledTaskFixture.Settings = New-ScheduledTaskSettingsSet `
                -Hidden -MultipleInstances IgnoreNew -ExecutionTimeLimit $OutlookHostExecutionLimit `
                -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        }
        function Disable-ScheduledTask {
            param([string]$TaskName, [object]$ErrorAction)
            $script:disableCount++
            if ($script:disableShouldFail) {
                throw 'simulated disable failure'
            }
            $script:scheduledTaskFixture.Settings = New-ScheduledTaskSettingsSet `
                -Disable -Hidden -MultipleInstances IgnoreNew -ExecutionTimeLimit $OutlookHostExecutionLimit `
                -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        }

        Install-OutlookHostTask -DeployRoot $deployRoot
        if ($script:registeredEnabled -ne $false -or
            $script:enableCount -ne 1 -or
            $script:disableCount -ne 0 -or
            -not [bool]$script:scheduledTaskFixture.Settings.Enabled) {
            throw 'The Outlook task install must validate while disabled and enable exactly once after validation.'
        }

        $script:enableCount = 0
        $script:disableCount = 0
        $script:disableShouldFail = $true
        $script:invalidateAfterEnable = $true
        $failure = $null
        try {
            Install-OutlookHostTask -DeployRoot $deployRoot
        } catch {
            $failure = $_
        }
        if ($null -eq $failure -or
            $failure.Exception.Message -notmatch 'could not be left disabled' -or
            $script:disableCount -ne 1) {
            throw 'An invalid Outlook task whose disable operation fails must stop deployment with explicit fail-closed evidence.'
        }
    } finally {
        $resolvedDeployRoot = [System.IO.Path]::GetFullPath($deployRoot)
        if (-not $resolvedDeployRoot.StartsWith($tempParent, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedDeployRoot) -notmatch '^FluxKnowledge-scheduler-install-test-[0-9a-f]{32}$') {
            throw 'The scheduler install test temporary directory is outside its expected boundary.'
        }
        Remove-Item -LiteralPath $resolvedDeployRoot -Recurse -Force
    }
}

& {
    $OutlookHostTaskName = 'FluxKnowledge.OutlookHost'
    $OutlookHostPayloadDirectory = 'outlook-host'
    $OutlookHostIntervalMinutes = 15
    $OutlookHostExecutionLimit = New-TimeSpan -Minutes 14
    Invoke-Expression (Get-DeploymentFunctionText -Name 'New-OutlookHostTaskTriggers')
    Invoke-Expression (Get-DeploymentFunctionText -Name 'Register-OutlookHostTask')

    $tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $deployRoot = Join-Path $tempParent ("FluxKnowledge-scheduler-disabled-test-{0}" -f [Guid]::NewGuid().ToString('N'))
    $payloadRoot = Join-Path $deployRoot $OutlookHostPayloadDirectory
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $payloadRoot 'run-outlook-host.ps1') -Force | Out-Null
    try {
        $script:registeredTaskSettings = $null
        function Register-ScheduledTask {
            param(
                [string]$TaskName,
                [object]$Action,
                [object[]]$Trigger,
                [object]$Principal,
                [object]$Settings,
                [string]$Description,
                [switch]$Force)
            $script:registeredTaskSettings = $Settings
        }

        Register-OutlookHostTask -DeployRoot $deployRoot
        if ($null -eq $script:registeredTaskSettings -or [bool]$script:registeredTaskSettings.Enabled) {
            throw 'The Outlook scheduled task must be registered disabled until its policy has been validated.'
        }
    } finally {
        $resolvedDeployRoot = [System.IO.Path]::GetFullPath($deployRoot)
        if (-not $resolvedDeployRoot.StartsWith($tempParent, [System.StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolvedDeployRoot) -notmatch '^FluxKnowledge-scheduler-disabled-test-[0-9a-f]{32}$') {
            throw 'The scheduler test temporary directory is outside its expected boundary.'
        }
        Remove-Item -LiteralPath $resolvedDeployRoot -Recurse -Force
    }
}
