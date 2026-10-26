[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
function Assert-True([bool]$Value,[string]$Message) {if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action) {
 $refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused ('Expected migration refusal was not observed: '+$Action.ToString())
}
$contract=Get-SourceJournalMigrationContract
# The initial migration has no timestamp in its filename. EF's attribute, rather
# than filenames, is the migration identity used by the actual SQL history.
$base=@(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File |
 ForEach-Object {[regex]::Matches([IO.File]::ReadAllText($_.FullName),'\[Migration\("(\d{14}_[^"]+)"\)\]') | ForEach-Object {$_.Groups[1].Value}} |
 Where-Object {$_ -cle $contract.Baseline} | Sort-Object)
$baseline=[pscustomobject]@{Identity='owned-database-incarnation';History=$base;SchemaSha256=$contract.BaselineSchemaSha256;CanAlter=$true}
$complete=[pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations));SchemaSha256=$contract.SchemaSha256;CanAlter=$true}
Assert-SourceJournalSchema -DatabaseState $baseline -AllowBaseline
Assert-SourceJournalSchema -DatabaseState $complete
Expect-Refusal {Assert-SourceJournalSchema -DatabaseState $baseline}
foreach($bad in @(
 [pscustomobject]@{Identity=$baseline.Identity;History=($base+@($contract.Migrations)[0]);SchemaSha256=$contract.SchemaSha256;CanAlter=$true},
 [pscustomobject]@{Identity=$baseline.Identity;History=$complete.History;SchemaSha256=('F'*64);CanAlter=$true},
 [pscustomobject]@{Identity=$baseline.Identity;History=($complete.History+'20261026000000_Unknown');SchemaSha256=$contract.SchemaSha256;CanAlter=$true},
 [pscustomobject]@{Identity=$baseline.Identity;History=$base;SchemaSha256=$contract.SchemaSha256;CanAlter=$true},
 [pscustomobject]@{Identity=$baseline.Identity;History=@($base|Select-Object -Skip 1);SchemaSha256=$contract.BaselineSchemaSha256;CanAlter=$true}
)){Expect-Refusal {Assert-SourceJournalSchema -DatabaseState $bad -AllowBaseline}}
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalMigrationContract-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
try {
 $case=[ordered]@{Current=$baseline;UpCalls=0;IntentObserved=$false;Starts=0}
 $state=New-SourceJournalMigrationState -DatabaseState $baseline -ReceiptDirectory $area -ReleaseId 'owned-release' -SqlSha256 $contract.UpSha256
 Invoke-SourceJournalMigrationAttempt -State $state -ReadState {$case.Current} -AssertStopped {} -RunUp {
  $case.IntentObserved=Test-Path -LiteralPath $state.IntentPath -PathType Leaf
  $case.UpCalls++;$case.Current=$complete
 }
 Assert-True ($case.IntentObserved -and $state.Applied -and $case.UpCalls -eq 1) 'Durable intent did not precede the one additive attempt.'
 Expect-Refusal {Invoke-SourceJournalMigrationAttempt -State $state -ReadState {$case.Current} -AssertStopped {} -RunUp {$case.UpCalls++}}
 Expect-Refusal {Assert-SourceJournalPredecessorStart -DatabaseState $baseline -MigrationState $state}
 Expect-Refusal {Assert-SourceJournalPredecessorStart -DatabaseState $complete}
 Assert-SourceJournalPredecessorStart -DatabaseState $baseline
 $failedArea=Join-Path $area 'failed';$null=New-Item -ItemType Directory -Path $failedArea
 $failed=New-SourceJournalMigrationState -DatabaseState $baseline -ReceiptDirectory $failedArea -ReleaseId 'failed-release' -SqlSha256 $contract.UpSha256
 Expect-Refusal {Invoke-SourceJournalMigrationAttempt -State $failed -ReadState {$baseline} -AssertStopped {} -RunUp {throw 'owned interrupted migration'}}
 Assert-True ($failed.Attempted -and !$failed.Applied -and (Test-Path -LiteralPath $failed.IntentPath)) 'Interrupted migration lost its intent.'
 Expect-Refusal {Assert-SourceJournalPredecessorStart -DatabaseState $baseline -MigrationState $failed}
 $driftArea=Join-Path $area 'drift';$null=New-Item -ItemType Directory -Path $driftArea
 $drift=New-SourceJournalMigrationState -DatabaseState $baseline -ReceiptDirectory $driftArea -ReleaseId 'drift-release' -SqlSha256 $contract.UpSha256
 $other=[pscustomobject]@{Identity='other-incarnation';History=$base;SchemaSha256=$contract.BaselineSchemaSha256;CanAlter=$true}
 Expect-Refusal {Invoke-SourceJournalMigrationAttempt -State $drift -ReadState {$other} -AssertStopped {} -RunUp {throw 'must not execute'}}
 Assert-True (!$drift.Attempted) 'Database identity drift reached mutation intent.'
 $payloads=@('Web','Cli','OutlookHost'|ForEach-Object {
  $role=$_;$root=Join-Path $area $role;$null=New-Item -ItemType Directory -Path $root
  $entry=Join-Path $root ('FluxKnowledge.'+$role+'.dll');[IO.File]::WriteAllText($entry,'owned compatible '+$role)
  @{Role=$role;Root=$root;Sha256=(Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash}
 })
 $current=@($payloads|ForEach-Object {@{Role=$_.Role;Root=$_.Root}})
 $fingerprint={param($root) (Get-FileHash -LiteralPath (Join-Path $root ('FluxKnowledge.'+[IO.Path]::GetFileName($root)+'.dll')) -Algorithm SHA256).Hash}
 Invoke-SourceJournalApplicationAdmission -ReadState {$complete} -ExpectedDatabaseIdentity $complete.Identity -ReviewedPayloads $payloads -CurrentPayloads $current -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}
 Assert-True ($case.Starts -eq 1) 'The reviewed current payload set was not admitted.'
 $wrong=@($payloads|ForEach-Object {$_.Clone()});$wrong[1].Sha256='F'*64
 Expect-Refusal {Invoke-SourceJournalApplicationAdmission -ReadState {$complete} -ExpectedDatabaseIdentity $complete.Identity -ReviewedPayloads $wrong -CurrentPayloads $current -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}}
 Expect-Refusal {Invoke-SourceJournalApplicationAdmission -ReadState {$complete} -ExpectedDatabaseIdentity $complete.Identity -ReviewedPayloads @() -CurrentPayloads $current -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}}
 Expect-Refusal {Invoke-SourceJournalApplicationAdmission -ReadState {$complete} -ReviewedPayloads $payloads -CurrentPayloads $current -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}}
 Expect-Refusal {Invoke-SourceJournalApplicationAdmission -ReadState {$baseline} -MigrationState $state -ReviewedPayloads $payloads -CurrentPayloads $current -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}}
 $recovered=$failed.Clone();$recovered.Attempted=$false # Simulate loss of the in-memory flag after durable intent.
 Expect-Refusal {Invoke-SourceJournalApplicationAdmission -ReadState {$baseline} -MigrationState $recovered -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}}
 $case.Reads=0
 Expect-Refusal {Invoke-SourceJournalApplicationAdmission -ReadState {
   $case.Reads++;if($case.Reads -eq 1){$complete}else{[pscustomobject]@{Identity='changed';History=$complete.History;SchemaSha256=$contract.SchemaSha256}}
  } -ExpectedDatabaseIdentity $complete.Identity -ReviewedPayloads $payloads -CurrentPayloads $current -ReadPayloadFingerprint $fingerprint -Action {$case.Starts++}}
 Assert-True ($case.Starts -eq 1 -and $case.Reads -eq 2) 'Changed payload/schema evidence reached a start callback.'
 Write-Output 'Journal migration contracts passed; no production calls.'
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalMigrationContract-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe owned fixture cleanup'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
