param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$updater=Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'
# Invalid mode combinations refuse before any IIS/SQL observation.
foreach($flag in @('OfflineProjectionMaintenance','ResumeOfflineProjectionMaintenance')) {
    $output=& pwsh -NoProfile -File $updater -SourceRoot (Join-Path $SourceRoot 'tests') -PlanOnly "-$flag" 2>&1 | Out-String
    if($LASTEXITCODE -eq 0 -or $output -notmatch 'index-update-explicit-projection-mode-required'){throw 'Offline flag escaped the explicit projection mode.'}
}
$output=& pwsh -NoProfile -File $updater -SourceRoot (Join-Path $SourceRoot 'tests') -PlanOnly -ApplySegmentedIndexProjection -ResumeOfflineProjectionMaintenance 2>&1 | Out-String
if($LASTEXITCODE -eq 0 -or $output -notmatch 'index-update-offline-resume-requires-apply'){throw 'Offline resume was admitted by PlanOnly.'}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-iis.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('FluxSegmentedConfig_'+[guid]::NewGuid().ToString('N'))
$CanonicalLiveRoot=Join-Path $root 'live'
[void][IO.Directory]::CreateDirectory((Join-Path $CanonicalLiveRoot 'Config'))
try {
    $hold=Join-Path $root 'hold.json'
    [IO.File]::WriteAllText($hold,'"owned-hold"')
    if((Get-SegmentedCanonicalHold $hold).Owner -cne 'owned-hold'){throw 'Canonical hold was not accepted.'}
    [IO.File]::AppendAllText($hold,"`n")
    $refused=$false
    try{Get-SegmentedCanonicalHold $hold}catch{if($_.Exception.Message -ceq 'index-update-canonical-hold-required'){$refused=$true}else{throw}}
    if(!$refused){throw 'Whitespace-padded hold allowed an unreproducible recovery binding.'}
    $path=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    [IO.File]::WriteAllText($path,'{"ConnectionStrings":{"FluxKnowledge":"isolated fixture"},"SourceAccess":{"Enabled":false},"IndexPublication":{"Mode":"Legacy","Extra":"preserve"}}')
    $acl=(Get-Acl -LiteralPath $path).Sddl
    $change=Get-SegmentedPublicationConfiguration $path
    $plan=@{OperationRoot=$root;ConfigurationSha256=$change.BeforeHash;DesiredConfigurationSha256=$change.AfterHash}
    Set-SegmentedPublicationConfiguration $plan
    $settings=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if($settings.IndexPublication.Mode -cne 'Segmented' -or $settings.IndexPublication.Extra -cne 'preserve' -or
        $settings.SourceAccess.Enabled -or $settings.ConnectionStrings.FluxKnowledge -cne 'isolated fixture' -or
        (Get-Acl -LiteralPath $path).Sddl -cne $acl){throw 'Publication mode changed unrelated settings or ACL.'}
    $when=(Get-Item -LiteralPath $path).LastWriteTimeUtc
    Set-SegmentedPublicationConfiguration $plan
    if((Get-Item -LiteralPath $path).LastWriteTimeUtc -ne $when){throw 'Already applied configuration was rewritten.'}
    [IO.File]::AppendAllText($path,' ')
    $refused=$false
    try{Set-SegmentedPublicationConfiguration $plan}catch{if($_.Exception.Message -ceq 'index-update-configuration-changed'){$refused=$true}else{throw}}
    if(!$refused){throw 'Unreviewed configuration drift was overwritten.'}
    if((Get-FileHash -LiteralPath (Join-Path $root 'configuration-before.json')).Hash -cne $change.BeforeHash){throw 'Original configuration was not preserved.'}
    'PASS: only publication mode changes; unrelated settings and file ACL remain; replay is idempotent and drift refuses.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotlike 'FluxSegmentedConfig_*'){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
