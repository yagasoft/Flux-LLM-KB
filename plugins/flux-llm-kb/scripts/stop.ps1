$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "invoke_hook.ps1") -Event "stop"
exit $LASTEXITCODE
