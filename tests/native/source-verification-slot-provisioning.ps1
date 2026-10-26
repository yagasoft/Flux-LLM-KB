param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater did not parse.'}
foreach($name in @('Assert-NotReparsePoint','Assert-StableRuntimeReaderAcl','Get-SourceVerificationReaderFilePlan','Ensure-SourceVerificationReaderFile')) {
    $functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($functions.Count -ne 1){throw "Missing production provisioning function: $name"}
    . ([scriptblock]::Create($functions[0].Extent.Text))
}
$guard=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-SourceVerificationReaderMaintenance'},$true)
foreach($required in @('Stopped','Get-HybridIisWorkerIds','ValidationHoldPath','Assert-NotReparsePoint')) {
    if(!$guard -or !$guard.Extent.Text.Contains($required)){throw 'Provisioning lost its stopped/held maintenance guard.'}
}
$activation=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-CandidatePayloadActivation'},$true)
if($activation.Body.EndBlock.Statements[0].Extent.Text -cne 'Ensure-SourceVerificationReaderFile'){throw 'Stable file must be provisioned before candidate activation and feature-off retirement.'}
function Require([bool]$condition,[string]$reason){if(!$condition){throw $reason}}
function MustRefuse([scriptblock]$action){$refused=$false;try{& $action}catch{$refused=$true};Require $refused 'Unsafe provisioning was admitted.'}
$ground='I:\Codex Workspaces\Staging\FluxKnowledge'
$CanonicalLiveRoot=Join-Path $ground ('VerificationSlotProvisioning-'+[guid]::NewGuid().ToString('N'))
$runtime=Join-Path $CanonicalLiveRoot 'Runtime'
$SourceVerificationReaderPath=Join-Path $runtime 'source-verification.lock'
$script:maintenanceAllowed=$false
function Assert-SourceVerificationReaderMaintenance {if(!$script:maintenanceAllowed){throw 'Injected missing maintenance proof.'}}
function Set-ReadOnlyRuntimeAcl([string]$path) {
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('IIS AppPool\FluxKnowledge','ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $path -AclObject $acl
}
try {
    [IO.Directory]::CreateDirectory($runtime)|Out-Null
    Set-ReadOnlyRuntimeAcl $CanonicalLiveRoot
    Set-ReadOnlyRuntimeAcl $runtime
    $before=(Get-Acl -LiteralPath $runtime).Sddl
    Require ((Get-SourceVerificationReaderFilePlan).action -ceq 'create-new-before-activation') 'Absent file must have a create plan.'
    Require (!(Test-Path -LiteralPath $SourceVerificationReaderPath)) 'Planning created the file.'
    MustRefuse {Ensure-SourceVerificationReaderFile}
    Require (!(Test-Path -LiteralPath $SourceVerificationReaderPath)) 'Missing maintenance proof created the file.'
    $script:maintenanceAllowed=$true
    Ensure-SourceVerificationReaderFile
    $identity=(& fsutil file queryfileid $SourceVerificationReaderPath|Out-String)
    Require ($LASTEXITCODE -eq 0) 'File identity observation failed.'
    Ensure-SourceVerificationReaderFile
    Require ((& fsutil file queryfileid $SourceVerificationReaderPath|Out-String) -ceq $identity) 'Repeated provisioning replaced the file.'
    Require ((Get-Acl -LiteralPath $runtime).Sddl -ceq $before) 'Provisioning changed Runtime permissions.'
    $held=[IO.FileStream]::new($SourceVerificationReaderPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
    try {MustRefuse {Ensure-SourceVerificationReaderFile}} finally {$held.Dispose()}
    $unsafe=Get-Acl -LiteralPath $SourceVerificationReaderPath
    $unsafe.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('BUILTIN\Users','Write','Allow'))
    Set-Acl -LiteralPath $SourceVerificationReaderPath -AclObject $unsafe
    $unsafeSddl=(Get-Acl -LiteralPath $SourceVerificationReaderPath).Sddl
    MustRefuse {Ensure-SourceVerificationReaderFile}
    Require ((Get-Acl -LiteralPath $SourceVerificationReaderPath).Sddl -ceq $unsafeSddl) 'Unsafe existing file was silently repaired.'
    Require ((& fsutil file queryfileid $SourceVerificationReaderPath|Out-String) -ceq $identity) 'Unsafe existing file was replaced.'
    Write-Output 'Source verification slot provisioning passed: held/stopped admission, plan-only, Runtime RX, stable identity, conflicting owner and unsafe ACL refusal.'
} finally {
    $resolved=[IO.Path]::GetFullPath($CanonicalLiveRoot)
    if([IO.Path]::GetDirectoryName($resolved) -cne $ground -or ![IO.Path]::GetFileName($resolved).StartsWith('VerificationSlotProvisioning-')){throw 'Fixture cleanup escaped owned staging.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
