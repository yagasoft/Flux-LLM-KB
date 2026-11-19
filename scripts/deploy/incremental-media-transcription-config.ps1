# Loaded only by the typed media migration and its isolated tests.
function New-MediaTranscriptionConfigurationChange {
    param([string]$Path, [string]$OptionsPath, [string]$ExpectedOptionsSha256)
    $bytes=Get-SourceAccessConfigurationBytes $OptionsPath
    $optionsHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($optionsHash -ine $ExpectedOptionsSha256) { throw 'media-options-hash-mismatch' }
    $options=ConvertFrom-UniqueConfigurationJson $bytes
    if (($options.Keys | Sort-Object) -join '|' -cne 'AfterUtc|CatchUpRevisionIds|SourceRootIds') { throw 'media-options-fields-invalid' }
    foreach ($key in @('SourceRootIds','CatchUpRevisionIds')) {
        $ids=$options[$key]
        if ($ids -isnot [array] -or $ids.Count -eq 0 -or ($key -ceq 'CatchUpRevisionIds' -and $ids.Count -ne 2)) { throw 'media-options-id-list-invalid' }
        $seen=[Collections.Generic.HashSet[Guid]]::new()
        foreach ($id in $ids) {
            $parsed=[Guid]::Empty
            if ($id -isnot [string] -or ![Guid]::TryParseExact($id,'D',[ref]$parsed) -or $parsed -eq [Guid]::Empty -or !$seen.Add($parsed)) {
                throw 'media-options-id-invalid-or-duplicate'
            }
        }
    }
    $cutoff=[DateTimeOffset]::MinValue
    if ($options.AfterUtc -isnot [string] -or $options.AfterUtc -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|\+00:00)$' -or
        ![DateTimeOffset]::TryParse($options.AfterUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None,[ref]$cutoff) -or
        $cutoff -eq [DateTimeOffset]::MinValue) { throw 'media-options-exact-utc-required' }
    $before=Get-SourceAccessConfigurationBytes $Path
    $config=ConvertFrom-UniqueConfigurationJson $before
    if (@($config.Keys | Where-Object { $_ -ilike 'MediaTranscription:*' -or ($_ -ieq 'MediaTranscription' -and $_ -cne 'MediaTranscription') }).Count) {
        throw 'media-configuration-ambiguous-section'
    }
    $config['MediaTranscription']=$options
    $after=[Text.UTF8Encoding]::new($false).GetBytes(($config | ConvertTo-Json -Depth 100))
    [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Before=$before;After=$after;Options=$options;OptionsHash=$optionsHash;
        BeforeHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($before));
        AfterHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($after)); Attempted=$false;RollbackVerified=$true}
}

function Assert-MediaTranscriptionConfigurationBinding {
    param($Change,[string]$ExpectedBefore,[string]$ExpectedAfter)
    if ($Change.BeforeHash -ine $ExpectedBefore -or $Change.AfterHash -ine $ExpectedAfter) {
        throw 'media-configuration-differs-from-reviewed-plan'
    }
}

function Set-MediaTranscriptionConfigurationChange {
    param($Change,[string]$Backup)
    Assert-SourceAccessConfigurationChange $Change -Original
    if (Test-Path -LiteralPath $Backup) { throw 'media-configuration-backup-already-exists' }
    $temporary=$Change.Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    # Create the staging file with the live settings ACL before it contains any private bytes.
    $file=[IO.FileSystemAclExtensions]::Create([IO.FileInfo]::new($temporary),[IO.FileMode]::CreateNew,
        [Security.AccessControl.FileSystemRights]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough,(Get-Acl -LiteralPath $Change.Path))
    try {
        try { $file.Write($Change.After); $file.Flush($true) } finally { $file.Dispose() }
        Assert-SourceAccessConfigurationChange $Change -Original
        $Change.Attempted=$true
        $Change.RollbackVerified=$false
        [IO.File]::Replace($temporary,$Change.Path,$Backup)
        if ((Get-FileHash -LiteralPath $Backup).Hash -cne $Change.BeforeHash) { throw 'media-configuration-predecessor-changed' }
        Assert-SourceAccessConfigurationChange $Change
    } finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}

function Restore-MediaTranscriptionConfigurationChange {
    param($Change,[string]$Backup)
    if ((Get-FileHash -LiteralPath $Change.Path).Hash -cne $Change.BeforeHash) {
        Assert-SourceAccessConfigurationChange $Change
        if ((Get-FileHash -LiteralPath $Backup).Hash -cne $Change.BeforeHash) { throw 'media-configuration-predecessor-changed' }
        $failed=$Backup+'.candidate'
        if (Test-Path -LiteralPath $failed) { throw 'media-configuration-recovery-already-attempted' }
        [IO.File]::Replace($Backup,$Change.Path,$failed)
    }
    Assert-SourceAccessConfigurationChange $Change -Original
    $Change.RollbackVerified=$true
}
