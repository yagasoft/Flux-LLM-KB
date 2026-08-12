$ErrorActionPreference = 'Stop'

$payloadRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$deployRoot = Split-Path -Parent $payloadRoot
$hostPath = Join-Path $payloadRoot 'FluxKnowledge.OutlookHost.exe'
$settingsPath = Join-Path $deployRoot 'appsettings.Production.json'

if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
    throw 'The deployed Outlook host payload is incomplete.'
}

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$connection = [string]$settings.ConnectionStrings.FluxKnowledge
if ([string]::IsNullOrWhiteSpace($connection)) {
    throw 'The local SQL connection is unavailable.'
}

$previous = $env:ConnectionStrings__FluxKnowledge
try {
    $env:ConnectionStrings__FluxKnowledge = $connection
    & $hostPath '--run-once'
    exit $LASTEXITCODE
} finally {
    if ($null -eq $previous) {
        Remove-Item Env:\ConnectionStrings__FluxKnowledge -ErrorAction SilentlyContinue
    } else {
        $env:ConnectionStrings__FluxKnowledge = $previous
    }
}
