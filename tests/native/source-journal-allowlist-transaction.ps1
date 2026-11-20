[CmdletBinding()]
param(
    [string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$InterruptFixtureRoot,
    [guid]$InterruptFixtureId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$module = Join-Path $SourceRoot 'scripts/deploy/source-journal-allowlist-transaction.psm1'
if (Test-Path -LiteralPath $module) { Import-Module $module -Force }
if (!(Get-Command Invoke-SourceJournalAllowlistTransaction -ErrorAction SilentlyContinue)) {
    throw 'The native allowlist transaction is missing.'
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Assert-Refused([scriptblock]$Attempt, [string]$Reason) {
    try { $null = & $Attempt }
    catch {
        if ($_.Exception.Message -like "*$Reason*") { return }
        throw
    }
    throw "Expected refusal for $Reason."
}

$first = '\\?\VOLUME{11111111-1111-1111-1111-111111111111}\'
$second = '\\?\VOLUME{22222222-2222-2222-2222-222222222222}\'
$third = '\\?\VOLUME{33333333-3333-3333-3333-333333333333}\'
$stagingBase = 'I:\Codex Workspaces\Staging\FluxKnowledge'
$staging = Join-Path $stagingBase ('JournalAllowlist-' + [guid]::NewGuid().ToString('N'))

function New-Fixture([string]$ExistingArea, [switch]$CrashAfterAppWrite) {
    $area = $ExistingArea
    if (!$area) {
        $area = Join-Path $staging ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $area
    }
    $app = Join-Path $area 'app.json'
    $helper = Join-Path $area 'helper.json'
    $holdMarker = Join-Path $area 'held.fixture'
    $stopMarker = Join-Path $area 'stopped.fixture'
    if (!$ExistingArea) {
        [IO.File]::WriteAllText($app, (@{ Volume = @($first); Marker = 'app setting' } | ConvertTo-Json) + "`r`n")
        [IO.File]::WriteAllText($helper, (@{ Volume = @($first); Marker = 'private helper setting' } | ConvertTo-Json) + "`n")
    }
    $state = @{
        Identity = 'isolated fixture 1'; Held = [IO.File]::Exists($holdMarker); Stopped = [IO.File]::Exists($stopMarker)
        Stops = 0; Starts = 0; Writes = 0; Probes = 0; Releases = 0
        FailHelperOnce = $false; ProbeFailures = 0; LoseReleaseResponse = $false
        SubstituteAtStop = $false; SkipStopping = $false
        Events = [System.Collections.Generic.List[string]]::new()
    }
    $ports = @{
        GetTargetIdentity = { $state.Identity }.GetNewClosure()
        ReadPair = {
            [byte[]]$appBytes = [IO.File]::ReadAllBytes($app)
            [byte[]]$helperBytes = [IO.File]::ReadAllBytes($helper)
            @{
                AppBytes = $appBytes; HelperBytes = $helperBytes
                AppVolumes = @(([Text.Encoding]::UTF8.GetString($appBytes) | ConvertFrom-Json).Volume)
                HelperVolumes = @(([Text.Encoding]::UTF8.GetString($helperBytes) | ConvertFrom-Json).Volume)
            }
        }.GetNewClosure()
        BuildPair = {
            param($Before, [string[]]$Approved)
            $appObject = [Text.Encoding]::UTF8.GetString($Before.AppBytes) | ConvertFrom-Json
            $helperObject = [Text.Encoding]::UTF8.GetString($Before.HelperBytes) | ConvertFrom-Json
            $appObject.Volume = $Approved
            $helperObject.Volume = $Approved
            @{
                AppBytes = [Text.Encoding]::UTF8.GetBytes(($appObject | ConvertTo-Json -Compress))
                HelperBytes = [Text.Encoding]::UTF8.GetBytes(($helperObject | ConvertTo-Json -Compress))
                AppVolumes = $Approved; HelperVolumes = $Approved
            }
        }
        GetAdmissionHeld = { $state.Held }.GetNewClosure()
        HoldAdmission = {
            [IO.File]::WriteAllText($holdMarker, 'held')
            $state.Held = $true
            $state.Events.Add('hold')
        }.GetNewClosure()
        StopOwners = {
            if (!$state.Held) { throw 'stop without hold' }
            if (!$state.SkipStopping -and !$state.Stopped) {
                [IO.File]::WriteAllText($stopMarker, 'stopped')
                $state.Stops++
                $state.Stopped = $true
            }
            if ($state.SubstituteAtStop) { [IO.File]::AppendAllText($helper, ' ') }
        }.GetNewClosure()
        AssertOwnersStopped = { if (!$state.Stopped) { throw 'owners still running' } }.GetNewClosure()
        WriteApp = {
            param([byte[]]$Expected, [byte[]]$Replacement)
            if (!$state.Held -or !$state.Stopped) { throw 'app write without stop and hold' }
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($app)) -cne [Convert]::ToBase64String($Expected)) { throw 'app CAS failed' }
            [IO.File]::WriteAllBytes($app, $Replacement)
            $state.Writes++
            if ($CrashAfterAppWrite) { [Environment]::Exit(73) }
        }.GetNewClosure()
        WriteHelper = {
            param([byte[]]$Expected, [byte[]]$Replacement)
            if (!$state.Held -or !$state.Stopped) { throw 'helper write without stop and hold' }
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($helper)) -cne [Convert]::ToBase64String($Expected)) { throw 'helper CAS failed' }
            if ($state.FailHelperOnce) { $state.FailHelperOnce = $false; throw 'interrupted between writes' }
            [IO.File]::WriteAllBytes($helper, $Replacement)
            $state.Writes++
        }.GetNewClosure()
        StartOwners = {
            if (!$state.Held) { throw 'start without hold' }
            if ($state.Stopped) {
                [IO.File]::Delete($stopMarker)
                $state.Starts++
                $state.Stopped = $false
            }
        }.GetNewClosure()
        VerifyTransport = {
            param([string[]]$Allowed, [string[]]$Removed)
            if (!$state.Held -or $state.Stopped) { throw 'probe with wrong lifecycle state' }
            $state.Probes++
            $state.Events.Add('probe ' + ($Allowed -join ',') + ' removed ' + ($Removed -join ','))
            if ($state.ProbeFailures -gt 0) { $state.ProbeFailures--; throw 'transport unavailable' }
        }.GetNewClosure()
        ReleaseAdmission = {
            [IO.File]::Delete($holdMarker)
            $state.Held = $false
            $state.Releases++
            if ($state.LoseReleaseResponse) { throw 'release response lost' }
        }.GetNewClosure()
    }
    @{
        Id = [guid]::NewGuid(); Area = $area; App = $app; Helper = $helper; State = $state; Ports = $ports
        OriginalApp = [Convert]::ToBase64String([IO.File]::ReadAllBytes($app))
        OriginalHelper = [Convert]::ToBase64String([IO.File]::ReadAllBytes($helper))
    }
}

