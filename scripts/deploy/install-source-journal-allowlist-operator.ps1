[CmdletBinding(DefaultParameterSetName = 'Plan')]
param(
    [AllowEmptyString()][ValidatePattern('^(?:[A-Za-z0-9]{1,40})?$')][string]$StageName = '',
    [Parameter(Mandatory)][ValidateRange(1024,65535)][int]$DashboardPort,
    [Parameter(Mandatory)][ValidateRange(1024,65535)][int]$OperatorPort,
    [Parameter(Mandatory)][ValidateCount(1,64)][string[]]$OperatorSid,
    [Parameter(Mandatory)][string]$DatabaseIdentity,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$WebSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$OutlookSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$HelperSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$TaskDefinitionSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$TaskSecuritySha256,
    [Parameter(Mandatory)][string]$JournalPipeName,
    [string[]]$StageVolumeIds = @(),
    [Parameter(ParameterSetName = 'Plan')][switch]$PlanOnly,
    [Parameter(Mandatory, ParameterSetName = 'Install')][switch]$Install,
    [Parameter(Mandatory, ParameterSetName = 'Install')][ValidatePattern('^[A-F0-9]{64}$')][string]$ExpectedPlanSha256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'The journal operator requires Windows.' }
$staging = $StageName.Length -gt 0
if ($OperatorPort -eq 5137 -or $OperatorPort -eq $DashboardPort -or ($DashboardPort -eq 5137) -eq $staging) {
    throw 'Dashboard and operator ports must be distinct and bound to the selected environment.'
}
$expectedCatalog = if ($staging) { "FluxKnowledge_Staging_$StageName" } else { 'FluxKnowledge' }
$db = $DatabaseIdentity.Split('|')
if ($db.Count -ne 3 -or $db[0] -cnotmatch '^[A-Za-z0-9.\\_-]{1,128}$' -or $db[1] -cne $expectedCatalog -or
    $db[2] -notmatch '^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$') { throw 'Database identity must match the exact staged or production catalogue.' }
if (($staging -and $JournalPipeName -cne "FluxKnowledge.SourceJournal.Staging-$StageName") -or
    (!$staging -and ($JournalPipeName -cnotmatch '^FluxKnowledge\.SourceJournal\.[A-Za-z0-9.-]{1,91}$' -or
        $JournalPipeName -imatch '^FluxKnowledge\.SourceJournal\.Staging-'))) { throw 'Journal pipe does not match the selected environment.' }
if (($staging -and ($StageVolumeIds.Count -ne 2 -or ($StageVolumeIds | Sort-Object -Unique).Count -ne 2 -or
    @($StageVolumeIds | Where-Object { $_ -cnotmatch '\A\\\\\?\\VOLUME\{[0-9A-F]{8}(?:-[0-9A-F]{4}){3}-[0-9A-F]{12}\}\\\z' }).Count)) -or
    (!$staging -and $StageVolumeIds.Count)) { throw 'Supply the exact two canonical disposable staging volume identities.' }
