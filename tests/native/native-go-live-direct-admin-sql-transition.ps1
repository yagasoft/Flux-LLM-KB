[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$ServerConnectionString = "",
    [switch]$ReproducePreFix
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$bootstrapPath = Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql'
$disposableSql = Join-Path $SourceRoot 'scripts\dev\ensure-disposable-sql.ps1'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function ConvertTo-SqlLiteral {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace("'", "''")
}

function ConvertTo-SqlIdentifier {
    param([Parameter(Mandatory = $true)][string]$Value)
    return "[$($Value.Replace(']', ']]'))]"
}

function Invoke-SqlNonQuery {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory = $true)][string]$CommandText
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        [void]$command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }
}

function Invoke-SqlScalar {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory = $true)][string]$CommandText
    )

    $command = $Connection.CreateCommand()
    try {
        $command.CommandTimeout = 120
        $command.CommandText = $CommandText
        return $command.ExecuteScalar()
    }
    finally {
        $command.Dispose()
    }
}

function Install-CanonicalBootstrapProcedures {
    param(
        [Parameter(Mandatory = $true)][System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory = $true)][string]$Bootstrap,
        [Parameter(Mandatory = $true)][string]$DatabaseName,
        [Parameter(Mandatory = $true)][string]$LoginName,
        [Parameter(Mandatory = $true)][string]$DataFile,
        [Parameter(Mandatory = $true)][string]$LogFile,
        [Parameter(Mandatory = $true)][hashtable]$Procedures,
        [Parameter(Mandatory = $true)][string]$BootstrapLogin,
        [switch]$ReproducePreFix
    )

    # The canonical script is used verbatim apart from disposable names, files and
    # the SQL-auth test login that stands in for the fixed Windows app-pool login.
    $script = $Bootstrap -replace '(?m)^:On Error exit\r?\n', '' -replace '(?m)^:setvar .*\r?\n', ''
    $script = $script -replace "(?ms)IF SUSER_ID\(N'IIS AppPool\\FluxKnowledge'\) IS NULL\s*\r?\n\s*CREATE LOGIN \[IIS AppPool\\FluxKnowledge\] FROM WINDOWS;\s*\r?\n", ''
    $script = $script.Replace('IIS AppPool\FluxKnowledge', $LoginName)
    foreach ($name in $Procedures.Keys) {
        $script = $script.Replace($name, $Procedures[$name])
    }
    $script = $script.Replace('I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf', $DataFile)
    $script = $script.Replace('I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf', $LogFile)
    $script = $script.Replace('FluxKnowledge', $DatabaseName)
    $script = $script.Replace('$(NativeGoLiveBootstrapLogin)', (ConvertTo-SqlLiteral $BootstrapLogin))
    # The final sqlcmd invocation guard is exercised by the closeout bootstrap
    # contract. This disposable run already owns an authenticated local session.
    $script = $script -replace "(?ms)DECLARE @BootstrapLogin sysname = N'.*?';\s*IF @BootstrapLogin=.*?THROW 51000, 'native-go-live-bootstrap-login-missing', 1;\s*", ''

    if ($ReproducePreFix) {
        # Faithful predecessor: Create leaves the app-pool database user in place,
        # and Manage attempts the ownership transfer without removing that user.
        $script = $script.Replace(
            '    EXEC sys.sp_executesql @CreateDatabase;',
            "    EXEC sys.sp_executesql @CreateDatabase;`r`n    EXEC(N'USE $(ConvertTo-SqlIdentifier $DatabaseName); CREATE USER $(ConvertTo-SqlIdentifier $LoginName) FOR LOGIN $(ConvertTo-SqlIdentifier $LoginName);');")
    }

    $batchNumber = 0
    foreach ($batch in ($script -split '(?im)^GO\s*$')) {
        $batchNumber++
        if (-not [string]::IsNullOrWhiteSpace($batch)) {
            try {
                Invoke-SqlNonQuery -Connection $Connection -CommandText $batch
            }
            catch {
                throw "Canonical bootstrap batch $batchNumber failed: $($_.Exception.Message)"
            }
        }
    }
}

