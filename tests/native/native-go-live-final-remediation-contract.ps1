param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$portsPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveWindowsHostPorts.cs'
$hostPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\GuardedNativeGoLiveHost.cs'
$executorPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveExecutor.cs'
$filesystemPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\HandleRelativeNativeFileSystem.cs'
$closeoutPath = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$bootstrapPath = Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql'
$webProgramPath = Join-Path $SourceRoot 'src\FluxKnowledge.Web\Program.cs'

$ports = Get-Content -LiteralPath $portsPath -Raw
$adaptersPath = Join-Path $SourceRoot 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveWindowsAdapters.cs'
$adapters = Get-Content -LiteralPath $adaptersPath -Raw
$hostText = Get-Content -LiteralPath $hostPath -Raw
$executorText = Get-Content -LiteralPath $executorPath -Raw
$filesystem = Get-Content -LiteralPath $filesystemPath -Raw
$closeout = Get-Content -LiteralPath $closeoutPath -Raw
$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw
$webProgram = Get-Content -LiteralPath $webProgramPath -Raw
$guardedHostText = $hostText.Substring($hostText.IndexOf('internal sealed class GuardedNativeGoLiveHost', [StringComparison]::Ordinal))

# The one-shot production path constructs no durable journal session or external journal transport.
Assert-True ($ports -match 'CreateProduction\([\s\S]*string mergedMainRoot\)') 'The production port factory does not bind the merged payload root.'
Assert-True ($ports -notmatch 'externalJournalRoot') 'The production port factory still requires an external journal root.'
Assert-True ($ports -notmatch 'DurableNativeGoLiveJournalSessionFactory') 'The one-shot production port factory still constructs a durable journal session.'
Assert-True ($closeout -match 'CreateProduction" -ParameterCount 2') 'The closeout still passes a journal location into one-shot production ports.'
Assert-True ($ports -match 'NativeGoLiveProductionPortFactory') 'The production factory still constructs bound ports directly.'

# The approved direct-admin bootstrap gives the fixed app-pool login local administrator authority.
Assert-True ($bootstrap -match 'ALTER SERVER ROLE \[sysadmin\] ADD MEMBER \[IIS AppPool\\FluxKnowledge\]') `
    'The direct-admin bootstrap does not grant the fixed app-pool login sysadmin.'
Assert-True ($bootstrap -match 'ALTER AUTHORIZATION ON DATABASE::\[FluxKnowledge\] TO \[IIS AppPool\\FluxKnowledge\]') `
    'The direct-admin bootstrap does not make the app-pool login catalogue owner.'
Assert-True ($bootstrap -notmatch 'REVOKE EXECUTE ON OBJECT::dbo\.FluxKnowledgeNativeGoLive') `
    'The direct-admin bootstrap retains obsolete lifecycle EXECUTE revocation.'
Assert-True ($bootstrap -notmatch 'ALTER ROLE \[db_datareader\] ADD MEMBER \[IIS AppPool\\FluxKnowledge\]|ALTER ROLE \[db_datawriter\] ADD MEMBER \[IIS AppPool\\FluxKnowledge\]') `
    'The direct-admin bootstrap retains obsolete data-role assignment.'
Assert-True ($bootstrap -notmatch 'CREATE USER \[IIS AppPool\\FluxKnowledge\] FOR LOGIN \[IIS AppPool\\FluxKnowledge\]') `
    'The direct-admin bootstrap creates an app-pool database user before transferring ownership.'
$finalisationStart = $bootstrap.IndexOf('CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveManageAppPool', [StringComparison]::Ordinal)
$finalisationEnd = $bootstrap.IndexOf('-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveManageAppPool', [StringComparison]::Ordinal)
$finalisation = $bootstrap.Substring($finalisationStart, $finalisationEnd - $finalisationStart)
Assert-True ($finalisation -match 'IS_SRVROLEMEMBER\(N''sysadmin'', N''IIS AppPool\\FluxKnowledge''\)<>1') `
    'Finalisation does not prove app-pool sysadmin membership.'
$finalizeBootstrap = $ports.IndexOf('await FinalizeBootstrapAuthorityAsync', [StringComparison]::Ordinal)
$finalAppPoolObservation = $ports.IndexOf('var appPool = await ObserveAppPoolAsync', $finalizeBootstrap, [StringComparison]::Ordinal)
Assert-True ($finalizeBootstrap -ge 0 -and $finalAppPoolObservation -gt $finalizeBootstrap) `
    'The app identity is not re-observed after bootstrap finalisation.'
$preflightStart = $ports.IndexOf('internal async ValueTask<NativeGoLiveSqlPreflightObservation> ObservePreflightAsync', [StringComparison]::Ordinal)
$preflightEnd = $ports.IndexOf('public async ValueTask<NativeGoLiveSqlPostBootstrapObservation> ProvisionAndObserveAsync', [StringComparison]::Ordinal)
$preflight = $ports.Substring($preflightStart, $preflightEnd - $preflightStart)
Assert-True ($preflight.IndexOf('ObserveAppPoolAsync', [StringComparison]::Ordinal) -lt 0) `
    'Preflight consumes the one-shot app authority observation before finalisation.'