$admitted = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($sid in $OperatorSid) {
    if ($sid -cnotmatch '^S-1-(?:5-21|12-1)(?:-[0-9]{1,10}){4}$' -or
        [Security.Principal.SecurityIdentifier]::new($sid).Value -cne $sid -or !$admitted.Add($sid)) {
        throw 'Supply distinct exact Windows user SIDs for the journal operators.'
    }
}
$liveRoot = if ($staging) { "I:\Codex Workspaces\Staging\FluxKnowledge\$StageName" } else { 'I:\FluxKnowledge' }
$taskName = if ($staging) { "FluxKnowledge.SourceJournalOperator-Staging-$StageName" } else { 'FluxKnowledge.SourceJournalOperator' }
$installRoot = if ($staging) { "I:\FluxKnowledgeSourceJournalOperator-Staging-$StageName" } else { 'I:\FluxKnowledgeSourceJournalOperator' }
$receiptDirectory = Join-Path $installRoot 'Receipts'
$bindingPath = Join-Path $installRoot 'binding.json'
$pwsh = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell\7\pwsh.exe'
$entryPoint = Join-Path $installRoot 'run-source-journal-allowlist-operator.ps1'
$arguments = '-NoLogo -NoProfile -NonInteractive -File "' + $entryPoint + '" -BindingPath "' + $bindingPath + '"'
$binding = [ordered]@{
    Version = 1; StageName = $StageName
    OperatorOrigin = "http://localhost:$OperatorPort"; DashboardOrigin = "http://127.0.0.1:$DashboardPort"
    OperatorSids = @($OperatorSid | Sort-Object -CaseSensitive); ReceiptDirectory = $receiptDirectory
    DatabaseIdentity = $DatabaseIdentity; WebSha256 = $WebSha256; OutlookSha256 = $OutlookSha256; HelperSha256 = $HelperSha256
    TaskDefinitionSha256 = $TaskDefinitionSha256; TaskSecuritySha256 = $TaskSecuritySha256; JournalPipeName = $JournalPipeName
    StageVolumes = @($StageVolumeIds | Sort-Object -CaseSensitive)
}
$packageFiles = @(
    'run-source-journal-allowlist-operator.ps1', 'source-journal-allowlist-operator-http.psm1', 'source-journal-allowlist-transport.cs', 'source-journal-allowlist-activation.psm1',
    'source-journal-allowlist-owners.ps1', 'source-journal-allowlist-transaction.psm1',
    'source-access-install-file.ps1', 'source-access-install-file.cs',
    'incremental-source-access-config.psm1', 'incremental-hybrid-passage-rebuild.psm1',
    'incremental-source-journal-migration.psm1', 'incremental-vector-publication-metadata-migration.psm1',
    'incremental-source-deletion-pending-queue-index.psm1', 'incremental-passage-window-eligibility-migration.psm1',
    'incremental-source-journal-helper-task.psm1', 'index-format-admission.psm1',
    'IndexEntryMetadata.cs', 'source-journal-schema-contract.sql', 'passage-window-eligibility-schema.sql',
    'outlook-watched-source-schema.sql', 'codex-workspace-notes-schema.sql',
    'media-transcription-gpu-schema.sql', 'gpu-model-residency-schema.sql'
)
function Get-InstallerHash([byte[]]$Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}
$contents = @{}
$hashes = [ordered]@{}
foreach ($name in $packageFiles) {
    $path = Join-Path $PSScriptRoot $name
    if ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) { throw 'Operator package contains a linked file.' }
    $contents[$name] = [IO.File]::ReadAllBytes($path)
    $hashes[$name] = Get-InstallerHash $contents[$name]
}
$plan = [ordered]@{
    InstallRoot = $installRoot; TaskName = $taskName; Executor = $pwsh; Arguments = $arguments
    Binding = $binding; Package = $hashes
}
$encoding = [Text.UTF8Encoding]::new($false)
$planHash = Get-InstallerHash ($encoding.GetBytes(($plan | ConvertTo-Json -Depth 6 -Compress)))
if (!$Install) {
    $plan.Mode = 'plan-only'
    $plan.PlanSha256 = $planHash
    $plan | ConvertTo-Json -Depth 6
    return
}
if ($planHash -cne $ExpectedPlanSha256) { throw 'The operator installation differs from the reviewed plan.' }
if (!(Test-Path -LiteralPath $liveRoot -PathType Container) -or !(Test-Path -LiteralPath $pwsh -PathType Leaf) -or
    (Test-Path -LiteralPath $installRoot) -or (Get-ScheduledTask -TaskPath '\' -TaskName $taskName -ErrorAction SilentlyContinue)) {
    throw 'The operator installer requires an existing deployment, machine PowerShell 7 and unused operator targets.'
}
. (Join-Path $PSScriptRoot 'source-access-install-file.ps1')
[FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath 'I:\').GetSecurityDescriptorBinaryForm(),$true)
$trustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$localSystem = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
for ($member = $pwsh; $member; $member = [IO.Path]::GetDirectoryName($member)) {
    if ([IO.File]::GetAttributes($member) -band [IO.FileAttributes]::ReparsePoint) {
        throw 'PowerShell must resolve through an unlinked installation namespace.'
    }
    # Windows servicing may own and write the Program Files chain. Map that one
    # well-known service SID only for the existing ACL verifier's trust decision.
    $acl = [Security.AccessControl.RawSecurityDescriptor]::new((Get-Acl -LiteralPath $member).GetSecurityDescriptorBinaryForm(),0)
    if ($acl.Owner.Value -ceq $trustedInstaller) { $acl.Owner = $localSystem }
    foreach ($ace in $acl.DiscretionaryAcl) {
        if ($ace -is [Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier.Value -ceq $trustedInstaller) {
            $ace.SecurityIdentifier = $localSystem
        }
    }
    $verifiedAcl = [byte[]]::new($acl.BinaryLength)
    $acl.GetBinaryForm($verifiedAcl,0)
    [FluxSourceAccessInstallFile]::ValidateDescriptor($verifiedAcl,$member.EndsWith('\'))
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($pwsh)
if ([version]::new($version.FileMajorPart,$version.FileMinorPart) -lt [version]'7.5') {
    throw 'Install PowerShell 7.5 or later in its machine-protected location.'
}
$executor = [FluxSourceAccessInstallFile]::Read($pwsh,64MB)
$descriptor = [Security.AccessControl.RawSecurityDescriptor]::new('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
$securityBytes = [byte[]]::new($descriptor.BinaryLength)
$descriptor.GetBinaryForm($securityBytes,0)
$directory = $null
try {
    $directory = [FluxSourceAccessInstallFile]::CreatePrivateDirectory($installRoot,$securityBytes)
    foreach ($name in $packageFiles) {
        $path = Join-Path $installRoot $name
        [IO.File]::WriteAllBytes($path, $contents[$name])
        if ((Get-FileHash -LiteralPath $path).Hash -cne $hashes[$name]) { throw 'Installed operator package failed verification.' }
    }
    $receiptLease = [FluxSourceAccessInstallFile]::CreatePrivateDirectory($receiptDirectory,$securityBytes)
    $receiptLease.Dispose()
    [IO.File]::WriteAllText($bindingPath,($binding | ConvertTo-Json -Depth 5 -Compress),$encoding)
    Import-Module (Join-Path $installRoot 'source-journal-allowlist-activation.psm1') -Force
    $null = Read-SourceJournalAllowlistOperatorBinding -Path $bindingPath
    $action = New-ScheduledTaskAction -Execute $pwsh -Argument $arguments -WorkingDirectory $installRoot
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settingsOptions = @{
        Disable = $true; ExecutionTimeLimit = [TimeSpan]::Zero; MultipleInstances = 'IgnoreNew'
        AllowStartIfOnBatteries = $true; DontStopIfGoingOnBatteries = $true
        RestartCount = 3; RestartInterval = [TimeSpan]::FromMinutes(1)
    }
    $settings = New-ScheduledTaskSettingsSet @settingsOptions
    $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal -Settings $settings
    $null = Register-ScheduledTask -TaskPath '\' -TaskName $taskName -InputObject $task
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $registered = $scheduler.GetFolder('\').GetTask($taskName)
    $registered.SetSecurityDescriptor('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)',0x10)
    $security = [Security.AccessControl.RawSecurityDescriptor]::new($registered.GetSecurityDescriptor(7))
    if ($security.Owner.Value -cnotin @('S-1-5-18','S-1-5-32-544') -or
        @($security.DiscretionaryAcl).Count -ne 2 -or
        @($security.DiscretionaryAcl | Where-Object { $_.SecurityIdentifier.Value -cnotin @('S-1-5-18','S-1-5-32-544') }).Count) {
        throw 'Installed operator task security could not be verified. The task is disabled.'
    }
    $null = Enable-ScheduledTask -TaskPath '\' -TaskName $taskName
    Start-ScheduledTask -TaskPath '\' -TaskName $taskName
    $ready = $false
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $rows = @(Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" -OperationTimeoutSec 3 | Where-Object {
            $_.ExecutablePath -ieq $pwsh -and $_.CommandLine -clike "*$arguments"
        })
        if ($rows.Count -eq 1 -and (Get-ScheduledTask -TaskPath '\' -TaskName $taskName).State -eq 'Running') {
            $owner = Invoke-CimMethod -InputObject $rows[0] -MethodName GetOwnerSid -OperationTimeoutSec 3
            if ($owner.ReturnValue -eq 0 -and $owner.Sid -ceq 'S-1-5-18') {
                try {
                    $response = Invoke-WebRequest -Uri ($binding.OperatorOrigin + '/status/00000000-0000-0000-0000-000000000001') -NoProxy -MaximumRedirection 0 -TimeoutSec 2 -SkipHttpErrorCheck
                    $ready = $response.StatusCode -eq 401 -and @($response.Headers['WWW-Authenticate']) -match '^(Negotiate|NTLM)'
                } catch { $ready = $false }
            }
        }
        if (!$ready) { Start-Sleep -Milliseconds 250 }
    } while (!$ready -and $watch.Elapsed.TotalSeconds -lt 15)
    $installStatus = if ($ready) { 'verified-listening' } else { 'startup-unverified' }
    $summary = [ordered]@{ Mode=$installStatus; TaskName=$taskName; Origin=$binding.OperatorOrigin; PlanSha256=$planHash }
    [IO.File]::WriteAllText((Join-Path $installRoot 'installation-receipt.json'),($summary | ConvertTo-Json -Compress),$encoding)
    if (!$ready) { throw 'The operator task was installed but its authenticated listener could not be proved. Preserve the installation receipt.' }
    $summary | ConvertTo-Json
} finally {
    if ($directory) { $directory.Dispose() }
    $executor.Dispose()
}
