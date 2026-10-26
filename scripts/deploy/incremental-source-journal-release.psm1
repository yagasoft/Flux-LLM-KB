Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'incremental-source-journal-migration.psm1') -ErrorAction Stop

# Keep the irreversible ordering small and exercisable independently of IIS.
# Native callbacks must prove their own target, stop and exact payload bindings.
function Invoke-SourceJournalReleaseActivation {
    param([hashtable]$MigrationState,[Parameter(Mandatory)][string]$ExpectedDatabaseIdentity,
        [Parameter(Mandatory)][hashtable]$Ports,[switch]$Disable)
    $required=@('AssertStopped','AssertPayloadSet','ApplyConfiguration','ReadDatabaseState')+
        $(if($Disable){@('RetireVerification')}else{@('UpdateHelper','RunMigration')})
    foreach($name in $required){
        if(!$Ports.ContainsKey($name) -or $Ports[$name] -isnot [scriptblock]){throw 'journal-release-callback-missing'}
    }
    if([string]::IsNullOrWhiteSpace($ExpectedDatabaseIdentity) -or
        (!$Disable -and (!$MigrationState -or $MigrationState.Identity -cne $ExpectedDatabaseIdentity))){
        throw 'journal-release-database-binding-required'
    }
    &$Ports.AssertStopped
    $before=&$Ports.ReadDatabaseState
    Assert-SourceJournalSchema -DatabaseState $before -AllowBaseline:(!$Disable)
    if($before.Identity -cne $ExpectedDatabaseIdentity){throw 'journal-release-database-incarnation-changed'}
    &$Ports.AssertPayloadSet ([bool]$Disable)
    if(!$Disable){
        &$Ports.UpdateHelper
        &$Ports.AssertStopped
        &$Ports.AssertPayloadSet $true
    }
    &$Ports.ApplyConfiguration
    &$Ports.AssertStopped
    if($Disable){
        &$Ports.RetireVerification
    }else{
        Invoke-SourceJournalMigrationAttempt -State $MigrationState -ReadState $Ports.ReadDatabaseState `
            -AssertStopped $Ports.AssertStopped -RunUp $Ports.RunMigration
    }
    &$Ports.AssertStopped
    &$Ports.AssertPayloadSet $true
    $after=&$Ports.ReadDatabaseState
    Assert-SourceJournalSchema -DatabaseState $after
    if($after.Identity -cne $ExpectedDatabaseIdentity){throw 'journal-release-database-incarnation-changed'}
}

Export-ModuleMember -Function Invoke-SourceJournalReleaseActivation
