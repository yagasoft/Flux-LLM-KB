Set-StrictMode -Version Latest

function Get-SourceAccessConfigurationBytes {
    param([string]$Path)
    $current=[IO.Path]::GetFullPath($Path)
    while ($current) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Source-access configuration path is a reparse point.' }
        $current=[IO.Path]::GetDirectoryName($current)
    }
    $file=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($file.Length -le 0 -or $file.Length -gt 2097152) { throw 'Source-access configuration length is invalid.' }
        $bytes=[byte[]]::new([int]$file.Length); $file.ReadExactly($bytes); return ,$bytes
    } finally { $file.Dispose() }
}
function Get-SourceAccessConfigurationHash {
    param([byte[]]$Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}
function New-SourceAccessConfigurationChange {
    param([Parameter(Mandatory)][string]$Path,[string]$PipeName='', [switch]$Disable)
    if ($Disable -eq ![string]::IsNullOrEmpty($PipeName)) { throw 'Choose one explicit pipe name or Disable.' }
    if (!$Disable -and ($PipeName.Length -gt 128 -or $PipeName -cnotmatch '^FluxKnowledge\.SourceAccess\.[A-Za-z0-9.-]+$')) { throw 'Invalid source-access pipe name.' }
    $before=Get-SourceAccessConfigurationBytes $Path
    $configuration=[Text.Encoding]::UTF8.GetString($before).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable -DateKind String
    if ($configuration -isnot [Collections.IDictionary]) { throw 'Source-access configuration must be a JSON object.' }
    if (!$configuration.Contains('SourceAccess')) { $configuration.SourceAccess=[ordered]@{} }
    if ($configuration.SourceAccess -isnot [Collections.IDictionary]) { throw 'SourceAccess must be a JSON object.' }
    $configuration.SourceAccess.Enabled=!$Disable
    if (!$Disable) { $configuration.SourceAccess.PipeName=$PipeName }
    $after=[Text.UTF8Encoding]::new($false).GetBytes(($configuration | ConvertTo-Json -Depth 100))
    return [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path); Before=$before; After=$after;
        BeforeHash=(Get-SourceAccessConfigurationHash $before); AfterHash=(Get-SourceAccessConfigurationHash $after);
        Enabled=(!$Disable); PipeName=$PipeName; Attempted=$false; RollbackVerified=$true}
}
function Write-SourceAccessConfigurationBytes {
    param($Change,[byte[]]$Expected,[byte[]]$Desired)
    # Preserve the existing file identity and ACL. The updater holds IIS stopped;
    # a partial write or unexpected drift keeps the deployment hold for recovery.
    $current=Get-SourceAccessConfigurationBytes $Change.Path
    if ((Get-SourceAccessConfigurationHash $current) -cne (Get-SourceAccessConfigurationHash $Expected)) { throw 'Source-access configuration drift; preserve the deployment hold.' }
    $file=[IO.FileStream]::new($Change.Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
    try {
        if ($file.Length -ne $Expected.Length) { throw 'Source-access configuration length drift.' }
        $held=[byte[]]::new($Expected.Length); $file.ReadExactly($held)
        if ((Get-SourceAccessConfigurationHash $held) -cne (Get-SourceAccessConfigurationHash $Expected)) { throw 'Source-access configuration changed before write.' }
        $Change.Attempted=$true; $Change.RollbackVerified=$false
        $file.Position=0; $file.Write($Desired); $file.SetLength($Desired.Length); $file.Flush($true)
        $file.Position=0; $verified=[byte[]]::new($Desired.Length); $file.ReadExactly($verified)
        if ((Get-SourceAccessConfigurationHash $verified) -cne (Get-SourceAccessConfigurationHash $Desired)) { throw 'Source-access configuration write is unconfirmed.' }
    } finally { $file.Dispose() }
}
function Set-SourceAccessConfigurationChange {
    param([Parameter(Mandatory)]$Change)
    Write-SourceAccessConfigurationBytes $Change $Change.Before $Change.After
}
function Assert-SourceAccessConfigurationChange {
    param([Parameter(Mandatory)]$Change,[switch]$Original)
    $expected=if($Original){$Change.BeforeHash}else{$Change.AfterHash}
    if ((Get-SourceAccessConfigurationHash (Get-SourceAccessConfigurationBytes $Change.Path)) -cne $expected) { throw 'Source-access configuration differs from the reviewed deployment state.' }
}
function Restore-SourceAccessConfigurationChange {
    param([Parameter(Mandatory)]$Change)
    $current=Get-SourceAccessConfigurationHash (Get-SourceAccessConfigurationBytes $Change.Path)
    if ($current -cne $Change.BeforeHash) { Write-SourceAccessConfigurationBytes $Change $Change.After $Change.Before }
    Assert-SourceAccessConfigurationChange $Change -Original
    $Change.RollbackVerified=$true
}

function ConvertFrom-UniqueConfigurationJson([byte[]]$Bytes) {
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes).TrimStart([char]0xfeff)
    $document=[Text.Json.JsonDocument]::Parse($text)
    try {
        function Assert-UniqueProperty($Element) {
            if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
                $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Element.EnumerateObject()) {
                    if (!$names.Add($property.Name)) {throw 'Duplicate configuration property.'}
                    Assert-UniqueProperty $property.Value
                }
            } elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
                foreach ($item in $Element.EnumerateArray()) {Assert-UniqueProperty $item}
            }
        }
        Assert-UniqueProperty $document.RootElement
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) {throw 'Configuration must be a JSON object.'}
    } finally {$document.Dispose()}
    ConvertFrom-Json -InputObject $text -AsHashtable -DateKind String
}

