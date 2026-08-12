[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$publishRoot = Join-Path $repoRoot 'artifacts\outlook-host-composition-test'

dotnet publish (Join-Path $repoRoot 'src\FluxKnowledge.OutlookHost\FluxKnowledge.OutlookHost.csproj') `
    --configuration Release `
    --no-restore `
    --output $publishRoot
if ($LASTEXITCODE -ne 0) { throw "Outlook host publish failed with exit code $LASTEXITCODE." }

$requiredPayloadFiles = @(
    'FluxKnowledge.OutlookHost.exe',
    'Microsoft.Office.Interop.Outlook.dll',
    'office.dll'
)

foreach ($requiredPayloadFile in $requiredPayloadFiles) {
    $payloadPath = Join-Path $publishRoot $requiredPayloadFile
    if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
        throw "Published Outlook host is missing $requiredPayloadFile."
    }
}
