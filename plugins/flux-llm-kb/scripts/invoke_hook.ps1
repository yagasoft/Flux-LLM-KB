param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("user-prompt-submit", "pre-compact", "stop")]
    [string]$Event
)

$ErrorActionPreference = "Stop"
$HookInput = [Console]::In.ReadToEnd()

function Test-FluxPython {
    param(
        [string]$Candidate
    )

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $false
    }

    try {
        & $Candidate -c "import flux_llm_kb" *> $null
        return ($LASTEXITCODE -eq 0)
    } catch {
        return $false
    }
}

$Candidates = @()
if ($env:FLUX_KB_PYTHON) {
    $Candidates += $env:FLUX_KB_PYTHON
}
$Candidates += "python"

$Python = $Candidates | Where-Object { Test-FluxPython $_ } | Select-Object -First 1
if (-not $Python) {
    Write-Error "No Python interpreter available for Flux LLM-KB hooks."
    exit 1
}

if ($HookInput) {
    $HookInput | & $Python -m flux_llm_kb.cli hook $Event
} else {
    & $Python -m flux_llm_kb.cli hook $Event
}
exit $LASTEXITCODE
