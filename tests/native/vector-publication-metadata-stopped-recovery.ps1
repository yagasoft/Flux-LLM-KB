[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $SourceRoot 'scripts/deploy/incremental-stopped-release-recovery.ps1')
function Check([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Refuses([scriptblock]$Action){$failed=$false;try{&$Action}catch{$failed=$true};Check $failed 'Expected refusal.'}
Check ([bool](Get-Command Assert-IncrementalVectorMetadataResumeEvidence -ErrorAction SilentlyContinue)) 'Vector metadata resume evidence guard is missing.'
function Get-VectorPublicationMetadataMigrationContract {[pscustomobject]@{Target='20261101195325_AddVectorPublicationMetadata';UpSha256='B'*64}}
$roles=@('Web','OutlookHost','Cli')
$original=@($roles|ForEach-Object {@{Role=$_;Root=('I:\owned\'+$_);Sha256='A'*64}})
$candidate=@($roles|ForEach-Object {@{Role=$_;Root=('I:\owned\'+$_);Sha256='C'*64}})
$installed=@($roles|ForEach-Object {@{Role=$_;Root=('I:\owned\'+$_);Sha256=$(if($_ -ceq 'Web'){'A'*64}else{'C'*64})}})
$binding=@{Version=2;Purpose='vector-publication-metadata-resume';PriorReleaseId='20261101T200000Z-'+('d'*12);PriorCommit='d'*40;DatabaseIdentity='owned-db';Payloads=$installed;MigrationReleaseBindingSha256='E'*64;MigrationIntentSha256='F'*64;ConfigurationSha256='1'*64;CompanionConfigurationSha256='2'*64;TaskIdentityHash='3'*64;LauncherSha256='4'*64;EnableCompanionAfterValidation=$true}
$release=@{Version=1;Purpose='vector-publication-metadata-release';ReleaseId=$binding.PriorReleaseId;Commit=$binding.PriorCommit;Identity=$binding.DatabaseIdentity;Target=(Get-VectorPublicationMetadataMigrationContract).Target;SqlSha256='B'*64;InitialKind='Old';OriginalPayloads=$original;CandidatePayloads=$candidate;ConfigurationSha256=$binding.ConfigurationSha256;CompanionConfigurationSha256=$binding.CompanionConfigurationSha256;TaskIdentityHash=$binding.TaskIdentityHash;LauncherSha256=$binding.LauncherSha256;EnableCompanionAfterValidation=$true}
$manifest=@{commit=$binding.PriorCommit;application_root='unused';migrations=$true;clean_slate=$false;vector_publication_metadata=@{Target=$release.Target;SqlSha256=$release.SqlSha256;ReleaseBindingSha256='E'*64}}
$intent=@{Version=1;Purpose='vector-publication-metadata-migration';ReleaseId=$binding.PriorReleaseId;Commit=$binding.PriorCommit;Identity=$binding.DatabaseIdentity;Target=$release.Target;SqlSha256=$release.SqlSha256;Attempted=$true;InitialKind='Old'}
Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $release $intent
$intent.InitialKind='New';$release.InitialKind='New';Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $release $intent
foreach($key in @('Identity','Commit','ReleaseId','Target','SqlSha256')){
    $bad=$intent.Clone();$bad[$key]='changed';Refuses {Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $release $bad}
}
$bad=$intent.Clone();$bad.Attempted=$false;Refuses {Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $release $bad}
$bad=$manifest.Clone();$bad.migrations=$false;Refuses {Assert-IncrementalVectorMetadataResumeEvidence $binding $bad $release $intent}
$bad=$release.Clone();$bad.ConfigurationSha256='0'*64;Refuses {Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $bad $intent}
$installed[0].Sha256='9'*64;Refuses {Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $release $intent};$installed[0].Sha256='A'*64
$candidate[0].Root='I:\foreign';Refuses {Assert-IncrementalVectorMetadataResumeEvidence $binding $manifest $release $intent};$candidate[0].Root='I:\owned\Web'
Assert-IncrementalStoppedRecoveryMode -Binding $binding -VectorMetadataMigration $true
Refuses {Assert-IncrementalStoppedRecoveryMode -Binding $binding -VectorMetadataMigration $false}
$ordinary=@{Version=1;Purpose='incremental-stopped-release'}
Assert-IncrementalStoppedRecoveryMode -Binding $ordinary -VectorMetadataMigration $false
Refuses {Assert-IncrementalStoppedRecoveryMode -Binding $ordinary -VectorMetadataMigration $true}
# Reuse the real V1 filesystem fixture without running its scenarios. All native
# observations remain fake; binding reads, fingerprints and atomic writes are real.
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
$fixtureAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'tests/native/incremental-stopped-release-recovery.ps1'),[ref]$null,[ref]$null)
$script:VectorFixtureParent='I:\Codex Workspaces\TestTemp\FluxKnowledge'
[void][IO.Directory]::CreateDirectory($script:VectorFixtureParent)
foreach($name in @('Assert-StoppedPoolRecoveryBaseline','Get-RepositoryRecoveryTaskObservation','Wait-InteractiveHostStopped','Get-CimInstance','Get-DeploymentSqlConnectionString','Get-RepositoryRecoveryCompanionConnectionString','Get-SourceJournalDatabaseState','New-RecoveryFixture','Read-FixtureBinding')){
    $definition=@($fixtureAst.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$false))[0]
    . ([scriptblock]::Create($definition.Extent.Text.Replace('[IO.Path]::GetTempPath()','$script:VectorFixtureParent')))
}
function Get-VectorPublicationMetadataDatabaseState {param($ConnectionString);@{Identity=$script:DatabaseIdentity;Kind=$script:ObservedKind}}
function Get-VectorPublicationMetadataStateKind {param($DatabaseState,$ExpectedDatabaseIdentity);if($DatabaseState.Identity -cne $ExpectedDatabaseIdentity -or $DatabaseState.Kind -cnotin @('Old','New')){throw 'fixture-exact-schema-refused'};$DatabaseState.Kind}
function New-VectorRecoveryFixture {
    param([switch]$BeforeIntent)
    New-RecoveryFixture
    $script:ObservedKind='Old'
    $script:Binding.Version=2;$script:Binding.Purpose='vector-publication-metadata-resume'
    $prior=Join-Path $IncrementalRecoveryRoot $PriorReleaseId
    $original=@($Binding.Payloads|ForEach-Object{$_.Clone()})
    # Reproduce old Web plus candidate Outlook. The old Web is not the candidate.
    [IO.File]::WriteAllText((Join-Path $prior 'candidate/marker.txt'),'candidate-web')
    [IO.File]::WriteAllText((Join-Path $prior 'candidate-interactive-host/marker.txt'),'candidate-companion')
    if(!$BeforeIntent){[IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker.txt'),'candidate-companion')}
    foreach($payload in $Binding.Payloads){$payload.Sha256=Get-HybridPayloadFingerprint $payload.Root}
    $candidates=@($Binding.Payloads|ForEach-Object{$_.Clone()})
    $candidates[0].Sha256=Get-HybridPayloadFingerprint (Join-Path $prior 'candidate')
    $candidates[1].Sha256=Get-HybridPayloadFingerprint (Join-Path $prior 'candidate-interactive-host')
    $release=@{Version=1;Purpose='vector-publication-metadata-release';ReleaseId=$PriorReleaseId;Commit=$Binding.PriorCommit;
        Identity=$DatabaseIdentity;Target=(Get-VectorPublicationMetadataMigrationContract).Target;SqlSha256='B'*64;
        InitialKind='Old';OriginalPayloads=$original;CandidatePayloads=$candidates}
    foreach($key in @('ConfigurationSha256','CompanionConfigurationSha256','TaskIdentityHash','LauncherSha256','EnableCompanionAfterValidation')){$release[$key]=$Binding[$key]}
    $intent=@{Version=1;Purpose='vector-publication-metadata-migration';ReleaseId=$PriorReleaseId;Commit=$Binding.PriorCommit;
        Identity=$DatabaseIdentity;Target=$release.Target;SqlSha256=$release.SqlSha256;Attempted=$true;InitialKind='Old'}
    $releasePath=Join-Path $prior 'vector-publication-metadata-release-binding.json'
    $intentPath=Join-Path $prior 'vector-publication-metadata-migration-intent.json'
    Write-SegmentedUpdateJson $releasePath $release
    if(!$BeforeIntent){Write-SegmentedUpdateJson $intentPath $intent}
    $Binding.MigrationReleaseBindingSha256=(Get-FileHash -LiteralPath $releasePath).Hash
    $Binding.MigrationIntentSha256=if($BeforeIntent){''}else{(Get-FileHash -LiteralPath $intentPath).Hash}
    $manifestPath=Join-Path $prior 'manifest.json'
    Write-SegmentedUpdateJson $manifestPath @{commit=$Binding.PriorCommit;application_root=$CanonicalDeployRoot;migrations=$true;clean_slate=$false;
        vector_publication_metadata=@{Target=$release.Target;SqlSha256=$release.SqlSha256;ReleaseBindingSha256=$Binding.MigrationReleaseBindingSha256}}
    $Binding.PriorManifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash
    Write-SegmentedUpdateJson $BindingPath $Binding
    $script:BindingSha256=(Get-FileHash -LiteralPath $BindingPath).Hash
}
New-VectorRecoveryFixture -BeforeIntent
$accepted=Read-FixtureBinding
Assert-IncrementalStoppedRecoveryBinding $accepted
Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath
Check ((Read-SegmentedUpdateJson $ValidationHoldPath) -ceq $NextReleaseId) 'Pre-intent crash recovery failed.'
foreach($drift in @('partial-intent','intent-directory','unexpected-new','candidate-installed','companion-candidate-installed')){
    New-VectorRecoveryFixture -BeforeIntent;$accepted=Read-FixtureBinding
    switch($drift){
        'partial-intent' {[IO.File]::WriteAllText((Join-Path $IncrementalRecoveryRoot ($PriorReleaseId+'/vector-publication-metadata-migration-intent.json')),'{')}
        'intent-directory' {[void][IO.Directory]::CreateDirectory((Join-Path $IncrementalRecoveryRoot ($PriorReleaseId+'/vector-publication-metadata-migration-intent.json')))}
        'unexpected-new' {$script:ObservedKind='New'}
        'candidate-installed' {
            [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker.txt'),'candidate-web')
            $Binding.Payloads[0].Sha256=Get-HybridPayloadFingerprint $CanonicalDeployRoot
            Write-SegmentedUpdateJson $BindingPath $Binding;$script:BindingSha256=(Get-FileHash -LiteralPath $BindingPath).Hash;$accepted=Read-FixtureBinding
        }
        'companion-candidate-installed' {
            [IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'marker.txt'),'candidate-companion')
            $Binding.Payloads[1].Sha256=Get-HybridPayloadFingerprint $InteractiveHostRoot
            Write-SegmentedUpdateJson $BindingPath $Binding;$script:BindingSha256=(Get-FileHash -LiteralPath $BindingPath).Hash;$accepted=Read-FixtureBinding
        }
    }
    $hold=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash
    Refuses {Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath}
    Check (!(Test-Path -LiteralPath $StatePath) -and (Get-FileHash -LiteralPath $ValidationHoldPath).Hash -ceq $hold) ('Pre-intent drift changed hold: '+$drift)
}
foreach($kind in @('Old','New')){
    New-VectorRecoveryFixture;$script:ObservedKind=$kind;$accepted=Read-FixtureBinding
    Assert-IncrementalStoppedRecoveryBinding $accepted
    Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath
    Check ((Read-SegmentedUpdateJson $ValidationHoldPath) -ceq $NextReleaseId) 'Typed hold transfer failed.'
    Refuses {Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath}
}
foreach($drift in @('unknown-schema','release-receipt','intent-receipt','candidate','foreign-installed','database','enabled')){
    New-VectorRecoveryFixture;$accepted=Read-FixtureBinding;$prior=Join-Path $IncrementalRecoveryRoot $PriorReleaseId
    switch($drift){
        'unknown-schema' {$script:ObservedKind='Partial'}
        'release-receipt' {[IO.File]::AppendAllText((Join-Path $prior 'vector-publication-metadata-release-binding.json'),' ')}
        'intent-receipt' {[IO.File]::AppendAllText((Join-Path $prior 'vector-publication-metadata-migration-intent.json'),' ')}
        'candidate' {[IO.File]::WriteAllText((Join-Path $prior 'candidate/marker.txt'),'changed')}
        'foreign-installed' {[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'marker.txt'),'changed')}
        'database' {$script:DatabaseIdentity='foreign'}
        'enabled' {$script:TaskEnabled=$true}
    }
    $hold=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash
    Refuses {Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath}
    Check (!(Test-Path -LiteralPath $StatePath) -and (Get-FileHash -LiteralPath $ValidationHoldPath).Hash -ceq $hold) ('Drift mutated hold: '+$drift)
}
$script:AtomicWriter=(Get-Command Write-SegmentedUpdateJson).ScriptBlock
function Write-SegmentedUpdateJson {
    param($Path,$Value)
    if($script:Interrupt -and $Path -ceq $ValidationHoldPath){
        Check (Test-Path -LiteralPath $StatePath) 'Hold replacement preceded durable transfer intent.'
        if($script:Interrupt -ceq 'after-intent'){throw 'injected-after-transfer-intent'}
    }
    & $script:AtomicWriter $Path $Value
    if($script:Interrupt -ceq 'after-replacement' -and $Path -ceq $ValidationHoldPath){throw 'injected-after-transfer-replacement'}
}
foreach($boundary in @('after-intent','after-replacement')){
    $script:Interrupt='';New-VectorRecoveryFixture -BeforeIntent;$accepted=Read-FixtureBinding
    $script:Interrupt=$boundary
    Refuses {Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath}
    $script:Interrupt=''
    $owner=if($boundary -ceq 'after-intent'){$PriorReleaseId}else{$NextReleaseId}
    Check ((Read-SegmentedUpdateJson $ValidationHoldPath) -ceq $owner) 'Interrupted V2 transfer lost deny-all hold.'
    Refuses {Move-IncrementalStoppedRecoveryHold $accepted $NextReleaseId $StatePath}
    Check ((Read-SegmentedUpdateJson $ValidationHoldPath) -ceq $owner) 'Interrupted V2 transfer replay changed owner.'
}
Write-Output 'Vector metadata stopped recovery passed: real typed binding/payload reads, Old/New transfer, pre-intent crash recovery, 12 drift refusals and two transfer interruptions.'
