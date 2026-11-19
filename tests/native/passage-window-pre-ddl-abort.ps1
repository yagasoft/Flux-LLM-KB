param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),[switch]$CodexWorkspace)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $SourceRoot 'scripts/deploy/incremental-passage-window-pre-ddl-abort.ps1')
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-passage-window-eligibility-migration.psm1') -Force
if(!(Get-Command Write-PassageWindowEligibilityRecord -ErrorAction SilentlyContinue)){throw 'Durable recovery writer is not exported.'}
foreach($failure in @('proof-before','intent','capture','start','proof-after','probe-held','unchanged','proof-release','release','probe-after','')){
 $calls=[Collections.Generic.List[string]]::new();$counts=@{Proof=0;Probe=0}
 $event={param($name) $calls.Add($name);if($failure -ceq $name){throw ('synthetic-'+$name)}}
 $ports=@{
  AssertEvidence={param($Running) $counts.Proof++;&$event $(if(!$Running){'proof-before'}elseif($counts.Proof -eq 2){'proof-after'}else{'proof-release'})}
  WriteIntent={&$event 'intent'};Capture={&$event 'capture';'baseline'}
  Start={&$event 'start'};Stop={&$event 'stop'}
  Probe={$counts.Probe++;&$event $(if($counts.Probe -eq 1){'probe-held'}else{'probe-after'})}
  AssertUnchanged={param($Baseline) if($Baseline -cne 'baseline'){throw 'Baseline changed'};&$event 'unchanged'}
  Release={&$event 'release'};Complete={&$event 'completed'}
 }
 $caught=$false;try{Invoke-PassageWindowPreDdlAbort $ports}catch{$caught=$true}
 if($caught -ne [bool]$failure){throw ('Wrong failure disposition: '+$failure)}
 if($failure -in @('proof-before','intent','capture') -and $calls.Contains('start')){throw 'Refused evidence reached startup.'}
 if($failure -in @('start','proof-after','probe-held','unchanged','proof-release') -and
    (!$calls.Contains('stop') -or $calls.Contains('release'))){throw 'Held startup failure did not stop and retain the hold.'}
 if($failure -in @('release','probe-after') -and $calls.Contains('stop')){throw 'Uncertain release crossed an automatic rollback boundary.'}
 if(!$failure -and ($calls -join '|') -cne 'proof-before|intent|capture|start|proof-after|probe-held|unchanged|proof-release|release|probe-after|completed'){
  throw 'Recovery ordering changed.'
 }
}
Write-Output 'Typed pre-DDL abort: 11 evidence/start/probe/release failure boundaries passed; no production calls.'

$caseRoot = Join-Path ([IO.Path]::GetTempPath()) ('outlook-abort-' + [guid]::NewGuid().ToString('N'))
$release = '20261119T000000Z-aaaaaaaaaaaa'
$IncrementalRecoveryRoot = $caseRoot
$directory = Join-Path $caseRoot $release
[void][IO.Directory]::CreateDirectory($directory)
$priorBindingSha = 'D' * 64
$ExpectedSourceJournalDatabaseIdentity = 'owned'
$CanonicalDeployRoot = Join-Path $caseRoot 'web'
$CanonicalLiveRoot = Join-Path $caseRoot 'live'
$InteractiveHostRoot = Join-Path $caseRoot 'interactive/host'
$SourceJournalHelperRoot = Join-Path $caseRoot 'helper'
$InteractiveHostTaskName = 'fixture'
$SiteName = 'fixture'
$ValidationHoldPath = Join-Path $caseRoot 'hold.json'
$ReadinessTimeoutSeconds = 10
$mode = if ($CodexWorkspace) { @{CodexWorkspace = $true} } else { @{Outlook = $true} }
$contract = Get-PassageWindowEligibilityMigrationContract @mode
$purpose = $contract.ArtifactPrefix + '-migration'
$receiptPrefix = if ($CodexWorkspace) { 'codex-workspace-pre-ddl-abort' } else { 'outlook-pre-ddl-abort' }
$policy = [byte[]]@(1, 2, 3)
$policyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($policy))
$fixtureHelperTask = @{
 TaskName='fixture-helper';PrincipalSid='sid';Executable='helper.exe';Arguments='fixture';
 WorkingDirectory=$SourceJournalHelperRoot;DefinitionSha256='definition';SecuritySha256='security';Enabled=$true;Running=$true
}
$binding = @{
 Version=1;Purpose=($purpose+'-release');ReleaseId=$release;Commit=('a'*40);Identity='owned';
 Target=$contract.Target;SqlSha256=$contract.UpSha256;EnableCompanionAfterValidation=$true;
 OriginalPayloads=@(
    @{Role='Web'; Root=$CanonicalDeployRoot; Sha256='original'},
    @{Role='OutlookHost'; Root=$InteractiveHostRoot; Sha256='original'},
    @{Role='Cli'; Root=(Join-Path $SourceJournalHelperRoot 'payload'); Sha256='original'}
 );
 ConfigurationSha256='config';CompanionConfigurationSha256='config';TaskIdentityHash='companion-identity';LauncherSha256='launcher';
 HelperTask=$fixtureHelperTask;HelperRepairPolicySha256=$policyHash;HelperRootReadPlanSha256=$policyHash;HelperJournalPolicySha256=$policyHash
}
$manifest = @{commit=$binding.Commit;migrations=$true;clean_slate=$false;application_root=$CanonicalDeployRoot
    ($contract.ManifestField)=@{ReleaseBindingSha256=$priorBindingSha}}
