$ErrorActionPreference = "Stop"
$Python = if ($env:FLUX_KB_PYTHON) { $env:FLUX_KB_PYTHON } else { "python" }
& $Python -m flux_llm_kb.cli hook stop
