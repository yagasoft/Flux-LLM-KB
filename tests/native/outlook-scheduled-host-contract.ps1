[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $SourceRoot 'scripts\deploy\run-outlook-host.ps1'
$closeout = Join-Path $SourceRoot 'scripts\dev\complete-feature.ps1'
foreach ($path in @($launcher, $closeout)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required disabled-Outlook contract file is missing: $path"
    }
}

$text = Get-Content -LiteralPath $launcher -Raw
if ($text -match '(?m)^\s*param\s*\(') {
    throw 'The Outlook launcher must not accept task arguments.'
}
if ($text -notmatch 'ConnectionStrings__FluxKnowledge' -or
    $text -notmatch '(?s)try\s*\{.*finally\s*\{') {
    throw 'The launcher must scope and clear the SQL connection value.'
}
if ($text -notmatch '--run-once' -or
    $text -match '--verbose-com-errors|spool|mailbox|credential|https?://') {
    throw 'The launcher action is not the fixed non-diagnostic local host invocation.'
}
if ($text -notmatch '\$PSScriptRoot' -or
    $text -notmatch 'appsettings\.Production\.json') {
    throw 'The launcher must resolve the local production settings from its installed directory.'
}

$closeoutText = Get-Content -LiteralPath $closeout -Raw
foreach ($obsolete in @(
    'outlook-scheduled-host-contract',
    'outlook-host-composition',
    'validate-native-outlook-ingress',
    'validate-native-worker-supervision',
    'deploy-native-windows')) {
    if ($closeoutText -match [regex]::Escape($obsolete)) {
        throw "The native closeout retains obsolete Outlook or worker deployment validation: $obsolete"
    }
}
foreach ($activationCommand in @(
    'Register-ScheduledTask', 'Enable-ScheduledTask', 'Start-ScheduledTask',
    'Register-OutlookHostTask', 'Install-OutlookHostTask')) {
    if ($closeoutText -match ("\b{0}\b" -f [regex]::Escape($activationCommand))) {
        throw "The native closeout retains an Outlook activation command: $activationCommand"
    }
}

Write-Output 'Outlook scheduled host disabled-only contract passed.'
