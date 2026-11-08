[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$OperatorRoot)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-projection-operator.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
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
    # Exercise the real IIS retirement composition through its native directory
    # identity call and durable binding files. Only the stopped-writer assertion
    # and final SQL operator are isolated ports; no production state is used.
    $CanonicalLiveRoot=Join-Path $area 'application'
    $null=New-Item -ItemType Directory -Path (Join-Path $CanonicalLiveRoot 'Runtime') -Force
    $releaseRoot=Join-Path $area 'release'
    $null=New-Item -ItemType Directory -Path $releaseRoot
    $releaseId='isolated-'+[guid]::NewGuid().ToString('N')
    $ValidationHoldPath=Join-Path $area 'hold.json'
    [IO.File]::WriteAllText($ValidationHoldPath,('"'+$releaseId+'"'))
    $sourceJournalChange=@{Path=(Join-Path $area 'configuration.json')}
    [IO.File]::WriteAllText($sourceJournalChange.Path,'{}')
    $ExpectedSourceJournalDatabaseIdentity='isolated|metadata-policy|'+[guid]::NewGuid().ToString('D')
    $SourceJournalPolicyOperatorRoot=(Resolve-Path -LiteralPath $OperatorRoot).Path
    $ExpectedSourceJournalPolicyOperatorSha256=Get-HybridPayloadFingerprint $SourceJournalPolicyOperatorRoot
    $sourceJournalRelease=@{StopProof=@{ExitedIdentities=@()};RetirementConfirmed=$false}
    $script:stopAssertions=0;$script:operatorCalls=0
    function Assert-DeploymentSourceJournalReleaseStopped {
        if(!$CanonicalLiveRoot.StartsWith($area+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Non-isolated runtime root.'}
        $script:stopAssertions++
    }
    function Invoke-IncrementalSourceJournalRetirement {
        param([string]$OperatorRoot,[string]$BindingsJson,[scriptblock]$AssertQuiescent)
        &$AssertQuiescent
        $binding=$BindingsJson|ConvertFrom-Json
        $evidence=Get-Content -LiteralPath $binding.StoppedWritersEvidencePath -Raw|ConvertFrom-Json
        if($binding.MetadataPolicy -cne 'events-weekly-metadata-v1' -or !$binding.RequireFeatureOff -or
            $evidence.RuntimeRoot -cne (Join-Path $CanonicalLiveRoot 'Runtime') -or !$evidence.WritersStopped -or
            [string]::IsNullOrWhiteSpace($evidence.RuntimeIdentityFingerprint) -or
            $binding.RuntimeIdentityFingerprint -cne $evidence.RuntimeIdentityFingerprint -or
            $binding.StoppedWritersEvidenceSha256 -cne (Get-FileHash -LiteralPath $binding.StoppedWritersEvidencePath).Hash -or
            $binding.OperatorEntrySha256 -cne (Get-FileHash -LiteralPath (Join-Path $OperatorRoot 'FluxKnowledge.Cli.dll')).Hash) {
            throw 'Composed stopped-retirement binding was not preserved.'
        }
        $script:operatorCalls++
        @{Version=1;IsolatedOperatorReached=$true}
    }
    Invoke-DeploymentSourceJournalVerificationRetirement
    if($script:stopAssertions -ne 2 -or $script:operatorCalls -ne 1 -or !$sourceJournalRelease.RetirementConfirmed -or
        !(Test-Path -LiteralPath (Join-Path $releaseRoot 'source-journal-retirement.json'))) {
        throw 'Composed retirement did not complete through its guarded operator port.'
    }
    [pscustomobject]@{Passed=4;CurrentReviewedOperator=$true;LegacyHelperUnchanged=$true;ComposedRetirementBound=$true;ProductionCalls=0;SqlCalls=0;ModelCalls=0}|ConvertTo-Json
}finally{
    $absolute=[IO.Path]::GetFullPath($area)
    if(!$absolute.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\MetadataPolicyOperator-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup.'}
    Remove-Item -LiteralPath $absolute -Recurse -Force
}
