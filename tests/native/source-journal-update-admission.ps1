[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-schema.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused ('Expected refusal: '+$Action.ToString())}
$contract=Get-SourceJournalMigrationContract
$base=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File |
 ForEach-Object {[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]')|ForEach-Object {$_.Groups[1].Value}}|
 Where-Object {$_ -cle $contract.Baseline}|Sort-Object)
$baseline=[pscustomobject]@{Identity='owned-incarnation';History=$base;SchemaSha256=$contract.BaselineSchemaSha256;CanAlter=$true}
$complete=[pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations));SchemaSha256=$contract.SchemaSha256;CanAlter=$true}
$projection=[pscustomobject]@{Identity=$baseline.Identity;History=$complete.History;
 SchemaSha256=(Get-SegmentedIndexMigrationContract).SchemaSha256;AuditSingletonCount=1;RequiredFormat=2}
Assert-SourceJournalComposedApplicationSchema -JournalState $complete -ProjectionState $projection
Assert-True (($projection.History -join '|') -ceq ($complete.History -join '|')) 'Composition changed the original observation.'
foreach($bad in @(
 [pscustomobject]@{Identity='different-incarnation';History=$complete.History;SchemaSha256=$projection.SchemaSha256;AuditSingletonCount=1;RequiredFormat=2},
 [pscustomobject]@{Identity=$baseline.Identity;History=($complete.History+'20261026000000_Unknown');SchemaSha256=$projection.SchemaSha256;AuditSingletonCount=1;RequiredFormat=2},
 [pscustomobject]@{Identity=$baseline.Identity;History=$complete.History;SchemaSha256=('F'*64);AuditSingletonCount=1;RequiredFormat=2}
)){Expect-Refusal {Assert-SourceJournalComposedApplicationSchema -JournalState $complete -ProjectionState $bad}}
$baseProjection=[pscustomobject]@{Identity=$baseline.Identity;History=$base;SchemaSha256=$projection.SchemaSha256;AuditSingletonCount=1;RequiredFormat=1}
Assert-SourceJournalComposedApplicationSchema -JournalState $baseline -ProjectionState $baseProjection
Expect-Refusal {Assert-SegmentedIndexApplicationSchema $projection} # The original contract still rejects unknown successors.
# Exercise the canonical updater's preparation function, with SQL observations
# supplied by these fixtures. No production configuration or SQL is accessed.
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Canonical updater parse failed.'}
$definition=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and
 $node.Name -ceq 'Get-SourceDeletionCleanupCandidateDatabase'},$false))
if($definition.Count -ne 1){throw 'Canonical candidate admission function missing.'}
Invoke-Expression $definition[0].Extent.Text
function Get-DeploymentSqlConnectionString {'synthetic-never-connect'}
function Get-SegmentedIndexDatabaseState {param($ConnectionString);$projection}
function Get-SourceJournalDatabaseState {param($ConnectionString);$complete}
$candidateState=[pscustomobject]@{Identity='legacy-id-and-create-time';IncarnationIdentity=$complete.Identity;History=$complete.History;SchemaValid=$true}
$observed=Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {$candidateState}
Assert-True ($observed.Identity -ceq 'legacy-id-and-create-time' -and $observed.IncarnationIdentity -ceq $complete.Identity) 'Canonical candidate preparation changed legacy identity or rejected the composed incarnation.'
$savedCandidate=$candidateState
foreach($incarnation in @('different-incarnation',$null)){
 $candidateState=[pscustomobject]@{Identity=$savedCandidate.Identity;IncarnationIdentity=$incarnation;History=$complete.History;SchemaValid=$true}
 Expect-Refusal {Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {$candidateState}}
}
$candidateState=[pscustomobject]@{Identity=$savedCandidate.Identity;History=$complete.History;SchemaValid=$true}
Expect-Refusal {Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {$candidateState}}
$candidateState=$savedCandidate
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalUpdateAdmission-'+[guid]::NewGuid().ToString('N'))
$recovery=Join-Path $area 'Recovery';$hold=Join-Path $area 'hold.json'
$releaseId='20261025T170000Z-6fbd224186ab';$release=Join-Path $recovery $releaseId
$null=New-Item -ItemType Directory -Path $release
try{
 Assert-True (!(Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity)) 'Absent hold invented an intent.'
 [IO.File]::WriteAllText($hold,($releaseId|ConvertTo-Json -Compress))
 Assert-True (!(Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity)) 'An ordinary hold invented an intent.'
 $state=New-SourceJournalMigrationState -DatabaseState $baseline -ReceiptDirectory $release -ReleaseId $releaseId -SqlSha256 $contract.UpSha256
 Expect-Refusal {Invoke-SourceJournalMigrationAttempt -State $state -ReadState {$baseline} -AssertStopped {} -RunUp {throw 'owned interruption'}}
 $intent=Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity
 Assert-True ($intent -ceq $state.IntentPath) 'Fresh recovery did not locate the durable intent from its hold.'
 [IO.File]::WriteAllText($hold,(@{version=1;releaseId=$releaseId;corpusRebuildOperationId=[guid]::NewGuid().ToString('D')}|ConvertTo-Json -Compress))
 Assert-True ((Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity) -ceq $state.IntentPath) 'The existing scoped rebuild hold lost its source recovery locator.'
 [IO.File]::WriteAllText($hold,($releaseId|ConvertTo-Json -Compress))
 Expect-Refusal {Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity 'different-incarnation'}
 $original=[IO.File]::ReadAllBytes($state.IntentPath)
 [IO.File]::WriteAllText($state.IntentPath,'{broken')
 Expect-Refusal {Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity}
 [IO.File]::WriteAllBytes($state.IntentPath,$original)
 $locked=[IO.File]::Open($state.IntentPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
 try{Expect-Refusal {Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity}}finally{$locked.Dispose()}
 $alias=Join-Path $area 'LinkedRecovery'
 $null=New-Item -ItemType Junction -Path $alias -Target $recovery
 try{Expect-Refusal {Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $alias -ExpectedDatabaseIdentity $baseline.Identity}}
 finally{Remove-Item -LiteralPath $alias -Force}
 foreach($invalid in @('../outside','20261025T170000Z-6fbd224186ab/../outside','not-a-release')){
  [IO.File]::WriteAllText($hold,($invalid|ConvertTo-Json -Compress))
  Expect-Refusal {Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity}
 }
 [IO.File]::WriteAllText($hold,($releaseId|ConvertTo-Json -Compress))
 $receipt=[Text.Encoding]::UTF8.GetString($original)|ConvertFrom-Json -AsHashtable
 $receipt.ReleaseId='20261025T170000Z-aaaaaaaaaaaa'
 [IO.File]::WriteAllText($state.IntentPath,($receipt|ConvertTo-Json -Depth 12))
 Expect-Refusal {Get-SourceJournalHeldMigrationIntent -HoldPath $hold -RecoveryRoot $recovery -ExpectedDatabaseIdentity $baseline.Identity}
 Write-Output 'Source journal composed schema and held-intent recovery passed; zero production operations.'
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalUpdateAdmission-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe admission fixture cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
