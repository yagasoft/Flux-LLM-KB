# Dot-sourced only by the canonical incremental updater; no action on import.
. (Join-Path $PSScriptRoot 'verify-ocr-stopped-maintenance.ps1')
. (Join-Path $PSScriptRoot 'ocr-desktop-classification.ps1')
. (Join-Path $PSScriptRoot 'incremental-segmented-index-iis.ps1')

function Get-OcrLifecycleMigrationContract {
    [ordered]@{ Baseline='20261021010911_AddIndexRecoveryReferenceAudit';Target='20261024061418_AddDocumentOcrExecutionLifecycle';
        ScriptSha256='3A66996FC45853B16E7329B5D51C1E10DDD0F05F9AC842C702591E516115DC20';
        TargetSchemaSha256='0E47151306639D34CE7F884EBC3B3B005DF2A87480D8AE4DF7662CE13843F74B';
        DatabaseCollation='SQL_Latin1_General_CP1_CI_AS' }
}

function Get-OcrReviewedMigrationPrefix([string]$Root) {
    # InitialPhase1.cs has no timestamp in its filename. EF's declared IDs in
    # the generated metadata are authoritative for it and subsequent migrations.
    $ids=@(foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Root 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.Designer.cs') {
        $attributes=[regex]::Matches([IO.File]::ReadAllText($file.FullName),'(?m)^\s*\[Migration\("(?<id>\d{14}_[A-Za-z0-9_]+)"\)\]')
        if ($attributes.Count -ne 1) { throw 'document-ocr-maintenance-migration-metadata-ambiguous' }
        $attributes[0].Groups['id'].Value
    })
    $target=(Get-OcrLifecycleMigrationContract).Target
    if ($ids -cnotcontains $target -or @($ids | Sort-Object -Unique).Count -ne $ids.Count) {
        throw 'document-ocr-maintenance-migration-metadata-ambiguous'
    }
    @($ids | Where-Object { $_ -clt $target } | Sort-Object)
}

function Write-OcrRecoveryJson([string]$Path,$Value) {
    [void](Assert-OcrPlainLocalPath $Path)
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 50))
    $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $stream.Write($bytes);$stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary,$Path,$true)
}

function Invoke-OcrScalar($Connection,[string]$Sql,$Transaction=$null,$Parameters=@{}) {
    $command=$Connection.CreateCommand()
    try {
        $command.CommandTimeout=30;$command.CommandText=$Sql
        if ($null -ne $Transaction) { $command.Transaction=$Transaction }
        foreach ($key in $Parameters.Keys) { [void]$command.Parameters.AddWithValue($key,$Parameters[$key]) }
        $command.ExecuteScalar()
    } finally { $command.Dispose() }
}

function Invoke-OcrJsonScalar($Connection,[string]$Sql,$Transaction=$null,$Parameters=@{}) {
    # FOR JSON streams multiple 2033-character rows unless it is wrapped as one scalar.
    Invoke-OcrScalar $Connection ('SELECT ('+$Sql.Trim().TrimEnd(';')+');') $Transaction $Parameters
}

function Get-OcrLifecycleDatabaseState([string]$ConnectionString) {
    $connection=[Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $identity=Invoke-OcrScalar $connection "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))+'|'+DB_NAME()+'|'+CONVERT(nvarchar(36),database_guid) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
        $collation=Invoke-OcrScalar $connection "SELECT CONVERT(nvarchar(128),DATABASEPROPERTYEX(DB_NAME(),'Collation'));"
        $history=Invoke-OcrJsonScalar $connection 'SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId FOR JSON PATH;'
        $columns=Invoke-OcrJsonScalar $connection @'
SELECT c.name,TYPE_NAME(c.user_type_id) AS type,c.max_length,c.is_nullable,c.collation_name,dc.definition AS default_definition
 FROM sys.columns c LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
 WHERE c.object_id=OBJECT_ID(N'dbo.DocumentOcrRequests') AND c.name IN
 (N'CleanupProofKind',N'ExecutionAdmissionGeneration',N'ExecutionBatchId',N'ExecutionClaimedAtUtc',N'ExecutionDeadlineAtUtc',N'ExecutionDispatchId',
 N'ExecutionInstanceId',N'ExecutionMachineFingerprint',N'ExecutionPhase',N'NativeCleanupConfirmedAtUtc',N'NativeProcessId',N'NativeProcessStartedAtUtc',
 N'PhysicalCleanupEvidenceSha256',N'RecoveryReason',N'RecoveryStartedAtUtc',N'RecoveryUpdatedAtUtc',N'SupervisorInstanceId',N'SupervisorProcessId',N'SupervisorProcessStartedAtUtc')
 ORDER BY c.name FOR JSON PATH,INCLUDE_NULL_VALUES;
'@
        $constraints=Invoke-OcrJsonScalar $connection @'
SELECT name,definition,is_disabled,is_not_trusted FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.DocumentOcrRequests')
 AND name IN (N'CK_DocumentOcrRequests_CleanupKind',N'CK_DocumentOcrRequests_CleanupProof',N'CK_DocumentOcrRequests_ExecutionIdentity',
 N'CK_DocumentOcrRequests_ExecutionPhase',N'CK_DocumentOcrRequests_NativeIdentity') ORDER BY name FOR JSON PATH;
'@
        if (!$identity -or $identity -notmatch '\|[0-9a-fA-F-]{36}$') { throw 'document-ocr-maintenance-database-incarnation-unavailable' }
        $schema='{"Columns":'+$columns+',"Constraints":'+$constraints+'}'
        $parsed=[ordered]@{Columns=@($columns | ConvertFrom-Json -AsHashtable | Where-Object { $null -ne $_ });
            Constraints=@($constraints | ConvertFrom-Json -AsHashtable | Where-Object { $null -ne $_ })}
        [ordered]@{Identity=[string]$identity;Collation=[string]$collation;History=@($history | ConvertFrom-Json | ForEach-Object MigrationId);
            Schema=$parsed;SchemaSha256=(Get-OcrBytesHash ([Text.Encoding]::UTF8.GetBytes($schema)))}
    } finally { $connection.Dispose() }
}

