Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'incremental-repository-recovery-migration.psm1')
Import-Module (Join-Path $PSScriptRoot 'incremental-hybrid-passage-rebuild.psm1')

function Assert-RepositoryRecoveryReceiptPath([string]$Path) {
    foreach ($itemPath in @((Split-Path -Parent $Path), $Path)) {
        if (Test-Path -LiteralPath $itemPath) {
            $item=Get-Item -LiteralPath $itemPath -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'repository-recovery-receipt-path-unsafe' }
        }
    }
}

function Read-RepositoryRecoveryReleaseReceipt {
    param([Parameter(Mandatory)][string]$ReceiptPath)
    Assert-RepositoryRecoveryReceiptPath $ReceiptPath
    $receipt=Get-Content -LiteralPath $ReceiptPath -Raw -ErrorAction Stop | ConvertFrom-Json -AsHashtable
    $phases=@('Prepared','MigrationIntent','MigrationVerified','PayloadActivationIntent','ValidatedHeld','HoldReleaseIntent','Completed','FailedHeld')
    if ($receipt.Version -ne 1 -or $receipt.Revision -lt 1 -or $receipt.Phase -cnotin $phases -or
        !$receipt.Binding -or $receipt.Binding.Commit -cnotmatch '^[0-9a-f]{40}$' -or
        $receipt.Binding.ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[0-9a-f]{12}-repositoryrecovery$' -or
        $receipt.Binding.OperationId -cnotmatch '^[0-9a-f-]{36}$' -or
        $receipt.Binding.MigrationSha256 -cne (Get-RepositoryRecoveryMigrationContract).UpSha256 -or
        $receipt.BindingHash -cne (Get-RepositoryRecoveryValueHash $receipt.Binding) -or
        [IO.Path]::GetFullPath($receipt.Binding.Paths.ReleaseRoot) -cne [IO.Path]::GetFullPath((Split-Path -Parent $ReceiptPath))) {
        throw 'repository-recovery-receipt-invalid; preserve the receipt and inspect its version/binding.'
    }
    Assert-RepositoryRecoveryDatabaseMatch $receipt.Binding.TargetDatabase (Get-RepositoryRecoveryTargetState $receipt.Binding.OriginalDatabase)
    if ($receipt.Phase -cne 'Prepared' -and !($receipt.Phase -ceq 'FailedHeld' -and $receipt.FailureAtPhase -ceq 'Prepared') -and
        $null -eq $receipt.Binding.Retained) { throw 'repository-recovery-receipt-invalid: retained proof missing' }
    if ($receipt.Phase -ceq 'Completed' -and (!$receipt.SavedResult -or
        $receipt.ResultHash -cne (Get-RepositoryRecoveryValueHash $receipt.SavedResult))) { throw 'repository-recovery-receipt-invalid: completed result' }
    return $receipt
}

function Save-RepositoryRecoveryReleaseReceipt {
    param([Parameter(Mandatory)][string]$ReceiptPath, [Parameter(Mandatory)]$Receipt)
    Assert-RepositoryRecoveryReceiptPath $ReceiptPath
    if (Test-Path -LiteralPath $ReceiptPath) {
        $prior=Read-RepositoryRecoveryReleaseReceipt $ReceiptPath
        if ($prior.Revision -ne $Receipt.Revision -or $prior.Binding.OperationId -cne $Receipt.Binding.OperationId) {
            throw 'repository-recovery-receipt-ownership-or-revision-changed'
        }
        $before=[ordered]@{}; $after=[ordered]@{}
        foreach ($key in $prior.Binding.Keys) { if ($key -cne 'Retained') { $before[$key]=$prior.Binding.$key } }
        foreach ($key in $Receipt.Binding.Keys) { if ($key -cne 'Retained') { $after[$key]=$Receipt.Binding.$key } }
        if ((Get-RepositoryRecoveryValueHash $before) -cne (Get-RepositoryRecoveryValueHash $after) -or
            ($prior.Phase -cne 'Prepared' -and (Get-RepositoryRecoveryValueHash $prior.Binding.Retained) -cne (Get-RepositoryRecoveryValueHash $Receipt.Binding.Retained))) {
            throw 'repository-recovery-receipt-binding-changed'
        }
    }
    $Receipt.Revision++
    $Receipt.BindingHash=Get-RepositoryRecoveryValueHash $Receipt.Binding
    $Receipt.ResultHash=Get-RepositoryRecoveryValueHash $Receipt.SavedResult
    $Receipt.ObservedAtUtc=[DateTime]::UtcNow.ToString('O')
    Write-HybridRebuildJson -Path $ReceiptPath -Value $Receipt
    [void](Read-RepositoryRecoveryReleaseReceipt $ReceiptPath)
}

