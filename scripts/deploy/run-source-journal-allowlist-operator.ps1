#Requires -Version 7.5
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BindingPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!$identity.IsSystem) { throw 'Run the installed journal allowlist operator as Local System.' }
} finally { $identity.Dispose() }

Import-Module (Join-Path $PSScriptRoot 'source-journal-allowlist-activation.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'source-journal-allowlist-operator-http.psm1') -Force
$binding = Read-SourceJournalAllowlistOperatorBinding -Path $BindingPath
Start-SourceJournalAllowlistOperatorListener -Binding ([hashtable]$binding)
