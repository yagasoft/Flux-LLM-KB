Set-StrictMode -Version Latest

function Open-IndexEntryBinding {
    param(
        [Parameter(Mandatory)][ValidateSet('Web','OutlookHost','Cli')][string]$Role,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][scriptblock]$ReadPayloadFingerprint,
        [string[]]$ReviewedLegacyPayloads = @(),
        [string]$ExpectedSha256 = ''
    )
    $rootPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $entry = Join-Path $rootPath ("FluxKnowledge.$Role.dll")
    for ($path = $entry; $null -ne $path; $path = [IO.Path]::GetDirectoryName($path)) {
        if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw ('index-admission-entry-reparse-point: ' + $Role)
        }
    }
    $stream = [IO.File]::Open($entry, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if (-not ('FluxKnowledge.Deployment.IndexEntryMetadata' -as [type])) {
            Add-Type -Path (Join-Path $PSScriptRoot 'IndexEntryMetadata.cs') -ErrorAction Stop
        }
        $formats = @([FluxKnowledge.Deployment.IndexEntryMetadata]::Read($stream, "FluxKnowledge.$Role"))
        $fingerprint = & $ReadPayloadFingerprint $rootPath
        if ($fingerprint -cnotmatch '^[A-Fa-f0-9]{64}$' -or
            ($ExpectedSha256 -and $fingerprint -ine $ExpectedSha256)) {
            throw ('index-admission-payload-binding-changed: ' + $Role)
        }
        $legacy = $false
        if ($formats.Count -eq 0 -and $ReviewedLegacyPayloads -icontains ($Role + '=' + $fingerprint)) {
            $formats = @(1); $legacy = $true
        }
        return @{ Role=$Role; Root=$rootPath; Sha256=$fingerprint; Formats=$formats; ReviewedLegacyV1=$legacy; Stream=$stream }
    }
    catch { $stream.Dispose(); throw }
}

function Get-IndexEntryBinding {
    param(
        [Parameter(Mandatory)][ValidateSet('Web','OutlookHost','Cli')][string]$Role,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][scriptblock]$ReadPayloadFingerprint,
        [string[]]$ReviewedLegacyPayloads = @()
    )
    $binding = Open-IndexEntryBinding -Role $Role -Root $Root -ReadPayloadFingerprint $ReadPayloadFingerprint -ReviewedLegacyPayloads $ReviewedLegacyPayloads
    try { return @{ Role=$binding.Role; Root=$binding.Root; Sha256=$binding.Sha256; Formats=$binding.Formats; ReviewedLegacyV1=$binding.ReviewedLegacyV1 } }
    finally { $binding.Stream.Dispose() }
}

