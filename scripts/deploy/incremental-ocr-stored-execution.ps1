# Dot-sourced by the canonical OCR maintenance path; import performs no action.
function Move-OcrStoredExecutionHold($Plan,$Connection,$Transaction,[string]$PlanSha256='') {
    if(!$Plan -or $Plan.ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or $Plan.HoldPath -cne $ValidationHoldPath){
        throw 'document-ocr-maintenance-hold-transfer-binding-invalid'
    }
    # The caller holds the updater mutex and the original-session Admission fence.
    # This guard precedes registration of the new release's own recovery writer.
    if([long](Invoke-OcrScalar $Connection 'SELECT COUNT_BIG(*) FROM IndexRecoveryWriters WITH(UPDLOCK,HOLDLOCK);' $Transaction) -ne 0){
        throw 'document-ocr-maintenance-recovery-writers-present'
    }
    $binding=$Plan['PredecessorHoldBinding']
    if(!$binding){[void](New-DeploymentValidationHold -Path $Plan.HoldPath -ReleaseId $Plan.ReleaseId);return}
    if($binding.ReleaseId -ceq $Plan.ReleaseId){throw 'document-ocr-maintenance-hold-transfer-binding-invalid'}
    $statePath=Join-Path $Plan.OperationRoot 'hold-transfer.json'
    $state=if([IO.File]::Exists($statePath)){Get-Content -Raw -LiteralPath $statePath|ConvertFrom-Json -AsHashtable -DateKind String}else{$null}
    if($state -and ($state.Version -ne 1 -or $state.ReleaseId -cne $Plan.ReleaseId -or $state.PriorReleaseId -cne $binding.ReleaseId -or
        $state.PredecessorBindingSha256 -cne $binding.InputSha256)){throw 'document-ocr-maintenance-hold-transfer-binding-changed'}
    [void](Assert-OcrPlainLocalPath $Plan.HoldPath)
    if(![IO.File]::Exists($Plan.HoldPath)){
        # The only permitted absence follows this exact plan's durable release
        # intent. Re-establish the new owner for containment or lost-ack resume.
        $deploymentPath=Join-Path $Plan.OperationRoot 'deployment-state.json'
        if(!$state -or !$state.HoldTransferred -or $PlanSha256 -cnotmatch '^[A-F0-9]{64}$' -or ![IO.File]::Exists($deploymentPath)){
            throw 'document-ocr-maintenance-missing-predecessor-hold'
        }
        $deployment=Get-Content -Raw -LiteralPath $deploymentPath|ConvertFrom-Json -AsHashtable -DateKind String
        # A resumed flow writes HoldIntent before EnsureHold, retaining its
        # previously committed release/start/activation intent flags and plan hash.
        $released=$deployment.Phase -cin @('HoldReleaseIntent','HoldIntent') -or ($deployment.Phase -ceq 'FailedHeld' -and $deployment.FailedAt -ceq 'HoldReleaseIntent')
        if($deployment.Version -ne 1 -or $deployment.PlanSha256 -cne $PlanSha256 -or !$released -or
            !$deployment.ActivationStarted -or !$deployment.StartIntent -or !$deployment.HoldReleaseIntent -or $deployment.Completed){
            throw 'document-ocr-maintenance-release-intent-unproven'
        }
        Assert-OcrForwardHoldBinding $binding $Plan.DatabaseIdentity -CheckPayloads:$false
        foreach($role in @(@{Role='Web';Root=$CanonicalDeployRoot},@{Role='OutlookHost';Root=$InteractiveHostRoot})){
            $entries=@($Plan.Payloads|Where-Object Role -ceq $role.Role)
            if($entries.Count -ne 1 -or $entries[0].Sha256 -cnotmatch '^[A-F0-9]{64}$' -or
                (Get-HybridPayloadFingerprint $role.Root) -cne $entries[0].Sha256){throw 'document-ocr-maintenance-release-payload-unproven'}
        }
        if($Plan.PreservedHelperPayload.Root -cne (Join-Path $SourceJournalHelperRoot 'payload') -or
            (Get-HybridPayloadFingerprint $Plan.PreservedHelperPayload.Root) -cne $Plan.PreservedHelperPayload.Sha256){
            throw 'document-ocr-maintenance-preserved-helper-changed'
        }
        [void](New-DeploymentValidationHold -Path $Plan.HoldPath -ReleaseId $Plan.ReleaseId)
        return
    }
    $owner=Get-Content -Raw -LiteralPath $Plan.HoldPath|ConvertFrom-Json
    if($owner -ceq $Plan.ReleaseId){
        if(!$state){throw 'document-ocr-maintenance-hold-transfer-intent-missing'}
        Assert-OcrForwardHoldBinding $binding $Plan.DatabaseIdentity -CheckPayloads:$false
        $state.HoldTransferred=$true;Write-OcrRecoveryJson $statePath $state
        return
    }
    if($owner -cne $binding.ReleaseId -or ($state -and $state.HoldTransferred)){
        throw 'document-ocr-maintenance-hold-owner-changed'
    }
    Assert-OcrForwardHoldBinding $binding $Plan.DatabaseIdentity
    if(!$state){
        $state=[ordered]@{Version=1;ReleaseId=$Plan.ReleaseId;PriorReleaseId=$binding.ReleaseId;
            PredecessorBindingSha256=$binding.InputSha256;HoldTransferIntentUtc=[DateTimeOffset]::UtcNow.ToString('O');HoldTransferred=$false}
        Write-OcrRecoveryJson $statePath $state
    }
    if((Get-FileHash -LiteralPath $Plan.HoldPath).Hash -cne $binding.HoldSha256){throw 'document-ocr-maintenance-hold-owner-changed'}
    # Same-directory atomic replacement keeps admission denied, including across a crash.
    Write-OcrRecoveryJson $Plan.HoldPath $Plan.ReleaseId
    if((Get-Content -Raw -LiteralPath $Plan.HoldPath|ConvertFrom-Json) -cne $Plan.ReleaseId){throw 'document-ocr-maintenance-hold-transfer-unconfirmed'}
    $state.HoldTransferred=$true;Write-OcrRecoveryJson $statePath $state
}

