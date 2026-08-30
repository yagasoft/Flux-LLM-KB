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

# Lifecycle access is one-shot, and the service principal receives only the two data roles.
Assert-True ($bootstrap -match 'REVOKE EXECUTE ON OBJECT::dbo\.FluxKnowledgeNativeGoLiveCreate') 'Bootstrap lifecycle EXECUTE is retained after bootstrap.'
Assert-True ($bootstrap -match 'ALTER ROLE \[db_datareader\] ADD MEMBER \[IIS AppPool\\FluxKnowledge\]') 'The app pool is not assigned db_datareader.'
Assert-True ($bootstrap -match 'ALTER ROLE \[db_datawriter\] ADD MEMBER \[IIS AppPool\\FluxKnowledge\]') 'The app pool is not assigned db_datawriter.'
Assert-True ($bootstrap -notmatch 'GRANT CONNECT, SELECT, INSERT, UPDATE, DELETE, EXECUTE TO \[IIS AppPool\\FluxKnowledge\]') 'The app pool retains direct DML or database-wide EXECUTE.'
Assert-True ($bootstrap -match 'CREATE USER \[IIS AppPool\\FluxKnowledge\] FOR LOGIN \[IIS AppPool\\FluxKnowledge\];[\s\S]*GRANT CONNECT TO \[IIS AppPool\\FluxKnowledge\];[\s\S]*ALTER ROLE \[db_datareader\] ADD MEMBER \[IIS AppPool\\FluxKnowledge\]') `
    'The final app user is not granted database CONNECT before public CONNECT is revoked.'
$finalisationStart = $bootstrap.IndexOf('CREATE PROCEDURE dbo.FluxKnowledgeNativeGoLiveManageAppPool', [StringComparison]::Ordinal)
$finalisationEnd = $bootstrap.IndexOf('-- END HASHED PROCEDURE: FluxKnowledgeNativeGoLiveManageAppPool', [StringComparison]::Ordinal)
$finalisation = $bootstrap.Substring($finalisationStart, $finalisationEnd - $finalisationStart)
Assert-True ($finalisation.IndexOf('GRANT CONNECT TO [IIS AppPool\FluxKnowledge];', [StringComparison]::Ordinal) -ge 0 -and
    $finalisation.IndexOf('GRANT CONNECT TO [IIS AppPool\FluxKnowledge];', [StringComparison]::Ordinal) -lt
    $finalisation.IndexOf('REVOKE CONNECT FROM public;', [StringComparison]::Ordinal)) `
    'Finalisation does not preserve the app user database CONNECT before revoking public CONNECT.'
Assert-True ($bootstrap -match "HAS_PERMS_BY_NAME\(N''FluxKnowledge'',N''DATABASE'',N''CONNECT''\)=1") `
    'The final app-pool observer does not prove target database CONNECT.'
$finalizeBootstrap = $ports.IndexOf('await FinalizeBootstrapAuthorityAsync', [StringComparison]::Ordinal)
$finalAppPoolObservation = $ports.IndexOf('var appPool = await ObserveAppPoolAsync', $finalizeBootstrap, [StringComparison]::Ordinal)
Assert-True ($finalizeBootstrap -ge 0 -and $finalAppPoolObservation -gt $finalizeBootstrap) `
    'The app identity and effective authority are not re-observed after bootstrap finalisation.'
$preflightStart = $ports.IndexOf('internal async ValueTask<NativeGoLiveSqlPreflightObservation> ObservePreflightAsync', [StringComparison]::Ordinal)
$preflightEnd = $ports.IndexOf('public async ValueTask<NativeGoLiveSqlPostBootstrapObservation> ProvisionAndObserveAsync', [StringComparison]::Ordinal)
$preflight = $ports.Substring($preflightStart, $preflightEnd - $preflightStart)
Assert-True ($preflight.IndexOf('ObserveAppPoolAsync', [StringComparison]::Ordinal) -lt 0) `
    'Preflight consumes the one-shot app authority observation before finalisation.'
Assert-True ($preflight -match 'COUNT_BIG\(\*\)[\s\S]*db_owner') `
    'The SQL preflight observer does not admit the exact authorised db_owner master role shape for guarded validation.'
