param([string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-passage-window-eligibility-migration.psm1') -Force
$contract = Get-PassageWindowEligibilityMigrationContract -GpuResidency
if ($contract.Baseline -cne '20261119125051_AddMediaTranscriptionGpuRequests' -or
    $contract.Target -cne '20261119201434_AddGpuModelResidencies' -or $contract.PriorHistoryCount -ne 83) {
    throw 'Expected the exact schema 83 to 84 migration.'
}
foreach ($direction in @('up','down')) {
    $file = Join-Path $SourceRoot ("scripts/deploy/gpu-model-residency-{0}.sql" -f $direction)
    if ((Get-FileHash -LiteralPath $file).Hash -cne $contract.($direction + 'Sha256')) { throw 'Residency migration SQL changed.' }
}
& (Join-Path $PSScriptRoot 'passage-window-eligibility-updater.ps1') -SourceRoot $SourceRoot -GpuResidency
& (Join-Path $PSScriptRoot 'passage-window-pre-ddl-abort.ps1') -SourceRoot $SourceRoot -GpuResidency