function Assert-OcrForwardUpdaterExited($Updater) {
    if(!$Updater -or [int]$Updater.ProcessId -le 0){throw 'document-ocr-maintenance-predecessor-owner-invalid'}
    $born=[DateTimeOffset]::Parse($Updater.StartedUtc,[Globalization.CultureInfo]::InvariantCulture)
    if($born.Offset -ne [TimeSpan]::Zero){throw 'document-ocr-maintenance-predecessor-owner-invalid'}
    try{$process=[Diagnostics.Process]::GetProcessById([int]$Updater.ProcessId)}catch [ArgumentException]{return}
    try {
        [void]$process.SafeHandle
        if($process.StartTime.ToUniversalTime().Ticks -eq $born.UtcDateTime.Ticks -and !$process.HasExited){
            throw 'document-ocr-maintenance-predecessor-updater-running'
        }
    } finally {$process.Dispose()}
}

function Assert-OcrForwardHoldBinding($Binding,[string]$DatabaseIdentity,[bool]$CheckPayloads=$true) {
    if(!$Binding -or $Binding.Version -ne 1 -or $Binding.Purpose -cne 'ocr-stored-execution-forward-hold' -or
        $Binding.ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or $Binding.DatabaseIdentity -cne $DatabaseIdentity -or
        $Binding.HoldPath -cne $ValidationHoldPath -or $Binding.InputSha256 -cnotmatch '^[A-F0-9]{64}$'){
        throw 'document-ocr-maintenance-predecessor-binding-invalid'
    }
    [void](Assert-OcrPlainLocalPath $Binding.InputPath)
    if((Get-FileHash -LiteralPath $Binding.InputPath).Hash -cne $Binding.InputSha256){throw 'document-ocr-maintenance-predecessor-binding-changed'}
    $saved=Get-Content -Raw -LiteralPath $Binding.InputPath|ConvertFrom-Json -AsHashtable -DateKind String
    foreach($field in @('Version','Purpose','ReleaseId','DatabaseIdentity','HoldPath','HoldSha256','ManifestPath','ManifestSha256','Updater','Payloads','Configurations')){
        if(($saved[$field]|ConvertTo-Json -Depth 12 -Compress) -cne ($Binding[$field]|ConvertTo-Json -Depth 12 -Compress)){
            throw 'document-ocr-maintenance-predecessor-binding-changed'
        }
    }
    Assert-OcrForwardUpdaterExited $Binding.Updater
    $manifest=Join-Path (Join-Path $IncrementalRecoveryRoot $Binding.ReleaseId) 'manifest.json'
    if($Binding.ManifestPath -cne $manifest){throw 'document-ocr-maintenance-predecessor-packet-changed'}
    [void](Assert-OcrPlainLocalPath $manifest)
    if((Get-FileHash -LiteralPath $manifest).Hash -cne $Binding.ManifestSha256){throw 'document-ocr-maintenance-predecessor-packet-changed'}
    $roles=@(@{Role='Web';Root=$CanonicalDeployRoot},@{Role='OutlookHost';Root=$InteractiveHostRoot},@{Role='Cli';Root=(Join-Path $SourceJournalHelperRoot 'payload')})
    if(@($Binding.Payloads).Count -ne 3){throw 'document-ocr-maintenance-predecessor-payload-changed'}
    foreach($role in $roles){
        $entries=@($Binding.Payloads|Where-Object Role -ceq $role.Role)
        if($entries.Count -ne 1 -or $entries[0].Root -cne $role.Root -or $entries[0].Sha256 -cnotmatch '^[A-F0-9]{64}$'){
            throw 'document-ocr-maintenance-predecessor-payload-changed'
        }
        if($CheckPayloads -and (Get-HybridPayloadFingerprint $role.Root) -cne $entries[0].Sha256){throw 'document-ocr-maintenance-predecessor-payload-changed'}
    }
    $configs=@((Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'),(Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json'))
    if(@($Binding.Configurations).Count -ne 2){throw 'document-ocr-maintenance-predecessor-configuration-changed'}
    foreach($config in $configs){
        $entries=@($Binding.Configurations|Where-Object Path -ceq $config)
        if($entries.Count -ne 1 -or (Get-FileHash -LiteralPath (Assert-OcrPlainLocalPath $config)).Hash -cne $entries[0].Sha256){
            throw 'document-ocr-maintenance-predecessor-configuration-changed'
        }
    }
}

