Set-StrictMode -Version Latest

function Assert-SourceJournalHelperNativeTaskTarget([string]$InstallRoot,[string]$TaskName){
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallRoot))
    if(![IO.Path]::IsPathFullyQualified($InstallRoot) -or [IO.Path]::GetDirectoryName($root) -ine 'I:\' -or
        !(($root -ieq 'I:\FluxKnowledgeSourceAccess' -and $TaskName -ceq 'FluxKnowledge.SourceAccess') -or
          ([IO.Path]::GetFileName($root) -cmatch '^FluxKnowledgeSourceAccess-Staging-[A-Za-z0-9-]+$' -and
           $TaskName -cmatch '^FluxKnowledge\.SourceAccess-Staging-[A-Za-z0-9-]+$'))){throw 'journal-helper-native-task-target-invalid'}
    $root
}

function ConvertFrom-SourceJournalHelperTaskSnapshot {
    param([Parameter(Mandatory)][string]$Xml,[Parameter(Mandatory)][string]$SecurityDescriptor,
        [Parameter(Mandatory)][string]$InstallRoot,[Parameter(Mandatory)][string]$TaskName,
        [Parameter(Mandatory)][bool]$Enabled,[Parameter(Mandatory)][bool]$Running,[Nullable[int]]$NativeLogonType=$null)
    $root=Assert-SourceJournalHelperNativeTaskTarget $InstallRoot $TaskName
    $security=[Security.AccessControl.RawSecurityDescriptor]::new($SecurityDescriptor)
    # Task Scheduler normalises its COM descriptor and can omit the protected
    # control flag. Require a real DACL and inspect all grants, including inherited
    # ones; the complete returned descriptor remains pinned before every action.
    if($null -eq $security.Owner -or $security.Owner.Value -cnotin @('S-1-5-18','S-1-5-32-544') -or
        $null -eq $security.DiscretionaryAcl -or
        !($security.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclPresent)){
        throw 'journal-helper-native-task-security-invalid'
    }
    foreach($ace in $security.DiscretionaryAcl){
        if($ace -isnot [Security.AccessControl.CommonAce] -or
            ($ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and
             $ace.SecurityIdentifier.Value -cnotin @('S-1-5-18','S-1-5-32-544'))){throw 'journal-helper-native-task-untrusted-access'}
    }
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver=$null;$settings.MaxCharactersInDocument=1048576
    $text=[IO.StringReader]::new($Xml);$reader=[Xml.XmlReader]::Create($text,$settings)
    try{$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)}finally{$reader.Dispose();$text.Dispose()}
    $ns=[Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
    $principals=$document.SelectNodes('/t:Task/t:Principals/t:Principal',$ns)
    $actions=$document.SelectNodes('/t:Task/t:Actions/*',$ns)
    $taskSettings=$document.SelectNodes('/t:Task/t:Settings',$ns)
    if($principals.Count -ne 1 -or $actions.Count -ne 1 -or $actions[0].LocalName -cne 'Exec' -or
        $actions[0].NamespaceURI -cne $ns.LookupNamespace('t') -or $taskSettings.Count -ne 1){throw 'journal-helper-native-task-shape-invalid'}
    $principal=$principals[0];$action=$actions[0]
    $xmlLogonTypes=$principal.SelectNodes('t:LogonType',$ns)
    if($principal.SelectSingleNode('t:UserId',$ns).InnerText -cnotin @('S-1-5-18','SYSTEM','NT AUTHORITY\SYSTEM') -or
        $null -eq $NativeLogonType -or $NativeLogonType -ne 5 -or $xmlLogonTypes.Count -gt 1 -or
        ($xmlLogonTypes.Count -eq 1 -and $xmlLogonTypes[0].InnerText -cne 'ServiceAccount') -or
        $principal.SelectSingleNode('t:RunLevel',$ns).InnerText -cne 'HighestAvailable') {throw 'journal-helper-native-task-principal-invalid'}
    $payload=Join-Path $root 'payload'
    $executable=$action.SelectSingleNode('t:Command',$ns).InnerText
    $arguments=$action.SelectSingleNode('t:Arguments',$ns).InnerText
    $working=$action.SelectSingleNode('t:WorkingDirectory',$ns).InnerText
    if($executable -ine (Join-Path $payload 'FluxKnowledge.Cli.exe') -or $working -ine $payload -or
        $arguments -cne ('source-access serve --broker-root "'+$root+'"')){throw 'journal-helper-native-task-action-invalid'}
    # Enabled is the only definition property the update is allowed to change.
    # Keep every trigger's Enabled field and all other definition bytes bound.
    $enabledNodes=$document.SelectNodes('/t:Task/t:Settings/t:Enabled',$ns)
    if($enabledNodes.Count -gt 1){throw 'journal-helper-native-task-enabled-invalid'}
    foreach($node in @($enabledNodes)){$null=$node.ParentNode.RemoveChild($node)}
    $hash={param([string]$Value)[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value)))}
    @{TaskName=$TaskName;PrincipalSid='S-1-5-18';Executable=$executable;Arguments=$arguments;WorkingDirectory=$working;
        Enabled=$Enabled;Running=$Running;DefinitionSha256=(&$hash $document.OuterXml);
        SecuritySha256=(&$hash $security.GetSddlForm([Security.AccessControl.AccessControlSections]::All))}
}

