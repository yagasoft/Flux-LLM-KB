param([string]$SourceRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-passage-window-eligibility-migration.psm1') -Force
$contract = Get-PassageWindowEligibilityMigrationContract -MediaTranscription
if ($contract.Baseline -cne '20261118225053_BindCodexNotesToWorkspace' -or
    $contract.Target -cne '20261119125051_AddMediaTranscriptionGpuRequests' -or $contract.PriorHistoryCount -ne 82) {
    throw 'Expected the exact schema 82 to 83 migration.'
}
foreach ($direction in @('up','down')) {
    $path = Join-Path $SourceRoot ("scripts/deploy/media-transcription-gpu-{0}.sql" -f $direction)
    if ((Get-FileHash -LiteralPath $path).Hash -cne $contract.($direction + 'Sha256')) { throw 'Reviewed migration SQL differs.' }
}

& (Join-Path $PSScriptRoot 'passage-window-eligibility-updater.ps1') -SourceRoot $SourceRoot -MediaTranscription
& (Join-Path $PSScriptRoot 'passage-window-pre-ddl-abort.ps1') -SourceRoot $SourceRoot -MediaTranscription
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-access-config.psm1') -Force
. (Join-Path $SourceRoot 'scripts/deploy/incremental-media-transcription-config.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('flux-media-options-'+[Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($fixture)
$settings=Join-Path $fixture 'settings.json'
$options=Join-Path $fixture 'options.json'
$backup=Join-Path $fixture 'settings.previous'
try {
    [IO.File]::WriteAllText($settings,'{"Preserved":true}')
    $cutoff='2026-11-19T00:00:00Z'
    $approved=@{SourceRootIds=@([Guid]::NewGuid());AfterUtc=$cutoff;CatchUpRevisionIds=@([Guid]::NewGuid(),[Guid]::NewGuid())}
    [IO.File]::WriteAllText($options,($approved|ConvertTo-Json))
    $hash=(Get-FileHash $options).Hash
    $change=New-MediaTranscriptionConfigurationChange $settings $options $hash
    $approvedBefore=$change.BeforeHash
    $approvedAfter=$change.AfterHash
    Assert-MediaTranscriptionConfigurationBinding $change $approvedBefore $approvedAfter
    foreach ($digests in @(@(('F'*64),$approvedAfter),@($approvedBefore,('E'*64)))) {
        $rejected=$false
        try { Assert-MediaTranscriptionConfigurationBinding $change $digests[0] $digests[1] } catch { $rejected=$true }
        if (!$rejected) { throw 'An unreviewed configuration hash was accepted.' }
    }
    [IO.File]::AppendAllText($settings,' ')
    $drift=New-MediaTranscriptionConfigurationChange $settings $options $hash
    $rejected=$false
    try { Assert-MediaTranscriptionConfigurationBinding $drift $approvedBefore $approvedAfter } catch { $rejected=$true }
    if (!$rejected -or $drift.Attempted -or (Test-Path -LiteralPath $backup)) { throw 'A pre-Apply configuration drift was admitted.' }
    [IO.File]::WriteAllBytes($settings,$change.Before)
    Set-MediaTranscriptionConfigurationChange $change $backup
    $result=ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes $settings)
    if (!$result.Preserved -or $result.MediaTranscription.AfterUtc -cne $cutoff -or $result.MediaTranscription.CatchUpRevisionIds.Count -ne 2) { throw 'Scope or unrelated setting changed.' }
    Restore-MediaTranscriptionConfigurationChange $change $backup
    Assert-SourceAccessConfigurationChange $change -Original
    foreach ($variant in @('wrong-hash','duplicate','unexpected','non-utc','one-catch-up')) {
        $bad=$approved.Clone()
        switch ($variant) {
            'duplicate' {$bad.SourceRootIds=@($bad.SourceRootIds[0],$bad.SourceRootIds[0])}
            'unexpected' {$bad.Unapproved=$true}
            'non-utc' {$bad.AfterUtc='2026-11-19T00:00:00+01:00'}
            'one-catch-up' {$bad.CatchUpRevisionIds=@($bad.CatchUpRevisionIds[0])}
        }
        [IO.File]::WriteAllText($options,($bad|ConvertTo-Json))
        $digest=if ($variant -eq 'wrong-hash') { 'A'*64 } else { (Get-FileHash $options).Hash }
        $rejected=$false
        try { $null=New-MediaTranscriptionConfigurationChange $settings $options $digest } catch { $rejected=$true }
        if (!$rejected) { throw "Unexpected acceptance for $variant." }
    }
} finally {
    foreach($file in @($settings,$options,$backup,($backup+'.candidate'))) { if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) } }
    [IO.Directory]::Delete($fixture)
}

