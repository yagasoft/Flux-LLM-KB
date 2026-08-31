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
        [Parameter(Mandatory)][string]$Name)

    $definition = $Ast.Find({
        param($candidate)
        $candidate -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $candidate.Name -ceq $Name
    }, $true)
    if ($null -eq $definition) {
        throw "Closeout function is missing: $Name"
    }
    $captured = & ([scriptblock]::Create(
        $definition.Extent.Text + "`n(Get-Item -LiteralPath 'Function:$Name').ScriptBlock"))
    Set-Item -LiteralPath "Function:script:$Name" -Value $captured
}

function Assert-FailsWith {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessage,
        [Parameter(Mandatory)][string]$AssertionMessage)

    $failure = $null
    try {
        & $Action
    }
    catch {
        $failure = $_
    }
    Assert-True ($null -ne $failure -and $failure.Exception.Message -ceq $ExpectedMessage) $AssertionMessage
}

function Get-CurrentWindowsRuntimeMoniker {
    switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'X64' { return 'win-x64' }
        'X86' { return 'win-x86' }
        'Arm64' { return 'win-arm64' }
        default { throw 'The test host architecture is unsupported.' }
    }
}

function New-HangingSqlClientSeam {
    param([Parameter(Mandatory)][string]$Root)

    $project = Join-Path $Root 'HangingSqlClient.csproj'
    $program = Join-Path $Root 'HangingSqlClient.cs'
    [IO.File]::WriteAllText($project, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Microsoft.Data.SqlClient</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
'@)
    [IO.File]::WriteAllText($program, @'
namespace Microsoft.Data.SqlClient;

public sealed class SqlConnection : IDisposable
{
    public SqlConnection(string connectionString) { }
    public void Open()
    {
        File.WriteAllText(
            Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_TEST_SQL_CHILD_HANG_LOG")
                ?? throw new InvalidOperationException("hanging child log missing"),
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Thread.Sleep(TimeSpan.FromSeconds(30));
    }
    public SqlCommand CreateCommand() => new();
    public void Dispose() { }
}

public sealed class SqlCommand : IDisposable
{
    public int CommandTimeout { get; set; }
    public string CommandText { get; set; } = string.Empty;
    public int ExecuteNonQuery() => 1;
    public void Dispose() { }
}
'@)
    $output = Join-Path $Root 'hanging-sqlclient-out'
    & dotnet build $project -c Release -o $output --nologo | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Unable to build the hanging SqlClient seam.'
    return Join-Path $output 'Microsoft.Data.SqlClient.dll'
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$closeout = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
$sqlClientAssemblyPath = Join-Path $SourceRoot 'artifacts\bin\FluxKnowledge.Web\release\runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll'
Assert-True (Test-Path -LiteralPath $closeout -PathType Leaf) 'Closeout script is missing.'
Assert-True (Test-Path -LiteralPath $sqlClientAssemblyPath -PathType Leaf) 'The packaged Windows SqlClient provider is missing.'

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($closeout, [ref]$tokens, [ref]$errors)
Assert-True ($errors.Count -eq 0) 'Closeout script does not parse.'
foreach ($name in @(
    'Get-NativeGoLiveWindowsSqlClientNativeSniAsset',
    'New-NativeGoLiveSqlChildCommand',
    'Stop-FeatureProcessTree',
    'Invoke-NativeGoLiveSqlChild')) {
    Import-CloseoutFunction -Ast $ast -Name $name
}

$priorBootstrap = [Environment]::GetEnvironmentVariable(
    'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP', [EnvironmentVariableTarget]::Process)
$priorPath = [Environment]::GetEnvironmentVariable('PATH', [EnvironmentVariableTarget]::Process)
$priorHangLog = [Environment]::GetEnvironmentVariable(
    'FLUXKNOWLEDGE_TEST_SQL_CHILD_HANG_LOG', [EnvironmentVariableTarget]::Process)
try {
    [Environment]::SetEnvironmentVariable(
        'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP',
        'parent-bootstrap-sentinel',
        [EnvironmentVariableTarget]::Process)
    $publishedPayloadRoot = Join-Path $SourceRoot 'artifacts\bin\FluxKnowledge.Web\release'
    $sqlClientNativeSniAsset = Get-NativeGoLiveWindowsSqlClientNativeSniAsset -MergedMainRoot $publishedPayloadRoot
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "FluxKnowledgeSniResolution-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    try {
        $missingDirectoryPayload = Join-Path $temporaryRoot 'missing-native-directory'
        New-Item -ItemType Directory -Path $missingDirectoryPayload | Out-Null
        Assert-FailsWith -Action {
            Get-NativeGoLiveWindowsSqlClientNativeSniAsset -MergedMainRoot $missingDirectoryPayload | Out-Null
        } -ExpectedMessage 'native-go-live-windows-sql-client-native-missing' `
            -AssertionMessage 'A published payload without the current architecture native directory was accepted.'

        $missingAssetPayload = Join-Path $temporaryRoot 'missing-native-asset'
        $missingAssetDirectory = Join-Path $missingAssetPayload (
            Join-Path 'runtimes' (Join-Path (Get-CurrentWindowsRuntimeMoniker) 'native'))
        New-Item -ItemType Directory -Path $missingAssetDirectory -Force | Out-Null
        Assert-FailsWith -Action {
            Get-NativeGoLiveWindowsSqlClientNativeSniAsset -MergedMainRoot $missingAssetPayload | Out-Null
        } -ExpectedMessage 'native-go-live-windows-sql-client-native-missing' `
            -AssertionMessage 'A published architecture native directory without Microsoft.Data.SqlClient.SNI.dll was accepted.'

        $missingExactSniAsset = Join-Path $missingAssetDirectory 'Microsoft.Data.SqlClient.SNI.dll'
        [Environment]::SetEnvironmentVariable(
            'PATH',
            "$($sqlClientNativeSniAsset.Directory);$priorPath",
            [EnvironmentVariableTarget]::Process)
        Assert-FailsWith -Action {
            Invoke-NativeGoLiveSqlChild -Operation 'probe' `
                -ConnectionString 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive' `
                -BootstrapLogin 'unused-for-ambient-asset-probe' `
                -BootstrapScript (Join-Path $SourceRoot 'unused-for-ambient-asset-probe.sql') `
                -SqlClientAssemblyPath $sqlClientAssemblyPath `
                -PublishedPayloadRoot $missingAssetPayload `
                -SqlClientNativeRuntimeIdentifier $sqlClientNativeSniAsset.RuntimeIdentifier `
                -SqlClientNativeSniAssetPath $missingExactSniAsset `
                -SqlChildExecutable 'pwsh' `
                -ResetSql 'SELECT 1;'
        } -ExpectedMessage 'native-go-live-bootstrap-probe-failed' `
            -AssertionMessage 'An ambient SNI asset satisfied a missing exact published SNI path.'
        [Environment]::SetEnvironmentVariable('PATH', $priorPath, [EnvironmentVariableTarget]::Process)

        $wrongSniAssetPath = Join-Path $temporaryRoot 'ambient-Microsoft.Data.SqlClient.SNI.dll'
        Copy-Item -LiteralPath $sqlClientNativeSniAsset.Path -Destination $wrongSniAssetPath
        Assert-FailsWith -Action {
            Invoke-NativeGoLiveSqlChild -Operation 'probe' `
                -ConnectionString 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive' `
                -BootstrapLogin 'unused-for-wrong-asset-probe' `
                -BootstrapScript (Join-Path $SourceRoot 'unused-for-wrong-asset-probe.sql') `
                -SqlClientAssemblyPath $sqlClientAssemblyPath `
                -PublishedPayloadRoot $publishedPayloadRoot `
                -SqlClientNativeRuntimeIdentifier $sqlClientNativeSniAsset.RuntimeIdentifier `
                -SqlClientNativeSniAssetPath $wrongSniAssetPath `
                -SqlChildExecutable 'pwsh' `
                -ResetSql 'SELECT 1;'
        } -ExpectedMessage 'native-go-live-bootstrap-probe-failed' `
            -AssertionMessage 'An SNI asset outside the planned payload path was accepted.'

        $wrongRuntimeIdentifier = if ($sqlClientNativeSniAsset.RuntimeIdentifier -ceq 'win-x86') {
            'win-x64'
        } else {
            'win-x86'
        }
        Assert-FailsWith -Action {
            Invoke-NativeGoLiveSqlChild -Operation 'probe' `
                -ConnectionString 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive' `
                -BootstrapLogin 'unused-for-wrong-runtime-probe' `
                -BootstrapScript (Join-Path $SourceRoot 'unused-for-wrong-runtime-probe.sql') `
                -SqlClientAssemblyPath $sqlClientAssemblyPath `
                -PublishedPayloadRoot $publishedPayloadRoot `
                -SqlClientNativeRuntimeIdentifier $wrongRuntimeIdentifier `
                -SqlClientNativeSniAssetPath $sqlClientNativeSniAsset.Path `
                -SqlChildExecutable 'pwsh' `
                -ResetSql 'SELECT 1;'
        } -ExpectedMessage 'native-go-live-bootstrap-probe-failed' `
            -AssertionMessage 'A planned SNI runtime identifier that mismatched the child architecture was accepted.'

        $hangingSqlClientAssemblyPath = New-HangingSqlClientSeam -Root $temporaryRoot
        $timeoutHangLog = Join-Path $temporaryRoot 'timeout-child.txt'
        [Environment]::SetEnvironmentVariable(
            'FLUXKNOWLEDGE_TEST_SQL_CHILD_HANG_LOG', $timeoutHangLog, [EnvironmentVariableTarget]::Process)
        $timeoutStopwatch = [Diagnostics.Stopwatch]::StartNew()
        Assert-FailsWith -Action {
            Invoke-NativeGoLiveSqlChild -Operation 'probe' `
                -ConnectionString 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive' `
                -BootstrapLogin 'unused-for-timeout-probe' `
                -BootstrapScript (Join-Path $SourceRoot 'unused-for-timeout-probe.sql') `
                -SqlClientAssemblyPath $hangingSqlClientAssemblyPath `
                -PublishedPayloadRoot $publishedPayloadRoot `
                -SqlClientNativeRuntimeIdentifier $sqlClientNativeSniAsset.RuntimeIdentifier `
                -SqlClientNativeSniAssetPath $sqlClientNativeSniAsset.Path `
                -SqlChildExecutable 'pwsh' `
                -ResetSql 'SELECT 1;' `
                -TimeoutSeconds 1
        } -ExpectedMessage 'native-go-live-bootstrap-probe-timed-out' `
            -AssertionMessage 'A hung SQL child did not fail with the deterministic timeout reason.'
        $timeoutStopwatch.Stop()
        Assert-True ($timeoutStopwatch.Elapsed.TotalSeconds -lt 8) `
            'The SQL child timeout did not stop the fresh child promptly.'
        $timeoutChildProcessId = [int](Get-Content -LiteralPath $timeoutHangLog -Raw)
        Assert-True ($null -eq (Get-Process -Id $timeoutChildProcessId -ErrorAction SilentlyContinue)) `
            'The SQL child timeout returned before the actual child process was terminated.'

        $cancellationHangLog = Join-Path $temporaryRoot 'cancellation-child.txt'
        [Environment]::SetEnvironmentVariable(
            'FLUXKNOWLEDGE_TEST_SQL_CHILD_HANG_LOG', $cancellationHangLog, [EnvironmentVariableTarget]::Process)
        $cancellation = [Threading.CancellationTokenSource]::new()
        $cancellation.CancelAfter(1000)
        $cancellationStopwatch = [Diagnostics.Stopwatch]::StartNew()
        $cancellationFailure = $null
        try {
            Invoke-NativeGoLiveSqlChild -Operation 'probe' `
                -ConnectionString 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive' `
                -BootstrapLogin 'unused-for-cancellation-probe' `
                -BootstrapScript (Join-Path $SourceRoot 'unused-for-cancellation-probe.sql') `
                -SqlClientAssemblyPath $hangingSqlClientAssemblyPath `
                -PublishedPayloadRoot $publishedPayloadRoot `
                -SqlClientNativeRuntimeIdentifier $sqlClientNativeSniAsset.RuntimeIdentifier `
                -SqlClientNativeSniAssetPath $sqlClientNativeSniAsset.Path `
                -SqlChildExecutable 'pwsh' `
                -ResetSql 'SELECT 1;' `
                -TimeoutSeconds 30 `
                -CancellationToken $cancellation.Token
        }
        catch {
            $cancellationFailure = $_
        }
        finally {
            $cancellation.Dispose()
        }
        $cancellationStopwatch.Stop()
        Assert-True ($null -ne $cancellationFailure -and
            ($cancellationFailure.Exception -is [OperationCanceledException] -or
            $cancellationFailure.Exception.InnerException -is [OperationCanceledException])) `
            'A cancelled SQL child did not surface cancellation after termination.'
        Assert-True ($cancellationStopwatch.Elapsed.TotalSeconds -lt 8) `
            'SQL child cancellation did not stop the fresh child promptly.'
        $cancellationChildProcessId = [int](Get-Content -LiteralPath $cancellationHangLog -Raw)
        Assert-True ($null -eq (Get-Process -Id $cancellationChildProcessId -ErrorAction SilentlyContinue)) `
            'SQL child cancellation returned before the actual child process was terminated.'

        Invoke-NativeGoLiveSqlChild -Operation 'probe' `
            -ConnectionString 'Data Source=localhost;Initial Catalog=master;Integrated Security=True;Encrypt=True;Trust Server Certificate=True;Connect Timeout=5;Connect Retry Count=0;Pooling=False;Application Name=FluxKnowledge.NativeGoLive' `
            -BootstrapLogin 'unused-for-read-only-probe' `
            -BootstrapScript (Join-Path $SourceRoot 'unused-for-read-only-probe.sql') `
            -SqlClientAssemblyPath $sqlClientAssemblyPath `
            -PublishedPayloadRoot $publishedPayloadRoot `
            -SqlClientNativeRuntimeIdentifier $sqlClientNativeSniAsset.RuntimeIdentifier `
            -SqlClientNativeSniAssetPath $sqlClientNativeSniAsset.Path `
            -SqlChildExecutable 'pwsh' `
            -ResetSql 'SELECT 1;'
    }
    finally {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Assert-True (([Environment]::GetEnvironmentVariable(
        'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP', [EnvironmentVariableTarget]::Process)) -ceq
        'parent-bootstrap-sentinel') 'The read-only SQL probe altered the parent bootstrap environment.'
}
finally {
    [Environment]::SetEnvironmentVariable(
        'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP',
        $priorBootstrap,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable('PATH', $priorPath, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        'FLUXKNOWLEDGE_TEST_SQL_CHILD_HANG_LOG',
        $priorHangLog,
        [EnvironmentVariableTarget]::Process)
}

Write-Output 'Native SqlClient SNI resolution contract passed.'
