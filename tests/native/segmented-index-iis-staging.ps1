param([Parameter(Mandatory)][string]$SourceRoot,[Parameter(Mandatory)][string]$OperatorRoot,
    [Parameter(Mandatory)][string]$ScratchRoot,[Parameter(Mandatory)][string]$ExpectedDatabase,[switch]$ResumeOfflineMaintenance)
$ErrorActionPreference='Stop';$VerbosePreference='Continue'
$root=[IO.Path]::GetFullPath($ScratchRoot)
if(!$root.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($root) -cnotlike 'FluxProjectionDeployment_*'){throw 'Not an owned fixture root.'}
$normalised=$env:FLUXKNOWLEDGE_PROJECTION_DEPLOYMENT_TEST_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$sql=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
if($sql.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$'){throw 'Not an owned disposable database.'}
foreach($name in @('incremental-segmented-index-update','incremental-segmented-index-schema','incremental-source-access-config',
    'incremental-hybrid-passage-rebuild','index-format-admission','recovery-reference-writer','incremental-projection-operator')) {
    Import-Module (Join-Path $SourceRoot ('scripts/deploy/'+$name+'.psm1')) -Force
}
. (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-iis.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater parse failure.'}
foreach($name in @('Get-DeploymentSqlConnectionString','ConvertTo-DeploymentValidationConnectionString','Get-RetainedPipelineStateBaseline',
    'Assert-RetainedPipelineStateUnchanged','Test-ApplicationPayload','Test-InteractiveHostPayload','New-DeploymentValidationHold','Assert-NotReparsePoint')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($definition.Count -ne 1){throw ('Missing actual updater function: '+$name)}
    Invoke-Expression $definition[0].Extent.Text
}
$CanonicalLiveRoot=Join-Path $root 'live';$CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
$CanonicalRecoveryRoot=Join-Path $CanonicalLiveRoot 'Recovery';$IncrementalRecoveryRoot=Join-Path $CanonicalRecoveryRoot 'IncrementalUpdates'
$ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/deployment-validation-hold.json'
$InteractiveHostRoot=Join-Path $root 'companion/outlook-host';$SiteName='DisposableProjectionFixture'
$ReadinessTimeoutSeconds=10;$RebuildTimeoutSeconds=30;$loopbackOrigin=@{Origin='http://127.0.0.1:59999'}
$operation=Join-Path $root 'operation';$payloadRoot=Join-Path $root 'payloads';$gitRoot=Join-Path $root 'git'
$configuration=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
if(!$ResumeOfflineMaintenance) {
foreach($path in @($CanonicalDeployRoot,$CanonicalRecoveryRoot,$InteractiveHostRoot,$operation,$gitRoot,
    (Join-Path $CanonicalLiveRoot 'Config'),(Split-Path -Parent $ValidationHoldPath))) {[void][IO.Directory]::CreateDirectory($path)}
[IO.File]::WriteAllText($configuration,(@{ConnectionStrings=@{FluxKnowledge=$sql.ConnectionString};IndexPublication=@{Mode='Legacy'}} | ConvertTo-Json -Depth 4))
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'),([IO.File]::ReadAllText($configuration)))
[IO.File]::WriteAllText($ValidationHoldPath,'"disposable-projection-fixture"')
[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'predecessor.dll'),'old application')
[IO.File]::WriteAllText((Join-Path $InteractiveHostRoot 'predecessor.dll'),'old companion')
& git init -q $gitRoot
if($LASTEXITCODE){throw 'Fixture Git init failed.'}
[IO.File]::WriteAllText((Join-Path $gitRoot 'marker.txt'),'owned fixture')
& git -C $gitRoot add marker.txt
& git -C $gitRoot -c user.name=Fixture -c user.email=fixture@example.invalid commit -qm 'Owned fixture'
if($LASTEXITCODE){throw 'Fixture Git commit failed.'}
$commit=(& git -C $gitRoot rev-parse HEAD | Out-String).Trim()
}
# Only IIS/task process boundaries and the test-root capability are substituted.
# Schema SQL, copy/write logic, actual operator loading, native conversion and
# fenced admission all execute unchanged against the owned fixture.
$script:pool='Stopped';$script:starts=0;$script:probes=0;$script:converts=0
function Assert-IncrementalIisPreflight {if($SiteName -cne 'DisposableProjectionFixture'){throw 'Wrong test site.'}}
function Assert-StoppedPoolRecoveryBaseline {if($script:pool -cne 'Stopped'){throw 'Fixture pool is not stopped.'}}
function Get-RepositoryRecoveryTaskObservation {return @{Enabled=$false;State='Disabled';IdentityHash=('A'*64);LauncherSha256=('B'*64);LauncherPath=(Join-Path $root 'never-executed.vbs')}}
function Get-CimInstance {param($ClassName,$ErrorAction);return @()}
function Assert-ApplicationPayloadReadAccess {param($Path);if($Path -cne $CanonicalDeployRoot){throw 'Wrong payload access target.'}}
function Start-WebAppPool {param($Name);if($Name -cne $SiteName){throw 'Wrong start target.'};$script:starts++;$script:pool='Started'}
function Wait-IisAppPoolState {param($Name,$ExpectedState,$TimeoutSeconds);if($Name -cne $SiteName -or $script:pool -cne $ExpectedState){throw 'Fixture pool state mismatch.'}}
function Invoke-RequiredLoopbackProbes {param($Origin,$TimeoutSeconds);if($Origin -cne 'http://127.0.0.1:59999'){throw 'Wrong test endpoint.'};$script:probes++}
function Assert-SegmentedOperationRoot([string]$RequestedRoot) {
    $candidate=Assert-SegmentedUpdatePath $RequestedRoot
    if(!$candidate.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture operation escaped.'}
    return $candidate
}
function Invoke-IncrementalProjectionOperator {
    param($OperatorRoot,$BindingsJson,[scriptblock]$AssertQuiescent,$TimeoutSeconds)
    $script:converts++
    & (Get-Module 'incremental-projection-operator') {
        param($payload,$index,$settings,$hold,$bindings,$check,$timeout,$mutex)
        Invoke-ProjectionOperatorBridge -OperatorRoot $payload -BeginMethod BeginForIsolatedTests `
            -BeginArguments @($index,$settings,$hold,$bindings,[Action]$check,$mutex) -TimeoutSeconds $timeout
    } $OperatorRoot (Join-Path $CanonicalLiveRoot 'Data/Index') $configuration $ValidationHoldPath $BindingsJson $AssertQuiescent $TimeoutSeconds ('Local\FluxKnowledge.ProjectionBridgeTest.'+[guid]::NewGuid().ToString('N'))
}
if(!$ResumeOfflineMaintenance) {
$web=Join-Path $payloadRoot 'Web';$companion=Join-Path $payloadRoot 'OutlookHost'
$cli=Join-Path (Join-Path $payloadRoot 'Cli') ('native-long-path-'+('n'*64))
foreach($path in @($web,$companion)){[void][IO.Directory]::CreateDirectory($path)}
$cliHash=Get-HybridPayloadFingerprint $OperatorRoot
Copy-SegmentedIndexPayload -Source $OperatorRoot -Destination $cli -ExpectedSha256 $cliHash
Copy-Item -LiteralPath (Join-Path $OperatorRoot 'FluxKnowledge.Web.dll') -Destination $web
[IO.File]::WriteAllText((Join-Path $web 'FluxKnowledge.Web.runtimeconfig.json'),'{}')
[IO.File]::WriteAllText((Join-Path $web 'web.config'),'<configuration/>')
# A never-executed companion PE declares the real role/capability without Office.
Add-Type -TypeDefinition 'public class LoadProjectionFixtureCompiler {}'
$compilation=[Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('FluxKnowledge.OutlookHost').WithOptions(
    [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary))
$compilation=$compilation.AddSyntaxTrees([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
    '[assembly:System.Reflection.AssemblyMetadata("FluxKnowledge.IndexFormats","1,2")] public class NeverExecutedCompanion {}')).AddReferences(
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile([object].Assembly.Location))
$file=[IO.File]::Create((Join-Path $companion 'FluxKnowledge.OutlookHost.dll'))
try{if(!$compilation.Emit($file).Success){throw 'Fixture PE compile failed.'}}finally{$file.Dispose()}
foreach($name in @('FluxKnowledge.OutlookHost.exe','FluxKnowledge.OutlookHost.runtimeconfig.json','run-outlook-host.ps1')){[IO.File]::WriteAllText((Join-Path $companion $name),'never executed')}
foreach($path in [IO.Directory]::GetFiles($web)){[IO.File]::Copy($path,(Join-Path $CanonicalDeployRoot ([IO.Path]::GetFileName($path))))}
foreach($path in [IO.Directory]::GetFiles($companion)){[IO.File]::Copy($path,(Join-Path $InteractiveHostRoot ([IO.Path]::GetFileName($path))))}
$database=Get-SegmentedIndexDatabaseState -ConnectionString $sql.ConnectionString
$change=Get-SegmentedPublicationConfiguration $configuration
$plan=@{Version=1;ReleaseId=([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+$commit.Substring(0,12));
    CreatedUtc=[DateTime]::UtcNow.AddSeconds(-2).ToString('O');OperationId=[guid]::NewGuid().ToString('D');SourceRoot=$gitRoot;Commit=$commit;
    OperationRoot=$operation;ImplementationSha256=(Get-SegmentedUpdateImplementationHash);DatabaseIdentity=$database.Identity;
    BaselineHistory=@($database.History);TargetFormat=2;OfflineMaintenance=$true;Payloads=@(foreach($role in @('Web','OutlookHost','Cli')){
        $path=if($role -ceq 'Cli'){$cli}else{Join-Path $payloadRoot $role};@{Role=$role;Root=$path;Sha256=(Get-HybridPayloadFingerprint $path);EntrySha256=(Get-FileHash -LiteralPath (Join-Path $path ('FluxKnowledge.'+$role+'.dll'))).Hash}});
    OriginalWebSha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot);OriginalCompanionSha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot);
    ConfigurationSha256=$change.BeforeHash;DesiredConfigurationSha256=$change.AfterHash;CompanionConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json')).Hash;
    HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;HoldOwner='disposable-projection-fixture';TaskIdentityHash=('A'*64);LauncherSha256=('B'*64);
    CompanionPolicy='preserve-disabled';CandidateStorage='preserve-reviewed-E-payloads';IndexRoot=(Join-Path $CanonicalLiveRoot 'Data/Index');RecoveryRoot=$CanonicalRecoveryRoot;ApplicationRoot=$CanonicalDeployRoot;CompanionRoot=$InteractiveHostRoot}
$stop=@{Completed=$true;PoolStopped=$true;TaskDisabled=$true;HoldRetained=$true;Phase='iis-writers-stopped';PlanSha256=('C'*64);
    ExitProof=@{VerifiedAtUtc=[DateTime]::UtcNow.ToString('O');ExitedIdentities=@()}}
$planPath=Join-Path $operation 'plan.json';$stopPath=Join-Path $operation 'stop.json'
Write-SegmentedUpdateJson $planPath $plan;Write-SegmentedUpdateJson $stopPath $stop
}
$planPath=Join-Path $operation 'plan.json';$stopPath=Join-Path $operation 'stop.json'
$plan=Read-SegmentedUpdateJson $planPath
$arguments=@{PlanPath=$planPath;ExpectedPlanSha256=(Get-FileHash -LiteralPath $planPath).Hash;StopReceiptPath=$stopPath;ExpectedStopReceiptSha256=(Get-FileHash -LiteralPath $stopPath).Hash}
$result=Invoke-SegmentedIndexIisUpdate @arguments -ResumeOfflineMaintenance:$ResumeOfflineMaintenance
if(!$ResumeOfflineMaintenance) {
    $state=Read-SegmentedUpdateJson (Join-Path $operation 'deployment-state.json')
    if($result.completed -or !$result.offline_ready -or $result.phase -cne 'offline-ready' -or $script:starts -ne 0 -or $script:probes -ne 0 -or
        $script:converts -ne 1 -or $state.StartIntentUtc -or $state.ReleaseIntentUtc -or ![IO.File]::Exists($ValidationHoldPath)){throw 'Offline preparation admitted processing or reported completion.'}
    [void](Invoke-SegmentedIndexIisUpdate @arguments)
    if($script:starts -ne 0 -or $script:converts -ne 1){throw 'Offline replay started a worker or repeated conversion.'}
    'PASS: offline schema upgrade, registered placement and native conversion are ready; pool remains stopped and hold retained for independent retrieval/cleanup.'
    return
}
if(!$result.completed -or $script:starts -ne 1 -or $script:probes -ne 1 -or [IO.File]::Exists($ValidationHoldPath)){throw 'Actual updater callbacks did not complete.'}
if($script:converts -ne 0){throw 'Offline resume repeated native conversion.'}
$packet=Join-Path $IncrementalRecoveryRoot $plan.ReleaseId
foreach($name in @('candidate','candidate-interactive-host','operator')){if([IO.Directory]::Exists((Join-Path $packet $name))){throw 'Candidate code was redundantly copied to Recovery.'}}
foreach($payload in $plan.Payloads){if((Get-HybridPayloadFingerprint $payload.Root) -cne $payload.Sha256){throw 'Reviewed staging bytes were not preserved.'}}
[void](Invoke-SegmentedIndexIisUpdate @arguments -ResumeOfflineMaintenance)
if($script:starts -ne 1 -or $script:probes -ne 1){throw 'Completed adapter replay performed operational work.'}
'PASS: actual baseline upgrade, registered payload backup/placement, native conversion, exact fenced admission, held validation and completed replay; IIS/task boundaries stayed isolated.'