function Get-SourceJournalHelperTaskObservation {
    param([Parameter(Mandatory)][string]$InstallRoot,[Parameter(Mandatory)][string]$TaskName)
    $null=Assert-SourceJournalHelperNativeTaskTarget $InstallRoot $TaskName
    $scheduler=New-Object -ComObject 'Schedule.Service';$scheduler.Connect()
    $task=$scheduler.GetFolder('\').GetTask($TaskName)
    ConvertFrom-SourceJournalHelperTaskSnapshot -Xml $task.Xml -SecurityDescriptor $task.GetSecurityDescriptor(7) -InstallRoot $InstallRoot -TaskName $TaskName `
        -Enabled ([bool]$task.Enabled) -Running ([bool]($task.State -in @(2,4) -or $task.GetInstances(0).Count -gt 0)) `
        -NativeLogonType ([int]$task.Definition.Principal.LogonType)
}

function Get-SourceJournalHelperNativeProcesses {
    param([Parameter(Mandatory)][string]$Executable)
    foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='FluxKnowledge.Cli.exe'" -ErrorAction Stop)){
        if(!$process.ExecutablePath){throw 'journal-helper-native-process-path-unavailable'}
        if($process.ExecutablePath.Equals($Executable,[StringComparison]::OrdinalIgnoreCase)){$process}
    }
}

function New-SourceJournalHelperNativeTaskPorts {
    param([Parameter(Mandatory)][hashtable]$State,[Parameter(Mandatory)][scriptblock]$AssertApplicationStopped,
        [Parameter(Mandatory)][scriptblock]$Probe)
    $null=Assert-SourceJournalHelperNativeTaskTarget $State.InstallRoot $State.TaskName
    $context=@{State=$State;Captured=[Collections.Generic.List[Diagnostics.Process]]::new()}
    $read={incremental-source-journal-helper-task\Get-SourceJournalHelperTaskObservation -InstallRoot $context.State.InstallRoot -TaskName $context.State.TaskName}.GetNewClosure()
    $owned={
        $current=&$read
        foreach($key in @('TaskName','PrincipalSid','Executable','Arguments','WorkingDirectory','DefinitionSha256','SecuritySha256')){
            if($current[$key] -cne $context.State.OriginalTask[$key]){throw 'journal-helper-native-task-binding-changed'}
        }
        $current
    }.GetNewClosure()
    $disable={
        $null=&$owned
        $null=Disable-ScheduledTask -TaskName $context.State.TaskName -TaskPath '\' -ErrorAction Stop
    }.GetNewClosure()
    $stop={
        $null=&$owned
        foreach($row in @(incremental-source-journal-helper-task\Get-SourceJournalHelperNativeProcesses $context.State.OriginalTask.Executable)){
            $process=$null
            try{
                $process=[Diagnostics.Process]::GetProcessById([int]$row.ProcessId)
                $null=$process.SafeHandle # Hold the native process incarnation, not a reusable PID.
                if($process.StartTime.ToUniversalTime().AddTicks(-($process.StartTime.Ticks%10)) -ne $row.CreationDate.ToUniversalTime()){
                    throw 'journal-helper-native-process-incarnation-changed'
                }
                $context.Captured.Add($process);$process=$null
            }catch [ArgumentException] { } # Recheck absence below; never kill a replacement PID.
            finally{if($null -ne $process){$process.Dispose()}}
        }
        Stop-ScheduledTask -TaskName $context.State.TaskName -TaskPath '\' -ErrorAction Stop
    }.GetNewClosure()
    $exit={
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do{
            $current=&$owned
            $remaining=@(incremental-source-journal-helper-task\Get-SourceJournalHelperNativeProcesses $context.State.OriginalTask.Executable)
            $live=@($context.Captured|Where-Object {!$_.HasExited})
            if(!$current.Enabled -and !$current.Running -and !$remaining.Count -and !$live.Count){
                foreach($process in $context.Captured){$process.Dispose()};$context.Captured.Clear();return
            }
            Start-Sleep -Milliseconds 200
        }while($watch.Elapsed.TotalSeconds -lt 15)
        throw 'journal-helper-native-exit-unproved-retain-hold'
    }.GetNewClosure()
    $enable={
        $null=&$owned
        $null=Enable-ScheduledTask -TaskName $context.State.TaskName -TaskPath '\' -ErrorAction Stop
    }.GetNewClosure()
    $start={
        $current=&$owned
        if(!$current.Enabled -or $current.Running){throw 'journal-helper-native-start-state-invalid'}
        Start-ScheduledTask -TaskName $context.State.TaskName -TaskPath '\' -ErrorAction Stop
    }.GetNewClosure()
    @{ReadTask=$read;AssertApplicationStopped=$AssertApplicationStopped;DisableTask=$disable;StopTask=$stop;AssertExited=$exit;
        EnableTask=$enable;StartTask=$start;Probe=$Probe;Context=$context}
}

function Close-SourceJournalHelperNativeTaskPorts([hashtable]$Ports){
    if($Ports -and $Ports.ContainsKey('Context')){foreach($process in $Ports.Context.Captured){$process.Dispose()};$Ports.Context.Captured.Clear()}
}
Export-ModuleMember -Function ConvertFrom-SourceJournalHelperTaskSnapshot,Get-SourceJournalHelperTaskObservation,
    Get-SourceJournalHelperNativeProcesses,New-SourceJournalHelperNativeTaskPorts,Close-SourceJournalHelperNativeTaskPorts
