param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/restore-outlook-scheduled-host.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Restoration operator must parse.'}
if($PSVersionTable.PSVersion.Major -lt 7) {
    $refusal=$null
    try { & (Join-Path $SourceRoot 'scripts/deploy/restore-outlook-scheduled-host.ps1') -Apply -BindingPath 'must-not-read' -ReceiptPath 'must-not-write' }
    catch { $refusal=$_.Exception.Message }
    if($refusal -cne 'outlook-restoration-apply-requires-pwsh7'){throw '5.1 Apply did not refuse before helper initialisation and native I/O.'}
}
$initialiser=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Initialize-OutlookHostRestorationHelpers'},$true))
if($initialiser.Count -ne 1){throw 'Actual helper initialiser is missing.'}
Invoke-Expression $initialiser[0].Extent.Text
Initialize-OutlookHostRestorationHelpers (Join-Path $SourceRoot 'scripts/deploy')
$hash=Get-RepositoryRecoveryValueHash ([ordered]@{z=1;a='x'})
if($hash -cne '8D6A75AC86D8B51BB56ACFBB96108ED81474AA3504C317F77C0C576BDE387CD3' -or
    $hash -cne (Get-RepositoryRecoveryValueHash ([ordered]@{a='x';z=1}))){throw 'Imported canonical hash changed.'}
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('flux-outlook-binding-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $fixture 'probe.txt'),'abc')
    $payloadHash=Get-HybridPayloadFingerprint $fixture
    if($payloadHash -cne '82CC8F9905D9D97EC87B7B42FE533D98CF2F3FE3185374C476368A2488F27752'){throw 'Imported payload fingerprint changed.'}
    $BindingPath=Join-Path $fixture 'binding.json'
    [IO.File]::WriteAllText($BindingPath,'{"IdentityHash":"task","LauncherSha256":"launcher","RunnerSha256":"runner","PayloadSha256":"payload","SessionId":1}')
    $loader=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Read-OutlookHostRestorationBinding'},$true))
    foreach($item in $loader){Invoke-Expression $item.Extent.Text}
    $assignment=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$binding'},$true))
    if($assignment.Count -ne 1){throw 'Actual entry-point binding assignment is missing.'}
    Invoke-Expression $assignment[0].Extent.Text
    if($binding.IdentityHash -cne 'task' -or $binding['SessionId'] -ne 1){throw 'Actual binding loading lost the reviewed values.'}
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if(!(Split-Path -Leaf $resolved).StartsWith('flux-outlook-binding-') -or !$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
$definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-OutlookHostRestoration'},$true))
if($definition.Count -ne 1){throw 'Missing focused task-restoration operation.'}
Invoke-Expression $definition[0].Extent.Text
$script:observation=@{IdentityHash='task';LauncherSha256='launcher';RunnerSha256='runner';PayloadSha256='payload';SessionId=1;Enabled=$false;State='Disabled';Interactive=$true;IgnoreNew=$true;Hosts=0}
$script:receipt=$null; $script:enables=0; $script:starts=0; $script:failEnable=$false; $script:failStart=$false
function Get-OutlookHostRestorationObservation { $script:observation.Clone() }
function Get-OutlookHostRestorationReceipt { $script:receipt }
function Save-OutlookHostRestorationReceipt($Path,$Receipt) { $script:receipt=$Receipt }
function Get-RepositoryRecoveryValueHash($Value) { $Value | ConvertTo-Json -Depth 8 -Compress }
function Enable-ScheduledTask { $script:enables++; $script:observation.Enabled=$true; $script:observation.State='Ready'; if($script:failEnable){throw 'enable acknowledgement lost'} }
function Disable-ScheduledTask { $script:observation.Enabled=$false; $script:observation.State='Disabled' }
function Start-ScheduledTask { $script:starts++; if($script:failStart){throw 'start acknowledgement lost'} }
function Assert-Refused([scriptblock]$Action){$refused=$false;try{& $Action | Out-Null}catch{$refused=$true};if(!$refused){throw 'Unsafe restoration was accepted.'}}
$binding=$script:observation.Clone()
foreach($change in @('IdentityHash','LauncherSha256','RunnerSha256','PayloadSha256','SessionId','Interactive','IgnoreNew')){
 $saved=$script:observation[$change];$script:observation[$change]=if($saved -is [bool]){!$saved}elseif($saved -is [int]){2}else{'changed'}
 Assert-Refused {Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce}
 $script:observation[$change]=$saved
}
$script:observation.Hosts=2
Assert-Refused {Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce}
$script:observation.Hosts=0
if($script:enables -or $script:starts){throw 'Refusal mutated the task.'}
$script:failEnable=$true
Assert-Refused {Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake'}
if($script:observation.Enabled){throw 'Lost enable acknowledgement did not restore the original disabled state.'}
$script:failEnable=$false; $script:receipt=$null
$result=Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce
if(!$script:observation.Enabled -or $script:starts -ne 1 -or $result.State -cne 'start-requested'){throw 'Explicit restoration did not enable and request one hidden task start.'}
Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce | Out-Null
if($script:starts -ne 1){throw 'Receipt replay launched duplicate work.'}
$script:receipt=$null;$script:observation.Enabled=$false;$script:observation.Hosts=1
$result=Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce
if($script:starts -ne 1 -or $result.State -cne 'existing-host'){throw 'An existing singleton was launched again.'}
$script:receipt=$null;$script:observation.Enabled=$false;$script:observation.Hosts=0;$script:failStart=$true
Assert-Refused {Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce}
if($script:observation.Enabled -or $script:receipt.State -cne 'start-uncertain'){throw 'Lost start acknowledgement was hidden or left the task enabled.'}
$script:failStart=$false
Assert-Refused {Invoke-OutlookHostRestoration -Binding $binding -ReceiptPath 'fake' -StartOnce}
if($script:starts -ne 2){throw 'An uncertain start was replayed.'}
'Outlook restoration loader/control-flow checks passed: identity/session refusal, rollback, singleton and receipt replay. Operational bindings and Apply require the same pwsh7 runtime.'
