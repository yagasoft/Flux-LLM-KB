[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
$fixture=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('SourceJournalConfig-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
try {
    $path=Join-Path $fixture 'settings.json'
    $optionsPath=Join-Path $fixture 'journal-options.json'
    $original=[Text.Encoding]::UTF8.GetBytes('{"Worker":{"Enabled":true},"SourceAccess":{"Enabled":true,"PipeName":"FluxKnowledge.SourceAccess.preserved"},"SourceJournal":{"Enabled":false,"SealingEnabled":false}}')
    [IO.File]::WriteAllBytes($path,$original)
    $acl=(Get-Acl -LiteralPath $path).GetSecurityDescriptorSddlForm('All')
    $options=[ordered]@{Enabled=$true;PipeName='FluxKnowledge.SourceJournal.config-test';ApprovedVolumes=@('\\?\Volume{11111111-1111-1111-1111-111111111111}\');PollSeconds=2;MetadataEntriesPerSecond=64;SealingEnabled=$true;SealQualifications=@([ordered]@{
        RootIdentityFingerprint=('a'*64);EnvironmentFingerprint=('b'*64);Scope='local-ntfs-synthetic';QualificationId='staging-only'})}
    function Write-Options($Value) {
        [IO.File]::WriteAllText($optionsPath,($Value|ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
        (Get-FileHash -LiteralPath $optionsPath -Algorithm SHA256).Hash
    }
    $optionsHash=Write-Options $options
    $change=New-SourceJournalConfigurationChange -Path $path -OptionsPath $optionsPath -ExpectedOptionsSha256 $optionsHash
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToHexString($original)) {throw 'Plan mutated configuration.'}
    Set-SourceAccessConfigurationChange $change
    Assert-SourceAccessConfigurationChange $change
    $active=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    if (!$active.Worker.Enabled -or !$active.SourceAccess.Enabled -or $active.SourceAccess.PipeName -cne 'FluxKnowledge.SourceAccess.preserved' -or
        !$active.SourceJournal.Enabled -or !$active.SourceJournal.SealingEnabled -or $active.SourceJournal.PollSeconds -ne 2) {throw 'Journal activation changed the wrong configuration.'}
    if ((Get-Acl -LiteralPath $path).GetSecurityDescriptorSddlForm('All') -cne $acl) {throw 'Configuration ACL changed.'}
    Restore-SourceAccessConfigurationChange $change
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToHexString($original)) {throw 'Rollback changed original bytes.'}
    # The reviewed options are not re-opened during activation.
    [IO.File]::WriteAllText($optionsPath,'{}')
    Set-SourceAccessConfigurationChange $change
    Assert-SourceAccessConfigurationChange $change
    Restore-SourceAccessConfigurationChange $change
    $refused=$false;try {New-SourceJournalConfigurationChange -Path $path -OptionsPath $optionsPath -ExpectedOptionsSha256 $optionsHash|Out-Null}catch{$refused=$true}
    if (!$refused) {throw 'Options hash drift was accepted.'}
    foreach ($case in @('text-bool','duplicate-volume','pipe-escape','unknown-option','cloud-without-provider','partial-provider','duplicate-root','invalid-rate','duplicate-property')) {
        $value=$options|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
        switch ($case) {
            'text-bool' {$value.Enabled='true'}
            'duplicate-volume' {$value.ApprovedVolumes+= $value.ApprovedVolumes[0]}
            'pipe-escape' {$value.PipeName='..\escape'}
            'unknown-option' {$value.Unreviewed=$true}
            'cloud-without-provider' {$value.SealQualifications[0].Scope='onedrive-hydrated'}
            'partial-provider' {$value.SealQualifications[0].ProviderName='OneDrive'}
            'duplicate-root' {$value.SealQualifications+= $value.SealQualifications[0]}
            'invalid-rate' {$value.MetadataEntriesPerSecond=0}
        }
        $sha=Write-Options $value
        if ($case -ceq 'duplicate-property') {
            [IO.File]::WriteAllText($optionsPath,([IO.File]::ReadAllText($optionsPath).Replace('"Enabled": true','"Enabled": true,"Enabled": false')))
            $sha=(Get-FileHash -LiteralPath $optionsPath -Algorithm SHA256).Hash
        }
        $refused=$false;try {New-SourceJournalConfigurationChange -Path $path -OptionsPath $optionsPath -ExpectedOptionsSha256 $sha|Out-Null}catch{$refused=$true}
        if (!$refused) {throw "Unsafe journal options accepted: $case"}
    }
    Set-SourceAccessConfigurationChange $change
    $disable=New-SourceJournalConfigurationChange -Path $path -Disable
    Set-SourceAccessConfigurationChange $disable
    $active=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    if ($active.SourceJournal.Enabled -or $active.SourceJournal.SealingEnabled -or !$active.SourceAccess.Enabled -or
        $active.SourceJournal.SealQualifications[0].QualificationId -cne 'staging-only') {throw 'Feature-off configuration failed or erased its qualification.'}
    Restore-SourceAccessConfigurationChange $disable
    Restore-SourceAccessConfigurationChange $change
    $validCloud=$options|ConvertTo-Json -Depth 15|ConvertFrom-Json -AsHashtable
    $validCloud.SealQualifications[0].Scope='onedrive-hydrated'
    $validCloud.SealQualifications[0].ProviderName='OneDrive'
    $validCloud.SealQualifications[0].ProviderVersion='synthetic-version'
    $cloudHash=Write-Options $validCloud
    $cloud=New-SourceJournalConfigurationChange -Path $path -OptionsPath $optionsPath -ExpectedOptionsSha256 $cloudHash
    Set-SourceAccessConfigurationChange $cloud
    if ((Get-Content -LiteralPath $path -Raw|ConvertFrom-Json).SourceJournal.SealQualifications[0].ProviderVersion -cne 'synthetic-version') {throw 'Provider binding changed.'}
    Restore-SourceAccessConfigurationChange $cloud
    $probe=Join-Path $fixture 'probe.ps1'
    [IO.File]::WriteAllText($probe,@'
param([string]$Module,[string]$Configuration,[string]$Options,[string]$OptionsHash)
$ErrorActionPreference='Stop'
Import-Module $Module -Force
(New-SourceJournalConfigurationChange -Path $Configuration -OptionsPath $Options -ExpectedOptionsSha256 $OptionsHash).AfterHash
'@)
    foreach ($attempt in 1..4) {
        $actual=& pwsh -NoProfile -NonInteractive -File $probe -Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Configuration $path -Options $optionsPath -OptionsHash $cloudHash
        if ($LASTEXITCODE -ne 0 -or $actual.Trim() -cne $cloud.AfterHash) {throw 'Journal configuration hash differs across processes.'}
    }
    [IO.File]::AppendAllText($path,' ')
    $refused=$false;try {Set-SourceAccessConfigurationChange $change}catch{$refused=$true}
    if (!$refused) {throw 'Configuration drift was overwritten.'}
    'source-journal-config-contract passed'
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\SourceJournalConfig-',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe fixture cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