function Assert-OcrLifecycleSchema($State,$Plan,[switch]$RequireOriginal) {
    $contract=Get-OcrLifecycleMigrationContract
    if ($State.Identity -cne $Plan.DatabaseIdentity) { throw 'document-ocr-maintenance-database-binding-changed' }
    if ($State.Collation -cne $contract.DatabaseCollation) { throw 'document-ocr-maintenance-unreviewed-database-collation' }
    $actual=@($State.History);$baseline=@($Plan.OriginalHistory)
    $old=($actual -join '|') -ceq ($baseline -join '|') -and $actual[-1] -ceq $contract.Baseline -and
        @($State.Schema.Columns).Count -eq 0 -and @($State.Schema.Constraints).Count -eq 0
    $new=($actual -join '|') -ceq ((@($baseline)+@($contract.Target)) -join '|') -and
        $State.SchemaSha256 -ceq $contract.TargetSchemaSha256 -and @($State.Schema.Columns).Count -eq 19 -and @($State.Schema.Constraints).Count -eq 5 -and
        @($State.Schema.Constraints | Where-Object { $_.is_disabled -or $_.is_not_trusted }).Count -eq 0
    if (!$old -and (!$new -or $RequireOriginal)) { throw 'document-ocr-maintenance-schema-or-history-changed' }
    return $new
}

function Get-OcrLegacyBinding([string]$ConnectionString,[Guid]$BatchId,[Guid]$RecoveryInstanceId) {
    $connection=[Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $json=Invoke-OcrJsonScalar $connection @'
SELECT d.BatchId,d.DispatchId,d.CapacitySlotKey,d.ExecutorKey,d.AdmissionGeneration,t.Id AS MiniTaskId,r.ParentJobId,
 s.OwnerKey AS CapacityOwnerKey,CONVERT(varchar(64),r.ResultDigest,2) AS ResultDigestHex,
 CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),r.ResultJson)),2) AS StoredJsonSha256
FROM dbo.GpuCapacitySlots s JOIN dbo.GpuBatches b ON b.Id=s.ActiveBatchId
JOIN dbo.GpuExecutorDispatches d ON d.BatchId=b.Id JOIN dbo.GpuMiniTasks t ON t.BatchId=b.Id
JOIN dbo.DocumentOcrRequests r ON r.MiniTaskId=t.Id
WHERE b.Id=@batch AND s.State=2 AND t.State=1 AND d.State=3 AND b.ItemCount=1
 AND s.OwnerKey=d.OwnerKey AND b.OwnerKey=d.OwnerKey AND b.AdmissionGeneration=d.AdmissionGeneration
 AND t.AdmissionGeneration=d.AdmissionGeneration AND r.ResultJson IS NOT NULL AND r.ResultDigest IS NOT NULL
 AND d.ExecutorKey=N'paddleocr-vl-local-gpu-0' AND d.CapacitySlotKey=N'paddleocr-vl-local-gpu-0'
FOR JSON PATH,INCLUDE_NULL_VALUES;
'@ $null @{'@batch'=$BatchId}
        $rows=@($json | ConvertFrom-Json -AsHashtable)
        if ($rows.Count -ne 1 -or $rows[0].ResultDigestHex -cnotmatch '^[A-F0-9]{64}$') { throw 'document-ocr-maintenance-legacy-reservation-not-exact' }
        $r=$rows[0]
        [ordered]@{Binding=[ordered]@{ Handle=[ordered]@{BatchId=$r.BatchId;CapacitySlotKey=$r.CapacitySlotKey;ExecutorKey=$r.ExecutorKey;
                AdmissionGeneration=$r.AdmissionGeneration;DispatchId=$r.DispatchId};MiniTaskId=$r.MiniTaskId;ParentJobId=$r.ParentJobId;
                CapacityOwnerKey=$r.CapacityOwnerKey;ResultDigest=[Convert]::ToBase64String([Convert]::FromHexString($r.ResultDigestHex));
                RecoveryInstanceId=$RecoveryInstanceId.ToString()};StoredJsonSha256=$r.StoredJsonSha256}
    } finally { $connection.Dispose() }
}

function Invoke-OcrLifecycleMigration([string]$ConnectionString,$Plan,[scriptblock]$AssertStopped,
    [scriptblock]$AfterStatement,[scriptblock]$AfterCommit) {
    & $AssertStopped
    $current=Get-OcrLifecycleDatabaseState $ConnectionString
    if (Assert-OcrLifecycleSchema $current $Plan) { return $current }
    $path=Join-Path $PSScriptRoot 'ocr-lifecycle-schema-up.sql'
    if ((Get-FileHash -LiteralPath $path).Hash -cne (Get-OcrLifecycleMigrationContract).ScriptSha256 -or
        $Plan.MigrationSha256 -cne (Get-OcrLifecycleMigrationContract).ScriptSha256) { throw 'document-ocr-maintenance-migration-script-changed' }
    $connection=[Data.SqlClient.SqlConnection]::new($ConnectionString)
    $failure=$null;$transaction=$null
    try {
        $connection.Open()
        $transaction=$connection.BeginTransaction()
        # EF applies these commands separately. New-column CHECK constraints cannot be
        # compiled in the same SQL batch as their ADD COLUMN commands.
        $body=[IO.File]::ReadAllText($path).Replace('BEGIN TRANSACTION;','').Replace('COMMIT;','') -replace '(?m)^GO\s*$',''
        try {
            foreach ($statement in $body.Split(';')) {
                if ($statement.Trim()) {
                    [void](Invoke-OcrScalar $connection $statement $transaction)
                    if ($null -ne $AfterStatement) { & $AfterStatement }
                }
            }
            $transaction.Commit()
            if ($null -ne $AfterCommit) { & $AfterCommit }
        }
        catch {
            $failure=$_
            try { $transaction.Rollback() } catch { } # A lost commit response may already have committed.
        }
    } finally { if ($null -ne $transaction) { $transaction.Dispose() };$connection.Dispose() }
    $after=Get-OcrLifecycleDatabaseState $ConnectionString
    if (!(Assert-OcrLifecycleSchema $after $Plan)) {
        if ($null -ne $failure) { throw $failure }
        throw 'document-ocr-maintenance-migration-not-confirmed'
    }
    & $AssertStopped
    return $after
}

