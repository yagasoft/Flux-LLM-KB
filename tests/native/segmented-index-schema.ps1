param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$ExpectedDatabase)
$ErrorActionPreference='Stop'
$normalised=$env:FLUXKNOWLEDGE_RECOVERY_REFERENCE_TEST_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$target=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
if($target.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$') { throw 'Not an owned disposable database.' }
$script:connection=$target.ConnectionString
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-schema.psm1') -Force
function Require([bool]$condition,[string]$reason){if(!$condition){throw $reason}}
function Sql([string]$text) {
    $sql=[Data.SqlClient.SqlConnection]::new($script:connection)
    try{$sql.Open();$command=$sql.CreateCommand();$command.CommandTimeout=3
        try{$command.CommandText=$text;return $command.ExecuteScalar()}finally{$command.Dispose()}
    }finally{$sql.Dispose()}
}
$original=Get-SegmentedIndexDatabaseState -ConnectionString $script:connection
Write-Output ('Baseline schema contract: '+$original.SchemaSha256)
Require ($original.History[-1] -ceq (Get-SegmentedIndexMigrationContract).Baseline) 'Incorrect isolated migration baseline.'
$script:observations=0;$script:failureMode='owner'
$quiescent={
    $script:observations++
    if($script:observations -eq 3){
        if($script:failureMode -ceq 'owner'){throw 'injected-owner-drift-after-first-ddl-batch'}
        $session=[int](Sql ("SELECT session_id FROM sys.dm_exec_sessions WHERE program_name=N'FluxKnowledge.SegmentedSchemaUpgrade' AND database_id=DB_ID() AND host_process_id="+[string]$PID+';'))
        if($session -le 0){throw 'Owned schema session not found.'}
        [void](Sql ('KILL '+[string]$session+';'))
    }
}
foreach($mode in @('owner','session')) {
    $script:observations=0;$script:failureMode=$mode;$refused=$false
    try{Invoke-SegmentedIndexSchemaUpgrade -ConnectionString $script:connection -ExpectedIdentity $original.Identity -ExpectedHistory $original.History -AssertQuiescent $quiescent | Out-Null}
    catch{if($mode -ceq 'owner' -and $_.Exception.Message -notlike '*injected-owner-drift*'){throw};$refused=$true}
    Require ($refused -and $script:observations -eq 3) 'Migration ignored the injected owner/session loss.'
    $rolledBack=Get-SegmentedIndexDatabaseState -ConnectionString $script:connection
    Require (($original.History -join '|') -ceq ($rolledBack.History -join '|')) 'Migration committed a partial history.'
    Require ($rolledBack.SchemaSha256 -ceq $original.SchemaSha256) 'Migration left partial schema after failure.'
}
$result=Invoke-SegmentedIndexSchemaUpgrade -ConnectionString $script:connection -ExpectedIdentity $original.Identity -ExpectedHistory $original.History -AssertQuiescent {}
Require (!$result.AlreadyApplied) 'Initial migration was not executed.'
$current=Get-SegmentedIndexDatabaseState -ConnectionString $script:connection
Assert-SegmentedIndexApplicationSchema $current
Require ($current.RequiredFormat -eq 1 -and $current.WriterCount -eq 0) 'Schema bootstrap changed projection mode or created a writer.'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
$tokens=$null;$errors=$null
$updater=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Actual updater did not parse.'}
$candidate=$updater.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Get-SourceDeletionCleanupCandidateDatabase'},$true)
Invoke-Expression $candidate.Extent.Text
function Get-DeploymentSqlConnectionString {$script:connection}
Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState {Get-SourceDeletionCleanupDatabaseState -ConnectionString $script:connection} | Out-Null
$replay=Invoke-SegmentedIndexSchemaUpgrade -ConnectionString $script:connection -ExpectedIdentity $original.Identity -ExpectedHistory $original.History -AssertQuiescent {}
Require $replay.AlreadyApplied 'A response-loss replay did not reconcile completed schema.'
try {
    [void](Sql 'ALTER TABLE dbo.IndexGenerations DROP COLUMN RetirementOperationId;')
    $refused=$false
    try{Assert-SegmentedIndexApplicationSchema (Get-SegmentedIndexDatabaseState -ConnectionString $script:connection)}catch{$refused=$true}
    Require $refused 'Schema admission accepted a missing retirement-intent column.'
} finally {[void](Sql 'ALTER TABLE dbo.IndexGenerations ADD RetirementOperationId uniqueidentifier NULL;')}
try {
    [void](Sql 'ALTER TABLE dbo.IndexGenerations ALTER COLUMN ReclaimedAtUtc datetimeoffset(6) NULL;')
    $refused=$false
    try{Assert-SegmentedIndexApplicationSchema (Get-SegmentedIndexDatabaseState -ConnectionString $script:connection)}catch{$refused=$true}
    Require $refused 'Schema admission accepted an altered retirement timestamp.'
} finally {[void](Sql 'ALTER TABLE dbo.IndexGenerations ALTER COLUMN ReclaimedAtUtc datetimeoffset(7) NULL;')}
function Set-WriterDefault([int]$value) {
    $constraint=[string](Sql "SELECT QUOTENAME(name) FROM sys.default_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.IndexRecoveryWriters') AND parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'dbo.IndexRecoveryWriters'),N'OwnerExitSufficient','ColumnId');")
    [void](Sql ('ALTER TABLE dbo.IndexRecoveryWriters DROP CONSTRAINT '+$constraint+'; ALTER TABLE dbo.IndexRecoveryWriters ADD DEFAULT CAST('+[string]$value+' AS bit) FOR OwnerExitSufficient;'))
}
try {
    Set-WriterDefault 1
    $refused=$false
    try{Assert-SegmentedIndexApplicationSchema (Get-SegmentedIndexDatabaseState -ConnectionString $script:connection)}catch{$refused=$true}
    Require $refused 'Schema admission accepted an unsafe writer default.'
} finally {Set-WriterDefault 0}
try {
    [void](Sql "INSERT dbo.__EFMigrationsHistory(MigrationId,ProductVersion) VALUES(N'99999999999999_Unknown',N'test');")
    $refused=$false
    try{Assert-SegmentedIndexApplicationSchema (Get-SegmentedIndexDatabaseState -ConnectionString $script:connection)}catch{$refused=$true}
    Require $refused 'Schema admission accepted unknown migration history.'
} finally {[void](Sql "DELETE dbo.__EFMigrationsHistory WHERE MigrationId=N'99999999999999_Unknown';")}
'PASS: exact atomic schema bootstrap, rollback, committed replay and altered-default refusal.'
