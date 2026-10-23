Set-StrictMode -Version Latest

function Get-DashboardSnapshotConfigurationBytes([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while($current) {
        if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw 'Dashboard configuration reparse point.'}
        $current=[IO.Path]::GetDirectoryName($current)
    }
    $file=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($file.Length -le 0 -or $file.Length -gt 2097152){throw 'Dashboard configuration length invalid.'}
        $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes);return ,$bytes
    } finally {$file.Dispose()}
}
function Get-DashboardSnapshotConfigurationHash([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}
function New-DashboardSnapshotConfigurationChange {
    param([Parameter(Mandatory)][string]$Path)
    $before=Get-DashboardSnapshotConfigurationBytes $Path
    $configuration=[Text.Encoding]::UTF8.GetString($before).TrimStart([char]0xfeff)|ConvertFrom-Json -AsHashtable -DateKind String
    if($configuration -isnot [Collections.IDictionary]){throw 'Dashboard configuration must be an object.'}
    if(!$configuration.Contains('Dashboard')){$configuration.Dashboard=[ordered]@{}}
    if($configuration.Dashboard -isnot [Collections.IDictionary]){throw 'Dashboard must be an object.'}
    $mode=$configuration.Dashboard['ReadConsistency']
    if($mode -and $mode -cnotin @('LegacyFenced','Snapshot')){throw 'Unknown dashboard consistency mode.'}
    $configuration.Dashboard.ReadConsistency='Snapshot'
    $after=[Text.UTF8Encoding]::new($false).GetBytes(($configuration|ConvertTo-Json -Depth 100))
    [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Before=$before;After=$after;
        BeforeHash=(Get-DashboardSnapshotConfigurationHash $before);AfterHash=(Get-DashboardSnapshotConfigurationHash $after);
        Attempted=$false;RollbackVerified=$true}
}
function Write-DashboardSnapshotConfigurationBytes($Change,[byte[]]$Expected,[byte[]]$Desired) {
    if((Get-DashboardSnapshotConfigurationHash (Get-DashboardSnapshotConfigurationBytes $Change.Path)) -cne
        (Get-DashboardSnapshotConfigurationHash $Expected)){throw 'Dashboard configuration drift; retain hold.'}
    # Keep the file identity and ACL. Physical writers are stopped by the caller.
    $file=[IO.FileStream]::new($Change.Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
    try {
        if($file.Length -ne $Expected.Length){throw 'Dashboard configuration length drift.'}
        $held=[byte[]]::new($Expected.Length);$file.ReadExactly($held)
        if((Get-DashboardSnapshotConfigurationHash $held) -cne (Get-DashboardSnapshotConfigurationHash $Expected)){throw 'Dashboard configuration drift before write.'}
        $Change.Attempted=$true;$Change.RollbackVerified=$false
        $file.Position=0;$file.Write($Desired);$file.SetLength($Desired.Length);$file.Flush($true)
        $file.Position=0;$verified=[byte[]]::new($Desired.Length);$file.ReadExactly($verified)
        if((Get-DashboardSnapshotConfigurationHash $verified) -cne (Get-DashboardSnapshotConfigurationHash $Desired)){throw 'Dashboard configuration write unconfirmed.'}
    } finally {$file.Dispose()}
}
function Set-DashboardSnapshotConfigurationChange($Change) {
    Write-DashboardSnapshotConfigurationBytes $Change $Change.Before $Change.After
}
function Assert-DashboardSnapshotConfigurationChange($Change,[switch]$Original) {
    $expected=if($Original){$Change.BeforeHash}else{$Change.AfterHash}
    if((Get-DashboardSnapshotConfigurationHash (Get-DashboardSnapshotConfigurationBytes $Change.Path)) -cne $expected){throw 'Dashboard configuration differs from reviewed state.'}
}
function Restore-DashboardSnapshotConfigurationChange($Change) {
    if((Get-DashboardSnapshotConfigurationHash (Get-DashboardSnapshotConfigurationBytes $Change.Path)) -cne $Change.BeforeHash) {
        Write-DashboardSnapshotConfigurationBytes $Change $Change.After $Change.Before
    }
    Assert-DashboardSnapshotConfigurationChange $Change -Original
    $Change.RollbackVerified=$true
}

function Open-DashboardSnapshotConnection([string]$ConnectionString) {
    $text=$ConnectionString -replace '(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=' -replace '(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount='
    $builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($text)
    if(!$builder.DataSource -or !$builder.InitialCatalog -or $builder.InitialCatalog -iin @('master','model','msdb','tempdb')){throw 'dashboard-database-invalid'}
    $builder['Pooling']=$false;$builder['Enlist']=$false;$builder['ConnectRetryCount']=0;$builder['Connect Timeout']=5
    $builder['Application Name']='FluxKnowledge.DashboardSnapshotActivation'
    $connection=[Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try{$connection.Open();return $connection}catch{$connection.Dispose();throw}
}
function Read-DashboardSnapshotDatabaseState($Connection) {
    $command=$Connection.CreateCommand()
    try {
        $command.CommandTimeout=10
        $command.CommandText=@'
SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',d.name,N'|',CONVERT(nvarchar(36),r.database_guid)),
       d.name,d.snapshot_isolation_state,d.is_read_committed_snapshot_on,d.is_accelerated_database_recovery_on
FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id=d.database_id WHERE d.database_id=DB_ID();
'@
        $reader=$command.ExecuteReader()
        try {
            if(!$reader.Read() -or $reader.IsDBNull(0)){throw 'dashboard-database-identity-unavailable'}
            return @{Identity=$reader.GetString(0);Name=$reader.GetString(1);SnapshotState=[int]$reader.GetByte(2);
                RcsiEnabled=$reader.GetBoolean(3);AdrEnabled=$reader.GetBoolean(4)}
        } finally {$reader.Dispose()}
    } finally {$command.Dispose()}
}
function Get-DashboardSnapshotDatabaseState([Parameter(Mandatory)][string]$ConnectionString) {
    $connection=Open-DashboardSnapshotConnection $ConnectionString
    try{Read-DashboardSnapshotDatabaseState $connection}finally{$connection.Dispose()}
}
function Assert-DashboardSnapshotDatabaseBinding([hashtable]$Binding,[hashtable]$Observed,[switch]$RequireOn) {
    foreach($key in @('DatabaseIdentity','InitialSnapshotState','IntendedSnapshotState','RcsiEnabled','AdrEnabled')) {
        if(!$Binding.ContainsKey($key)){throw 'dashboard-option-binding-required'}
    }
    if(!$Binding.DatabaseIdentity -or $Binding.InitialSnapshotState -notin @(0,1) -or $Binding.IntendedSnapshotState -ne 1 -or
        !$Observed -or $Observed.Identity -cne $Binding.DatabaseIdentity -or
        $Observed.RcsiEnabled -ne $Binding.RcsiEnabled -or $Observed.AdrEnabled -ne $Binding.AdrEnabled){throw 'dashboard-option-binding-changed'}
    if($RequireOn) {
        if($Observed.SnapshotState -ne 1){throw 'dashboard-option-must-be-fully-on'}
    } elseif($Observed.SnapshotState -ne $Binding.InitialSnapshotState){throw 'dashboard-option-initial-state-changed'}
}
function Enable-DashboardSnapshotDatabaseOption([Parameter(Mandatory)][string]$ConnectionString,[hashtable]$Binding) {
    $connection=Open-DashboardSnapshotConnection $ConnectionString
    try {
        $observed=Read-DashboardSnapshotDatabaseState $connection
        Assert-DashboardSnapshotDatabaseBinding $Binding $observed
        if($observed.SnapshotState -eq 1){return}
        $quote=[Data.SqlClient.SqlCommandBuilder]::new()
        $command=$connection.CreateCommand()
        try {
            $command.CommandTimeout=30
            $command.CommandText='ALTER DATABASE '+$quote.QuoteIdentifier($observed.Name)+' SET ALLOW_SNAPSHOT_ISOLATION ON;'
            [void]$command.ExecuteNonQuery()
        } finally {$command.Dispose();$quote.Dispose()}
    } finally {$connection.Dispose()}
}
function Invoke-DashboardSnapshotActivation {
    param([hashtable]$State,[hashtable]$Binding,[scriptblock]$ReadState,[scriptblock]$Enable,[scriptblock]$WriteState)
    if($State.ContainsKey('DashboardSnapshotActivation')){throw 'dashboard-activation-reconciliation-required; never replay an intent'}
    $before=& $ReadState
    Assert-DashboardSnapshotDatabaseBinding $Binding $before
    $State.DashboardSnapshotActivation=@{IntentUtc=[DateTime]::UtcNow.ToString('O');Status='IntentRecorded';Before=$before;LastObserved=$null}
    & $WriteState
    try {
        if($before.SnapshotState -eq 0){& $Enable}
        $after=& $ReadState
        $State.DashboardSnapshotActivation.LastObserved=$after
        Assert-DashboardSnapshotDatabaseBinding $Binding $after -RequireOn
        $State.DashboardSnapshotActivation.Status='ConfirmedOn'
    } catch {
        $State.DashboardSnapshotActivation.Status='ReconciliationRequired'
        try{$State.DashboardSnapshotActivation.LastObserved=& $ReadState}
        catch{$State.DashboardSnapshotActivation.LastObserved=$null}
        throw
    } finally {
        # A cancelled ALTER can have taken effect. Keep the resulting state even
        # on failure, and never reverse the option automatically.
        & $WriteState
    }
}
function Assert-DashboardSnapshotActivation([hashtable]$State,[hashtable]$Binding,[scriptblock]$ReadState) {
    if(!$State.ContainsKey('DashboardSnapshotActivation') -or $State.DashboardSnapshotActivation.Status -cne 'ConfirmedOn') {
        throw 'dashboard-activation-reconciliation-required'
    }
    Assert-DashboardSnapshotDatabaseBinding $Binding (& $ReadState) -RequireOn
}
Export-ModuleMember -Function New-DashboardSnapshotConfigurationChange,Set-DashboardSnapshotConfigurationChange,Assert-DashboardSnapshotConfigurationChange,Restore-DashboardSnapshotConfigurationChange,Get-DashboardSnapshotDatabaseState,Assert-DashboardSnapshotDatabaseBinding,Enable-DashboardSnapshotDatabaseOption,Invoke-DashboardSnapshotActivation,Assert-DashboardSnapshotActivation