$postBootstrapObserverStart = $ports.IndexOf('private async ValueTask<NativeGoLiveSqlPostBootstrapObservation> ObservePostBootstrapAsync', [StringComparison]::Ordinal)
$postBootstrapObserverEnd = $ports.IndexOf('private static async ValueTask<BootstrapLifecycleRevocationObservation>', $postBootstrapObserverStart, [StringComparison]::Ordinal)
$postBootstrapObserver = $ports.Substring($postBootstrapObserverStart, $postBootstrapObserverEnd - $postBootstrapObserverStart)
Assert-True ($postBootstrapObserver -match 'appPool\.EffectiveAuthorityFindings') `
    'The final SQL post-bootstrap observation does not retain the final app-pool authority evidence.'
Assert-True ($postBootstrapObserver -notmatch 'bootstrapEvidence\.EffectiveAuthorityFindings') `
    'The final SQL post-bootstrap observation incorrectly retains bootstrap-principal authority evidence.'

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
Assert-True ($ports -notmatch 'Directory\.CreateDirectory\(_plan\.Layout\.SqlDataRoot\)') 'SQL directory creation bypasses the held-handle primitive.'
Assert-True ($ports -notmatch 'Directory\.CreateDirectory\(path\)') 'ACL directory creation bypasses the held-handle primitive.'
Assert-True ($filesystem -match 'SetDirectorySecurityAsync') 'ACL mutation has no held-handle primitive.'

# The production configuration is atomically written and verified before IIS, without forwarding bootstrap credentials.
Assert-True ($ports -match 'WriteProductionConfigurationAsync') 'The exact production configuration is not written through the native adapter.'
Assert-True ($hostText -match 'WriteProductionConfigurationAsync') 'The host does not create production configuration before starting IIS.'
Assert-True ($ports -match 'RemoveBootstrapFromChildEnvironment') 'Child processes do not explicitly remove the bootstrap environment variable.'
Assert-True ($hostText -match 'ParseAndClearBootstrap') 'Bootstrap parsing is not deferred to the guarded host.'
Assert-True ($closeout -notmatch 'RecoverAsync') 'closeout.json is still used to reconstruct live authority.'
Assert-True ($ports -match 'ObserveLifecycleAuthorityRevocationAsync' -and
    $hostText -match 'BootstrapLifecyclePermissionRowsAbsent') `
    'Post-bootstrap validation does not prove that lifecycle grants were removed.'
$lifecycleObserverStart = $ports.IndexOf('private static async ValueTask<BootstrapLifecycleRevocationObservation> ObserveLifecycleAuthorityRevocationAsync', [StringComparison]::Ordinal)
$lifecycleObserverEnd = $ports.IndexOf('private string CatalogueConnection', $lifecycleObserverStart, [StringComparison]::Ordinal)
$lifecycleObserver = $ports.Substring($lifecycleObserverStart, $lifecycleObserverEnd - $lifecycleObserverStart)
Assert-True ($lifecycleObserver -match "COALESCE\(HAS_DBACCESS\(N'FluxKnowledge'\),0\)=1") `
    'The bootstrap catalogue-access observation does not truthfully report retained access.'
$postBootstrapStart = $hostText.IndexOf('private void ValidatePostBootstrap', [StringComparison]::Ordinal)
$postBootstrapEnd = $hostText.IndexOf('private static bool ValidateBootstrapProcedureEvidence', $postBootstrapStart, [StringComparison]::Ordinal)
$postBootstrap = $hostText.Substring($postBootstrapStart, $postBootstrapEnd - $postBootstrapStart)
Assert-True ($postBootstrap -match 'HasPermittedBootstrapPostBootstrapAuthority\(value\)' -and
    $postBootstrap -match '!value\.BootstrapLifecyclePermissionRowsAbsent') `
    'Final bootstrap validation does not constrain the authorised bootstrap observation or prove direct grants were removed.'
$bootstrapAuthorityStart = $hostText.IndexOf('private static bool HasPermittedBootstrapPostBootstrapAuthority', [StringComparison]::Ordinal)
$bootstrapAuthorityEnd = $hostText.IndexOf('private static bool ValidateBootstrapProcedureEvidence', $bootstrapAuthorityStart, [StringComparison]::Ordinal)
$bootstrapAuthority = $hostText.Substring($bootstrapAuthorityStart, $bootstrapAuthorityEnd - $bootstrapAuthorityStart)
Assert-True ($bootstrapAuthority -match 'HasPermittedBootstrapRoles\(' -and
    $bootstrapAuthority -match '!value\.BootstrapLifecycleAuthorityRevoked && value\.BootstrapCanAccessCatalogue' -and
    $hostText -match 'private static bool HasPermittedBootstrapRoles[\s\S]*ExactSet\(serverRoles, \["sysadmin"\]\) && ExactSet\(masterDatabaseRoles, \["db_owner"\]\)') `
    'Final bootstrap validation does not allow only the effective sysadmin and db_owner bootstrap authority.'

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
