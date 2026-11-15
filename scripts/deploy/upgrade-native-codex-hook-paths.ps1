#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourcePluginRoot,
    [Parameter(Mandatory)][string]$CachedPluginRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedAdapterSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedOriginalHooksSha256,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
function Assert-Physical([string]$Path) {
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){
        if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Hook upgrade refuses linked paths'}
    }
}
function Get-Normalised($Value){$Value|ConvertTo-Json -Depth 12 -Compress}
$source=[IO.Path]::GetFullPath($SourcePluginRoot).TrimEnd('\')
$cache=[IO.Path]::GetFullPath($CachedPluginRoot).TrimEnd('\')
foreach($root in @($source,$cache)){Assert-Physical $root}
$sourceIdentity=Get-Content -LiteralPath (Join-Path $source '.codex-plugin\plugin.json') -Raw|ConvertFrom-Json
$cacheIdentity=Get-Content -LiteralPath (Join-Path $cache '.codex-plugin\plugin.json') -Raw|ConvertFrom-Json
if($sourceIdentity.name -cne 'fluxknowledge' -or $cacheIdentity.name -cne 'fluxknowledge' -or
    $sourceIdentity.version -cne $cacheIdentity.version -or [IO.Path]::GetFileName($source) -cne 'fluxknowledge' -or
    [IO.Path]::GetFileName($cache) -cne $sourceIdentity.version){throw 'Exact FluxKnowledge plugin identity required'}
foreach($root in @($source,$cache)){
    foreach($relative in @('.codex-plugin\plugin.json','.mcp.json','hooks\hooks.json')){Assert-Physical (Join-Path $root $relative)}
}
if((Get-FileHash -LiteralPath (Join-Path $source '.mcp.json')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $cache '.mcp.json')).Hash){throw 'MCP definitions differ; preserve the installation'}
$adapter=Join-Path $source 'hooks\invoke-native-hook.ps1';Assert-Physical $adapter
if((Get-FileHash -LiteralPath $adapter).Hash -ine $ExpectedAdapterSha256){throw 'Canonical adapter hash changed'}
$literal="'"+$adapter.Replace("'","''")+"'"
function New-Handler([string]$Event,[string]$Status){
    [ordered]@{type='command';command="pwsh -NoProfile -File $literal $Event";
        commandWindows="powershell -NoProfile -ExecutionPolicy Bypass -File $literal $Event";statusMessage=$Status}
}
$definition=[ordered]@{hooks=[ordered]@{
    UserPromptSubmit=@([ordered]@{hooks=@((New-Handler 'UserPromptSubmit' 'Retrieving local FluxKnowledge context'))})
    PreCompact=@([ordered]@{matcher='manual|auto';hooks=@((New-Handler 'PreCompact' 'Preparing local turn capture'))})
    Stop=@([ordered]@{hooks=@((New-Handler 'Stop' 'Finalising local Codex turn'))})
}}
$normalised=Get-Normalised $definition
$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($definition|ConvertTo-Json -Depth 12).Replace("`r`n","`n")+"`n")
$targets=@($source,$cache)|ForEach-Object{
    $path=Join-Path $_ 'hooks\hooks.json'
    $hash=(Get-FileHash -LiteralPath $path).Hash
    $alreadyCanonical=(Get-Normalised (Get-Content -LiteralPath $path -Raw|ConvertFrom-Json)) -ceq $normalised
    if(!$alreadyCanonical -and $hash -ine $ExpectedOriginalHooksSha256){throw 'Hook definitions changed; no replacements started'}
    [pscustomobject]@{Path=$path;BeforeSha256=$hash;AlreadyCanonical=$alreadyCanonical;Changed=$false}
}
if($Apply){
    foreach($target in $targets){
        if($target.AlreadyCanonical){continue}
        Assert-Physical $target.Path
        if((Get-FileHash -LiteralPath $target.Path).Hash -cne $target.BeforeSha256){throw 'Hook definitions changed before replacement'}
        if((Get-FileHash -LiteralPath $adapter).Hash -ine $ExpectedAdapterSha256){throw 'Canonical adapter changed before replacement'}
        $temporary=Join-Path (Split-Path -Parent $target.Path) ('.hook-location-'+[guid]::NewGuid().ToString('N')+'.tmp')
        try{
            $file=[IO.File]::Open($temporary,'CreateNew','Write','None')
            try{$file.Write($bytes);$file.Flush($true)}finally{$file.Dispose()}
            Assert-Physical (Split-Path -Parent $target.Path)
            [IO.File]::Move($temporary,$target.Path,$true)
            $target.Changed=$true
        }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
    }
}
foreach($target in $targets){
    if($Apply -and (Get-Normalised (Get-Content -LiteralPath $target.Path -Raw|ConvertFrom-Json)) -cne $normalised){throw 'Canonical command verification failed'}
}
[ordered]@{Mode=if($Apply){'apply'}else{'plan'};Targets=$targets;CanonicalAdapter=$adapter;
    AdapterSha256=$ExpectedAdapterSha256;CacheAdapterRequired=$false;ServicesRestarted=0;HookInvocations=0;
    ReviewRequired=($Apply -and @($targets|Where-Object Changed).Count -gt 0);
    Resume='Already canonical definitions are preserved; an interrupted two-file upgrade can be rerun with the same hashes.'}|ConvertTo-Json -Depth 6
