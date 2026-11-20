# Native ports run inside the incremental updater's mutex. The installer identity
# pins each payload, task definition, database incarnation and namespace.
function Get-AllowlistManager {
    if(!('Microsoft.Web.Administration.ServerManager' -as [type])){
        Add-Type -Path (Join-Path $env:SystemRoot 'System32\inetsrv\Microsoft.Web.Administration.dll')
    }
    return [Microsoft.Web.Administration.ServerManager]::new()
}

function Get-AllowlistIis($Context,$Manager){
    $target=$Context.Target
    $site=$Manager.Sites[$target.Site];$pool=$Manager.ApplicationPools[$target.Site]
    if(!$site -or !$pool -or [string]$site.State -cne 'Started' -or $site.Applications.Count -ne 1 -or
        $site.Bindings.Count -ne 1 -or $site.Bindings[0].Protocol -cne 'http' -or
        $site.Bindings[0].BindingInformation -cne ('127.0.0.1:'+([uri]$Context.Binding.DashboardOrigin).Port+':') -or
        [string]$pool.ProcessModel.IdentityType -cne 'ApplicationPoolIdentity'){throw 'iis-binding-invalid'}
    $app=$site.Applications['/']
    if(!$app -or $app.ApplicationPoolName -cne $target.Site -or $app.VirtualDirectories.Count -ne 1 -or
        $app.VirtualDirectories['/'].PhysicalPath -ine $target.Web){throw 'iis-app-binding-invalid'}
    $appcmd=Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
    $owners=@(& $appcmd list app "/apppool.name:$($target.Site)" /text:app.name)
    if($LASTEXITCODE -ne 0 -or $owners.Count -ne 1 -or $owners[0] -cne ($target.Site+'/')){throw 'iis-pool-shared'}
    $hosting=$Manager.GetWebConfiguration($target.Site).GetSection('system.webServer/aspNetCore')
    if($target.Stage){
        if($hosting.GetAttributeValue('processPath') -ine '.\FluxKnowledge.StagingHost.exe' -or
            $hosting.GetAttributeValue('arguments') -cne ('--settings-file "'+$target.LiveRoot+'\Config\staging.json" --journal-iis') -or
            $hosting.GetAttributeValue('hostingModel') -ine 'outofprocess'){throw 'iis-stage-host-invalid'}
    }elseif($hosting.GetAttributeValue('hostingModel') -ine 'inprocess'){throw 'iis-production-host-invalid'}
    $pool
}

function Get-AllowlistTask($Context){
    $task=Get-SourceJournalHelperTaskObservation -InstallRoot $Context.Target.Helper -TaskName $Context.Target.Task
    if($task.DefinitionSha256 -cne $Context.Binding.TaskDefinitionSha256 -or $task.SecuritySha256 -cne $Context.Binding.TaskSecuritySha256){throw 'helper-task-drift'}
    $task
}

function Assert-AllowlistHelperRunning($Context){
    $task=Get-AllowlistTask $Context
    if(!$task.Running -or !$task.Enabled){throw 'helper-not-running'}
    $rows=@(Get-SourceJournalHelperNativeProcesses -Executable $task.Executable -IsExitedProcess $Context.Native.IsExitedProcess)
    if($rows.Count -ne 1 -or $rows[0].CommandLine -cnotin @(('"'+$task.Executable+'" '+$task.Arguments),($task.Executable+' '+$task.Arguments))){throw 'helper-process-unconfirmed'}
    $owner=Invoke-CimMethod -InputObject $rows[0] -MethodName GetOwnerSid -OperationTimeoutSec 5
    if($owner.ReturnValue -ne 0 -or $owner.Sid -cne $task.PrincipalSid){throw 'helper-process-owner-unconfirmed'}
}

