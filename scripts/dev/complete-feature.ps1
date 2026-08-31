param(
    [string]$FeatureWorktree = (Get-Location).Path,
    [string]$MainRoot = "",
    [string]$CommitMessage = "Complete native feature",
    [switch]$DryRun,
    [switch]$KeepWorktree,
    [switch]$GoLive,
    [switch]$ConfirmCleanSlate,
    [switch]$ConfirmConfigureVss,
    [switch]$ConfirmDestroySql,
    [switch]$ConfirmRegisterCodex,
    [int]$StepTimeoutSeconds = 600,
    [int]$TestStepTimeoutSeconds = 1800
)

$ErrorActionPreference = "Stop"
$nativeGoLiveBootstrapEnvironmentName = "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP"

function Get-MainWorktreePath {
    param([string]$Worktree)

    $lines = git -C $Worktree worktree list --porcelain
    $currentPath = $null
    foreach ($line in $lines) {
        if ($line -like "worktree *") {
            $currentPath = $line.Substring("worktree ".Length)
        } elseif ($line -eq "branch refs/heads/main" -and $currentPath) {
            return $currentPath
        }
    }

    throw "Unable to locate main worktree from git worktree list."
}

function New-StepLogPath {
    param([string]$Name)

    $safeName = ($Name -replace "[^A-Za-z0-9_.-]", "-").Trim("-")
    return Join-Path $script:LogRoot ("{0:yyyyMMdd-HHmmss}-{1}.log" -f [DateTime]::UtcNow, $safeName)
}

function Stop-FeatureProcessTree {
    param([int]$ProcessId)

    if ($null -eq (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        return
    }
    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction Stop)
    foreach ($child in $children) {
        Stop-FeatureProcessTree -ProcessId ([int]$child.ProcessId)
    }
    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
    }
    catch {
        if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            throw
        }
    }
    if ($null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw "Failed to stop process tree member $ProcessId."
    }
}

function ConvertTo-FeatureCommandArgument {
    param([string]$Value)

    if ($Value -match '[\s"]') {
        return '"' + ($Value -replace '"', '\"') + '"'
    }

    return $Value
}

function Get-FeatureTaskResult {
    param($Task)

    if ($null -eq $Task) {
        return ""
    }

    if ($Task.Wait(5000)) {
        return $Task.Result
    }

    return "[log stream did not close within 5 seconds]"
}

function Write-FeatureStepOutput {
    param(
        [string]$LogPath,
        [string]$Stdout,
        [string]$Stderr
    )

    "" | Out-File -FilePath $LogPath -Encoding utf8
    if ($Stdout) {
        $Stdout | Out-File -FilePath $LogPath -Append -Encoding utf8
    }

    if ($Stderr) {
        "[stderr]" | Out-File -FilePath $LogPath -Append -Encoding utf8
        $Stderr | Out-File -FilePath $LogPath -Append -Encoding utf8
    }
}

function New-FeatureStepScript {
    param([string]$Command)

return @"
`$ErrorActionPreference = "Stop"
if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable(
        "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP",
        [EnvironmentVariableTarget]::Process))) {
    throw "The native SQL bootstrap must not be visible to a closeout child process."
}
try {
$Command
    if (`$global:LASTEXITCODE -is [int] -and `$global:LASTEXITCODE -ne 0) {
        exit `$global:LASTEXITCODE
    }
    exit 0
} catch {
    Write-Error `$_
    exit 1
}
"@
}

function Complete-FeatureStepRecord {
    param(
        [System.Collections.IDictionary]$Record,
        [DateTime]$StartedAt
    )

    if ($Record["finished_at"]) {
        return
    }

    $Record["finished_at"] = [DateTime]::UtcNow.ToString("o")
    $finishedAt = [DateTime]::Parse(
        $Record["finished_at"],
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    $Record["duration_seconds"] = [Math]::Round(($finishedAt - $StartedAt).TotalSeconds, 3)
}

function Write-SummaryAndExit {
    param([int]$ExitCode)

    [ordered]@{
        ok = ($ExitCode -eq 0)
        failed_step = $script:FailedStep
        log_root = $script:LogRoot
        steps = $script:Steps
    } | ConvertTo-Json -Depth 8

    exit $ExitCode
}

function Invoke-FeatureStep {
    param(
        [string]$Name,
        [string]$Command,
        [string]$Cwd,
        [int]$TimeoutSeconds = 0,
        [string]$FailureHint = "",
        [hashtable]$Environment = @{},
        [switch]$RunInDryRun
    )

    $logPath = New-StepLogPath -Name $Name
    $startedAt = [DateTime]::UtcNow
    $record = [ordered]@{
        name = $Name
        cwd = $Cwd
        command = $Command
        started_at = $startedAt.ToString("o")
        finished_at = $null
        duration_seconds = $null
        exit_code = 0
        log_path = $logPath
        skipped = [bool]($DryRun -and -not $RunInDryRun)
    }
    $script:Steps += $record

    if ($DryRun -and -not $RunInDryRun) {
        "DRY RUN: $Command" | Out-File -FilePath $logPath -Encoding utf8
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        return
    }

    $stdoutText = ""
    $stderrText = ""
    $stdoutTask = $null
    $stderrTask = $null
    $effectiveTimeoutSeconds = if ($TimeoutSeconds -gt 0) { $TimeoutSeconds } else { $StepTimeoutSeconds }
    try {
        $stepScript = New-FeatureStepScript -Command $Command
        $encodedCommand = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($stepScript))
        $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $processInfo.FileName = "powershell"
        $processInfo.Arguments = (@("-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encodedCommand) |
                ForEach-Object { ConvertTo-FeatureCommandArgument $_ }) -join " "
        $processInfo.WorkingDirectory = $Cwd
        $processInfo.UseShellExecute = $false
        $processInfo.CreateNoWindow = $true
        $processInfo.RedirectStandardOutput = $true
        $processInfo.RedirectStandardError = $true
        $bootstrapEnvironmentName = "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP"
        if ($Environment.ContainsKey($bootstrapEnvironmentName)) {
            throw "The native SQL bootstrap cannot be forwarded to a child process."
        }
        [void]$processInfo.EnvironmentVariables.Remove($bootstrapEnvironmentName)
        foreach ($entry in $Environment.GetEnumerator()) {
            $processInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $processInfo
        [void]$process.Start()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($effectiveTimeoutSeconds * 1000)) {
            $record.exit_code = 124
            Stop-FeatureProcessTree -ProcessId $process.Id
            $process.WaitForExit(5000) | Out-Null
            $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
            $stderrText = Get-FeatureTaskResult -Task $stderrTask
            Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
            "Step '$Name' timed out after $effectiveTimeoutSeconds seconds; process tree was stopped." |
                Out-File -FilePath $logPath -Append -Encoding utf8
            Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
            throw "Step '$Name' timed out after $effectiveTimeoutSeconds seconds. See $logPath"
        }

        $process.WaitForExit()
        $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
        $stderrText = Get-FeatureTaskResult -Task $stderrTask
        Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
        $record.exit_code = $process.ExitCode
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        if ($process.ExitCode -ne 0) {
            throw "Step '$Name' failed with exit code $($process.ExitCode). See $logPath"
        }
    } catch {
        $stdoutText = Get-FeatureTaskResult -Task $stdoutTask
        $stderrText = Get-FeatureTaskResult -Task $stderrTask
        Write-FeatureStepOutput -LogPath $logPath -Stdout $stdoutText -Stderr $stderrText
        if ($record.exit_code -eq 0) {
            $record.exit_code = 1
        }
        Complete-FeatureStepRecord -Record $record -StartedAt $startedAt
        $script:FailedStep = $Name
        $errorText = $_.ToString()
        $errorText | Out-File -FilePath $logPath -Append -Encoding utf8
        if ($FailureHint) {
            $FailureHint | Out-File -FilePath $logPath -Append -Encoding utf8
            throw "$errorText`n$FailureHint"
        }

        throw
    }
}

