[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PayloadPath,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$PolicyPath,
    [Parameter(Mandatory)][string]$RootReadPlanPath,
    [Parameter(Mandatory)][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][string]$ExpectedPolicySha256,
    [Parameter(Mandatory)][string]$ExpectedRootReadPlanSha256,
    [string]$InstallRoot = 'I:\FluxKnowledgeSourceAccess',
    [string]$TaskName = 'FluxKnowledge.SourceAccess',
    [switch]$PlanOnly,
    [switch]$Apply,
    [switch]$ConfirmPrivilegedHelper,
    [switch]$ConfirmSourceRootReadGrant
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PlanOnly -eq $Apply) { throw 'Choose exactly one of -PlanOnly or -Apply.' }
if ($Apply -and (-not $ConfirmPrivilegedHelper -or -not $ConfirmSourceRootReadGrant)) {
    throw 'Applying requires explicit approval for both the privileged helper and the one-root read grant.'
}
if (-not $IsWindows) { throw 'The source-access broker requires Windows.' }
$brokerTaskName = $TaskName
if ($brokerTaskName -notmatch '^FluxKnowledge\.SourceAccess(?:-Staging-[A-Za-z0-9-]+)?$') { throw 'Use the fixed production task or an isolated staging task name.' }
$brokerInstallRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallRoot))
$volumeRoot = [IO.Path]::GetPathRoot($brokerInstallRoot)
if ([IO.Path]::GetDirectoryName($brokerInstallRoot) -ine $volumeRoot -or
    [IO.Path]::GetFileName($brokerInstallRoot) -notmatch '^FluxKnowledgeSourceAccess(?:-[A-Za-z0-9-]+)?$') {
    throw 'Use a dedicated FluxKnowledgeSourceAccess directory directly beneath a fixed NTFS volume root.'
}
if ($Apply -and ((Test-Path -LiteralPath $brokerInstallRoot) -or (Get-ScheduledTask -TaskName $brokerTaskName -ErrorAction SilentlyContinue))) {
    throw 'The helper directory or task already exists. Preserve it and prepare a reviewed update; this installer never overwrites an installation.'
}
$volume = [IO.DriveInfo]::new($volumeRoot)
if ($volume.DriveType -ne [IO.DriveType]::Fixed -or $volume.DriveFormat -ine 'NTFS') { throw 'The helper requires fixed NTFS storage.' }

