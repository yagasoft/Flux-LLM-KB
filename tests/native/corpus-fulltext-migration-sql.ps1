[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference = 'Stop'
if (!$SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-corpus-fulltext-migration.psm1') -Force
$contract = Get-CorpusFullTextMigrationContract
# Import the actual updater functions, without executing deployment or reading production configuration.
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Deployment script parse failed.' }
foreach ($name in @('Get-AppliedMigrationIds','Get-CorpusFullTextDatabaseState','New-CorpusFullTextMigrationScript','Invoke-GeneratedSqlScript')) {
    $definition = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
    if ($definition.Count -ne 1) { throw "Missing deployment function: $name" }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
$server = (& (Join-Path $SourceRoot 'scripts/dev/ensure-disposable-sql.ps1') | Out-String).Trim()
if ([string]::IsNullOrWhiteSpace($server)) { throw 'Disposable SQL prerequisite returned no connection.' }
$dbName = 'FluxKnowledgeTest_CorpusMigration_' + [guid]::NewGuid().ToString('N')
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($server)
$builder['Initial Catalog'] = $dbName
$script:testConnection = $builder.ConnectionString
function Get-DeploymentSqlConnectionString { return $script:testConnection }
function Invoke-TestSql([string]$ConnectionString, [string]$Sql) {
    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open(); $command = $connection.CreateCommand()
        try { $command.CommandText=$Sql; $command.CommandTimeout=120; [void]$command.ExecuteNonQuery() }
        finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}
$artifacts = Join-Path $SourceRoot 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$up = New-CorpusFullTextMigrationScript -SourceRoot $SourceRoot -Direction up -OutputPath (Join-Path $artifacts 'corpus-chunk-fulltext-up.sql')
$down = New-CorpusFullTextMigrationScript -SourceRoot $SourceRoot -Direction down -OutputPath (Join-Path $artifacts 'corpus-chunk-fulltext-down.sql')
$created = $false
try {
    Invoke-TestSql $server "CREATE DATABASE [$dbName];"
    $created = $true
    Invoke-TestSql $script:testConnection @"
CREATE TABLE dbo.TextChunks (Id bigint NOT NULL CONSTRAINT PK_TextChunks PRIMARY KEY, Content nvarchar(max) NOT NULL);
CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL);
INSERT dbo.__EFMigrationsHistory VALUES (N'$($contract.Baseline)',N'10.0.10');
"@
    Invoke-TestSql $script:testConnection 'CREATE FULLTEXT CATALOG [FluxKnowledge];'
    foreach ($partial in @($false,$true)) {
        $before = Get-CorpusFullTextDatabaseState
        Assert-CorpusFullTextMigrationBaseline $before
        $state = New-CorpusFullTextMigrationState -OriginalHistory $before.History
        try {
            Invoke-CorpusFullTextMigrationAttempt -State $state -ReadState { Get-CorpusFullTextDatabaseState } -RunUp {
                if ($partial) {
                    Invoke-TestSql $script:testConnection 'CREATE FULLTEXT INDEX ON dbo.TextChunks (Content LANGUAGE 1033) KEY INDEX PK_TextChunks ON FluxKnowledge WITH CHANGE_TRACKING AUTO;'
                    throw 'Injected failure before history insert.'
                }
                Invoke-GeneratedSqlScript -Path $up.Path
            }
            if ($partial) { throw 'Expected partial migration failure.' }
        } catch { if (!$partial -or $_.Exception.Message -cne 'Injected failure before history insert.') { throw } }
        Undo-CorpusFullTextMigrationAttempt -State $state -ReadState { Get-CorpusFullTextDatabaseState } -RunDown { Invoke-GeneratedSqlScript -Path $down.Path }
        if (!$state.SchemaRollbackVerified -or $state.RollbackVerified) { throw 'Schema recovery incorrectly released application hold.' }
        Assert-CorpusFullTextMigrationBaseline (Get-CorpusFullTextDatabaseState)
    }
    Write-Output 'Corpus Full-Text real SQL passed: exact pinned up/down, index metadata, complete and partial migration recovery.'
} finally {
    if ($created) {
        [System.Data.SqlClient.SqlConnection]::ClearAllPools()
        Invoke-TestSql $server "ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$dbName];"
    }
}
