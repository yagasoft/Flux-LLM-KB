[CmdletBinding()]
param([string]$SourceRoot = '')
$ErrorActionPreference='Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
$installer=Join-Path $SourceRoot 'scripts\deploy\install-source-access-broker.ps1'
$installVolume=$env:FLUXKNOWLEDGE_TEST_SOURCE_ACCESS_VOLUME_ROOT
if ([string]::IsNullOrWhiteSpace($installVolume)) { $installVolume=[IO.Path]::GetPathRoot([IO.Path]::GetTempPath()) }
if (![IO.Path]::IsPathFullyQualified($installVolume) -or $installVolume -ine [IO.Path]::GetPathRoot($installVolume) -or
    $installVolume.StartsWith('\\') -or [IO.DriveInfo]::new($installVolume).DriveType -ne [IO.DriveType]::Fixed) {
    throw 'The source-access installation fixture requires an explicit local fixed volume root.'
}
$fixture=[IO.Directory]::CreateTempSubdirectory('FluxKnowledgeSourceAccessInstall_').FullName
try {
    $payload=Join-Path $fixture 'payload'; $null=[IO.Directory]::CreateDirectory($payload)
    $files=@(foreach($name in @('FluxKnowledge.Cli.exe','FluxKnowledge.Cli.dll','FluxKnowledge.Integrations.dll','FluxKnowledge.Cli.deps.json','FluxKnowledge.Cli.runtimeconfig.json')) {
        $path=Join-Path $payload $name; [IO.File]::WriteAllText($path,'Synthetic plan-only fixture; never execute.')
        [pscustomobject]@{relativePath=$name;byteLength=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    })
    $manifest=Join-Path $fixture 'manifest.json'; @{version=1;commit=('a'*40);files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding utf8
    $policy=Join-Path $fixture 'policy.json'; $source=Join-Path $fixture 'source'
    @{Version=1;IndexerSid='S-1-5-21-1-2-3-1001';PipeName='FluxKnowledge.SourceAccess.isolated-install';Roots=@(@{Path=$source;IdentityFingerprint=('b'*64);InheritableRead=$true});ProtectedRoots=@($payload)} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $policy -Encoding utf8
    $rootPlan=Join-Path $fixture 'root-read-plan.json'
    @{Version=1;RootPath=$source;RootIdentityFingerprint=('b'*64);IndexerSid='S-1-5-21-1-2-3-1001';ActualIndexerReadVerified=$false} |
        ConvertTo-Json | Set-Content -LiteralPath $rootPlan -Encoding utf8
    $argsMap=@{PayloadPath=$payload;ManifestPath=$manifest;PolicyPath=$policy;RootReadPlanPath=$rootPlan;
        ExpectedManifestSha256=(Get-FileHash -LiteralPath $manifest).Hash;ExpectedPolicySha256=(Get-FileHash -LiteralPath $policy).Hash;
        ExpectedRootReadPlanSha256=(Get-FileHash -LiteralPath $rootPlan).Hash;
        InstallRoot=(Join-Path $installVolume ('FluxKnowledgeSourceAccess-Staging-'+[Guid]::NewGuid().ToString('N')));
        TaskName=('FluxKnowledge.SourceAccess-Staging-'+[Guid]::NewGuid().ToString('N'))}
    $plan=(& $installer @argsMap -PlanOnly | Out-String) | ConvertFrom-Json
    if ($plan.mode -ne 'plan-only' -or $plan.principal -ne 'NT AUTHORITY\SYSTEM' -or $plan.source_root -cne $source -or
        $plan.existing_children_rewritten -ne $false -or $plan.source_contents_changed -ne $false -or
        $plan.onedrive_configuration_changed -ne $false -or (Test-Path -LiteralPath $argsMap.InstallRoot) -or
        $plan.task -cne $argsMap.TaskName) { throw 'Plan-only did not remain within the isolated observational contract.' }
    $private=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
    $privateBytes=[byte[]]::new($private.BinaryLength); $private.GetBinaryForm($privateBytes,0)
    $created=Join-Path $fixture 'exclusive'
    $held=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($created,$privateBytes)
    try {
        [FluxSourceAccessInstallFile]::ValidatePrivateDirectory($held,$created)
        $rejected=$false
        try { $unexpected=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($created,$privateBytes); $unexpected.Dispose() } catch { $rejected=$true }
        if (-not $rejected -or @(Get-ChildItem -LiteralPath $created -Force).Count -ne 0) { throw 'A preclaimed directory was not refused before writes.' }
    } finally { $held.Dispose() }
    $outside=Join-Path $fixture 'outside'; $null=[IO.Directory]::CreateDirectory($outside)
    $junction=Join-Path $fixture 'preclaimed-link'; $null=New-Item -ItemType Junction -Path $junction -Target $outside
    $rejected=$false
    try { $unexpected=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($junction,$privateBytes); $unexpected.Dispose() } catch { $rejected=$true }
    if (-not $rejected -or @(Get-ChildItem -LiteralPath $outside -Force).Count -ne 0) { throw 'A preclaimed junction was not refused before writes.' }
    foreach ($sddl in @('O:BAG:BAD:P(A;;FA;;;BA)(A;;GA;;;BU)','O:BAG:BAD:P(A;;FA;;;BA)(A;;GW;;;BU)',
                        'O:BAG:BAD:P(OA;;FA;00112233-4455-6677-8899-aabbccddeeff;;BU)')) {
        $unsafe=[Security.AccessControl.RawSecurityDescriptor]::new($sddl)
        $unsafeBytes=[byte[]]::new($unsafe.BinaryLength); $unsafe.GetBinaryForm($unsafeBytes,0)
        $rejected=$false
        try { [FluxSourceAccessInstallFile]::ValidateDescriptor($unsafeBytes,$false) } catch { $rejected=$true }
        if (-not $rejected) { throw 'Unsafe generic rights or an unsupported ACE was accepted.' }
    }
    $generic=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;;FA;;;BA)(A;;GA;;;BU)')
    $genericBytes=[byte[]]::new($generic.BinaryLength); $generic.GetBinaryForm($genericBytes,0)
    $rejected=$false
    try { [FluxSourceAccessInstallFile]::ValidateDescriptor($genericBytes,$true) } catch { $rejected=$true }
    if (-not $rejected) { throw 'Generic mutation rights at the volume root were accepted.' }
    $rejected=$false
    try { & $installer @argsMap -Apply | Out-Null } catch { $rejected=$_.Exception.Message -like '*explicit approval*' }
    if (-not $rejected -or (Test-Path -LiteralPath $argsMap.InstallRoot)) { throw 'Unapproved apply did not stop before installation.' }
    [IO.File]::AppendAllText((Join-Path $payload 'FluxKnowledge.Cli.dll'),'changed')
    $rejected=$false
    try { & $installer @argsMap -PlanOnly | Out-Null } catch { $rejected=$_.Exception.Message -like '*checksum*' -or $_.Exception.Message -like '*Unsafe candidate*' }
    if (-not $rejected) { throw 'Changed payload was accepted.' }
    [pscustomobject]@{passed=10;source_bytes_changed=0;source_folder_acl_changes=0;scheduled_tasks_created=0;production_calls=0} | ConvertTo-Json
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped its isolated temporary root.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