# A held no-recall, no-follow file supplies attributes, physical path, bounded bytes
# and the checksum. No code is loaded from the candidate before protection/verification.
. (Join-Path $PSScriptRoot 'source-access-install-file.ps1')
[FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $volumeRoot).GetSecurityDescriptorBinaryForm(),$true)
function Read-SealedJson {
    param([string]$Path,[string]$ExpectedSha256)
    if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'An exact SHA-256 is required.' }
    $stream = [FluxSourceAccessInstallFile]::Read($Path,2097152)
    try {
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes)
        $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        if ($actual -ine $ExpectedSha256) { throw "Sealed input checksum changed: $Path" }
        return [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -DateKind String
    } finally { $stream.Dispose() }
}
$manifest = Read-SealedJson $ManifestPath $ExpectedManifestSha256
$policy = Read-SealedJson $PolicyPath $ExpectedPolicySha256
$rootReadPlan = Read-SealedJson $RootReadPlanPath $ExpectedRootReadPlanSha256
$payloadCandidateRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PayloadPath))
if ($manifest.version -ne 1 -or $manifest.commit -notmatch '^[0-9a-f]{40}$' -or @($manifest.files).Count -eq 0 -or
    $policy.Version -ne 1 -or $rootReadPlan.Version -ne 1 -or
    $policy.IndexerSid -cne $rootReadPlan.IndexerSid -or $rootReadPlan.ActualIndexerReadVerified -ne $false -or
    @($policy.Roots | Where-Object { $_.Path -ceq $rootReadPlan.RootPath -and $_.IdentityFingerprint -ceq $rootReadPlan.RootIdentityFingerprint -and $_.InheritableRead }).Count -ne 1) {
    throw 'The sealed payload identity, fixed policy and one-root grant plan do not match.'
}
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $relative = [string]$entry.relativePath
    $relative = $relative.Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Contains([char]0) -or
        @($relative.Split(@('\','/'),[StringSplitOptions]::None) | Where-Object { $_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count -ne 0 -or
        -not $names.Add($relative) -or $entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$entry.byteLength -lt 0 -or
        $relative -match '(?i)\.(bin|gguf|onnx|safetensors|pth|pt|model)$|(^|[\\/])(tokenizer\.json|vocab\.json|merges\.txt)$|runtimeconfig\.dev\.json$') {
        throw 'The payload contains an ambiguous path, duplicate, model payload or development probing configuration.'
    }
    $source = Join-Path $payloadCandidateRoot $relative
    $stream = [FluxSourceAccessInstallFile]::Read($source,[long]$entry.byteLength)
    try {
        if ($stream.Length -ne [long]$entry.byteLength -or
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) -ine $entry.sha256) { throw "Payload checksum changed: $relative" }
    } finally { $stream.Dispose() }
}
foreach ($required in @('FluxKnowledge.Cli.exe','FluxKnowledge.Cli.dll','FluxKnowledge.Integrations.dll','FluxKnowledge.Cli.deps.json','FluxKnowledge.Cli.runtimeconfig.json')) {
    if (-not $names.Contains($required)) { throw "Missing required helper payload: $required" }
}
$plan = [ordered]@{
    mode = $(if($Apply){'apply'}else{'plan-only'}); commit=$manifest.commit; install_root=$brokerInstallRoot
    task=$brokerTaskName; principal='NT AUTHORITY\SYSTEM'; task_action=(Join-Path $brokerInstallRoot 'payload\FluxKnowledge.Cli.exe')
    task_arguments=('source-access serve --broker-root "{0}"' -f $brokerInstallRoot); task_working_directory=(Join-Path $brokerInstallRoot 'payload')
    pipe=$policy.PipeName; indexer_sid=$policy.IndexerSid; source_root=$rootReadPlan.RootPath; physical_identity=$rootReadPlan.RootIdentityFingerprint
    source_grant='One root only: add missing 0x100081 read rights with object/container inheritance; preserve all existing ACEs, owner, group and inheritance/protection flags.'
    existing_children_rewritten=$false; source_contents_changed=$false; onedrive_configuration_changed=$false
    payload_manifest_sha256=$ExpectedManifestSha256; policy_sha256=$ExpectedPolicySha256; root_read_plan_sha256=$ExpectedRootReadPlanSha256
    activation='Register and start one protected startup task. IIS client activation is a separate approved incremental application/configuration update.'
    rollback='Disable the IIS client with a reviewed incremental update; stop/remove only this task. Preserve source grants and pending intents for separately reviewed removal/verification.'
}
if ($PlanOnly) { $plan | ConvertTo-Json -Depth 10; return }