function Read-AllowlistPair($Context){
    $appBytes=Read-AllowlistFile $Context.Target.AppConfig
    $helperBytes=Read-AllowlistFile $Context.Target.Policy 65536 -Private
    $app=ConvertFrom-UniqueConfigurationJson $appBytes;$helper=ConvertFrom-UniqueConfigurationJson $helperBytes
    if($app.SourceJournal.Enabled -isnot [bool] -or !$app.SourceJournal.Enabled -or $app.SourceJournal.PipeName -cne $Context.Binding.JournalPipeName -or
        $helper.Version -isnot [long] -or $helper.Version -ne 1 -or $helper.PipeName -cne $Context.Binding.JournalPipeName -or
        @($helper.Keys|Where-Object {$_ -cnotin @('Version','ApprovedVolumes','PipeName','RepairEnabled')}).Count -or
        ($helper.Contains('RepairEnabled') -and $helper.RepairEnabled -isnot [bool])){throw 'configuration-binding-mismatch'}
    $appSet=Get-AllowlistVolumeSet $app.SourceJournal.ApprovedVolumes
    $helperSet=Get-AllowlistVolumeSet $helper.ApprovedVolumes
    if($Context.Target.Stage){
        $permitted=Get-AllowlistVolumeSet $Context.Binding.StageVolumes
        if(!$permitted.IsSupersetOf($appSet) -or !$permitted.IsSupersetOf($helperSet)){throw 'staging-volume-outside-binding'}
    }
    @{AppBytes=$appBytes;HelperBytes=$helperBytes;AppVolumes=[string[]]$app.SourceJournal.ApprovedVolumes;HelperVolumes=[string[]]$helper.ApprovedVolumes}
}

function Test-AllowlistHold($Context){
    if(![IO.File]::Exists($Context.Target.Hold)){
        if(Test-Path -LiteralPath $Context.Target.Hold){throw 'invalid-hold'}
        return $false
    }
    $bytes=Read-AllowlistFile $Context.Target.Hold 256
    if([Text.Encoding]::UTF8.GetString($bytes) -cne ($Context.Release|ConvertTo-Json -Compress)){throw 'foreign-hold'}
    return $true
}

function Assert-AllowlistHold($Context){
    if(!(Test-AllowlistHold $Context)){throw 'owned-hold-missing'}
}

function Assert-AllowlistPredecessor($Context){
    $manager=Get-AllowlistManager
    try{if([string](Get-AllowlistIis $Context $manager).State -cne 'Started'){throw 'predecessor-pool-stopped'}}finally{$manager.Dispose()}
    Assert-AllowlistHelperRunning $Context
}

function Set-AllowlistHold($Context){
    if(Test-AllowlistHold $Context){return}
    Assert-AllowlistPredecessor $Context
    $bytes=[Text.Encoding]::UTF8.GetBytes(($Context.Release|ConvertTo-Json -Compress))
    $file=[IO.File]::Open($Context.Target.Hold,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$file.Write($bytes);$file.Flush($true)}finally{$file.Dispose()}
    Assert-AllowlistHold $Context
}

function Get-AllowlistProcesses($Context,$Pool){
    $catalog=[Collections.Generic.Dictionary[int,object]]::new()
    $queue=[Collections.Generic.Queue[int]]::new()
    foreach($worker in $Pool.WorkerProcesses){$queue.Enqueue($worker.ProcessId)}
    while($queue.Count){
        $pidValue=$queue.Dequeue()
        if($catalog.ContainsKey($pidValue)){continue}
        $row=Get-CimInstance Win32_Process -Filter "ProcessId=$pidValue" -OperationTimeoutSec 5
        if(!$row -or $row.CreationDate -isnot [datetime]){throw 'iis-process-observation-unconfirmed'}
        $created=$row.CreationDate.ToUniversalTime()
        $catalog.Add($pidValue,@{Id=$pidValue;Created=$created.Ticks.ToString()})
        foreach($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$pidValue" -OperationTimeoutSec 5)){
            if($child.CreationDate.ToUniversalTime() -ge $created){$queue.Enqueue([int]$child.ProcessId)}
        }
    }
    @($catalog.Values)
}

