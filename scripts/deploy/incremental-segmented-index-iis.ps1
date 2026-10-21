# Dot-sourced by the canonical incremental updater. No work occurs on import.
function Get-SegmentedPayloadFiles([string]$Root) {
    $rootPath=Assert-SegmentedUpdatePath $Root
    $prefix=$rootPath.TrimEnd('\')+'\'
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($rootPath)
    $files=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    while($pending.Count) {
        $directory=$pending.Pop()
        foreach($path in [IO.Directory]::GetFileSystemEntries($directory)) {
            $attributes=[IO.File]::GetAttributes($path)
            if($attributes -band [IO.FileAttributes]::ReparsePoint){throw 'index-update-payload-reparse-point'}
            if($attributes -band [IO.FileAttributes]::Directory){$pending.Push($path)}
            else{$files.Add($path.Substring($prefix.Length),$path)}
        }
    }
    return ,$files
}

function Copy-SegmentedIndexPayload {
    param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256)
    $sourcePath=Assert-SegmentedUpdatePath $Source
    $destinationPath=Assert-SegmentedUpdatePath $Destination
    if($sourcePath.TrimEnd('\') -ieq $destinationPath.TrimEnd('\') -or
        ($sourcePath.TrimEnd('\')+'\').StartsWith($destinationPath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
        ($destinationPath.TrimEnd('\')+'\').StartsWith($sourcePath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'index-update-payload-path-overlap'}
    $sourceFiles=Get-SegmentedPayloadFiles $sourcePath
    if((Get-HybridPayloadFingerprint $sourcePath) -ine $ExpectedSha256){throw 'index-update-source-payload-changed'}
    [void][IO.Directory]::CreateDirectory($destinationPath)
    $existing=Get-SegmentedPayloadFiles $destinationPath
    if($existing.Count -and (Get-HybridPayloadFingerprint $destinationPath) -ieq $ExpectedSha256){return}
    foreach($relative in $sourceFiles.Keys) {
        $target=Join-Path $destinationPath $relative
        [void](Assert-SegmentedUpdatePath $target)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        [IO.File]::Copy($sourceFiles[$relative],$target,$true)
    }
    # Mirror only the approved payload root. The root/ACL remains in place;
    # unrelated directories and Recovery packets are never swept recursively.
    foreach($relative in $existing.Keys) {
        if(!$sourceFiles.ContainsKey($relative)) {
            [void](Assert-SegmentedUpdatePath $existing[$relative])
            [IO.File]::Delete($existing[$relative])
        }
    }
    if((Get-HybridPayloadFingerprint $sourcePath) -ine $ExpectedSha256 -or
        (Get-HybridPayloadFingerprint $destinationPath) -ine $ExpectedSha256){throw 'index-update-payload-copy-incomplete'}
}

function Get-SegmentedPublicationConfiguration([string]$Path) {
    & (Get-Module 'incremental-source-access-config') {
        param($configurationPath)
        $before=Get-SourceAccessConfigurationBytes $configurationPath
        $settings=[Text.Encoding]::UTF8.GetString($before).TrimStart([char]0xfeff) | ConvertFrom-Json -AsHashtable -DateKind String
        if($settings -isnot [Collections.IDictionary]){throw 'index-update-configuration-invalid'}
        if(!$settings.Contains('IndexPublication')){$settings.IndexPublication=@{}}
        if($settings.IndexPublication -isnot [Collections.IDictionary]){throw 'index-update-configuration-invalid'}
        $settings.IndexPublication.Mode='Segmented'
        $after=[Text.UTF8Encoding]::new($false).GetBytes(($settings | ConvertTo-Json -Depth 100))
        [pscustomobject]@{Path=$configurationPath;Before=$before;After=$after;
            BeforeHash=(Get-SourceAccessConfigurationHash $before);AfterHash=(Get-SourceAccessConfigurationHash $after);
            Attempted=$false;RollbackVerified=$false}
    } $Path
}

function Get-SegmentedUpdateImplementationHash {
    $names=@('update-native-iis-incremental.ps1','incremental-segmented-index-iis.ps1',
        'incremental-segmented-index-update.psm1','incremental-segmented-index-schema.psm1',
        'segmented-index-schema-up.sql','segmented-index-schema-contract.sql','index-format-admission.psm1',
        'incremental-projection-operator.psm1','ProjectionOperatorLoadContext.cs','IndexEntryMetadata.cs',
        'recovery-reference-writer.psm1','incremental-source-access-config.psm1','incremental-hybrid-passage-rebuild.psm1',
        'loopback-deployment-safety.ps1')
    $records=@(foreach($name in $names){$name+'|'+(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name)).Hash})
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($records -join "`n"))))
}

function Assert-SegmentedOperationRoot([string]$Root) {
    $path=Assert-SegmentedUpdatePath $Root
    if(!$path.StartsWith('E:\Codex Workspaces\Staging\FluxKnowledge\',[StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Directory]::Exists($path)){throw 'index-update-private-operation-root-required'}
    return $path.TrimEnd('\')
}

function Get-SegmentedCanonicalHold([string]$Path) {
    $owner=Read-SegmentedUpdateJson $Path
    if($owner -isnot [string] -or !$owner){throw 'index-update-deny-all-hold-required'}
    $canonical=[Text.UTF8Encoding]::new($false).GetBytes(($owner | ConvertTo-Json -Compress))
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonical))
    if((Get-FileHash -LiteralPath $Path).Hash -cne $hash){throw 'index-update-canonical-hold-required'}
    return @{Owner=$owner;Sha256=$hash}
}

function New-SegmentedIndexIisPlan {
    param([string]$Root,[string]$PayloadRoot,[string]$OperationRoot,[switch]$OfflineMaintenance)
    $rootPath=(Resolve-Path -LiteralPath $Root).Path
    $operation=Assert-SegmentedOperationRoot $OperationRoot
    $payload=Assert-SegmentedOperationRoot $PayloadRoot
    $commit=(& git -C $rootPath rev-parse HEAD | Out-String).Trim()
    if($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$' -or (& git -C $rootPath status --porcelain)){throw 'index-update-clean-committed-source-required'}
    $database=Get-SegmentedIndexDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
    $contract=Get-SegmentedIndexMigrationContract
    if(@($database.History)[-1] -cne $contract.Baseline -or $database.SchemaSha256 -cne $contract.BaselineSchemaSha256 -or
        $database.RequiredFormat -ne 1){throw 'index-update-reviewed-schema-baseline-required'}
    $companionDatabase=Get-SegmentedIndexDatabaseState -ConnectionString (Get-RepositoryRecoveryCompanionConnectionString)
    if($database.Identity -cne $companionDatabase.Identity){throw 'index-update-companion-database-changed'}
    $configuration=Get-SegmentedPublicationConfiguration (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json')
    $hold=Get-SegmentedCanonicalHold $ValidationHoldPath
    $task=Get-RepositoryRecoveryTaskObservation
    if($task.Enabled -or $task.State -ceq 'Running'){throw 'index-update-companion-must-remain-disabled'}
    $payloads=@(foreach($role in @('Web','OutlookHost','Cli')) {
        $directory=Join-Path $payload $role
        $entry=Join-Path $directory ('FluxKnowledge.'+$role+'.dll')
        if([Diagnostics.FileVersionInfo]::GetVersionInfo($entry).ProductVersion -cne ('1.0.0+'+$commit)){throw 'index-update-payload-commit-changed'}
        $binding=Get-IndexEntryBinding -Role $role -Root $directory -ReadPayloadFingerprint {param($path) Get-HybridPayloadFingerprint $path}
        if($binding.Formats -notcontains 2){throw 'index-update-payload-format-incompatible'}
        @{Role=$role;Root=$directory;Sha256=$binding.Sha256;EntrySha256=(Get-FileHash -LiteralPath $entry).Hash}
    })
    Test-ApplicationPayload (Join-Path $payload 'Web');Test-InteractiveHostPayload (Join-Path $payload 'OutlookHost')
    $created=[DateTime]::UtcNow
    return [ordered]@{Version=1;OperationId=[guid]::NewGuid().ToString('D');CreatedUtc=$created.ToString('O');
        ReleaseId=($created.ToString('yyyyMMddTHHmmssZ')+'-'+$commit.Substring(0,12));SourceRoot=$rootPath;Commit=$commit;
        OperationRoot=$operation;ImplementationSha256=(Get-SegmentedUpdateImplementationHash);DatabaseIdentity=$database.Identity;
        BaselineHistory=@($database.History);SchemaUpSha256=$contract.UpSha256;TargetFormat=2;Payloads=$payloads;
        OfflineMaintenance=[bool]$OfflineMaintenance;
        OriginalWebSha256=(Get-HybridPayloadFingerprint $CanonicalDeployRoot);OriginalCompanionSha256=(Get-HybridPayloadFingerprint $InteractiveHostRoot);
        ConfigurationSha256=$configuration.BeforeHash;DesiredConfigurationSha256=$configuration.AfterHash;
        CompanionConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json')).Hash;
        HoldSha256=$hold.Sha256;HoldOwner=$hold.Owner;TaskIdentityHash=$task.IdentityHash;LauncherSha256=$task.LauncherSha256;
        CompanionPolicy='preserve-disabled';CandidateStorage='preserve-reviewed-E-payloads';IndexRoot=(Join-Path $CanonicalLiveRoot 'Data/Index');RecoveryRoot=$CanonicalRecoveryRoot;
        ApplicationRoot=$CanonicalDeployRoot;CompanionRoot=$InteractiveHostRoot;
        Recovery='retained prior payloads; forward resume only; rollback rebuilds current SQL into format1 with separate reviewed plan';
        RequiredStopProof='exact independently reviewed stop receipt, then current stopped pool/task/owner recheck; after every unknown start a newer proof is mandatory';
        NoModelAcquisition=$true;NoSourceEdits=$true;NoLegacyPayloadDeletion=$true}
}

function Set-SegmentedPublicationConfiguration($Plan) {
    $path=Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json'
    $hash=(Get-FileHash -LiteralPath $path).Hash
    if($hash -ceq $Plan.DesiredConfigurationSha256){return}
    if($hash -cne $Plan.ConfigurationSha256){throw 'index-update-configuration-changed'}
    $change=Get-SegmentedPublicationConfiguration $path
    if($change.BeforeHash -cne $Plan.ConfigurationSha256 -or $change.AfterHash -cne $Plan.DesiredConfigurationSha256){throw 'index-update-configuration-binding-changed'}
    $backup=Join-Path $Plan.OperationRoot 'configuration-before.json'
    if([IO.File]::Exists($backup)) {
        if((Get-FileHash -LiteralPath $backup).Hash -cne $Plan.ConfigurationSha256){throw 'index-update-configuration-backup-changed'}
    } else {
        $file=[IO.File]::Open($backup,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try{$file.Write($change.Before);$file.Flush($true)}finally{$file.Dispose()}
    }
    & (Get-Module 'incremental-source-access-config') {
        param($configurationChange)
        Write-SourceAccessConfigurationBytes $configurationChange $configurationChange.Before $configurationChange.After
    } $change
}

function Assert-SegmentedIisStopped($Plan,$StopReceipt,[string]$After='') {
    if(!$StopReceipt.Completed -or !$StopReceipt.PoolStopped -or !$StopReceipt.TaskDisabled -or !$StopReceipt.HoldRetained -or
        $StopReceipt.Phase -cne 'iis-writers-stopped' -or $StopReceipt.PlanSha256 -cnotmatch '^[A-Fa-f0-9]{64}$') {throw 'index-update-reviewed-stop-proof-required'}
    $verified=[DateTime]::Parse($StopReceipt.ExitProof.VerifiedAtUtc).ToUniversalTime()
    $required=if($After){[DateTime]::Parse($After).ToUniversalTime()}else{[DateTime]::Parse($Plan.CreatedUtc).ToUniversalTime()}
    if($verified -le $required -or $verified -gt [DateTime]::UtcNow.AddSeconds(5)){throw 'index-update-fresh-stop-proof-required'}
    Assert-StoppedPoolRecoveryBaseline
    $task=Get-RepositoryRecoveryTaskObservation
    if($task.Enabled -or $task.State -ceq 'Running' -or $task.IdentityHash -cne $Plan.TaskIdentityHash -or
        $task.LauncherSha256 -cne $Plan.LauncherSha256){throw 'index-update-companion-owner-changed'}
    $launcher=[regex]::Escape($task.LauncherPath)
    $hostScript=[regex]::Escape((Join-Path $InteractiveHostRoot 'run-outlook-host.ps1'))
    $processes=@(Get-CimInstance Win32_Process -ErrorAction Stop)
    foreach($identity in $StopReceipt.ExitProof.ExitedIdentities) {
        if(@($processes | Where-Object { $_.ProcessId -eq [int]$identity.ProcessId -and
            $_.CreationDate.ToUniversalTime() -eq [DateTime]::Parse($identity.Created).ToUniversalTime() }).Count){throw 'index-update-owner-exit-not-proved'}
    }
    if(@($processes | Where-Object {
        $_.ProcessId -ne $PID -and ($_.Name -ceq 'FluxKnowledge.OutlookHost.exe' -or
            ($_.Name -ceq 'wscript.exe' -and $_.CommandLine -match ('(?:^|\s)"?'+$launcher+'"?(?:\s|$)')) -or
            ($_.Name -in @('pwsh.exe','powershell.exe') -and $_.CommandLine -match ('(?i)-File\s+"?'+$hostScript+'"?(?:\s|$)')) -or
            ($_.Name -ceq 'dotnet.exe' -and $_.CommandLine -match '(?:^|\s)(?:"[^"]*\\FluxKnowledge\.OutlookHost\.dll"|[^"\s]*\\FluxKnowledge\.OutlookHost\.dll)(?:\s|$)'))
    }).Count){throw 'index-update-companion-exit-not-proved'}
    if((Get-FileHash -LiteralPath $ValidationHoldPath).Hash -cne $Plan.HoldSha256 -or
        (Read-SegmentedUpdateJson $ValidationHoldPath) -cne $Plan.HoldOwner){throw 'index-update-deny-all-hold-changed'}
}

function Assert-SegmentedIisBindings($Plan,$State) {
    if($Plan.Version -ne 1 -or $Plan.TargetFormat -ne 2 -or $Plan.ReleaseId -cnotmatch '^\d{8}T\d{6}Z-[a-f0-9]{12}$' -or
        $Plan.ApplicationRoot -cne $CanonicalDeployRoot -or $Plan.CompanionRoot -cne $InteractiveHostRoot -or
        $Plan.RecoveryRoot -cne $CanonicalRecoveryRoot -or $Plan.IndexRoot -cne (Join-Path $CanonicalLiveRoot 'Data/Index') -or
        $Plan.CompanionPolicy -cne 'preserve-disabled' -or $Plan.CandidateStorage -cne 'preserve-reviewed-E-payloads'){throw 'index-update-plan-scope-invalid'}
    if($Plan.HoldOwner -isnot [string] -or !$Plan.HoldOwner -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.UTF8Encoding]::new($false).GetBytes(($Plan.HoldOwner | ConvertTo-Json -Compress)))) -cne $Plan.HoldSha256) {
        throw 'index-update-canonical-hold-required'
    }
    [void](Assert-SegmentedOperationRoot $Plan.OperationRoot)
    if((Get-SegmentedUpdateImplementationHash) -cne $Plan.ImplementationSha256 -or
        (& git -C $Plan.SourceRoot rev-parse HEAD | Out-String).Trim() -cne $Plan.Commit -or
        (& git -C $Plan.SourceRoot status --porcelain)){throw 'index-update-implementation-changed'}
    $roles=@($Plan.Payloads | ForEach-Object Role | Sort-Object)
    if(($roles -join '|') -cne 'Cli|OutlookHost|Web'){throw 'index-update-payload-set-invalid'}
    foreach($payload in $Plan.Payloads) {
        [void](Assert-SegmentedOperationRoot $payload.Root)
        if((Get-HybridPayloadFingerprint $payload.Root) -cne $payload.Sha256){throw 'index-update-reviewed-payload-changed'}
    }
    $hash=(Get-FileHash -LiteralPath (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json')).Hash
    $allowed=if($State.ConfigurationApplied){@($Plan.DesiredConfigurationSha256)}else{@($Plan.ConfigurationSha256,$Plan.DesiredConfigurationSha256)}
    if($hash -cnotin $allowed -or (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $InteractiveHostRoot) 'appsettings.Production.json')).Hash -cne $Plan.CompanionConfigurationSha256){throw 'index-update-configuration-changed'}
    $database=Get-SegmentedIndexDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
    if($database.Identity -cne $Plan.DatabaseIdentity){throw 'index-update-database-incarnation-changed'}
    if($State.SchemaVerified){Assert-SegmentedIndexApplicationSchema $database}
    $release=Join-Path $IncrementalRecoveryRoot $Plan.ReleaseId
    if($State.BackupsReady) {
        if((Get-HybridPayloadFingerprint (Join-Path $release 'previous')) -cne $Plan.OriginalWebSha256 -or
            (Get-HybridPayloadFingerprint (Join-Path $release 'previous-interactive-host')) -cne $Plan.OriginalCompanionSha256){throw 'index-update-predecessor-evidence-changed'}
    }
    if($State.PayloadsReady) {
        if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne @($Plan.Payloads | Where-Object Role -CEQ 'Web')[0].Sha256 -or
            (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne @($Plan.Payloads | Where-Object Role -CEQ 'OutlookHost')[0].Sha256){throw 'index-update-installed-payload-changed'}
    } elseif(!$State.BackupsReady) {
        if((Get-HybridPayloadFingerprint $CanonicalDeployRoot) -cne $Plan.OriginalWebSha256 -or
            (Get-HybridPayloadFingerprint $InteractiveHostRoot) -cne $Plan.OriginalCompanionSha256){throw 'index-update-predecessor-payload-changed'}
    }
}

function Assert-SegmentedIisCurrent($Plan,$State) {
    $database=Get-SegmentedIndexDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
    Assert-SegmentedIndexApplicationSchema $database
    if($database.Identity -cne $Plan.DatabaseIdentity -or $database.RequiredFormat -ne 2){throw 'index-update-database-incarnation-changed'}
    $normalised=ConvertTo-DeploymentValidationConnectionString (Get-DeploymentSqlConnectionString)
    $builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($normalised)
    $builder['Pooling']=$false;$builder['ConnectRetryCount']=0;$builder['Connect Timeout']=5
    $connection=[Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
    try {
        $connection.Open();$command=$connection.CreateCommand()
        try {
            $command.CommandTimeout=5
            $command.CommandText='SELECT COUNT(*) FROM dbo.IndexState s JOIN dbo.IndexGenerations g ON g.Id=s.ActiveIndexGenerationId WHERE s.Id=1 AND s.RequiredIndexFormat=2 AND g.Id=@id AND g.FormatVersion=2 AND g.ValidatedAtUtc IS NOT NULL AND g.RetiredAtUtc IS NULL AND g.ReclaimedAtUtc IS NULL;'
            [void]$command.Parameters.Add('@id',[Data.SqlDbType]::UniqueIdentifier);$command.Parameters['@id'].Value=[guid]$State.GenerationId
            if([int]$command.ExecuteScalar() -ne 1){throw 'index-update-recorded-result-is-not-current'}
        }finally{$command.Dispose()}
    }finally{$connection.Dispose()}
}

function New-SegmentedIisProjectionPlan($Plan,[string]$OperatorRoot,[string]$Path) {
    if([IO.File]::Exists($Path)) {
        $saved=Read-SegmentedUpdateJson $Path
        if($saved.Plan.OperationId -cne $Plan.OperationId -or $saved.Plan.TargetFormat -ne 2){throw 'index-update-projection-plan-changed'}
        return (Get-FileHash -LiteralPath $Path).Hash
    }
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @((Join-Path $OperatorRoot 'FluxKnowledge.Cli.dll'),'index-projection','plan','--operation-id',$Plan.OperationId,'--target-format','2')){$start.ArgumentList.Add($argument)}
    $start.Environment['ConnectionStrings__FluxKnowledge']=Get-DeploymentSqlConnectionString
    $start.Environment['Usearch__RootPath']=$Plan.IndexRoot
    $child=[Diagnostics.Process]::Start($start)
    try {
        $output=$child.StandardOutput.ReadToEndAsync();$errorOutput=$child.StandardError.ReadToEndAsync()
        if(!$child.WaitForExit(30000)){$child.Kill($true);$child.WaitForExit();throw 'index-update-read-only-plan-timeout'}
        $text=$output.GetAwaiter().GetResult();[void]$errorOutput.GetAwaiter().GetResult()
        if($child.ExitCode -ne 0 -or $text.Length -gt 65536){throw 'index-update-read-only-plan-failed'}
        $value=$text | ConvertFrom-Json -AsHashtable -DateKind String
        if($value.Plan.OperationId -cne $Plan.OperationId -or $value.Plan.TargetFormat -ne 2){throw 'index-update-projection-plan-invalid'}
        Write-SegmentedUpdateJson $Path $value
        return (Get-FileHash -LiteralPath $Path).Hash
    }finally{$child.Dispose()}
}

function Invoke-SegmentedIisAdmission($Plan,$State,[scriptblock]$Action) {
    $payloads=@(foreach($role in @('Web','OutlookHost')) {
        $payload=@($Plan.Payloads | Where-Object Role -CEQ $role)[0]
        @{Role=$role;Root=$(if($role -ceq 'Web'){$CanonicalDeployRoot}else{$InteractiveHostRoot});ExpectedSha256=$payload.Sha256}
    })
    Invoke-IndexFormatAdmission -ConnectionString (Get-DeploymentSqlConnectionString) -Payloads $payloads `
        -ReadPayloadFingerprint {param($root) Get-HybridPayloadFingerprint $root} -WebIndexMode Segmented -OperationFormats @(2) `
        -ExpectedActiveGenerationId ([guid]$State.GenerationId) `
        -ExpectedConfigurationPath (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json') `
        -ExpectedConfigurationSha256 $Plan.DesiredConfigurationSha256 -ExpectedDatabaseIdentity $Plan.DatabaseIdentity -Action $Action
}

function Invoke-SegmentedIndexIisUpdate {
    param([Parameter(Mandatory)][string]$PlanPath,[Parameter(Mandatory)][string]$ExpectedPlanSha256,
        [Parameter(Mandatory)][string]$StopReceiptPath,[Parameter(Mandatory)][string]$ExpectedStopReceiptSha256,
        [switch]$OfflineMaintenance,[switch]$ResumeOfflineMaintenance)
    if($ExpectedPlanSha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or $ExpectedStopReceiptSha256 -cnotmatch '^[A-Fa-f0-9]{64}$'){throw 'index-update-reviewed-hashes-required'}
    $plan=Read-SegmentedUpdateJson $PlanPath $ExpectedPlanSha256
    if($plan.ContainsKey('OfflineMaintenance') -and $plan.OfflineMaintenance -isnot [bool]){throw 'index-update-offline-plan-invalid'}
    $plannedOffline=$plan.ContainsKey('OfflineMaintenance') -and [bool]$plan.OfflineMaintenance
    if(($OfflineMaintenance -or $ResumeOfflineMaintenance) -and !$plannedOffline){throw 'index-update-offline-mode-binding-changed'}
    $stop=Read-SegmentedUpdateJson $StopReceiptPath $ExpectedStopReceiptSha256
    $operation=Assert-SegmentedOperationRoot $plan.OperationRoot
    $release=Join-Path $IncrementalRecoveryRoot $plan.ReleaseId
    $projectionPath=Join-Path $operation 'projection-plan.json'
    $baselinePath=Join-Path $operation 'retained-baseline.json'
    $operator=@($plan.Payloads | Where-Object Role -CEQ 'Cli')[0].Root
    $mutex=[Threading.Mutex]::new($false,'Global\FluxKnowledge.IncrementalIisUpdate.v1');$owns=$false
    try {
        try{$owns=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owns=$true}
        if(!$owns){throw 'index-update-another-deployment-is-running'}
        Assert-IncrementalIisPreflight
        $ports=@{
            AssertStopped={param($after) Assert-SegmentedIisStopped $plan $stop $after}
            AssertBindings={param($state) Assert-SegmentedIisBindings $plan $state}
            UpgradeSchema={
                if(![IO.File]::Exists($baselinePath)){Write-SegmentedUpdateJson $baselinePath (Get-RetainedPipelineStateBaseline)}
                [void](Invoke-SegmentedIndexSchemaUpgrade -ConnectionString (Get-DeploymentSqlConnectionString) `
                    -ExpectedIdentity $plan.DatabaseIdentity -ExpectedHistory $plan.BaselineHistory `
                    -AssertQuiescent {Assert-SegmentedIisStopped $plan $stop})
            }
            BeginWriter={Start-RecoveryReferenceWrite -ConnectionString (Get-DeploymentSqlConnectionString) -RecoveryRoot $CanonicalRecoveryRoot -OwnerExitSufficient}
            BackupPayloads={
                [void](Assert-SegmentedUpdatePath $release);[void][IO.Directory]::CreateDirectory($release)
                Copy-SegmentedIndexPayload -Source $CanonicalDeployRoot -Destination (Join-Path $release 'previous') -ExpectedSha256 $plan.OriginalWebSha256
                Copy-SegmentedIndexPayload -Source $InteractiveHostRoot -Destination (Join-Path $release 'previous-interactive-host') -ExpectedSha256 $plan.OriginalCompanionSha256
                Write-SegmentedUpdateJson (Join-Path $release 'manifest.json') ([ordered]@{commit=$plan.Commit;staged_at_utc=$plan.CreatedUtc;
                    application_root=$CanonicalDeployRoot;migrations=$true;clean_slate=$false;reviewed_legacy_index_payloads=@()})
            }
            PlacePayloads={
                Assert-SegmentedIisStopped $plan $stop
                $web=@($plan.Payloads | Where-Object Role -CEQ 'Web')[0]
                $companion=@($plan.Payloads | Where-Object Role -CEQ 'OutlookHost')[0]
                Copy-SegmentedIndexPayload -Source $web.Root -Destination $CanonicalDeployRoot -ExpectedSha256 $web.Sha256
                Copy-SegmentedIndexPayload -Source $companion.Root -Destination $InteractiveHostRoot -ExpectedSha256 $companion.Sha256
                Test-ApplicationPayload $CanonicalDeployRoot;Assert-ApplicationPayloadReadAccess $CanonicalDeployRoot
                Test-InteractiveHostPayload $InteractiveHostRoot
            }
            CompleteWriter={param($lease) Complete-RecoveryReferenceWrite $lease}
            CloseWriter={param($lease) Close-RecoveryReferenceWrite $lease}
            CapturePlan={New-SegmentedIisProjectionPlan $plan $operator $projectionPath}
            Convert={
                param($state)
                $entry=@($plan.Payloads | Where-Object Role -CEQ 'Cli')[0]
                $bindings=@{PlanPath=$projectionPath;PlanSha256=$state.ProjectionPlanSha256;
                    ConfigurationSha256=(Get-FileHash -LiteralPath (Join-Path $CanonicalLiveRoot 'Config/appsettings.Production.json')).Hash;
                    HoldSha256=$plan.HoldSha256;DatabaseIncarnation=($plan.DatabaseIdentity.Split('|')[-1]);OperatorEntrySha256=$entry.EntrySha256} | ConvertTo-Json -Compress
                $result=Invoke-IncrementalProjectionOperator -OperatorRoot $operator -BindingsJson $bindings `
                    -AssertQuiescent {Assert-SegmentedIisStopped $plan $stop} -TimeoutSeconds $RebuildTimeoutSeconds
                return $result.RecordedGeneration
            }
            AssertCurrent={param($state) Assert-SegmentedIisCurrent $plan $state}
            Configure={Assert-SegmentedIisStopped $plan $stop;Set-SegmentedPublicationConfiguration $plan}
            Start={param($state)
                Invoke-SegmentedIisAdmission $plan $state {Start-WebAppPool -Name $SiteName}
                Wait-IisAppPoolState -Name $SiteName -ExpectedState Started -TimeoutSeconds $ReadinessTimeoutSeconds
            }
            Validate={param($state)
                Invoke-RequiredLoopbackProbes -Origin $loopbackOrigin.Origin -TimeoutSeconds $ReadinessTimeoutSeconds
                Assert-RetainedPipelineStateUnchanged -Baseline (Read-SegmentedUpdateJson $baselinePath) -Current (Get-RetainedPipelineStateBaseline)
                Assert-SegmentedIisCurrent $plan $state
            }
            Release={param($state)
                if((Get-FileHash -LiteralPath $ValidationHoldPath).Hash -cne $plan.HoldSha256){throw 'index-update-deny-all-hold-changed'}
                Invoke-SegmentedIisAdmission $plan $state {[IO.File]::Delete($ValidationHoldPath)}
            }
            EnsureHold={[void](New-DeploymentValidationHold -Path $ValidationHoldPath -ReleaseId $plan.HoldOwner)}
            AssertCompleted={param($state)
                Assert-SegmentedIisBindings $plan $state
                $database=Get-SegmentedIndexDatabaseState -ConnectionString (Get-DeploymentSqlConnectionString)
                if($database.RequiredFormat -ne 2){throw 'index-update-completed-format-changed'}
            }
        }
        foreach($key in @($ports.Keys)){$ports[$key]=$ports[$key].GetNewClosure()}
        $result=Invoke-SegmentedIndexUpdateFlow -StatePath (Join-Path $operation 'deployment-state.json') -PlanSha256 $ExpectedPlanSha256 -Ports $ports `
            -OfflineMaintenance:$plannedOffline -ResumeOfflineMaintenance:$ResumeOfflineMaintenance
        [ordered]@{release_id=$plan.ReleaseId;commit=$plan.Commit;generation_id=$result.GenerationId;format=2;completed=$result.Completed;
            offline_ready=[bool]$result.OfflineReady;phase=$(if($result.Completed){'completed'}elseif($result.OfflineReady){'offline-ready'}else{'incomplete'});
            deployment_state=(Join-Path $operation 'deployment-state.json');companion='preserved disabled';legacy_payloads='preserved; no retirement authority used'}
    }finally{if($owns){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