$CleanupWorktreeCommand = @'
$MainRoot = $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT
$FeatureWorktree = $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE
$Branch = $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH

function Normalize-CleanupPath {
    param([string]$Path)

    if (-not $Path) {
        return ""
    }

    try {
        $fullPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    } catch {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
    }

    return $fullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar).Replace(
            [System.IO.Path]::AltDirectorySeparatorChar,
            [System.IO.Path]::DirectorySeparatorChar)
}

function Test-WorktreeRegistered {
    param([string]$Worktree)

    $target = Normalize-CleanupPath -Path $Worktree
    foreach ($line in (git worktree list --porcelain)) {
        if ($line -like "worktree *") {
            $current = Normalize-CleanupPath -Path $line.Substring("worktree ".Length)
            if ($current.Equals($target, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
    }

    return $false
}

function Test-DirectoryEmpty {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    return -not [bool](Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop | Select-Object -First 1)
}

Set-Location $MainRoot
git worktree remove "$FeatureWorktree"
$removeExit = $LASTEXITCODE
if ($removeExit -ne 0) {
    if ((-not (Test-WorktreeRegistered -Worktree $FeatureWorktree)) -and
        (Test-DirectoryEmpty -Path $FeatureWorktree)) {
        "git worktree remove left an empty directory; continuing cleanup."
    } else {
        exit $removeExit
    }
}

git worktree prune
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

git branch -D $Branch
exit $LASTEXITCODE
'@

function Assert-NativeGoLiveAcknowledgements {
    $confirmations = @(
        [bool]$ConfirmCleanSlate,
        [bool]$ConfirmConfigureVss,
        [bool]$ConfirmDestroySql,
        [bool]$ConfirmRegisterCodex)
    $confirmedCount = @($confirmations | Where-Object { $_ }).Count
    if ($GoLive -and $confirmedCount -ne $confirmations.Count) {
        throw "-GoLive requires -ConfirmCleanSlate, -ConfirmConfigureVss, -ConfirmDestroySql and -ConfirmRegisterCodex."
    }
    if (-not $GoLive -and $confirmedCount -ne 0) {
        throw "Native go-live acknowledgement switches require -GoLive."
    }
}

function New-DirectNativeGoLiveStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Command,
        [switch]$Skipped)

    $record = [ordered]@{
        name = $Name
        cwd = $MainRoot
        command = $Command
        started_at = [DateTime]::UtcNow.ToString("o")
        finished_at = $null
        duration_seconds = $null
        exit_code = 0
        log_path = $null
        skipped = [bool]$Skipped
    }
    $script:Steps += $record
    return $record
}

function Add-DirectNativeGoLiveStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Command,
        [switch]$Skipped)

    $record = New-DirectNativeGoLiveStep -Name $Name -Command $Command -Skipped:$Skipped
    Complete-FeatureStepRecord -Record $record -StartedAt ([DateTime]::Parse(
        $record.started_at,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind))
}

function Record-NativeGoLiveFailure {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Record,
        [Parameter(Mandatory)][Exception]$Exception)

    $script:FailedStep = 'native-go-live'
    if ($Exception.Message -cmatch "\A(?:Native go-live failed with safe reason code '(go-live-(?:acknowledgement-required|cancelled-before-admission|lease-unavailable|closeout-capability-(?:unrecognised|expired|binding-mismatch|consumed))|clean-slate-(?:incomplete|admission-failed)|vss-exact-action-not-proved|native-go-live-bootstrap-(?:(?:reset|install|probe)-(?:connection|sni-load|script-parse|sql-batch-[1-9][0-9]*)-failed|(?:reset|install|probe)-failed))'\.|(native-go-live-bridge-(?:composition|invocation|discovery|call|result)-failed))\z") {
        $Record.reason_code = if ([string]::IsNullOrWhiteSpace($Matches[1])) { $Matches[2] } else { $Matches[1] }
    }
}

function Get-RequiredReflectionType {
    param(
        [Parameter(Mandatory)][Reflection.Assembly]$Assembly,
        [Parameter(Mandatory)][string]$Name)

    return $Assembly.GetType($Name, $true, $false)
}

function Get-RequiredReflectionMethod {
    param(
        [Parameter(Mandatory)][Type]$Type,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$ParameterCount,
        [switch]$Static)

    $flags = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
    $flags = $flags -bor $(if ($Static) { [Reflection.BindingFlags]::Static } else { [Reflection.BindingFlags]::Instance })
    $methods = @($Type.GetMethods($flags) | Where-Object {
            $_.Name -ceq $Name -and $_.GetParameters().Count -eq $ParameterCount
        })
    if ($methods.Count -ne 1) {
        throw "The native go-live reflection boundary cannot resolve $($Type.FullName).$Name."
    }
    return $methods[0]
}

