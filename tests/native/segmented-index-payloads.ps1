param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-iis.ps1')
$root=Join-Path ([IO.Path]::GetTempPath()) ('FluxSegmentedPayload_'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $source=Join-Path $root 'source';$destination=Join-Path $root 'destination'
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'nested'))
    [void][IO.Directory]::CreateDirectory($destination)
    [IO.File]::WriteAllText((Join-Path $source 'nested/dependency.dll'),'complete reviewed bytes')
    [IO.File]::WriteAllText((Join-Path $source 'entry.dll'),'reviewed entry')
    [IO.File]::WriteAllText((Join-Path $destination 'obsolete.dll'),'predecessor')
    [void][IO.Directory]::CreateDirectory((Join-Path $destination 'nested'))
    [IO.File]::WriteAllText((Join-Path $destination 'nested/dependency.dll'),'partial interrupted copy')
    $acl=(Get-Acl -LiteralPath $destination).Sddl
    $expected=Get-HybridPayloadFingerprint $source
    Copy-SegmentedIndexPayload -Source $source -Destination $destination -ExpectedSha256 $expected
    if((Get-HybridPayloadFingerprint $destination) -cne $expected -or (Get-Acl -LiteralPath $destination).Sddl -cne $acl){throw 'Copy failed or replaced the live directory ACL.'}
    $when=(Get-Item -LiteralPath (Join-Path $destination 'entry.dll')).LastWriteTimeUtc
    Copy-SegmentedIndexPayload -Source $source -Destination $destination -ExpectedSha256 $expected
    if((Get-Item -LiteralPath (Join-Path $destination 'entry.dll')).LastWriteTimeUtc -ne $when){throw 'Already verified payload was rewritten.'}
    $escape=Join-Path $root 'outside';[void][IO.Directory]::CreateDirectory($escape)
    [IO.File]::WriteAllText((Join-Path $escape 'preserve.txt'),'unrelated')
    $link=Join-Path $source 'escape'
    New-Item -ItemType Junction -Path $link -Target $escape | Out-Null
    try {
        $refused=$false
        try{Copy-SegmentedIndexPayload -Source $source -Destination $destination -ExpectedSha256 $expected}catch{if($_.Exception.Message -like '*reparse*'){$refused=$true}else{throw}}
        if(!$refused){throw 'Source path escape was followed.'}
    }finally{[IO.Directory]::Delete($link)}
    $link=Join-Path $destination 'escape'
    New-Item -ItemType Junction -Path $link -Target $escape | Out-Null
    try {
        $refused=$false
        try{Copy-SegmentedIndexPayload -Source $source -Destination $destination -ExpectedSha256 $expected}catch{if($_.Exception.Message -like '*reparse*'){$refused=$true}else{throw}}
        if(!$refused){throw 'Destination path escape was followed.'}
    }finally{[IO.Directory]::Delete($link)}
    if([IO.File]::ReadAllText((Join-Path $escape 'preserve.txt')) -cne 'unrelated'){throw 'Unrelated bytes changed.'}
    'PASS: interrupted copy resumes, obsolete payload files retire, repeated placement preserves verified bytes and root ACL, source/destination escapes refuse.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotlike 'FluxSegmentedPayload_*'){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