# Exercise the actions behind the last admission fence with a disposable hold.
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/update-native-iis-incremental.ps1'),[ref]$tokens,[ref]$errors)
foreach ($name in @('Start-IndexCompatibleWebAppPool','Remove-DeploymentValidationHold','Enable-IndexCompatibleOutlookTask')) {
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
    . ([scriptblock]::Create('function '+$name+' '+$node.Body.Extent.Text))
}
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-hybrid-passage-rebuild.psm1') -DisableNameChecking
$ValidationHoldPath=Join-Path ([IO.Path]::GetTempPath()) ('typed-hold-'+[Guid]::NewGuid().ToString('N'))
$release='owned-release'; $script:starts=0; $script:enables=0
function Start-WebAppPool {param($Name) $script:starts++}
function Enable-ScheduledTask {param($TaskName,$TaskPath,$ErrorAction) $script:enables++}
function Invoke-DeploymentIndexAdmission {param($Roles,$Action) if ($changeDuringAdmission) { & $changeDuringAdmission }; & $Action }
function Expect-Refusal([scriptblock]$Action) { try { & $Action } catch { return }; throw 'Expected ownership refusal.' }
try {
    $changeDuringAdmission=$null
    foreach ($invalid in @('missing','foreign')) {
        if ($invalid -eq 'foreign') { [IO.File]::WriteAllText($ValidationHoldPath,'"different-release"') }
        Expect-Refusal { Start-IndexCompatibleWebAppPool -Name 'fixture' -RequiredHoldReleaseId $release }
        Expect-Refusal { Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $release -RequireOwned }
    }
    foreach ($changeDuringAdmission in @({[IO.File]::Delete($ValidationHoldPath)}, {[IO.File]::WriteAllText($ValidationHoldPath,'"different-release"')})) {
        foreach ($action in @({Start-IndexCompatibleWebAppPool -Name 'fixture' -RequiredHoldReleaseId $release},
                              {Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $release -RequireOwned})) {
            [IO.File]::WriteAllText($ValidationHoldPath,($release|ConvertTo-Json -Compress))
            Expect-Refusal $action
        }
    }
    $changeDuringAdmission=$null
    if ($script:starts -ne 0) { throw 'Candidate started after losing the hold.' }
    [IO.File]::WriteAllText($ValidationHoldPath,($release|ConvertTo-Json -Compress))
    Start-IndexCompatibleWebAppPool -Name 'fixture' -RequiredHoldReleaseId $release
    $released=Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $release -RequireOwned
    Expect-Refusal { Enable-IndexCompatibleOutlookTask -TaskName 'fixture' -ReleasedHold $released -ExpectedReleaseId 'unrelated' }
    $changeDuringAdmission={[IO.File]::WriteAllText($ValidationHoldPath,'"other"')}
    Expect-Refusal { Enable-IndexCompatibleOutlookTask -TaskName 'fixture' -ReleasedHold $released -ExpectedReleaseId $release }
    [IO.File]::Delete($ValidationHoldPath)
    $changeDuringAdmission=$null
    Enable-IndexCompatibleOutlookTask -TaskName 'fixture' -ReleasedHold $released -ExpectedReleaseId $release
    if ($script:starts -ne 1 -or $script:enables -ne 1) { throw 'Expected exactly one owned start and post-release enablement.' }
} finally { if ([IO.File]::Exists($ValidationHoldPath)) { [IO.File]::Delete($ValidationHoldPath) } }
