$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "invoke_hook.ps1") -Event "pre-compact"
exit $LASTEXITCODE
