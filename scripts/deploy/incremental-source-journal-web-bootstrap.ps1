function Invoke-IncrementalSourceJournalWebBootstrap {
    param([Parameter(Mandatory)][string]$PacketPath, [Parameter(Mandatory)][string]$ExpectedPacketSha256,
        [switch]$Execute, [string]$ResumeReleaseId = '')

    Import-Module (Join-Path $PSScriptRoot 'source-journal-web-bootstrap.psm1') -Force
    $ownerModule = Import-Module (Join-Path $PSScriptRoot 'source-journal-allowlist-activation.psm1') -Force -PassThru
    Import-Module WebAdministration -ErrorAction Stop
    $packetBytes = Get-SourceAccessConfigurationBytes $PacketPath
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($packetBytes)) -cne $ExpectedPacketSha256) {
        throw 'web-bootstrap-packet-hash-mismatch'
    }
    $packet = ConvertFrom-UniqueConfigurationJson $packetBytes
    $binding = $packet.OperatorBinding
    if ($binding.StageName -cne '' -or $binding.DashboardOrigin -cne 'http://127.0.0.1:5137' -or
        $binding.ReceiptDirectory -cne 'I:\FluxKnowledgeSourceJournalOperator\Receipts' -or
        $binding.OperatorOrigin -cnotmatch '\Ahttp://localhost:[1-9][0-9]{3,4}\z') { throw 'web-bootstrap-target-mismatch' }
    $configPath = Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    $journalPolicyPath = Join-Path $SourceJournalHelperRoot 'journal-policy.json'
    $operatorTask = 'FluxKnowledge.SourceJournalOperator'
    $helperTask = 'FluxKnowledge.SourceAccess'
    $installedResume = $ResumeReleaseId.Length -gt 0
    if ($installedResume -and $ResumeReleaseId -cnotmatch '\Aweb-bootstrap-[a-f0-9]{32}\z') {
        throw 'web-bootstrap-resume-id-invalid'
    }
    $candidateRoot = [IO.Path]::GetFullPath($packet.WebCandidate)
    $installer = [IO.Path]::GetFullPath($packet.Installer)
    if ($candidateRoot -inotlike 'I:\Codex Workspaces\Operational Evidence\FluxKnowledge\*' -or
        $installer -inotlike 'I:\Codex Workspaces\Operational Evidence\FluxKnowledge\*\install-source-journal-allowlist-operator.ps1') {
        throw 'web-bootstrap-candidate-outside-evidence-root'
    }
    foreach ($path in $candidateRoot, $installer) {
        for ($part = $path; $part; $part = [IO.Path]::GetDirectoryName($part)) {
            Assert-NotReparsePoint -Path $part -Message 'Linked bootstrap inputs are unsupported.'
        }
    }
    $ReviewedSourceJournalPayloads = @("Web=$($binding.WebSha256)", "Cli=$($binding.HelperSha256)", "OutlookHost=$($binding.OutlookSha256)")
    $ExpectedSourceJournalDatabaseIdentity = $binding.DatabaseIdentity
    $operatorArguments = @{
        DashboardPort = 5137
        OperatorPort = ([uri]$binding.OperatorOrigin).Port
        OperatorSid = $binding.OperatorSids
        DatabaseIdentity = $binding.DatabaseIdentity
        WebSha256 = $binding.WebSha256
        OutlookSha256 = $binding.OutlookSha256
        HelperSha256 = $binding.HelperSha256
        TaskDefinitionSha256 = $binding.TaskDefinitionSha256
        TaskSecuritySha256 = $binding.TaskSecuritySha256
        JournalPipeName = $binding.JournalPipeName
    }
    $gate = [Threading.Mutex]::new($false, 'Global\FluxKnowledge.IncrementalIisUpdate.v1')
    $acquired = $false
    $inputLeases = [Collections.Generic.List[IDisposable]]::new()
    $recoveryDirectory = $null
    $recoveryWriter = $null
    try {
        if ($Execute) {
            $acquired = $gate.WaitOne(0)
            if (!$acquired) { throw 'web-bootstrap-deployment-busy' }
        }
        if (!$installedResume) { Assert-IncrementalIisPreflight }
        $assertIisOwnership = {
            & $ownerModule {
                param($operatorBinding)
                $manager = Get-AllowlistManager
                try { $null = Get-AllowlistIis @{Binding=$operatorBinding;Target=(Get-AllowlistTargets $operatorBinding)} $manager }
                finally { $manager.Dispose() }
            } $binding
        }
        & $assertIisOwnership
        $outlookTaskXml = Export-ScheduledTask -TaskName $InteractiveHostTaskName -TaskPath '\'
        if (!$installedResume -and ((Test-Path -LiteralPath $ValidationHoldPath) -or (Test-Path -LiteralPath 'I:\FluxKnowledgeSourceJournalOperator') -or
            (Get-ScheduledTask -TaskName $operatorTask -TaskPath '\' -ErrorAction SilentlyContinue) -or
            @(Get-NetTCPConnection -LocalPort $operatorArguments.OperatorPort -ErrorAction SilentlyContinue).Count)) {
            throw 'web-bootstrap-target-occupied'
        }
        $releaseId = if ($installedResume) { $ResumeReleaseId } else { 'web-bootstrap-' + [guid]::NewGuid().ToString('N') }
        $recovery = Join-Path $IncrementalRecoveryRoot $releaseId
        $hold = { Assert-HybridDeploymentHoldOwner -Path $ValidationHoldPath -ReleaseId $releaseId }
        $prior = $null
        if ($installedResume) {
            & $hold
            if ((Get-SourceJournalHeldReleaseId $ValidationHoldPath) -cne $releaseId -or
                (Get-SourceJournalHeldMigrationIntent -HoldPath $ValidationHoldPath -RecoveryRoot $IncrementalRecoveryRoot `
                    -ExpectedDatabaseIdentity $binding.DatabaseIdentity) -or
                (Test-SourceJournalHeldHelperRecovery -HoldPath $ValidationHoldPath -InstallRoot $SourceJournalHelperRoot)) {
                throw 'web-bootstrap-resume-journal-fence-mismatch'
            }
            $recoveryDirectory = [FluxSourceAccessInstallFile]::OpenPrivateDirectory($recovery)
            $prior = ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes (Join-Path $recovery 'state.json'))
            if ($prior.ReleaseId -cne $releaseId -or $prior.Phase -cne 'InstallIntent' -or
                $prior.Binding.PacketSha256 -cne $ExpectedPacketSha256 -or (Get-WebAppPoolState -Name $SiteName).Value -cne 'Stopped' -or
                (Get-HybridPayloadFingerprint (Join-Path $recovery 'PreviousWeb')) -cne $packet.PredecessorWebSha256) {
                throw 'web-bootstrap-resume-fence-mismatch'
            }
        }
        $assertRoles = {
            param([string]$ExpectedWebHash)
            if ((Export-ScheduledTask -TaskName $InteractiveHostTaskName -TaskPath '\') -cne $outlookTaskXml) {
                throw 'web-bootstrap-outlook-task-drift'
            }
            $expected = @{Web=$ExpectedWebHash; OutlookHost=$binding.OutlookSha256; Cli=$binding.HelperSha256}
            foreach ($role in Get-DeploymentSourceJournalPayloads) {
                if ((Get-HybridPayloadFingerprint $role.Root) -cne $expected[$role.Role]) { throw "web-bootstrap-payload-drift: $($role.Role)" }
            }
            $helper = Get-SourceJournalHelperTaskObservation -InstallRoot $SourceJournalHelperRoot -TaskName $helperTask
            if (!$helper.Enabled -or !$helper.Running -or $helper.DefinitionSha256 -cne $binding.TaskDefinitionSha256 -or
                $helper.SecuritySha256 -cne $binding.TaskSecuritySha256 -or
                (Get-FileHash -LiteralPath $journalPolicyPath).Hash -cne $packet.HelperPolicySha256) {
                throw 'web-bootstrap-helper-drift'
            }
        }
        & $assertRoles $(if ($installedResume) { $binding.WebSha256 } else { $packet.PredecessorWebSha256 })
        Test-ApplicationPayload $candidateRoot
        $inputLeases.Add([FluxSourceAccessInstallFile]::Read($installer,2MB))
        foreach ($file in Get-ChildItem -LiteralPath $candidateRoot -File -Recurse -Force) {
            $inputLeases.Add([FluxSourceAccessInstallFile]::Read($file.FullName, [long]::MaxValue))
        }
        if ((Get-HybridPayloadFingerprint $candidateRoot) -cne $binding.WebSha256 -or
            (Get-FileHash -LiteralPath $installer).Hash -cne $packet.InstallerSha256) { throw 'web-bootstrap-artifact-drift' }
        $installerPlan = & $installer @operatorArguments -PlanOnly | ConvertFrom-Json -AsHashtable
        if ($installerPlan.PlanSha256 -cne $packet.OperatorPlanSha256) { throw 'web-bootstrap-operator-plan-drift' }
        if ((ConvertTo-Json -InputObject $installerPlan.Binding -Depth 6 -Compress) -cne
            (ConvertTo-Json -InputObject $binding -Depth 6 -Compress)) { throw 'web-bootstrap-operator-binding-mismatch' }
        foreach ($file in $installerPlan.Package.Keys) {
            if ($file -cnotmatch '\A[A-Za-z0-9_-][A-Za-z0-9_.-]*\z') { throw 'web-bootstrap-package-path-invalid' }
            $member = Join-Path (Split-Path -Parent $installer) $file
            $inputLeases.Add([FluxSourceAccessInstallFile]::Read($member,16MB))
            if ((Get-FileHash -LiteralPath $member).Hash -cne $installerPlan.Package[$file]) {
                throw 'web-bootstrap-package-drift'
            }
        }
        $before = Get-SourceAccessConfigurationBytes $(if ($installedResume) { Join-Path $recovery 'previous-config.json' } else { $configPath })
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($before)) -cne $packet.PredecessorConfigSha256) {
            throw 'web-bootstrap-config-drift'
        }
        $config = ConvertFrom-UniqueConfigurationJson $before
        $policy = ConvertFrom-UniqueConfigurationJson (Get-SourceAccessConfigurationBytes $journalPolicyPath)
        if ($config.Contains('SourceJournalAllowlistOperator') -or !$config.SourceJournal.Enabled -or
            $config.SourceJournal.PipeName -cne $binding.JournalPipeName -or $policy.PipeName -cne $binding.JournalPipeName -or
            @($config.SourceJournal.ApprovedVolumes).Count -ne 1 -or @($policy.ApprovedVolumes).Count -ne 1 -or
            $config.SourceJournal.ApprovedVolumes[0] -cne $packet.RetainedVolume -or $policy.ApprovedVolumes[0] -cne $packet.RetainedVolume) {
            throw 'web-bootstrap-journal-baseline-mismatch'
        }
        $config.SourceJournalAllowlistOperator = @{ Origin = $binding.OperatorOrigin }
        $after = [Text.UTF8Encoding]::new($false).GetBytes(($config | ConvertTo-Json -Depth 100))
        $afterHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($after))
        if ($afterHash -cne $packet.ConfigAfterSha256) { throw 'web-bootstrap-resulting-config-drift' }
        $assertDatabase = {
            $observed = Get-SourceJournalDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
            if ($observed.Identity -cne $binding.DatabaseIdentity -or $observed.History.Count -ne 84 -or
                $observed.History[-1] -cne '20261119201434_AddGpuModelResidencies') { throw 'web-bootstrap-database-schema-drift' }
            Assert-SourceJournalSchema $observed
        }
        & $assertDatabase
        $plan = [ordered]@{
            Mode = 'plan-only'; PacketSha256 = $ExpectedPacketSha256; Site = $SiteName; App = $CanonicalDeployRoot
            DatabaseIdentity = $binding.DatabaseIdentity; SchemaCount = 84; WebBefore = $packet.PredecessorWebSha256
            WebAfter = $binding.WebSha256; ConfigBefore = $packet.PredecessorConfigSha256; ConfigAfter = $afterHash
            OperatorPlan = $packet.OperatorPlanSha256; Origin = $binding.OperatorOrigin
            ReleaseRequires = @('owned hold','healthy Web and index','held journal transport','configured preview and Windows challenge','zero accepted operator operations')
        }
        $assertInstalled = {
            & $assertRoles $binding.WebSha256
            $installedBinding = Read-SourceJournalAllowlistOperatorBinding 'I:\FluxKnowledgeSourceJournalOperator\binding.json'
            $installedTask = Get-ScheduledTask -TaskName $operatorTask -TaskPath '\'
            if ((Get-FileHash -LiteralPath $configPath).Hash -cne $afterHash -or
                $installedTask.State -ne 'Running' -or $installedTask.Actions.Count -ne 1 -or
                $installedTask.Actions[0].Execute -ine $installerPlan.Executor -or
                $installedTask.Actions[0].Arguments -cne $installerPlan.Arguments -or
                $installedTask.Actions[0].WorkingDirectory -ine $installerPlan.InstallRoot -or
                $installedTask.Principal.UserId -inotIn @('SYSTEM','S-1-5-18') -or
                @(Get-ChildItem -LiteralPath $binding.ReceiptDirectory -Force).Count) {
                throw 'web-bootstrap-config-or-operator-drift'
            }
            $receiptPath = Join-Path $installerPlan.InstallRoot 'installation-receipt.json'
            $receipt = & $ownerModule { param($path) ConvertFrom-UniqueConfigurationJson (Read-AllowlistFile $path 32768 -Private) } $receiptPath
            if ($receipt.Mode -cne 'verified-listening' -or $receipt.TaskName -cne $operatorTask -or
                $receipt.PlanSha256 -cne $installerPlan.PlanSha256 -or $receipt.Origin -cne $binding.OperatorOrigin) {
                throw 'web-bootstrap-installation-receipt-drift'
            }
            foreach ($name in $installerPlan.Package.Keys) {
                $installedPath = Join-Path $installerPlan.InstallRoot $name
                $bytes = & $ownerModule { param($path) ,(Read-AllowlistFile $path 16MB -Private) } $installedPath
                if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([byte[]]$bytes)) -cne $installerPlan.Package[$name]) {
                    throw "web-bootstrap-installed-package-drift: $name"
                }
            }
            $processes = @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -OperationTimeoutSec 5 | Where-Object {
                $_.ExecutablePath -ieq $installerPlan.Executor -and $_.CommandLine -clike "*$($installerPlan.Arguments)"
            })
            if ($processes.Count -ne 1) { throw 'web-bootstrap-operator-process-unverified' }
            $processOwner = Invoke-CimMethod -InputObject $processes[0] -MethodName GetOwnerSid -OperationTimeoutSec 5
            if ($processOwner.ReturnValue -ne 0 -or $processOwner.Sid -cne 'S-1-5-18') { throw 'web-bootstrap-operator-process-owner-unverified' }
            foreach ($key in $binding.Keys) {
                if ((ConvertTo-Json -InputObject $installedBinding[$key] -Compress) -cne
                    (ConvertTo-Json -InputObject $binding[$key] -Compress)) { throw 'web-bootstrap-installed-binding-drift' }
            }
        }
        if ($installedResume) {
            if ((ConvertTo-Json -InputObject $prior.Binding -Depth 8 -Compress) -cne
                (ConvertTo-Json -InputObject $plan -Depth 8 -Compress)) { throw 'web-bootstrap-resume-plan-drift' }
            & $assertInstalled
        }
        if (!$Execute) {
            if ($installedResume) { $plan.Mode = 'installed-recovery-plan-only'; $plan.ReleaseId = $releaseId }
            $plan | ConvertTo-Json -Depth 6
            return
        }

        $recoveryWriter = Start-RecoveryReferenceWrite -ConnectionString (Get-DeploymentSqlConnectionString) -RecoveryRoot $CanonicalRecoveryRoot
        Assert-NotReparsePoint -Path $IncrementalRecoveryRoot -Message 'Invalid bootstrap recovery root.'
        if (!$installedResume) {
            $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
            $acl = [byte[]]::new($descriptor.BinaryLength)
            $descriptor.GetBinaryForm($acl, 0)
            $recoveryDirectory = [FluxSourceAccessInstallFile]::CreatePrivateDirectory($recovery, $acl)
        }
        $state = @{ Baseline = $null }
        Invoke-SourceJournalWebBootstrap -ResumeInstalled:$installedResume -Ports @{
            WritePhase = {
                param($phase)
                Write-HybridRebuildJson -Path (Join-Path $recovery 'state.json') -Value @{Phase=$phase; ReleaseId=$releaseId; Binding=$plan}
            }
            CreateHold = { [void](New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId) }
            AssertHold = $hold
            PrepareInstalled = {
                & $assertInstalled
                $state.Baseline = Get-RetainedPipelineStateBaseline
                Write-HybridRebuildJson -Path (Join-Path $recovery 'installed-recovery.json') -Value @{
                    ReleaseId=$releaseId; PacketSha256=$ExpectedPacketSha256; ObservedAtUtc=[DateTime]::UtcNow
                    Phase='InstallIntent'; Pool='Stopped'; InstalledWeb=$binding.WebSha256; InstalledConfig=$afterHash
                }
            }
            StopWeb = {
                & $assertIisOwnership
                Stop-HybridIisAfterGpuDrain
                $state.Baseline = Get-RetainedPipelineStateBaseline
            }
            InstallWebAndOperator = {
                & $assertRoles $packet.PredecessorWebSha256
                [IO.File]::WriteAllBytes((Join-Path $recovery 'previous-config.json'), $before)
                $backup = Join-Path $recovery 'PreviousWeb'
                $originalAcl = (Get-Acl -LiteralPath $configPath).Sddl
                [IO.Directory]::Move($CanonicalDeployRoot, (Join-Path $recovery 'PreviousWeb'))
                if ((Get-HybridPayloadFingerprint $backup) -cne $packet.PredecessorWebSha256 -or
                    (Get-FileHash -LiteralPath (Join-Path $recovery 'previous-config.json')).Hash -cne $packet.PredecessorConfigSha256) {
                    throw 'web-bootstrap-predecessor-backup-unproved'
                }
                Write-HybridRebuildJson -Path (Join-Path $recovery 'predecessor-security.json') -Value @{
                    Config=$originalAcl; Web=(Get-Acl -LiteralPath $backup).Sddl
                }
                Invoke-CandidatePayloadActivation -CandidateRoot $candidateRoot -ApplicationRoot $CanonicalDeployRoot
                & $assertRoles $binding.WebSha256
                if ((Get-FileHash -LiteralPath $installer).Hash -cne $packet.InstallerSha256) { throw 'web-bootstrap-installer-drift' }
                $installed = & $installer @operatorArguments -Install -ExpectedPlanSha256 $packet.OperatorPlanSha256 | ConvertFrom-Json
                if ($installed.Mode -cne 'verified-listening' -or $installed.TaskName -cne $operatorTask) { throw 'web-bootstrap-operator-listener-unverified' }
                & $ownerModule { param($path,$before,$after) Save-AllowlistFile $path $before $after } $configPath $before $after
                & $assertInstalled
            }
            StartWeb = { Start-IndexCompatibleWebAppPool -Name $SiteName -RequiredHoldReleaseId $releaseId }
            VerifyHeld = {
                Invoke-RequiredLoopbackProbes -Origin $binding.DashboardOrigin -TimeoutSeconds $ReadinessTimeoutSeconds
                & $assertIisOwnership
                # Rejecting an empty preview exercises the enabled, read-only handoff
                # route. It creates no operator confirmation or accepted operation.
                $preview = Invoke-WebRequest -Uri ($binding.DashboardOrigin+'/sources/journal/verify-preview') -Method Post `
                    -ContentType 'application/json' -Body '{}' -TimeoutSec 12 -NoProxy -MaximumRedirection 0 -SkipHttpErrorCheck
                $challenge = Invoke-WebRequest -Uri ($binding.OperatorOrigin+'/status/00000000-0000-0000-0000-000000000001') `
                    -TimeoutSec 12 -NoProxy -MaximumRedirection 0 -SkipHttpErrorCheck
                if ($preview.StatusCode -ne 400 -or $challenge.StatusCode -ne 401 -or
                    -not (@($challenge.Headers['WWW-Authenticate']) -match '^(Negotiate|NTLM)')) {
                    throw 'web-bootstrap-held-operator-handoff-unverified'
                }
                $helper = Invoke-RestMethod -Uri ($binding.DashboardOrigin+'/health/source-journal-helper') -NoProxy -TimeoutSec 12 -MaximumRedirection 0
                $poolSid = ([Security.Principal.NTAccount]::new('IIS APPPOOL',$SiteName)).Translate([Security.Principal.SecurityIdentifier]).Value
                if ($helper.Version -ne 1 -or $helper.VolumesChecked -ne 1 -or @($helper.ApprovedVolumes).Count -ne 1 -or
                    $helper.ApprovedVolumes[0] -cne $packet.RetainedVolume -or $helper.PipeName -cne $binding.JournalPipeName -or
                    $helper.CallerSid -cne $poolSid) { throw 'web-bootstrap-held-journal-unverified' }
                Assert-RetainedPipelineStateUnchanged -Baseline $state.Baseline -Current (Get-RetainedPipelineStateBaseline)
                & $assertInstalled
                Write-HybridRebuildJson -Path (Join-Path $recovery 'held-readiness.json') -Value @{
                    ReleaseId=$releaseId; ObservedUtc=[DateTime]::UtcNow; Readiness='healthy'; Volume=$packet.RetainedVolume
                    ReviewedWeb=$binding.WebSha256; ReviewedConfig=$afterHash
                }
            }
            ReleaseHold = {
                & $assertInstalled
                & $assertDatabase
                [void](Remove-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $releaseId -RequireOwned)
            }
            VerifyReleased = {
                if (Test-Path -LiteralPath $ValidationHoldPath) { throw 'web-bootstrap-release-hold-remains' }
                Invoke-RequiredLoopbackProbes -Origin $binding.DashboardOrigin -TimeoutSeconds $ReadinessTimeoutSeconds
                & $assertRoles $binding.WebSha256
            }
        }
        Complete-RecoveryReferenceWrite -Lease $recoveryWriter
        [pscustomobject]@{Mode='web-bootstrap-applied'; ReleaseId=$releaseId; Recovery=$recovery; WebSha256=$binding.WebSha256} | ConvertTo-Json
    }
    finally {
        try {
            if ($recoveryDirectory) { $recoveryDirectory.Dispose() }
            foreach ($lease in $inputLeases) { $lease.Dispose() }
            if ($recoveryWriter) { Close-RecoveryReferenceWrite -Lease $recoveryWriter }
        }
        finally {
            if ($acquired) { $gate.ReleaseMutex() }
            $gate.Dispose()
        }
    }
}