function Assert-AllowlistStopped($Context){
    Assert-AllowlistHold $Context
    $proof=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $Context.ProcessPath 131072 -Private)
    if($proof.OperationId -cne $Context.Id -or $proof.Processes -isnot [array]){throw 'stop-proof-invalid'}
    foreach($identity in $proof.Processes){
        $pidValue=[int]$identity.Id
        $row=Get-CimInstance Win32_Process -Filter "ProcessId=$pidValue" -OperationTimeoutSec 5
        if($row -and $row.CreationDate.ToUniversalTime().Ticks.ToString() -ceq $identity.Created){throw 'iis-descendant-not-exited'}
    }
    $manager=Get-AllowlistManager
    try{
        $pool=Get-AllowlistIis $Context $manager
        if([string]$pool.State -cne 'Stopped' -or $pool.WorkerProcesses.Count){throw 'iis-not-stopped'}
    }finally{$manager.Dispose()}
    if($Context.Target.Stage){
        # ANCM can launch the out-of-process host during pool shutdown, after the
        # initial tree snapshot. A process with this stage executable must exit too.
        $entry=(Join-Path $Context.Target.Web 'FluxKnowledge.StagingHost.exe').Replace('\','\\')
        $left=@(Get-CimInstance Win32_Process -Filter "ExecutablePath='$entry'" -OperationTimeoutSec 5)
        if($left.Count){throw 'staging-host-not-exited'}
    }
    & $Context.Native.AssertExited
}

function Stop-AllowlistOwners($Context){
    Assert-AllowlistHold $Context
    # Record the process tree before stopping IIS, including out-of-process ANCM
    # children. A fresh runner must still prove each exact incarnation exited.
    $sql=Get-AllowlistConnection (ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $Context.Target.AppConfig)) $Context.Target
    Invoke-WithHybridGpuDrain -ConnectionString $sql -TimeoutSeconds 120 -StopApplication {
        $manager=Get-AllowlistManager
        try{
            $pool=Get-AllowlistIis $Context $manager
            if([string]$pool.State -ceq 'Started'){
                $captured=@(Get-AllowlistProcesses $Context $pool)
                if([IO.File]::Exists($Context.ProcessPath)){
                    $old=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $Context.ProcessPath 131072 -Private)
                    if($old.OperationId -cne $Context.Id){throw 'stop-proof-invalid'}
                    # Retain observations across a lost stop response.
                    $captured+=@($old.Processes)
                }
                $unique=@($captured|Group-Object {([string]$_.Id)+'|'+$_.Created}|ForEach-Object {$_.Group[0]})
                if($unique.Count -gt 1024){throw 'process-proof-limit'}
                Write-HybridRebuildJson -Path $Context.ProcessPath -Value @{OperationId=$Context.Id;Processes=$unique}
                $null=$pool.Stop()
            }elseif([string]$pool.State -cne 'Stopped' -or ![IO.File]::Exists($Context.ProcessPath)){throw 'iis-stop-state-unproved'}
        }finally{$manager.Dispose()}
        $timer=[Diagnostics.Stopwatch]::StartNew()
        do{
            $manager=Get-AllowlistManager
            try{$pool=Get-AllowlistIis $Context $manager;$stopped=[string]$pool.State -ceq 'Stopped' -and !$pool.WorkerProcesses.Count}finally{$manager.Dispose()}
            if($stopped){break}; Start-Sleep -Milliseconds 200
        }while($timer.Elapsed.TotalSeconds -lt 120)
        if(!$stopped){throw 'iis-stop-timeout'}
    }
    $task=Get-AllowlistTask $Context
    if($task.Enabled){& $Context.Native.DisableTask}
    if($task.Running){& $Context.Native.StopTask}
    Assert-AllowlistStopped $Context
}

