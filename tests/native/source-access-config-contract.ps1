[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
$fixture=[IO.Directory]::CreateTempSubdirectory('FluxSourceAccessConfig_').FullName
try {
    $path=Join-Path $fixture 'settings.json'
    $original=[Text.Encoding]::UTF8.GetBytes('{"ConnectionStrings":{"Unchanged":"private fixture"},"SourceAccess":{"Other":42},"Worker":{"Enabled":true}}')
    [IO.File]::WriteAllBytes($path,$original)
    $beforeAcl=(Get-Acl -LiteralPath $path).GetSecurityDescriptorSddlForm('All')
    $change=New-SourceAccessConfigurationChange -Path $path -PipeName 'FluxKnowledge.SourceAccess.isolated-config'
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToHexString($original)) { throw 'Preparation changed configuration.' }
    Set-SourceAccessConfigurationChange -Change $change
    Assert-SourceAccessConfigurationChange -Change $change
    $active=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if (!$active.SourceAccess.Enabled -or $active.SourceAccess.Other -ne 42 -or $active.ConnectionStrings.Unchanged -cne 'private fixture') { throw 'Activation changed unrelated configuration.' }
    if ((Get-Acl -LiteralPath $path).GetSecurityDescriptorSddlForm('All') -cne $beforeAcl) { throw 'Activation changed configuration permissions.' }
    Restore-SourceAccessConfigurationChange -Change $change
    if (!$change.RollbackVerified -or [Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToHexString($original)) { throw 'Rollback did not restore exact bytes.' }
    $disable=New-SourceAccessConfigurationChange -Path $path -Disable
    Set-SourceAccessConfigurationChange -Change $disable
    if ((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).SourceAccess.Enabled -ne $false) { throw 'Disable failed.' }
    Restore-SourceAccessConfigurationChange -Change $disable
    [IO.File]::AppendAllText($path,' ')
    $refused=$false; try { Set-SourceAccessConfigurationChange -Change $change } catch { $refused=$true }
    if (!$refused) { throw 'Configuration drift was overwritten.' }
    [IO.File]::WriteAllBytes($path,$original)
    Set-SourceAccessConfigurationChange -Change $change
    [IO.File]::AppendAllText($path,' ')
    $refused=$false; try { Restore-SourceAccessConfigurationChange -Change $change } catch { $refused=$true }
    if (!$refused -or $change.RollbackVerified) { throw 'Rollback drift was not retained for recovery.' }
    $refused=$false; try { New-SourceAccessConfigurationChange -Path $path -PipeName '..\escape' | Out-Null } catch { $refused=$true }
    if (!$refused) { throw 'Invalid pipe name was accepted.' }
    $withBom=[byte[]](@(0xef,0xbb,0xbf)+$original)
    [IO.File]::WriteAllBytes($path,$withBom)
    $bomChange=New-SourceAccessConfigurationChange -Path $path -Disable
    Set-SourceAccessConfigurationChange -Change $bomChange
    Restore-SourceAccessConfigurationChange -Change $bomChange
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToHexString($withBom)) { throw 'BOM rollback changed original bytes.' }

    # Execute the actual updater callbacks with isolated effects, not copied wiring.
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw 'Updater does not parse.' }
    $swap=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Invoke-IncrementalApplicationPayloadSwap'},$true))[-1]
    $callbacks=@{}
    for ($index=0;$index -lt $swap.CommandElements.Count-1;$index++) {
        $element=$swap.CommandElements[$index]
        if ($element -is [Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -in @('ActivateCandidate','PrepareRollbackApplication','ValidateRollbackApplication')) {
            $callbacks[$element.ParameterName]=$swap.CommandElements[$index+1].ScriptBlock.GetScriptBlock()
        }
    }
    function Invoke-CandidatePayloadActivation { param($CandidateRoot,$ApplicationRoot) }
    function global:Invoke-RequiredLoopbackProbes { param($Origin,$TimeoutSeconds) }
    function global:Get-RetainedPipelineStateBaseline { return 1 }
    function global:Assert-RetainedPipelineStateUnchanged { param($Baseline,$Current) }
    $sourceAccessChange=New-SourceAccessConfigurationChange -Path $path -PipeName 'FluxKnowledge.SourceAccess.isolated-config'
    $candidateRoot=$fixture; $CanonicalDeployRoot=$fixture; $EnableUnattendedDiscovery=$false
    $ApplyResumableSourceDeletionCleanupMigration=$false; $RecoverStoppedPool=$false; $ApplyCodeDisclosureProofMigration=$false
    $ApplyCorpusChunkFullTextMigration=$false; $ApplyMigrations=$false; $DeferReadinessForScopedRemediation=$false
    $hostingChange=@{Attempted=$false}; $deploymentValidation=@{Baseline=1}; $loopbackOrigin=@{Origin='unused'}; $ReadinessTimeoutSeconds=1
    & $callbacks.ActivateCandidate
    Assert-SourceAccessConfigurationChange -Change $sourceAccessChange
    & $callbacks.PrepareRollbackApplication
    & $callbacks.ValidateRollbackApplication
    if (!$deploymentValidation.PayloadRollbackVerified -or !$sourceAccessChange.RollbackVerified) { throw 'Actual updater callbacks did not verify configuration rollback.' }
    Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-iis-payload-swap.psm1') -Force
    $gate=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Test-IncrementalRollbackHoldRelease'},$true)
    . ([scriptblock]::Create($gate.Extent.Text))
    $outer=$swap.Parent
    while ($outer -isnot [Management.Automation.Language.TryStatementAst]) { $outer=$outer.Parent }
    $finish=[scriptblock]::Create(($outer.Finally.Statements.Extent.Text -join "`n"))
    function global:Invoke-CandidatePayloadActivation { param($CandidateRoot,$ApplicationRoot)
        $null=[IO.Directory]::CreateDirectory($ApplicationRoot)
        [IO.File]::WriteAllText((Join-Path $ApplicationRoot 'version'),'candidate')
    }
    function Remove-DeploymentValidationHold { param($Path,$ReleaseId)
        if ($Path -cne $ValidationHoldPath -or $ReleaseId -cne 'isolated') { throw 'Unexpected fixture hold.' }
        [IO.File]::Delete($Path)
    }
    foreach ($failure in @('validation','foreign','partial')) {
        [IO.File]::WriteAllBytes($path,$original)
        $sourceAccessChange=New-SourceAccessConfigurationChange -Path $path -PipeName 'FluxKnowledge.SourceAccess.isolated-config'
        $caseRoot=Join-Path $fixture $failure; $null=[IO.Directory]::CreateDirectory($caseRoot)
        $CanonicalDeployRoot=Join-Path $caseRoot 'live'; $candidateRoot=Join-Path $caseRoot 'candidate'
        $null=[IO.Directory]::CreateDirectory($CanonicalDeployRoot); $null=[IO.Directory]::CreateDirectory($candidateRoot)
        [IO.File]::WriteAllText((Join-Path $CanonicalDeployRoot 'version'),'previous')
        $ValidationHoldPath=Join-Path $caseRoot 'hold.json'; [IO.File]::WriteAllText($ValidationHoldPath,'isolated')
        $deploymentValidation=@{HoldCreated=$true;PayloadRollbackVerified=$false;RollbackVerified=$true;Baseline=1}
        $hostingChange=@{Attempted=$false;RollbackVerified=$true}
        $calls=@{Starts=0}; $refused=$false
        try {
            $null=Invoke-IncrementalApplicationPayloadSwap -ApplicationRoot $CanonicalDeployRoot -CandidateRoot $candidateRoot `
                -PreviousRoot (Join-Path $caseRoot 'previous') -FailedRoot (Join-Path $caseRoot 'failed') `
                -StopApplication {} -ActivateCandidate ($callbacks.ActivateCandidate.GetNewClosure()) -StartApplication {
                    $calls.Starts++
                    if ($calls.Starts -eq 1) { Assert-SourceAccessConfigurationChange -Change $sourceAccessChange }
                    else { Assert-SourceAccessConfigurationChange -Change $sourceAccessChange -Original }
                } -ValidateApplication {
                    if ($failure -ceq 'foreign') { [IO.File]::AppendAllText($path,' ') }
                    if ($failure -ceq 'partial') { [IO.File]::WriteAllText($path,'{"partial":') }
                    throw 'Injected candidate validation failure.'
                } -PrepareRollbackApplication ($callbacks.PrepareRollbackApplication.GetNewClosure()) -ValidateRollbackApplication ($callbacks.ValidateRollbackApplication.GetNewClosure())
        } catch { $refused=$true; $swapFailure=$_.Exception.Message }
        if (!$refused -or [IO.File]::ReadAllText((Join-Path $CanonicalDeployRoot 'version')) -cne 'previous') { throw 'Disposable swap did not preserve the predecessor.' }
        if ($failure -ceq 'validation') {
            if ($calls.Starts -ne 2 -or !$sourceAccessChange.RollbackVerified) { throw "Configuration was not restored before predecessor startup: $swapFailure" }
        } elseif ($calls.Starts -ne 1 -or $sourceAccessChange.RollbackVerified) { throw 'Uncertain configuration allowed predecessor startup.' }
        # Prove configuration alone retains the hold even if payload restoration is verified.
        $deploymentValidation.PayloadRollbackVerified=$true
        $holdReleaseAttempted=$false; $interactiveHostMutationStarted=$false; $interactiveHostRollbackVerified=$true; $releaseId='isolated'
        $mutex=[Threading.Mutex]::new($false); $leaseAcquired=$mutex.WaitOne(0)
        . $finish
        if ((Test-Path -LiteralPath $ValidationHoldPath) -ne ($failure -cne 'validation')) { throw 'The actual finalisation gate mishandled uncertain configuration.' }
    }
    [pscustomobject]@{passed=13;production_calls=0} | ConvertTo-Json
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped temporary root.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
