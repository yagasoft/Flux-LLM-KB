Set-StrictMode -Version Latest

function Assert-SegmentedUpdatePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    for($current=$full;$current;$current=[IO.Path]::GetDirectoryName($current)) {
        if([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw 'index-update-reparse-point'}
        }
    }
    return $full
}

function Read-SegmentedUpdateJson([string]$Path,[string]$ExpectedSha256='') {
    $path=Assert-SegmentedUpdatePath $Path
    $file=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($file.Length -lt 1 -or $file.Length -gt 1MB){throw 'index-update-receipt-length-invalid'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
        if($ExpectedSha256 -and [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ine $ExpectedSha256){throw 'index-update-receipt-hash-changed'}
        return ([Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -AsHashtable -DateKind String)
    }finally{$file.Dispose()}
}

function Write-SegmentedUpdateJson([string]$Path,$Value) {
    $path=Assert-SegmentedUpdatePath $Path
    $temporary=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 40))
    $file=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$file.Write($bytes);$file.Flush($true)}finally{$file.Dispose()}
    [IO.File]::Move($temporary,$path,$true)
}

function Invoke-SegmentedIndexUpdateFlow {
    param([Parameter(Mandatory)][string]$StatePath,[Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$PlanSha256,
        [Parameter(Mandatory)][hashtable]$Ports,[switch]$OfflineMaintenance,[switch]$ResumeOfflineMaintenance)
    if($ResumeOfflineMaintenance -and !$OfflineMaintenance){throw 'index-update-offline-mode-required'}
    $state=if(Test-Path -LiteralPath $StatePath){Read-SegmentedUpdateJson $StatePath}else{
        @{Version=1;PlanSha256=$PlanSha256;SchemaVerified=$false;BackupsReady=$false;PayloadsReady=$false;
            ProjectionPlanSha256=$null;GenerationId=$null;ConfigurationApplied=$false;
            OfflineMaintenance=[bool]$OfflineMaintenance;OfflineReady=$false;
            StartIntentUtc=$null;ReleaseIntentUtc=$null;Completed=$false;LastFailure=$null}
    }
    if($state.Version -ne 1 -or $state.PlanSha256 -cne $PlanSha256){throw 'index-update-plan-binding-changed'}
    $storedOffline=$state.ContainsKey('OfflineMaintenance') -and [bool]$state.OfflineMaintenance
    if($storedOffline -ne [bool]$OfflineMaintenance){throw 'index-update-offline-mode-binding-changed'}
    if(!$state.ContainsKey('OfflineReady')){$state.OfflineReady=$false}
    if($ResumeOfflineMaintenance -and !$state.OfflineReady){throw 'index-update-offline-checkpoint-required'}
    if($state.Completed){& $Ports.AssertCompleted $state;return $state}
    $save={Write-SegmentedUpdateJson $StatePath $state}.GetNewClosure()
    $writer=$null
    try {
        # An earlier request may have started a process even when its response was
        # lost. Never infer a stopped owner from the old pre-start observation.
        $after=if($state.ReleaseIntentUtc){$state.ReleaseIntentUtc}else{$state.StartIntentUtc}
        & $Ports.AssertStopped $after
        & $Ports.AssertBindings $state
        & $save
        if(!$state.SchemaVerified){& $Ports.UpgradeSchema;$state.SchemaVerified=$true;& $save}
        if(!$state.PayloadsReady) {
            $writer=& $Ports.BeginWriter
            if(!$state.BackupsReady){& $Ports.BackupPayloads;$state.BackupsReady=$true;& $save}
            & $Ports.PlacePayloads;$state.PayloadsReady=$true;& $save
            & $Ports.CompleteWriter $writer
            & $Ports.CloseWriter $writer;$writer=$null
        }
        if(!$state.ProjectionPlanSha256){$state.ProjectionPlanSha256=& $Ports.CapturePlan;& $save}
        if(!$state.GenerationId) {
            $result=& $Ports.Convert $state
            $id=[guid]::Empty
            if($result.FormatVersion -ne 2 -or ![guid]::TryParse([string]$result.Id,[ref]$id) -or $id -eq [guid]::Empty){throw 'index-update-conversion-result-invalid'}
            $state.GenerationId=$id.ToString('D');& $save
        }
        & $Ports.AssertCurrent $state
        if(!$state.ConfigurationApplied){& $Ports.Configure;$state.ConfigurationApplied=$true;& $save}
        if($OfflineMaintenance -and !$ResumeOfflineMaintenance) {
            & $Ports.AssertBindings $state
            & $Ports.AssertCurrent $state
            & $Ports.AssertStopped $after
            $state.OfflineReady=$true;$state.LastFailure=$null;& $save
            return $state
        }
        # Only the following immediate start and hold release may admit work.
        # Their callbacks independently fence exact generation/config/payloads.
        & $Ports.AssertCurrent $state
        $state.StartIntentUtc=[DateTime]::UtcNow.ToString('O');$state.ReleaseIntentUtc=$null;& $save
        & $Ports.Start $state
        & $Ports.Validate $state
        $state.ReleaseIntentUtc=[DateTime]::UtcNow.ToString('O');& $save
        & $Ports.Release $state
        $state.Completed=$true;$state.LastFailure=$null;& $save
        return $state
    } catch {
        $failure=$_
        if($state.StartIntentUtc){& $Ports.EnsureHold}
        $reason=$failure.Exception.Message
        $state.LastFailure=if($reason -cmatch '^index-[a-z-]{1,140}$'){$reason}else{'index-update-failed-reconcile-held-state'}
        & $save
        throw $failure
    } finally {
        if($null -ne $writer){& $Ports.CloseWriter $writer}
    }
}

Export-ModuleMember -Function Assert-SegmentedUpdatePath,Read-SegmentedUpdateJson,Write-SegmentedUpdateJson,Invoke-SegmentedIndexUpdateFlow
