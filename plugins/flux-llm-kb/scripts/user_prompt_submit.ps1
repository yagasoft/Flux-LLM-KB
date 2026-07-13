$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "invoke_hook.ps1") -Event "user-prompt-submit"
exit $LASTEXITCODE
