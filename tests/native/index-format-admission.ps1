param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$WebAssembly,
    [Parameter(Mandatory)][string]$CliAssembly, [Parameter(Mandatory)][string]$ScratchRoot,
    [Parameter(Mandatory)][string]$ExpectedDatabase)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/index-format-admission.psm1') -Force
$normalised=$env:FLUXKNOWLEDGE_INDEX_ADMISSION_TEST_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$target=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
if($target.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$') { throw 'Not an owned disposable database.' }
$connection=$target.ConnectionString
function Sql([string]$text) {
    $sql=[Data.SqlClient.SqlConnection]::new($connection)
    try { $sql.Open(); $command=$sql.CreateCommand(); $command.CommandTimeout=2
        try { $command.CommandText=$text; return $command.ExecuteScalar() } finally { $command.Dispose() }
    } finally { $sql.Dispose() }
}
function Assert([bool]$condition,[string]$message) { if(!$condition) { throw $message } }
function Refuses([scriptblock]$work,[string]$reason) {
    try { & $work } catch { if($_.Exception.Message -like ('*'+$reason+'*')) { return }; throw }
    throw ('Expected refusal: '+$reason)
}
$fingerprint={param($root) Get-HybridPayloadFingerprint -Path $root}
$web=Join-Path $ScratchRoot 'web'; $cli=Join-Path $ScratchRoot 'cli'; $legacy=Join-Path $ScratchRoot 'legacy'
foreach($directory in @($web,$cli,$legacy)) { New-Item -ItemType Directory -Path $directory | Out-Null }
Copy-Item -LiteralPath $WebAssembly -Destination (Join-Path $web 'FluxKnowledge.Web.dll')
Copy-Item -LiteralPath $CliAssembly -Destination (Join-Path $cli 'FluxKnowledge.Cli.dll')
$bytes=[IO.File]::ReadAllBytes($WebAssembly)
$key=[Text.Encoding]::Latin1.GetString($bytes).IndexOf('FluxKnowledge.IndexFormats',[StringComparison]::Ordinal)
Assert ($key -ge 0) 'Build produced no entry capability.'
$bytes[$key]=[byte][char]'Z' # Synthetic unmarked predecessor; code is never executed.
[IO.File]::WriteAllBytes((Join-Path $legacy 'FluxKnowledge.Web.dll'),$bytes)
$legacyHash=& $fingerprint $legacy
$reviewed=@('Web='+$legacyHash)
$script:actions=0
$action={ $script:actions++ }
$webPayload=@(@{Role='Web';Root=$web})
function Admit([hashtable[]]$payloads,[scriptblock]$action,[string]$mode='Segmented',[string[]]$reviewed=@()) {
    Invoke-IndexFormatAdmission -ConnectionString $connection -Payloads $payloads -Action $action `
        -ReadPayloadFingerprint $fingerprint -WebIndexMode $mode -ReviewedLegacyPayloads $reviewed
}

[void](Sql 'UPDATE [IndexState] SET [RequiredIndexFormat]=2 WHERE [Id]=1;')
Admit $webPayload $action
Assert ($script:actions -eq 1) 'A declared compatible entry was not admitted.'
Admit @(@{Role='Cli';Root=$cli}) $action 'Legacy'
Assert ($script:actions -eq 2) 'A model-free compatible operator was not admitted.'
Refuses { Invoke-IndexFormatAdmission -ConnectionString $connection -Payloads @(@{Role='Cli';Root=$cli}) `
    -ReadPayloadFingerprint $fingerprint -OperationFormats @(1) -Action $action } 'operation-incompatible'
Refuses { Admit $webPayload $action 'Legacy' } 'segmented-publication-disabled'
Refuses { Admit @(@{Role='Web';Root=$legacy}) $action 'Segmented' $reviewed } 'payload-incompatible'
Refuses { Admit @(@{Role='Web';Root=$legacy}) $action 'Segmented' @('Cli='+$legacyHash) } 'payload-incompatible'
Assert ($script:actions -eq 2) 'An incompatible predecessor reached the start callback.'

# Both existing fences are held only during the immediate action.
Admit $webPayload {
    $result=Sql @'
DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'FluxKnowledge.DerivedIndexRecovery',
    @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0; SELECT @r;
'@
    Assert ([int]$result -lt 0) 'Recovery exclusion was not held.'
    $blocked=$false
    try { [void](Sql 'SET LOCK_TIMEOUT 200; UPDATE [IndexState] SET [RequiredIndexFormat]=1 WHERE [Id]=1;') }
    catch { if($_.Exception.InnerException.Number -eq 1222 -or $_.Exception.Number -eq 1222) { $blocked=$true } else { throw } }
    Assert $blocked 'A format activation passed the admission publication fence.'
}
$after=Sql @'
DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'FluxKnowledge.DerivedIndexRecovery',
    @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0; SELECT @r;
'@
Assert ([int]$after -ge 0) 'Admission retained a recovery lock through readiness.'
[void](Sql 'UPDATE [IndexState] SET [RequiredIndexFormat]=1 WHERE [Id]=1;')
Admit @(@{Role='Web';Root=$legacy}) $action 'Legacy' $reviewed
Assert ($script:actions -eq 3) 'The exact reviewed legacy v1 entry was not admitted.'
Refuses { Admit @(@{Role='Web';Root=$legacy}) $action 'Legacy' } 'payload-incompatible'

# Conflicting metadata must not become a legacy escape hatch.
$bytes=[IO.File]::ReadAllBytes($WebAssembly)
$value=$key+'FluxKnowledge.IndexFormats'.Length+1
Assert ([Text.Encoding]::Latin1.GetString($bytes,$value,3) -ceq '1,2') 'Unexpected fixture metadata encoding.'
$bytes[$value+2]=[byte][char]'9'
[IO.File]::WriteAllBytes((Join-Path $legacy 'FluxKnowledge.Web.dll'),$bytes)
$badHash=& $fingerprint $legacy
Refuses { Admit @(@{Role='Web';Root=$legacy}) $action 'Legacy' @('Web='+$badHash) } 'format-declaration-invalid'
Assert ($script:actions -eq 3) 'Malformed metadata reached a start callback.'

# Conversion admission must bind the exact current generation and pin settings
# through the immediate action, not merely accept any format-2 projection.
[void](Sql 'UPDATE [IndexState] SET [RequiredIndexFormat]=2 WHERE [Id]=1;')
$current=[guid](Sql 'SELECT ActiveIndexGenerationId FROM IndexState WHERE Id=1;')
$configuration=Join-Path $ScratchRoot 'admission-config.json'
[IO.File]::WriteAllText($configuration,'{"IndexPublication":{"Mode":"Segmented"}}')
$configurationHash=(Get-FileHash -LiteralPath $configuration).Hash
$databaseIdentity=[string](Sql "SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();")
$exact=@{ConnectionString=$connection;Payloads=$webPayload;ReadPayloadFingerprint=$fingerprint;WebIndexMode='Segmented';
    ExpectedActiveGenerationId=$current;ExpectedConfigurationPath=$configuration;ExpectedConfigurationSha256=$configurationHash;ExpectedDatabaseIdentity=$databaseIdentity}
$script:exactActions=0
Refuses {Invoke-IndexFormatAdmission @exact -Action {$script:exactActions++} -OperationFormats @(1)} 'operation-incompatible'
$wrong=$exact.Clone();$wrong.ExpectedActiveGenerationId=[guid]::NewGuid()
Refuses {Invoke-IndexFormatAdmission @wrong -Action {$script:exactActions++}} 'generation-changed'
$wrong=$exact.Clone();$wrong.ExpectedConfigurationSha256=('A'*64)
Refuses {Invoke-IndexFormatAdmission @wrong -Action {$script:exactActions++}} 'configuration-changed'
$wrong=$exact.Clone();$wrong.ExpectedDatabaseIdentity='wrong-database-incarnation'
Refuses {Invoke-IndexFormatAdmission @wrong -Action {$script:exactActions++}} 'database-incarnation-changed'
Invoke-IndexFormatAdmission @exact -Action {
    $denied=$false
    try{[IO.File]::WriteAllText($configuration,'changed')}catch [IO.IOException]{$denied=$true}
    Assert $denied 'Admission did not pin the reviewed configuration through the action.'
    $script:exactActions++
}
Assert ($script:exactActions -eq 1) 'Changed bindings reached an immediate action.'

# Exercise the actual updater callback for both candidate and automatic predecessor rollback.
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
$tokens=$null; $parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$parseErrors)
Assert ($parseErrors.Count -eq 0) 'Updater did not parse.'
foreach($name in @('Invoke-DeploymentIndexAdmission','Start-IndexCompatibleWebAppPool','Enable-IndexCompatibleOutlookTask')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    Assert ($definition.Count -eq 1) ('Missing actual callback: '+$name)
    Invoke-Expression $definition[0].Extent.Text
}
function Get-DeploymentSqlConnectionString { $connection }
function Start-WebAppPool { param($Name) $script:starts++ }
$script:starts=0
$CanonicalLiveRoot=$ScratchRoot; $CanonicalDeployRoot=$legacy
$ReviewedLegacyIndexPayloads=$reviewed
$configRoot=Join-Path $ScratchRoot 'Config'; New-Item -ItemType Directory $configRoot | Out-Null
[IO.File]::WriteAllText((Join-Path $configRoot 'appsettings.Production.json'),'{"IndexPublication":{"Mode":"Segmented"}}')
$hold=Join-Path $ScratchRoot 'deployment-validation-hold.json'; [IO.File]::WriteAllText($hold,'"test-release"')
$candidate=Join-Path $ScratchRoot 'candidate'; New-Item -ItemType Directory $candidate | Out-Null
Copy-Item -LiteralPath $WebAssembly -Destination (Join-Path $candidate 'FluxKnowledge.Web.dll')
$bytes=[IO.File]::ReadAllBytes($WebAssembly); $bytes[$key]=[byte][char]'Z'
[IO.File]::WriteAllBytes((Join-Path $legacy 'FluxKnowledge.Web.dll'),$bytes)
[void](Sql 'UPDATE [IndexState] SET [RequiredIndexFormat]=2 WHERE [Id]=1;')
Refuses {
    Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $legacy -CandidateRoot $candidate `
        -PreviousRoot (Join-Path $ScratchRoot 'previous') -FailedRoot (Join-Path $ScratchRoot 'failed') `
        -ActivateCandidate { [IO.Directory]::Move($candidate,$legacy) } -StopApplication {} `
        -StartApplication { Start-IndexCompatibleWebAppPool -Name 'disposable-test-only' } `
        -ValidateApplication { throw 'deliberate candidate validation failure' }
} 'rollback'
Assert ($script:starts -eq 1) 'The incompatible predecessor reached the actual native start port.'
Assert ([IO.File]::Exists($hold)) 'Rollback refusal removed the hold.'
& {
    $script:taskEnables=0
    function Invoke-DeploymentIndexAdmission { param($Roles,$Action)
        Assert ($Roles.Count -eq 1 -and $Roles[0] -ceq 'OutlookHost') 'Task admission used the wrong role.'; & $Action }
    function Enable-ScheduledTask { param($TaskName,$TaskPath)
        Assert ($TaskName -ceq 'FluxKnowledge.OutlookHost' -and $TaskPath -ceq '\') 'The actual enable callback lost the exact task path.'
        $script:taskEnables++ }
    Enable-IndexCompatibleOutlookTask -TaskName 'FluxKnowledge.OutlookHost' -TaskPath '\' | Out-Null
    Assert ($script:taskEnables -eq 1) 'The root task callback was not invoked once.'
    Refuses { Enable-IndexCompatibleOutlookTask -TaskName 'FluxKnowledge.OutlookHost' -TaskPath '\foreign\' } 'TaskPath'
    Assert ($script:taskEnables -eq 1) 'An unrelated task path reached the native enable port.'
}
Write-Output 'PASS: entry capabilities, predecessor refusal, reviewed v1 bridge, metadata integrity and both admission fences.'
