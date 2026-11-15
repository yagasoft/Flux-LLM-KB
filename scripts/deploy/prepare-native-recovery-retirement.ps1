#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OperatorRoot,
    [Parameter(Mandatory)][string]$CurrentReleasePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][int]$HelperProcessId,
    [string]$RecoveryRoot='I:\FluxKnowledge\Recovery',
    [string]$IndexRoot='I:\FluxKnowledge\Data\Index',
    [string]$AppRoot='I:\FluxKnowledge\App',
    [string]$OutlookRoot='C:\inetpub\FluxKnowledge\outlook-host',
    [string]$HelperRoot='I:\FluxKnowledgeSourceAccess\payload',
    [string]$Database='FluxKnowledge',
    [string]$SiteUrl='http://127.0.0.1:5137',
    [string]$AppPool='FluxKnowledge'
)
$ErrorActionPreference='Stop'
function Assert-Physical([string]$Path){
    for($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)){
        if((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked retirement input'}
    }
}
function Read-JsonSql([string]$Sql){
    $command=$connection.CreateCommand();$command.CommandTimeout=5;$command.CommandText=$Sql
    $reader=$command.ExecuteReader();$text=[Text.StringBuilder]::new()
    try{while($reader.Read()){[void]$text.Append($reader.GetString(0));if($text.Length -gt 1MB){throw 'Metadata query budget exceeded'}}}finally{$reader.Dispose();$command.Dispose()}
    if($text.Length){$text.ToString()|ConvertFrom-Json}
}
function Snapshot([string]$Root){
    Assert-Physical $Root
    $files=@(Get-ChildItem -LiteralPath $Root -Recurse -Force|ForEach-Object{
        if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked runtime payload'}
        if(!$_.PSIsContainer){[ordered]@{RelativePath=[IO.Path]::GetRelativePath($Root,$_.FullName);Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}}
    })
    if($files.Count -lt 1 -or $files.Count -gt 2048){throw 'Runtime inventory budget exceeded'}
    [ordered]@{RootPath=[IO.Path]::GetFullPath($Root);Files=$files}
}
foreach($path in @($OperatorRoot,$CurrentReleasePath,$RecoveryRoot,$IndexRoot,$AppRoot,$OutlookRoot,$HelperRoot)){Assert-Physical $path}
$holdPath=Join-Path (Split-Path -Parent $AppRoot) 'Runtime\deployment-validation-hold.json'
if(Test-Path -LiteralPath $holdPath){throw 'A deployment hold remains active'}
if($Database -in @('master','model','msdb','tempdb') -or $Database -notmatch '^[A-Za-z0-9_]+$'){throw 'Invalid application catalogue'}
if((Invoke-WebRequest -Uri ($SiteUrl.TrimEnd('/')+'/health/ready') -TimeoutSec 10).StatusCode -ne 200){throw 'Known-good running application required'}
Add-Type -Path (Join-Path $OperatorRoot 'FluxKnowledge.Integrations.dll')
$probe=[FluxKnowledge.Integrations.Windows.WindowsInteractiveGpuOwnerProbe]::new()
$fingerprint=$probe.Current.MachineFingerprint
$recovery=[IO.Path]::GetFullPath($RecoveryRoot).TrimEnd('\')
$release=[IO.Path]::GetFullPath($CurrentReleasePath).TrimEnd('\')
if([IO.Path]::GetDirectoryName($release) -ine (Join-Path $recovery 'IncrementalUpdates')){throw 'Current release is outside the exact recovery layout'}
$entry=Join-Path $AppRoot 'FluxKnowledge.Web.dll'
$savedEntry=Join-Path $release 'candidate\FluxKnowledge.Web.dll'
if((Get-FileHash -LiteralPath $entry).Hash -cne (Get-FileHash -LiteralPath $savedEntry).Hash){throw 'Current release does not match the live entry assembly'}
$allProcesses=@(Get-CimInstance Win32_Process)
$references=@($allProcesses|Where-Object {$_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.IndexOf($recovery+'\',[StringComparison]::OrdinalIgnoreCase) -ge 0})
if($references.Count){throw 'A live process still references Recovery; preserve its packet'}
foreach($service in Get-CimInstance Win32_Service){if($service.PathName -and $service.PathName.IndexOf($recovery+'\',[StringComparison]::OrdinalIgnoreCase) -ge 0){throw 'A service still references Recovery'}}
foreach($task in Get-ScheduledTask){foreach($action in $task.Actions){if(($action.Execute+' '+$action.Arguments).IndexOf($recovery+'\',[StringComparison]::OrdinalIgnoreCase) -ge 0){throw 'A registered task still references Recovery'}}}
[xml]$workers=(& "$env:windir\System32\inetsrv\appcmd.exe" list wp "/apppool.name:$AppPool" /xml)
if($LASTEXITCODE -ne 0){throw 'IIS owner observation failed'}
$workerIds=@($workers.appcmd.WP|ForEach-Object{[int]$_.GetAttribute('WP.NAME')})
if($workerIds.Count -ne 1){throw 'One stable application worker is required'}
$owners=@(($workerIds+[int[]]@($HelperProcessId))|ForEach-Object{
    $process=Get-Process -Id $_
    [ordered]@{ProcessId=$process.Id;StartedAtUtc=([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToString('O');MachineFingerprint=$fingerprint}
})
$helper=@($allProcesses|Where-Object ProcessId -eq $HelperProcessId)
if($helper.Count -ne 1 -or !$helper[0].CommandLine -or $helper[0].CommandLine.IndexOf($HelperRoot,[StringComparison]::OrdinalIgnoreCase) -lt 0){throw 'The helper does not match its reviewed payload root'}
$connection=[Data.SqlClient.SqlConnection]::new("Data Source=localhost;Initial Catalog=$Database;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=5;ConnectRetryCount=0;Pooling=False;ApplicationIntent=ReadOnly")
try{
    $connection.Open()
    $state=@(Read-JsonSql 'SELECT * FROM dbo.IndexState FOR JSON PATH;')[0]
    $incarnation=@(Read-JsonSql 'SELECT CONVERT(nvarchar(36),database_guid) AS Id FROM sys.database_recovery_status WHERE database_id=DB_ID() FOR JSON PATH;')[0].Id
    $writers=@(Read-JsonSql 'SELECT TOP (1025) * FROM dbo.IndexRecoveryWriters ORDER BY Id FOR JSON PATH;')
    if($writers.Count -gt 1024){throw 'Writer metadata budget exceeded'}
    foreach($writer in $writers){
        if($writer.RecoveryRoot -ine $recovery){throw 'Writer belongs to another root'}
        $owner=[FluxKnowledge.Application.Gpu.GpuInteractiveOwnerIdentity]::new([int]$writer.OwnerProcessId,[DateTimeOffset]$writer.OwnerStartedAtUtc,[string]$writer.OwnerMachineFingerprint)
        if($probe.Observe($owner).ToString() -cne 'Exited'){throw 'Exact writer process exit not proven'}
    }
    $rebuilds=@(Read-JsonSql 'SELECT TOP (129) Id,TargetEpoch,ManifestHash,CompletedAtUtc,SupersedesOperationId FROM dbo.CorpusRebuildOperations ORDER BY CreatedAtUtc DESC FOR JSON PATH;')
    if($rebuilds.Count -gt 128){throw 'Rebuild metadata budget exceeded'}
    $baseline=@($rebuilds|Where-Object {$_.CompletedAtUtc -and $_.TargetEpoch -eq $state.CorpusEpoch}|Select-Object -First 1)
}finally{$connection.Dispose()}
$packets=@(Get-ChildItem -LiteralPath $recovery -Force|ForEach-Object{
    if(!$_.PSIsContainer){throw 'Unclassified recovery root file'}
    if($_.Name -ceq 'IncrementalUpdates'){Get-ChildItem -LiteralPath $_.FullName -Force}else{$_}
})
if($packets.Count -gt 512 -or @($packets|Where-Object {!$_.PSIsContainer}).Count){throw 'Recovery packet budget or shape changed'}
$orderedPackets=@($packets|Where-Object FullName -ine $release)
$orderedPackets+=@($packets|Where-Object FullName -ieq $release)
$moves=@($orderedPackets|ForEach-Object{
    Assert-Physical $_.FullName
    [ordered]@{RelativePath=[IO.Path]::GetRelativePath($recovery,$_.FullName);DirectoryIdentity=[FluxKnowledge.Integrations.Files.PhysicalFileIdentity]::GetDirectory($_.FullName).IdentityFingerprint}
})
$operation=[guid]::NewGuid()
$parent=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath));Assert-Physical $parent
$quarantine=Join-Path $parent ('retired-recovery-'+$operation.ToString('N'))
New-Item -ItemType Directory -Path $quarantine|Out-Null
$snapshots=@(Snapshot $AppRoot;Snapshot $OutlookRoot;Snapshot $HelperRoot)
$plan=[ordered]@{Version=1;OperationId=$operation;DatabaseIncarnation=$incarnation;RecoveryRoot=$recovery;IndexRoot=[IO.Path]::GetFullPath($IndexRoot);
    ApplicationEntry=[IO.Path]::GetFullPath($entry);ApplicationSha256=(Get-FileHash -LiteralPath $entry).Hash.ToLowerInvariant();
    RetainedApplicationEntry=[IO.Path]::GetRelativePath($recovery,$savedEntry);QuarantineRoot=$quarantine;
    RecoveryIdentity=[FluxKnowledge.Integrations.Files.PhysicalFileIdentity]::GetDirectory($recovery).IdentityFingerprint;
    CorpusEpoch=$state.CorpusEpoch;CorpusVersion=$state.CorpusVersion;ActiveGeneration=$state.ActiveIndexGenerationId;
    CompletedRebuildId=if($baseline.Count){$baseline[0].Id}else{$null};CompletedRebuildManifestHash=if($baseline.Count){$baseline[0].ManifestHash}else{$null};
    ServiceOwners=$owners;Writers=$writers;Moves=$moves;RetainedPackets=@();RuntimeSnapshots=$snapshots;RequiredAbsentPaths=@($holdPath)}
$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($plan|ConvertTo-Json -Depth 12)+"`n")
$output=[IO.File]::Open([IO.Path]::GetFullPath($OutputPath),'CreateNew','Write','None')
try{$output.Write($bytes);$output.Flush($true)}finally{$output.Dispose()}
[ordered]@{Plan=[IO.Path]::GetFullPath($OutputPath);PlanSha256=(Get-FileHash -LiteralPath $OutputPath).Hash;Packets=$moves.Count;
    Writers=$writers.Count;RuntimeFiles=($snapshots|ForEach-Object{$_.Files.Count}|Measure-Object -Sum).Sum;Quarantine=$quarantine;
    SqlMutations=0;ArchiveMoves=0;BackupsCreated=0;Consequence='Saved rollback copies become eligible for user deletion after native seal and source cleanup verification.'}|ConvertTo-Json
