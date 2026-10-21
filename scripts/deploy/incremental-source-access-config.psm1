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
    if (!$configuration.Contains('SourceAccess')) { $configuration.SourceAccess=@{} }
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
Export-ModuleMember -Function New-SourceAccessConfigurationChange,Set-SourceAccessConfigurationChange,Assert-SourceAccessConfigurationChange,Restore-SourceAccessConfigurationChange