Assert-True ($preflight -notmatch 'HAS_PERMS_BY_NAME|db_owner|sys\.server_role_members|sys\.database_role_members') `
    'The SQL preflight observer retains obsolete bootstrap scope or least-privilege checks.'
$postBootstrapObserverStart = $ports.IndexOf('private async ValueTask<NativeGoLiveSqlPostBootstrapObservation> ObservePostBootstrapAsync', [StringComparison]::Ordinal)
$postBootstrapObserverEnd = $ports.IndexOf('private string CatalogueConnection', $postBootstrapObserverStart, [StringComparison]::Ordinal)
$postBootstrapObserver = $ports.Substring($postBootstrapObserverStart, $postBootstrapObserverEnd - $postBootstrapObserverStart)
Assert-True ($postBootstrapObserver -match 'appPool\.LoginSidHex') `
    'The final SQL post-bootstrap observation does not bind catalogue ownership to the app-pool SID.'
Assert-True ($postBootstrapObserver -notmatch 'EffectiveAuthority|LifecycleAuthority|PermissionRowsAbsent') `
    'The final SQL post-bootstrap observation retains obsolete authority evidence.'

# Clean-slate preflight must not inspect a hierarchy that admission has just proved absent.
$productionPreflightStart = $ports.IndexOf('internal sealed class NativeGoLiveWindowsPreflightPort', [StringComparison]::Ordinal)
$productionPreflightEnd = $ports.IndexOf('internal static class NativeGoLiveChildStartBuilder', $productionPreflightStart, [StringComparison]::Ordinal)
$productionPreflight = $ports.Substring($productionPreflightStart, $productionPreflightEnd - $productionPreflightStart)
Assert-True ($productionPreflight -notmatch 'ObserveEffectiveAsync') `
    'Clean-slate production preflight still observes ACLs before the hierarchy exists.'
$preflightValidationStart = $hostText.IndexOf('private void ValidatePreflight', [StringComparison]::Ordinal)
$preflightValidationEnd = $hostText.IndexOf('private static void ValidateSqlPreflight', $preflightValidationStart, [StringComparison]::Ordinal)
$preflightValidation = $hostText.Substring($preflightValidationStart, $preflightValidationEnd - $preflightValidationStart)
Assert-True ($preflightValidation -notmatch 'ValidateAcls') `
    'Clean-slate guarded preflight still requires post-hierarchy ACL evidence.'

# One-shot admission explicitly wipes and recreates the root through the held-handle primitive.
Assert-True ($ports -match 'WipeRootAsync' -and $ports -match 'CreateEmptyRootAsync') `
    'The one-shot clean-slate path does not wipe and recreate its root.'
$observeStart = $adapters.IndexOf('public NativeGoLiveIisObservation Observe', [StringComparison]::Ordinal)
$observeEnd = $adapters.IndexOf('private static void EnsureWindows', $observeStart, [StringComparison]::Ordinal)
$iisObserve = $adapters.Substring($observeStart, $observeEnd - $observeStart)
Assert-True ($iisObserve -match 'GetSection\("system\.webServer/security/authentication/anonymousAuthentication"\)' -and
    $iisObserve -match 'GetSection\("system\.webServer/security/authentication/windowsAuthentication"\)' -and
    $iisObserve -notmatch 'anonymousAuthentication", plan\.IisSiteName' -and
    $iisObserve -notmatch 'windowsAuthentication", plan\.IisSiteName') `
    'Pre-admission IIS observation must not load the absent application web.config.'
Assert-True ($ports -notmatch 'Directory\.CreateDirectory\(_plan\.Layout\.SqlDataRoot\)') 'SQL directory creation bypasses the held-handle primitive.'
Assert-True ($ports -notmatch 'Directory\.CreateDirectory\(path\)') 'ACL directory creation bypasses the held-handle primitive.'
Assert-True ($filesystem -match 'SetDirectorySecurityAsync') 'ACL mutation has no held-handle primitive.'

# The production configuration is atomically written and verified before IIS, without forwarding bootstrap credentials.
Assert-True ($ports -match 'WriteProductionConfigurationAsync') 'The exact production configuration is not written through the native adapter.'
Assert-True ($hostText -match 'WriteProductionConfigurationAsync') 'The host does not create production configuration before starting IIS.'
Assert-True ($ports -match 'RemoveBootstrapFromChildEnvironment') 'Child processes do not explicitly remove the bootstrap environment variable.'
Assert-True ($hostText -match 'ParseAndClearBootstrap') 'Bootstrap parsing is not deferred to the guarded host.'
Assert-True ($closeout -notmatch 'RecoverAsync') 'closeout.json is still used to reconstruct live authority.'
Assert-True ($ports -notmatch 'ObserveLifecycleAuthorityRevocationAsync|ObserveCurrentEffectiveAuthorityAsync|ReadAuthorityFindingsAsync|ReadCurrentAuthorityFindingsAsync|BootstrapLifecycleRevocationObservation|BootstrapEffectiveAuthorityObservation') `
    'The direct-admin bridge retains obsolete lifecycle or effective-authority observation code.'