function Invoke-Fixture($Fixture, [string[]]$Expected = @($first), [string[]]$Proposed = @($first, $second)) {
    Invoke-SourceJournalAllowlistTransaction -OperationId $Fixture.Id -ExpectedApprovedVolumes $Expected `
        -ApprovedVolumes $Proposed -ReceiptRoot $Fixture.Area -Ports $Fixture.Ports
}

function Assert-OriginalPair($Fixture) {
    Assert-True (([Convert]::ToBase64String([IO.File]::ReadAllBytes($Fixture.App))) -ceq $Fixture.OriginalApp) 'App original bytes were not restored.'
    Assert-True (([Convert]::ToBase64String([IO.File]::ReadAllBytes($Fixture.Helper))) -ceq $Fixture.OriginalHelper) 'Helper original bytes were not restored.'
}

if ($InterruptFixtureRoot) {
    $directory = Get-Item -LiteralPath $InterruptFixtureRoot -Force
    $expectedBase = (Resolve-Path -LiteralPath $stagingBase).ProviderPath
    if ($directory -isnot [IO.DirectoryInfo] -or $directory.Name -cnotmatch '^[a-f0-9]{32}$' -or
        $directory.Parent.Name -cnotmatch '^JournalAllowlist-[a-f0-9]{32}$' -or $directory.Parent.Parent.FullName -ne $expectedBase -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($directory.Parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid staging directory for the interruption fixture.' }
    $fixture = New-Fixture -ExistingArea $directory.FullName -CrashAfterAppWrite
    $fixture.Id = $InterruptFixtureId
    $null = Invoke-Fixture $fixture
    throw 'The interruption point returned without process termination.'
}

$null = New-Item -ItemType Directory -Path $staging
try {
    $fixture = New-Fixture
    Assert-Refused { Invoke-Fixture $fixture @($second) } 'stale'
    Assert-Refused { Invoke-Fixture $fixture @($first) @($first, $first) } 'volume'
    Assert-Refused { Invoke-Fixture $fixture @($first) @('I:') } 'volume'
    Assert-True (!$fixture.State.Held -and $fixture.State.Stops -eq 0) 'Invalid request acquired the hold or stopped owners.'
    $result = Invoke-Fixture $fixture
    Assert-True ($result -is [hashtable] -and $result.Status -ceq 'Completed' -and [guid]$result.OperationId -eq $fixture.Id) 'Successful request did not return a completed receipt.'
    Assert-True (!$result.ContainsKey('Before') -and !$result.ContainsKey('After') -and !$result.ContainsKey('TargetIdentity')) 'Public receipt exposed protected configuration evidence.'
    Assert-True (!$fixture.State.Held -and $fixture.State.Stops -eq 1 -and $fixture.State.Starts -eq 1 -and $fixture.State.Writes -eq 2) 'Successful operation did not release once after paired writes.'
    $app = Get-Content -LiteralPath $fixture.App -Raw | ConvertFrom-Json
    $helper = Get-Content -LiteralPath $fixture.Helper -Raw | ConvertFrom-Json
    Assert-True ($app.Marker -ceq 'app setting' -and $helper.Marker -ceq 'private helper setting') 'Fixture marker settings were lost.'
    Assert-True (($app.Volume -join '|') -ceq "$first|$second" -and ($helper.Volume -join '|') -ceq "$first|$second") 'Resulting configuration pairs disagree.'
    Assert-True ((Invoke-Fixture $fixture @($first) @($second.ToLowerInvariant(), $first)).Status -ceq 'Completed') 'Identical canonical retry failed.'
    Assert-Refused { Invoke-Fixture $fixture @($first) @($first, $third) } 'already bound'
    Assert-True ($fixture.State.Starts -eq 1 -and $fixture.State.Writes -eq 2) 'Retry repeated configuration or owner mutations.'

    $fixture = New-Fixture
    $fixture.State.FailHelperOnce = $true
    $result = Invoke-Fixture $fixture
    Assert-True ($result.Status -ceq 'Reverted' -and !$fixture.State.Held) 'A reversible split write did not restore the previous pair.'
    Assert-OriginalPair $fixture
    Assert-True ($fixture.State.Writes -eq 2 -and $fixture.State.Events.Contains("probe $first removed $second")) 'Split-write reversal did not verify old transport and denial of the proposed volume.'
    Assert-True ((Invoke-Fixture $fixture).Status -ceq 'Reverted' -and $fixture.State.Starts -eq 1) 'A reverted operation resumed applying its proposal.'

    $fixture = New-Fixture
    $command = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $command.UseShellExecute = $false
    foreach ($argument in @('-NoProfile', '-File', $PSCommandPath, '-SourceRoot', $SourceRoot,
        '-InterruptFixtureRoot', $fixture.Area, '-InterruptFixtureId', $fixture.Id.ToString('D'))) {
        $command.ArgumentList.Add($argument)
    }
    $child = [Diagnostics.Process]::Start($command)
    try {
        if (!$child.WaitForExit(15000)) { $child.Kill(); throw 'The interruption fixture did not exit.' }
        Assert-True ($child.ExitCode -eq 73) 'The child runner did not abruptly stop after its first replacement.'
    }
    finally { $child.Dispose() }
    Assert-True (([Convert]::ToBase64String([IO.File]::ReadAllBytes($fixture.App))) -cne $fixture.OriginalApp -and
        ([Convert]::ToBase64String([IO.File]::ReadAllBytes($fixture.Helper))) -ceq $fixture.OriginalHelper) 'The interrupted process did not leave the actual split pair.'
    $resumed = New-Fixture -ExistingArea $fixture.Area
    Assert-True ($resumed.State.Held -and $resumed.State.Stopped) 'The interrupted process lost the hold or stop proof.'
    Assert-Refused { Invoke-Fixture $resumed } 'unresolved'
    $resumed.Id = $fixture.Id
    Assert-True ((Invoke-Fixture $resumed).Status -ceq 'Reverted' -and !$resumed.State.Held -and
        $resumed.State.Writes -eq 1 -and $resumed.State.Starts -eq 1) 'Fresh runner did not recover the interrupted split from durable evidence.'
    Assert-OriginalPair $fixture

    $fixture = New-Fixture
    $fixture.State.ProbeFailures = 1
    $result = Invoke-Fixture $fixture @($first) @($second)
    Assert-True ($result.Status -ceq 'Reverted' -and !$fixture.State.Held -and $fixture.State.Probes -eq 2) 'Failed forward transport did not prove rollback transport.'
    Assert-True ($fixture.State.Events.Contains("probe $second removed $first")) 'Forward probe missed removed-volume denial.'
    Assert-OriginalPair $fixture

    $fixture = New-Fixture
    $fixture.State.ProbeFailures = 2
    $result = Invoke-Fixture $fixture
    Assert-True ($result.Status -ceq 'RecoveryRequired' -and $fixture.State.Held) 'Unproved rollback transport released admission.'
    Assert-OriginalPair $fixture
    $prior = $fixture.Id
    $fixture.Id = [guid]::NewGuid()
    Assert-Refused { Invoke-Fixture $fixture } 'unresolved'
    $fixture.Id = $prior
    Assert-True ((Invoke-Fixture $fixture).Status -ceq 'Reverted' -and !$fixture.State.Held -and $fixture.State.Starts -eq 2) 'Retry repeated a completed owner restart or failed to verify rollback.'

    $fixture = New-Fixture
    $fixture.State.LoseReleaseResponse = $true
    $result = Invoke-Fixture $fixture
    Assert-True ($result.Status -ceq 'RecoveryRequired' -and !$fixture.State.Held) 'Unconfirmed release did not record durable recovery evidence.'
    Assert-True ((Invoke-Fixture $fixture).Status -ceq 'Completed' -and $fixture.State.Releases -eq 1 -and $fixture.State.Writes -eq 2 -and $fixture.State.Starts -eq 1) 'Retry after completed hold release edited the live pair or repeated start.'

    $fixture = New-Fixture
    $fixture.State.SubstituteAtStop = $true
    $result = Invoke-Fixture $fixture
    Assert-True ($result.Status -ceq 'RecoveryRequired' -and $fixture.State.Held -and $fixture.State.Writes -eq 0) 'Changed helper bytes were overwritten or admission was reopened.'
    $fixture.State.Identity = 'isolated fixture 2'
    Assert-Refused { Invoke-Fixture $fixture } 'identity'

    $fixture = New-Fixture
    $fixture.State.SkipStopping = $true
    $result = Invoke-Fixture $fixture
    Assert-True ($result.Status -ceq 'RecoveryRequired' -and $fixture.State.Held -and $fixture.State.Writes -eq 0) 'An unproved process stop permitted a configuration write or released the hold.'
    Assert-OriginalPair $fixture

    $fixture = New-Fixture
    $fixture.State.ProbeFailures = 2
    Assert-True ((Invoke-Fixture $fixture).Status -ceq 'RecoveryRequired') 'Corruption fixture did not retain a recovery receipt.'
    $receiptPath = Join-Path $fixture.Area "source-journal-allowlist-$($fixture.Id.ToString('D')).json"
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    $receipt.Before.AppBase64 = [Convert]::ToBase64String([byte[]]@(1, 2, 3))
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 6))
    $starts = $fixture.State.Starts
    Assert-Refused { Invoke-Fixture $fixture } 'corrupt-receipt-config-bytes'
    Assert-True ($fixture.State.Held -and $fixture.State.Starts -eq $starts) 'A corrupted byte snapshot triggered lifecycle changes.'

    $fixture = New-Fixture
    $linkedReceipt = Join-Path $fixture.Area "source-journal-allowlist-$($fixture.Id.ToString('D')).json"
    $unrelated = New-Fixture
    $null = New-Item -ItemType Junction -Path $linkedReceipt -Target $unrelated.Area
    try {
        Assert-Refused { Invoke-Fixture $fixture } 'invalid-receipt-file'
        Assert-True (!$fixture.State.Held -and $fixture.State.Stops -eq 0) 'A linked receipt permitted admission changes.'
    }
    finally { Remove-Item -LiteralPath $linkedReceipt -Force }

    Write-Output 'Source journal allowlist transaction fixtures passed.'
}
finally {
    $verifiedStaging = (Resolve-Path -LiteralPath $staging).ProviderPath
    $stagingParent = (Resolve-Path -LiteralPath $stagingBase).ProviderPath
    if (!$verifiedStaging.StartsWith($stagingParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture cleanup path escaped the staging directory.'
    }
    Remove-Item -LiteralPath $verifiedStaging -Recurse -Force
}
