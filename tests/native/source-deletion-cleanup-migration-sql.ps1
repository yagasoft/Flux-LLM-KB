[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$SqlPath)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-deletion-cleanup-migration.psm1') -Force
$text=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_SOURCE_CLEANUP_DISPOSABLE_SQL')
$text=[regex]::Replace($text,'(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=')
$text=[regex]::Replace($text,'(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount=')
$builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new($text)
if ($builder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$') { throw 'Migration SQL tests require a generated disposable catalogue.' }
$builder['Pooling']=$false; $text=$builder.ConnectionString
$contract=Get-SourceDeletionCleanupMigrationContract
if ((Get-FileHash -LiteralPath $SqlPath -Algorithm SHA256).Hash -cne $contract.UpSha256) { throw 'Migration SQL differs from the reviewed hash.' }
function Assert-True([bool]$Value,[string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action) { $failed=$false; try { & $Action } catch { $failed=$true }; Assert-True $failed 'Expected SQL refusal was not observed.' }
function Get-DeploymentSqlConnectionString { $text }
function Get-RepositoryRecoveryCompanionConnectionString { $text }
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count -ne 0) { throw 'The updater does not parse.' }
foreach ($name in @('Invoke-GeneratedSqlScript','Get-SourceDeletionCleanupCandidateDatabase','Assert-SourceDeletionCleanupCompanionDatabase')) {
    $function=$ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true)
    Invoke-Expression $function.Extent.Text
}
function Invoke-TestSql([string]$Sql) {
    $connection=[System.Data.SqlClient.SqlConnection]::new($text)
    try {
        $connection.Open()
        foreach ($batch in [regex]::Split($Sql,'(?im)^\s*GO\s*(?:--.*)?$') | Where-Object { ![string]::IsNullOrWhiteSpace($_) }) {
            $command=$connection.CreateCommand()
            try { $command.CommandText=$batch; [void]$command.ExecuteNonQuery() } finally { $command.Dispose() }
        }
    } finally { $connection.Dispose() }
}
& pwsh -NoProfile -File (Join-Path $SourceRoot 'tests/native/source-deletion-cleanup-updater.ps1') -SourceRoot $SourceRoot -SqlPath $SqlPath
if ($LASTEXITCODE -ne 0) { throw 'Actual updater recovery callbacks failed.' }
Invoke-TestSql @'
INSERT dbo.SourceDeletionOperations
 (Id,SourceRootId,State,Phase,ReasonCode,PipelineRecordCount,SourceArtifactCount,SharedArtifactCount,CreatedAtUtc,UpdatedAtUtc)
VALUES ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222',4,N'attention',N'source-delete-cleanup-failed',11,0,0,SYSUTCDATETIME(),SYSUTCDATETIME());
'@
$before=Get-SourceDeletionCleanupDatabaseState $text
Assert-SourceDeletionCleanupMigrationBaseline $before
Assert-SourceDeletionCleanupCompanionDatabase -Expected $before
Expect-Failure { Assert-SourceDeletionCleanupCompanionDatabase -Expected ([pscustomobject]@{ Identity='a-different-database' }) }
$sql=Get-Content -LiteralPath $SqlPath -Raw
Expect-Failure { Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState { Get-SourceDeletionCleanupDatabaseState $text } }
$faultPath=$SqlPath+'.interrupted'
try {
    $nextColumn=$sql.IndexOf('ALTER TABLE [SourceDeletionOperations] ADD [LastProgressAtUtc]',[StringComparison]::Ordinal)
    $cut=$sql.LastIndexOf('IF NOT EXISTS (',$nextColumn,[StringComparison]::Ordinal)
    Assert-True ($cut -gt 0) 'Reviewed SQL has no transaction failure boundary.'
    [IO.File]::WriteAllText($faultPath,$sql.Substring(0,$cut)+"`nTHROW 51000, 'injected failure after first column before history/commit', 1;")
    Expect-Failure { Invoke-GeneratedSqlScript -Path $faultPath }
} finally { if (Test-Path -LiteralPath $faultPath) { Remove-Item -LiteralPath $faultPath } }
$rolledBack=Get-SourceDeletionCleanupDatabaseState $text
Assert-True ($rolledBack.ColumnCount -eq 0 -and $rolledBack.CheckCount -eq 0 -and
    ($before.History -join ',') -ceq ($rolledBack.History -join ',') -and $before.LegacySha256 -ceq $rolledBack.LegacySha256) 'Interrupted SQL did not atomically preserve predecessor schema/history/attention.'