function Invoke-RequiredReflectionMethod {
    param(
        [Parameter(Mandatory)][Reflection.MethodInfo]$Method,
        [AllowNull()][object]$Instance,
        [object[]]$Arguments = @())

    try {
        return $Method.Invoke($Instance, $Arguments)
    } catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}

function New-RequiredReflectionInstance {
    param(
        [Parameter(Mandatory)][Type]$Type,
        [object[]]$Arguments = @())

    $flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
    $constructors = @($Type.GetConstructors($flags) | Where-Object {
            $parameters = $_.GetParameters()
            if ($parameters.Count -ne $Arguments.Count) { return $false }
            for ($index = 0; $index -lt $parameters.Count; $index++) {
                $argument = $Arguments[$index]
                $parameterType = $parameters[$index].ParameterType
                if ($null -eq $argument) {
                    if ($parameterType.IsValueType -and $null -eq [Nullable]::GetUnderlyingType($parameterType)) {
                        return $false
                    }
                } elseif (-not $parameterType.IsInstanceOfType($argument)) {
                    return $false
                }
            }
            return $true
        })
    if ($constructors.Count -ne 1) {
        throw "The native go-live reflection boundary cannot resolve the $($Type.FullName) constructor."
    }
    try {
        return $constructors[0].Invoke($Arguments)
    } catch [Reflection.TargetInvocationException] {
        throw $_.Exception.InnerException
    }
}

function Get-RequiredReflectionProperty {
    param(
        [Parameter(Mandatory)][object]$Instance,
        [Parameter(Mandatory)][string]$Name)

    $flags = [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::NonPublic
    $property = $Instance.GetType().GetProperty($Name, $flags)
    if ($null -eq $property) {
        throw "The native go-live reflection boundary cannot resolve property $Name."
    }
    return $property.GetValue($Instance)
}

function Complete-ReflectedValueTask {
    param([Parameter(Mandatory)][object]$ValueTask)

    $asTask = $ValueTask.GetType().GetMethod("AsTask", [Reflection.BindingFlags]::Instance -bor [Reflection.BindingFlags]::Public)
    if ($null -eq $asTask) {
        throw "The native go-live closeout boundary did not return a ValueTask."
    }
    $task = $asTask.Invoke($ValueTask, @())
    return $task.GetAwaiter().GetResult()
}

function Clear-NativeGoLiveBootstrapEnvironment {
    [Environment]::SetEnvironmentVariable(
        "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP",
        $null,
        [EnvironmentVariableTarget]::Process)
    Remove-Item Env:\FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP -ErrorAction SilentlyContinue
}

function Assert-NativeGoLiveBootstrapEnvironment {
    Assert-NativeGoLiveBootstrapEnvironmentPresent
    $value = [Environment]::GetEnvironmentVariable(
        "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP",
        [EnvironmentVariableTarget]::Process)
    Assert-NativeGoLiveBootstrapConnection -ConnectionString $value
}

function Assert-NativeGoLiveBootstrapEnvironmentPresent {
    $value = [Environment]::GetEnvironmentVariable(
        "FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP",
        [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "sql-bootstrap-environment-missing"
    }
}

function Get-NativeGoLiveWindowsSqlClientAssemblyPath {
    param([Parameter(Mandatory)][string]$MergedMainRoot)

    $path = Join-Path $MergedMainRoot 'runtimes\win\lib\net9.0\Microsoft.Data.SqlClient.dll'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw 'native-go-live-windows-sql-client-missing'
    }

    return $path
}

function Get-NativeGoLiveWindowsSqlClientNativeSniAsset {
    param([Parameter(Mandatory)][string]$MergedMainRoot)

    $runtime = switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'X64' { 'win-x64'; break }
        'X86' { 'win-x86'; break }
        'Arm64' { 'win-arm64'; break }
        default { throw 'native-go-live-windows-sql-client-native-architecture-unsupported' }
    }
    $root = [IO.Path]::GetFullPath($MergedMainRoot)
    $directory = Join-Path $root (Join-Path 'runtimes' (Join-Path $runtime 'native'))
    $asset = [IO.Path]::GetFullPath((Join-Path $directory 'Microsoft.Data.SqlClient.SNI.dll'))
    if (-not (Test-Path -LiteralPath $directory -PathType Container) -or
        -not (Test-Path -LiteralPath $asset -PathType Leaf)) {
        throw 'native-go-live-windows-sql-client-native-missing'
    }

    return [pscustomobject]@{
        RuntimeIdentifier = $runtime
        Directory = $directory
        Path = $asset
    }
}

function Load-NativeGoLiveWindowsSqlClientNativeSniAsset {
    param([Parameter(Mandatory)][string]$SqlClientNativeSniAssetPath)

    $assetPath = [IO.Path]::GetFullPath($SqlClientNativeSniAssetPath)
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw 'native-go-live-windows-sql-client-native-missing'
    }
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeGoLiveSqlClientParentSniAsset
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

    public static bool LoadExact(string path)
    {
        var expected = Path.GetFullPath(path);
        var module = LoadLibrary(expected);
        if (module == IntPtr.Zero) return false;
        var actual = new StringBuilder(32768);
        var length = GetModuleFileName(module, actual, actual.Capacity);
        return length > 0 && length < actual.Capacity && string.Equals(
            Path.GetFullPath(actual.ToString()), expected, StringComparison.OrdinalIgnoreCase);
    }
}
"@
    if (-not [NativeGoLiveSqlClientParentSniAsset]::LoadExact($assetPath)) {
        throw 'native-go-live-windows-sql-client-native-load-failed'
    }
}

function Import-NativeGoLiveWindowsSqlClientAssembly {
    param([Parameter(Mandatory)][string]$SqlClientAssemblyPath)

    try {
        $assembly = [Reflection.Assembly]::LoadFrom($SqlClientAssemblyPath)
        $expectedPath = [IO.Path]::GetFullPath($SqlClientAssemblyPath)
        if ([string]::IsNullOrWhiteSpace($assembly.Location) -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($assembly.Location),
                $expectedPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'native-go-live-windows-sql-client-load-mismatch'
        }
        return $assembly
    } catch {
        throw 'native-go-live-windows-sql-client-load-failed'
    }
}