$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
try { if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'An elevated administrator is required.' } }
finally { $identity.Dispose() }
foreach ($name in @('DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_ROOT','DOTNET_ROOT_X64','CORECLR_ENABLE_PROFILING','COR_ENABLE_PROFILING','COMPLUS_ApplicationMigrationRuntimeActivationConfigPath')) {
    foreach ($target in @('Machine','Process')) {
        if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name,$target))) { throw "Review the runtime override before elevated execution: $name ($target)" }
    }
}
$privateSecurity = [Security.AccessControl.DirectorySecurity]::new()
$privateSecurity.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
# Exclusively create and pin the validated private namespace before the first child write.
$installationHandle = [FluxSourceAccessInstallFile]::CreatePrivateDirectory($brokerInstallRoot,$privateSecurity.GetSecurityDescriptorBinaryForm())
try {
if (@(Get-ChildItem -LiteralPath $brokerInstallRoot -Force).Count -ne 0) { throw 'The new helper directory is not empty; preserve it for review.' }
$payloadDestination = Join-Path $brokerInstallRoot 'payload'
$null = [IO.Directory]::CreateDirectory($payloadDestination)
$null = [IO.Directory]::CreateDirectory((Join-Path $brokerInstallRoot 'journal'))
function Copy-SealedFile {
    param([string]$Source,[string]$Destination,[string]$Sha256,[long]$Length)
    $inputFile = [FluxSourceAccessInstallFile]::Read($Source,$Length)
    try {
        if ($inputFile.Length -ne $Length) { throw 'A sealed input changed length.' }
        $outputFile = [IO.FileStream]::new($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $inputFile.CopyTo($outputFile); $outputFile.Flush($true) } finally { $outputFile.Dispose() }
    } finally { $inputFile.Dispose() }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ine $Sha256) { throw 'A protected copy failed its checksum.' }
}
foreach ($entry in $manifest.files) {
    [FluxSourceAccessInstallFile]::ValidatePrivateDirectory($installationHandle,$brokerInstallRoot)
    $destination = Join-Path $payloadDestination $entry.relativePath
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    Copy-SealedFile (Join-Path $payloadCandidateRoot $entry.relativePath) $destination $entry.sha256 ([long]$entry.byteLength)
}
foreach ($item in @(
    @{source=$PolicyPath; name='policy.json'; hash=$ExpectedPolicySha256},
    @{source=$RootReadPlanPath; name='root-read-plan.json'; hash=$ExpectedRootReadPlanSha256},
    @{source=$ManifestPath; name='payload-manifest.json'; hash=$ExpectedManifestSha256}
)) {
    Copy-SealedFile $item.source (Join-Path $brokerInstallRoot $item.name) $item.hash (Get-Item -LiteralPath $item.source).Length
}
# Verify ownership and every effective allow ACE for the complete private execution chain.
foreach ($item in @((Get-Item -LiteralPath $brokerInstallRoot)) + @(Get-ChildItem -LiteralPath $brokerInstallRoot -Recurse -Force)) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'A protected payload path became a reparse point.' }
    [FluxSourceAccessInstallFile]::ValidateDescriptor((Get-Acl -LiteralPath $item.FullName).GetSecurityDescriptorBinaryForm(),$false)
}
[FluxSourceAccessInstallFile]::ValidatePrivateDirectory($installationHandle,$brokerInstallRoot)
$helperExe = Join-Path $payloadDestination 'FluxKnowledge.Cli.exe'
$runtimeConfig=Get-Content -LiteralPath (Join-Path $payloadDestination 'FluxKnowledge.Cli.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'additionalProbingPaths' -or
    $runtimeConfig.runtimeOptions.PSObject.Properties.Name -contains 'startupHooks') { throw 'The SYSTEM helper cannot use additional probing paths or startup hooks.' }
& $helperExe source-access apply-root-read --broker-root $brokerInstallRoot --plan-sha256 $ExpectedRootReadPlanSha256
if ($LASTEXITCODE -ne 0) { throw 'The approved root read grant did not complete. Preserve the installation, plan and any pending intent; do not replay.' }
$action=New-ScheduledTaskAction -Execute $helperExe -Argument $plan.task_arguments -WorkingDirectory $payloadDestination
$principal=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
$settings.Hidden=$true
$task=New-ScheduledTask -Action $action -Principal $principal -Settings $settings -Trigger (New-ScheduledTaskTrigger -AtStartup)
$null=Register-ScheduledTask -TaskName $brokerTaskName -InputObject $task
$scheduler=New-Object -ComObject 'Schedule.Service'; $scheduler.Connect()
$registered=$scheduler.GetFolder('\').GetTask($brokerTaskName)
$registered.SetSecurityDescriptor('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)',0)
$taskSecurity=[Security.AccessControl.RawSecurityDescriptor]::new($registered.GetSecurityDescriptor(7))
if ($taskSecurity.Owner.Value -notin @('S-1-5-18','S-1-5-32-544') -or $null -eq $taskSecurity.DiscretionaryAcl) { throw 'The task descriptor is not administrator controlled.' }
foreach ($ace in $taskSecurity.DiscretionaryAcl) {
    if ($ace -isnot [Security.AccessControl.CommonAce] -or ($ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and
        $ace.SecurityIdentifier.Value -notin @('S-1-5-18','S-1-5-32-544'))) { throw 'The task grants access to an unexpected principal.' }
}
if ($registered.Definition.Actions.Count -ne 1 -or $registered.Definition.Actions.Item(1).Path -ine $helperExe -or
    $registered.Definition.Actions.Item(1).Arguments -cne $plan.task_arguments -or $registered.Definition.Actions.Item(1).WorkingDirectory -ine $payloadDestination -or
    $registered.Definition.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')) { throw 'The registered task identity or action differs from the sealed installation.' }
$plan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $brokerInstallRoot 'installation-receipt.json') -Encoding utf8
Start-ScheduledTask -TaskName $brokerTaskName
$plan | ConvertTo-Json -Depth 10
} finally { $installationHandle.Dispose() }