function Invoke-OcrRecoveryFlow([string]$StatePath,[string]$PlanSha256,$Ports,[switch]$Resume) {
    $state=if ([IO.File]::Exists($StatePath)) { Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json -AsHashtable -DateKind String }
        else { [ordered]@{Version=1;PlanSha256=$PlanSha256;Phase='Prepared';ActivationStarted=$false;StartIntent=$false;HoldReleaseIntent=$false;Completed=$false} }
    if ($state.Version -ne 1 -or $state.PlanSha256 -cne $PlanSha256) { throw 'document-ocr-maintenance-state-binding-changed' }
    if ($state.Completed) { & $Ports.AssertCompleted;return $state }
    if ($state.Phase -ne 'Prepared' -and !$Resume) { throw 'document-ocr-maintenance-explicit-resume-required' }
    $origin=if ($state.Phase -ceq 'FailedHeld') { $state.FailedAt } else { $state.Phase }
    if ($origin -cin @('PayloadActivationIntent','StartIntent','HoldReleaseIntent')) { $state.ActivationStarted=$true }
    $save={param($phase) $state.Phase=$phase;Write-OcrRecoveryJson $StatePath $state}.GetNewClosure()
    & $Ports.AssertBindings
    try {
        & $save 'HoldIntent'; & $Ports.EnsureHold
        & $save 'StopIntent'; & $Ports.StopOwners
        & $save 'Stopped'; & $Ports.AssertStopped
        & $save 'MigrationIntent'; & $Ports.Migrate
        & $save 'SettlementIntent'; & $Ports.Settle
        $state.ActivationStarted=$true;& $save 'PayloadActivationIntent'; & $Ports.BackupAndActivate
        $state.StartIntent=$true;& $save 'StartIntent'; & $Ports.Start
        & $Ports.ValidateHeld
        $state.HoldReleaseIntent=$true;& $save 'HoldReleaseIntent'; & $Ports.ReleaseHold
        & $Ports.RestoreCompanionPolicy
        & $Ports.ValidateReleased
        $state.Completed=$true;& $save 'Completed'
        return $state
    } catch {
        $failed=$state.Phase
        try { & $Ports.EnsureHold; & $Ports.StopOwners; if (!$state.StartIntent) { & $Ports.RestorePriorPayloads } }
        catch { $state.ContainmentIncomplete=$true }
        $state.FailedAt=$failed;& $save 'FailedHeld'
        throw "document-ocr-maintenance-failed-held: $failed; state=$StatePath"
    }
}

function Get-OcrRecoveryImplementationHash {
    $names=@('update-native-iis-incremental.ps1','incremental-ocr-lifecycle-recovery.ps1','verify-ocr-stopped-maintenance.ps1',
        'ocr-lifecycle-schema-up.sql','ocr-desktop-classification.ps1','incremental-iis-payload-swap.psm1','incremental-segmented-index-iis.ps1',
        'incremental-segmented-index-update.psm1','incremental-repository-recovery-migration.psm1',
        'incremental-hybrid-passage-rebuild.psm1','index-format-admission.psm1','IndexEntryMetadata.cs','recovery-reference-writer.psm1')
    Get-OcrBytesHash ([Text.Encoding]::UTF8.GetBytes((@($names | ForEach-Object { $_+'|'+(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_)).Hash }) -join "`n")))
}

function Get-OcrInstalledPayloadHash([string]$Path,[bool]$AllowPartial) {
    [void](Assert-OcrPlainLocalPath $Path)
    if (![IO.Directory]::Exists($Path) -and $AllowPartial) { return '' }
    try { return Get-HybridPayloadFingerprint $Path }
    catch { if ($AllowPartial -and $_.Exception.Message -ceq 'hybrid-payload-empty') { return '' };throw }
}

function Assert-OcrRecoveryCompletedBindings($Plan) {
    if ([IO.File]::Exists($ValidationHoldPath)) { throw 'document-ocr-maintenance-completed-hold-present' }
    foreach ($pair in @(@($Plan.ConfigurationPath,$Plan.ConfigurationSha256),@($Plan.CompanionConfigurationPath,$Plan.CompanionConfigurationSha256))) {
        if ((Get-FileHash -LiteralPath $pair[0]).Hash -cne $pair[1]) { throw 'document-ocr-maintenance-completed-configuration-changed' }
    }
    foreach ($role in @('Web','OutlookHost')) {
        $entry=@($Plan.Payloads | Where-Object Role -ceq $role)[0]
        $installed=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
        if ((Get-HybridPayloadFingerprint $installed) -cne $entry.Sha256) { throw 'document-ocr-maintenance-completed-payload-changed' }
        Assert-RepositoryRecoveryCandidateCommit $installed $Plan.Commit
    }
    $task=Get-RepositoryRecoveryTaskObservation
    if ($task.IdentityHash -cne $Plan.Task.IdentityHash -or $task.LauncherSha256 -cne $Plan.Task.LauncherSha256 -or $task.Enabled -ne $Plan.Task.Enabled) {
        throw 'document-ocr-maintenance-completed-task-policy-changed'
    }
}