function Assert-NativeGoLiveBootstrapConnection {
    param([Parameter(Mandatory)][string]$ConnectionString)

    try {
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.set_ConnectionString($ConnectionString)
    } catch {
        throw "sql-bootstrap-malformed"
    }

    $expected = [ordered]@{
        "Data Source" = "localhost"
        "Initial Catalog" = "master"
        "Integrated Security" = "True"
        "Encrypt" = "True"
        "Trust Server Certificate" = "True"
        "Connect Timeout" = "5"
        "Connect Retry Count" = "0"
        "Pooling" = "False"
        "Application Name" = "FluxKnowledge.NativeGoLive"
    }
    if ($builder.Keys.Count -ne $expected.Count) {
        throw "sql-bootstrap-not-local-integrated"
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if (-not $builder.ContainsKey($entry.Key) -or
            -not [string]::Equals([string]$builder[$entry.Key], [string]$entry.Value, [StringComparison]::OrdinalIgnoreCase)) {
            throw "sql-bootstrap-not-local-integrated"
        }
    }
}

function New-NativeGoLiveSqlChildCommand {
    return @'
$ErrorActionPreference = 'Stop'
$safeFailureReason = $null
try {
    $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($null -eq $request -or [string]::IsNullOrWhiteSpace($request.connectionString) -or
        [string]::IsNullOrWhiteSpace($request.operation) -or
        [string]::IsNullOrWhiteSpace($request.sqlClientAssemblyPath) -or
        [string]::IsNullOrWhiteSpace($request.publishedPayloadRoot) -or
        [string]::IsNullOrWhiteSpace($request.sqlClientNativeRuntimeIdentifier) -or
        [string]::IsNullOrWhiteSpace($request.sqlClientNativeSniAssetPath)) {
        exit 1
    }
    if ([string]$request.operation -cnotin @('reset', 'install', 'probe')) { exit 1 }
    $operation = [string]$request.operation
    $safeFailureReason = "native-go-live-bootstrap-$operation-sni-load-failed"
    $runtime = switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        'X64' { 'win-x64'; break }
        'X86' { 'win-x86'; break }
        'Arm64' { 'win-arm64'; break }
        default { exit 1 }
    }
    if (-not [string]::Equals(
            [string]$request.sqlClientNativeRuntimeIdentifier,
            $runtime,
            [StringComparison]::Ordinal)) { exit 1 }
    if (-not [IO.Path]::IsPathFullyQualified([string]$request.publishedPayloadRoot) -or
        -not [IO.Path]::IsPathFullyQualified([string]$request.sqlClientNativeSniAssetPath)) { exit 1 }
    $expectedSniAsset = [IO.Path]::GetFullPath((Join-Path ([string]$request.publishedPayloadRoot) (
        Join-Path 'runtimes' (Join-Path $runtime 'native\Microsoft.Data.SqlClient.SNI.dll'))))
    $sniAsset = [IO.Path]::GetFullPath([string]$request.sqlClientNativeSniAssetPath)
    if (-not [string]::Equals($sniAsset, $expectedSniAsset, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $sniAsset -PathType Leaf)) { exit 1 }
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeGoLiveSqlClientSniAsset
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

    public static bool LoadExact(string path)
    {
        var expected = Path.GetFullPath(path);
        var module = LoadLibrary(expected);
        if (module == IntPtr.Zero) return false;
        var actual = new StringBuilder(32768);
        var length = GetModuleFileName(module, actual, actual.Capacity);
        return length > 0 && length < actual.Capacity && string.Equals(
            Path.GetFullPath(actual.ToString()), expected, StringComparison.OrdinalIgnoreCase);
    }
}
"@
    if (-not [NativeGoLiveSqlClientSniAsset]::LoadExact($sniAsset)) {
        exit 1
    }
    Add-Type -Path $request.sqlClientAssemblyPath
    $safeFailureReason = $null
    $sql = if ($request.operation -ceq 'reset') {
        [string]$request.resetSql
    } elseif ($request.operation -ceq 'probe') {
        'SELECT 1;'
    } elseif ($request.operation -ceq 'install') {
        $safeFailureReason = "native-go-live-bootstrap-$operation-script-parse-failed"
        $bootstrap = Get-Content -LiteralPath $request.bootstrapScript -Raw
        $tsqlLines = [System.Collections.Generic.List[string]]::new()
        foreach ($line in $bootstrap -split "`r?`n") {
            if ($line -match '(?i)^\s*:On\s+Error\s+exit\s*$') { continue }
            if ($line -match '(?i)^\s*:setvar\s+NativeGoLiveBootstrapLogin\s+"__SUPPLY_AT_EXECUTION__"\s*$') {
                continue
            }
            if ($line -match '(?i)^\s*--\s*(?:BEGIN|END)\s+HASHED\s+PROCEDURE:\s*FluxKnowledgeNativeGoLive(?:Create|Drop|ManageAppPool|ObserveAppPool)\s*$') {
                continue
            }
            if ($line -match '(?i)^\s*GO\s*$') {
                $tsqlLines.Add($line)
                continue
            }
            if ($line -match '(?i)^\s*GO(?:\s+.*)?$' -or $line -match '^\s*!!') {
                throw 'native-go-live-bootstrap-sqlcmd-directive-unsupported'
            }
            if ($line -match '^\s*:') {
                throw 'native-go-live-bootstrap-sqlcmd-directive-unsupported'
            }
            $tsqlLines.Add($line)
        }
        $bootstrap = [string]::Join([Environment]::NewLine, $tsqlLines)
        if ([regex]::IsMatch($bootstrap, '(?m)^\s*:')) {
            throw 'native-go-live-bootstrap-sqlcmd-directive-unsupported'
        }
        $bootstrap = $bootstrap.Replace('$(NativeGoLiveBootstrapLogin)', ([string]$request.bootstrapLogin).Replace("'", "''"))
        if ($bootstrap.Contains('$(')) {
            throw 'native-go-live-bootstrap-sqlcmd-variable-unsupported'
        }
        $safeFailureReason = $null
        $bootstrap
    } else {
        exit 1
    }
    $batches = if ($request.operation -ceq 'install') {
        [regex]::Split($sql, '(?im)^\s*GO\s*(?:\r?\n|$)')
    } else {
        @($sql)
    }
    $safeFailureReason = "native-go-live-bootstrap-$operation-connection-failed"
    $connection = [Microsoft.Data.SqlClient.SqlConnection]::new([string]$request.connectionString)
    try {
        $connection.Open()
        $safeFailureReason = $null
        $batchNumber = 0
        foreach ($batch in $batches) {
            if ([string]::IsNullOrWhiteSpace($batch)) { continue }
            $batchNumber++
            $safeFailureReason = "native-go-live-bootstrap-$operation-sql-batch-$batchNumber-failed"
            $command = $connection.CreateCommand()
            try {
                $command.CommandTimeout = 30
                $command.CommandText = $batch
                [void]$command.ExecuteNonQuery()
            } finally {
                $command.Dispose()
            }
            $safeFailureReason = $null
        }
    } finally {
        $connection.Dispose()
    }
} catch {
    if ($null -ne $safeFailureReason) { [Console]::Out.Write($safeFailureReason) }
    exit 1
}
'@
}

