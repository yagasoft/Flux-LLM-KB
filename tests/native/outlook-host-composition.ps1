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

$officeInterop = Join-Path $publishRoot 'office.dll'
if (-not (Test-Path -LiteralPath $officeInterop -PathType Leaf)) {
    throw "Published Outlook host is missing office.dll."
}
