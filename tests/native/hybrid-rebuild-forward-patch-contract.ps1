[CmdletBinding()]
param([string]$SourceRoot='', [string]$Mode='patch')
$ErrorActionPreference='Stop'
if (-not $SourceRoot) { $SourceRoot=Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) }
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -Force
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Patch updater parse failed.' }
foreach($name in @('Invoke-HybridPassageIisPatch','Assert-NotReparsePoint')) {
    $definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($definition.Count -ne 1){throw "Patch function missing: $name"}
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
function Get-DeploymentSqlConnectionString { 'Data Source=localhost;Initial Catalog=FluxKnowledge_PatchContract;Integrated Security=True' }
function Get-AppliedMigrationIds { @($contract.Baseline)+@($contract.Suffix) }
function Get-HybridPreservedInputFingerprint { if($evidence.InputFingerprint){$evidence.InputFingerprint}else{'unchanged-public-inputs'} }
function Get-HybridPatchBinding { param($RecoveryRoot,$ReleaseId,$ConnectionString) $predecessor }
function Assert-HybridPatchPacket { param($RecoveryRoot,$Binding,$ConnectionString)
    if($Binding.ReleaseId -cne $predecessor.ReleaseId -or (Get-FileHash -LiteralPath $oldJournal).Hash -cne $oldJournalHash){throw 'changed-predecessor'}
}
function Test-ApplicationPayload { param($Path) if(-not(Test-Path -LiteralPath (Join-Path $Path 'marker'))){throw 'payload-missing'} }
function Assert-ApplicationPayloadReadAccess { param($Path) }
function Get-WebAppPoolState { param($Name) @{Value=$evidence.PoolState} }
function Start-WebAppPool { param($Name) $evidence.PoolState='Started';$evidence.Starts++ }
function Wait-IisAppPoolState { param($Name,$ExpectedState,$TimeoutSeconds) if($evidence.PoolState -cne $ExpectedState){throw 'pool-state-mismatch'} }
function Disable-ScheduledTask { param($TaskName) $evidence.Disabled++ }
function Enable-ScheduledTask { param($TaskName)
    if($evidence.Mode -eq 'after-release' -and -not $evidence.EnableFailed){$evidence.EnableFailed=$true;throw 'injected-post-release-enable-failure'}
    $evidence.Enabled++
}
function Wait-InteractiveHostStopped { param($TaskName,$TimeoutSeconds) }
function Stop-HybridIisAfterGpuDrain {
    if($evidence.Mode -eq 'before-drain'){throw 'injected-drain-failure'}
    if($evidence.Mode -eq 'stopped-owner' -and $evidence.Starts -lt 1){throw 'dead-owner-recovery-never-started'}
    $evidence.PoolState='Stopped';$evidence.Drains++
}
function Assert-CanonicalPath { param($RequestedPath,$ExpectedPath,$Message)
    if($RequestedPath -cne $CanonicalDeployRoot -or $ExpectedPath -cne 'I:\FluxKnowledge\App'){throw 'unexpected-canonical-patch-target'}
}
function Invoke-CandidatePayloadActivation { param($CandidateRoot,$ApplicationRoot)
    New-Item -ItemType Directory -Path $ApplicationRoot | Out-Null
    if($evidence.Mode -eq 'empty-copy'){throw 'injected-empty-copy'}
    $text=Get-Content -LiteralPath (Join-Path $CandidateRoot 'marker') -Raw
    if($evidence.Mode -in @('partial-copy','preallocated-copy','same-metadata-copy','candidate-junction','previous-junction')) {
        $incomplete=if($evidence.Mode -in @('partial-copy','candidate-junction','previous-junction')){$text.Substring(0,3)}else{('0' * $text.Length)}
        $incompletePath=Join-Path $ApplicationRoot 'marker'
        Set-Content -LiteralPath $incompletePath -Value $incomplete -NoNewline
        if($evidence.Mode -eq 'same-metadata-copy'){
            (Get-Item -LiteralPath $incompletePath).LastWriteTimeUtc=(Get-Item -LiteralPath (Join-Path $CandidateRoot 'marker')).LastWriteTimeUtc
        }
        throw 'injected-incomplete-copy'
    }
    Copy-Item -LiteralPath (Join-Path $CandidateRoot 'marker') -Destination (Join-Path $ApplicationRoot 'marker')
}
function Invoke-FixedLoopbackProbe { param($Uri,$TimeoutSeconds)
    if($evidence.Mode -eq 'after-activation'){throw 'injected-probe-failure'}
    [IO.MemoryStream]::new()
}
function Invoke-RequiredLoopbackProbes { param($Origin,$TimeoutSeconds)
    if(-not(Test-Path -LiteralPath $ValidationHoldPath) -and -not $evidence.Finished){throw 'hold-released-before-finish'}
}
function Invoke-HybridRebuildOperator { param($OperatorRoot,$ConnectionString,$Arguments)
    switch($Arguments[0]){
        'verify-models' { @{Verified=$true;Offline=$true} }
        'status' { @{Committed=$true;Completed=$evidence.Finished;ManifestHash=('b'*64);TargetEpoch=$oldEpoch;
            PendingItems=$(if($evidence.Mode -eq 'timeout'){1}else{0});RunningItems=0;FailedEmbeddingJobs=0;FailedPublishJobs=0} }
        'finish' { $evidence.Finished=$true; @{Completed=$true} }
        default {throw 'unexpected-operator-command'}
    }
}
function dotnet {
    $destination=$args[([Array]::IndexOf($args,'-o'))+1]
    if(-not $destination){throw 'publish-output-missing'}
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $destination 'marker') -Value 'new-corrected-payload' -NoNewline
    $global:LASTEXITCODE=0
}
$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temporary=[IO.Path]::GetFullPath((Join-Path $parent ('HybridPatch-'+[Guid]::NewGuid().ToString('N'))))
if(-not $temporary.StartsWith($parent+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'unsafe-test-root'}
try {
    $contract=Get-HybridPassageMigrationContract
    foreach($case in @('review-mismatch','success','before-drain','partial-copy','empty-copy','preallocated-copy','same-metadata-copy','candidate-junction','previous-junction','stopped-owner','after-activation','timeout','after-release')) {
        $CanonicalLiveRoot=Join-Path $temporary $case
        $CanonicalDeployRoot=Join-Path $CanonicalLiveRoot 'App'
        $IncrementalRecoveryRoot=Join-Path $CanonicalLiveRoot 'Recovery/IncrementalUpdates'
        $ValidationHoldPath=Join-Path $CanonicalLiveRoot 'Runtime/deployment-validation-hold.json'
        $InteractiveHostTaskName='Synthetic';$SiteName='Synthetic';$ReadinessTimeoutSeconds=5
        $RebuildTimeoutSeconds=if($case -eq 'timeout'){0}else{5}
        $loopbackOrigin=@{Origin='http://127.0.0.1:5137'}
        $oldRelease='20260927T000000Z-aaaaaaaaaaaa-hybrid'
        $oldOperation=[Guid]::NewGuid().ToString('D');$oldEpoch=[Guid]::NewGuid().ToString('D')
        $oldRoot=Join-Path $IncrementalRecoveryRoot $oldRelease
        foreach($path in @($CanonicalDeployRoot,(Join-Path $CanonicalLiveRoot 'Config'),(Split-Path $ValidationHoldPath -Parent),$oldRoot)){
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }
        Set-Content -LiteralPath (Join-Path $CanonicalDeployRoot 'marker') -Value 'old-compatible-payload' -NoNewline
        Set-Content -LiteralPath (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json') -Value '{}' -NoNewline
        $oldJournal=Join-Path $oldRoot 'hybrid-state.json'; Set-Content -LiteralPath $oldJournal -Value 'immutable-predecessor' -NoNewline
        $oldJournalHash=(Get-FileHash -LiteralPath $oldJournal).Hash
        Write-HybridRebuildJson -Path $ValidationHoldPath -Value $oldRelease
        $predecessor=@{ReleaseId=$oldRelease;OperationId=$oldOperation;TargetEpoch=$oldEpoch;ManifestHash=('b'*64)
            OriginalHistory=@($contract.Baseline);CandidateHash=(Get-HybridPayloadFingerprint $CanonicalDeployRoot)
            ActivatedConfigHash=(Get-FileHash -LiteralPath (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json')).Hash
            PreservedInputFingerprint='unchanged-public-inputs';InteractiveHostWasEnabled=$true;JournalHash=$oldJournalHash}
        $evidence=@{Mode=$case;PoolState='Started';Starts=0;Disabled=0;Enabled=0;Drains=0;Finished=$false}
        if($case -eq 'stopped-owner'){$evidence.PoolState='Stopped'}
        $reviewRoot=Join-Path $CanonicalLiveRoot 'reviewed-candidate';New-Item -ItemType Directory -Path $reviewRoot | Out-Null
        Set-Content -LiteralPath (Join-Path $reviewRoot 'marker') -Value 'new-corrected-payload' -NoNewline
        $expectedHash=Get-HybridPayloadFingerprint $reviewRoot
        if($case -eq 'review-mismatch'){$expectedHash='F'*64}
        $failure=$null
        try { Invoke-HybridPassageIisPatch -SourceRoot $SourceRoot -Commit ('e'*40) -PredecessorRelease $oldRelease -ResumeRelease '' `
            -ExpectedCandidateHash $expectedHash -ExpectedOperatorHash $expectedHash | Out-Null }
        catch { $failure=$_.Exception.Message }
        if($case -eq 'review-mismatch'){
            if(-not $failure -or $evidence.Drains -ne 0 -or (Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $predecessor.CandidateHash){throw 'unreviewed-payload-reached-activation'}
            continue
        }
        $patchRoot=@(Get-ChildItem -LiteralPath $IncrementalRecoveryRoot -Directory | Where-Object Name -like '*-hybridpatch')
        if($patchRoot.Count -ne 1){throw "patch packet not created: $case ($failure)"}
        $patchRelease=$patchRoot[0].Name
        $binding=Get-Content -LiteralPath (Join-Path $patchRoot[0].FullName 'patch-binding.json') -Raw | ConvertFrom-Json -AsHashtable
        if($binding.OperationId -cne $oldOperation -or $binding.TargetEpoch -cne $oldEpoch){throw 'operation-or-epoch-changed'}
        if($case -in @('success','stopped-owner')){
            if($failure -or (Test-Path -LiteralPath $ValidationHoldPath) -or -not $evidence.Finished -or $evidence.Enabled -ne 1){throw "success-case-failed:$failure"}
            if($case -eq 'stopped-owner' -and ($evidence.Starts -lt 2 -or $evidence.Drains -ne 1)){throw 'dead-owner-recovery-not-proven'}
        }
        else {
            if(-not $failure){throw "injected-case-did-not-fail:$case"}
            if($case -eq 'after-release') {
                if(Test-Path -LiteralPath $ValidationHoldPath){throw 'terminal-hold-was-not-released'}
                $evidence.InputFingerprint='legitimate-later-ingress'
            }
            else {
                $hold=Get-Content -LiteralPath $ValidationHoldPath -Raw | ConvertFrom-Json -AsHashtable
                $expected=if($case -in @('before-drain','partial-copy','empty-copy','preallocated-copy','same-metadata-copy','candidate-junction','previous-junction')){$oldRelease}else{$patchRelease}
                if($hold -isnot [string] -or $hold -cne $expected){throw "wrong-hold-owner:$case"}
            }
            if($evidence.Enabled){throw "premature-intake:$case"}
            if($case -in @('partial-copy','empty-copy','preallocated-copy','same-metadata-copy','candidate-junction','previous-junction')){
                $progress=Get-Content -LiteralPath (Join-Path $patchRoot[0].FullName 'patch-progress.json') -Raw | ConvertFrom-Json -AsHashtable
                if(-not $progress.ActivationIntent){throw 'activation-intent-not-durable'}
            }
            if($case -in @('candidate-junction','previous-junction')){
                $rootName=if($case -eq 'candidate-junction'){'candidate'}else{'previous'}
                $root=Join-Path $patchRoot[0].FullName $rootName
                $saved=Join-Path $patchRoot[0].FullName ($rootName+'.saved')
                Move-Item -LiteralPath $root -Destination $saved
                New-Item -ItemType Junction -Path $root -Target $saved | Out-Null
                $unsafeFailure=$null
                try { Invoke-HybridPassageIisPatch -SourceRoot $SourceRoot -Commit ('e'*40) -PredecessorRelease '' -ResumeRelease $patchRelease | Out-Null }
                catch { $unsafeFailure=$_.Exception.Message }
                if(-not $unsafeFailure -or $unsafeFailure -notmatch 'hybrid-patch-payload-root-unsafe'){throw "root-junction-not-refused:$case ($unsafeFailure)"}
                Remove-Item -LiteralPath $root -Force
                if(-not(Test-Path -LiteralPath $saved -PathType Container)){throw 'junction-target-was-removed'}
                Move-Item -LiteralPath $saved -Destination $root
            }
            $evidence.Mode='resume-success';$RebuildTimeoutSeconds=5
            Invoke-HybridPassageIisPatch -SourceRoot $SourceRoot -Commit ('e'*40) -PredecessorRelease '' -ResumeRelease $patchRelease | Out-Null
            if(Test-Path -LiteralPath $ValidationHoldPath){throw "resume-retained-hold:$case"}
            if($evidence.Enabled -ne 1){throw "intake-preference-lost:$case"}
        }
        if((Get-FileHash -LiteralPath $oldJournal).Hash -cne $oldJournalHash){throw 'predecessor-journal-changed'}
        if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $binding.CandidateHash){throw 'new-payload-mismatch'}
    }
    'Hybrid forward-patch contract passed: same operation/epoch, pre-drain refusal, interrupted copy replay, post-activation refusal, timeout replay, immutable predecessor and controlled intake.'
}
finally { if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force} }
