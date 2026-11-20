#Requires -Version 7.5
[CmdletBinding(DefaultParameterSetName='Plan')]
param(
    [ValidatePattern('^[A-Za-z0-9]{0,40}$')][string]$StageName = '',
    [Parameter(ParameterSetName='Plan')][switch]$PlanOnly,
    [Parameter(Mandatory,ParameterSetName='Verify')][switch]$Verify,
    [Parameter(ParameterSetName='Verify')][switch]$RequireRunning,
    [Parameter(Mandatory,ParameterSetName='Apply')][switch]$Apply,
    [Parameter(ParameterSetName='Apply')][switch]$Resume,
    [Parameter(Mandatory,ParameterSetName='Apply')][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedPlanSha256
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root = 'I:\FluxKnowledgeSourceJournalOperator'
$taskName = 'FluxKnowledge.SourceJournalOperator'
$gate = 'Global\FluxKnowledge.IncrementalIisUpdate.v1'
if ($StageName) {
    $root += "-Staging-$StageName"
    $taskName += "-Staging-$StageName"
    $gate += "-Staging-$StageName"
}
$bindingPath = Join-Path $root 'binding.json'
$pendingPath = Join-Path $root 'refresh-pending.json'
$pwshPath = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell\7\pwsh.exe'
$arguments = '-NoLogo -NoProfile -NonInteractive -File "' + (Join-Path $root 'run-source-journal-allowlist-operator.ps1') + '" -BindingPath "' + $bindingPath + '"'
$package = @((Import-PowerShellDataFile (Join-Path $PSScriptRoot 'source-journal-allowlist-package.psd1')).Files)
if (!$package.Count -or @($package | Where-Object { $_ -cnotmatch '^[A-Za-z0-9][A-Za-z0-9.-]+\.(ps1|psm1|cs|sql)$' }).Count -or
    @($package | Sort-Object -Unique).Count -ne $package.Count) { throw 'operator-package-manifest-invalid' }
$module = Import-Module (Join-Path $PSScriptRoot 'source-journal-allowlist-activation.psm1') -PassThru -Force
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-PrivateBytes([string]$Path) {
    return ,(& $module { param($p) Read-AllowlistFile $p -Private } $Path)
}
function Hash-Bytes([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}
function Bytes-FromJson($Value) {
    return ,$utf8.GetBytes(($Value | ConvertTo-Json -Depth 12 -Compress))
}
function Write-New([string]$Path,[byte[]]$Bytes) {
    $file = [IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
    try { $file.Write($Bytes); $file.Flush($true) } finally { $file.Dispose() }
}
function Replace-Private([string]$Path,[byte[]]$Expected,[byte[]]$Bytes) {
    & $module { param($p,$e,$b) Save-AllowlistFile $p $e $b } $Path $Expected $Bytes
}
function Get-OperatorTask {
    $task = Get-ScheduledTask -TaskPath '\' -TaskName $taskName -ErrorAction Stop
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $registered = $scheduler.GetFolder('\').GetTask($taskName)
    $sddl = $registered.GetSecurityDescriptor(7)
    $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($sddl)
    $acl = [byte[]]::new($descriptor.BinaryLength)
    $descriptor.GetBinaryForm($acl,0)
    [FluxSourceAccessInstallFile]::ValidateDescriptor($acl,$false)
    if (@($descriptor.DiscretionaryAcl | Where-Object SecurityIdentifier -NotIn @('S-1-5-18','S-1-5-32-544')).Count) {
        throw 'operator-task-security-changed'
    }
    if ($task.Principal.UserId -cnotin @('SYSTEM','S-1-5-18') -or $task.Principal.LogonType -ne 'ServiceAccount' -or
        $task.Principal.RunLevel -ne 'Highest' -or @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ine $pwshPath -or
        $task.Actions[0].Arguments -cne $arguments -or $task.Actions[0].WorkingDirectory -ine $root -or $task.Settings.MultipleInstances -ne 'IgnoreNew') {
        throw 'operator-task-definition-changed'
    }
    # Enabled is restored separately and is the only task setting refresh changes.
    $xml = [xml]$registered.Xml
    $enabled = $xml.SelectSingleNode("//*[local-name()='Settings']/*[local-name()='Enabled']")
    if ($enabled) { $null = $enabled.ParentNode.RemoveChild($enabled) }
    return @{ Enabled=[bool]$task.Settings.Enabled; Running=$task.State -eq 'Running';
        Hash=(Hash-Bytes ($utf8.GetBytes($xml.OuterXml+'|'+$sddl))) }
}
function Get-OperatorProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -OperationTimeoutSec 5 | Where-Object {
        $_.ExecutablePath -ieq $pwshPath -and $_.CommandLine -clike "*$arguments"
    })
}
function Test-OperatorListener($Binding) {
    $rows = @(Get-OperatorProcesses)
    if ($rows.Count -ne 1) { return $false }
    $owner = Invoke-CimMethod -InputObject $rows[0] -MethodName GetOwnerSid -OperationTimeoutSec 5
    if ($owner.ReturnValue -ne 0 -or $owner.Sid -cne 'S-1-5-18' -or
        ([DateTime]$rows[0].CreationDate).ToUniversalTime() -lt [IO.File]::GetLastWriteTimeUtc($bindingPath)) { return $false }
    try {
        $reply = Invoke-WebRequest ($Binding.OperatorOrigin+'/status/00000000-0000-0000-0000-000000000001') -NoProxy -MaximumRedirection 0 -TimeoutSec 2 -SkipHttpErrorCheck
        return $reply.StatusCode -eq 401 -and [bool](@($reply.Headers['WWW-Authenticate']) -match '^(Negotiate|NTLM)')
    } catch { return $false }
}
function Assert-Settled($Binding) {
    foreach ($entry in (Get-ChildItem -LiteralPath $Binding.ReceiptDirectory -File -Filter '*.json')) {
        if ($entry.Name -notmatch '^(http|source-journal-allowlist)-[0-9a-f-]{36}\.json$') { continue }
        $record = $utf8.GetString((Read-PrivateBytes $entry.FullName)) | ConvertFrom-Json -AsHashtable
        $terminal = if ($entry.Name.StartsWith('http-')) { @('succeeded','rejected','rolled-back') } else { @('Completed','Reverted') }
        if ($record.Status -cin $terminal) { continue }
        if ($entry.Name.StartsWith('http-') -and (Get-ConfiguredSourceJournalAllowlistResult -Binding $Binding -OperationId ([guid]$record.OperationId) -ExpectedApprovedVolumes $record.ExpectedApprovedVolumes -ApprovedVolumes $record.ApprovedVolumes)) { continue }
        throw 'operator-refresh-allowlist-operation-unresolved'
    }
}
function Assert-InstalledPackage($Binding,$Task) {
    if ($Binding.Version -ne 2 -or [IO.File]::Exists($pendingPath)) { throw 'operator-refresh-missing-or-incomplete' }
    foreach ($name in $package) {
        if ((Hash-Bytes (Read-PrivateBytes (Join-Path $root $name))) -cne $newHashes[$name]) { throw 'operator-refresh-package-drift' }
    }
    if ($Task.Enabled -and (!$Task.Running -or !(Test-OperatorListener $Binding))) { throw 'operator-refresh-listener-not-confirmed' }
}
$newBytes = @{}
$newHashes = [ordered]@{}
foreach ($name in $package) {
    $source = Join-Path $PSScriptRoot $name
    if ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint) { throw 'operator-package-linked' }
    $newBytes[$name] = [IO.File]::ReadAllBytes($source)
    $newHashes[$name] = Hash-Bytes $newBytes[$name]
}
$mutex = $null
$locked = $false
try {
    if ($Apply) {
        $mutex = [Threading.Mutex]::new($false,$gate)
        try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (!$locked) { throw 'operator-refresh-deployment-busy' }
    }
    $binding = Read-SourceJournalAllowlistOperatorBinding $bindingPath
    Assert-Settled $binding
    $task = Get-OperatorTask
    if ($Verify) {
        if ($RequireRunning -and (!$task.Enabled -or !$task.Running)) { throw 'operator-refresh-listener-not-running' }
        Assert-InstalledPackage $binding $task
        return
    }
    if ($Resume) {
        $intent = $utf8.GetString((Read-PrivateBytes $pendingPath)) | ConvertFrom-Json -AsHashtable
        $plan = $intent.Plan
        $backupName = $intent.BackupName
        if ($backupName -cnotmatch '^refresh-[a-f0-9]{32}$') { throw 'operator-refresh-backup-name-invalid' }
    } else {
        if ([IO.File]::Exists($pendingPath)) { throw 'operator-refresh-resume-required' }
        $before = [ordered]@{}
        foreach ($name in @($package)+'binding.json') { $before[$name] = Hash-Bytes (Read-PrivateBytes (Join-Path $root $name)) }
        $backupName = 'refresh-' + [guid]::NewGuid().ToString('N')
        $plan = [ordered]@{ Root=$root; TaskName=$taskName; TaskHash=$task.Hash; Enabled=$task.Enabled; Running=$task.Running;
            From=$before; To=$newHashes }
    }
    $planHash = Hash-Bytes (Bytes-FromJson $plan)
    if (!$Apply) { [ordered]@{ Root=$root; TaskName=$taskName; FromVersion=$binding.Version; ToVersion=2; PlanSha256=$planHash } | ConvertTo-Json; return }
    if ($planHash -cne $ExpectedPlanSha256 -or $plan.Root -cne $root -or $plan.TaskName -cne $taskName -or $plan.TaskHash -cne $task.Hash -or
        (Hash-Bytes (Bytes-FromJson $plan.To)) -cne (Hash-Bytes (Bytes-FromJson $newHashes))) { throw 'operator-refresh-plan-changed' }

    if (!$Resume -and $binding.Version -eq 2 -and @($package | Where-Object { $plan.From[$_] -cne $newHashes[$_] }).Count -eq 0) {
        Assert-InstalledPackage $binding $task
        [ordered]@{ Mode='already-current'; TaskName=$taskName; Version=$binding.Version; PlanSha256=$planHash } | ConvertTo-Json
        return
    }
    $backup = Join-Path $binding.ReceiptDirectory $backupName
    if (!$Resume) {
        $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
        $bytes = [byte[]]::new($descriptor.BinaryLength)
        $descriptor.GetBinaryForm($bytes,0)
        $lease = [FluxSourceAccessInstallFile]::CreatePrivateDirectory($backup,$bytes)
        try {
            foreach ($name in @($package)+'binding.json') {
                $original = Read-PrivateBytes (Join-Path $root $name)
                if ((Hash-Bytes $original) -cne $plan.From[$name]) { throw 'operator-refresh-predecessor-changed' }
                Write-New (Join-Path $backup $name) $original
            }
            $stagedIntent = Join-Path $root ('.refresh-' + [guid]::NewGuid().ToString('N') + '.tmp')
            Write-New $stagedIntent (Bytes-FromJson ([ordered]@{ Plan=$plan; BackupName=$backupName }))
            [IO.File]::Move($stagedIntent,$pendingPath)
        } finally { $lease.Dispose() }
    }
    $oldBinding = $utf8.GetString((Read-PrivateBytes (Join-Path $backup 'binding.json'))) | ConvertFrom-Json -AsHashtable
    $oldBinding.Version = 2
    $newBytes['binding.json'] = Bytes-FromJson $oldBinding
    $null = Disable-ScheduledTask -TaskPath '\' -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    if (@(Get-OperatorProcesses).Count) { Stop-ScheduledTask -TaskPath '\' -TaskName $taskName }
    while (@(Get-OperatorProcesses).Count -or (Get-OperatorTask).Running) {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'operator-refresh-exit-unconfirmed' }
        Start-Sleep -Milliseconds 250
    }
    try { Assert-Settled $binding } catch {
        if (!$Resume) {
            if ($plan.Enabled) { $null = Enable-ScheduledTask -TaskPath '\' -TaskName $taskName }
            if ($plan.Running) { Start-ScheduledTask -TaskPath '\' -TaskName $taskName }
            [IO.File]::Move($pendingPath,(Join-Path $backup 'aborted-before-replacement.json'))
        }
        throw
    }
    foreach ($name in @($package)+'binding.json') {
        $old = Read-PrivateBytes (Join-Path $backup $name)
        $path = Join-Path $root $name
        $current = Read-PrivateBytes $path
        if ((Hash-Bytes $old) -cne $plan.From[$name]) { throw 'operator-refresh-backup-drift' }
        if ((Hash-Bytes $current) -ceq (Hash-Bytes $newBytes[$name])) { continue }
        if ((Hash-Bytes $current) -cne $plan.From[$name]) { throw 'operator-refresh-file-unrecognised' }
        Replace-Private $path $old $newBytes[$name]
    }
    $binding = Read-SourceJournalAllowlistOperatorBinding $bindingPath
    if ((Get-OperatorTask).Hash -cne $plan.TaskHash) { throw 'operator-refresh-task-changed' }
    if ($plan.Enabled) { $null = Enable-ScheduledTask -TaskPath '\' -TaskName $taskName }
    if ($plan.Running) {
        Start-ScheduledTask -TaskPath '\' -TaskName $taskName
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        while (!(Test-OperatorListener $binding)) {
            if ([DateTime]::UtcNow -ge $deadline) {
                $null = Disable-ScheduledTask -TaskPath '\' -TaskName $taskName
                Stop-ScheduledTask -TaskPath '\' -TaskName $taskName
                throw 'operator-refresh-listener-not-confirmed'
            }
            Start-Sleep -Milliseconds 250
        }
    }
    [IO.File]::Move($pendingPath,(Join-Path $backup 'completed.json'))
    [ordered]@{ Mode='refreshed'; TaskName=$taskName; Version=$binding.Version; PlanSha256=$planHash; Evidence=$backup } | ConvertTo-Json
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