function Invoke-NativeGoLiveSqlChild {
    param(
        [Parameter(Mandatory)][ValidateSet('reset', 'install', 'probe')][string]$Operation,
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$BootstrapLogin,
        [Parameter(Mandatory)][string]$BootstrapScript,
        [Parameter(Mandatory)][string]$SqlClientAssemblyPath,
        [Parameter(Mandatory)][string]$PublishedPayloadRoot,
        [Parameter(Mandatory)][string]$SqlClientNativeRuntimeIdentifier,
        [Parameter(Mandatory)][string]$SqlClientNativeSniAssetPath,
        [Parameter(Mandatory)][string]$SqlChildExecutable,
        [Parameter(Mandatory)][string]$ResetSql,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 300,
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None)

    $CancellationToken.ThrowIfCancellationRequested()
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $SqlChildExecutable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('-NoProfile')
    $start.ArgumentList.Add('-NonInteractive')
    $start.ArgumentList.Add('-EncodedCommand')
    $start.ArgumentList.Add([Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes((New-NativeGoLiveSqlChildCommand))))
    [void]$start.Environment.Remove('FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP')
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "native-go-live-bootstrap-$Operation-start-failed" }
        $payload = [ordered]@{
            operation = $Operation
            connectionString = $ConnectionString
            bootstrapLogin = $BootstrapLogin
            bootstrapScript = $BootstrapScript
            sqlClientAssemblyPath = $SqlClientAssemblyPath
            publishedPayloadRoot = $PublishedPayloadRoot
            sqlClientNativeRuntimeIdentifier = $SqlClientNativeRuntimeIdentifier
            sqlClientNativeSniAssetPath = $SqlClientNativeSniAssetPath
            resetSql = $ResetSql
        } | ConvertTo-Json -Compress
        $process.StandardInput.Write($payload)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $timedOut = $false
        $cancelled = $false
        while (-not $process.HasExited) {
            if ($CancellationToken.IsCancellationRequested) {
                $cancelled = $true
                break
            }
            $remainingMilliseconds = [Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if ($remainingMilliseconds -le 0) {
                $timedOut = $true
                break
            }
            [void]$process.WaitForExit([Math]::Min(250, [int]$remainingMilliseconds))
        }
        if ($timedOut -or $cancelled) {
            try {
                Stop-FeatureProcessTree -ProcessId $process.Id
            }
            catch {
                throw "native-go-live-bootstrap-$Operation-termination-failed"
            }
            if (-not $process.WaitForExit(5000) -or -not $process.HasExited) {
                throw "native-go-live-bootstrap-$Operation-termination-unproved"
            }
            [void]$stdout.Wait(5000)
            [void]$stderr.Wait(5000)
            if ($cancelled) {
                $CancellationToken.ThrowIfCancellationRequested()
            }
            throw "native-go-live-bootstrap-$Operation-timed-out"
        }
        if (-not $stdout.Wait(5000) -or -not $stderr.Wait(5000)) {
            throw "native-go-live-bootstrap-$Operation-stream-close-timed-out"
        }
        $CancellationToken.ThrowIfCancellationRequested()
        if ($process.ExitCode -ne 0) {
            $failureReason = [string]$stdout.Result
            if ($failureReason -cmatch '\Anative-go-live-bootstrap-(?:reset|install|probe)-(?:connection|sni-load|script-parse|sql-batch-[1-9][0-9]*)-failed\z') {
                throw $failureReason
            }
            throw "native-go-live-bootstrap-$Operation-failed"
        }
    } finally {
        $process.Dispose()
    }
}