$receiptRoot=Join-Path ([IO.Path]::GetTempPath()) ('flux-source-sql-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $receiptRoot | Out-Null
try {
    $state=New-SourceDeletionCleanupMigrationState -DatabaseState $before -ReceiptDirectory $receiptRoot `
        -Binding @{ ReleaseId='sql-test'; Commit=('a'*40); WebPayloadSha256=('B'*64); CompanionPayloadSha256=('C'*64); SqlSha256=$contract.UpSha256 }
    $read={ Get-SourceDeletionCleanupDatabaseState $text }
    try { Invoke-SourceDeletionCleanupMigrationAttempt -State $state -ReadState $read -RunUp { Invoke-GeneratedSqlScript -Path $SqlPath } }
    catch { throw "Migration validation: schema SHA256=$((& $read).SchemaSha256). $($_.Exception.Message)" }
    Assert-True $state.Applied 'Reviewed SQL did not establish its complete target.'
    $eligible=Get-SourceDeletionCleanupCandidateDatabase -Root $SourceRoot -MigrationMode $false -ReadState $read
    Assert-True $eligible.SchemaValid 'App-only eligibility rejected the verified target.'
    Invoke-GeneratedSqlScript -Path $SqlPath
    Assert-SourceDeletionCleanupActivation -State $state -ReadState $read
    Expect-Failure { Assert-SourceDeletionCleanupMigrationBaseline (& $read) }
    foreach ($change in @(
        @{ Up='ALTER TABLE dbo.SourceDeletionOperations NOCHECK CONSTRAINT CK_SourceDeletionOperations_MembershipRowsDeleted;'; Down='ALTER TABLE dbo.SourceDeletionOperations WITH CHECK CHECK CONSTRAINT CK_SourceDeletionOperations_MembershipRowsDeleted;' },
        @{ Up='ALTER TABLE dbo.SourceDeletionOperations ALTER COLUMN ResumePhase nvarchar(64) COLLATE Latin1_General_100_CI_AS NULL;'; Down='ALTER TABLE dbo.SourceDeletionOperations ALTER COLUMN ResumePhase nvarchar(64) COLLATE Latin1_General_100_BIN2 NULL;' },
        @{ Up='ALTER TABLE dbo.SourceDeletionOperations ALTER COLUMN LastProgressAtUtc datetimeoffset(6) NULL;'; Down='ALTER TABLE dbo.SourceDeletionOperations ALTER COLUMN LastProgressAtUtc datetimeoffset(7) NULL;' },
        @{ Up="UPDATE dbo.SourceDeletionOperations SET State=0 WHERE Id='11111111-1111-1111-1111-111111111111';"; Down="UPDATE dbo.SourceDeletionOperations SET State=4 WHERE Id='11111111-1111-1111-1111-111111111111';" }
    )) {
        Invoke-TestSql $change.Up
        Expect-Failure { Assert-SourceDeletionCleanupActivation -State $state -ReadState $read }
        Invoke-TestSql $change.Down
    }
    # Updating State deliberately changes rowversion; recognise this as operation drift even after State is restored.
    Expect-Failure { Assert-SourceDeletionCleanupActivation -State $state -ReadState $read }
    $after=& $read
    Assert-True ($after.SchemaValid -and $after.ProgressIsInitial -and $after.RunnableCount -eq 0) 'Target contract was not restored.'
    Write-Output "Verified target schema: $($after.SchemaJson)"
    Write-Output "Disposable SQL passed: atomic interruption, attention preservation, exact target schema $($after.SchemaSha256), no replay and schema/operation drift refusal."
} finally {
    $resolved=(Resolve-Path -LiteralPath $receiptRoot).Path
    if (![string]::Equals($resolved,[IO.Path]::GetFullPath($receiptRoot),[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected SQL test receipt cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
