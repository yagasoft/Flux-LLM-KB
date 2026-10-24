param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ExpectedDatabase)
$ErrorActionPreference='Stop'
$text=$env:FLUXKNOWLEDGE_OFFLINE_CODE_TEST_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount='
$target=[Data.SqlClient.SqlConnectionStringBuilder]::new($text)
if($target.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Owned disposable database required.'}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-schema.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/offline-code-maintenance.ps1')
function Get-DeploymentSqlConnectionString {$target.ConnectionString}
function Sql([string]$Text){$connection=[Data.SqlClient.SqlConnection]::new($target.ConnectionString);try{$connection.Open();$command=$connection.CreateCommand();try{$command.CommandTimeout=3;$command.CommandText=$Text;$command.ExecuteScalar()}finally{$command.Dispose()}}finally{$connection.Dispose()}}
$before=Get-OfflineCodeDatabaseBinding
if($before.RequiredFormat -ne 2 -or $before.Identity -notlike ('*|'+$ExpectedDatabase+'|*') -or !$before.SchemaSha256 -or !$before.Projection){throw 'Actual schema/current-generation binding is incomplete.'}
if((Get-OfflineCodeDatabaseBinding).Projection -cne $before.Projection){throw 'Read-only binding changed the projection.'}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
$tokens=$null;$errors=$null
$updater=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Actual updater did not parse.' }
$candidate=$updater.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SourceDeletionCleanupCandidateDatabase'},$true)
. ([scriptblock]::Create($candidate.Extent.Text))
[void](Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {Get-SourceDeletionCleanupDatabaseState -ConnectionString $target.ConnectionString})
$actualSchema=Get-SegmentedIndexDatabaseState -ConnectionString $target.ConnectionString
foreach ($drift in @('unknown-successor','duplicate-successor','changed-index-schema')) {
    $changed=$actualSchema.PSObject.Copy()
    switch ($drift) {
        'unknown-successor' { $changed.History=@($actualSchema.History)+@('99999999999999_Unknown') }
        'duplicate-successor' { $changed.History=@($actualSchema.History)+@('20261024061418_AddDocumentOcrExecutionLifecycle') }
        'changed-index-schema' { $changed.SchemaSha256='F'*64 }
    }
    $refused=$false
    try { Assert-SegmentedIndexApplicationSchema $changed } catch { $refused=$_.Exception.Message -like 'index-schema-reconciliation-required*' }
    if (!$refused) { throw ('Unreviewed index schema/history accepted: '+$drift) }
}
[void](Sql 'UPDATE dbo.IndexState SET CorpusVersion=CorpusVersion+1 WHERE Id=1; SELECT 1;')
try {
    $refused=$false
    try{Get-OfflineCodeDatabaseBinding|Out-Null}catch{if($_.Exception.Message -notmatch 'current-projection-required'){throw};$refused=$true}
    if(!$refused){throw 'Publication lag was accepted as a current projection.'}
} finally {[void](Sql 'UPDATE dbo.IndexState SET CorpusVersion=CorpusVersion-1 WHERE Id=1; SELECT 1;')}
if((Get-OfflineCodeDatabaseBinding).Projection -cne $before.Projection){throw 'Disposable projection restoration failed.'}
[void](Sql 'UPDATE dbo.IndexGenerations SET RetiredAtUtc=SYSDATETIMEOFFSET() WHERE Id=(SELECT ActiveIndexGenerationId FROM dbo.IndexState WHERE Id=1); SELECT 1;')
try {
    $refused=$false
    try{Get-OfflineCodeDatabaseBinding|Out-Null}catch{if($_.Exception.Message -notmatch 'current-projection-required'){throw};$refused=$true}
    if(!$refused){throw 'A retired generation with a matching corpus stamp was accepted.'}
} finally {[void](Sql 'UPDATE dbo.IndexGenerations SET RetiredAtUtc=NULL WHERE Id=(SELECT ActiveIndexGenerationId FROM dbo.IndexState WHERE Id=1); SELECT 1;')}
Write-Output 'PASS: actual disposable SQL schema, incarnation, format2, generation/stamp and lag refusal.'
