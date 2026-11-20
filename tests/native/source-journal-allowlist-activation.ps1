$ErrorActionPreference='Stop'
$module=Import-Module (Join-Path $PSScriptRoot '../../scripts/deploy/source-journal-allowlist-activation.psm1') -PassThru -Force
& $module {
    function Assert-Rejected([scriptblock]$Action) {
        try { &$Action } catch { return }
        throw 'Expected invalid input to be rejected'
    }
    $stage='OperatorFixture'
    $root="I:\Codex Workspaces\Staging\FluxKnowledge\$stage"
    $binding=@{Version=1;OperatorOrigin='http://localhost:5511';DashboardOrigin='http://127.0.0.1:5512';
        StageVolumes=@('\\?\Volume{11111111-1111-1111-1111-111111111111}\','\\?\Volume{22222222-2222-2222-2222-222222222222}\');OperatorSids=@('S-1-5-21-1001-1002-1003-1004');ReceiptDirectory="I:\FluxKnowledgeSourceJournalOperator-Staging-$stage\Receipts";StageName=$stage;
        DatabaseIdentity="TESTHOST|FluxKnowledge_Staging_$stage|11111111-2222-3333-4444-555555555555";
        JournalPipeName="FluxKnowledge.SourceJournal.Staging-$stage";WebSha256='A'*64;OutlookSha256='B'*64;
        HelperSha256='C'*64;TaskDefinitionSha256='D'*64;TaskSecuritySha256='E'*64}
    $targets=Get-AllowlistTargets $binding
    $helperPorts=New-SourceJournalHelperNativeTaskPorts -State @{InstallRoot=$targets.Helper;TaskName=$targets.Task;OriginalTask=@{}} -AssertApplicationStopped {} -Probe {}
    # Dynamic callback modules have a different command search scope.
    $null=& $helperPorts.ReadTask.Module {Get-Command incremental-source-journal-helper-task\Get-SourceJournalHelperTaskObservation -ErrorAction Stop}
    if($targets.LiveRoot -cne $root -or $targets.Site -cne "FluxKnowledge-Staging-$stage"){throw 'wrong staging target'}
    foreach($mutation in @(@('OperatorOrigin','http://127.0.0.1:5511'),@('DashboardOrigin','http://127.0.0.1:5137'),
        @('StageName','..\FluxKnowledge'),@('ReceiptDirectory','I:\FluxKnowledge\SourceJournalOperator\Receipts'),
        @('JournalPipeName','FluxKnowledge.SourceJournal.Production'),@('OperatorOrigin',$binding.DashboardOrigin),
        @('StageVolumes',@()),@('WebSha256','A'),@('OperatorSids',@('S-1-5-18','S-1-5-18')), @('DatabaseIdentity','TESTHOST|FluxKnowledge|11111111-2222-3333-4444-555555555555'))){
        $bad=$binding.Clone();$bad[$mutation[0]]=$mutation[1];Assert-Rejected { Get-AllowlistTargets $bad }
    }
    $v1='\\?\Volume{11111111-1111-1111-1111-111111111111}\';$v2='\\?\Volume{22222222-2222-2222-2222-222222222222}\'
    $app='{"SourceJournal":{"Enabled":true,"ApprovedVolumes":["'+($v1|ConvertTo-Json -Compress).Trim('"')+'"],"PipeName":"x"},"Other":{"n":1.231234567890123456789,"dated":"2026-01-01"}}'
    $helper=@{Version=1;PipeName='x';ApprovedVolumes=@($v1);RepairEnabled=$false}|ConvertTo-Json
    $before=@{AppBytes=[Text.Encoding]::UTF8.GetBytes($app);HelperBytes=[Text.Encoding]::UTF8.GetBytes($helper)}
    $pair=New-AllowlistPair $before @($v1,$v2)
    $changed=[Text.Encoding]::UTF8.GetString($pair.AppBytes)
    if(!$changed.Contains('1.231234567890123456789') -or $pair.AppVolumes.Count -ne 2 -or $pair.HelperVolumes.Count -ne 2){throw 'Unrelated configuration changed'}
    Assert-Rejected { New-AllowlistPair $before @($v1,$v1) }
    Assert-Rejected { Get-AllowlistConnection @{ConnectionStrings=@{FluxKnowledge='Server=localhost;Database=FluxKnowledge;Integrated Security=true'}} $targets }
}
'Allowlist binding, isolation and policy editing passed.'

# Exercise the real Windows no-follow, replacement and ACL APIs on private files.
& $module {
    $stage='Acl'+[Guid]::NewGuid().ToString('N')
    $base='I:\Codex Workspaces\Staging\FluxKnowledge'
    $root=Join-Path $base $stage
    $owner=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
    $descriptor=[byte[]]::new($owner.BinaryLength);$owner.GetBinaryForm($descriptor,0)
    $lease=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($root,$descriptor)
    try{
        $file=Join-Path $root 'sample.json';$before=[Text.Encoding]::UTF8.GetBytes('{"n":1}');$after=[Text.Encoding]::UTF8.GetBytes('{"n":2}')
        [IO.File]::WriteAllBytes($file,$before)
        $sections=[Security.AccessControl.AccessControlSections]'Access,Owner,Group'
        $descriptorBefore=(Get-AllowlistDescriptor (Get-Acl $file))
        Save-AllowlistFile $file $before $after
        if((Get-AllowlistSha ([IO.File]::ReadAllBytes($file))) -cne (Get-AllowlistSha $after) -or
            (Get-AllowlistDescriptor (Get-Acl $file)) -cne $descriptorBefore){throw 'CAS did not preserve file and descriptor'}
        $rejected=$false
        try{Save-AllowlistFile $file $before $after}catch{$rejected=$true}
        if(!$rejected){throw 'CAS accepted stale predecessor'}
        $ports=New-AllowlistNativePorts @{Target=@{Hold=(Join-Path $root 'hold')};Release='fixture'}
        if((& $ports.GetAdmissionHeld) -ne $false){throw 'Absent hold misidentified'}
        [IO.File]::WriteAllText((Join-Path $root 'hold'),'"foreign"')
        $rejected=$false;try{$null=& $ports.GetAdmissionHeld}catch{$rejected=$true}
        if(!$rejected){throw 'Foreign hold accepted'}
    }finally{
        $lease.Dispose()
        if((Resolve-Path -LiteralPath $root).ProviderPath -cne $root -or [IO.Path]::GetDirectoryName($root) -cne $base){throw 'Unsafe staging cleanup'}
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
'Native atomic replacement, unchanged ACL and owned hold checks passed.'

# Full public entry point, real allowlist transaction and both protected files.
# OS owner controls are injected here; the separate IIS exercise owns the real pool.
& $module {
    function script:Get-AllowlistTargetIdentity($Context){ return $Context.BindingDigest }
    function script:Assert-AllowlistPredecessor($Context){if($Context.ContainsKey('Stopped') -and $Context.Stopped){throw 'fixture-owners-stopped'}}
    function script:Set-AllowlistHold($Context){
        [IO.File]::WriteAllText($Context.Target.Hold,($Context.Release|ConvertTo-Json -Compress))
    }
    function script:Stop-AllowlistOwners($Context){ Assert-AllowlistHold $Context; $Context.Stopped=$true }
    function script:Assert-AllowlistStopped($Context){if(!$Context.Stopped){throw 'fixture-owners-alive'}}
    function script:Start-AllowlistOwners($Context){Assert-AllowlistStopped $Context;$Context.Stopped=$false}
    function script:Invoke-AllowlistAdmission($Context,[scriptblock]$Action){if($script:DenyStartGuard){throw 'denied'};&$Action}
    function script:Confirm-AllowlistTransport($Context,[string[]]$Allowed,[string[]]$Removed){
        Assert-AllowlistHold $Context
        $pair=Read-AllowlistPair $Context
        $set=Get-AllowlistVolumeSet $Allowed
        if($Context.Stopped -or !$set.SetEquals($pair.AppVolumes) -or !$set.SetEquals($pair.HelperVolumes)){throw 'fixture-transport-failure'}
    }
    $stage='Native'+[guid]::NewGuid().ToString('N')
    $base='I:\Codex Workspaces\Staging\FluxKnowledge'
    $stageRoot=Join-Path $base $stage
    $operatorRoot="I:\FluxKnowledgeSourceJournalOperator-Staging-$stage"
    $helperRoot="I:\FluxKnowledgeSourceAccess-Staging-$stage"
    $dirs=[Collections.Generic.List[string]]::new()
    $owner=[Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
    $descriptor=[byte[]]::new($owner.BinaryLength);$owner.GetBinaryForm($descriptor,0)
    $vols=@('\\?\Volume{11111111-1111-1111-1111-111111111111}\','\\?\Volume{22222222-2222-2222-2222-222222222222}\')
    try{
        foreach($path in @($stageRoot,$operatorRoot,$helperRoot)){
            $new=[FluxSourceAccessInstallFile]::CreatePrivateDirectory($path,$descriptor);$new.Dispose();$dirs.Add($path)
        }
        $receipt=Join-Path $operatorRoot 'Receipts'
        foreach($path in @($receipt,(Join-Path $stageRoot 'Config'),(Join-Path $stageRoot 'Runtime'))){$null=[IO.Directory]::CreateDirectory($path)}
        $binding=@{Version=1;OperatorOrigin='http://localhost:5511';DashboardOrigin='http://127.0.0.1:5512';
            OperatorSids=@('S-1-5-21-1001-1002-1003-1004');ReceiptDirectory=$receipt;StageName=$stage;StageVolumes=$vols;
            DatabaseIdentity="TESTHOST|FluxKnowledge_Staging_$stage|11111111-2222-3333-4444-555555555555";
            JournalPipeName="FluxKnowledge.SourceJournal.Staging-$stage";WebSha256='A'*64;OutlookSha256='B'*64;
            HelperSha256='C'*64;TaskDefinitionSha256='D'*64;TaskSecuritySha256='E'*64}
        $targets=Get-AllowlistTargets $binding
        [IO.File]::WriteAllText($targets.BindingPath,($binding|ConvertTo-Json -Depth 4))
        $app=@{SourceJournal=@{Enabled=$true;ApprovedVolumes=$vols;PipeName=$binding.JournalPipeName};Marker='preserve'}
        $policy=@{Version=1;ApprovedVolumes=$vols;PipeName=$binding.JournalPipeName}
        [IO.File]::WriteAllText($targets.AppConfig,($app|ConvertTo-Json));[IO.File]::WriteAllText($targets.Policy,($policy|ConvertTo-Json))
        $script:DenyStartGuard=$true
        $denied=Invoke-ConfiguredSourceJournalAllowlist -Binding $binding -OperationId ([guid]::NewGuid()) -ExpectedApprovedVolumes $vols -ApprovedVolumes @($vols[0])
        if($denied.Status -cne 'rejected' -or [IO.File]::Exists($targets.Hold) -or @(Get-ChildItem -LiteralPath $receipt).Count -or
            (Read-AllowlistPair @{Target=$targets;Binding=$binding}).AppVolumes.Count -ne 2){throw 'Predictably denied IIS start reached the hold or config write'}
        $script:DenyStartGuard=$false
        $id=[guid]::NewGuid()
        $result=Invoke-ConfiguredSourceJournalAllowlist -Binding $binding -OperationId $id -ExpectedApprovedVolumes $vols -ApprovedVolumes @($vols[0])
        if($result.Status -cne 'succeeded' -or [IO.File]::Exists($targets.Hold)){throw ("Native transaction did not complete: "+($result|ConvertTo-Json -Compress))}
        $after=Read-AllowlistPair @{Target=$targets;Binding=$binding}
        if($after.AppVolumes.Count -ne 1 -or $after.HelperVolumes.Count -ne 1){throw 'Native configuration pair differs'}
        $historical=Get-ConfiguredSourceJournalAllowlistResult -Binding $binding -OperationId $id -ExpectedApprovedVolumes $vols -ApprovedVolumes @($vols[0])
        if($historical.Status -cne 'succeeded'){throw 'Exact finished receipt not recovered'}
        if($null -ne (Get-ConfiguredSourceJournalAllowlistResult -Binding $binding -OperationId $id -ExpectedApprovedVolumes @($vols[0]) -ApprovedVolumes $vols)){throw 'Read-only receipt accepted mismatched sets'}
        $again=Invoke-ConfiguredSourceJournalAllowlist -Binding $binding -OperationId $id -ExpectedApprovedVolumes $vols -ApprovedVolumes @($vols[0])
        if($again.Status -cne 'succeeded'){throw 'Native receipt replay failed'}
        $outside=Invoke-ConfiguredSourceJournalAllowlist -Binding $binding -OperationId ([guid]::NewGuid()) -ExpectedApprovedVolumes @($vols[0]) -ApprovedVolumes @('\\?\Volume{33333333-3333-3333-3333-333333333333}\')
        if($outside.Status -cne 'rejected'){throw 'Cross-stage volume admitted'}
    }finally{
        foreach($dir in $dirs){
            $actual=(Resolve-Path -LiteralPath $dir).ProviderPath
            if($actual -cne $dir -or $actual -cnotin @($stageRoot,$operatorRoot,$helperRoot) -or !$dir.EndsWith($stage,[StringComparison]::Ordinal)){throw 'Unsafe fixture cleanup'}
            Remove-Item -LiteralPath $actual -Recurse -Force
        }
    }
}
'Protected native transaction, receipt replay and staging volume isolation passed.'
