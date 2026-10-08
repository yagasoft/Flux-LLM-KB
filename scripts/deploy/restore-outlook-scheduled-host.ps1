[CmdletBinding()]
param(
    [string]$BindingPath='',
    [string]$ReceiptPath='',
    [switch]$StartOnce,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
if($Apply -and $PSVersionTable.PSVersion.Major -lt 7){throw 'outlook-restoration-apply-requires-pwsh7'}

function Initialize-OutlookHostRestorationHelpers([string]$DeployScriptRoot) {
    # Reuse only the identity/path/hash helpers. Importing the whole migration
    # module would bootstrap an unrelated SQL reader requiring the newer runtime.
    $sources=[ordered]@{
        'incremental-repository-recovery-migration.psm1'=@('ConvertTo-RepositoryRecoveryCanonicalValue','Get-RepositoryRecoveryValueHash')
        'incremental-repository-recovery-release.psm1'=@('Assert-RepositoryRecoveryReceiptPath')
        'incremental-hybrid-passage-rebuild.psm1'=@('Get-HybridPayloadFingerprint')
        'update-native-iis-incremental.ps1'=@('Assert-NotReparsePoint','Get-RepositoryRecoveryTaskObservation')
    }
    foreach($file in $sources.Keys) {
        $tokens=$null;$errors=$null
        $source=[Management.Automation.Language.Parser]::ParseFile((Join-Path $DeployScriptRoot $file),[ref]$tokens,[ref]$errors)
        if($errors.Count){throw 'outlook-restoration-updater-unavailable'}
        foreach($name in $sources[$file]) {
            $definitions=@($source.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
            if($definitions.Count -ne 1){throw 'outlook-restoration-updater-identity-helper-missing'}
            Invoke-Expression ($definitions[0].Extent.Text -replace '^function\s+', 'function script:')
        }
    }
}

function Read-OutlookHostRestorationBinding([string]$Path) {
    $document=Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json
    $result=[ordered]@{}
    foreach($name in @('IdentityHash','LauncherSha256','RunnerSha256','PayloadSha256','SessionId')) {
        if($null -eq $document.PSObject.Properties[$name]){throw 'outlook-restoration-binding-incomplete'}
        $result[$name]=$document.$name
    }
    return $result
}

function Get-OutlookHostRestorationObservation {
    $task=Get-ScheduledTask -TaskName 'FluxKnowledge.OutlookHost' -TaskPath '\' -ErrorAction Stop
    $observation=Get-RepositoryRecoveryTaskObservation
    $runner='C:\inetpub\FluxKnowledge\outlook-host\run-outlook-host.ps1'
    $hostPath='C:\inetpub\FluxKnowledge\outlook-host\FluxKnowledge.OutlookHost.exe'
    foreach($path in @($observation.LauncherPath,$runner,$hostPath)) {
        Assert-NotReparsePoint -Path $path -Message 'outlook-restoration-path-unsafe'
        for($parent=[IO.DirectoryInfo](Split-Path -Parent $path);$null -ne $parent;$parent=$parent.Parent) {
            if($parent.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'outlook-restoration-parent-path-unsafe'}
        }
    }
    $sessionId=(Get-Process -Id $PID).SessionId
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    $taskSid=if($task.Principal.UserId -match '^S-1-'){$task.Principal.UserId}
        else{([Security.Principal.NTAccount]$task.Principal.UserId).Translate([Security.Principal.SecurityIdentifier]).Value}
    $interactive=$sessionId -gt 0 -and $identity.User.Value -ceq $taskSid -and
        @((Get-Process -Name explorer -ErrorAction SilentlyContinue) | Where-Object SessionId -EQ $sessionId).Count -gt 0 -and
        [string]$task.Principal.LogonType -ceq 'Interactive' -and [string]$task.Principal.RunLevel -ceq 'Limited'
    $hosts=@(Get-CimInstance Win32_Process -Filter "Name = 'FluxKnowledge.OutlookHost.exe'" -ErrorAction Stop)
    foreach($hostProcess in $hosts) {
        if($hostProcess.ExecutablePath -ine $hostPath -or $hostProcess.SessionId -ne $sessionId -or
            (Get-Process -Id $hostProcess.ProcessId -ErrorAction Stop).MainWindowHandle -ne 0){throw 'outlook-restoration-unexpected-host'}
    }
    $observation.RunnerSha256=(Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash
    $observation.PayloadSha256=Get-HybridPayloadFingerprint (Split-Path -Parent $runner)
    $observation.SessionId=$sessionId
    $observation.Interactive=$interactive
    $observation.IgnoreNew=[string]$task.Settings.MultipleInstances -ceq 'IgnoreNew'
    $observation.Hosts=$hosts.Count
    return $observation
}

function Get-OutlookHostRestorationReceipt($Path) {
    if(Test-Path -LiteralPath $Path) {
        Assert-RepositoryRecoveryReceiptPath $Path
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    return $null
}

function Save-OutlookHostRestorationReceipt($Path,$Receipt) {
    $absolute=[IO.Path]::GetFullPath($Path)
    if(!$absolute.StartsWith('I:\FluxKnowledge\Recovery\IncrementalUpdates\',[StringComparison]::OrdinalIgnoreCase)){
        throw 'outlook-restoration-receipt-outside-existing-recovery-root'
    }
    Assert-RepositoryRecoveryReceiptPath $absolute
    if(!(Test-Path -LiteralPath (Split-Path -Parent $absolute) -PathType Container)){throw 'outlook-restoration-release-root-missing'}
    $temporary=$absolute+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    [IO.File]::WriteAllText($temporary,($Receipt | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $absolute -Force -ErrorAction Stop
}

function Invoke-OutlookHostRestoration {
    param([Parameter(Mandatory)]$Binding,[Parameter(Mandatory)][string]$ReceiptPath,[switch]$StartOnce)
    $before=Get-OutlookHostRestorationObservation
    foreach($name in @('IdentityHash','LauncherSha256','RunnerSha256','PayloadSha256','SessionId')){
        if($before[$name] -cne $Binding[$name]){throw ('outlook-restoration-binding-drift: '+$name)}
    }
    if(!$before.Interactive -or !$before.IgnoreNew -or $before.Hosts -gt 1){throw 'outlook-restoration-session-or-singleton-unavailable'}
    $saved=Get-OutlookHostRestorationReceipt $ReceiptPath
    if($saved){
        if($saved.BindingHash -cne (Get-RepositoryRecoveryValueHash $Binding) -or $saved.StartOnce -ne [bool]$StartOnce){throw 'outlook-restoration-receipt-conflict'}
        if($saved.State -in @('start-uncertain','starting')){throw 'outlook-restoration-prior-start-outcome-uncertain'}
        if($saved.State -in @('enabled','start-requested','existing-host') -and $before.Enabled){return $saved}
        throw 'outlook-restoration-receipt-requires-inspection'
    }
    $receipt=[ordered]@{Version=1;BindingHash=(Get-RepositoryRecoveryValueHash $Binding);StartOnce=[bool]$StartOnce;OriginalEnabled=[bool]$before.Enabled;State='enabling';UpdatedUtc=[DateTime]::UtcNow.ToString('O')}
    Save-OutlookHostRestorationReceipt $ReceiptPath $receipt
    $startAttempted=$false
    try {
        if(!$before.Enabled){Enable-ScheduledTask -TaskName 'FluxKnowledge.OutlookHost' -TaskPath '\' -ErrorAction Stop | Out-Null}
        $current=Get-OutlookHostRestorationObservation
        foreach($name in @('IdentityHash','LauncherSha256','RunnerSha256','PayloadSha256','SessionId')){
            if($current[$name] -cne $Binding[$name]){throw 'outlook-restoration-post-enable-drift'}
        }
        if(!$current.Enabled -or !$current.Interactive -or !$current.IgnoreNew -or $current.Hosts -gt 1){throw 'outlook-restoration-enable-unverified'}
        $receipt.State='enabled'
        if($StartOnce){
            if($current.Hosts -eq 1 -or $current.State -ceq 'Running'){$receipt.State='existing-host'}
            else {
                $receipt.State='starting'; Save-OutlookHostRestorationReceipt $ReceiptPath $receipt
                $startAttempted=$true
                Start-ScheduledTask -TaskName 'FluxKnowledge.OutlookHost' -TaskPath '\' -ErrorAction Stop
                $receipt.State='start-requested'
            }
        }
        Save-OutlookHostRestorationReceipt $ReceiptPath $receipt
        return $receipt
    } catch {
        $failure=$_
        $receipt.State=if($startAttempted){'start-uncertain'}else{'enable-failed'}
        $current=Get-OutlookHostRestorationObservation
        if($current.IdentityHash -ceq $Binding.IdentityHash -and !$before.Enabled){
            Disable-ScheduledTask -TaskName 'FluxKnowledge.OutlookHost' -TaskPath '\' -ErrorAction Stop | Out-Null
            if((Get-OutlookHostRestorationObservation).Enabled){throw 'outlook-restoration-disabled-rollback-unverified'}
        }
        Save-OutlookHostRestorationReceipt $ReceiptPath $receipt
        throw $failure
    }
}

Initialize-OutlookHostRestorationHelpers $PSScriptRoot
$InteractiveHostTaskName='FluxKnowledge.OutlookHost'
if(!$Apply){Get-OutlookHostRestorationObservation | ConvertTo-Json -Depth 8;return}
if(!$BindingPath -or !$ReceiptPath){throw 'Apply requires the reviewed binding and release receipt path.'}
$binding=Read-OutlookHostRestorationBinding $BindingPath
Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath $ReceiptPath -StartOnce:$StartOnce | ConvertTo-Json -Depth 8