function Confirm-OcrRecoveryBackup([string]$Installed,[string]$Backup,[string]$ExpectedSha256,[scriptblock]$AssertStopped) {
    if ((Get-OcrInstalledPayloadHash $Backup $true) -ceq $ExpectedSha256) { return }
    # An interrupted copy is repairable only while the authoritative predecessor
    # still exists in full. Never use candidate or partially activated bytes.
    & $AssertStopped
    if ((Get-OcrInstalledPayloadHash $Installed $false) -cne $ExpectedSha256) {
        throw 'document-ocr-maintenance-rollback-payload-changed'
    }
    Copy-SegmentedIndexPayload -Source $Installed -Destination $Backup -ExpectedSha256 $ExpectedSha256
}

function New-OcrLifecycleRecoveryPlan([string]$Root,[string]$OperationRoot,[Guid]$BatchId,[string]$DesktopClassificationsPath) {
    $desktop=Read-OcrReviewedDesktopClassifications $DesktopClassificationsPath
    $operation=Assert-OcrPlainLocalPath $OperationRoot
    if (!$operation.StartsWith('I:\Codex Workspaces\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Directory]::Exists($operation)) {
        throw 'document-ocr-maintenance-new-private-operation-root-required'
    }
    if ((& git -C $Root status --porcelain).Length) { throw 'document-ocr-maintenance-clean-source-required' }
    $commit=(& git -C $Root rev-parse HEAD).Trim()
    if ($commit -cnotmatch '^[0-9a-f]{40}$') { throw 'document-ocr-maintenance-source-identity-unavailable' }
    $database=Get-OcrLifecycleDatabaseState (Get-DeploymentSqlConnectionString)
    if ($database.Identity -cne (Get-OcrLifecycleDatabaseState (Get-RepositoryRecoveryCompanionConnectionString)).Identity) {
        throw 'document-ocr-maintenance-companion-database-mismatch'
    }
    $expected=@(Get-OcrReviewedMigrationPrefix $Root)
    if (($database.History -join '|') -cne ($expected -join '|')) { throw 'document-ocr-maintenance-unreviewed-migration-prefix' }
    if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'ocr-lifecycle-schema-up.sql')).Hash -cne (Get-OcrLifecycleMigrationContract).ScriptSha256) {
        throw 'document-ocr-maintenance-migration-script-changed'
    }
    [void][IO.Directory]::CreateDirectory($operation)
    $payloads=@(foreach ($role in @('Web','OutlookHost','Cli')) {
        $destination=Join-Path $operation $role
        $log=Join-Path $operation ('publish-'+$role+'.log')
        & dotnet publish (Join-Path $Root "src/FluxKnowledge.$role/FluxKnowledge.$role.csproj") -c Release --no-restore --nologo -o $destination *> $log
        if ($LASTEXITCODE -ne 0) { throw "document-ocr-maintenance-publish-failed: $log" }
        if ($role -eq 'OutlookHost') { Copy-Item -LiteralPath (Join-Path $Root 'scripts/deploy/run-outlook-host.ps1') -Destination $destination }
        Assert-RepositoryRecoveryCandidateCommit $destination $commit
        [ordered]@{Role=$role;Root=$destination;Sha256=(Get-HybridPayloadFingerprint $destination)}
    })
    $smiPaths=@(Get-Command nvidia-smi.exe -ErrorAction SilentlyContinue | ForEach-Object Source)
    if (!$smiPaths.Count) { $smiPaths=@(Get-ChildItem -LiteralPath (Join-Path $env:SystemRoot 'System32/DriverStore/FileRepository') -Recurse -Filter 'nvidia-smi.exe' -File | ForEach-Object FullName) }
    if ($smiPaths.Count -ne 1) { throw 'document-ocr-maintenance-driver-tool-ambiguous' }
    $smi=$smiPaths[0];[void](Assert-OcrPlainLocalPath $smi)
    $raw=& $smi -q -x
    if ($LASTEXITCODE -ne 0) { throw 'document-ocr-maintenance-driver-query-failed' }
    [xml]$driver=$raw -join "`n"
    if (@($driver.nvidia_smi_log.gpu).Count -ne 1 -or !$driver.nvidia_smi_log.gpu.uuid) { throw 'document-ocr-maintenance-single-gpu-required' }
    $excluded=@(foreach ($path in @($driver.SelectNodes('//gpu/processes/process_info') | ForEach-Object process_name | Sort-Object -Unique)) {
        if ((Test-OcrExecutionBoundary $path ([IO.Path]::GetFileName($path)) '') -or
            [IO.Path]::GetFileName($path) -match '^(?i:dotnet|pwsh|powershell|cmd|node|rundll32|mshta|wscript|cscript)\.exe$') {
            throw 'document-ocr-maintenance-live-gpu-process-needs-classification'
        }
        $classified=@($desktop.Entries | Where-Object Path -ieq $path)
        if ($classified.Count -ne 1) { throw 'document-ocr-maintenance-desktop-process-needs-classification' }
        $classified[0]
    })
    $legacy=Get-OcrLegacyBinding (Get-DeploymentSqlConnectionString) $BatchId ([Guid]::NewGuid())
    $task=Get-RepositoryRecoveryTaskObservation
    $releaseId=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+$commit.Substring(0,12)+'-ocrlifecycle'
    $plan=[ordered]@{Version=1;ReleaseId=$releaseId;CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');Commit=$commit;SourceRoot=$Root;
        OperationRoot=$operation;DatabaseIdentity=$database.Identity;OriginalHistory=$database.History;
        MigrationSha256=(Get-OcrLifecycleMigrationContract).ScriptSha256;ImplementationSha256=(Get-OcrRecoveryImplementationHash);
        Binding=$legacy.Binding;StoredJsonSha256=$legacy.StoredJsonSha256;Payloads=$payloads;
        OriginalWebSha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot);OriginalCompanionSha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot);
        HoldPath=$ValidationHoldPath;ConfigurationPath=(Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json');
        ConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json')).Hash;
        CompanionConfigurationPath=(Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json');
        CompanionConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json')).Hash;
        IisPool=$SiteName;TaskName=$InteractiveHostTaskName;Task=[ordered]@{Enabled=$task.Enabled;IdentityHash=$task.IdentityHash;
            LauncherPath=$task.LauncherPath;LauncherSha256=$task.LauncherSha256};GpuUuid=[string]$driver.nvidia_smi_log.gpu.uuid;
        DriverVersion=[string]$driver.nvidia_smi_log.driver_version;SmiPath=$smi;SmiSha256=(Get-FileHash -LiteralPath $smi).Hash;
        DesktopClassificationInputSha256=$desktop.InputSha256;ExcludedGpuExecutables=$excluded;VerifierScriptPath=(Join-Path $PSScriptRoot 'verify-ocr-stopped-maintenance.ps1');
        VerifierScriptSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'verify-ocr-stopped-maintenance.ps1')).Hash}
    [void](Assert-OcrLifecycleSchema $database $plan -RequireOriginal)
    $path=Join-Path $operation 'plan.json';Write-OcrRecoveryJson $path $plan
    [ordered]@{plan=$path;sha256=(Get-FileHash -LiteralPath $path).Hash;commit=$commit;database=$database.Identity;batch=$BatchId;
        actions=@('hold admissions','stop exact IIS and companion owners','prove native cleanup','apply pinned additive migration',
            'settle exact legacy reservation preserving result','activate matching payloads','validate held, release, validate queue progress');
        failure='hold retained and owners stopped; prior matching payloads restored only before start intent; no Down migration or settlement reversal'}
}