$postBootstrapStart = $hostText.IndexOf('private void ValidatePostBootstrap', [StringComparison]::Ordinal)
$postBootstrapEnd = $hostText.IndexOf('private static bool ValidateBootstrapProcedureEvidence', $postBootstrapStart, [StringComparison]::Ordinal)
$postBootstrap = $hostText.Substring($postBootstrapStart, $postBootstrapEnd - $postBootstrapStart)
Assert-True ($postBootstrap -notmatch 'HasPermittedBootstrapPostBootstrapAuthority|BootstrapLifecyclePermissionRowsAbsent|EffectiveAuthority') `
    'Final bootstrap validation retains obsolete least-privilege authority checks.'

# The executor and guarded preflight use only the one-shot admission path.
Assert-True ($executorText -match 'AdmitAndWipeAsync' -and $executorText -match 'VerifyOneShotPreflightAsync') `
    'The executor does not enter the one-shot admission and preflight path.'
Assert-True ($executorText -notmatch '\.ReadJournalAsync\(' -and
    $executorText -notmatch '\.CompareAndSwapJournalAsync\(' -and
    $executorText -notmatch '\.PreflightAsync\(') `
    'The one-shot executor still invokes a legacy journal or preflight operation.'
Assert-True ($hostText -match 'INativeGoLiveOneShotPreflightPort' -and
    $guardedHostText -notmatch '_ports\.Preflight') `
    'The guarded host still binds the recovery preflight port.'
Assert-True ($guardedHostText -notmatch 'ClaimCloseoutAuthorityAsync' -and
    $guardedHostText -notmatch '_authorityIssuer') `
    'The guarded one-shot host still performs cross-run authority re-authorisation.'

# Configuration is canonical/no-follow, followed by a no-listener composition proof before IIS.
Assert-True ($ports -match 'NativeGoLiveProductionConfigurationSerializer') 'Production configuration has more than one serializer path.'
Assert-True ($ports -match 'NativeGoLivePublishedCompositionPort') 'Published-Web composition proof is not part of production ports.'
$oneShotPublish = $guardedHostText.IndexOf('public async ValueTask PublishAndStartAsync(', [StringComparison]::Ordinal)
$oneShotPublishText = $guardedHostText.Substring($oneShotPublish)
$configWrite = $oneShotPublishText.IndexOf('WriteProductionConfigurationAsync', [StringComparison]::Ordinal)
$compositionProof = $oneShotPublishText.IndexOf('ValidatePublishedCompositionAsync', [StringComparison]::Ordinal)
$iisStart = $oneShotPublishText.IndexOf('StartAsync(plan.AppPoolName', [StringComparison]::Ordinal)
Assert-True ($configWrite -ge 0 -and $compositionProof -gt $configWrite -and
    $iisStart -gt $compositionProof -and $oneShotPublishText -notmatch 'WriteCompleteMarkerAsync') `
    'Published configuration proof and IIS start are not in the one-shot order.'
Assert-True ($webProgram -match '--validate-native-go-live-composition' -and
    $webProgram -match 'ValidateNativeGoLiveComposition\(builder.Services, builder.Configuration\)') `
    'The published Web host has no fixed no-listener composition validation mode.'

# One child builder and the closeout preamble prevent bootstrap forwarding to every child path.
Assert-True ($ports -match 'NativeGoLiveChildStartBuilder' -and
    $ports -match 'NativeGoLiveChildStartBuilder\.Create\("codex"\)' -and
    $ports -match 'NativeGoLiveChildStartBuilder\.Create\("dotnet"\)') `
    'Codex and Web composition use different bootstrap child-process paths.'
Assert-True ($closeout -match 'bootstrap must not be visible to a closeout child process') `
    'PowerShell children do not fail closed on bootstrap visibility.'

Write-Output 'Native go-live final remediation contract passed.'
