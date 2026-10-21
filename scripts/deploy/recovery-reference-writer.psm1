Set-StrictMode -Version Latest

# The lease owns a SQL session, not a thread-affine mutex. Never emit the lease:
# its connection is private deployment configuration. Disposal leaves unfinished proof.
function Start-RecoveryReferenceWrite {
    param([Parameter(Mandatory)][string]$ConnectionString, [Parameter(Mandatory)][string]$RecoveryRoot,
        [switch]$OwnerExitSufficient)
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($RecoveryRoot))
    if(![IO.Path]::IsPathFullyQualified($RecoveryRoot) -or $root.Length -gt 2048) { throw 'index-recovery-reference-root-invalid' }
    $normalised=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount='
    $builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    $builder['Pooling']=$false; $builder['Enlist']=$false; $builder['ConnectRetryCount']=0; $builder['Connect Timeout']=5
    $builder['Application Name']='FluxKnowledge.RecoveryReferenceWriter'
    if([string]::IsNullOrWhiteSpace($builder.DataSource) -or [string]::IsNullOrWhiteSpace($builder.InitialCatalog) -or
        $builder.InitialCatalog -iin @('master','model','msdb','tempdb')) { throw 'index-recovery-reference-database-invalid' }
    $machine=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key=$machine.OpenSubKey('SOFTWARE\Microsoft\Cryptography',$false)
        try { $machineId=[guid]::Empty
            if($null -eq $key -or ![guid]::TryParse([string]$key.GetValue('MachineGuid'),[ref]$machineId) -or $machineId -eq [guid]::Empty) { throw 'index-recovery-reference-machine-unavailable' }
        } finally { if($key) { $key.Dispose() } }
    } finally { $machine.Dispose() }
    $fingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($machineId.ToString('N')))).ToLowerInvariant()
    $process=[Diagnostics.Process]::GetCurrentProcess()
    try { $processId=$process.Id; $started=[DateTimeOffset]::new($process.StartTime.ToUniversalTime()) } finally { $process.Dispose() }
    $id=[guid]::NewGuid(); $connection=[Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    $transaction=$null
    try {
        $connection.Open(); $command=$connection.CreateCommand()
        try {
            $command.CommandTimeout=10
            $command.CommandText=@'
DECLARE @result int;
EXEC @result=sys.sp_getapplock @Resource=N'FluxKnowledge.RecoveryReferences', @LockMode=N'Shared', @LockOwner=N'Session', @LockTimeout=2000;
IF @result<0 THROW 51000, 'index-recovery-reference-busy', 1;
IF OBJECT_ID(N'dbo.IndexRecoveryAudit',N'U') IS NULL OR OBJECT_ID(N'dbo.IndexRecoveryWriters',N'U') IS NULL
    THROW 51000, 'index-recovery-reference-schema-required', 1;
'@
            [void]$command.ExecuteNonQuery()
            $transaction=$connection.BeginTransaction([Data.IsolationLevel]::Serializable); $command.Transaction=$transaction
            $command.CommandText=@'
SET LOCK_TIMEOUT 5000;
IF APPLOCK_MODE(N'public',N'FluxKnowledge.RecoveryReferences',N'Session')<>N'Shared'
    THROW 51000, 'index-recovery-reference-session-lost', 1;
UPDATE dbo.IndexRecoveryAudit SET IsValid=0,FailurePath=NULL WHERE Id=1;
IF @@ROWCOUNT<>1 THROW 51000, 'index-recovery-audit-state-missing', 1;
INSERT dbo.IndexRecoveryWriters(Id,RecoveryRoot,OwnerProcessId,OwnerStartedAtUtc,OwnerMachineFingerprint,OwnerExitSufficient,CreatedAtUtc)
    VALUES(@id,@root,@pid,@started,@machine,@exitSufficient,CONVERT(datetimeoffset(7),SYSUTCDATETIME()));
'@
            [void]$command.Parameters.Add('@id',[Data.SqlDbType]::UniqueIdentifier); $command.Parameters['@id'].Value=$id
            [void]$command.Parameters.Add('@root',[Data.SqlDbType]::NVarChar,2048); $command.Parameters['@root'].Value=$root
            [void]$command.Parameters.Add('@pid',[Data.SqlDbType]::Int); $command.Parameters['@pid'].Value=$processId
            [void]$command.Parameters.Add('@started',[Data.SqlDbType]::DateTimeOffset); $command.Parameters['@started'].Value=$started
            [void]$command.Parameters.Add('@machine',[Data.SqlDbType]::Char,64); $command.Parameters['@machine'].Value=$fingerprint
            [void]$command.Parameters.Add('@exitSufficient',[Data.SqlDbType]::Bit); $command.Parameters['@exitSufficient'].Value=[bool]$OwnerExitSufficient
            [void]$command.ExecuteNonQuery(); $transaction.Commit()
        } finally { $command.Dispose() }
        return [pscustomobject]@{ Connection=$connection; Id=$id; Root=$root; ProcessId=$processId; Started=$started;
            MachineFingerprint=$fingerprint; Completed=$false; Disposed=$false }
    } catch { $connection.Dispose(); throw }
    finally { if($transaction) { $transaction.Dispose() } }
}

function Complete-RecoveryReferenceWrite {
    param([Parameter(Mandatory)]$Lease)
    if($Lease.Disposed) { throw 'index-recovery-reference-writer-disposed' }
    if($Lease.Completed) { return }
    if($Lease.Connection.State -ne [Data.ConnectionState]::Open) { throw 'index-recovery-reference-session-lost' }
    $command=$Lease.Connection.CreateCommand()
    try {
        $command.CommandTimeout=5
        $command.CommandText=@'
IF APPLOCK_MODE(N'public',N'FluxKnowledge.RecoveryReferences',N'Session')<>N'Shared'
    THROW 51000, 'index-recovery-reference-session-lost', 1;
DELETE dbo.IndexRecoveryWriters WHERE Id=@id AND RecoveryRoot=@root AND OwnerProcessId=@pid
    AND OwnerStartedAtUtc=@started AND OwnerMachineFingerprint=@machine;
IF @@ROWCOUNT<>1 THROW 51000, 'index-recovery-writer-binding-changed', 1;
'@
        [void]$command.Parameters.Add('@id',[Data.SqlDbType]::UniqueIdentifier); $command.Parameters['@id'].Value=$Lease.Id
        [void]$command.Parameters.Add('@root',[Data.SqlDbType]::NVarChar,2048); $command.Parameters['@root'].Value=$Lease.Root
        [void]$command.Parameters.Add('@pid',[Data.SqlDbType]::Int); $command.Parameters['@pid'].Value=$Lease.ProcessId
        [void]$command.Parameters.Add('@started',[Data.SqlDbType]::DateTimeOffset); $command.Parameters['@started'].Value=$Lease.Started
        [void]$command.Parameters.Add('@machine',[Data.SqlDbType]::Char,64); $command.Parameters['@machine'].Value=$Lease.MachineFingerprint
        [void]$command.ExecuteNonQuery(); $Lease.Completed=$true
    } finally { $command.Dispose() }
}

function Close-RecoveryReferenceWrite {
    param([Parameter(Mandatory)]$Lease)
    if($Lease.Disposed) { return }
    $Lease.Disposed=$true; $Lease.Connection.Dispose()
}

Export-ModuleMember -Function Start-RecoveryReferenceWrite,Complete-RecoveryReferenceWrite,Close-RecoveryReferenceWrite
