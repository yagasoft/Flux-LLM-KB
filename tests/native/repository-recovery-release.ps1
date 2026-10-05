[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-repository-recovery-release.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-repository-recovery-migration.psm1')
function Assert-True([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Action, [string]$Reason) {
    $failed=$false
    try { & $Action | Out-Null } catch { $failed=$true; if ($_.Exception.Message -notmatch $Reason) { throw } }
    Assert-True $failed "Expected refusal: $Reason"
}
function New-Case([bool]$TaskEnabled) {
    $root=Join-Path ([IO.Path]::GetTempPath()) ('flux-recovery-release-'+[Guid]::NewGuid().ToString('N'))
    $release='20261005T000000Z-09945ae1f034-repositoryrecovery'
    $releaseRoot=Join-Path $root $release
    $paths=[ordered]@{ ReleaseRoot=$releaseRoot; ApplicationRoot=(Join-Path $root 'app');
        CandidateRoot=(Join-Path $releaseRoot 'candidate'); PreviousRoot=(Join-Path $releaseRoot 'previous'); FailedRoot=(Join-Path $releaseRoot 'failed');
        CompanionRoot=(Join-Path $root 'host'); CompanionCandidateRoot=(Join-Path $releaseRoot 'candidate-interactive-host');
        CompanionPreviousRoot=(Join-Path $releaseRoot 'previous-interactive-host'); HoldPath=(Join-Path $root 'hold.json') }
    foreach ($key in @('ReleaseRoot','ApplicationRoot','CandidateRoot','CompanionRoot','CompanionCandidateRoot')) {
        [void](New-Item -ItemType Directory -Path $paths.$key -Force)
        if ($key -ne 'ReleaseRoot') { Set-Content -LiteralPath (Join-Path $paths.$key 'payload.txt') -Value $key }
    }
    $contract=Get-RepositoryRecoveryMigrationContract
    $ids=@('20260726215521_InitialPhase1') + @(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src/FluxKnowledge.Infrastructure.SqlServer/Persistence/Migrations') -Filter '*.cs' -File |
        Where-Object { $_.Name -match '^\d{14}_.+\.cs$' -and $_.Name -notlike '*.Designer.cs' -and $_.BaseName -cle $contract.Baseline } |
        Sort-Object BaseName | ForEach-Object BaseName)
    $old=[ordered]@{ Identity=@{ ServerName='disposable'; DatabaseName='disposable'; DatabaseGuid=[Guid]::NewGuid().ToString() };
        History=@($ids | ForEach-Object { @{ MigrationId=$_; ProductVersion='10.0.10' } }); Columns=@(); CanAlter=$true; CanInsertHistory=$true }
    $binding=[ordered]@{ ReleaseId=$release; OperationId=[Guid]::NewGuid().ToString(); Commit=('09945ae1f0345617e46266369a3e3e9171da08f9');
        Paths=$paths; OriginalDatabase=$old; TargetDatabase=(Get-RepositoryRecoveryTargetState $old); MigrationSha256=$contract.UpSha256;
        Payloads=@{ OriginalWeb=(Get-HybridPayloadFingerprint $paths.ApplicationRoot); CandidateWeb=(Get-HybridPayloadFingerprint $paths.CandidateRoot);
            OriginalCompanion=(Get-HybridPayloadFingerprint $paths.CompanionRoot); CandidateCompanion=(Get-HybridPayloadFingerprint $paths.CompanionCandidateRoot) };
        OriginalTask=@{ Enabled=$TaskEnabled; Identity='task'; Xml='original XML' };
        OriginalIis=@{ PoolState='Started'; StartMode='AlwaysRunning'; IdleTimeout='00:00:00'; PreloadEnabled=$true };
        ConfigurationSha256=('C'*64); LauncherSha256=('D'*64); Retained=$null }
    $box=@{ TaskEnabled=$TaskEnabled; Pool='Started'; Db=$old; SqlCalls=0; OldStarts=0; NewStarts=0; ReleaseCalls=0; Restores=0;
        ProbeFailure=$false; RestoreFailure=$false; ActivationFailure=$false; HoldOwner=$null }
    $ports=@{
        CreateHold={ $box.HoldOwner=$release; Write-HybridRebuildJson -Path $paths.HoldPath -Value $release }.GetNewClosure()
        AssertHold={ if ($box.HoldOwner -cne $release) { throw 'hold-owner-changed' } }.GetNewClosure()
        DisableTask={ $box.TaskEnabled=$false }.GetNewClosure()
        AssertTaskDisabled={ if ($box.TaskEnabled) { throw 'task-not-disabled' } }.GetNewClosure()
        Stop={ $box.Pool='Stopped' }.GetNewClosure()
        CaptureRetained={ return @{ Projection=@{Version=1}; Tables=@{Vectors=@{RowCount=329;Fingerprint=('A'*64)}} } }
        ReadDatabase={ return $box.Db }.GetNewClosure()
        ApplyMigration={ $box.SqlCalls++; $box.Db=$binding.TargetDatabase; return $box.Db }.GetNewClosure()
        BackupCompanion={ Copy-Item -LiteralPath $paths.CompanionRoot -Destination $paths.CompanionPreviousRoot -Recurse }.GetNewClosure()
        Activate={
            Assert-True ($box.Pool -ceq 'Stopped' -and $box.HoldOwner -ceq $release -and !$box.TaskEnabled) 'Live mutation preceded hold/task/stop.'
            Copy-Item -LiteralPath (Join-Path $paths.CompanionCandidateRoot 'payload.txt') -Destination $paths.CompanionRoot -Force
            [void](New-Item -ItemType Directory -Path $paths.ApplicationRoot)
            Copy-Item -LiteralPath (Join-Path $paths.CandidateRoot 'payload.txt') -Destination $paths.ApplicationRoot
        }.GetNewClosure()
        Start={ $box.Pool='Started'; if ((Get-Content -LiteralPath (Join-Path $paths.ApplicationRoot 'payload.txt')).Trim() -ceq 'CandidateRoot') {
                $box.NewStarts++
            } else { $box.OldStarts++ } }.GetNewClosure()
        Validate={ if ($box.ProbeFailure) { throw 'candidate-probe-failure' } }.GetNewClosure()
        ValidateWebRollback={ Assert-True ($box.Pool -ceq 'Stopped') 'Predecessor restarted.'
            Assert-True ((Get-HybridPayloadFingerprint $paths.ApplicationRoot) -ceq $binding.Payloads.OriginalWeb) 'Prior Web bytes not restored.' }.GetNewClosure()
        RestoreCompanion={ $box.Restores++; if ($box.RestoreFailure) { throw 'companion-restore-failure' }
            Copy-Item -LiteralPath (Join-Path $paths.CompanionPreviousRoot 'payload.txt') -Destination $paths.CompanionRoot -Force }.GetNewClosure()
        AssertOriginals={ Assert-True ((Get-HybridPayloadFingerprint $paths.CompanionRoot) -ceq $binding.Payloads.OriginalCompanion) 'Companion bytes not preserved.' }.GetNewClosure()
        ReleaseHold={ $box.ReleaseCalls++; if ($box.ActivationFailure) { throw 'lost-release-acknowledgement' }
            $box.HoldOwner=$null; Remove-Item -LiteralPath $paths.HoldPath }.GetNewClosure()
        PostReleaseProbes={ }
        RestoreTaskPolicy={ $box.TaskEnabled=$TaskEnabled }.GetNewClosure()
        AssertCompleted={ Assert-True ($box.TaskEnabled -eq $TaskEnabled) 'Original task activation policy changed.' }.GetNewClosure()
        AssertMigrationLocations={
            if (Test-Path -LiteralPath $paths.PreviousRoot) { throw 'payload-location-drift' }
            Assert-True ((Get-HybridPayloadFingerprint $paths.ApplicationRoot) -ceq $binding.Payloads.OriginalWeb) 'payload-location-drift'
        }.GetNewClosure()
    }
    $ports.Swap={ param($activate,$start,$validate,$rollback)
        Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $paths.ApplicationRoot -CandidateRoot $paths.CandidateRoot `
            -PreviousRoot $paths.PreviousRoot -FailedRoot $paths.FailedRoot -RestartPreviousApplication:$false `
            -StopApplication $ports.Stop -ActivateCandidate $activate -StartApplication $start -ValidateApplication $validate -ValidateRollbackApplication $rollback
    }.GetNewClosure()
    return @{ Root=$root; Binding=$binding; Box=$box; Ports=$ports; Path=(Join-Path $paths.ReleaseRoot 'repository-recovery-receipt.json') }
}

$cases=[Collections.Generic.List[object]]::new()
try {
    # Explicit continuation retains the old release/candidate bindings while
    # recording the corrected updater's separate operator identity.
    $case=New-Case $false; $cases.Add($case)
    $originalBindingHash=Get-RepositoryRecoveryValueHash $case.Binding
    [void](New-RepositoryRecoveryReleaseReceipt -ReceiptPath $case.Path -Binding $case.Binding)
    & $case.Ports.CreateHold; & $case.Ports.Stop
    $case.Ports.CreateHold={ throw 'Prepared resume must retain the owned hold.' }
    $case.Ports.AssertPreparedLocations={ Assert-True ($case.Box.Pool -ceq 'Stopped') 'prepared-location-drift' }.GetNewClosure()
    $case.Ports.CaptureRetained={
        $intent=Read-RepositoryRecoveryReleaseReceipt $case.Path
        Assert-True ($intent.Phase -ceq 'Prepared' -and $intent.PreparedContinuation.operator_commit -ceq ('f'*40)) 'Capture preceded durable operator evidence.'
        return @{ Projection=@{Version=1}; Tables=@{Vectors=@{RowCount=329;Fingerprint=('A'*64)}} }
    }.GetNewClosure()
    $result=Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports -ResumePrepared -OperatorCommit ('f'*40)
    $resumed=Read-RepositoryRecoveryReleaseReceipt $case.Path
    Assert-True ($result.ok -and $case.Box.SqlCalls -eq 1 -and $resumed.Binding.Commit -ceq $case.Binding.Commit -and
        $resumed.PreparedContinuation.operator_commit -ceq ('f'*40) -and $resumed.PreparedContinuation.original_binding_hash -ceq $originalBindingHash) 'Prepared continuation changed original identity or omitted operator evidence.'
    Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports -ResumePrepared -OperatorCommit ('f'*40) } 'unsupported-prepared'

    foreach ($boundary in @('baseline','failure','intent','later-phase','binding','hold','gpu','location','database')) {
        $case=New-Case $false; $cases.Add($case)
        $receipt=New-RepositoryRecoveryReleaseReceipt -ReceiptPath $case.Path -Binding $case.Binding
        & $case.Ports.CreateHold; & $case.Ports.Stop
        $case.Ports.AssertPreparedLocations={ Assert-True ($case.Box.Pool -ceq 'Stopped') 'prepared-location-drift' }.GetNewClosure()
        $case.Ports.CaptureRetained={ throw 'Unsafe Prepared request reached capture.' }
        $reason='unsupported-prepared'
        switch ($boundary) {
            'baseline' { $receipt.Binding.Retained=@{Projection=@{Version=1};Tables=@{}}; Save-RepositoryRecoveryReleaseReceipt $case.Path $receipt }
            'failure' { $receipt.Failure='injected-prior-failure'; Save-RepositoryRecoveryReleaseReceipt $case.Path $receipt }
            'intent' { $receipt.Reconciliation=@{mode='prior-continuation'}; Save-RepositoryRecoveryReleaseReceipt $case.Path $receipt }
            'later-phase' { $receipt.Binding.Retained=@{Projection=@{Version=1};Tables=@{}}; $receipt.Phase='MigrationIntent'; Save-RepositoryRecoveryReleaseReceipt $case.Path $receipt }
            'binding' { $case.Binding=$case.Binding | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable; $case.Binding.Commit=('e'*40); $reason='binding-or-operator-drift' }
            'hold' { $case.Box.HoldOwner='another-release'; $reason='blocked-before-hold' }
            'gpu' { $case.Ports.Stop={ throw 'uncertain-gpu-cleanup' }; $reason='uncertain-gpu-cleanup' }
            'location' { $case.Box.Pool='Started'; $reason='prepared-location-drift' }
            'database' { $case.Box.Db=$case.Box.Db | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable; $case.Box.Db.Identity.DatabaseGuid=[Guid]::NewGuid().ToString(); $reason='database-state-drift' }
        }
        Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports -ResumePrepared -OperatorCommit ('f'*40) } $reason
        Assert-True ($case.Box.SqlCalls -eq 0 -and $case.Box.NewStarts -eq 0 -and $case.Box.ReleaseCalls -eq 0) "Unsafe Prepared boundary crossed activation: $boundary"
    }

    foreach ($enabled in @($false,$true)) {
        $case=New-Case $enabled; $cases.Add($case)
        $result=Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports
        Assert-True ($result.ok -and $case.Box.SqlCalls -eq 1 -and $case.Box.ReleaseCalls -eq 1) 'Release did not complete exactly once.'
        $receipt=Read-RepositoryRecoveryReleaseReceipt $case.Path
        Assert-True ($receipt.Phase -ceq 'Completed' -and $case.Box.TaskEnabled -eq $enabled) 'Completion or original policy not durable.'
        # A duplicate returns the saved result after read-only checks, without new SQL/start/release.
        [void](Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports)
        Assert-True ($case.Box.SqlCalls -eq 1 -and $case.Box.NewStarts -eq 1 -and $case.Box.ReleaseCalls -eq 1) 'Completed replay repeated a mutation.'
    }
    foreach ($failure in @('ProbeFailure','RestoreFailure','ActivationFailure')) {
        $case=New-Case $true; $cases.Add($case); $case.Box.ProbeFailure=($failure -ne 'ActivationFailure'); $case.Box.$failure=$true
        Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports } 'held|activation-uncertain'
        $receipt=Read-RepositoryRecoveryReleaseReceipt $case.Path
        Assert-True (!$case.Box.TaskEnabled -and $case.Box.OldStarts -eq 0) 'Failure restarted or enabled the predecessor.'
        if ($failure -eq 'ActivationFailure') {
            Assert-True ($receipt.Phase -ceq 'HoldReleaseIntent' -and $case.Box.Restores -eq 0) 'Uncertain activation rolled back or lost intent.'
        } else {
            Assert-True ($receipt.Phase -ceq 'FailedHeld' -and $case.Box.Pool -ceq 'Stopped' -and $case.Box.HoldOwner -ceq $case.Binding.ReleaseId -and $case.Box.ReleaseCalls -eq 0) 'Failed candidate released its hold.'
        }
        Expect-Failure { Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports } 'unsupported-restart-boundary'
    }
    foreach ($applied in @($false,$true)) {
        $case=New-Case $false; $cases.Add($case)
        $case.Binding.Retained=& $case.Ports.CaptureRetained
        $receipt=New-RepositoryRecoveryReleaseReceipt -ReceiptPath $case.Path -Binding $case.Binding
        $receipt.Phase='MigrationIntent'; Save-RepositoryRecoveryReleaseReceipt $case.Path $receipt
        & $case.Ports.CreateHold; & $case.Ports.DisableTask; & $case.Ports.Stop
        if ($applied) { $case.Box.Db=$case.Binding.TargetDatabase }
        $result=Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports
        Assert-True ($result.migration -ceq $(if ($applied) {'applied'} else {'not-applied'}) -and $case.Box.SqlCalls -eq 0 -and $case.Box.NewStarts -eq 0 -and $case.Box.ReleaseCalls -eq 0) 'Lost-acknowledgement reconciliation mutated runtime.'
        $case.Box.HoldOwner='another-release'
        Expect-Failure { Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports } 'hold-owner-changed'
    }
    $case=New-Case $true; $cases.Add($case)
    $case.Ports.Stop={ throw 'uncertain-gpu-cleanup' }
    Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports } 'uncertain-gpu-cleanup'
    $receipt=Read-RepositoryRecoveryReleaseReceipt $case.Path
    Assert-True ($receipt.Phase -ceq 'FailedHeld' -and $receipt.FailureAtPhase -ceq 'Prepared' -and
        $case.Box.SqlCalls -eq 0 -and $case.Box.NewStarts -eq 0 -and $case.Box.ReleaseCalls -eq 0 -and !$case.Box.TaskEnabled) 'Uncertain cleanup crossed the migration/activation boundary.'
    $case=New-Case $true; $cases.Add($case)
    $case.Ports.CreateHold={ throw 'another-release-owns-hold' }
    Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports } 'blocked-before-hold'
    Assert-True ($case.Box.TaskEnabled -and $case.Box.Pool -ceq 'Started' -and $case.Box.SqlCalls -eq 0) 'A failed hold acquisition took ownership of another release.'
    $case=New-Case $false; $cases.Add($case); $lost=$case
    $case.Ports.ApplyMigration={ $lost.Box.SqlCalls++; $lost.Box.Db=$lost.Binding.TargetDatabase; throw 'lost-sql-acknowledgement' }.GetNewClosure()
    Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports } 'failed-held'
    $reconciled=Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports
    Assert-True ($reconciled.migration -ceq 'applied' -and $case.Box.SqlCalls -eq 1 -and $case.Box.ReleaseCalls -eq 0) 'Lost SQL acknowledgement was replayed or released.'
    $case.Box.Db=$case.Binding.TargetDatabase | ConvertTo-Json -Depth 12 | ConvertFrom-Json -AsHashtable
    $case.Box.Db.Identity.DatabaseGuid=[Guid]::NewGuid().ToString()
    Expect-Failure { Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports } 'database-state-drift'
    $case.Box.Db=$case.Binding.TargetDatabase | ConvertTo-Json -Depth 12 | ConvertFrom-Json -AsHashtable
    $case.Box.Db.History[0].ProductVersion='unexpected-version'
    Expect-Failure { Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports } 'database-state-drift'
    $case.Box.Db=$case.Binding.TargetDatabase
    Set-Content -LiteralPath (Join-Path $case.Binding.Paths.ApplicationRoot 'payload.txt') -Value 'external change'
    Expect-Failure { Invoke-RepositoryRecoveryReceiptReconciliation -ReceiptPath $case.Path -Ports $case.Ports } 'payload-location-drift'

    # Execute the updater's actual wrapper against disposable paths and read-only
    # management ports. This catches callback scope/wiring errors, independently
    # of the release-flow tests above. No production configuration is loaded.
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'), [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) 'Updater syntax failed.'
    $wanted=@('Get-RepositoryRecoveryTaskObservation','Assert-RepositoryRecoveryTaskIdentity','Assert-RepositoryRecoveryPayloadHash','Assert-RepositoryRecoveryNewWorker',
        'Invoke-RepositoryRecoveryIisUpdate','Test-IisHostingSettingsMatch')
    foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -cin $wanted }, $true)) {
        Invoke-Expression $definition.Extent.Text
    }
    # Exercise the actual candidate-owner helper with deterministic process identities.
    # CIM exposes microseconds; the native process start retains all 100 ns ticks.
    $ownerStarted=[DateTime]::Parse('2026-10-05T00:00:00Z', $null, [Globalization.DateTimeStyles]::RoundtripKind)
    function New-OwnerCase {
        return @{ Processes=@{
            100=[pscustomobject]@{ProcessId=100;ParentProcessId=50;CreationDate=$ownerStarted.AddSeconds(1);ExecutablePath='synthetic-iis'}
            200=[pscustomobject]@{ProcessId=200;ParentProcessId=100;CreationDate=$ownerStarted.AddSeconds(2);ExecutablePath='synthetic-host'}
            300=[pscustomobject]@{ProcessId=300;ParentProcessId=200;CreationDate=$ownerStarted.AddSeconds(3);ExecutablePath='synthetic-worker'}
        }; NativeStart=$ownerStarted.AddSeconds(3).AddTicks(7); NativeReads=0; CimReads=@{}; ReusePid=0; ChangeNativeStart=$false }
    }
    function Get-HybridIisWorkerIds { param($AppCmdPath,$PoolName) return @(100) }
    function Get-Process { param($Id,$ErrorAction)
        $ownerCase.NativeReads++
        $start=$ownerCase.NativeStart
        if ($ownerCase.ChangeNativeStart -and $ownerCase.NativeReads -gt 1) { $start=$start.AddTicks(1) }
        return @{StartTime=$start}
    }
    function Get-CimInstance { param($ClassName,$Filter,$ErrorAction)
        if ($Filter -notmatch '^ProcessId = (\d+)$') { throw 'Unexpected synthetic process lookup.' }
        $id=[int]$Matches[1]
        if (!$ownerCase.CimReads.ContainsKey($id)) { $ownerCase.CimReads[$id]=0 }
        $ownerCase.CimReads[$id]++
        if ($id -eq $ownerCase.ReusePid -and $ownerCase.CimReads[$id] -gt 1) {
            $ownerCase.Processes[$id].CreationDate=$ownerStarted.AddSeconds(5)
        }
        return $ownerCase.Processes[$id]
    }
    function Get-FileHash { param($LiteralPath,$Algorithm) return @{Hash=('D'*64)} }
    $ownerRow=@{ProcessId=300;ProcessStartedAtUtc=$ownerStarted.AddSeconds(3).AddTicks(7);ExecutableFingerprint=('D'*64)}
    $ownerCase=New-OwnerCase
    Assert-RepositoryRecoveryNewWorker $ownerRow $ownerStarted
    $ownerCase=New-OwnerCase; $ownerCase.Processes[200].CreationDate=$ownerStarted.AddSeconds(4)
    Expect-Failure { Assert-RepositoryRecoveryNewWorker $ownerRow $ownerStarted } 'owner-unverified'
    $ownerCase=New-OwnerCase; $ownerCase.Processes[100].CreationDate=$ownerStarted.AddSeconds(-1)
    Expect-Failure { Assert-RepositoryRecoveryNewWorker $ownerRow $ownerStarted } 'owner-unverified'
    foreach ($reused in @(100,200,300)) {
        $ownerCase=New-OwnerCase; $ownerCase.ReusePid=$reused
        Expect-Failure { Assert-RepositoryRecoveryNewWorker $ownerRow $ownerStarted } 'owner-unverified|process-identity-drift'
    }
    $ownerCase=New-OwnerCase; $ownerCase.ChangeNativeStart=$true
    Expect-Failure { Assert-RepositoryRecoveryNewWorker $ownerRow $ownerStarted } 'process-identity-drift'
    Remove-Item -LiteralPath Function:Get-FileHash

    $wrapperCase=New-Case $false; $cases.Add($wrapperCase)
    $CanonicalLiveRoot=$wrapperCase.Root; $CanonicalRecoveryRoot=$wrapperCase.Root; $CanonicalDeployRoot=$wrapperCase.Binding.Paths.ApplicationRoot
    $IncrementalRecoveryRoot=$wrapperCase.Root; $InteractiveHostRoot=$wrapperCase.Binding.Paths.CompanionRoot
    $ValidationHoldPath=$wrapperCase.Binding.Paths.HoldPath; $InteractiveHostTaskName='FluxKnowledge.OutlookHost'; $SiteName='FluxKnowledge'
    $ReadinessTimeoutSeconds=10
    [void](New-Item -ItemType Directory -Path (Join-Path $CanonicalLiveRoot 'Config'))
    $configPath=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    Set-Content -LiteralPath $configPath -Value '{"disposable":"unchanged"}'
    $companionConfigPath=Join-Path $CanonicalLiveRoot 'appsettings.Production.json'
    Set-Content -LiteralPath $companionConfigPath -Value '{"disposable":"unchanged"}'
    function Get-ScheduledTask { param($TaskName,$TaskPath,$ErrorAction) return @{Settings=@{Enabled=$wrapperCase.Box.TaskEnabled};State='Ready'} }
    function Export-ScheduledTask { param($TaskName,$TaskPath,$ErrorAction)
        $enabled=if ($wrapperCase.Box.TaskEnabled) { '' } else { '<Enabled>false</Enabled>' }
        return '<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Settings>'+ $enabled +
            '</Settings><Actions><Exec><Command>C:\Windows\System32\wscript.exe</Command><Arguments>//B //NoLogo "E:\Codex Workspaces\Scripts\Outlook\run-outlook-hidden.vbs"</Arguments></Exec></Actions></Task>'
    }
    function Assert-NotReparsePoint { param($Path,$Message) if ($Path -like '*run-outlook-hidden.vbs') { return }
        Assert-True (((Get-Item -LiteralPath $Path).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) $Message }
    function Get-FileHash { param($LiteralPath,$Algorithm='SHA256') if ($LiteralPath -like '*run-outlook-hidden.vbs') { return @{Hash=('D'*64)} }
        return Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm }
    function Get-IncrementalIisHostingSettings {
        $observed=$wrapperCase.Binding.OriginalIis | ConvertTo-Json | ConvertFrom-Json -AsHashtable
        $observed.PoolState=$wrapperCase.Box.Pool
        return $observed
    }
    function Get-WebAppPoolState { param($Name) return @{Value=$wrapperCase.Box.Pool} }
    function Get-DeploymentSqlConnectionString { return 'disposable-placeholder' }
    function Get-RepositoryRecoveryDatabaseState { param($ConnectionString) return $wrapperCase.Box.Db }
    function Get-RepositoryRecoveryRetainedState { param($ConnectionString,$Projection,$VerifyNewWorker) return $wrapperCase.Binding.Retained }
    function Assert-RepositoryRecoveryEvidenceEmpty { param($ConnectionString) }
    function Wait-InteractiveHostStopped { param($TaskName,$TimeoutSeconds) Assert-True (!$wrapperCase.Box.TaskEnabled) 'Task policy not held.' }
    foreach ($forbidden in @('Publish-InteractiveHostCandidate','Copy-InteractiveHostPayload','Restore-InteractiveHostPayload',
        'New-DeploymentValidationHold','Remove-DeploymentValidationHold','Stop-HybridIisAfterGpuDrain','Start-WebAppPool','Enable-ScheduledTask','Disable-ScheduledTask')) {
        Set-Item -LiteralPath ('Function:'+ $forbidden) -Value { throw 'Reconciliation invoked a forbidden production action.' }
    }
    $wrapperCase.Binding.OriginalTask=Get-RepositoryRecoveryTaskObservation
    $wrapperCase.Binding.ConfigurationSha256=(Get-FileHash -LiteralPath $configPath).Hash
    $wrapperCase.Binding.CompanionConfigurationSha256=(Get-FileHash -LiteralPath $companionConfigPath).Hash
    [void](Invoke-RepositoryRecoveryRelease -ReceiptPath $wrapperCase.Path -Binding $wrapperCase.Binding -Ports $wrapperCase.Ports)
    $wrapperResult=Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit $wrapperCase.Binding.Commit -ExistingRelease $wrapperCase.Binding.ReleaseId | ConvertFrom-Json
    Assert-True ($wrapperResult.ok -and $wrapperCase.Box.SqlCalls -eq 1) 'Actual updater wrapper replay failed or performed work.'
    $wrapperCase.Box.TaskEnabled=$true
    Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit $wrapperCase.Binding.Commit -ExistingRelease $wrapperCase.Binding.ReleaseId } 'task-or-launcher-drift'
    $wrapperCase.Box.TaskEnabled=$false
    Set-Content -LiteralPath $companionConfigPath -Value '{"disposable":"changed"}'
    Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit $wrapperCase.Binding.Commit -ExistingRelease $wrapperCase.Binding.ReleaseId } 'configuration-or-hosting-drift'
    Set-Content -LiteralPath $companionConfigPath -Value '{"disposable":"unchanged"}'
    Set-Content -LiteralPath $configPath -Value '{"disposable":"changed"}'
    Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit $wrapperCase.Binding.Commit -ExistingRelease $wrapperCase.Binding.ReleaseId } 'configuration-or-hosting-drift'

    # Real wrapper inspection of a Prepared release is read-only, accepts the
    # separately identified updater, and still verifies the original candidates.
    $wrapperCase=New-Case $false; $cases.Add($wrapperCase)
    $CanonicalLiveRoot=$wrapperCase.Root; $CanonicalRecoveryRoot=$wrapperCase.Root; $CanonicalDeployRoot=$wrapperCase.Binding.Paths.ApplicationRoot
    $IncrementalRecoveryRoot=$wrapperCase.Root; $InteractiveHostRoot=$wrapperCase.Binding.Paths.CompanionRoot
    $ValidationHoldPath=$wrapperCase.Binding.Paths.HoldPath
    [void](New-Item -ItemType Directory -Path (Join-Path $CanonicalLiveRoot 'Config'))
    $configPath=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    $companionConfigPath=Join-Path $CanonicalLiveRoot 'appsettings.Production.json'
    Set-Content -LiteralPath $configPath -Value '{"disposable":"unchanged"}'
    Set-Content -LiteralPath $companionConfigPath -Value '{"disposable":"unchanged"}'
    $wrapperCase.Binding.OriginalTask=Get-RepositoryRecoveryTaskObservation
    $wrapperCase.Binding.ConfigurationSha256=(Get-FileHash -LiteralPath $configPath).Hash
    $wrapperCase.Binding.CompanionConfigurationSha256=(Get-FileHash -LiteralPath $companionConfigPath).Hash
    Set-Content -LiteralPath (Join-Path $wrapperCase.Binding.Paths.ReleaseRoot 'repository-recovery-up.sql') -Value 'synthetic-reviewed-up'
    function Get-FileHash { param($LiteralPath,$Algorithm='SHA256')
        if ($LiteralPath -like '*run-outlook-hidden.vbs') { return @{Hash=('D'*64)} }
        if ($LiteralPath -like '*repository-recovery-up.sql') {
            if ((Get-Content -LiteralPath $LiteralPath).Trim() -cne 'synthetic-reviewed-up') { return @{Hash=('0'*64)} }
            return @{Hash=(Get-RepositoryRecoveryMigrationContract).UpSha256}
        }
        return Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm
    }
    function Assert-RepositoryRecoveryCandidateCommit { param($Path,$Commit)
        Assert-True ($Path -cin @($wrapperCase.Binding.Paths.CandidateRoot,$wrapperCase.Binding.Paths.CompanionCandidateRoot) -and $Commit -ceq $wrapperCase.Binding.Commit) 'Original candidate label was not checked.' }
    [void](New-RepositoryRecoveryReleaseReceipt $wrapperCase.Path $wrapperCase.Binding)
    & $wrapperCase.Ports.CreateHold; & $wrapperCase.Ports.Stop
    $receiptHash=(Get-FileHash -LiteralPath $wrapperCase.Path).Hash
    $inspected=Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('f'*40) -ExistingRelease $wrapperCase.Binding.ReleaseId -ResumePrepared -InspectPrepared
    Assert-True ($inspected.candidate_commit -ceq $wrapperCase.Binding.Commit -and $inspected.operator_commit -ceq ('f'*40) -and
        (Get-FileHash -LiteralPath $wrapperCase.Path).Hash -ceq $receiptHash -and $wrapperCase.Box.SqlCalls -eq 0 -and $wrapperCase.Box.NewStarts -eq 0 -and $wrapperCase.Box.ReleaseCalls -eq 0) 'Prepared inspection mutated state or rebound its candidate.'
    Set-Content -LiteralPath (Join-Path $wrapperCase.Binding.Paths.CandidateRoot 'payload.txt') -Value 'changed-candidate'
    Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('f'*40) -ExistingRelease $wrapperCase.Binding.ReleaseId -ResumePrepared -InspectPrepared } 'payload-drift'
    Set-Content -LiteralPath (Join-Path $wrapperCase.Binding.Paths.CandidateRoot 'payload.txt') -Value 'CandidateRoot'
    Set-Content -LiteralPath (Join-Path $wrapperCase.Binding.Paths.ReleaseRoot 'repository-recovery-up.sql') -Value 'changed-sql'
    Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('f'*40) -ExistingRelease $wrapperCase.Binding.ReleaseId -ResumePrepared -InspectPrepared } 'prepared-sql-drift'

    function Use-PreparedWrapperCase($NextCase) {
        $script:wrapperCase=$NextCase
        $script:CanonicalLiveRoot=$NextCase.Root; $script:CanonicalRecoveryRoot=$NextCase.Root
        $script:CanonicalDeployRoot=$NextCase.Binding.Paths.ApplicationRoot; $script:IncrementalRecoveryRoot=$NextCase.Root
        $script:InteractiveHostRoot=$NextCase.Binding.Paths.CompanionRoot; $script:ValidationHoldPath=$NextCase.Binding.Paths.HoldPath
        [void](New-Item -ItemType Directory -Path (Join-Path $CanonicalLiveRoot 'Config'))
        $script:configPath=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
        $script:companionConfigPath=Join-Path $CanonicalLiveRoot 'appsettings.Production.json'
        Set-Content -LiteralPath $configPath -Value '{"disposable":"unchanged"}'
        Set-Content -LiteralPath $companionConfigPath -Value '{"disposable":"unchanged"}'
        $NextCase.Binding.OriginalTask=Get-RepositoryRecoveryTaskObservation
        $NextCase.Binding.ConfigurationSha256=(Get-FileHash -LiteralPath $configPath).Hash
        $NextCase.Binding.CompanionConfigurationSha256=(Get-FileHash -LiteralPath $companionConfigPath).Hash
        Set-Content -LiteralPath (Join-Path $NextCase.Binding.Paths.ReleaseRoot 'repository-recovery-up.sql') -Value 'synthetic-reviewed-up'
        [void](New-RepositoryRecoveryReleaseReceipt $NextCase.Path $NextCase.Binding)
        & $NextCase.Ports.CreateHold; & $NextCase.Ports.Stop
        $NextCase.Ports.AssertPreparedLocations={}
    }
    $case=New-Case $false; $cases.Add($case); Use-PreparedWrapperCase $case
    [void](Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports -ResumePrepared -OperatorCommit ('f'*40))
    $completed=Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('f'*40) -ExistingRelease $case.Binding.ReleaseId | ConvertFrom-Json
    Assert-True ($completed.ok -and $case.Box.SqlCalls -eq 1 -and $case.Box.NewStarts -eq 1 -and $case.Box.ReleaseCalls -eq 1) 'Recorded continuation operator could not inspect completed result, or replayed it.'
    Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('e'*40) -ExistingRelease $case.Binding.ReleaseId } 'release-commit-drift'
    $edited=Read-RepositoryRecoveryReleaseReceipt $case.Path
    $edited.PreparedContinuation.operator_commit=('e'*40)
    Expect-Failure { Save-RepositoryRecoveryReleaseReceipt $case.Path $edited } 'operator-continuation-drift'

    foreach ($applied in @($false,$true)) {
        $case=New-Case $false; $cases.Add($case); Use-PreparedWrapperCase $case
        $case.Ports.ApplyMigration={
            $case.Box.SqlCalls++
            if ($applied) { $case.Box.Db=$case.Binding.TargetDatabase }
            throw 'lost-sql-acknowledgement'
        }.GetNewClosure()
        Expect-Failure { Invoke-RepositoryRecoveryRelease -ReceiptPath $case.Path -Binding $case.Binding -Ports $case.Ports -ResumePrepared -OperatorCommit ('f'*40) } 'failed-held'
        $reconciled=Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('f'*40) -ExistingRelease $case.Binding.ReleaseId | ConvertFrom-Json
        $after=Read-RepositoryRecoveryReleaseReceipt $case.Path
        Assert-True ($reconciled.migration -ceq $(if ($applied) {'applied'} else {'not-applied'}) -and
            $after.PreparedContinuation.operator_commit -ceq ('f'*40) -and $case.Box.SqlCalls -eq 1 -and $case.Box.NewStarts -eq 0 -and $case.Box.ReleaseCalls -eq 0) 'Reconciliation lost continuation identity or replayed unknown work.'
        Expect-Failure { Invoke-RepositoryRecoveryIisUpdate -SourceRoot $SourceRoot -Commit ('e'*40) -ExistingRelease $case.Binding.ReleaseId } 'release-commit-drift'
    }

    $case=New-Case $false; $cases.Add($case)
    $receipt=New-RepositoryRecoveryReleaseReceipt -ReceiptPath $case.Path -Binding $case.Binding
    $changed=Get-Content -LiteralPath $case.Path -Raw | ConvertFrom-Json -AsHashtable
    $changed.Version=999; Write-HybridRebuildJson $case.Path $changed
    Expect-Failure { Read-RepositoryRecoveryReleaseReceipt $case.Path } 'receipt-invalid'
    $changed.Version=1; $changed.Binding.Commit=('f'*40); Write-HybridRebuildJson $case.Path $changed
    Expect-Failure { Read-RepositoryRecoveryReleaseReceipt $case.Path } 'receipt-invalid'
    Write-Output 'Release ports passed: both task policies, exact completed replay, stopped held rollback, companion failure, activation uncertainty, lost SQL acknowledgement and receipt/hold refusal.'
} finally {
    foreach ($case in $cases) {
        $absolute=[IO.Path]::GetFullPath($case.Root)
        if (!$absolute.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe disposable cleanup path.' }
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