function New-SourceJournalConfigurationChange {
    param([Parameter(Mandatory)][string]$Path,[string]$OptionsPath='',
        [string]$ExpectedOptionsSha256='', [switch]$Disable)
    if ($Disable) {
        if ($OptionsPath -or $ExpectedOptionsSha256) {throw 'Journal disable cannot replace qualifications.'}
    } elseif (!$OptionsPath -or $ExpectedOptionsSha256 -cnotmatch '^[A-Fa-f0-9]{64}$') {throw 'Journal activation requires hash-bound options.'}
    $before=Get-SourceAccessConfigurationBytes $Path
    $configuration=ConvertFrom-UniqueConfigurationJson $before
    if (!$configuration.Contains('SourceJournal')) {$configuration.SourceJournal=[ordered]@{}}
    if ($configuration.SourceJournal -isnot [Collections.IDictionary]) {throw 'SourceJournal must be a JSON object.'}
    if ($Disable) {
        $configuration.SourceJournal.Enabled=$false
        $configuration.SourceJournal.SealingEnabled=$false
    } else {
        $bytes=Get-SourceAccessConfigurationBytes $OptionsPath
        if ((Get-SourceAccessConfigurationHash $bytes) -cne $ExpectedOptionsSha256.ToUpperInvariant()) {throw 'Journal options differ from the reviewed hash.'}
        $options=ConvertFrom-UniqueConfigurationJson $bytes
        $keys=@('Enabled','PipeName','ApprovedVolumes','PollSeconds','MetadataEntriesPerSecond','SealingEnabled','SealQualifications')
        if ($options.Count -ne $keys.Count -or @($options.Keys|Where-Object {$_ -cnotin $keys}).Count -or
            $options.Enabled -isnot [bool] -or !$options.Enabled -or $options.SealingEnabled -isnot [bool] -or
            $options.PipeName -isnot [string] -or $options.PipeName.Length -gt 128 -or
            $options.PipeName -cnotmatch '^FluxKnowledge\.SourceJournal\.[A-Za-z0-9.-]*$' -or
            $options.PollSeconds -isnot [long] -or $options.PollSeconds -lt 1 -or $options.PollSeconds -gt 60 -or
            $options.MetadataEntriesPerSecond -isnot [long] -or $options.MetadataEntriesPerSecond -lt 1 -or $options.MetadataEntriesPerSecond -gt 4096) {throw 'Journal options are invalid.'}
        if ($options.ApprovedVolumes -isnot [array] -or $options.ApprovedVolumes.Count -lt 1 -or $options.ApprovedVolumes.Count -gt 16) {throw 'Approved journal volumes are required.'}
        $volumes=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($volume in $options.ApprovedVolumes) {
            if ($volume -isnot [string] -or $volume -inotmatch '^\\\\\?\\Volume\{[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\}\\$' -or !$volumes.Add($volume)) {throw 'Journal volume identity is invalid or duplicated.'}
        }
        if ($options.SealQualifications -isnot [array] -or $options.SealQualifications.Count -gt 1024) {throw 'Journal qualifications must be a bounded array.'}
        $roots=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($qualification in $options.SealQualifications) {
            $required=@('RootIdentityFingerprint','EnvironmentFingerprint','Scope','QualificationId')
            $allowed=$required+@('ProviderName','ProviderVersion')
            if ($qualification -isnot [Collections.IDictionary] -or @($qualification.Keys|Where-Object {$_ -cnotin $allowed}).Count -or
                @($required|Where-Object {!$qualification.Contains($_)}).Count) {throw 'Journal qualification fields are invalid.'}
            foreach ($fingerprint in @('RootIdentityFingerprint','EnvironmentFingerprint')) {
                if ($qualification[$fingerprint] -isnot [string] -or $qualification[$fingerprint] -cnotmatch '^[a-f0-9]{64}$') {throw 'Journal qualification fingerprint is invalid.'}
            }
            if (!$roots.Add($qualification.RootIdentityFingerprint)) {throw 'Journal root qualification is duplicated.'}
            foreach ($field in @('Scope','QualificationId')) {
                if ($qualification[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace($qualification[$field]) -or $qualification[$field].Length -gt 256) {throw 'Journal qualification label is invalid.'}
            }
            $hasProvider=$qualification.Contains('ProviderName') -or $qualification.Contains('ProviderVersion')
            if ($hasProvider) {
                foreach ($field in @('ProviderName','ProviderVersion')) {
                    if (!$qualification.Contains($field) -or $qualification[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace($qualification[$field]) -or $qualification[$field].Length -gt 256) {throw 'Journal provider identity is incomplete.'}
                }
            } elseif (!$qualification.Scope.StartsWith('local-ntfs-',[StringComparison]::Ordinal) -and $qualification.Scope -cne 'isolated-local-ntfs-staging') {throw 'Cloud qualification requires its tested provider identity.'}
        }
        # Fixed insertion order keeps PlanOnly and Apply hashes stable across processes.
        $desired=[ordered]@{}
        foreach ($key in $keys) {$desired[$key]=$options[$key]}
        $desired.ApprovedVolumes=@($options.ApprovedVolumes|ForEach-Object {$_.ToUpperInvariant()})
        $configuration.SourceJournal=$desired
    }
    $after=[Text.UTF8Encoding]::new($false).GetBytes(($configuration|ConvertTo-Json -Depth 100))
    [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Before=$before;After=$after;
        BeforeHash=(Get-SourceAccessConfigurationHash $before);AfterHash=(Get-SourceAccessConfigurationHash $after);
        Enabled=(!$Disable);OptionsSha256=$ExpectedOptionsSha256.ToUpperInvariant();Attempted=$false;RollbackVerified=$true}
}
Export-ModuleMember -Function New-SourceAccessConfigurationChange,New-SourceJournalConfigurationChange,Set-SourceAccessConfigurationChange,Assert-SourceAccessConfigurationChange,Restore-SourceAccessConfigurationChange,ConvertFrom-UniqueConfigurationJson,Get-SourceAccessConfigurationBytes