function Invoke-NativeGoLiveBootstrap {
    param(
        [Parameter(Mandatory)][string]$BootstrapScript,
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][string]$BootstrapLogin,
        [Parameter(Mandatory)][string]$SqlClientAssemblyPath,
        [Parameter(Mandatory)][string]$PublishedPayloadRoot,
        [Parameter(Mandatory)][string]$SqlClientNativeRuntimeIdentifier,
        [Parameter(Mandatory)][string]$SqlClientNativeSniAssetPath,
        [string]$SqlChildExecutable = 'pwsh',
        [Threading.CancellationToken]$CancellationToken = [Threading.CancellationToken]::None)

    $now = [DateTime]::UtcNow.ToString("o")
    $record = [ordered]@{
        name = "native-go-live-bootstrap"
        cwd = $MainRoot
        command = "Install-NativeGoLiveBootstrap"
        started_at = $now
        finished_at = $now
        duration_seconds = 0
        exit_code = 0
        log_path = $null
        skipped = [bool]$DryRun
    }
    $script:Steps += $record
    if ($DryRun) {
        return
    }

    try {
        if (-not (Test-Path -LiteralPath $BootstrapScript -PathType Leaf)) {
            throw "native-go-live-bootstrap-script-missing"
        }
        if (-not (Test-Path -LiteralPath $SqlClientAssemblyPath -PathType Leaf)) {
            throw "native-go-live-sql-client-missing"
        }
        Assert-NativeGoLiveBootstrapConnection -ConnectionString $ConnectionString
        if ([string]::IsNullOrWhiteSpace($BootstrapLogin)) {
            throw "native-go-live-bootstrap-identity-missing"
        }
        $reset = @'
USE master;
IF OBJECT_ID(N'dbo.FluxKnowledgeNativeGoLiveCreate',N'P') IS NOT NULL DROP PROCEDURE dbo.FluxKnowledgeNativeGoLiveCreate;
IF OBJECT_ID(N'dbo.FluxKnowledgeNativeGoLiveDrop',N'P') IS NOT NULL DROP PROCEDURE dbo.FluxKnowledgeNativeGoLiveDrop;
IF OBJECT_ID(N'dbo.FluxKnowledgeNativeGoLiveManageAppPool',N'P') IS NOT NULL DROP PROCEDURE dbo.FluxKnowledgeNativeGoLiveManageAppPool;
IF OBJECT_ID(N'dbo.FluxKnowledgeNativeGoLiveObserveAppPool',N'P') IS NOT NULL DROP PROCEDURE dbo.FluxKnowledgeNativeGoLiveObserveAppPool;
'@
        Invoke-NativeGoLiveSqlChild -Operation 'reset' -ConnectionString $ConnectionString `
            -BootstrapLogin $BootstrapLogin -BootstrapScript $BootstrapScript `
            -SqlClientAssemblyPath $SqlClientAssemblyPath -PublishedPayloadRoot $PublishedPayloadRoot `
            -SqlClientNativeRuntimeIdentifier $SqlClientNativeRuntimeIdentifier `
            -SqlClientNativeSniAssetPath $SqlClientNativeSniAssetPath `
            -SqlChildExecutable $SqlChildExecutable `
            -ResetSql $reset -CancellationToken $CancellationToken
        Invoke-NativeGoLiveSqlChild -Operation 'install' -ConnectionString $ConnectionString `
            -BootstrapLogin $BootstrapLogin -BootstrapScript $BootstrapScript `
            -SqlClientAssemblyPath $SqlClientAssemblyPath -PublishedPayloadRoot $PublishedPayloadRoot `
            -SqlClientNativeRuntimeIdentifier $SqlClientNativeRuntimeIdentifier `
            -SqlClientNativeSniAssetPath $SqlClientNativeSniAssetPath `
            -SqlChildExecutable $SqlChildExecutable `
            -ResetSql $reset -CancellationToken $CancellationToken
    } catch {
        $record.exit_code = 1
        if ($_.Exception.Message -cmatch '\Anative-go-live-bootstrap-(?:reset|install|probe)-(?:connection|sni-load|script-parse|sql-batch-[1-9][0-9]*)-failed\z') {
            $record.reason_code = $_.Exception.Message
        }
        $script:FailedStep = "native-go-live-bootstrap"
        throw
    } finally {
        Complete-FeatureStepRecord -Record $record -StartedAt ([DateTime]::Parse(
            $record.started_at,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind))
    }
}

