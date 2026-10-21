param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
function Expect-Failure([scriptblock]$Action,[string]$Reason) {
    try{& $Action}catch{if($_.Exception.Message -ceq $Reason){return};throw}
    throw ('Expected failure: '+$Reason)
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('FluxSegmentedFlow_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    foreach($fault in @('Schema','Backup','Place','Capture','Convert','Configure','Start','Validate','Release','None')) {
        $statePath=Join-Path $root ($fault+'.json')
        $runtime=@{Fault=$fault;Calls=[Collections.Generic.List[string]]::new();FreshStop=$true;ProofAt=[DateTime]::UtcNow;Held=$true;
            Generation=[guid]::NewGuid().ToString('D');Current=$true;WriterClosed=0;WriterComplete=0}
        $ports=@{
            AssertBindings={param($state);$runtime.Calls.Add('Bindings');if($runtime.ContainsKey('BindingsValid') -and !$runtime.BindingsValid){throw 'test-configuration-changed'}}
            AssertStopped={param($after);$runtime.Calls.Add('Stopped');if($after -and (!$runtime.FreshStop -or $runtime.ProofAt -le [DateTime]::Parse($after).ToUniversalTime())){throw 'test-fresh-stop-required'}}
            UpgradeSchema={ $runtime.Calls.Add('Schema');if($runtime.Fault -ceq 'Schema'){throw 'test-fault'} }
            BeginWriter={ $runtime.Calls.Add('BeginWriter');return @{Completed=$false} }
            BackupPayloads={ $runtime.Calls.Add('Backup');if($runtime.Fault -ceq 'Backup'){throw 'test-fault'} }
            PlacePayloads={ $runtime.Calls.Add('Place');if($runtime.Fault -ceq 'Place'){throw 'test-fault'} }
            CapturePlan={ $runtime.Calls.Add('Capture');if($runtime.Fault -ceq 'Capture'){throw 'test-fault'};return ('B'*64) }
            Convert={param($state);$runtime.Calls.Add('Convert');if($runtime.Fault -ceq 'Convert'){throw 'test-fault'};
                return @{Id=$runtime.Generation;FormatVersion=2} }
            Configure={ $runtime.Calls.Add('Configure');if($runtime.Fault -ceq 'Configure'){throw 'test-fault'} }
            CompleteWriter={param($writer);$runtime.WriterComplete++;$writer.Completed=$true}
            CloseWriter={param($writer);$runtime.WriterClosed++}
            AssertCurrent={param($state);if(!$runtime.Current){throw 'test-newer-generation-must-survive'}}
            Start={ $runtime.Calls.Add('Start');$runtime.FreshStop=$false;if($runtime.Fault -ceq 'Start'){throw 'test-fault'} }
            Validate={ $runtime.Calls.Add('Validate');if($runtime.Fault -ceq 'Validate'){throw 'test-fault'} }
            Release={ $runtime.Calls.Add('Release');$runtime.Held=$false;if($runtime.Fault -ceq 'Release'){throw 'test-fault'} }
            EnsureHold={ $runtime.Held=$true }
            AssertCompleted={param($state);$runtime.Calls.Add('CompletedRead')}
        }
        $invoke={Invoke-SegmentedIndexUpdateFlow -StatePath $statePath -PlanSha256 ('A'*64) -Ports $ports}
        if($fault -cne 'None') {
            Expect-Failure $invoke 'test-fault'
            if(!$runtime.Held){throw 'Failure lost the hold.'}
            if($fault -in @('Start','Validate','Release')) {
                $calls=$runtime.Calls.Count
                Expect-Failure $invoke 'test-fresh-stop-required'
                if(@($runtime.Calls | Select-Object -Skip $calls | Where-Object {$_ -in @('Schema','Backup','Place','Convert','Configure','Start')}).Count){throw 'Unknown start allowed further mutation.'}
            }
            if($fault -ceq 'Release') {
                $runtime.FreshStop=$true;$runtime.ProofAt=[DateTime]::UtcNow;$runtime.Fault='Start'
                Expect-Failure $invoke 'test-fault'
                $runtime.FreshStop=$true # Same proof predates the second start.
                Expect-Failure $invoke 'test-fresh-stop-required'
            }
            $runtime.Fault='None';$runtime.FreshStop=$true;$runtime.ProofAt=[DateTime]::UtcNow
        }
        $result=& $invoke
        if(!$result.Completed -or $result.GenerationId -cne $runtime.Generation -or $runtime.Held){throw 'Forward recovery did not finish.'}
        $calls=$runtime.Calls.Count
        $runtime.Current=$false # A later normal publication must never be reactivated by a completed replay.
        [void](& $invoke)
        if(($runtime.Calls | Select-Object -Skip $calls) -cne 'CompletedRead'){throw 'Completed replay performed operational work.'}
        if($runtime.WriterClosed -lt 1 -or $runtime.WriterComplete -lt 1){throw 'Writer lifecycle was not closed.'}
        Expect-Failure {Invoke-SegmentedIndexUpdateFlow -StatePath $statePath -PlanSha256 ('C'*64) -Ports $ports} 'index-update-plan-binding-changed'
    }
    # Offline preparation commits a truthful checkpoint without admitting work.
    $statePath=Join-Path $root 'offline.json'
    $runtime.Calls.Clear();$runtime.Current=$true;$runtime.Held=$true;$runtime.FreshStop=$true
    $runtime.ProofAt=[DateTime]::UtcNow;$runtime.Fault='Configure';$runtime.BindingsValid=$true
    $offline={Invoke-SegmentedIndexUpdateFlow -StatePath $statePath -PlanSha256 ('A'*64) -Ports $ports -OfflineMaintenance}
    $resume={Invoke-SegmentedIndexUpdateFlow -StatePath $statePath -PlanSha256 ('A'*64) -Ports $ports -OfflineMaintenance -ResumeOfflineMaintenance}
    Expect-Failure {Invoke-SegmentedIndexUpdateFlow -StatePath $statePath -PlanSha256 ('A'*64) -Ports $ports -ResumeOfflineMaintenance} 'index-update-offline-mode-required'
    Expect-Failure $resume 'index-update-offline-checkpoint-required'
    Expect-Failure $offline 'test-fault'
    $runtime.Fault='None'
    $prepared=& $offline
    if(!$prepared.OfflineReady -or $prepared.Completed -or $prepared.StartIntentUtc -or $prepared.ReleaseIntentUtc -or !$runtime.Held -or
        @($runtime.Calls | Where-Object {$_ -in @('Start','Validate','Release')}).Count){throw 'Offline preparation admitted work or misreported completion.'}
    [void](& $offline)
    if(@($runtime.Calls | Where-Object {$_ -ceq 'Convert'}).Count -ne 1){throw 'Offline replay repeated conversion.'}
    Expect-Failure {Invoke-SegmentedIndexUpdateFlow -StatePath $statePath -PlanSha256 ('A'*64) -Ports $ports} 'index-update-offline-mode-binding-changed'
    $runtime.Current=$false
    Expect-Failure $resume 'test-newer-generation-must-survive'
    $runtime.Current=$true;$runtime.BindingsValid=$false
    Expect-Failure $resume 'test-configuration-changed'
    $runtime.BindingsValid=$true;$runtime.Fault='Validate'
    Expect-Failure $resume 'test-fault'
    Expect-Failure $resume 'test-fresh-stop-required'
    $runtime.Fault='None';$runtime.FreshStop=$true;$runtime.ProofAt=[DateTime]::UtcNow
    $completed=& $resume
    if(!$completed.Completed -or !$completed.OfflineReady -or $runtime.Held -or
        @($runtime.Calls | Where-Object {$_ -ceq 'Convert'}).Count -ne 1){throw 'Offline resume repeated conversion or failed to complete.'}
    # A recorded conversion result cannot overwrite a newer active publication on resume.
    $state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable
    $state.Completed=$false;$state.StartIntentUtc=$null;$state.ReleaseIntentUtc=$null
    [IO.File]::WriteAllText($statePath,($state | ConvertTo-Json -Depth 10))
    $runtime.Current=$false
    Expect-Failure $resume 'test-newer-generation-must-survive'
    'PASS: mutation failures resume safely; offline preparation stays held without starting workers; resume refuses stale bindings; uncertain starts require fresh stop proof; completed replay never reactivates a result.'
} finally {
    $resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cnotlike 'FluxSegmentedFlow_*'){throw 'Unsafe fixture cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
