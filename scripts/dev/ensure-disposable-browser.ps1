[CmdletBinding()]
param(
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'

function Resolve-ApprovedBrowserExecutable {
    param([Parameter(Mandatory = $true)][string]$Candidate)

    if (-not [System.IO.Path]::IsPathRooted($Candidate) -or $Candidate.StartsWith('\\', [System.StringComparison]::Ordinal)) {
        throw 'Disposable browser requires an explicitly supplied local executable path.'
    }

    $resolved = (Resolve-Path -LiteralPath $Candidate -ErrorAction Stop).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or
        -not [string]::Equals([System.IO.Path]::GetExtension($resolved), '.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Disposable browser requires an existing .exe file.'
    }

    return $resolved
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $cacheRoot = Join-Path $env:LOCALAPPDATA 'ms-playwright'
    if (-not (Test-Path -LiteralPath $cacheRoot -PathType Container)) {
        throw 'No local Playwright browser cache was found; browser tests will not download a browser.'
    }

    $candidates = Get-ChildItem -LiteralPath $cacheRoot -Recurse -File -Filter 'chrome-headless-shell.exe' -ErrorAction SilentlyContinue
    if ($null -eq $candidates -or $candidates.Count -eq 0) {
        $candidates = Get-ChildItem -LiteralPath $cacheRoot -Recurse -File -Filter 'chrome.exe' -ErrorAction SilentlyContinue
    }
    if ($null -eq $candidates -or $candidates.Count -eq 0) {
        throw 'No Chromium executable exists in the local Playwright cache; browser tests will not download a browser.'
    }

    $ExecutablePath = $candidates[0].FullName
}

$resolvedExecutable = Resolve-ApprovedBrowserExecutable -Candidate $ExecutablePath
$probeStart = [System.Diagnostics.ProcessStartInfo]::new($resolvedExecutable)
$probeStart.UseShellExecute = $false
$probeStart.RedirectStandardOutput = $true
$probeStart.RedirectStandardError = $true
$probeStart.ArgumentList.Add('--version')
$probe = [System.Diagnostics.Process]::Start($probeStart)
$null = $probe.StandardOutput.ReadToEnd()
$null = $probe.StandardError.ReadToEnd()
$probe.WaitForExit()
if ($probe.ExitCode -ne 0) {
    throw "Validated local browser version probe failed with exit code $($probe.ExitCode)."
}

Write-Output $resolvedExecutable
