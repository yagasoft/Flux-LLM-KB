[CmdletBinding()]
param(
    [string]$TestFilter = 'Category=Browser',
    [string]$ExecutablePath,
    [string]$SqlServerConnectionString
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$browserHelper = Join-Path $PSScriptRoot 'ensure-disposable-browser.ps1'
$validatedExecutable = (& $browserHelper -ExecutablePath $ExecutablePath).Trim()
if ([string]::IsNullOrWhiteSpace($validatedExecutable)) {
    throw 'Disposable browser prerequisite did not return a validated executable path.'
}

$start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.WorkingDirectory = $repositoryRoot
$start.ArgumentList.Add('test')
$start.ArgumentList.Add('tests/FluxKnowledge.Web.Tests/FluxKnowledge.Web.Tests.csproj')
$start.ArgumentList.Add('--configuration')
$start.ArgumentList.Add('Release')
$start.ArgumentList.Add('--no-restore')
$start.ArgumentList.Add('--filter')
$start.ArgumentList.Add($TestFilter)
$start.Environment['FLUXKNOWLEDGE_BROWSER_TESTS'] = '1'
$start.Environment['FLUXKNOWLEDGE_BROWSER_EXECUTABLE'] = $validatedExecutable
if (-not [string]::IsNullOrWhiteSpace($SqlServerConnectionString)) {
    $start.Environment['FLUXKNOWLEDGE_TEST_SQL_CONNECTION'] = $SqlServerConnectionString
}

$process = [System.Diagnostics.Process]::Start($start)
$process.WaitForExit()
exit $process.ExitCode
