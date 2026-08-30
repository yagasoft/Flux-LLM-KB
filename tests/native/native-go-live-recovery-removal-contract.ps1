[CmdletBinding()]
param(
    [string]$SourceRoot = ""
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-False {
    param([bool]$Condition, [string]$Message)
    if ($Condition) { throw $Message }
}

function Get-RequiredText {
    param([string]$RelativePath)

    $path = Join-Path $SourceRoot $RelativePath
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Required deployment source file is missing: $path"
    return Get-Content -LiteralPath $path -Raw
}

$removedFiles = @(
    'src\FluxKnowledge.Application\Operations\NativeGoLive\NativeGoLiveJournal.cs',
    'src\FluxKnowledge.Application\Operations\NativeGoLive\NativeGoLiveRootMarker.cs',
    'src\FluxKnowledge.Application\Operations\NativeGoLive\NativeGoLiveAuthority.cs',
    'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveJournalStore.cs',
    'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveLease.cs',
    'tests\FluxKnowledge.Domain.Tests\Operations\NativeGoLiveAuthorityTests.cs',
    'tests\FluxKnowledge.Domain.Tests\Operations\NativeGoLiveRootAdmissionTests.cs',
    'tests\FluxKnowledge.Domain.Tests\Operations\NativeGoLiveRootMarkerTests.cs',
    'tests\FluxKnowledge.Integration.Tests\Operations\NativeGoLiveJournalStoreTests.cs',
    'tests\FluxKnowledge.Integration.Tests\Operations\NativeGoLiveExecutorTests.cs',
    'tests\FluxKnowledge.Integration.Tests\Operations\NativeGoLiveGuardedHostTests.cs',
    'tests\FluxKnowledge.Integration.Tests\Operations\NativeGoLiveHostLifecycleTests.cs',
    'tests\FluxKnowledge.Integration.Tests\Operations\NativeGoLiveWindowsAdapterTests.cs'
)
foreach ($relativePath in $removedFiles) {
    Assert-False (Test-Path -LiteralPath (Join-Path $SourceRoot $relativePath)) "Deployment recovery file remains: $relativePath"
}

$hostText = Get-RequiredText 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\GuardedNativeGoLiveHost.cs'
$ports = Get-RequiredText 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLivePorts.cs'
$windowsPorts = Get-RequiredText 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveWindowsHostPorts.cs'
$adapters = Get-RequiredText 'src\FluxKnowledge.Integrations\Windows\NativeGoLive\NativeGoLiveWindowsAdapters.cs'
$layout = Get-RequiredText 'src\FluxKnowledge.Application\Operations\LiveRootLayout.cs'
$closeout = Get-RequiredText 'scripts\dev\complete-feature.ps1'
$module = Get-RequiredText 'scripts\deploy\native-go-live.psm1'
$bootstrap = Get-RequiredText 'scripts\deploy\native-go-live-bootstrap.sql'
$layoutTests = Get-RequiredText 'tests\FluxKnowledge.Domain.Tests\Operations\LiveRootLayoutTests.cs'
$fileSystemTests = Get-RequiredText 'tests\FluxKnowledge.Integration.Tests\Operations\HandleRelativeNativeFileSystemTests.cs'
$deploymentText = $hostText + $ports + $windowsPorts + $adapters + $layout + $closeout + $module + $bootstrap +
    $layoutTests + $fileSystemTests

foreach ($forbidden in @(
    'NativeGoLiveJournal',
    'NativeGoLiveAdoptionState',
    'NativeGoLiveJournalPhase',
    'NativeGoLiveRootMarker',
    'NativeGoLiveRootAdmission',
    'NativeGoLiveRootShape',
    'NativeGoLiveOwnerMarker',
    'INativeGoLiveJournalSession',
    'DurableRecoveryPrefix',
    'journal-marker',
    'adoption-prefix',
    'historic-preliminary',
    'native-go-live-owner.json',
    'closeout.json',
    'Read-NativeGoLiveCloseoutJournal',
    'Write-NativeGoLiveCloseoutJournal',
    'ExistingCloseoutJournal',
    'RecoverAsync',
    'ResumeAsync',
    'ReplayAsync'
)) {
    Assert-False ($deploymentText -match [regex]::Escape($forbidden)) "Deployment recovery surface remains: $forbidden"
}

Write-Output 'Native go-live recovery removal contract passed.'
