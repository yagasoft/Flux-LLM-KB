[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ReceiptDirectory,[Parameter(Mandatory)][string]$WebEntryPath)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
foreach($module in @('incremental-source-journal-migration','incremental-source-journal-helper','incremental-source-access-config',
 'incremental-segmented-index-schema','incremental-hybrid-passage-rebuild','incremental-source-deletion-cleanup-migration','index-format-admission')){
 Import-Module (Join-Path $SourceRoot ('scripts/deploy/'+$module+'.psm1')) -Force
}
$testConnection=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_JOURNAL_DISPOSABLE_SQL') -replace '(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount='
$builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($testConnection)
if($builder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$'){throw 'Composed admission requires the generated disposable catalogue.'}
$builder['Pooling']=$false;$testConnection=$builder.ConnectionString
$receipt=[IO.Path]::GetFullPath($ReceiptDirectory)
if(!$receipt.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalMigration-',[StringComparison]::OrdinalIgnoreCase)){throw 'Composed admission must use the isolated migration fixture.'}
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action,[string]$Reason){$failure='';try{&$Action}catch{$failure=$_.Exception.Message};Assert-True ($failure -like $Reason) ('Wrong admission refusal: '+$failure)}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Canonical updater parse failed.'}
foreach($name in @('Invoke-DeploymentIndexAdmission','Get-DeploymentSourceJournalBoundary','Get-DeploymentSourceJournalPayloads',
 'Invoke-DeploymentSourceJournalAdmission','Get-SourceDeletionCleanupCandidateDatabase')){
 $nodes=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$false))
 if($nodes.Count -ne 1){throw ('Missing canonical composition: '+$name)}
 Invoke-Expression $nodes[0].Extent.Text
}
function Get-DeploymentSqlConnectionString {$testConnection}
function Set-FixtureIndexFormat([int]$Format){
 $connection=[Data.SqlClient.SqlConnection]::new($testConnection)
 try{$connection.Open();$command=$connection.CreateCommand();try{$command.CommandTimeout=10;$command.CommandText='UPDATE dbo.IndexState SET RequiredIndexFormat=@format WHERE Id=1;';$null=$command.Parameters.AddWithValue('@format',$Format);Assert-True ($command.ExecuteNonQuery() -eq 1) 'Fixture publication singleton missing.'}finally{$command.Dispose()}}finally{$connection.Dispose()}
}
$area=Join-Path $receipt ('Composed-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
$CanonicalLiveRoot=$area;$CanonicalDeployRoot=Join-Path $area 'Web';$SourceJournalHelperRoot=Join-Path $area 'helper';$InteractiveHostRoot=Join-Path $area 'Outlook'
$ValidationHoldPath=Join-Path $area 'hold.json';$IncrementalRecoveryRoot=Join-Path $area 'Recovery'
$sourceJournalMigrationState=$null;$sourceJournalHelperState=$null;$ReviewedLegacyIndexPayloads=@();$box=@{Actions=0}
$originalFormat=$null
try{
 $null=New-Item -ItemType Directory -Path (Join-Path $area 'Config'),$CanonicalDeployRoot,(Join-Path $SourceJournalHelperRoot 'payload'),$InteractiveHostRoot
 [IO.File]::Copy($WebEntryPath,(Join-Path $CanonicalDeployRoot 'FluxKnowledge.Web.dll'))
 # Inner journal admission binds these fixture files; the outer Web metadata
 # reader uses the real built Web assembly. No executable is launched.
 [IO.File]::WriteAllText((Join-Path $SourceJournalHelperRoot 'payload/FluxKnowledge.Cli.dll'),'owned CLI binding fixture')
 [IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'FluxKnowledge.OutlookHost.dll'),'owned Outlook binding fixture')
 $configurationPath=Join-Path $area 'Config/appsettings.Production.json'
 [IO.File]::WriteAllText($configurationPath,'{"SourceJournal":{"Enabled":true,"SealingEnabled":false},"IndexPublication":{"Mode":"Legacy"}}')
 $journal=Get-SourceJournalDatabaseState $testConnection
 $projection=Get-SegmentedIndexDatabaseState $testConnection
 Assert-SourceJournalComposedApplicationSchema -JournalState $journal -ProjectionState $projection
 $candidate=Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false `
  -ReadState {Get-SourceDeletionCleanupDatabaseState -ConnectionString $testConnection}
 $legacy=Get-SourceDeletionCleanupDatabaseState -ConnectionString $testConnection
 Assert-True ($candidate.Identity -ceq $legacy.Identity -and $candidate.IncarnationIdentity -ceq $journal.Identity) 'Canonical candidate changed the legacy identity or composed another database.'
 foreach($incarnation in @('different-incarnation',$null)){
  $wrong=$candidate.PSObject.Copy();$wrong.IncarnationIdentity=$incarnation
  Expect-Refusal {Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {$wrong}} '*Source journal schema changed during candidate admission*'
 }
 $wrong=$candidate.PSObject.Copy();$wrong.PSObject.Properties.Remove('IncarnationIdentity')
 Expect-Refusal {Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {$wrong}} '*Source journal schema changed during candidate admission*'
 $originalFormat=$projection.RequiredFormat;$ExpectedSourceJournalDatabaseIdentity=$journal.Identity
 $ReviewedSourceJournalPayloads=@(Get-DeploymentSourceJournalPayloads|ForEach-Object {$_.Role+'='+(Get-HybridPayloadFingerprint $_.Root)})
 Set-FixtureIndexFormat 1
 Invoke-DeploymentIndexAdmission -Roles @('Web') -Action {$box.Actions++}
 Assert-True ($box.Actions -eq 1) 'Actual nested format/journal SQL fences did not admit the bound Web action.'
 $saved=@($ReviewedSourceJournalPayloads)
 $ReviewedSourceJournalPayloads=@($saved|ForEach-Object {if($_ -cmatch '^Cli='){'Cli='+('F'*64)}else{$_}})
 Expect-Refusal {Invoke-DeploymentIndexAdmission -Roles @('Web') -Action {$box.Actions++}} 'journal-schema-unreviewed-payload-start-forbidden'
 $ReviewedSourceJournalPayloads=$saved;$ExpectedSourceJournalDatabaseIdentity='wrong-incarnation'
 Expect-Refusal {Invoke-DeploymentIndexAdmission -Roles @('Web') -Action {$box.Actions++}} 'journal-schema-reviewed-database-binding-required'
 $ExpectedSourceJournalDatabaseIdentity=$journal.Identity
 Set-FixtureIndexFormat 2
 Expect-Refusal {Invoke-DeploymentIndexAdmission -Roles @('Web') -Action {$box.Actions++}} 'index-admission-segmented-publication-disabled'
 [IO.File]::WriteAllText($configurationPath,'{"SourceJournal":{"Enabled":true,"SealingEnabled":false},"IndexPublication":{"Mode":"Segmented"}}')
 Invoke-DeploymentIndexAdmission -Roles @('Web') -Action {$box.Actions++}
 Assert-True ($box.Actions -eq 2) 'Format-two composition failed or a refused action executed.'
 [pscustomobject]@{Result='Passed';ActualSql=$true;RealWebMetadata=$true;Formats=@(1,2);RejectedBindings=@('CLI hash','database incarnation','segmented mode');ProductionCalls=0}|ConvertTo-Json
}finally{
 if($null -ne $originalFormat){Set-FixtureIndexFormat $originalFormat}
 $resolved=[IO.Path]::GetFullPath($area)
 if(![IO.Path]::GetDirectoryName($resolved).Equals([IO.Path]::TrimEndingDirectorySeparator($receipt),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe composed admission cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
