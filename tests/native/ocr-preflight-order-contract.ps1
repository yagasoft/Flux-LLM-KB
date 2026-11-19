param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
$fixtureRoot='I:\Codex Workspaces\Staging\FluxKnowledge'
$area=Join-Path $fixtureRoot ('FluxOcrPreflight_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($area)
$operation=Join-Path $area 'operation'
$smi=Join-Path $area 'synthetic-smi.ps1'
[IO.File]::WriteAllText($smi,"'<nvidia_smi_log><driver_version>fixture</driver_version><gpu><uuid>fixture</uuid><processes /></gpu></nvidia_smi_log>';exit 0")
$box=@{PublishCalls=0;TerminalReads=0;SchemaDrift=$true}
$history=@(Get-OcrReviewedMigrationPrefix $SourceRoot -All)
$contract=Get-OcrLifecycleMigrationContract
function git {
    param([Parameter(ValueFromRemainingArguments)][object[]]$GitArgs)
    if($GitArgs -contains 'status'){return}
    if($GitArgs -contains 'rev-parse'){return ('a'*40)}
    throw 'Unexpected fixture Git operation.'
}
function Read-OcrReviewedDesktopClassifications {param($Path) @{InputSha256=('A'*64);Entries=@()}}
function Get-DeploymentSqlConnectionString {'synthetic'}
function Get-RepositoryRecoveryCompanionConnectionString {'synthetic'}
function Get-OcrLifecycleDatabaseState {
    param($ConnectionString)
    @{Identity='synthetic';History=$history;Collation=$contract.DatabaseCollation
        SchemaSha256=$(if($box.SchemaDrift){'0'*64}else{$contract.TargetSchemaSha256})
        Schema=@{Columns=@(1..19);Constraints=@(1..5 | ForEach-Object {@{is_disabled=$false;is_not_trusted=$false}})}}
}
function Get-OcrOsSnapshot {@()}
function Get-HybridIisWorkerIds {param($AppCmdPath,$PoolName) @()}
function Get-Command {param($Name,$ErrorAction) @{Source=$smi}}
function Get-OcrTerminalCleanupBinding {param($ConnectionString,$BatchId,$RecoveryInstanceId) $box.TerminalReads++;throw 'fixture-terminal-binding-drift'}
function dotnet {$box.PublishCalls++;throw 'fixture-publish-before-preflight'}
try{
    $reason=''
    try{New-OcrLifecycleRecoveryPlan $SourceRoot $operation ([Guid]::NewGuid()) (Join-Path $area 'classifications.json') -CleanupOnly|Out-Null}
    catch{$reason=$_.Exception.Message}
    if($reason -notlike '*schema-or-history-changed*' -or $box.TerminalReads -ne 0 -or $box.PublishCalls -ne 0 -or [IO.Directory]::Exists($operation)){
        throw "Schema drift did not fail during preflight: reason=$reason publish=$($box.PublishCalls) terminal=$($box.TerminalReads)"
    }
    $box.SchemaDrift=$false
    $reason=''
    try{New-OcrLifecycleRecoveryPlan $SourceRoot $operation ([Guid]::NewGuid()) (Join-Path $area 'classifications.json') -CleanupOnly|Out-Null}
    catch{$reason=$_.Exception.Message}
    if($reason -notlike '*fixture-terminal-binding-drift*' -or $box.TerminalReads -ne 1 -or $box.PublishCalls -ne 0 -or [IO.Directory]::Exists($operation)){
        throw "Prerequisite failure created/published a candidate: reason=$reason publish=$($box.PublishCalls) terminal=$($box.TerminalReads)"
    }
    'OCR preflight rejected schema and terminal binding drift before creating an operation directory or invoking the publisher.'
}finally{
    $resolved=[IO.Path]::GetFullPath($area)
    if([IO.Path]::GetDirectoryName($resolved) -cne $fixtureRoot -or
        ![IO.Path]::GetFileName($resolved).StartsWith('FluxOcrPreflight_')){throw 'Unsafe preflight fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
