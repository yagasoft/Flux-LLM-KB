[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = 'Stop'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Import-CloseoutFunction {
    param(
        [Parameter(Mandatory)][System.Management.Automation.Language.Ast]$Ast,
        [Parameter(Mandatory)][string]$Name,
        [switch]$Optional)

    $definition = $Ast.Find({
        param($candidate)
        $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $candidate.Name -ceq $Name
    }, $true)
    if ($null -eq $definition) {
        if ($Optional) { return }
        throw "Closeout function is missing: $Name"
    }
    $captured = & ([scriptblock]::Create(
        $definition.Extent.Text + "`n(Get-Item -LiteralPath 'Function:$Name').ScriptBlock"))
    Set-Item -LiteralPath "Function:script:$Name" -Value $captured
}

function New-RecordingSqlClientSeam {
    param([Parameter(Mandatory)][string]$Root)

    $project = Join-Path $Root 'RecordingSqlClient.csproj'
    $program = Join-Path $Root 'RecordingSqlClient.cs'
    [IO.File]::WriteAllText($project, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Microsoft.Data.SqlClient</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
'@)
    [IO.File]::WriteAllText($program, @'
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Data.SqlClient;

public sealed class SqlConnection : IDisposable
{
    private readonly string _connectionString;

    public SqlConnection(string connectionString)
    {
        _connectionString = connectionString;
        Record(new
        {
            kind = "connection",
            processId = Environment.ProcessId,
            bootstrapEnvironmentStripped = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP")),
            connectionReceived = !string.IsNullOrWhiteSpace(connectionString),
            connectionOnCommandLine = Environment.GetCommandLineArgs().Any(argument => argument.Contains(connectionString, StringComparison.Ordinal)),
            canonicalConnection = IsCanonicalConnection(connectionString)
        });
    }

    public void Open() => Console.WriteLine(_connectionString);

    public SqlCommand CreateCommand() => new(_connectionString);

    public void Dispose() { }

    internal static void Record(object value)
    {
        var log = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SQL_CHILD_LOG")
            ?? throw new InvalidOperationException("recording log missing");
        File.AppendAllText(log, JsonSerializer.Serialize(value) + Environment.NewLine);
    }

    private static bool IsCanonicalConnection(string connectionString)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Data Source"] = "localhost",
            ["Initial Catalog"] = "master",
            ["Integrated Security"] = "True",
            ["Encrypt"] = "True",
            ["Trust Server Certificate"] = "True",
            ["Connect Timeout"] = "5",
            ["Connect Retry Count"] = "0",
            ["Pooling"] = "False",
            ["Application Name"] = "FluxKnowledge.NativeGoLive"
        };
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return builder.Keys.Count == expected.Count && expected.All(pair =>
            builder.ContainsKey(pair.Key) && string.Equals(builder[pair.Key]?.ToString(), pair.Value, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SqlCommand : IDisposable
{
    private static readonly string[] ExpectedResetObjects =
    [
        "FluxKnowledgeNativeGoLiveCreate", "FluxKnowledgeNativeGoLiveDrop",
        "FluxKnowledgeNativeGoLiveManageAppPool", "FluxKnowledgeNativeGoLiveObserveAppPool",
        "FluxKnowledgeNativeGoLiveCertificateLogin", "FluxKnowledgeNativeGoLiveCertificate"
    ];
    private readonly string _connectionString;

    internal SqlCommand(string connectionString) => _connectionString = connectionString;

    public int CommandTimeout { get; set; }
    public string CommandText { get; set; } = string.Empty;

    public int ExecuteNonQuery()
    {
        var resetObjects = Regex.Matches(
                CommandText,
                @"DROP\s+(?:PROCEDURE|LOGIN|CERTIFICATE)\s+(?:dbo\.)?([A-Za-z0-9_]+)",
                RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var resetBatch = resetObjects.Length > 0;
        var initialBootstrapBatch = CommandText.Contains(
            "-- Reviewed SQL Server bootstrap authority for the native go-live lifecycle.",
            StringComparison.Ordinal);
        var validTsql =
            !Regex.IsMatch(CommandText, @"(?m)^\s*:") &&
            !Regex.IsMatch(CommandText, @"(?im)^\s*GO\s*(?:\r?\n|$)") &&
            !CommandText.Contains("$(NativeGoLiveBootstrapLogin)", StringComparison.Ordinal);
        SqlConnection.Record(new
        {
            kind = "command",
            processId = Environment.ProcessId,
            validTsql,
            resetBatch,
            namedResetOnly = !resetBatch || resetObjects.SequenceEqual(ExpectedResetObjects.OrderBy(value => value, StringComparer.Ordinal)),
            initialBootstrapBatch,
            canonicalConnection = _connectionString.Length > 0
        });
        Console.WriteLine(_connectionString);
        if (initialBootstrapBatch && string.Equals(
                Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SQLCLIENT_FAIL_OPERATION"),
                "install",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("disposable install failure");
        }
        return 1;
    }

    public void Dispose() { }
}
'@)
    $output = Join-Path $Root 'recording-sqlclient-out'
    & dotnet build $project -c Release -o $output --nologo | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Unable to build the recording SqlClient seam.'
    return Join-Path $output 'Microsoft.Data.SqlClient.dll'
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeout = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$bootstrapScript = Join-Path $SourceRoot 'scripts\deploy\native-go-live-bootstrap.sql'
Assert-True (Test-Path -LiteralPath $closeout -PathType Leaf) 'Closeout script is missing.'
Assert-True (Test-Path -LiteralPath $bootstrapScript -PathType Leaf) 'Bootstrap script is missing.'

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($closeout, [ref]$tokens, [ref]$errors)
Assert-True ($errors.Count -eq 0) 'Closeout script does not parse.'
foreach ($name in @(
    'Complete-FeatureStepRecord',
    'Assert-NativeGoLiveBootstrapEnvironment',
    'Assert-NativeGoLiveBootstrapConnection',
    'Get-NativeGoLiveWindowsSqlClientAssemblyPath',
    'Get-NativeGoLiveWindowsSqlClientNativeSniAsset',
    'Import-NativeGoLiveWindowsSqlClientAssembly',
    'New-RequiredReflectionInstance',
    'Invoke-NativeGoLiveBootstrap')) {
    Import-CloseoutFunction -Ast $ast -Name $name
}
foreach ($name in @('New-NativeGoLiveSqlChildCommand', 'Invoke-NativeGoLiveSqlChild')) {
    Import-CloseoutFunction -Ast $ast -Name $name
}

Add-Type @'
public sealed class NativeGoLiveConstructorSelectionProbe
{
    public NativeGoLiveConstructorSelectionProbe(string first, string second) { Selected = "exact"; }
    public NativeGoLiveConstructorSelectionProbe(object first, System.Func<string> second) { Selected = "wrong"; }
    public string Selected { get; }
}
'@
$constructorProbe = New-RequiredReflectionInstance `
    -Type ([NativeGoLiveConstructorSelectionProbe]) -Arguments @('first', 'second')
Assert-True ($constructorProbe.Selected -ceq 'exact') `
    'The closeout reflection boundary did not select the exact constructor signature.'

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeBootstrapChild-$([Guid]::NewGuid().ToString('N'))"
$priorBootstrap = [Environment]::GetEnvironmentVariable(
    'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP', [EnvironmentVariableTarget]::Process)
$priorChildLog = [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQL_CHILD_LOG', [EnvironmentVariableTarget]::Process)
$priorFailure = [Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQLCLIENT_FAIL_OPERATION', [EnvironmentVariableTarget]::Process)
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $portableProvider = Join-Path $SourceRoot 'artifacts\bin\FluxKnowledge.Web\release\Microsoft.Data.SqlClient.dll'
    $windowsProvider = Join-Path $SourceRoot 'artifacts\bin\FluxKnowledge.Web\release\runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll'
    Assert-True ((Test-Path -LiteralPath $portableProvider -PathType Leaf) -and
        (Test-Path -LiteralPath $windowsProvider -PathType Leaf)) `
        'The release output does not contain the packaged portable and Windows SqlClient provider assets.'

    $rootOnlyLayout = Join-Path $temporaryRoot 'root-only-payload'
    New-Item -ItemType Directory -Path $rootOnlyLayout | Out-Null
    Copy-Item -LiteralPath $portableProvider -Destination (Join-Path $rootOnlyLayout 'Microsoft.Data.SqlClient.dll')
    $rootOnlyFailure = $null
    try {
        Get-NativeGoLiveWindowsSqlClientAssemblyPath -MergedMainRoot $rootOnlyLayout | Out-Null
    } catch {
        $rootOnlyFailure = $_
    }
    Assert-True ($null -ne $rootOnlyFailure -and
        $rootOnlyFailure.Exception.Message -ceq 'native-go-live-windows-sql-client-missing') `
        'The portable publish-root SqlClient provider was accepted without the exact Windows runtime asset.'

    $windowsLayout = Join-Path $SourceRoot 'artifacts\bin\FluxKnowledge.Web\release'
    $windowsAsset = $windowsProvider
    $nativeSniAsset = Get-NativeGoLiveWindowsSqlClientNativeSniAsset -MergedMainRoot $windowsLayout
    $selectedProvider = Get-NativeGoLiveWindowsSqlClientAssemblyPath -MergedMainRoot $windowsLayout
    Assert-True ([string]::Equals(
        [IO.Path]::GetFullPath($selectedProvider),
        [IO.Path]::GetFullPath($windowsAsset),
        [StringComparison]::OrdinalIgnoreCase)) `
        'The packaged-layout selection did not choose the explicit Windows runtime SqlClient asset.'
    $loadedProvider = Import-NativeGoLiveWindowsSqlClientAssembly -SqlClientAssemblyPath $selectedProvider
    Assert-True ([string]::Equals(
        [IO.Path]::GetFullPath($loadedProvider.Location),
        [IO.Path]::GetFullPath($windowsAsset),
        [StringComparison]::OrdinalIgnoreCase)) `
        'The selected Windows SqlClient runtime asset did not load from its packaged location.'

    $invalidLayout = Join-Path $temporaryRoot 'invalid-windows-provider-payload'
    $invalidAssetDirectory = Join-Path $invalidLayout 'runtimes\win\lib\net9.0'
    New-Item -ItemType Directory -Path $invalidAssetDirectory -Force | Out-Null
    $invalidAsset = Join-Path $invalidAssetDirectory 'Microsoft.Data.SqlClient.dll'
    [IO.File]::WriteAllBytes($invalidAsset, [byte[]](0, 1, 2, 3))
    $invalidLoadFailure = $null
    try {
        Import-NativeGoLiveWindowsSqlClientAssembly -SqlClientAssemblyPath (
            Get-NativeGoLiveWindowsSqlClientAssemblyPath -MergedMainRoot $invalidLayout) | Out-Null
    } catch {
        $invalidLoadFailure = $_
    }
    Assert-True ($null -ne $invalidLoadFailure -and
        $invalidLoadFailure.Exception.Message -ceq 'native-go-live-windows-sql-client-load-failed') `
        'An unloadable packaged Windows SqlClient runtime asset did not fail closed.'

    $sqlClientSeam = New-RecordingSqlClientSeam -Root $temporaryRoot
    $recordPath = Join-Path $temporaryRoot 'children.jsonl'
    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQL_CHILD_LOG', $recordPath, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP', 'bootstrap-environment-sentinel', [EnvironmentVariableTarget]::Process)
    $script:DryRun = $false
    $script:MainRoot = $SourceRoot
    $script:Steps = @()
    $script:FailedStep = $null
    $connection = 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive'

    $output = & {
        Invoke-NativeGoLiveBootstrap -BootstrapScript $bootstrapScript -ConnectionString $connection `
            -BootstrapLogin 'disposable-bootstrap-login' -SqlClientAssemblyPath $sqlClientSeam `
            -PublishedPayloadRoot $windowsLayout `
            -SqlClientNativeRuntimeIdentifier $nativeSniAsset.RuntimeIdentifier `
            -SqlClientNativeSniAssetPath $nativeSniAsset.Path
    } *>&1 | Out-String
    Assert-True (-not $output.Contains($connection, [StringComparison]::Ordinal)) `
        'The SQL child output exposed bootstrap connection material.'
    $records = @(Get-Content -LiteralPath $recordPath | ForEach-Object { $_ | ConvertFrom-Json })
    $connections = @($records | Where-Object { $_.kind -ceq 'connection' })
    $commands = @($records | Where-Object { $_.kind -ceq 'command' })
    Assert-True ($connections.Count -eq 2 -and $connections[0].processId -ne $connections[1].processId) `
        'The bootstrap did not execute reset and install in two distinct generated SQL children.'
    Assert-True (@($connections | Where-Object {
        -not $_.bootstrapEnvironmentStripped -or -not $_.connectionReceived -or
        $_.connectionOnCommandLine -or -not $_.canonicalConnection
    }).Count -eq 0) 'The generated SQL children did not receive the exact connection safely.'
    $resetBatches = @($commands | Where-Object { $_.processId -eq $connections[0].processId })
    $installBatches = @($commands | Where-Object { $_.processId -eq $connections[1].processId })
    Assert-True ($resetBatches.Count -eq 1 -and $resetBatches[0].resetBatch -and
        $resetBatches[0].namedResetOnly -and $resetBatches[0].validTsql) `
        'The generated reset child did not retain its exact named-object T-SQL limit.'
    Assert-True ($installBatches.Count -gt 1 -and $installBatches[0].initialBootstrapBatch -and
        $installBatches[0].validTsql -and @($installBatches | Where-Object { -not $_.validTsql }).Count -eq 0) `
        'The generated install child submitted a non-T-SQL first or subsequent bootstrap batch.'

    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQLCLIENT_FAIL_OPERATION', 'install', [EnvironmentVariableTarget]::Process)
    $script:Steps = @()
    $script:FailedStep = $null
    $failure = $null
    try {
        Invoke-NativeGoLiveBootstrap -BootstrapScript $bootstrapScript -ConnectionString $connection `
            -BootstrapLogin 'disposable-bootstrap-login' -SqlClientAssemblyPath $sqlClientSeam `
            -PublishedPayloadRoot $windowsLayout `
            -SqlClientNativeRuntimeIdentifier $nativeSniAsset.RuntimeIdentifier `
            -SqlClientNativeSniAssetPath $nativeSniAsset.Path
    } catch {
        $failure = $_
    }
    Assert-True ($null -ne $failure -and $failure.Exception.Message -ceq 'native-go-live-bootstrap-install-failed') `
        'SQL child failure did not propagate as the safe install failure.'
    Assert-True ($script:FailedStep -ceq 'native-go-live-bootstrap') 'Bootstrap failure did not identify the failed closeout step.'
    $allRecords = @(Get-Content -LiteralPath $recordPath | ForEach-Object { $_ | ConvertFrom-Json })
    Assert-True (@($allRecords | Where-Object { $_.kind -ceq 'connection' }).Count -eq 4) `
        'The failing non-dry bootstrap did not invoke reset then install generated SQL children.'

    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQLCLIENT_FAIL_OPERATION', $null, [EnvironmentVariableTarget]::Process)
    foreach ($unsupportedMetaCommand in @(':r unsupported.sql', 'GO 2', '!! unsupported-shell-command')) {
        $unsupportedBootstrap = Join-Path $temporaryRoot "unsupported-native-go-live-bootstrap-$($unsupportedMetaCommand.GetHashCode()).sql"
        [IO.File]::WriteAllText($unsupportedBootstrap, (Get-Content -LiteralPath $bootstrapScript -Raw) + "`n$unsupportedMetaCommand`n")
        $script:Steps = @()
        $script:FailedStep = $null
        $unsupportedFailure = $null
        try {
            Invoke-NativeGoLiveBootstrap -BootstrapScript $unsupportedBootstrap -ConnectionString $connection `
                -BootstrapLogin 'disposable-bootstrap-login' -SqlClientAssemblyPath $sqlClientSeam `
                -PublishedPayloadRoot $windowsLayout `
                -SqlClientNativeRuntimeIdentifier $nativeSniAsset.RuntimeIdentifier `
                -SqlClientNativeSniAssetPath $nativeSniAsset.Path
        } catch {
            $unsupportedFailure = $_
        }
        Assert-True ($null -ne $unsupportedFailure -and
            $unsupportedFailure.Exception.Message -ceq 'native-go-live-bootstrap-install-failed') `
            'An unsupported sqlcmd meta-command did not fail the install child closed.'
        Assert-True ($script:FailedStep -ceq 'native-go-live-bootstrap') `
            'Unsupported sqlcmd meta-command failure did not identify the bootstrap step.'
    }
} finally {
    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP', $priorBootstrap, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQL_CHILD_LOG', $priorChildLog, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable('FLUXKNOWLEDGE_TEST_SQLCLIENT_FAIL_OPERATION', $priorFailure, [EnvironmentVariableTarget]::Process)
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output 'Native closeout non-dry bootstrap contract passed.'
