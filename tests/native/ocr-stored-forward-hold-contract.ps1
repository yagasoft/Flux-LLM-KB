param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-stored-execution.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
foreach($name in @('Assert-NotReparsePoint','New-DeploymentValidationHold')){
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($definition.Count -ne 1){throw 'Hold entry point missing.'}
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
function Get-HybridPayloadFingerprint([string]$Path){(Get-FileHash -LiteralPath (Join-Path $Path 'payload.txt')).Hash}
$script:forwardWriterCount=0
function Invoke-OcrScalar($Connection,[string]$Sql,$Transaction=$null,$Parameters=@{}) {
    if($Sql -notmatch 'IndexRecoveryWriters' -or $Sql -notmatch 'UPDLOCK,HOLDLOCK'){throw 'Expected a serialised writer guard.'}
    return $script:forwardWriterCount
}
$root=Join-Path $env:TEMP ('FluxOcrForward_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $CanonicalLiveRoot=Join-Path $root 'Live';$CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
    $InteractiveHostRoot=Join-Path $root 'OutlookHost';$SourceJournalHelperRoot=Join-Path $root 'Helper'
    $IncrementalRecoveryRoot=Join-Path $root 'Recovery';$ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/hold.json'
    $old='20261118T000000Z-aaaaaaaaaaaa';$new='20261118T010000Z-bbbbbbbbbbbb'
    $oldRoot=Join-Path $IncrementalRecoveryRoot $old
    foreach($directory in @($CanonicalDeployRoot,$InteractiveHostRoot,(Join-Path $SourceJournalHelperRoot 'payload'),$oldRoot,(Split-Path -Parent $ValidationHoldPath))){[void][IO.Directory]::CreateDirectory($directory)}
    $payloads=@(foreach($entry in @(@{Role='Web';Root=$CanonicalDeployRoot},@{Role='OutlookHost';Root=$InteractiveHostRoot},@{Role='Cli';Root=(Join-Path $SourceJournalHelperRoot 'payload')})){
        [IO.File]::WriteAllText((Join-Path $entry.Root 'payload.txt'),$entry.Role)
        @{Role=$entry.Role;Root=$entry.Root;Sha256=(Get-HybridPayloadFingerprint $entry.Root)}
    })
    $manifest=Join-Path $oldRoot 'manifest.json';[IO.File]::WriteAllText($manifest,'{"commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}')
    $config=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json';[void][IO.Directory]::CreateDirectory((Split-Path -Parent $config));[IO.File]::WriteAllText($config,'{}')
    $companionConfig=Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json';[IO.File]::WriteAllText($companionConfig,'{}')
    Write-OcrRecoveryJson $ValidationHoldPath $old
    $receipt=@{Version=1;Purpose='ocr-stored-execution-forward-hold';ReleaseId=$old;HoldPath=$ValidationHoldPath;
        HoldSha256=(Get-FileHash -LiteralPath $ValidationHoldPath).Hash;ManifestPath=$manifest;ManifestSha256=(Get-FileHash -LiteralPath $manifest).Hash;
        Updater=@{ProcessId=[int]::MaxValue;StartedUtc='2026-11-18T00:00:00Z'};Payloads=$payloads;
        Configurations=@(@{Path=$config;Sha256=(Get-FileHash -LiteralPath $config).Hash},@{Path=$companionConfig;Sha256=(Get-FileHash -LiteralPath $companionConfig).Hash});DatabaseIdentity='disposable-fixture'}
    $receiptPath=Join-Path $root 'predecessor.json';Write-OcrRecoveryJson $receiptPath $receipt
    $receipt.InputPath=$receiptPath;$receipt.InputSha256=(Get-FileHash -LiteralPath $receiptPath).Hash
    $operation=Join-Path $root 'Operation';[void][IO.Directory]::CreateDirectory($operation)
    $plan=@{ReleaseId=$new;HoldPath=$ValidationHoldPath;OperationRoot=$operation;DatabaseIdentity='disposable-fixture';PredecessorHoldBinding=$receipt}
    Move-OcrStoredExecutionHold $plan 'disposable-connection' 'disposable-transaction'
    if((Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $new){throw 'Forward transfer did not acquire the held owner.'}
    Move-OcrStoredExecutionHold $plan 'disposable-connection' 'disposable-transaction'
    $state=Get-Content -Raw -LiteralPath (Join-Path $operation 'hold-transfer.json')|ConvertFrom-Json -AsHashtable
    if(!$state.HoldTransferred -or $state.PredecessorBindingSha256 -cne $receipt.InputSha256){throw 'Transfer receipt/replay not bound.'}
    if((Get-Content -Raw -LiteralPath $manifest) -cne '{"commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}'){throw 'Predecessor packet changed.'}
    $sourceReceipt=Get-Content -Raw -LiteralPath $receiptPath|ConvertFrom-Json -AsHashtable -DateKind String
    foreach($fault in @('live-updater','writer','manifest','payload','config','other-hold','changed-binding','missing-role')){
        Write-OcrRecoveryJson $ValidationHoldPath $old
        $candidate=$sourceReceipt|ConvertTo-Json -Depth 12|ConvertFrom-Json -AsHashtable -DateKind String
        $priorManifest=[IO.File]::ReadAllText($manifest);$priorPayload=[IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'payload.txt'));$priorConfig=[IO.File]::ReadAllText($config)
        try {
            switch($fault){
                'live-updater'{$self=[Diagnostics.Process]::GetCurrentProcess();try{$candidate.Updater=@{ProcessId=$self.Id;StartedUtc=$self.StartTime.ToUniversalTime().ToString('O')}}finally{$self.Dispose()}}
                'writer'{$script:forwardWriterCount=1}
                'manifest'{[IO.File]::WriteAllText($manifest,'changed')}
                'payload'{[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'payload.txt'),'changed')}
                'config'{[IO.File]::WriteAllText($config,'changed')}
                'other-hold'{Write-OcrRecoveryJson $ValidationHoldPath '20261118T020000Z-cccccccccccc'}
                'missing-role'{$candidate.Payloads=@($candidate.Payloads|Where-Object Role -ne 'Cli')}
            }
            $path=Join-Path $root ('predecessor-'+$fault+'.json');Write-OcrRecoveryJson $path $candidate
            $candidate.InputPath=$path;$candidate.InputSha256=(Get-FileHash -LiteralPath $path).Hash
            if($fault -eq 'changed-binding'){$candidate.Updater.ProcessId=12345}
            $area=Join-Path $root ('Operation-'+$fault);[void][IO.Directory]::CreateDirectory($area)
            $attempt=@{ReleaseId=$new;HoldPath=$ValidationHoldPath;OperationRoot=$area;DatabaseIdentity='disposable-fixture';PredecessorHoldBinding=$candidate}
            $before=[IO.File]::ReadAllText($ValidationHoldPath);$refused=$false
            try{Move-OcrStoredExecutionHold $attempt 'disposable-connection' 'disposable-transaction'}catch{$refused=$true}
            if(!$refused -or [IO.File]::ReadAllText($ValidationHoldPath) -cne $before){throw "Unsafe forward hold effect for $fault."}
        }finally{
            $script:forwardWriterCount=0
            [IO.File]::WriteAllText($manifest,$priorManifest);[IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'payload.txt'),$priorPayload);[IO.File]::WriteAllText($config,$priorConfig)
        }
    }
    $script:realForwardWrite=(Get-Command Write-OcrRecoveryJson).ScriptBlock
    function Write-OcrRecoveryJson([string]$Path,$Value){
        if($Path -ceq $ValidationHoldPath -and !$script:forwardFaultUsed -and !$script:forwardFaultAfter){$script:forwardFaultUsed=$true;throw 'Synthetic interruption before atomic replace.'}
        & $script:realForwardWrite $Path $Value
        if($Path -ceq $ValidationHoldPath -and !$script:forwardFaultUsed -and $script:forwardFaultAfter){$script:forwardFaultUsed=$true;throw 'Synthetic interruption after atomic replace.'}
    }
    foreach($after in @($false,$true)){
        & $script:realForwardWrite $ValidationHoldPath $old
        $area=Join-Path $root ('Operation-interrupted-'+$after);[void][IO.Directory]::CreateDirectory($area)
        $attempt=@{ReleaseId=$new;HoldPath=$ValidationHoldPath;OperationRoot=$area;DatabaseIdentity='disposable-fixture';PredecessorHoldBinding=$receipt}
        $script:forwardFaultUsed=$false;$script:forwardFaultAfter=$after;$refused=$false
        try{Move-OcrStoredExecutionHold $attempt 'disposable-connection' 'disposable-transaction'}catch{$refused=$true}
        if(!$refused -or !$script:forwardFaultUsed -or ![IO.File]::Exists($ValidationHoldPath)){throw 'Interruption did not retain an owned hold.'}
        $expected=if($after){$new}else{$old}
        if((Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $expected){throw 'Atomic replacement exposed partial ownership.'}
        Move-OcrStoredExecutionHold $attempt 'disposable-connection' 'disposable-transaction'
        if((Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $new){throw 'Interrupted transfer failed exact replay.'}
    }
    foreach($failure in @('released-validation','release-response')){
        & $script:realForwardWrite $ValidationHoldPath $old
        $area=Join-Path $root ('Operation-'+$failure);[void][IO.Directory]::CreateDirectory($area)
        $attempt=@{ReleaseId=$new;HoldPath=$ValidationHoldPath;OperationRoot=$area;DatabaseIdentity='disposable-fixture';PredecessorHoldBinding=$receipt;
            Payloads=$payloads;PreservedHelperPayload=@{Root=(Join-Path $SourceJournalHelperRoot 'payload');Sha256=(Get-HybridPayloadFingerprint (Join-Path $SourceJournalHelperRoot 'payload'))}}
        $flow=@{Running=$false;Failed=$false;Stops=0}
        $planHash='D'*64;$flowPath=Join-Path $area 'deployment-state.json'
        $ports=@{
            AssertBindings={}
            EnsureHold={Move-OcrStoredExecutionHold $attempt 'disposable-connection' 'disposable-transaction' $planHash}
            StopOwners={
                if(![IO.File]::Exists($ValidationHoldPath) -or (Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $new){throw 'Containment stopped owners without restoring the new hold.'}
                $flow.Running=$false;$flow.Stops++
            }
            AssertStopped={};Migrate={};Settle={};BackupAndActivate={};RestorePriorPayloads={}
            Start={$flow.Running=$true};ValidateHeld={}
            ReleaseHold={
                [IO.File]::Delete($ValidationHoldPath)
                if($failure -eq 'release-response' -and !$flow.Failed){$flow.Failed=$true;throw 'Synthetic lost release acknowledgement.'}
            }
            RestoreCompanionPolicy={}
            ValidateReleased={if($failure -eq 'released-validation' -and !$flow.Failed){$flow.Failed=$true;throw 'Synthetic post-release validation failure.'}}
            AssertCompleted={}
        }
        $refused=$false
        try{Invoke-OcrRecoveryFlow $flowPath $planHash $ports|Out-Null}catch{$refused=$true}
        $saved=Get-Content -Raw -LiteralPath $flowPath|ConvertFrom-Json -AsHashtable
        if(!$refused -or $flow.Running -or $flow.Stops -ne 2 -or $saved.ContainmentIncomplete -or
            ![IO.File]::Exists($ValidationHoldPath) -or (Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $new){throw 'Post-release failure escaped held stopped containment.'}
        # Reproduce a process loss after release and before its acknowledgement/containment.
        [IO.File]::Delete($ValidationHoldPath)
        $done=Invoke-OcrRecoveryFlow $flowPath $planHash $ports -Resume
        if(!$done.Completed -or !$flow.Running -or [IO.File]::Exists($ValidationHoldPath)){throw 'Released-hold response loss did not resume through the same plan.'}
    }
    $area=Join-Path $root 'Operation-early-absence';[void][IO.Directory]::CreateDirectory($area)
    $attempt=@{ReleaseId=$new;HoldPath=$ValidationHoldPath;OperationRoot=$area;DatabaseIdentity='disposable-fixture';PredecessorHoldBinding=$receipt}
    $earlyRefused=$false
    try{Move-OcrStoredExecutionHold $attempt 'disposable-connection' 'disposable-transaction' ('D'*64)}catch{$earlyRefused=$true}
    if(!$earlyRefused -or [IO.File]::Exists($ValidationHoldPath)){throw 'An early missing predecessor hold was recreated.'}
    'Stored OCR forward hold transfer and replay preserve the predecessor packet.'
} finally {if([IO.Directory]::Exists($root)){[IO.Directory]::Delete($root,$true)}}
