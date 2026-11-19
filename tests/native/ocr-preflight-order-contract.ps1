param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
$area=Join-Path ([IO.Path]::GetTempPath()) ('FluxOcrPreflight_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($area)
$operation=Join-Path $area 'operation'
$smi=Join-Path $area 'synthetic-smi.ps1'
[IO.File]::WriteAllText($smi,"'<nvidia_smi_log><driver_version>fixture</driver_version><gpu><uuid>fixture</uuid><processes /></gpu></nvidia_smi_log>';exit 0")
$box=@{PublishCalls=0;TerminalReads=0}
function git {
    param([Parameter(ValueFromRemainingArguments)][object[]]$GitArgs)
    if($GitArgs -contains 'status'){return}
    if($GitArgs -contains 'rev-parse'){return ('a'*40)}
    throw 'Unexpected fixture Git operation.'
}
function Read-OcrReviewedDesktopClassifications {param($Path) @{InputSha256=('A'*64);Entries=@()}}
function Get-DeploymentSqlConnectionString {'synthetic'}
function Get-RepositoryRecoveryCompanionConnectionString {'synthetic'}
function Get-OcrLifecycleDatabaseState {param($ConnectionString) @{Identity='synthetic';History=@('fixture')}}
function Get-OcrReviewedMigrationPrefix {param($Root,[switch]$All) @('fixture')}
function Get-OcrOsSnapshot {@()}
function Get-HybridIisWorkerIds {param($AppCmdPath,$PoolName) @()}
function Get-Command {param($Name,$ErrorAction) @{Source=$smi}}
function Get-OcrTerminalCleanupBinding {param($ConnectionString,$BatchId,$RecoveryInstanceId) $box.TerminalReads++;throw 'fixture-terminal-binding-drift'}
function dotnet {$box.PublishCalls++;throw 'fixture-publish-before-preflight'}
try{
    $reason=''
    try{New-OcrLifecycleRecoveryPlan $SourceRoot $operation ([Guid]::NewGuid()) (Join-Path $area 'classifications.json') -CleanupOnly|Out-Null}
    catch{$reason=$_.Exception.Message}
    if($reason -notlike '*fixture-terminal-binding-drift*' -or $box.TerminalReads -ne 1 -or $box.PublishCalls -ne 0 -or [IO.Directory]::Exists($operation)){
        throw "Prerequisite failure created/published a candidate: reason=$reason publish=$($box.PublishCalls) terminal=$($box.TerminalReads)"
    }
    'OCR preflight drift refused before creating an operation directory or invoking any publisher; no production calls.'
}finally{
    $resolved=[IO.Path]::GetFullPath($area)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetFileName($resolved).StartsWith('FluxOcrPreflight_')){throw 'Unsafe preflight fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
