[CmdletBinding()]
param([string]$SourceRoot=(Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-source-journal-migration.psm1') -Force
function Assert-True([bool]$Value,[string]$Message){if(!$Value){throw $Message}}
function Expect-Refusal([scriptblock]$Action){$refused=$false;try{&$Action}catch{$refused=$true};Assert-True $refused 'Changed SQL was executed.'}
$bytes=Read-SourceJournalMigrationSql
$expected=[Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
$area=Join-Path 'I:\Codex Workspaces\Staging\FluxKnowledge' ('JournalSqlBytes-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $area
$path=Join-Path $area 'reviewed.sql';[IO.File]::WriteAllBytes($path,$bytes)
$box=@{Calls=0}
try{
 $inputBytes=Read-SourceJournalMigrationSql -Path $path
 Invoke-SourceJournalVerifiedSql -SqlBytes $inputBytes -RunSql {
  param($sql)
  $box.Calls++;$inputBytes[0]=[byte]($inputBytes[0] -bxor 1)
  [IO.File]::WriteAllText($path,'A changed path must never be reopened for execution.')
  Assert-True ($sql -ceq $expected) 'Mutation changed the verified SQL text.'
 }
 Expect-Refusal {Read-SourceJournalMigrationSql -Path $path}
 Expect-Refusal {Invoke-SourceJournalVerifiedSql -SqlBytes $inputBytes -RunSql {$box.Calls++}}
 Expect-Refusal {Invoke-SourceJournalVerifiedSql -SqlBytes $bytes -RunSql {param($sql);$box.Calls++;throw 'owned SQL execution fault'}}
 Assert-True ($box.Calls -eq 2) 'Invalid bytes reached execution or a failed attempt was retried.'
 [pscustomobject]@{Passed=4;SqlCalls=0;ProductionCalls=0;ExactArtifactSha256=(Get-SourceJournalMigrationContract).UpSha256}|ConvertTo-Json
}finally{
 $resolved=[IO.Path]::GetFullPath($area)
 if(!$resolved.StartsWith('I:\Codex Workspaces\Staging\FluxKnowledge\JournalSqlBytes-',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe SQL byte fixture cleanup.'}
 Remove-Item -LiteralPath $resolved -Recurse -Force
}
