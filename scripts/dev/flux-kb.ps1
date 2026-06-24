param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$FluxArgs = @()
)

$ErrorActionPreference = "Stop"

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$SourcePath = Join-Path $RepoRoot "src"
$Python = if ($env:FLUX_KB_DEV_PYTHON) {
    $env:FLUX_KB_DEV_PYTHON
} elseif ($env:FLUX_KB_PYTHON) {
    $env:FLUX_KB_PYTHON
} else {
    "python"
}

$previousPythonPath = $env:PYTHONPATH
try {
    if ($previousPythonPath) {
        $env:PYTHONPATH = "$SourcePath$([System.IO.Path]::PathSeparator)$previousPythonPath"
    } else {
        $env:PYTHONPATH = $SourcePath
    }
    & $Python -m flux_llm_kb.cli @FluxArgs
    exit $LASTEXITCODE
} finally {
    $env:PYTHONPATH = $previousPythonPath
}
