[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$OperatorRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-projection-operator.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-iis.ps1')
Add-Type -Path (Join-Path $SourceRoot 'scripts/deploy/ProjectionOperatorLoadContext.cs')
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('MetadataPolicyOperator-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path (Join-Path $area 'legacy-helper/payload') -Force
try{
    $DisableIncrementalSourceScanning=$true
    $SourceJournalHelperRoot=Join-Path $area 'legacy-helper'
    $legacy=Join-Path $SourceJournalHelperRoot 'payload'
    [IO.File]::WriteAllText((Join-Path $legacy 'old-operator.txt'),'No current metadata-policy operator is installed here.')
    $before=Get-HybridPayloadFingerprint $legacy
    $SourceJournalPolicyOperatorRoot=(Resolve-Path -LiteralPath $OperatorRoot).Path
    $ExpectedSourceJournalPolicyOperatorSha256=Get-HybridPayloadFingerprint $SourceJournalPolicyOperatorRoot
    if((Get-DeploymentSourceJournalPolicyOperatorRoot) -cne $SourceJournalPolicyOperatorRoot){throw 'Feature-off selected the legacy helper instead of the reviewed operator.'}
    $ExpectedSourceJournalPolicyOperatorSha256='0'*64
    $refused=$false
    try{[void](Get-DeploymentSourceJournalPolicyOperatorRoot)}catch{$refused=$_.Exception.Message -eq 'metadata-policy-operator-binding-changed'}
    if(!$refused){throw 'A changed operator binding was accepted.'}
    $SourceJournalPolicyOperatorRoot=$legacy
    $ExpectedSourceJournalPolicyOperatorSha256=Get-HybridPayloadFingerprint $legacy
    $refused=$false
    try{[void](Get-DeploymentSourceJournalPolicyOperatorRoot)}catch{$refused=$true}
    if(!$refused){throw 'A legacy helper without the current operator was accepted.'}
    if((Get-HybridPayloadFingerprint $legacy) -cne $before){throw 'The legacy helper payload changed.'}
    [pscustomobject]@{Passed=3;CurrentReviewedOperator=$true;LegacyHelperUnchanged=$true;ProductionCalls=0;SqlCalls=0;ModelCalls=0}|ConvertTo-Json
}finally{
    $absolute=[IO.Path]::GetFullPath($area)
    if(!$absolute.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\MetadataPolicyOperator-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