function Read-OcrForwardHoldBinding([string]$Path,[string]$ExpectedSha256,[string]$DatabaseIdentity) {
    if(!$Path -or $ExpectedSha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'document-ocr-maintenance-reviewed-predecessor-required'}
    [void](Assert-OcrPlainLocalPath $Path)
    if((Get-FileHash -LiteralPath $Path).Hash -cne $ExpectedSha256){throw 'document-ocr-maintenance-reviewed-predecessor-required'}
    $binding=Get-Content -Raw -LiteralPath $Path|ConvertFrom-Json -AsHashtable -DateKind String
    if($binding.Contains('InputPath') -or $binding.Contains('InputSha256')){throw 'document-ocr-maintenance-predecessor-binding-invalid'}
    $binding.InputPath=$Path;$binding.InputSha256=$ExpectedSha256
    Assert-OcrForwardHoldBinding $binding $DatabaseIdentity
    if((Get-FileHash -LiteralPath $ValidationHoldPath).Hash -cne $binding.HoldSha256 -or
        (Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $binding.ReleaseId){throw 'document-ocr-maintenance-hold-owner-changed'}
    $binding
}

function Get-OcrStoredExecutionBinding([string]$ConnectionString,[Guid]$BatchId,[Guid]$OperationId) {
    if($BatchId -eq [Guid]::Empty -or $OperationId -eq [Guid]::Empty){throw 'document-ocr-maintenance-stored-binding-invalid'}
    $normalised=$ConnectionString -replace '(?i)Trust Server Certificate=','TrustServerCertificate=' -replace '(?i)Connect Retry Count=','ConnectRetryCount='
    $options=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    $options['Pooling']=$false;$options['Connect Timeout']=5;$options['ConnectRetryCount']=0
    $connection=[Data.SqlClient.SqlConnection]::new($options.ConnectionString)
    try {
        $connection.Open()
        $rows=@((Invoke-OcrJsonScalar $connection @'
SELECT d.BatchId,d.DispatchId,d.CapacitySlotKey,d.ExecutorKey,d.AdmissionGeneration,t.Id AS MiniTaskId,r.ParentJobId,
 r.PipelineRecordId,r.RetainedSourceRevisionId,root.Id AS SourceRootId,root.ConfigurationRevision AS RootConfigurationRevision,
 b.OwnerKey AS CapacityOwnerKey,r.ExecutionInstanceId,r.ExecutionClaimedAtUtc,r.ExecutionDeadlineAtUtc,
 r.SupervisorInstanceId,r.SupervisorProcessId,r.SupervisorProcessStartedAtUtc,r.ExecutionMachineFingerprint,
 r.NativeProcessId,r.NativeProcessStartedAtUtc,CONVERT(varchar(64),r.ResultDigest,2) AS ResultDigestHex,
 CONVERT(varchar(16),CONVERT(binary(8),r.RowVersion),2) AS RequestRowVersionHex,
 CONVERT(varchar(16),CONVERT(binary(8),t.RowVersion),2) AS TaskRowVersionHex,
 CONVERT(varchar(16),CONVERT(binary(8),job.RowVersion),2) AS JobRowVersionHex
FROM dbo.GpuBatches b JOIN dbo.GpuExecutorDispatches d ON d.BatchId=b.Id JOIN dbo.GpuMiniTasks t ON t.BatchId=b.Id
JOIN dbo.DocumentOcrRequests r ON r.MiniTaskId=t.Id JOIN dbo.GpuCapacitySlots slot ON slot.SlotKey=b.CapacitySlotKey
JOIN dbo.Jobs job ON job.Id=r.ParentJobId JOIN dbo.PipelineRecords record ON record.Id=r.PipelineRecordId
JOIN dbo.SourceRevisions source ON source.Id=record.SourceRevisionId JOIN dbo.SourceRootConfigurations root ON root.Id=source.SourceRootId
WHERE b.Id=@batch AND b.State=4 AND t.State=1 AND d.State=3 AND b.ItemCount=1 AND root.State=0
 AND slot.State=2 AND slot.ActiveBatchId=b.Id AND slot.OwnerKey COLLATE Latin1_General_100_BIN2=b.OwnerKey COLLATE Latin1_General_100_BIN2
 AND b.OwnerKey COLLATE Latin1_General_100_BIN2=d.OwnerKey COLLATE Latin1_General_100_BIN2
 AND b.CapacitySlotKey=d.CapacitySlotKey AND b.AdmissionGeneration=d.AdmissionGeneration AND t.AdmissionGeneration=d.AdmissionGeneration
 AND t.ParentJobId=job.Id AND job.PipelineRecordId=record.Id AND job.SourceRevision=record.Revision AND r.SourceRevision=job.SourceRevision
 AND record.IsDeleted=0 AND r.RetainedSourceRevisionId=source.Id AND source.SuppressedAtUtc IS NULL
 AND source.ContentSha256 COLLATE Latin1_General_100_BIN2=r.ContentSha256 COLLATE Latin1_General_100_BIN2
 AND t.ModelRuntimeKey COLLATE Latin1_General_100_BIN2=r.ModelRuntimeKey COLLATE Latin1_General_100_BIN2
 AND t.SettingsFingerprint COLLATE Latin1_General_100_BIN2=r.SettingsFingerprint COLLATE Latin1_General_100_BIN2
 AND b.ModelRuntimeKey COLLATE Latin1_General_100_BIN2=t.ModelRuntimeKey COLLATE Latin1_General_100_BIN2
 AND b.SettingsFingerprint COLLATE Latin1_General_100_BIN2=t.SettingsFingerprint COLLATE Latin1_General_100_BIN2
 AND d.ExecutorKey=N'paddleocr-vl-local-gpu-0' AND d.CapacitySlotKey=N'paddleocr-vl-local-gpu-0'
 AND (SELECT COUNT_BIG(*) FROM dbo.GpuMiniTasks member WHERE member.BatchId=b.Id)=1
 AND r.State=1 AND r.ResultJson IS NOT NULL AND DATALENGTH(r.ResultDigest)=32
 AND r.ExecutionPhase IN(2,5) AND r.ExecutionInstanceId IS NOT NULL AND r.ExecutionDispatchId=d.DispatchId
 AND r.ExecutionBatchId=b.Id AND r.ExecutionAdmissionGeneration=d.AdmissionGeneration
 AND r.ExecutionMachineFingerprint IS NOT NULL AND r.SupervisorInstanceId IS NOT NULL AND r.SupervisorProcessId IS NOT NULL
 AND r.SupervisorProcessStartedAtUtc IS NOT NULL AND r.ExecutionClaimedAtUtc IS NOT NULL AND r.ExecutionDeadlineAtUtc IS NOT NULL
 AND r.NativeProcessId IS NOT NULL AND r.NativeProcessStartedAtUtc IS NOT NULL
 AND r.NativeCleanupConfirmedAtUtc IS NULL AND r.CleanupProofKind IS NULL AND r.PhysicalCleanupEvidenceSha256 IS NULL
FOR JSON PATH;
'@ $null @{'@batch'=$BatchId})|ConvertFrom-Json -AsHashtable -DateKind String|Where-Object {$null -ne $_})
        if($rows.Count -ne 1 -or $rows[0] -isnot [Collections.IDictionary]){throw 'document-ocr-maintenance-stored-binding-not-exact'}
        $row=$rows[0]
        foreach($name in @('RequestRowVersionHex','TaskRowVersionHex','JobRowVersionHex')){
            if($row[$name] -cnotmatch '^[A-F0-9]{16}$'){throw 'document-ocr-maintenance-stored-binding-not-exact'}
        }
        if($row.ResultDigestHex -cnotmatch '^[A-F0-9]{64}$'){throw 'document-ocr-maintenance-stored-binding-not-exact'}
        $handle=[ordered]@{BatchId=$row.BatchId;CapacitySlotKey=$row.CapacitySlotKey;ExecutorKey=$row.ExecutorKey;
            AdmissionGeneration=$row.AdmissionGeneration;DispatchId=$row.DispatchId}
        [ordered]@{
            Lease=[ordered]@{Handle=$handle;MiniTaskId=$row.MiniTaskId;ExecutionInstanceId=$row.ExecutionInstanceId;
                Supervisor=[ordered]@{InstanceId=$row.SupervisorInstanceId;Process=[ordered]@{ProcessId=$row.SupervisorProcessId;
                    StartedAtUtc=$row.SupervisorProcessStartedAtUtc;MachineFingerprint=$row.ExecutionMachineFingerprint}};
                ClaimedAtUtc=$row.ExecutionClaimedAtUtc;DeadlineAtUtc=$row.ExecutionDeadlineAtUtc}
            NativeProcess=[ordered]@{ProcessId=$row.NativeProcessId;StartedAtUtc=$row.NativeProcessStartedAtUtc;MachineFingerprint=$row.ExecutionMachineFingerprint}
            ParentJobId=$row.ParentJobId;PipelineRecordId=$row.PipelineRecordId;RetainedSourceRevisionId=$row.RetainedSourceRevisionId;
            SourceRootId=$row.SourceRootId;RootConfigurationRevision=$row.RootConfigurationRevision;CapacityOwnerKey=$row.CapacityOwnerKey;
            ResultDigest=[Convert]::ToBase64String([Convert]::FromHexString($row.ResultDigestHex));
            RequestRowVersion=[Convert]::ToBase64String([Convert]::FromHexString($row.RequestRowVersionHex));
            TaskRowVersion=[Convert]::ToBase64String([Convert]::FromHexString($row.TaskRowVersionHex));
            JobRowVersion=[Convert]::ToBase64String([Convert]::FromHexString($row.JobRowVersionHex));OperationId=$OperationId.ToString()
        }
    } finally {$connection.Dispose()}
}
