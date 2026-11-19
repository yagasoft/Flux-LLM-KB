param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater parse failed.'}
foreach($name in @('Assert-NotReparsePoint','New-DeploymentValidationHold','Stop-JournalFailureApplication')){
    $nodes=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($nodes.Count -eq 1){. ([scriptblock]::Create($nodes[0].Extent.Text))}
    elseif($name -ne 'Stop-JournalFailureApplication'){throw 'Required actual hold entry missing.'}
}
$swap=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap'},$true)|
    Where-Object {$_.Extent.Text.Contains('-ApplicationRoot $CanonicalDeployRoot')})
if($swap.Count -ne 1){throw 'Actual swap dispatch not unique.'}
$elements=@($swap[0].CommandElements)
$stop=$null
for($index=0;$index -lt $elements.Count-1;$index++){
    if($elements[$index] -is [Management.Automation.Language.CommandParameterAst] -and $elements[$index].ParameterName -ceq 'StopApplication'){
        $stop=[scriptblock]::Create($elements[$index+1].ScriptBlock.Extent.Text.TrimStart('{').TrimEnd('}'))
    }
}
$recovery=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -ceq '$interactiveHostMutationStarted'},$true))
if(!$stop -or $recovery.Count -ne 1){throw 'Actual failure composition not unique.'}
$root=Join-Path $env:TEMP ('FluxInitialDrain_'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try {
    $app=Join-Path $root 'App';$candidate=Join-Path $root 'Candidate';$runtime=Join-Path $root 'Runtime'
    foreach($path in @($app,$candidate,$runtime)){[void][IO.Directory]::CreateDirectory($path)}
    [IO.File]::WriteAllText((Join-Path $app 'payload.txt'),'original');[IO.File]::WriteAllText((Join-Path $candidate 'payload.txt'),'candidate')
    $box=@{Stops=0;WorkerAlive=$true;CompanionDisabled=$true}
    function Stop-HybridIisAfterGpuDrain {param([switch]$PassThru)$box.Stops++;throw 'synthetic-uncertain-capacity'}
    function Assert-DeploymentSourceJournalPredecessorRestorationAllowed {throw 'journal-predecessor-restoration-prohibited'}
    function Disable-ScheduledTask {param($TaskName,$ErrorAction)$box.CompanionDisabled=$true}
    function Wait-InteractiveHostStopped {param($TaskName,$TimeoutSeconds)if(!$box.CompanionDisabled){throw 'Companion is still active.'}}
    $ApplyResumableSourceDeletionCleanupMigration=$false;$ApplyTypedSchemaMigration=$false;$EnsureSourceDeletionPendingQueueIndex=$false
    $ApplyVectorPublicationMetadataMigration=$false;$SourceDeletionAcceptanceMaintenanceReceipt='';$sourceJournalMode=$false
    $deploymentValidation=@{HoldCreated=$false;JournalRecoveryRequired=$false;Baseline=$null;ApplicationStopFailed=$false}
    $ValidationHoldPath=Join-Path $runtime 'hold.json';$releaseId='20261118T010000Z-aaaaaaaaaaaa';$releaseRoot=Join-Path $root 'Recovery'
    $InteractiveHostTaskName='isolated';$ReadinessTimeoutSeconds=1;$interactiveHostMutationStarted=$true
    $deploymentFailure=$null
    try {
        Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $app -CandidateRoot $candidate -PreviousRoot (Join-Path $root 'Previous') -FailedRoot (Join-Path $root 'Failed') `
            -StopApplication $stop -ActivateCandidate {throw 'An unproved stop activated payload.'} -StartApplication {throw 'An unproved stop started work.'} -ValidateApplication {}|Out-Null
    }catch{$deploymentFailure=$_}
    if(!$deploymentFailure -or $box.Stops -ne 1){throw 'Initial drain failure did not preserve the swap boundary.'}
    $failed=$false
    try{& ([scriptblock]::Create($recovery[0].Extent.Text))}catch{$failed=$true}
    if(!$failed -or $box.Stops -ne 1 -or !$box.WorkerAlive -or !$box.CompanionDisabled){throw 'Recovery repeated an unproved initial drain or changed its worker.'}
    if(!(Test-Path -LiteralPath $ValidationHoldPath) -or (Get-Content -Raw -LiteralPath $ValidationHoldPath|ConvertFrom-Json) -cne $releaseId){throw 'Failed recovery lost its owned hold.'}
    if([IO.File]::ReadAllText((Join-Path $app 'payload.txt')) -cne 'original'){throw 'Unproved stop changed the Web payload.'}
    'Actual initial stop and protected journal failure retain the worker, hold and payload without a second drain.'
}finally{if([IO.Directory]::Exists($root)){[IO.Directory]::Delete($root,$true)}}