function Invoke-NativeGoLiveComposition {
    param(
        [Parameter(Mandatory)][string]$MergedMainRoot,
        [Parameter(Mandatory)][string]$CommittedSha,
        [Parameter(Mandatory)][hashtable]$Acknowledgements,
        [Parameter(Mandatory)][string]$BootstrapScript)

    $applicationAssemblyPath = Join-Path $MergedMainRoot "FluxKnowledge.Application.dll"
    $integrationsAssemblyPath = Join-Path $MergedMainRoot "FluxKnowledge.Integrations.dll"
    foreach ($assemblyPath in @($applicationAssemblyPath, $integrationsAssemblyPath)) {
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
            throw "The immutable native go-live payload is incomplete."
        }
    }
    $sqlClientAssemblyPath = Get-NativeGoLiveWindowsSqlClientAssemblyPath -MergedMainRoot $MergedMainRoot
    $sqlClientNativeSniAsset = Get-NativeGoLiveWindowsSqlClientNativeSniAsset -MergedMainRoot $MergedMainRoot
    Load-NativeGoLiveWindowsSqlClientNativeSniAsset -SqlClientNativeSniAssetPath $sqlClientNativeSniAsset.Path
    $sqlClientAssembly = Import-NativeGoLiveWindowsSqlClientAssembly -SqlClientAssemblyPath $sqlClientAssemblyPath
    $applicationAssembly = [Reflection.Assembly]::LoadFrom($applicationAssemblyPath)
    $integrationsAssembly = [Reflection.Assembly]::LoadFrom($integrationsAssemblyPath)
    $bootstrapLogin = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    if ([string]::IsNullOrWhiteSpace($bootstrapLogin)) {
        throw 'native-go-live-bootstrap-identity-missing'
    }
    $bootstrapConnection = [Environment]::GetEnvironmentVariable(
        'FLUXKNOWLEDGE_NATIVE_GO_LIVE_SQL_BOOTSTRAP',
        [EnvironmentVariableTarget]::Process)
    Assert-NativeGoLiveBootstrapConnection -ConnectionString $bootstrapConnection
    Invoke-NativeGoLiveBootstrap -BootstrapScript $BootstrapScript -ConnectionString $bootstrapConnection `
        -BootstrapLogin $bootstrapLogin -SqlClientAssemblyPath $sqlClientAssemblyPath `
        -PublishedPayloadRoot $MergedMainRoot `
        -SqlClientNativeRuntimeIdentifier $sqlClientNativeSniAsset.RuntimeIdentifier `
        -SqlClientNativeSniAssetPath $sqlClientNativeSniAsset.Path
    Clear-NativeGoLiveBootstrapEnvironment
    $bootstrapInstaller = [System.Func[string, System.Threading.CancellationToken, System.Threading.Tasks.Task]] {
        param([string]$ignoredConnection, [System.Threading.CancellationToken]$ignoredToken)
        return [System.Threading.Tasks.Task]::CompletedTask
    }

    $planType = Get-RequiredReflectionType -Assembly $applicationAssembly `
        -Name "FluxKnowledge.Application.Operations.NativeGoLivePlan"
    $plan = Invoke-RequiredReflectionMethod `
        -Method (Get-RequiredReflectionMethod -Type $planType -Name "CreateProduction" -ParameterCount 1 -Static) `
        -Instance $null `
        -Arguments @($CommittedSha)
    $capabilityIssuerType = Get-RequiredReflectionType -Assembly $integrationsAssembly `
        -Name "FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveCloseoutCapabilityIssuer"
    $capabilityIssuer = New-RequiredReflectionInstance -Type $capabilityIssuerType -Arguments @($null)
    $hasherType = Get-RequiredReflectionType -Assembly $integrationsAssembly `
        -Name "FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLivePayloadHasher"
    $manifest = Invoke-RequiredReflectionMethod `
        -Method (Get-RequiredReflectionMethod -Type $hasherType -Name "Compute" -ParameterCount 1 -Static) `
        -Instance $null `
        -Arguments @($MergedMainRoot)

    $capability = Invoke-RequiredReflectionMethod `
        -Method (Get-RequiredReflectionMethod -Type $capabilityIssuerType -Name "Issue" -ParameterCount 3) `
        -Instance $capabilityIssuer `
        -Arguments @($plan, $MergedMainRoot, [string]$manifest.Sha256)
    $portsFactoryType = Get-RequiredReflectionType -Assembly $integrationsAssembly `
        -Name "FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveWindowsHostPorts"
    $ports = Invoke-RequiredReflectionMethod `
        -Method (Get-RequiredReflectionMethod -Type $portsFactoryType -Name "CreateProduction" -ParameterCount 2 -Static) `
        -Instance $null `
        -Arguments @($plan, $MergedMainRoot)
    $hostType = Get-RequiredReflectionType -Assembly $integrationsAssembly `
        -Name "FluxKnowledge.Integrations.Windows.NativeGoLive.GuardedNativeGoLiveHost"
    $nativeGoLiveHost = New-RequiredReflectionInstance -Type $hostType `
        -Arguments @($capability, $plan, $MergedMainRoot, $ports, $bootstrapInstaller)
    $requestType = Get-RequiredReflectionType -Assembly $integrationsAssembly `
        -Name "FluxKnowledge.Integrations.Windows.NativeGoLive.NativeGoLiveRequest"
    $request = New-RequiredReflectionInstance -Type $requestType -Arguments @(
        $plan, $false,
        [bool]$Acknowledgements.ConfirmCleanSlate,
        [bool]$Acknowledgements.ConfirmConfigureVss,
        [bool]$Acknowledgements.ConfirmDestroySql,
        [bool]$Acknowledgements.ConfirmRegisterCodex,
        $MergedMainRoot, [string]$manifest.Sha256, $manifest)

    return [pscustomobject]@{
        CapabilityIssuer = $capabilityIssuer
        Capability = $capability
        Request = $request
        NativeGoLiveHost = $nativeGoLiveHost
    }
}

function Invoke-NativeGoLiveModuleBridge {
    param(
        [Parameter(Mandatory)][string]$ModulePath,
        [Parameter(Mandatory)][object]$Composition)

    $module = Import-Module $ModulePath -Force -PassThru
    try {
        return & $module {
            param($Issuer, $Capability, $Request, $NativeGoLiveHost)
            Invoke-NativeGoLive -CapabilityIssuer $Issuer -Capability $Capability -Request $Request -NativeGoLiveHost $NativeGoLiveHost
        } $Composition.CapabilityIssuer $Composition.Capability $Composition.Request $Composition.NativeGoLiveHost
    } finally {
        Remove-Module $module -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-NativeGoLive {
    param(
        [Parameter(Mandatory)][string]$MergedMainRoot,
        [Parameter(Mandatory)][string]$CommittedSha,
        [Parameter(Mandatory)][hashtable]$Acknowledgements,
        [Parameter(Mandatory)][string]$ModulePath,
        [Parameter(Mandatory)][string]$BootstrapScript)

    foreach ($name in @("ConfirmCleanSlate", "ConfirmConfigureVss", "ConfirmDestroySql", "ConfirmRegisterCodex")) {
        if (-not $Acknowledgements.ContainsKey($name) -or -not [bool]$Acknowledgements[$name]) {
            throw "Every native go-live acknowledgement is required."
        }
    }
    try {
        try {
            $composition = Invoke-NativeGoLiveComposition -MergedMainRoot $MergedMainRoot -CommittedSha $CommittedSha `
                -Acknowledgements $Acknowledgements -BootstrapScript $BootstrapScript
        } catch {
            throw 'native-go-live-bridge-composition-failed'
        }
        try {
            $result = Invoke-NativeGoLiveModuleBridge -ModulePath $ModulePath -Composition $composition
        } catch {
            if ($_.Exception.Message -cmatch '\Anative-go-live-bridge-(?:discovery|call|result)-failed\z') {
                throw $_.Exception
            }
            throw 'native-go-live-bridge-invocation-failed'
        }
        if (-not $result.Succeeded) {
            throw "Native go-live failed with safe reason code '$($result.ReasonCode)'."
        }
    } finally {
        Clear-NativeGoLiveBootstrapEnvironment
    }
}

function Invoke-CloseoutCleanup {
    if ($KeepWorktree) {
        return
    }
    $previousMainRoot = $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT
    $previousFeatureWorktree = $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE
    $previousBranch = $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH
    try {
        $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $MainRoot
        $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $FeatureWorktree
        $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH = $Branch
        Invoke-FeatureStep -Name "cleanup-worktree" -Cwd $MainRoot -Command $CleanupWorktreeCommand
    } finally {
        $env:FLUXKNOWLEDGE_CLOSEOUT_MAIN_ROOT = $previousMainRoot
        $env:FLUXKNOWLEDGE_CLOSEOUT_FEATURE_WORKTREE = $previousFeatureWorktree
        $env:FLUXKNOWLEDGE_CLOSEOUT_BRANCH = $previousBranch
    }
}

function Invoke-FinalPushAndCleanup {
    Invoke-FeatureStep -Name "push-main" -Cwd $MainRoot -Command 'git push origin main'
    Invoke-CloseoutCleanup
}

Assert-NativeGoLiveAcknowledgements
$FeatureWorktree = (Resolve-Path -LiteralPath $FeatureWorktree).Path
if (-not $MainRoot) {
    $MainRoot = Get-MainWorktreePath -Worktree $FeatureWorktree
}
$MainRoot = (Resolve-Path -LiteralPath $MainRoot).Path
$Branch = (git -C $FeatureWorktree branch --show-current).Trim()
if (-not $Branch.StartsWith("codex/", [System.StringComparison]::Ordinal)) {
    throw "Refusing to complete non-codex branch '$Branch'."
}
$script:LogRoot = Join-Path $MainRoot ".agents\run-logs"
New-Item -ItemType Directory -Force -Path $script:LogRoot | Out-Null
$script:Steps = @()
$script:FailedStep = $null
$safeCommitMessage = $CommitMessage.Replace("'", "''")
$safeBranch = $Branch.Replace("'", "''")
$goLiveModulePath = Join-Path (Split-Path -Parent $PSScriptRoot) "deploy\native-go-live.psm1"
$goLiveBootstrapScript = Join-Path (Split-Path -Parent $PSScriptRoot) "deploy\native-go-live-bootstrap.sql"
if (-not (Test-Path -LiteralPath $goLiveModulePath -PathType Leaf)) {
    throw "The private native go-live module is missing."
}
$closeoutRoot = Join-Path $MainRoot ".agents\native-go-live"