function New-RepositoryRecoveryReleaseReceipt {
    param([Parameter(Mandatory)][string]$ReceiptPath, [Parameter(Mandatory)]$Binding)
    if (Test-Path -LiteralPath $ReceiptPath) { throw 'repository-recovery-receipt-already-exists' }
    $receipt=[ordered]@{ Version=1; Revision=0; Phase='Prepared'; Binding=$Binding; BindingHash=$null;
        SavedResult=$null; ResultHash=$null; Failure=$null; FailureAtPhase=$null; Reconciliation=$null; ObservedAtUtc=$null }
    Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
    return $receipt
}

function Invoke-RepositoryRecoveryRelease {
    param([Parameter(Mandatory)][string]$ReceiptPath, [Parameter(Mandatory)]$Binding,
        [Parameter(Mandatory)][Collections.IDictionary]$Ports)
    $receipt=New-RepositoryRecoveryReleaseReceipt $ReceiptPath $Binding
    $held=$false; $companionMutationStarted=$false
    try {
        & $Ports.CreateHold
        & $Ports.AssertHold; $held=$true
        # Original policy/XML and payload identities are already durable. No live
        # payload is touched until task exit and exact IIS/GPU drain are proven.
        if ($Binding.OriginalTask.Enabled) { & $Ports.DisableTask }
        & $Ports.AssertTaskDisabled
        & $Ports.Stop
        & $Ports.AssertOriginals
        Assert-RepositoryRecoveryDatabaseMatch (& $Ports.ReadDatabase) $Binding.OriginalDatabase
        $Binding.Retained=& $Ports.CaptureRetained
        Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        $receipt.Phase='MigrationIntent'; Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        & $Ports.AssertHold; & $Ports.AssertTaskDisabled
        $after=& $Ports.ApplyMigration
        Assert-RepositoryRecoveryDatabaseMatch $after $Binding.TargetDatabase
        $receipt.Phase='MigrationVerified'; Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        & $Ports.AssertHold; & $Ports.AssertTaskDisabled
        & $Ports.BackupCompanion
        $receipt.Phase='PayloadActivationIntent'; Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        $runtime=@{ CompanionMutationStarted=$false }
        $activate={ & $Ports.AssertHold; & $Ports.AssertTaskDisabled
            $runtime.CompanionMutationStarted=$true; & $Ports.Activate }.GetNewClosure()
        try { [void](& $Ports.Swap $activate $Ports.Start $Ports.Validate $Ports.ValidateWebRollback) }
        finally { $companionMutationStarted=$runtime.CompanionMutationStarted }
        & $Ports.AssertHold; & $Ports.AssertTaskDisabled
        & $Ports.Validate
        $receipt.Phase='ValidatedHeld'; Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        $receipt.Phase='HoldReleaseIntent'; Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        & $Ports.ReleaseHold
        & $Ports.PostReleaseProbes
        & $Ports.RestoreTaskPolicy
        & $Ports.AssertCompleted
        $receipt.SavedResult=[ordered]@{ ok=$true; mode='applied'; commit=$Binding.Commit; release_root=$Binding.Paths.ReleaseRoot;
            migration=(Get-RepositoryRecoveryMigrationContract).Target; script_sha256=$Binding.MigrationSha256;
            deployment_validation_hold='released-after-exact-schema-and-retained-state-validation';
            interactive_host_activation='original enabled policy restored; task not triggered'; receipt=$ReceiptPath }
        $receipt.Phase='Completed'; Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        return $receipt.SavedResult
    } catch {
        $failure=$_.Exception.Message; $receipt.FailureAtPhase=$receipt.Phase; $receipt.Failure=$failure
        if ($receipt.Phase -cin @('HoldReleaseIntent','Completed')) {
            # Workers may have written after release. Never restore an incompatible
            # predecessor or replay SQL when activation acknowledgement is uncertain.
            Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
            throw "repository-recovery-activation-uncertain; candidate/schema retained; inspect $ReceiptPath. $failure"
        }
        if (!$held) {
            # A failed hold acquisition grants no authority over another release.
            Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
            throw "repository-recovery-blocked-before-hold; inspect $ReceiptPath. $failure"
        }
        $rollbackFailure=$null
        try {
            & $Ports.AssertHold
            & $Ports.DisableTask; & $Ports.AssertTaskDisabled
            & $Ports.Stop
            if ($companionMutationStarted) { & $Ports.RestoreCompanion }
            & $Ports.AssertOriginals
        } catch { $rollbackFailure=$_.Exception.Message }
        $receipt.Phase='FailedHeld'
        if ($rollbackFailure) { $receipt.Failure+="; rollback unverified: $rollbackFailure" }
        Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
        throw "repository-recovery-failed-held; hold not released, predecessor restart refused; stopped/task/payload verification=$(!$rollbackFailure); inspect $ReceiptPath. $($receipt.Failure)"
    }
}

