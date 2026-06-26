[CmdletBinding()]
param(
    [string]$Python = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$bundledPython = Join-Path $env:USERPROFILE ".cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
if ([string]::IsNullOrWhiteSpace($Python)) {
    if (Test-Path -LiteralPath $bundledPython) {
        $Python = $bundledPython
    } elseif (-not [string]::IsNullOrWhiteSpace($env:PYTHON)) {
        $Python = $env:PYTHON
    } else {
        $Python = "python"
    }
}

Push-Location $repoRoot
try {
    & $Python "scripts\docs\build_dashboard_user_guide.py" --capture-screens
    if ($LASTEXITCODE -ne 0) {
        throw "Screenshot generation failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