function Invoke-IndexFormatAdmission {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][hashtable[]]$Payloads,
        [Parameter(Mandatory)][scriptblock]$ReadPayloadFingerprint,
        [Parameter(Mandatory)][scriptblock]$Action,
        [string[]]$ReviewedLegacyPayloads = @(),
        [int[]]$OperationFormats = @(1,2),
        [ValidateSet('Legacy','Segmented')][string]$WebIndexMode = 'Legacy',
        [guid]$ExpectedActiveGenerationId=[guid]::Empty,
        [string]$ExpectedConfigurationPath='',
        [string]$ExpectedConfigurationSha256='',
        [string]$ExpectedDatabaseIdentity=''
    )
    if ($Payloads.Count -eq 0) { throw 'index-admission-payloads-required' }
    $held = [Collections.Generic.List[hashtable]]::new()
    $connection = $null; $transaction = $null; $configuration = $null
    try {
        if([string]::IsNullOrEmpty($ExpectedConfigurationPath) -ne [string]::IsNullOrEmpty($ExpectedConfigurationSha256)) {
            throw 'index-admission-configuration-binding-incomplete'
        }
        if($ExpectedConfigurationPath) {
            for($path=[IO.Path]::GetFullPath($ExpectedConfigurationPath);$path;$path=[IO.Path]::GetDirectoryName($path)) {
                if([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint){throw 'index-admission-configuration-path-unsafe'}
            }
            $configuration=[IO.File]::Open($ExpectedConfigurationPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            if($configuration.Length -gt 2MB -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($configuration)) -ine $ExpectedConfigurationSha256) {
                throw 'index-admission-configuration-changed'
            }
        }
        foreach ($payload in $Payloads) {
            $expected = if ($payload.ContainsKey('ExpectedSha256')) { $payload.ExpectedSha256 } else { '' }
            $held.Add((Open-IndexEntryBinding -Role $payload.Role -Root $payload.Root -ExpectedSha256 $expected `
                -ReadPayloadFingerprint $ReadPayloadFingerprint -ReviewedLegacyPayloads $ReviewedLegacyPayloads))
        }
        $normalised = [regex]::Replace($ConnectionString, '(?i)(^|;)\s*Trust Server Certificate\s*=', '$1TrustServerCertificate=')
        $normalised = [regex]::Replace($normalised, '(?i)(^|;)\s*Connect Retry Count\s*=', '$1ConnectRetryCount=')
        $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
        $builder['Connect Timeout']=2; $builder['ConnectRetryCount']=0; $builder['Pooling']=$false; $builder['Enlist']=$false
        if ([string]::IsNullOrWhiteSpace($builder.DataSource) -or [string]::IsNullOrWhiteSpace($builder.InitialCatalog) -or
            $builder.InitialCatalog -iin @('master','model','msdb','tempdb')) { throw 'index-admission-database-target-invalid' }
        $connection = [Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandTimeout=5
            if($ExpectedDatabaseIdentity) {
                $command.CommandText="SELECT CONCAT(CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')),N'|',DB_NAME(),N'|',CONVERT(nvarchar(36),database_guid)) FROM sys.database_recovery_status WHERE database_id=DB_ID();"
                if([string]$command.ExecuteScalar() -cne $ExpectedDatabaseIdentity){throw 'index-admission-database-incarnation-changed'}
            }
            $command.CommandText = @'
DECLARE @result int;
EXEC @result = sys.sp_getapplock @Resource=N'FluxKnowledge.DerivedIndexRecovery',
    @LockMode=N'Shared', @LockOwner=N'Session', @LockTimeout=2000;
SELECT @result;
'@
            if ([int]$command.ExecuteScalar() -lt 0) { throw 'index-admission-recovery-busy' }
            $transaction = $connection.BeginTransaction([Data.IsolationLevel]::Serializable)
            $command.Transaction=$transaction
            # This is the existing publication row fence, not a new lock resource.
            $command.CommandText = @'
SET LOCK_TIMEOUT 2000;
IF COL_LENGTH(N'dbo.IndexState',N'RequiredIndexFormat') IS NULL
    SELECT CONVERT(int,1) FROM [dbo].[IndexState] WITH(UPDLOCK,HOLDLOCK) WHERE [Id]=1;
ELSE
    EXEC sys.sp_executesql N'SELECT [RequiredIndexFormat] FROM [dbo].[IndexState] WITH(UPDLOCK,HOLDLOCK) WHERE [Id]=1;';
'@
            $required = $command.ExecuteScalar()
            if ($required -isnot [int] -or $required -notin @(1,2)) { throw 'index-admission-required-format-unknown' }
            if ($OperationFormats -notcontains $required) { throw 'index-admission-operation-incompatible' }
            if($ExpectedActiveGenerationId -ne [guid]::Empty) {
                $command.CommandText='SELECT COUNT(*) FROM dbo.IndexState s JOIN dbo.IndexGenerations g ON g.Id=s.ActiveIndexGenerationId WHERE s.Id=1 AND g.Id=@generation AND g.FormatVersion=@format AND g.ValidatedAtUtc IS NOT NULL AND g.RetiredAtUtc IS NULL AND g.ReclaimedAtUtc IS NULL;'
                [void]$command.Parameters.Add('@generation',[Data.SqlDbType]::UniqueIdentifier);$command.Parameters['@generation'].Value=$ExpectedActiveGenerationId
                [void]$command.Parameters.Add('@format',[Data.SqlDbType]::Int);$command.Parameters['@format'].Value=$required
                if([int]$command.ExecuteScalar() -ne 1){throw 'index-admission-generation-changed'}
            }
            foreach ($binding in $held) {
                if ($binding.Formats -notcontains $required) { throw ('index-admission-payload-incompatible: ' + $binding.Role + '; required=' + $required) }
                if ($required -eq 2 -and $binding.Role -ceq 'Web' -and $WebIndexMode -cne 'Segmented') {
                    throw 'index-admission-segmented-publication-disabled'
                }
            }
            # Only an immediate start/enable/hold mutation belongs here. No readiness waits.
            & $Action
            $transaction.Commit()
        }
        finally { $command.Dispose() }
    }
    finally {
        if ($transaction) { $transaction.Dispose() }
        if ($connection) { $connection.Dispose() }
        foreach ($binding in $held) { $binding.Stream.Dispose() }
        if ($configuration) { $configuration.Dispose() }
    }
}

Export-ModuleMember -Function Get-IndexEntryBinding, Invoke-IndexFormatAdmission