function Invoke-RepositoryRecoveryReceiptReconciliation {
    param([Parameter(Mandatory)][string]$ReceiptPath, [Parameter(Mandatory)][Collections.IDictionary]$Ports)
    $receipt=Read-RepositoryRecoveryReleaseReceipt $ReceiptPath
    if ($receipt.Phase -ceq 'Completed') {
        Assert-RepositoryRecoveryDatabaseMatch (& $Ports.ReadDatabase) $receipt.Binding.TargetDatabase
        & $Ports.AssertCompleted
        return $receipt.SavedResult
    }
    $phase=if ($receipt.Phase -ceq 'FailedHeld') { $receipt.FailureAtPhase } else { $receipt.Phase }
    if ($phase -cnotin @('MigrationIntent','MigrationVerified')) {
        throw "repository-recovery-unsupported-restart-boundary: $phase; no SQL, payload replay, worker start or hold release permitted."
    }
    & $Ports.AssertHold; & $Ports.AssertTaskDisabled; & $Ports.AssertMigrationLocations
    $current=& $Ports.ReadDatabase
    $target=$receipt.Binding.TargetDatabase; $original=$receipt.Binding.OriginalDatabase
    if ((Get-RepositoryRecoveryValueHash $current.History) -ceq (Get-RepositoryRecoveryValueHash $target.History)) {
        Assert-RepositoryRecoveryDatabaseMatch $current $target
        $outcome='applied'
    } else {
        if ($phase -ceq 'MigrationVerified') { throw 'repository-recovery-database-state-drift: verified migration disappeared' }
        Assert-RepositoryRecoveryDatabaseMatch $current $original
        $outcome='not-applied'
    }
    # Inspect only. A confirmed old state is not permission for another attempt;
    # exact target state is not permission to resume payload activation.
    $receipt.Reconciliation=[ordered]@{ migration=$outcome; held=$true; next_action='review forward continuation of this preserved release' }
    Save-RepositoryRecoveryReleaseReceipt $ReceiptPath $receipt
    return $receipt.Reconciliation
}

Export-ModuleMember -Function New-RepositoryRecoveryReleaseReceipt, Read-RepositoryRecoveryReleaseReceipt,
    Save-RepositoryRecoveryReleaseReceipt, Invoke-RepositoryRecoveryRelease, Invoke-RepositoryRecoveryReceiptReconciliation
