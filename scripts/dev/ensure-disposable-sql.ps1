[CmdletBinding()]
param(
    [string]$ServerConnectionString,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

function Test-LoopbackSqlConnectionString {
    param([Parameter(Mandatory = $true)][string]$ConnectionString)

    $rawBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $rawBuilder.set_ConnectionString($ConnectionString)
    }
    catch {
        throw 'Disposable SQL requires a valid SQL Server connection string.'
    }

    $dataSourceAliases = @()
    foreach ($key in $rawBuilder.Keys) {
        $normalised = $key.Replace(' ', '').ToLowerInvariant()
        if ($normalised -in @('initialcatalog', 'database', 'attachdbfilename', 'extendedproperties', 'initialfilename', 'userinstance') -or $normalised.Contains('attach')) {
            throw 'Disposable SQL requires a server-level connection without catalogue, attachment or user-instance fields.'
        }
        if ($normalised -in @('datasource', 'server', 'address', 'addr', 'networkaddress')) {
            $dataSourceAliases += $key
        }
    }

    if ($dataSourceAliases.Count -ne 1) {
        throw 'Disposable SQL requires exactly one data-source alias so SqlClient cannot redirect the selected server.'
    }

    try {
        $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    catch {
        throw 'Disposable SQL requires a valid SQL Server connection string.'
    }

    if (-not [string]::IsNullOrWhiteSpace($builder.FailoverPartner)) {
        throw 'Disposable SQL does not allow failover or alternate-server targets.'
    }
    if ([string]$builder.ApplicationIntent -ieq 'ReadOnly') {
        throw 'Disposable SQL does not allow read-only routing targets.'
    }
    if ($builder.MultiSubnetFailover) {
        throw 'Disposable SQL does not allow multi-subnet failover targets.'
    }

    $server = [string]$builder.DataSource
    if ([string]::IsNullOrWhiteSpace($server)) { throw 'Disposable SQL requires a server target.' }
    if ($server -ieq '(localdb)\MSSQLLocalDB') { return $builder.ConnectionString }
    $target = $server.Trim()
    if ($target.StartsWith('tcp:', [System.StringComparison]::OrdinalIgnoreCase)) {
        $target = $target.Substring(4)
    }
    if ($target.StartsWith('[')) {
        $closingBracket = $target.IndexOf(']')
        if ($closingBracket -le 1 -or ($closingBracket + 1 -lt $target.Length -and $target[$closingBracket + 1] -ne ',')) {
            throw 'Disposable SQL target must resolve exclusively to loopback.'
        }
        $hostName = $target.Substring(1, $closingBracket - 1)
    }
    else {
        $separator = $target.LastIndexOf(',')
        $hostName = if ($separator -ge 0) { $target.Substring(0, $separator) } else { $target }
    }
    $hostName = $hostName.Trim()
    if ([string]::IsNullOrWhiteSpace($hostName)) { throw 'Disposable SQL requires a server target.' }

    $address = $null
    if ([System.Net.IPAddress]::TryParse($hostName, [ref]$address)) {
        if ([System.Net.IPAddress]::IsLoopback($address)) { return $builder.ConnectionString }
        throw 'Disposable SQL target must resolve exclusively to loopback.'
    }
    if ($hostName -ieq 'localhost') { return $builder.ConnectionString }
    try { $addresses = [System.Net.Dns]::GetHostAddresses($hostName) } catch { throw 'Disposable SQL target must resolve exclusively to loopback.' }
    if ($addresses.Count -eq 0 -or @($addresses | Where-Object { -not [System.Net.IPAddress]::IsLoopback($_) }).Count -ne 0) {
        throw 'Disposable SQL target must resolve exclusively to loopback.'
    }

    return $builder.ConnectionString
}

function Start-SelectedLocalDb {
    param([Parameter(Mandatory = $true)][string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    $server = [string]$builder.DataSource

    if ($server -ine '(localdb)\MSSQLLocalDB') {
        return
    }

    $localDb = Get-Command 'sqllocaldb.exe' -ErrorAction Stop
    & $localDb.Source start MSSQLLocalDB | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Disposable SQL could not start the selected (localdb)\MSSQLLocalDB instance.'
    }
}

function Assert-SelectedServerIsReachable {
    param([Parameter(Mandatory = $true)][string]$ConnectionString)

    Add-Type -AssemblyName System.Data
    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);"
            if ($null -eq $command.ExecuteScalar()) {
                throw 'Disposable SQL target did not return server-level engine metadata.'
            }
        }
        finally {
            $command.Dispose()
        }
    }
    catch {
        throw "Disposable SQL could not validate the selected loopback server: $($_.Exception.Message)"
    }
    finally {
        $connection.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($ServerConnectionString)) {
    $ServerConnectionString = 'Server=localhost;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
}

$ServerConnectionString = Test-LoopbackSqlConnectionString -ConnectionString $ServerConnectionString
if ($ValidateOnly) {
    Write-Output $ServerConnectionString
    return
}
Start-SelectedLocalDb -ConnectionString $ServerConnectionString
Assert-SelectedServerIsReachable -ConnectionString $ServerConnectionString
Write-Output $ServerConnectionString