$checkpoint = @{Phase='Prepared';ReleaseId=$release;OriginalPayloadSha256='original';Task=$fixtureHelperTask}
function Read-SegmentedUpdateJson {
 param($Path,$ExpectedSha)
 switch ([IO.Path]::GetFileName($Path)) {
   ($purpose+'-release-binding.json') { $binding }
   'manifest.json' { $manifest }
   ($purpose+'-failure-reconciliation.json') { @{ReleaseId=$release;Identity='owned';Attempted=$false;SettledState='Old';HoldRetained=$true} }
   'state.json' { $checkpoint }
   default { throw 'Unexpected recovery evidence.' }
 }
}
function Assert-HybridDeploymentHoldOwner { if (!$case.Held) { throw 'Hold would be lost.' } }
function Assert-PassageWindowEligibilityStopped { if ($case.Started) { throw 'Web is already started.' } }
function Get-WebAppPoolState { @{Value='Started'} }
function Assert-SegmentedUpdatePath($Path) { $Path }
function Get-HybridPayloadFingerprint { 'original' }
function Get-DeploymentSqlConnectionString { 'web' }
function Get-RepositoryRecoveryCompanionConnectionString { 'outlook' }
function Get-PassageWindowEligibilityDatabaseState { param($ConnectionString,[switch]$Outlook,[switch]$CodexWorkspace); if($Outlook -ne !$mode.ContainsKey('CodexWorkspace') -or $CodexWorkspace -ne $mode.ContainsKey('CodexWorkspace')){throw 'Missing Outlook state mode.'}; $ConnectionString }
function Get-PassageWindowEligibilityStateKind { param($DatabaseState,$ExpectedDatabaseIdentity,[switch]$Outlook,[switch]$CodexWorkspace); if($Outlook -ne !$mode.ContainsKey('CodexWorkspace') -or $CodexWorkspace -ne $mode.ContainsKey('CodexWorkspace')){throw 'Missing Outlook classifier mode.'}; 'Old' }
function Get-FileHash { @{Hash='config'} }
function Get-RepositoryRecoveryTaskObservation { @{Enabled=$case.Enabled;State='Disabled';IdentityHash='companion-identity';LauncherSha256='launcher'} }
function Wait-InteractiveHostStopped {}
function Assert-DeploymentSourceJournalCliWritersStopped {}
function Get-SourceJournalHelperPayloadManifest { @{Sha256='original'} }
function Get-SourceJournalHelperTaskObservation { $fixtureHelperTask }
function Read-SourceJournalHelperFile { ,$policy }
function Start-IndexCompatibleWebAppPool { $case.Started=$true }
function Wait-IisAppPoolState {}
function Stop-HybridIisAfterGpuDrain { $case.Started=$false;$case.Stops++ }
function Get-RetainedPipelineStateBaseline { 'baseline' }
function Assert-RetainedPipelineStateUnchanged {}
function Remove-DeploymentValidationHold { $case.Held=$false }
function Invoke-RequiredLoopbackProbes {
    $case.Probes++
    if ($failure -eq $case.Probes) { throw 'Injected probe failure.' }
}
function Enable-IndexCompatibleOutlookTask {
    if ($case.Held -or $case.Probes -ne 2) { throw 'Companion enabled before final probe.' }
    $case.EnableAttempts++
    if ($failure -eq 3) { throw 'Injected companion enablement failure.' }
    $case.Enabled=$true
}
function Get-ScheduledTask { @{Settings=@{Enabled=$case.Enabled}} }
try {
    foreach ($failure in 0..3) {
        $case=@{Held=$true;Started=$false;Enabled=$false;Probes=0;EnableAttempts=0;Stops=0}
        $intent=Join-Path $directory ($receiptPrefix+'-intent.json')
        $completed=Join-Path $directory ($receiptPrefix+'-completed.json')
        [IO.File]::Delete($intent)
        [IO.File]::Delete($completed)
        $caught=$false
        try { Invoke-DeploymentPassageWindowPreDdlAbort $release $priorBindingSha -Apply @mode | Out-Null }
        catch { $caught=$true; if (!$failure) { throw } }
        if ($caught -ne [bool]$failure -or !(Test-Path -LiteralPath $intent)) { throw 'Invalid composed Outlook recovery outcome.' }
        if ($failure -eq 0 -and (!$case.Enabled -or $case.Held -or !(Test-Path -LiteralPath $completed))) { throw 'Prepared recovery did not restore service.' }
        if ($failure -in 1,2 -and ($case.EnableAttempts -ne 0 -or $case.Held -ne ($failure -eq 1))) { throw 'Probe failure crossed the companion gate.' }
        if ($failure -eq 3 -and ($case.EnableAttempts -ne 1 -or $case.Enabled -or $case.Held -or $case.Stops -ne 0 -or (Test-Path -LiteralPath $completed))) {
            throw 'Enablement failure did not preserve the spent intent.'
        }
        if ($failure -eq 3) {
            try { Invoke-DeploymentPassageWindowPreDdlAbort $release $priorBindingSha -Apply @mode | Out-Null }
            catch { }
            if ($case.EnableAttempts -ne 1 -or $case.Stops -ne 0) { throw 'Enablement failure replayed recovery.' }
        }
    }
    Write-Output 'Outlook Prepared recovery and held/released probe and companion restoration boundaries passed.'
} finally {
    [IO.File]::Delete((Join-Path $directory ($receiptPrefix+'-intent.json')))
    [IO.File]::Delete((Join-Path $directory ($receiptPrefix+'-completed.json')))
    [IO.Directory]::Delete($directory)
    [IO.Directory]::Delete($caseRoot)
}
