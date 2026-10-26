[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-helper-task.psm1') -Force
$script:passed=0
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message};$script:passed++}
function Expect-Refusal([scriptblock]$Action){$refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused 'An unsafe task snapshot was accepted.'}
$root='I:\FluxKnowledgeSourceAccess-Staging-task-contract';$task='FluxKnowledge.SourceAccess-Staging-task-contract'
$security='O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)'
$xml=@'
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task" version="1.4">
 <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
 <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
 <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><Enabled>true</Enabled><Hidden>true</Hidden></Settings>
 <Actions Context="Author"><Exec><Command>I:\FluxKnowledgeSourceAccess-Staging-task-contract\payload\FluxKnowledge.Cli.exe</Command><Arguments>source-access serve --broker-root "I:\FluxKnowledgeSourceAccess-Staging-task-contract"</Arguments><WorkingDirectory>I:\FluxKnowledgeSourceAccess-Staging-task-contract\payload</WorkingDirectory></Exec></Actions>
</Task>
'@
function Observe([string]$Definition=$xml,[string]$Descriptor=$security,[bool]$Enabled=$true,[Nullable[int]]$NativeLogonType=5){
 ConvertFrom-SourceJournalHelperTaskSnapshot -Xml $Definition -SecurityDescriptor $Descriptor -InstallRoot $root -TaskName $task -Enabled $Enabled -Running $false -NativeLogonType $NativeLogonType
}
$baseline=Observe
Assert-True ($baseline.PrincipalSid -ceq 'S-1-5-18' -and !$baseline.Running -and $baseline.Enabled) 'The task snapshot changed identity/state.'
$disabled=Observe -Definition $xml.Replace('<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><Enabled>true</Enabled>',
 '<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><Enabled>false</Enabled>') -Enabled $false
Assert-True ($disabled.DefinitionSha256 -ceq $baseline.DefinitionSha256 -and !$disabled.Enabled) 'Global Enabled is not the sole mutable definition field.'
$trigger=Observe -Definition $xml.Replace('<BootTrigger><Enabled>true</Enabled>','<BootTrigger><Enabled>false</Enabled>')
Assert-True ($trigger.DefinitionSha256 -cne $baseline.DefinitionSha256) 'Trigger enablement was removed from the immutable binding.'
$nativeXml=$xml.Replace('<LogonType>ServiceAccount</LogonType>','')
$nativeSecurity='O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;SY)'
$native=Observe -Definition $nativeXml -Descriptor $nativeSecurity
Assert-True ($native.PrincipalSid -ceq 'S-1-5-18') 'The actual SYSTEM scheduler representation was rejected.'
$nativeDisabled=Observe -Definition $nativeXml.Replace('<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><Enabled>true</Enabled>',
 '<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><Enabled>false</Enabled>') -Descriptor $nativeSecurity -Enabled $false
Assert-True ($nativeDisabled.DefinitionSha256 -ceq $native.DefinitionSha256 -and $nativeDisabled.SecuritySha256 -ceq $native.SecuritySha256) 'Allowed enablement changed a native task binding.'
Assert-True ($native.DefinitionSha256 -cne $baseline.DefinitionSha256) 'The returned XML was altered to manufacture a LogonType node.'
$securityDrift=Observe -Definition $nativeXml -Descriptor ($nativeSecurity+'(A;;FR;;;BA)')
Assert-True ($securityDrift.SecuritySha256 -cne $native.SecuritySha256) 'Later descriptor drift was omitted from the pinned security fingerprint.'
foreach($definition in @($xml,$nativeXml)){
 Expect-Refusal {Observe -Definition $definition -NativeLogonType $null}
 Expect-Refusal {Observe -Definition $definition -NativeLogonType 3}
}
Expect-Refusal {Observe -Definition $nativeXml.Replace('<RunLevel>','<LogonType>InteractiveToken</LogonType><RunLevel>')}
foreach($definition in @(
 $xml.Replace('<UserId>S-1-5-18</UserId>','<UserId>S-1-5-21-1-2-3-1001</UserId>'),
 $xml.Replace('ServiceAccount','InteractiveToken'),$xml.Replace('HighestAvailable','LeastPrivilege'),
 $xml.Replace('FluxKnowledge.Cli.exe','other.exe'),$xml.Replace('source-access serve','source-access repair'),
 $xml.Replace('</Actions>','<Exec><Command>other.exe</Command></Exec></Actions>'),
 $xml.Replace('<Hidden>true</Hidden>','<Hidden>true</Hidden></Settings><Settings>'),
 ('<!DOCTYPE Task [<!ENTITY x "unsafe">]>'+$xml)
)){Expect-Refusal {Observe -Definition $definition}}
foreach($descriptor in @('O:BUG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)',
 'O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;BU)',
 'O:BAG:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(A;ID;FR;;;BU)',
 'O:BAG:BA','O:BAG:BAD:NO_ACCESS_CONTROL')){
 Expect-Refusal {Observe -Descriptor $descriptor}
}
Expect-Refusal {ConvertFrom-SourceJournalHelperTaskSnapshot -Xml $xml -SecurityDescriptor $security -InstallRoot 'I:\unrelated' -TaskName $task -Enabled $true -Running $false -NativeLogonType 5}
Expect-Refusal {ConvertFrom-SourceJournalHelperTaskSnapshot -Xml $xml -SecurityDescriptor $security -InstallRoot $root -TaskName 'Unrelated.Task' -Enabled $true -Running $false -NativeLogonType 5}
[pscustomobject]@{Passed=$script:passed;TaskCalls=0;ProcessCalls=0;ProductionCalls=0}|ConvertTo-Json
