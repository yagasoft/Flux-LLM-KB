Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The caller holds the canonical incremental updater mutex from target discovery
# through receipt publication and validates the protected receipt root. Native ports
# are scoped to that target. Stop, start and release must tolerate a repeated call
# after an interrupted response. GetAdmissionHeld throws for a foreign hold.
# Pair states are original (0), replacement app with original helper (1), and
# replacement pair (2). A helper-first split is outside this transaction's history.
$script:OperationPhases = @{
    Prepared = @(0); Hold = @(0); Stop = @(0); WriteApp = @(0, 1); WriteHelper = @(1, 2)
    Start = @(2); Verify = @(2); Release = @(2)
    RollbackStop = @(0, 1, 2); RestoreHelper = @(1, 2); RestoreApp = @(0, 1)
    RollbackStart = @(0); RollbackVerify = @(0); RollbackRelease = @(0)
}
$script:MaxConfigBytes = 1MB
$script:MaxReceiptBytes = 8MB

function Get-AllowlistVolumes([object]$Values) {
    if ($Values -isnot [array] -or $Values.Count -lt 1 -or $Values.Count -gt 16) {
        throw 'allowlist-invalid-volume-count'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($volume in $Values) {
        if ($volume -isnot [string] -or $volume -notmatch '^\\\\\?\\Volume\{[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\}\\\z' -or
            !$seen.Add($volume.ToUpperInvariant())) {
            throw 'allowlist-invalid-or-duplicate-volume'
        }
    }
    [string[]]$volumes = @($seen)
    [Array]::Sort($volumes, [StringComparer]::Ordinal)
    return ,$volumes
}

function Get-AllowlistDigest([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}

function Get-AllowlistTargetDigest([hashtable]$Ports) {
    $identity = & $Ports.GetTargetIdentity
    if ($identity -isnot [string] -or [string]::IsNullOrWhiteSpace($identity) -or $identity.Length -gt 16384) {
        throw 'allowlist-invalid-target-identity'
    }
    Get-AllowlistDigest ([Text.Encoding]::UTF8.GetBytes($identity))
}

function Get-AllowlistPairSnapshot($Pair) {
    if ($Pair -isnot [Collections.IDictionary] -or $Pair.AppBytes -isnot [byte[]] -or $Pair.HelperBytes -isnot [byte[]] -or
        $Pair.AppBytes.Length -lt 1 -or $Pair.AppBytes.Length -gt $script:MaxConfigBytes -or
        $Pair.HelperBytes.Length -lt 1 -or $Pair.HelperBytes.Length -gt $script:MaxConfigBytes) {
        throw 'allowlist-invalid-config-pair'
    }
    @{
        AppVolumes = Get-AllowlistVolumes $Pair.AppVolumes
        HelperVolumes = Get-AllowlistVolumes $Pair.HelperVolumes
        AppSha256 = Get-AllowlistDigest $Pair.AppBytes
        HelperSha256 = Get-AllowlistDigest $Pair.HelperBytes
    }
}

function Assert-AllowlistPairVolumes([hashtable]$Snapshot, [string[]]$Volumes, [string]$Failure) {
    $expected = $Volumes -join '|'
    if (($Snapshot.AppVolumes -join '|') -cne $expected -or ($Snapshot.HelperVolumes -join '|') -cne $expected) {
        throw $Failure
    }
}

function Assert-AllowlistPlainReceipt($Item) {
    if ($Item -isnot [IO.FileInfo] -or ($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $Item.Length -lt 1 -or $Item.Length -gt $script:MaxReceiptBytes) {
        throw 'allowlist-invalid-receipt-file'
    }
}

function Read-AllowlistReceipt($Item) {
    Assert-AllowlistPlainReceipt $Item
    $receipt = Get-Content -LiteralPath $Item.FullName -Raw | ConvertFrom-Json -AsHashtable -DateKind String
    if ($receipt.Version -ne 1 -or $Item.Name -cne "source-journal-allowlist-$($receipt.OperationId).json" -or
        $receipt.OperationId -cnotmatch '^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}\z' -or
        $receipt.TargetIdentityDigest -cnotmatch '^[A-F0-9]{64}\z') {
        throw 'allowlist-invalid-receipt-identity'
    }
    if ($receipt.Status -cin @('Completed', 'Reverted')) {
        if ($receipt.Phase -cne 'Finished') { throw 'allowlist-invalid-finished-receipt' }
    }
    elseif ($receipt.Status -cnotin @('InProgress', 'RecoveryRequired') -or
        $receipt.Phase -cnotin $script:OperationPhases.Keys -or
        ($receipt.Phase -ceq 'RollbackStop' -and $receipt.RollbackFrom -notin @(0, 1, 2))) {
        throw 'allowlist-invalid-receipt-phase'
    }
    foreach ($name in @('ExpectedApprovedVolumes', 'ApprovedVolumes')) {
        $volumes = Get-AllowlistVolumes $receipt[$name]
        if (($volumes -join '|') -cne ($receipt[$name] -join '|')) { throw 'allowlist-invalid-receipt-volume-order' }
    }
    return $receipt
}

function Write-AllowlistReceipt([string]$Path, [Collections.IDictionary]$Receipt) {
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(($Receipt | ConvertTo-Json -Depth 6 -Compress))
    if ($bytes.Length -gt $script:MaxReceiptBytes) { throw 'allowlist-receipt-too-large' }
    try {
        $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $Path) {
            Assert-AllowlistPlainReceipt (Get-Item -LiteralPath $Path -Force)
            [IO.File]::Replace($temporary, $Path, [NullString]::Value)
        }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Set-AllowlistPhase([hashtable]$Context, [string]$Phase, [string]$Status = 'InProgress',
    [hashtable]$Extra = @{}) {
    $next = @{} + $Context.Receipt
    $next.Phase = $Phase
    $next.Status = $Status
    $next.UpdatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    foreach ($key in $Extra.Keys) { $next[$key] = $Extra[$key] }
    Write-AllowlistReceipt $Context.Path $next
    $Context.Receipt = $next
}

function Get-AllowlistPublicReceipt([Collections.IDictionary]$Receipt) {
    @{
        OperationId = $Receipt.OperationId; Status = $Receipt.Status; Phase = $Receipt.Phase
        ExpectedApprovedVolumes = $Receipt.ExpectedApprovedVolumes; ApprovedVolumes = $Receipt.ApprovedVolumes
        UpdatedUtc = $Receipt.UpdatedUtc; FailurePhase = $Receipt.FailurePhase; RecoveryError = $Receipt.RecoveryError
    }
}

function Get-AllowlistReceiptBytes([Collections.IDictionary]$Snapshot, [string]$Name) {
    [byte[]]$bytes = [Convert]::FromBase64String($Snapshot[$Name + 'Base64'])
    if ($bytes.Length -lt 1 -or $bytes.Length -gt $script:MaxConfigBytes -or
        (Get-AllowlistDigest $bytes) -cne $Snapshot[$Name + 'Sha256']) {
        throw 'allowlist-corrupt-receipt-config-bytes'
    }
    return ,$bytes
}

function Assert-AllowlistTarget([hashtable]$Context) {
    if ((Get-AllowlistTargetDigest $Context.Ports) -cne $Context.Receipt.TargetIdentityDigest) {
        throw 'allowlist-target-identity-changed'
    }
}

function Get-AllowlistObservedState([hashtable]$Context, [int[]]$AllowedStates) {
    Assert-AllowlistTarget $Context
    $current = Get-AllowlistPairSnapshot (& $Context.Ports.ReadPair)
    $before = $Context.Receipt.Before
    $after = $Context.Receipt.After
    $state = -1
    if ($current.HelperSha256 -ceq $before.HelperSha256) {
        if ($current.AppSha256 -ceq $before.AppSha256) { $state = 0 }
        if ($current.AppSha256 -ceq $after.AppSha256) { $state = 1 }
    }
    elseif ($current.HelperSha256 -ceq $after.HelperSha256 -and $current.AppSha256 -ceq $after.AppSha256) { $state = 2 }
    if ($state -notin $AllowedStates) { throw 'allowlist-unexpected-config-pair' }
    return $state
}

function Test-AllowlistAdmissionHeld([hashtable]$Context) {
    $held = & $Context.Ports.GetAdmissionHeld
    if ($held -isnot [bool]) { throw 'allowlist-invalid-hold-response' }
    return $held
}

function Assert-AllowlistAdmissionHeld([hashtable]$Context) {
    if (!(Test-AllowlistAdmissionHeld $Context)) { throw 'allowlist-owned-hold-missing' }
}

function Set-AllowlistConfig([hashtable]$Context, [string]$Name, [int]$ExpectedState, [switch]$Restore) {
    Assert-AllowlistAdmissionHeld $Context
    $null = & $Context.Ports.AssertOwnersStopped
    $null = Get-AllowlistObservedState $Context @($ExpectedState)
    $from = $Context.Receipt.Before
    $to = $Context.Receipt.After
    if ($Restore) { $from, $to = $to, $from }
    $expected = Get-AllowlistReceiptBytes $from $Name
    $replacement = Get-AllowlistReceiptBytes $to $Name
    $null = & $Context.Ports['Write' + $Name] $expected $replacement
}

function Assert-AllowlistTransport([hashtable]$Context, [switch]$Restore) {
    Assert-AllowlistAdmissionHeld $Context
    $allowed = $Context.Receipt.ApprovedVolumes
    $other = $Context.Receipt.ExpectedApprovedVolumes
    $expectedState = 2
    if ($Restore) {
        $allowed, $other = $other, $allowed
        $expectedState = 0
    }
    $null = Get-AllowlistObservedState $Context @($expectedState)
    $removed = [string[]]@($other | Where-Object { $_ -cnotin $allowed })
    $null = & $Context.Ports.VerifyTransport ([string[]]$allowed) $removed
    Assert-AllowlistAdmissionHeld $Context
    $null = Get-AllowlistObservedState $Context @($expectedState)
}

function Complete-AllowlistRelease([hashtable]$Context, [switch]$Restore, [switch]$RecheckTransport) {
    $status = 'Completed'
    $phase = 'Release'
    $state = 2
    if ($Restore) { $status = 'Reverted'; $phase = 'RollbackRelease'; $state = 0 }
    $null = Get-AllowlistObservedState $Context @($state)
    if ($RecheckTransport) {
        # A fresh runner may only accept missing hold evidence after the original
        # runner persisted the release intent against a proved pair and transport.
        if (!(Test-AllowlistAdmissionHeld $Context)) {
            Set-AllowlistPhase $Context 'Finished' $status @{ RecoveryError = $null }
            return
        }
        Assert-AllowlistTransport $Context -Restore:$Restore
    }
    else {
        Assert-AllowlistAdmissionHeld $Context
        Set-AllowlistPhase $Context $phase
    }
    $null = & $Context.Ports.ReleaseAdmission
    if (Test-AllowlistAdmissionHeld $Context) { throw 'allowlist-release-did-not-clear-hold' }
    Set-AllowlistPhase $Context 'Finished' $status @{ RecoveryError = $null }
}

function Invoke-AllowlistRollback([hashtable]$Context) {
    $phase = $Context.Receipt.Phase
    if ($phase -ceq 'Hold') {
        $null = Get-AllowlistObservedState $Context @(0)
        if (!(Test-AllowlistAdmissionHeld $Context)) { $null = & $Context.Ports.HoldAdmission }
        Set-AllowlistPhase $Context 'RollbackVerify'
    }
    elseif ($phase -cnotin @('RollbackStop', 'RestoreHelper', 'RestoreApp', 'RollbackStart', 'RollbackVerify')) {
        Assert-AllowlistAdmissionHeld $Context
        $state = Get-AllowlistObservedState $Context $script:OperationPhases[$phase]
        Set-AllowlistPhase $Context 'RollbackStop' -Extra @{ RollbackFrom = $state }
    }

    Assert-AllowlistAdmissionHeld $Context
    if ($Context.Receipt.Phase -ceq 'RollbackStop') {
        $state = Get-AllowlistObservedState $Context @($Context.Receipt.RollbackFrom)
        $null = & $Context.Ports.StopOwners
        $null = & $Context.Ports.AssertOwnersStopped
        $null = Get-AllowlistObservedState $Context @($state)
        if ($state -eq 2) { Set-AllowlistPhase $Context 'RestoreHelper' }
        elseif ($state -eq 1) { Set-AllowlistPhase $Context 'RestoreApp' }
        else { Set-AllowlistPhase $Context 'RollbackStart' }
    }
    if ($Context.Receipt.Phase -ceq 'RestoreHelper') {
        $state = Get-AllowlistObservedState $Context @(1, 2)
        if ($state -eq 2) { Set-AllowlistConfig $Context 'Helper' 2 -Restore }
        $null = Get-AllowlistObservedState $Context @(1)
        Set-AllowlistPhase $Context 'RestoreApp'
    }
    if ($Context.Receipt.Phase -ceq 'RestoreApp') {
        $state = Get-AllowlistObservedState $Context @(0, 1)
        if ($state -eq 1) { Set-AllowlistConfig $Context 'App' 1 -Restore }
        $null = Get-AllowlistObservedState $Context @(0)
        Set-AllowlistPhase $Context 'RollbackStart'
    }
    if ($Context.Receipt.Phase -ceq 'RollbackStart') {
        $null = Get-AllowlistObservedState $Context @(0)
        $null = & $Context.Ports.StartOwners
        Set-AllowlistPhase $Context 'RollbackVerify'
    }
    Assert-AllowlistTransport $Context -Restore
    Complete-AllowlistRelease $Context -Restore
}

function Start-AllowlistApply([hashtable]$Context) {
    $null = Get-AllowlistObservedState $Context @(0)
    if (Test-AllowlistAdmissionHeld $Context) { throw 'allowlist-unexpected-existing-hold' }
    Set-AllowlistPhase $Context 'Hold'
    $null = & $Context.Ports.HoldAdmission
    Assert-AllowlistAdmissionHeld $Context
    $null = Get-AllowlistObservedState $Context @(0)
    Set-AllowlistPhase $Context 'Stop'
    $null = & $Context.Ports.StopOwners
    $null = & $Context.Ports.AssertOwnersStopped
    $null = Get-AllowlistObservedState $Context @(0)
    Set-AllowlistPhase $Context 'WriteApp'
    Set-AllowlistConfig $Context 'App' 0
    $null = Get-AllowlistObservedState $Context @(1)
    Set-AllowlistPhase $Context 'WriteHelper'
    Set-AllowlistConfig $Context 'Helper' 1
    $null = Get-AllowlistObservedState $Context @(2)
    Assert-AllowlistAdmissionHeld $Context
    Set-AllowlistPhase $Context 'Start'
    $null = & $Context.Ports.StartOwners
    Set-AllowlistPhase $Context 'Verify'
    Assert-AllowlistTransport $Context
    Complete-AllowlistRelease $Context
}

function Invoke-SourceJournalAllowlistTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][guid]$OperationId,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ExpectedApprovedVolumes,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ApprovedVolumes,
        [Parameter(Mandatory)][string]$ReceiptRoot,
        [Parameter(Mandatory)][hashtable]$Ports
    )

    if ($OperationId -eq [guid]::Empty) { throw 'allowlist-invalid-operation-id' }
    $id = $OperationId.ToString('D')
    $expected = Get-AllowlistVolumes $ExpectedApprovedVolumes
    $proposed = Get-AllowlistVolumes $ApprovedVolumes
    if (($expected -join '|') -ceq ($proposed -join '|')) { throw 'allowlist-no-volume-change' }
    foreach ($name in @('GetTargetIdentity', 'ReadPair', 'BuildPair', 'GetAdmissionHeld', 'HoldAdmission', 'StopOwners',
        'AssertOwnersStopped', 'WriteApp', 'WriteHelper', 'StartOwners', 'VerifyTransport', 'ReleaseAdmission')) {
        if ($Ports[$name] -isnot [scriptblock]) { throw "allowlist-missing-port-$name" }
    }

    $root = Get-Item -LiteralPath $ReceiptRoot -Force
    if ($root -isnot [IO.DirectoryInfo] -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'allowlist-invalid-receipt-root'
    }
    $path = Join-Path $root.FullName "source-journal-allowlist-$id.json"
    $receipt = $null
    foreach ($entry in (Get-ChildItem -LiteralPath $root.FullName -Filter 'source-journal-allowlist-*.json' -Force)) {
        $stored = Read-AllowlistReceipt $entry
        if ($stored.OperationId -ceq $id) { $receipt = $stored }
        elseif ($stored.Status -cnotin @('Completed', 'Reverted')) { throw 'allowlist-unresolved-operation-exists' }
    }

    if ($null -eq $receipt) {
        $identity = Get-AllowlistTargetDigest $Ports
        $before = & $Ports.ReadPair
        $beforeSnapshot = Get-AllowlistPairSnapshot $before
        Assert-AllowlistPairVolumes $beforeSnapshot $expected 'allowlist-stale-or-mismatched-volume-sets'
        $beforeSnapshot.AppBase64 = [Convert]::ToBase64String($before.AppBytes)
        $beforeSnapshot.HelperBase64 = [Convert]::ToBase64String($before.HelperBytes)
        $after = & $Ports.BuildPair $before $proposed
        $afterSnapshot = Get-AllowlistPairSnapshot $after
        Assert-AllowlistPairVolumes $afterSnapshot $proposed 'allowlist-invalid-proposed-pair-volumes'
        if ($afterSnapshot.AppSha256 -ceq $beforeSnapshot.AppSha256 -or $afterSnapshot.HelperSha256 -ceq $beforeSnapshot.HelperSha256) {
            throw 'allowlist-proposed-pair-missing-config-change'
        }
        $afterSnapshot.AppBase64 = [Convert]::ToBase64String($after.AppBytes)
        $afterSnapshot.HelperBase64 = [Convert]::ToBase64String($after.HelperBytes)
        $receipt = @{
            Version = 1; OperationId = $id; TargetIdentityDigest = $identity
            ExpectedApprovedVolumes = $expected; ApprovedVolumes = $proposed
            Before = $beforeSnapshot; After = $afterSnapshot; FailurePhase = $null; RecoveryError = $null
        }
        $context = @{ Receipt = $receipt; Path = $path; Ports = $Ports }
        $null = Get-AllowlistObservedState $context @(0)
        if (Test-AllowlistAdmissionHeld $context) { throw 'allowlist-unexpected-existing-hold' }
        Set-AllowlistPhase $context 'Prepared'
    }
    else {
        if (($receipt.ExpectedApprovedVolumes -join '|') -cne ($expected -join '|') -or
            ($receipt.ApprovedVolumes -join '|') -cne ($proposed -join '|')) { throw 'allowlist-operation-id already bound to a different request' }
        if ($receipt.Status -cin @('Completed', 'Reverted')) { return (Get-AllowlistPublicReceipt $receipt) }
        $context = @{ Receipt = $receipt; Path = $path; Ports = $Ports }
        Assert-AllowlistTarget $context
        foreach ($side in @($receipt.Before, $receipt.After)) {
            $null = Get-AllowlistReceiptBytes $side 'App'
            $null = Get-AllowlistReceiptBytes $side 'Helper'
        }
    }

    try {
        switch -CaseSensitive ($context.Receipt.Phase) {
            'Prepared' { Start-AllowlistApply $context }
            'Release' { Complete-AllowlistRelease $context -RecheckTransport }
            'RollbackRelease' { Complete-AllowlistRelease $context -Restore -RecheckTransport }
            default {
                if (!$context.Receipt.FailurePhase) {
                    Set-AllowlistPhase $context $context.Receipt.Phase -Extra @{ FailurePhase = $context.Receipt.Phase }
                }
                Invoke-AllowlistRollback $context
            }
        }
    }
    catch {
        $phase = $context.Receipt.Phase
        $errorCode = $_.Exception.Message
        if ($errorCode -cnotmatch '^allowlist-[a-z0-9-]+\z') { $errorCode = 'native-operation-failed' }
        $failure = $context.Receipt.FailurePhase
        if (!$failure) { $failure = $phase }
        # Record the last durable phase, never the exception text of a native port.
        $alreadyRecovering = $null -ne $context.Receipt.FailurePhase
        Set-AllowlistPhase $context $phase 'RecoveryRequired' @{ FailurePhase = $failure; RecoveryError = $errorCode }
        if (!$alreadyRecovering -and $phase -cnotin @('Prepared', 'Release', 'RollbackRelease')) {
            try { Invoke-AllowlistRollback $context }
            catch {
                $errorCode = $_.Exception.Message
                if ($errorCode -cnotmatch '^allowlist-[a-z0-9-]+\z') { $errorCode = 'native-operation-failed' }
                Set-AllowlistPhase $context $context.Receipt.Phase 'RecoveryRequired' @{ RecoveryError = $errorCode }
            }
        }
    }
    Get-AllowlistPublicReceipt $context.Receipt
}

# Read-only reconciliation for an HTTP receipt whose worker lost its response.
# Only a terminal receipt for the exact reviewed intent can resolve that result.
function Get-SourceJournalAllowlistTransactionResult {
    param([Parameter(Mandatory)][string]$ReceiptRoot,[Parameter(Mandatory)][guid]$OperationId,
        [Parameter(Mandatory)][string[]]$ExpectedApprovedVolumes,[Parameter(Mandatory)][string[]]$ApprovedVolumes)
    $id=$OperationId.ToString('D')
    $path=Join-Path $ReceiptRoot "source-journal-allowlist-$id.json"
    if(![IO.File]::Exists($path)){return $null}
    $item=Get-Item -LiteralPath $path -Force
    $receipt=Read-AllowlistReceipt $item
    $before=Get-AllowlistVolumes $ExpectedApprovedVolumes
    $after=Get-AllowlistVolumes $ApprovedVolumes
    if($receipt.OperationId -cne $id -or $receipt.Status -cnotin @('Completed','Reverted') -or
        ($before -join '|') -cne ($receipt.ExpectedApprovedVolumes -join '|') -or
        ($after -join '|') -cne ($receipt.ApprovedVolumes -join '|')){return $null}
    return Get-AllowlistPublicReceipt $receipt
}

Export-ModuleMember -Function Invoke-SourceJournalAllowlistTransaction,Get-SourceJournalAllowlistTransactionResult
