param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
$stored=$env:FLUXKNOWLEDGE_OCR_STORED_EXECUTION -ceq '1'
& (Join-Path $SourceRoot 'tests/native/source-journal-start-admission.ps1') -SourceRoot $SourceRoot -OcrCleanupOnly:(!$stored) -OcrStoredExecution:$stored
if($LASTEXITCODE -and $LASTEXITCODE -ne 0){throw 'OCR composed admission failed.'}
. (Join-Path $SourceRoot 'scripts/deploy/incremental-ocr-lifecycle-recovery.ps1')
function Assert-IdentityRefused([scriptblock]$Action){
    try{&$Action|Out-Null}catch{if($_.Exception.Message -like '*owned-gpu-identity-changed*'){return};throw}
    throw 'Changed owned process identity was admitted.'
}
[xml]$gpu='<nvidia_smi_log><gpu><processes><process_info><pid>42</pid><process_name>C:\Windows\System32\inetsrv\w3wp.exe</process_name></process_info><process_info><pid>43</pid><process_name>C:\Windows\System32\inetsrv\w3wp.exe</process_name></process_info></processes></gpu></nvidia_smi_log>'
$prior=@(@{Pid=42;StartedUtc='2026-01-01T00:00:00Z';Name='w3wp.exe';Path='C:\Windows\System32\inetsrv\w3wp.exe'})
$remaining=@(Select-OcrDesktopGpuProcesses $gpu @(42) $prior $prior)
if($remaining.Count -ne 1 -or [int]$remaining[0].pid -ne 43){throw 'Another pool sharing w3wp.exe was omitted.'}
if(@(Select-OcrDesktopGpuProcesses $gpu $null @() @()).Count -ne 2){throw 'An empty owned-worker inventory hid other GPU processes.'}
$changed=@(@{Pid=42;StartedUtc='2026-01-02T00:00:00Z';Name='w3wp.exe';Path=$prior[0].Path})
Assert-IdentityRefused {Select-OcrDesktopGpuProcesses $gpu @(42) $prior $changed}
$changed[0].StartedUtc=$prior[0].StartedUtc;$changed[0].Path='C:\foreign\w3wp.exe'
Assert-IdentityRefused {Select-OcrDesktopGpuProcesses $gpu @(42) $prior $changed}
Assert-IdentityRefused {Select-OcrDesktopGpuProcesses $gpu @(99) $prior $prior}
$changed[0].Path=$prior[0].Path;$gpu.SelectSingleNode('//process_info/process_name').InnerText='C:\foreign\w3wp.exe'
Assert-IdentityRefused {Select-OcrDesktopGpuProcesses $gpu @(42) $prior $changed}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-segmented-index-update.psm1') -Force
$area=$env:FLUXKNOWLEDGE_OCR_PACKET_TEST_ROOT
if(!$area -or ![IO.Path]::GetFullPath($area).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){
    throw 'Isolated packet output required.'
}
$CanonicalDeployRoot=Join-Path $area 'App'
$plan=@{Version=2;Mode='CleanupOnly';Binding=$null;TerminalBinding=@{Handle=@{BatchId=[Guid]::NewGuid()}};
    Commit=('a'*40);CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}
if($stored){$plan.Version=3;$plan.Mode='StoredExecution';$plan.TerminalBinding=$null;$plan.StoredExecutionBinding=@{Lease=@{Handle=@{BatchId=[Guid]::NewGuid()}}}}
$release=Join-Path (Join-Path $area 'Recovery/IncrementalUpdates') (Get-OcrRecoveryReleaseId $plan.Commit $true)
[void][IO.Directory]::CreateDirectory($release)
foreach($role in @('Web','OutlookHost')){
    $installed=Join-Path $area $role
    [void][IO.Directory]::CreateDirectory($installed)
    [IO.File]::WriteAllText((Join-Path $installed ('FluxKnowledge.'+$role+'.dll')),'synthetic application entry')
    $backup=Get-OcrRecoveryBackupPath $release $role $true
    Confirm-OcrRecoveryBackup $installed $backup (Get-HybridPayloadFingerprint $installed) {}
}
Write-OcrRecoveryJson (Join-Path $release 'manifest.json') (New-OcrApplicationManifest $plan)
if((Get-OcrRecoveryBackupPath $release Web $false) -cne (Join-Path $release 'previous-Web')){throw 'v1 backup compatibility changed.'}
'OCR emitted an ordinary recovery packet using the actual release-name, backup and manifest producers.'
