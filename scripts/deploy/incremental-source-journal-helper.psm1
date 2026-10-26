Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'source-access-install-file.ps1')
Import-Module (Join-Path $PSScriptRoot 'incremental-source-access-config.psm1') -ErrorAction Stop
Import-Module (Join-Path $PSScriptRoot 'incremental-source-journal-migration.psm1') -ErrorAction Stop

function Get-SourceJournalHelperHash([byte[]]$Bytes){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))}
function Read-SourceJournalHelperFile([string]$Path,[long]$Maximum=2097152){
    $file=[FluxSourceAccessInstallFile]::Read($Path,$Maximum)
    try{
        [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $Path).GetSecurityDescriptorBinaryForm(),$false)
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes);return ,$bytes
    }finally{$file.Dispose()}
}
function Get-SourceJournalHelperPayloadManifest {
    param([Parameter(Mandatory)][string]$Path)
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    $held=[FluxSourceAccessInstallFile]::OpenPrivateDirectory($root)
    try{
        $items=@(Get-ChildItem -LiteralPath $root -Recurse -Force)
        if($items.Count -gt 4096){throw 'journal-helper-payload-entry-limit'}
        foreach($item in $items){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'journal-helper-payload-reparse-point'}
            [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $item.FullName).GetSecurityDescriptorBinaryForm(),$false)
        }
        $files=@(foreach($item in $items|Where-Object {!$_.PSIsContainer}){
            $file=[FluxSourceAccessInstallFile]::Read($item.FullName,1073741824)
            try{[pscustomobject]@{Path=[IO.Path]::GetRelativePath($root,$item.FullName);Bytes=$file.Length;
                Sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file))}}finally{$file.Dispose()}
        })
        $files=@($files|Sort-Object Path)
        foreach($required in @('FluxKnowledge.Cli.exe','FluxKnowledge.Cli.dll','FluxKnowledge.Integrations.dll',
            'FluxKnowledge.Cli.deps.json','FluxKnowledge.Cli.runtimeconfig.json')){
            if(@($files|Where-Object Path -CEQ $required).Count -ne 1){throw 'journal-helper-payload-required-file-missing'}
        }
        if(($files|Measure-Object Bytes -Sum).Sum -gt 2147483648){throw 'journal-helper-payload-byte-limit'}
        $runtime=ConvertFrom-UniqueConfigurationJson (Read-SourceJournalHelperFile (Join-Path $root 'FluxKnowledge.Cli.runtimeconfig.json'))
        if(!$runtime.Contains('runtimeOptions') -or $runtime.runtimeOptions.Contains('additionalProbingPaths') -or $runtime.runtimeOptions.Contains('startupHooks')){
            throw 'journal-helper-payload-runtime-override'
        }
        $json=ConvertTo-Json -InputObject @($files) -Depth 4 -Compress
        [pscustomobject]@{Files=$files;Sha256=(Get-SourceJournalHelperHash ([Text.Encoding]::UTF8.GetBytes($json)))}
    }finally{$held.Dispose()}
}
function Write-SourceJournalHelperCheckpoint($State,[string]$Phase){
    $State.Phase=$Phase
    $receipt=[ordered]@{Version=1;ReleaseId=$State.ReleaseId;InstallRoot=$State.InstallRoot;Task=$State.OriginalTask;
        OriginalPayloadSha256=$State.OriginalPayloadSha256;CandidatePayloadSha256=$State.CandidatePayloadSha256;
        RepairPolicySha256=$State.RepairPolicySha256;RootReadPlanSha256=$State.RootReadPlanSha256;
        OriginalJournalPolicySha256=$State.Policy.BeforeHash;JournalPolicySha256=$State.Policy.AfterHash;
        Phase=$Phase;ObservedAtUtc=[DateTimeOffset]::UtcNow}
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($receipt|ConvertTo-Json -Depth 8))
    $file=[IO.FileStream]::new($State.CheckpointPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$file.Write($bytes);$file.SetLength($bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
}
function New-SourceJournalHelperUpdate {
    param([Parameter(Mandatory)][string]$InstallRoot,[Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][string]$ReleaseId,[Parameter(Mandatory)][string]$CandidatePath,
        [Parameter(Mandatory)][string]$ExpectedCandidateSha256,[Parameter(Mandatory)][hashtable]$TaskObservation,
        [Parameter(Mandatory)][string]$PipeName,[Parameter(Mandatory)][string[]]$ApprovedVolumes)
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallRoot));$drive=[IO.Path]::GetPathRoot($root)
    if(![IO.Path]::IsPathFullyQualified($InstallRoot) -or [IO.Path]::GetDirectoryName($root) -ine $drive -or
        [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed -or [IO.DriveInfo]::new($drive).DriveFormat -ine 'NTFS' -or
        !(($root -ieq 'I:\FluxKnowledgeSourceAccess' -and $TaskName -ceq 'FluxKnowledge.SourceAccess') -or
          ([IO.Path]::GetFileName($root) -cmatch '^FluxKnowledgeSourceAccess-Staging-[a-zA-Z0-9-]+$' -and $TaskName -cmatch '^FluxKnowledge\.SourceAccess-Staging-[a-zA-Z0-9-]+$')) -or
        $ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or $ExpectedCandidateSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){
        throw 'journal-helper-update-binding-invalid'
    }
    if($PipeName.Length -gt 128 -or $PipeName -cnotmatch '^FluxKnowledge\.SourceJournal\.[A-Za-z0-9.-]+$' -or
        $ApprovedVolumes.Count -lt 1 -or $ApprovedVolumes.Count -gt 16 -or
        @($ApprovedVolumes|Sort-Object -Unique).Count -ne $ApprovedVolumes.Count -or
        @($ApprovedVolumes|Where-Object {$_ -inotmatch '^\\\\\?\\Volume\{[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\}\\$'}).Count){
        throw 'journal-helper-journal-policy-invalid'
    }
    $payload=Join-Path $root 'payload'
    if($TaskObservation.TaskName -cne $TaskName -or $TaskObservation.PrincipalSid -cne 'S-1-5-18' -or
        $TaskObservation.Executable -ine (Join-Path $payload 'FluxKnowledge.Cli.exe') -or $TaskObservation.WorkingDirectory -ine $payload -or
        $TaskObservation.Arguments -cne ('source-access serve --broker-root "'+$root+'"') -or
        $TaskObservation.DefinitionSha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or $TaskObservation.SecuritySha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or
        $TaskObservation.Enabled -isnot [bool] -or $TaskObservation.Running -isnot [bool] -or
        ($TaskObservation.Running -and !$TaskObservation.Enabled)){throw 'journal-helper-existing-task-unreviewed'}
    [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $drive).GetSecurityDescriptorBinaryForm(),$true)
    $held=[FluxSourceAccessInstallFile]::OpenPrivateDirectory($root)
    try{
        $original=Get-SourceJournalHelperPayloadManifest $payload
        $candidate=Get-SourceJournalHelperPayloadManifest $CandidatePath
        if($candidate.Sha256 -ine $ExpectedCandidateSha256){throw 'journal-helper-candidate-changed'}
        $repair=Read-SourceJournalHelperFile (Join-Path $root 'policy.json')
        $readPlan=Read-SourceJournalHelperFile (Join-Path $root 'root-read-plan.json')
        $policyPath=Join-Path $root 'journal-policy.json';$before=$null;$repairEnabled=$true
        if(Test-Path -LiteralPath $policyPath){
            $before=Read-SourceJournalHelperFile $policyPath 65536
            $existing=ConvertFrom-UniqueConfigurationJson $before
            if($existing.Version -isnot [long] -or $existing.Version -ne 1 -or
                $existing.PipeName -isnot [string] -or $existing.PipeName -cnotmatch '^FluxKnowledge\.SourceJournal\.[A-Za-z0-9.-]+$' -or
                $existing.ApprovedVolumes -isnot [array] -or $existing.ApprovedVolumes.Count -lt 1 -or $existing.ApprovedVolumes.Count -gt 16 -or
                @($existing.Keys|Where-Object {$_ -cnotin @('Version','PipeName','ApprovedVolumes','RepairEnabled')}).Count){throw 'journal-helper-existing-journal-policy-invalid'}
            $repairEnabled=$existing.Contains('RepairEnabled') -and $existing.RepairEnabled -ceq $true
            if($existing.Contains('RepairEnabled') -and $existing.RepairEnabled -isnot [bool]){throw 'journal-helper-existing-repair-setting-invalid'}
        }
        $after=[Text.UTF8Encoding]::new($false).GetBytes(([ordered]@{Version=1;PipeName=$PipeName;
            ApprovedVolumes=@($ApprovedVolumes|ForEach-Object {$_.ToUpperInvariant()});RepairEnabled=[bool]$repairEnabled}|ConvertTo-Json -Depth 4))
        $security=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
        $descriptor=[byte[]]::new($security.BinaryLength);$security.GetBinaryForm($descriptor,0)
        $updates=Join-Path $root 'updates'
        $updatesLease=if(Test-Path -LiteralPath $updates){[FluxSourceAccessInstallFile]::OpenPrivateDirectory($updates)}else{[FluxSourceAccessInstallFile]::CreatePrivateDirectory($updates,$descriptor)}
        try{
            $release=Join-Path $updates $ReleaseId
            $releaseLease=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($release,$descriptor)
            try{
                $staged=Join-Path $release 'candidate';$null=[IO.Directory]::CreateDirectory($staged)
                foreach($item in $candidate.Files){
                    $source=Join-Path $CandidatePath $item.Path;$destination=Join-Path $staged $item.Path
                    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
                    $inputFile=[FluxSourceAccessInstallFile]::Read($source,$item.Bytes)
                    try{
                        $outputFile=[IO.FileStream]::new($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                        try{$inputFile.CopyTo($outputFile);$outputFile.Flush($true)}finally{$outputFile.Dispose()}
                    }finally{$inputFile.Dispose()}
                }
                if((Get-SourceJournalHelperPayloadManifest $staged).Sha256 -cne $candidate.Sha256){throw 'journal-helper-protected-copy-unconfirmed'}
                $state=@{Version=1;InstallRoot=$root;TaskName=$TaskName;ReleaseId=$ReleaseId;OriginalTask=$TaskObservation.Clone();
                    OriginalPayloadSha256=$original.Sha256;CandidatePayloadSha256=$candidate.Sha256;
                    RepairPolicySha256=(Get-SourceJournalHelperHash $repair);RootReadPlanSha256=(Get-SourceJournalHelperHash $readPlan);
                    Policy=[pscustomobject]@{Path=$policyPath;Before=$before;After=$after;BeforeHash=$(if($null -ne $before){Get-SourceJournalHelperHash $before}else{''});
                        AfterHash=(Get-SourceJournalHelperHash $after);Attempted=$false;RollbackVerified=$true};
                    PayloadRoot=$payload;CandidateRoot=$staged;PreviousRoot=(Join-Path $release 'previous');FailedRoot=(Join-Path $release 'failed');
                    RootLease=$held;CheckpointPath=(Join-Path $release 'state.json');Phase='Prepared';Attempted=$false;
                    OldPayloadMoved=$false;CandidatePlaced=$false;Completed=$false;RollbackVerified=$false}
                Write-SourceJournalHelperCheckpoint $state 'Prepared'
                $held=$null
                return $state
            }finally{$releaseLease.Dispose()}
        }finally{$updatesLease.Dispose()}
    }finally{if($null -ne $held){$held.Dispose()}}
}
function Assert-SourceJournalHelperTaskBindings($State,$Ports){
    [FluxSourceAccessInstallFile]::ValidatePrivateDirectory($State.RootLease,$State.InstallRoot)
    $task=&$Ports.ReadTask
    foreach($field in @('TaskName','PrincipalSid','Executable','Arguments','WorkingDirectory','DefinitionSha256','SecuritySha256')){
        if($task[$field] -cne $State.OriginalTask[$field]){throw 'journal-helper-task-binding-changed'}
    }
}
function Assert-SourceJournalHelperBindings($State,$Ports){
    Assert-SourceJournalHelperTaskBindings $State $Ports
    if((Get-SourceJournalHelperHash (Read-SourceJournalHelperFile (Join-Path $State.InstallRoot 'policy.json'))) -cne $State.RepairPolicySha256 -or
        (Get-SourceJournalHelperHash (Read-SourceJournalHelperFile (Join-Path $State.InstallRoot 'root-read-plan.json'))) -cne $State.RootReadPlanSha256){throw 'journal-helper-source-policy-binding-changed'}
    $expected=if($State.Policy.Attempted -and !$State.Policy.RollbackVerified){$State.Policy.AfterHash}else{$State.Policy.BeforeHash}
    if(!$expected){
        if(Test-Path -LiteralPath $State.Policy.Path){throw 'journal-helper-journal-policy-binding-changed'}
    }elseif(!(Test-Path -LiteralPath $State.Policy.Path) -or
        (Get-SourceJournalHelperHash (Read-SourceJournalHelperFile $State.Policy.Path 65536)) -cne $expected){
        throw 'journal-helper-journal-policy-binding-changed'
    }
}
function Stop-SourceJournalHelperForUpdate($State,$Ports){
    &$Ports.AssertApplicationStopped
    # Policy drift forbids restoration, but must not prevent cancellation of the
    # exact still-owned task/process after an uncertain start.
    Assert-SourceJournalHelperTaskBindings $State $Ports
    &$Ports.DisableTask
    &$Ports.StopTask
    &$Ports.AssertExited
    $current=&$Ports.ReadTask
    if($current.Enabled -or $current.Running){throw 'journal-helper-stop-unproved'}
    Assert-SourceJournalHelperTaskBindings $State $Ports
}
function Restore-SourceJournalHelperPolicy($State){
    if(!$State.Policy.Attempted){return}
    if($null -ne $State.Policy.Before){Restore-SourceAccessConfigurationChange -Change $State.Policy;return}
    if((Get-SourceJournalHelperHash (Read-SourceJournalHelperFile $State.Policy.Path 65536)) -cne $State.Policy.AfterHash){throw 'journal-helper-new-policy-drift-retain-hold'}
    Remove-Item -LiteralPath $State.Policy.Path -Force -ErrorAction Stop
    $State.Policy.RollbackVerified=$true
}
function Invoke-SourceJournalHelperUpdate {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][hashtable]$Ports)
    foreach($port in @('ReadTask','AssertApplicationStopped','DisableTask','StopTask','AssertExited','EnableTask','StartTask','Probe')){
        if(!$Ports.ContainsKey($port) -or $Ports[$port] -isnot [scriptblock]){throw 'journal-helper-update-port-missing'}
    }
    if($State.Attempted){throw 'journal-helper-attempt-requires-reconciliation'}
    &$Ports.AssertApplicationStopped
    Assert-SourceJournalHelperBindings $State $Ports
    $task=&$Ports.ReadTask
    if($task.Enabled -cne $State.OriginalTask.Enabled -or $task.Running -cne $State.OriginalTask.Running){throw 'journal-helper-task-state-changed-before-attempt'}
    if((Get-SourceJournalHelperPayloadManifest $State.PayloadRoot).Sha256 -cne $State.OriginalPayloadSha256 -or
        (Get-SourceJournalHelperPayloadManifest $State.CandidateRoot).Sha256 -cne $State.CandidatePayloadSha256){throw 'journal-helper-payload-changed-before-attempt'}
    $State.Attempted=$true
    Write-SourceJournalHelperCheckpoint $State 'StopIntent'
    try{
        Stop-SourceJournalHelperForUpdate $State $Ports
        Assert-SourceJournalHelperBindings $State $Ports
        Move-Item -LiteralPath $State.PayloadRoot -Destination $State.PreviousRoot -ErrorAction Stop
        $State.OldPayloadMoved=$true
        Move-Item -LiteralPath $State.CandidateRoot -Destination $State.PayloadRoot -ErrorAction Stop
        $State.CandidatePlaced=$true
        if($null -ne $State.Policy.Before){Set-SourceAccessConfigurationChange -Change $State.Policy}
        else{
            $file=[IO.FileStream]::new($State.Policy.Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
            $State.Policy.Attempted=$true;$State.Policy.RollbackVerified=$false
            try{$file.Write($State.Policy.After);$file.Flush($true)}finally{$file.Dispose()}
        }
        Assert-SourceAccessConfigurationChange -Change $State.Policy
        Assert-SourceJournalHelperBindings $State $Ports
        if((Get-SourceJournalHelperPayloadManifest $State.PayloadRoot).Sha256 -cne $State.CandidatePayloadSha256){throw 'journal-helper-placed-payload-unconfirmed'}
        Write-SourceJournalHelperCheckpoint $State 'StartIntent'
        if($State.OriginalTask.Enabled){&$Ports.EnableTask}
        if($State.OriginalTask.Running){&$Ports.StartTask;&$Ports.Probe $true}
        Assert-SourceJournalHelperBindings $State $Ports
        $State.Completed=$true
        Write-SourceJournalHelperCheckpoint $State 'Completed'
    }catch{
        $failure=$_
        # No file/policy rollback or old restart until exact native exit is proved.
        # An unproved exit or any drift leaves the task disabled and hold intact.
        try{
            Stop-SourceJournalHelperForUpdate $State $Ports
            Assert-SourceJournalHelperBindings $State $Ports
            if($State.CandidatePlaced){Move-Item -LiteralPath $State.PayloadRoot -Destination $State.FailedRoot -ErrorAction Stop}
            if($State.OldPayloadMoved){Move-Item -LiteralPath $State.PreviousRoot -Destination $State.PayloadRoot -ErrorAction Stop}
            Restore-SourceJournalHelperPolicy $State
            if((Get-SourceJournalHelperPayloadManifest $State.PayloadRoot).Sha256 -cne $State.OriginalPayloadSha256){throw 'journal-helper-predecessor-payload-unconfirmed'}
            Assert-SourceJournalHelperBindings $State $Ports
            if($State.OriginalTask.Enabled){&$Ports.EnableTask}
            if($State.OriginalTask.Running){&$Ports.StartTask;&$Ports.Probe $false}
            $State.RollbackVerified=$true
            Write-SourceJournalHelperCheckpoint $State 'RolledBack'
        }catch{
            $recoveryFailure=$_
            try{Stop-SourceJournalHelperForUpdate $State $Ports}
            catch{
                throw ('journal-helper-update-and-recovery-unconfirmed-retain-hold: update='+$failure.Exception.Message+
                    '; recovery='+$recoveryFailure.Exception.Message+'; terminal-exit-unproved='+$_.Exception.Message)
            }
            throw ('journal-helper-update-and-recovery-unconfirmed-retain-hold: update='+$failure.Exception.Message+
                '; recovery='+$recoveryFailure.Exception.Message+'; task-disabled-native-exit-proved')
        }
        throw $failure
    }
}
function Test-SourceJournalHeldHelperRecovery {
    param([Parameter(Mandatory)][string]$HoldPath,[Parameter(Mandatory)][string]$InstallRoot,[string]$TaskName='FluxKnowledge.SourceAccess')
    if(!(Test-Path -LiteralPath $HoldPath)){return $false}
    $releaseId=Get-SourceJournalHeldReleaseId $HoldPath
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallRoot))
    $updates=Join-Path $root 'updates';$release=Join-Path $updates $releaseId
    if(!(Test-Path -LiteralPath $release)){return $false}
    $leases=[Collections.Generic.List[IDisposable]]::new()
    try{
        foreach($path in @($root,$updates,$release)){$leases.Add([FluxSourceAccessInstallFile]::OpenPrivateDirectory($path))}
        $checkpoint=ConvertFrom-UniqueConfigurationJson (Read-SourceJournalHelperFile (Join-Path $release 'state.json') 65536)
        $keys=@('Version','ReleaseId','InstallRoot','Task','OriginalPayloadSha256','CandidatePayloadSha256','RepairPolicySha256',
            'RootReadPlanSha256','OriginalJournalPolicySha256','JournalPolicySha256','Phase','ObservedAtUtc')
        if($checkpoint.Version -isnot [long] -or $checkpoint.Version -ne 1 -or $checkpoint.ReleaseId -cne $releaseId -or $checkpoint.InstallRoot -ine $root -or
            $checkpoint.Count -ne $keys.Count -or @($checkpoint.Keys|Where-Object {$_ -cnotin $keys}).Count -or
            $checkpoint.Phase -isnot [string] -or $checkpoint.Phase -cnotin @('Prepared','StopIntent','Stopped','StartIntent','Completed','RolledBack') -or
            $checkpoint.Task -isnot [Collections.IDictionary] -or $checkpoint.Task.TaskName -cne $TaskName -or
            $checkpoint.Task.PrincipalSid -cne 'S-1-5-18' -or $checkpoint.Task.Executable -ine (Join-Path $root 'payload/FluxKnowledge.Cli.exe') -or
            $checkpoint.Task.Arguments -cne ('source-access serve --broker-root "'+$root+'"') -or
            $checkpoint.Task.WorkingDirectory -ine (Join-Path $root 'payload')){
            throw 'journal-helper-held-checkpoint-invalid'
        }
        if($checkpoint.Task.Enabled -isnot [bool] -or $checkpoint.Task.Running -isnot [bool] -or
            ($checkpoint.Task.Running -and !$checkpoint.Task.Enabled) -or
            $checkpoint.OriginalJournalPolicySha256 -isnot [string] -or
            ($checkpoint.OriginalJournalPolicySha256 -and $checkpoint.OriginalJournalPolicySha256 -cnotmatch '^[A-Fa-f0-9]{64}$') -or
            $checkpoint.ObservedAtUtc -isnot [string] -or [string]::IsNullOrWhiteSpace($checkpoint.ObservedAtUtc)){
            throw 'journal-helper-held-checkpoint-invalid'
        }
        foreach($field in @('DefinitionSha256','SecuritySha256')){
            if($checkpoint.Task[$field] -isnot [string] -or $checkpoint.Task[$field] -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'journal-helper-held-checkpoint-invalid'}
        }
        foreach($field in @('OriginalPayloadSha256','CandidatePayloadSha256','RepairPolicySha256','RootReadPlanSha256','JournalPolicySha256')){
            if($checkpoint[$field] -isnot [string] -or $checkpoint[$field] -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'journal-helper-held-checkpoint-invalid'}
        }
        return $checkpoint.Phase -cin @('StopIntent','Stopped','StartIntent')
    }finally{foreach($lease in $leases){$lease.Dispose()}}
}

function Close-SourceJournalHelperUpdate([hashtable]$State){if($State -and $State.RootLease){$State.RootLease.Dispose();$State.RootLease=$null}}
Export-ModuleMember -Function Get-SourceJournalHelperPayloadManifest,New-SourceJournalHelperUpdate,Invoke-SourceJournalHelperUpdate,Close-SourceJournalHelperUpdate,Test-SourceJournalHeldHelperRecovery