function Stop-OcrApplicationOwners($Plan) {
    $stopPath=Join-Path $Plan.OperationRoot 'stopped-owners.json'
    $before=@(Get-OcrOsSnapshot)
    $workerIds=@(Get-HybridIisWorkerIds -AppCmdPath (Join-Path $env:SystemRoot 'System32/inetsrv/appcmd.exe') -PoolName $SiteName)
    $owners=[Collections.Generic.Dictionary[string,object]]::new()
    if ([IO.File]::Exists($stopPath)) {
        $prior=Get-Content -Raw -LiteralPath $stopPath | ConvertFrom-Json -AsHashtable -DateKind String
        if ($prior.ReleaseId -cne $Plan.ReleaseId) { throw 'document-ocr-maintenance-stop-receipt-changed' }
        foreach ($identity in @($prior.ExitedIdentities)) { $owners[$identity.Pid.ToString()+'|'+$identity.StartedUtc]=$identity }
    }
    foreach ($process in $before) {
        if ($process.Pid -in $workerIds -or ($process.Path+'\').StartsWith($InteractiveHostRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or
            ($process.Name -ieq 'wscript.exe' -and $process.CommandLine.Contains($Plan.Task.LauncherPath,[StringComparison]::OrdinalIgnoreCase))) {
            if (!$process.StartedUtc -or !$process.Path) { throw 'document-ocr-maintenance-owner-identity-unavailable' }
            $owners[$process.Pid.ToString()+'|'+$process.StartedUtc]=[ordered]@{Pid=$process.Pid;StartedUtc=$process.StartedUtc;Path=$process.Path}
        }
    }
    $record=[ordered]@{ReleaseId=$Plan.ReleaseId;StopIntentAtUtc=[DateTimeOffset]::UtcNow.ToString('O');ExitedIdentities=@($owners.Values);VerifiedExitAtUtc=$null}
    Write-OcrRecoveryJson $stopPath $record
    Disable-ScheduledTask -TaskName $InteractiveHostTaskName -TaskPath '\' -ErrorAction Stop | Out-Null
    if ((Get-ScheduledTask -TaskName $InteractiveHostTaskName -TaskPath '\').State -eq 'Running') {
        Stop-ScheduledTask -TaskName $InteractiveHostTaskName -TaskPath '\' -ErrorAction Stop
    }
    if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') { Stop-WebAppPool -Name $SiteName }
    Wait-IisAppPoolState -Name $SiteName -ExpectedState Stopped -TimeoutSeconds $ReadinessTimeoutSeconds
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $current=@(Get-OcrOsSnapshot)
        # Discover late descendants as well as those in the original snapshot. Never kill by name.
        $changed=$true
        while ($changed) {
            $changed=$false
            foreach ($process in @($before)+@($current)) {
                $key=$process.Pid.ToString()+'|'+$process.StartedUtc
                if (!$process.StartedUtc -or $owners.ContainsKey($key)) { continue }
                if (@($owners.Values | Where-Object { $_.Pid -eq $process.ParentPid -and $_.StartedUtc -cle $process.StartedUtc }).Count) {
                    $owners[$key]=[ordered]@{Pid=$process.Pid;StartedUtc=$process.StartedUtc;Path=$process.Path};$changed=$true
                }
            }
        }
        $record.ExitedIdentities=@($owners.Values);Write-OcrRecoveryJson $stopPath $record
        $remaining=@($current | Where-Object { $owners.ContainsKey($_.Pid.ToString()+'|'+$_.StartedUtc) })
        $workers=@(Get-HybridIisWorkerIds -AppCmdPath (Join-Path $env:SystemRoot 'System32/inetsrv/appcmd.exe') -PoolName $SiteName)
        if (!$remaining.Count -and !$workers.Count -and (Get-ScheduledTask -TaskName $InteractiveHostTaskName -TaskPath '\').State -ne 'Running') {
            $record.VerifiedExitAtUtc=[DateTimeOffset]::UtcNow.ToString('O');Write-OcrRecoveryJson $stopPath $record
            return
        }
        if ($timer.Elapsed.TotalSeconds -ge $ReadinessTimeoutSeconds) { throw 'document-ocr-maintenance-exact-owner-exit-unproven' }
        Start-Sleep -Milliseconds 250
    } while ($true)
}

function Invoke-OcrLegacySettlement($Plan,[string]$PlanPath,[string]$PlanSha256) {
    $cli=@($Plan.Payloads | Where-Object Role -ceq 'Cli')[0]
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach ($argument in @((Join-Path $cli.Root 'FluxKnowledge.Cli.dll'),'ocr-recovery','apply','--plan',$PlanPath,'--sha256',$PlanSha256)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['ConnectionStrings__FluxKnowledge']=Get-DeploymentSqlConnectionString
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(150000)) { $process.Kill($true);throw 'document-ocr-maintenance-settlement-budget-exceeded' }
        $output=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $Plan.OperationRoot 'settlement-output.json'),$output)
        if ($process.ExitCode -ne 0) { throw 'document-ocr-maintenance-settlement-refused: '+$errorText.Trim() }
        $result=$output | ConvertFrom-Json -AsHashtable
        if (!$result.Settled -or [Guid]$result.BatchId -ne [Guid]$Plan.Binding.Handle.BatchId) { throw 'document-ocr-maintenance-settlement-unconfirmed' }
    } finally { $process.Dispose() }
}