Assert-True (Test-Path -LiteralPath $bootstrapPath -PathType Leaf) 'Native bootstrap SQL is missing.'
Assert-True (Test-Path -LiteralPath $disposableSql -PathType Leaf) 'Disposable SQL helper is missing.'
$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw
$connectionString = if ([string]::IsNullOrWhiteSpace($ServerConnectionString)) {
    & pwsh -NoProfile -File $disposableSql
} else {
    & pwsh -NoProfile -File $disposableSql -ServerConnectionString $ServerConnectionString
}
Assert-True ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($connectionString)) `
    'Disposable SQL server could not be prepared.'

Add-Type -AssemblyName System.Data
$runId = [Guid]::NewGuid().ToString('N')
$databaseName = "NativeGoLiveTransition_$runId"
$loginName = "NativeGoLiveTransitionLogin_$runId"
$procedures = @{
    'FluxKnowledgeNativeGoLiveCreate' = "NativeGoLiveTransitionCreate_$runId"
    'FluxKnowledgeNativeGoLiveDrop' = "NativeGoLiveTransitionDrop_$runId"
    'FluxKnowledgeNativeGoLiveManageAppPool' = "NativeGoLiveTransitionManage_$runId"
    'FluxKnowledgeNativeGoLiveObserveAppPool' = "NativeGoLiveTransitionObserve_$runId"
}
$databaseQuoted = ConvertTo-SqlIdentifier $databaseName
$loginQuoted = ConvertTo-SqlIdentifier $loginName
$temporaryPassword = "A!$runId`z"
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "NativeGoLiveTransition_$runId"
$dataFile = Join-Path $temporaryDirectory "$databaseName.mdf"
$logFile = Join-Path $temporaryDirectory "$databaseName`_log.ldf"
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
try {
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    $connection.Open()
    $bootstrapLogin = [string](Invoke-SqlScalar -Connection $connection -CommandText 'SELECT ORIGINAL_LOGIN();')
    Assert-True (-not [string]::IsNullOrWhiteSpace($bootstrapLogin)) 'Disposable SQL server did not expose the trusted bootstrap login.'

    Invoke-SqlNonQuery -Connection $connection -CommandText "CREATE LOGIN $loginQuoted WITH PASSWORD=N'$(ConvertTo-SqlLiteral $temporaryPassword)', CHECK_POLICY=OFF, CHECK_EXPIRATION=OFF;"
    Install-CanonicalBootstrapProcedures -Connection $connection -Bootstrap $bootstrap -DatabaseName $databaseName `
        -LoginName $loginName -DataFile $dataFile -LogFile $logFile -Procedures $procedures `
        -BootstrapLogin $bootstrapLogin -ReproducePreFix:$ReproducePreFix

    $createProcedure = ConvertTo-SqlIdentifier $procedures['FluxKnowledgeNativeGoLiveCreate']
    $manageProcedure = ConvertTo-SqlIdentifier $procedures['FluxKnowledgeNativeGoLiveManageAppPool']
    $invocationPrelude = "DECLARE @TransitionAppPoolSid varbinary(85)=SUSER_SID(N'$(ConvertTo-SqlLiteral $loginName)');"
    Invoke-SqlNonQuery -Connection $connection -CommandText "$invocationPrelude EXEC dbo.$createProcedure @Catalogue=N'$(ConvertTo-SqlLiteral $databaseName)', @DataFile=N'$(ConvertTo-SqlLiteral $dataFile)', @LogFile=N'$(ConvertTo-SqlLiteral $logFile)', @AppPoolLogin=N'$(ConvertTo-SqlLiteral $loginName)', @AppPoolSid=@TransitionAppPoolSid;"
    Invoke-SqlNonQuery -Connection $connection -CommandText "$invocationPrelude EXEC dbo.$manageProcedure @Catalogue=N'$(ConvertTo-SqlLiteral $databaseName)', @AppPoolLogin=N'$(ConvertTo-SqlLiteral $loginName)', @AppPoolSid=@TransitionAppPoolSid, @BootstrapLogin=N'$(ConvertTo-SqlLiteral $bootstrapLogin)';"

    if (-not $ReproducePreFix) {
        $ownerAndSysAdmin = [string](Invoke-SqlScalar -Connection $connection -CommandText "SELECT CONCAT(CONVERT(varchar(170), owner_sid, 1), N'|', CONVERT(varchar(10), IS_SRVROLEMEMBER(N'sysadmin', N'$(ConvertTo-SqlLiteral $loginName)')), N'|', CONVERT(varchar(10), CASE WHEN DATABASE_PRINCIPAL_ID(N'$(ConvertTo-SqlLiteral $loginName)') IS NULL THEN 1 ELSE 0 END)) FROM sys.databases WHERE name=N'$(ConvertTo-SqlLiteral $databaseName)';")
        $expectedOwner = [string](Invoke-SqlScalar -Connection $connection -CommandText "SELECT CONVERT(varchar(170), SUSER_SID(N'$(ConvertTo-SqlLiteral $loginName)'), 1);")
        Assert-True ($ownerAndSysAdmin -ceq "$expectedOwner|1|1") 'Canonical Create then Manage did not transfer ownership to the app-pool sysadmin without a named database user.'
    }
}
finally {
    if ($connection.State -eq [System.Data.ConnectionState]::Open) {
        try {
            Invoke-SqlNonQuery -Connection $connection -CommandText "USE master; IF DB_ID(N'$(ConvertTo-SqlLiteral $databaseName)') IS NOT NULL BEGIN ALTER DATABASE $databaseQuoted SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE $databaseQuoted; END;"
        }
        finally {
            foreach ($procedureName in $procedures.Values) {
                try { Invoke-SqlNonQuery -Connection $connection -CommandText "USE master; IF OBJECT_ID(N'dbo.$procedureName', N'P') IS NOT NULL DROP PROCEDURE dbo.$(ConvertTo-SqlIdentifier $procedureName);" } catch { }
            }
            try { Invoke-SqlNonQuery -Connection $connection -CommandText "USE master; IF SUSER_ID(N'$(ConvertTo-SqlLiteral $loginName)') IS NOT NULL DROP LOGIN $loginQuoted;" } catch { }
        }
    }
    $connection.Dispose()
    Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $ReproducePreFix) {
    Write-Output 'Native direct-admin canonical SQL ownership transition passed.'
}
