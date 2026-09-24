Set-StrictMode -Version Latest

function Get-CorpusFullTextMigrationContract {
    [pscustomobject]@{
        Baseline = '20260920122758_AddDocumentOcrRequestsAndArtifactMetadata'
        Target = '20260924125920_AddCorpusChunkFullTextIndex'
        UpSha256 = '77825ED4D7ABF4EF2474123050CDB996C09D6F04038735FC34E6C35ECDD1C707'
        DownSha256 = '9D873DB12FBC257438E36039CC31F72AD680BF676492F09115BACA0590DEA9A1'
    }
}

function Test-ExactMigrationHistory([string[]]$Actual, [string[]]$Expected) {
    return [string]::Equals(($Actual -join "`n"), ($Expected -join "`n"), [StringComparison]::Ordinal)
}

function Assert-CorpusFullTextMigrationBaseline {
    param([Parameter(Mandatory)]$DatabaseState)
    $contract = Get-CorpusFullTextMigrationContract
    $history = @($DatabaseState.History)
    if ($history.Count -eq 0 -or $history[-1] -cne $contract.Baseline -or
        $history -ccontains $contract.Target -or $DatabaseState.IndexPresent) {
        throw 'Corpus Full-Text migration requires the exact reviewed baseline and no existing TextChunks Full-Text index.'
    }
    if (!$DatabaseState.FullTextInstalled -or !$DatabaseState.CataloguePresent -or !$DatabaseState.KeyPresent) {
        throw 'Corpus Full-Text migration requires Full-Text Search, the FluxKnowledge catalogue and the unique integer PK_TextChunks key.'
    }
}

function New-CorpusFullTextMigrationState {
    param([Parameter(Mandatory)][string[]]$OriginalHistory)
    return @{
        OriginalHistory = @($OriginalHistory)
        Attempted = $false; Applied = $false
        SchemaRollbackVerified = $true; RollbackVerified = $true
    }
}

function Invoke-CorpusFullTextMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$RunUp)
    # Payload recovery calls StopApplication a second time. Never replay even a partial attempt.
    if ($State.Attempted) { return }
    $before = & $ReadState
    Assert-CorpusFullTextMigrationBaseline $before
    if (!(Test-ExactMigrationHistory $before.History $State.OriginalHistory)) {
        throw 'Migration history changed since preflight.'
    }
    $State.Attempted = $true
    $State.RollbackVerified = $false
    $State.SchemaRollbackVerified = $false
    & $RunUp
    $after = & $ReadState
    $expected = @($State.OriginalHistory) + (Get-CorpusFullTextMigrationContract).Target
    if (!(Test-ExactMigrationHistory $after.History $expected) -or !$after.IndexPresent -or !$after.IndexValid) {
        throw 'Corpus Full-Text migration did not establish the exact reviewed schema and history.'
    }
    $State.Applied = $true
}

function Undo-CorpusFullTextMigrationAttempt {
    param([Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][scriptblock]$ReadState,
        [Parameter(Mandatory)][scriptblock]$RunDown)
    $State.RollbackVerified = $false
    if (!$State.Attempted) { return }
    $State.SchemaRollbackVerified = $false
    $before = & $ReadState
    $applied = @($State.OriginalHistory) + (Get-CorpusFullTextMigrationContract).Target
    if ((!(Test-ExactMigrationHistory $before.History $State.OriginalHistory) -and
         !(Test-ExactMigrationHistory $before.History $applied)) -or
        ($before.IndexPresent -and !$before.IndexValid)) {
        throw 'Refusing to roll back an unrecognised schema or Full-Text index.'
    }
    & $RunDown
    $after = & $ReadState
    if (!(Test-ExactMigrationHistory $after.History $State.OriginalHistory) -or $after.IndexPresent) {
        throw 'Corpus Full-Text schema rollback could not be verified.'
    }
    $State.Applied = $false
    $State.SchemaRollbackVerified = $true
}

function Confirm-CorpusFullTextMigrationRollback {
    param([Parameter(Mandatory)][hashtable]$State,
        [Parameter(Mandatory)][scriptblock]$ValidatePriorApplication)
    $State.RollbackVerified = $false
    if (!$State.SchemaRollbackVerified) { throw 'Schema rollback must be verified before checking the prior application.' }
    & $ValidatePriorApplication
    $State.RollbackVerified = $true
}

Export-ModuleMember -Function Get-CorpusFullTextMigrationContract, Assert-CorpusFullTextMigrationBaseline,
    New-CorpusFullTextMigrationState, Invoke-CorpusFullTextMigrationAttempt,
    Undo-CorpusFullTextMigrationAttempt, Confirm-CorpusFullTextMigrationRollback