function Invoke-AllowlistAdmission($Context,[scriptblock]$Action){
    $bytes=Read-AllowlistFile $Context.Target.AppConfig
    $settings=ConvertFrom-UniqueConfigurationJson $bytes
    $sql=Get-AllowlistConnection $settings $Context.Target
    $mode=if($settings.Contains('IndexPublication') -and $settings.IndexPublication.Contains('Mode')){$settings.IndexPublication.Mode}else{'Legacy'}
    $payloads=@($Context.Payloads|ForEach-Object {@{Root=$_.Root;Role=$_.Role;Sha256=$_.Digest}})
    $web=@{Root=$Context.Target.Web;Role='Web';ExpectedSha256=($Context.Payloads | Where-Object Role -CEQ 'Web').Digest}
    $argsForIndex=@{ConnectionString=$sql;Payloads=@($web);WebIndexMode=$mode;ExpectedDatabaseIdentity=$Context.Binding.DatabaseIdentity;
        ReadPayloadFingerprint={param($path) Get-HybridPayloadFingerprint $path};ExpectedConfigurationPath=$Context.Target.AppConfig;
        ExpectedConfigurationSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))}
    $guard=$Action
    Invoke-IndexFormatAdmission @argsForIndex -Action {
        Invoke-SourceJournalApplicationAdmission -ReadState {Get-SourceJournalDatabaseState -ConnectionString $sql} -RequireFeatureSchema -ExpectedDatabaseIdentity $Context.Binding.DatabaseIdentity -ReviewedPayloads $payloads -CurrentPayloads $payloads -ReadPayloadFingerprint {param($path) Get-HybridPayloadFingerprint $path} -Action $guard
    }
}

function Start-AllowlistOwners($Context){
    Assert-AllowlistHold $Context
    Invoke-AllowlistAdmission $Context {
        $task=Get-AllowlistTask $Context
        if(!$task.Enabled){& $Context.Native.EnableTask}
        if(!$task.Running){& $Context.Native.StartTask}
        $manager=Get-AllowlistManager
        try{
            $pool=Get-AllowlistIis $Context $manager
            if([string]$pool.State -ceq 'Stopped'){$null=$pool.Start()}elseif([string]$pool.State -cne 'Started'){throw 'iis-start-state-unproved'}
        }finally{$manager.Dispose()}
    }
}

function Confirm-AllowlistTransport($Context,[string[]]$Allowed,[string[]]$Removed){
    Assert-AllowlistHold $Context
    $manager=Get-AllowlistManager
    try{if([string](Get-AllowlistIis $Context $manager).State -cne 'Started'){throw 'iis-start-unproved'}}finally{$manager.Dispose()}
    $policy=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile (Join-Path $Context.Target.Helper 'policy.json') 65536 -Private)
    $poolSid=([Security.Principal.NTAccount]::new('IIS APPPOOL',$Context.Target.Site)).Translate([Security.Principal.SecurityIdentifier]).Value
    if($policy.IndexerSid -cne $poolSid){throw 'policy-caller-invalid'}
    $app=ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $Context.Target.AppConfig)
    $repair=if($app.Contains('SourceAccess') -and $app.SourceAccess.Enabled){'NotApproved'}else{'Disabled'}
    $uri=$Context.Binding.DashboardOrigin+'/health/source-journal-helper'
    if($Removed.Count){$uri+='?'+(@($Removed|ForEach-Object {'removedVolume='+[uri]::EscapeDataString($_)}) -join '&')}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do{
        Assert-AllowlistHold $Context
        try{
            Assert-AllowlistHelperRunning $Context
            $response=Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 12 -MaximumRedirection 0 -NoProxy
            $expected=Get-AllowlistVolumeSet $Allowed
            if($response.Version -ne 1 -or $response.VolumesChecked -ne $Allowed.Count -or
                !$expected.SetEquals((Get-AllowlistVolumeSet $response.ApprovedVolumes)) -or
                $response.PipeName -cne $Context.Binding.JournalPipeName -or $response.RepairTransport -cne $repair -or
                $response.CallerSid -cne $poolSid){throw 'held-transport-mismatch'}
            if($Removed.Count -and ($response.RemovedVolumes.Count -ne $Removed.Count -or
                !(Get-AllowlistVolumeSet $Removed).SetEquals((Get-AllowlistVolumeSet $response.RemovedVolumes)))){throw 'removed-volume-still-admitted'}
            Assert-AllowlistHold $Context
            return
        }catch{
            if($timer.Elapsed.TotalSeconds -ge 60){throw 'held-transport-unproved'}
            Start-Sleep -Milliseconds 300
        }
    }while($true)
}
