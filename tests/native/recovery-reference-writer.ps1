param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][string]$ScratchRoot,
    [Parameter(Mandatory)][string]$ExpectedDatabase, [ValidateSet('complete','lost-session','external-incomplete','descendant')][string]$Mode)
$ErrorActionPreference='Stop'
if($Mode -ceq 'descendant') {
    $process=[Diagnostics.Process]::GetCurrentProcess()
    try { [IO.File]::WriteAllText((Join-Path $ScratchRoot 'descendant-owner'), "$($process.Id)|$($process.StartTime.ToUniversalTime().Ticks)") }
    finally { $process.Dispose() }
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while(![IO.File]::Exists((Join-Path $ScratchRoot 'descendant-release'))) {
        if([DateTime]::UtcNow -gt $deadline) { throw 'Isolated descendant release timed out.' }
        Start-Sleep -Milliseconds 20
    }
    exit 0
}
$normalised=$env:FLUXKNOWLEDGE_RECOVERY_REFERENCE_TEST_SQL -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
$target=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
if($target.InitialCatalog -cne $ExpectedDatabase -or $ExpectedDatabase -cnotmatch '^FluxKnowledge_Phase1Tests_[a-f0-9]{32}$') { throw 'Not an owned disposable database.' }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/recovery-reference-writer.psm1') -Force
$lease=Start-RecoveryReferenceWrite -ConnectionString $target.ConnectionString -RecoveryRoot (Join-Path $ScratchRoot 'Recovery') -OwnerExitSufficient:($Mode -ceq 'lost-session')
try {
    if($Mode -ceq 'external-incomplete') {
        $start=[Diagnostics.ProcessStartInfo]::new('pwsh')
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach($value in @('-NoProfile','-File',$PSCommandPath,'-SourceRoot',$SourceRoot,'-ScratchRoot',$ScratchRoot,'-ExpectedDatabase',$ExpectedDatabase,'-Mode','descendant')) { $start.ArgumentList.Add($value) }
        $descendant=[Diagnostics.Process]::Start($start)
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(5)
            while(![IO.File]::Exists((Join-Path $ScratchRoot 'descendant-owner'))) {
                if($descendant.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Isolated descendant did not start.' }
                Start-Sleep -Milliseconds 20
            }
        } finally { $descendant.Dispose() }
    }
    if($Mode -in @('lost-session','external-incomplete')) { $lease.Connection.Close() }
    [IO.File]::WriteAllText((Join-Path $ScratchRoot 'ready'),'ready')
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while(![IO.File]::Exists((Join-Path $ScratchRoot 'release'))) {
        if([DateTime]::UtcNow -gt $deadline) { throw 'Isolated writer release timed out.' }
        Start-Sleep -Milliseconds 20
    }
    if($Mode -ceq 'complete') {
        Complete-RecoveryReferenceWrite -Lease $lease
        Complete-RecoveryReferenceWrite -Lease $lease # Same completed write is idempotent.
    }
    'PASS: isolated Recovery writer lifetime.'
} finally { Close-RecoveryReferenceWrite -Lease $lease }