function Invoke-OcrLifecycleRecoveryUpdate([string]$PlanPath,[string]$ExpectedPlanSha256,[switch]$Resume) {
    [void](Assert-OcrPlainLocalPath $PlanPath)
    if ($ExpectedPlanSha256 -cnotmatch '^[A-F0-9]{64}$' -or (Get-FileHash -LiteralPath $PlanPath).Hash -cne $ExpectedPlanSha256) {
        throw 'document-ocr-maintenance-reviewed-plan-required'
    }
    $plan=Get-Content -Raw -LiteralPath $PlanPath | ConvertFrom-Json -AsHashtable -DateKind String
    if ($plan.Version -ne 1 -or $plan.OperationRoot -cne (Split-Path -Parent $PlanPath) -or
        $plan.ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-ocrlifecycle$' -or
        $plan.HoldPath -cne $ValidationHoldPath -or $plan.IisPool -cne $SiteName -or $plan.TaskName -cne $InteractiveHostTaskName -or
        $plan.ConfigurationPath -cne (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json') -or
        $plan.CompanionConfigurationPath -cne (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json') -or
        $plan.ImplementationSha256 -cne (Get-OcrRecoveryImplementationHash)) { throw 'document-ocr-maintenance-plan-binding-changed' }
    $release=Join-Path $IncrementalRecoveryRoot $plan.ReleaseId
    [void](Assert-OcrPlainLocalPath $release)
    $mutex=[Threading.Mutex]::new($false,'Global\FluxKnowledge.IncrementalIisUpdate.v1');$owns=$false
    try {
        try { $owns=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owns=$true }
        if (!$owns) { throw 'document-ocr-maintenance-other-deployment-running' }
        Assert-IncrementalIisPreflight
        $resumeBinding=if ($Resume) { Assert-OcrRecoveryResumePreflight $PlanPath $ExpectedPlanSha256 } else { $null }
        $allowPartial=$null -ne $resumeBinding -and [bool]$resumeBinding.AllowPartialPayloads
        $ports=@{
            AssertBindings={
                foreach ($entry in @($plan.Payloads)) {
                    [void](Assert-OcrPlainLocalPath $entry.Root)
                    if ((Get-HybridPayloadFingerprint $entry.Root) -cne $entry.Sha256) { throw 'document-ocr-maintenance-candidate-changed' }
                    Assert-RepositoryRecoveryCandidateCommit $entry.Root $plan.Commit
                }
                foreach ($role in @('Web','OutlookHost')) {
                    $installed=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
                    $original=if ($role -eq 'Web') {$plan.OriginalWebSha256}else{$plan.OriginalCompanionSha256}
                    $candidate=@($plan.Payloads | Where-Object Role -ceq $role)[0].Sha256
                    $actual=Get-OcrInstalledPayloadHash $installed $allowPartial
                    if ($actual -cne $original -and (!$Resume -or ($actual -cne $candidate -and !$allowPartial))) {
                        throw 'document-ocr-maintenance-installed-predecessor-changed'
                    }
                }
                foreach ($pair in @(@($plan.ConfigurationPath,$plan.ConfigurationSha256),@($plan.CompanionConfigurationPath,$plan.CompanionConfigurationSha256))) {
                    if ((Get-FileHash -LiteralPath $pair[0]).Hash -cne $pair[1]) { throw 'document-ocr-maintenance-configuration-changed' }
                }
                $task=Get-RepositoryRecoveryTaskObservation
                if ($task.IdentityHash -cne $plan.Task.IdentityHash -or $task.LauncherSha256 -cne $plan.Task.LauncherSha256) { throw 'document-ocr-maintenance-task-changed' }
                [void](Assert-OcrLifecycleSchema (Get-OcrLifecycleDatabaseState (Get-DeploymentSqlConnectionString)) $plan)
            }
            EnsureHold={
                $connection=[Data.SqlClient.SqlConnection]::new((Get-DeploymentSqlConnectionString))
                try {
                    $connection.Open();$transaction=$connection.BeginTransaction()
                    try {
                        $result=Invoke-OcrScalar $connection "DECLARE @r int;EXEC @r=sp_getapplock @Resource=N'FluxKnowledge.GpuScheduler.Admission',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=10000;SELECT @r;" $transaction
                        if ([int]$result -lt 0) { throw 'document-ocr-maintenance-admission-lock-unavailable' }
                        [void](New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $plan.ReleaseId)
                        $transaction.Commit()
                    } finally { $transaction.Dispose() }
                } finally { $connection.Dispose() }
            }
            StopOwners={Stop-OcrApplicationOwners $plan}
            AssertStopped={
                $stop=Get-Content -Raw -LiteralPath (Join-Path $plan.OperationRoot 'stopped-owners.json') | ConvertFrom-Json -AsHashtable -DateKind String
                Assert-OcrOwnersStopped $plan $stop @(Get-OcrOsSnapshot)
            }
            Migrate={
                [void](Invoke-OcrLifecycleMigration (Get-DeploymentSqlConnectionString) $plan {
                    $stop=Get-Content -Raw -LiteralPath (Join-Path $plan.OperationRoot 'stopped-owners.json') | ConvertFrom-Json -AsHashtable -DateKind String
                    Assert-OcrOwnersStopped $plan $stop @(Get-OcrOsSnapshot)
                })
            }
            Settle={Invoke-OcrLegacySettlement $plan $PlanPath $ExpectedPlanSha256}
            BackupAndActivate={
                [void][IO.Directory]::CreateDirectory($release)
                $writer=Start-RecoveryReferenceWrite -ConnectionString (Get-DeploymentSqlConnectionString) -RecoveryRoot $CanonicalRecoveryRoot -OwnerExitSufficient
                try {
                    foreach ($role in @('Web','OutlookHost')) {
                        $entry=@($plan.Payloads | Where-Object Role -ceq $role)[0]
                        $destination=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
                        $originalHash=if ($role -eq 'Web') {$plan.OriginalWebSha256}else{$plan.OriginalCompanionSha256}
                        $backup=Join-Path $release ('previous-'+$role)
                        Confirm-OcrRecoveryBackup $destination $backup $originalHash {
                            $stop=Get-Content -Raw -LiteralPath (Join-Path $plan.OperationRoot 'stopped-owners.json') | ConvertFrom-Json -AsHashtable -DateKind String
                            Assert-OcrOwnersStopped $plan $stop @(Get-OcrOsSnapshot)
                        }
                    }
                    Write-OcrRecoveryJson (Join-Path $release 'manifest.json') ([ordered]@{commit=$plan.Commit;staged_at_utc=$plan.CreatedAtUtc;
                        application_root=$CanonicalDeployRoot;migrations=$true;clean_slate=$false;ocr_recovery_plan_sha256=$ExpectedPlanSha256;reviewed_legacy_index_payloads=@()})
                    foreach ($role in @('Web','OutlookHost')) {
                        $entry=@($plan.Payloads | Where-Object Role -ceq $role)[0]
                        $destination=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
                        Copy-SegmentedIndexPayload -Source $entry.Root -Destination $destination -ExpectedSha256 $entry.Sha256
                    }
                    Test-ApplicationPayload $CanonicalDeployRoot;Assert-ApplicationPayloadReadAccess $CanonicalDeployRoot
                    Test-InteractiveHostPayload $InteractiveHostRoot
                    Complete-RecoveryReferenceWrite $writer
                } finally { Close-RecoveryReferenceWrite $writer }
            }
            RestorePriorPayloads={
                foreach ($role in @('Web','OutlookHost')) {
                    $backup=Join-Path $release ('previous-'+$role)
                    if ([IO.Directory]::Exists($backup)) {
                        $destination=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
                        $hash=if ($role -eq 'Web') {$plan.OriginalWebSha256}else{$plan.OriginalCompanionSha256}
                        Copy-SegmentedIndexPayload -Source $backup -Destination $destination -ExpectedSha256 $hash
                    }
                }
            }
            Start={
                Invoke-DeploymentIndexAdmission -Action { Start-WebAppPool -Name $SiteName }
                Wait-IisAppPoolState -Name $SiteName -ExpectedState Started -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            ValidateHeld={
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
                if (([IO.File]::ReadAllText($ValidationHoldPath)) -cne ($plan.ReleaseId | ConvertTo-Json -Compress)) { throw 'document-ocr-maintenance-hold-changed' }
                foreach ($role in @('Web','OutlookHost')) {
                    $entry=@($plan.Payloads | Where-Object Role -ceq $role)[0]
                    $installed=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
                    if ((Get-HybridPayloadFingerprint $installed) -cne $entry.Sha256) { throw 'document-ocr-maintenance-installed-payload-changed' }
                }
            }
            ReleaseHold={Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $plan.ReleaseId}
            RestoreCompanionPolicy={if ($plan.Task.Enabled) { Enable-IndexCompatibleOutlookTask -TaskName $InteractiveHostTaskName }}
            ValidateReleased={Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds}
            AssertCompleted={
                Assert-OcrRecoveryCompletedBindings $plan
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
                [void](Assert-OcrLifecycleSchema (Get-OcrLifecycleDatabaseState (Get-DeploymentSqlConnectionString)) $plan)
            }
        }
        foreach ($key in @($ports.Keys)) { $ports[$key]=$ports[$key].GetNewClosure() }
        $result=Invoke-OcrRecoveryFlow -StatePath (Join-Path $plan.OperationRoot 'deployment-state.json') -PlanSha256 $ExpectedPlanSha256 -Ports $ports -Resume:$Resume
        [ordered]@{completed=$result.Completed;commit=$plan.Commit;batch=$plan.Binding.Handle.BatchId;release=$release;
            state=(Join-Path $plan.OperationRoot 'deployment-state.json');models='unchanged; no acquisition';next='validate original continuation and subsequent queued GPU work'}
    } finally { if ($owns) { $mutex.ReleaseMutex() };$mutex.Dispose() }
}

function Assert-OcrRecoveryResumePreflight([string]$PlanPath,[string]$ExpectedPlanSha256) {
    if (!$ApplyOcrLifecycleRecovery -or !$ResumeOcrLifecycleRecovery -or $ExpectedPlanSha256 -cnotmatch '^[A-F0-9]{64}$' -or
        (Get-FileHash -LiteralPath (Assert-OcrPlainLocalPath $PlanPath)).Hash -cne $ExpectedPlanSha256) {
        throw 'document-ocr-maintenance-bound-resume-required'
    }
    $p=Get-Content -Raw -LiteralPath $PlanPath | ConvertFrom-Json -AsHashtable -DateKind String
    $statePath=Join-Path $p.OperationRoot 'deployment-state.json'
    [void](Assert-OcrPlainLocalPath $statePath)
    $state=Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json -AsHashtable -DateKind String
    if ($state.Version -ne 1 -or $state.PlanSha256 -cne $ExpectedPlanSha256 -or $p.Version -ne 1 -or
        $p.OperationRoot -cne (Split-Path -Parent $PlanPath) -or $p.ImplementationSha256 -cne (Get-OcrRecoveryImplementationHash)) {
        throw 'document-ocr-maintenance-resume-binding-changed'
    }
    if ($state.Completed) { return [ordered]@{AllowPartialPayloads=$false;Completed=$true} }
    $phase=if ($state.Phase -ceq 'FailedHeld') { $state.FailedAt } else { $state.Phase }
    if ($phase -cnotin @('Prepared','HoldIntent','StopIntent','Stopped','MigrationIntent','SettlementIntent',
        'PayloadActivationIntent','StartIntent','HoldReleaseIntent')) { throw 'document-ocr-maintenance-resume-phase-unrecognised' }
    foreach ($pair in @(@($p.ConfigurationPath,$p.ConfigurationSha256),@($p.CompanionConfigurationPath,$p.CompanionConfigurationSha256))) {
        if ((Get-FileHash -LiteralPath $pair[0]).Hash -cne $pair[1]) { throw 'document-ocr-maintenance-resume-configuration-changed' }
    }
    $activationStarted=($state.Contains('ActivationStarted') -and $state.ActivationStarted) -or
        $phase -cin @('PayloadActivationIntent','StartIntent','HoldReleaseIntent')
    $entering=$phase -cin @('Prepared','HoldIntent','StopIntent') -and !$activationStarted
    $releaseInterrupted=$phase -ceq 'HoldReleaseIntent' -or ($state.Contains('HoldReleaseIntent') -and $state.HoldReleaseIntent)
    $holdExists=[IO.File]::Exists($ValidationHoldPath)
    if (($holdExists -and [IO.File]::ReadAllText($ValidationHoldPath) -cne ($p.ReleaseId | ConvertTo-Json -Compress)) -or
        (!$holdExists -and !$entering -and !$releaseInterrupted)) {
        throw 'document-ocr-maintenance-resume-hold-unproven'
    }
    $task=Get-RepositoryRecoveryTaskObservation
    $taskMayBeOriginal=$entering -or $releaseInterrupted
    if (($task.Enabled -and (!$taskMayBeOriginal -or !$p.Task.Enabled)) -or
        ($task.State -eq 'Running' -and !$taskMayBeOriginal) -or
        $task.IdentityHash -cne $p.Task.IdentityHash -or $task.LauncherSha256 -cne $p.Task.LauncherSha256) {
        throw 'document-ocr-maintenance-resume-companion-unproven'
    }
    $partial=$false
    foreach ($role in @('Web','OutlookHost')) {
        $installed=if ($role -eq 'Web') {$CanonicalDeployRoot}else{$InteractiveHostRoot}
        $original=if ($role -eq 'Web') {$p.OriginalWebSha256}else{$p.OriginalCompanionSha256}
        $candidate=@($p.Payloads | Where-Object Role -ceq $role)[0].Sha256
        $actual=Get-OcrInstalledPayloadHash $installed $true
        if ($actual -cne $original -and ($entering -or $actual -cne $candidate)) {
            if (!$activationStarted) {
                throw 'document-ocr-maintenance-resume-installed-payload-unproven'
            }
            $partial=$true
        }
    }
    if ($partial) {
        if ((Get-WebAppPoolState -Name $SiteName).Value -ne 'Stopped') { throw 'document-ocr-maintenance-partial-payload-pool-not-stopped' }
        foreach ($role in @('Web','OutlookHost')) {
            $backup=Join-Path (Join-Path $IncrementalRecoveryRoot $p.ReleaseId) ('previous-'+$role)
            $expected=if ($role -eq 'Web') {$p.OriginalWebSha256}else{$p.OriginalCompanionSha256}
            if ((Get-HybridPayloadFingerprint (Assert-OcrPlainLocalPath $backup)) -cne $expected) { throw 'document-ocr-maintenance-resume-rollback-payload-unproven' }
        }
    }
    [ordered]@{AllowPartialPayloads=$partial;Completed=$false}
}