try {
    if ($GoLive -and -not $DryRun) {
        Assert-NativeGoLiveBootstrapEnvironmentPresent
    }
    Invoke-FeatureStep -Name "verify-main-clean" -Cwd $MainRoot -Command 'if ((git status --porcelain) -ne $null) { git status --short; exit 1 }' -RunInDryRun
    Invoke-FeatureStep -Name "dotnet-tool-restore" -Cwd $FeatureWorktree -Command 'dotnet tool restore'
    Invoke-FeatureStep -Name "dotnet-restore-locked" -Cwd $FeatureWorktree -Command 'dotnet restore FluxKnowledge.slnx --locked-mode'
    Invoke-FeatureStep -Name "dotnet-build-release" -Cwd $FeatureWorktree -Command 'dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror'
    Invoke-FeatureStep -Name "dotnet-test-native" -Cwd $FeatureWorktree -Command 'dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"' -TimeoutSeconds $TestStepTimeoutSeconds
    Invoke-FeatureStep -Name "native-closeout-contract" -Cwd $FeatureWorktree -Command 'pwsh -NoProfile -File .\tests\native\complete-feature-dryrun.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "native-go-live-bootstrap-nondryrun-contract" -Cwd $FeatureWorktree -Command 'pwsh -NoProfile -File .\tests\native\complete-feature-bootstrap-nondryrun.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "native-go-live-contract" -Cwd $FeatureWorktree -Command 'pwsh -NoProfile -File .\tests\native\native-go-live-contract.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "native-go-live-direct-admin-sql-transition-contract" -Cwd $FeatureWorktree -Command 'pwsh -NoProfile -File .\tests\native\native-go-live-direct-admin-sql-transition.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "native-go-live-one-shot-admission-contract" -Cwd $FeatureWorktree -Command 'dotnet test .\tests\FluxKnowledge.Integration.Tests\FluxKnowledge.Integration.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~NativeGoLiveOneShotAdmissionTests" --logger "console;verbosity=minimal"'
    Invoke-FeatureStep -Name "native-go-live-recovery-removal-contract" -Cwd $FeatureWorktree -Command 'pwsh -NoProfile -File .\tests\native\native-go-live-recovery-removal-contract.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "native-deployment-contract" -Cwd $FeatureWorktree -Command 'pwsh -NoProfile -File .\tests\native\native-deployment-plan.ps1 -SourceRoot .'
    Invoke-FeatureStep -Name "feature-commit" -Cwd $FeatureWorktree -Command "git add -A -- . ':(exclude).superpowers/sdd/'; if ((git status --porcelain) -ne `$null) { git commit -m '$safeCommitMessage' }"
    Invoke-FeatureStep -Name "sync-main" -Cwd $MainRoot -Command 'git pull --ff-only origin main'
    Invoke-FeatureStep -Name "squash-merge" -Cwd $MainRoot -Command "git merge --squash '$safeBranch'"
    Invoke-FeatureStep -Name "dotnet-tool-restore-main" -Cwd $MainRoot -Command 'dotnet tool restore'
    Invoke-FeatureStep -Name "dotnet-restore-locked-main" -Cwd $MainRoot -Command 'dotnet restore FluxKnowledge.slnx --locked-mode'
    Invoke-FeatureStep -Name "dotnet-build-release-main" -Cwd $MainRoot -Command 'dotnet build FluxKnowledge.slnx -c Release --no-restore -warnaserror'
    Invoke-FeatureStep -Name "dotnet-test-native-main" -Cwd $MainRoot -Command 'dotnet test FluxKnowledge.slnx -c Release --no-build --logger "console;verbosity=minimal"' -TimeoutSeconds $TestStepTimeoutSeconds
    Invoke-FeatureStep -Name "main-commit" -Cwd $MainRoot -Command "if ((git status --porcelain) -ne `$null) { git commit -m '$safeCommitMessage' } else { 'No staged changes to commit.' }"

    $headSha = (git -C $MainRoot rev-parse HEAD).Trim()
    if ($GoLive) {
        $mergedMainRoot = Join-Path $closeoutRoot "payload-$headSha"
        $safeMergedMainRoot = $mergedMainRoot.Replace("'", "''")
        $publishCommand = "if (Test-Path -LiteralPath '$safeMergedMainRoot') { Remove-Item -LiteralPath '$safeMergedMainRoot' -Recurse -Force }; dotnet publish .\src\FluxKnowledge.Web\FluxKnowledge.Web.csproj -c Release --no-build --no-restore -o '$safeMergedMainRoot'"
        Invoke-FeatureStep -Name "publish-merged-main" -Cwd $MainRoot -Command $publishCommand
        if ($DryRun) {
            Add-DirectNativeGoLiveStep -Name "native-go-live" -Command "Invoke-NativeGoLive" -Skipped
            Add-DirectNativeGoLiveStep -Name "native-go-live-bootstrap" -Command "Install-NativeGoLiveBootstrap" -Skipped
        } else {
            $acknowledgements = @{
                ConfirmCleanSlate = [bool]$ConfirmCleanSlate
                ConfirmConfigureVss = [bool]$ConfirmConfigureVss
                ConfirmDestroySql = [bool]$ConfirmDestroySql
                ConfirmRegisterCodex = [bool]$ConfirmRegisterCodex
            }
            $goLiveRecord = New-DirectNativeGoLiveStep -Name "native-go-live" -Command "Invoke-NativeGoLive"
            try {
                Invoke-NativeGoLive -MergedMainRoot $mergedMainRoot -CommittedSha $headSha `
                    -Acknowledgements $acknowledgements -ModulePath $goLiveModulePath `
                    -BootstrapScript $goLiveBootstrapScript
            } catch {
                $goLiveRecord.exit_code = 1
                Record-NativeGoLiveFailure -Record $goLiveRecord -Exception $_.Exception
                throw
            } finally {
                Complete-FeatureStepRecord -Record $goLiveRecord -StartedAt ([DateTime]::Parse(
                    $goLiveRecord.started_at,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind))
            }
        }
    }
    Invoke-FinalPushAndCleanup
    Write-SummaryAndExit -ExitCode 0
} catch {
    Write-SummaryAndExit -ExitCode 1
}
