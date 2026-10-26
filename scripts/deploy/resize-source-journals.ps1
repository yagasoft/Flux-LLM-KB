[CmdletBinding()]
param(
 [switch]$PlanOnly,[switch]$Apply,
 [ValidateSet('E:','I:')][string[]]$Drive=@('E:','I:'),
 [string]$ApplicationCursorEvidencePath='',
 [string]$ReviewedPlanPath='', [string]$ExpectedPlanSha256='',
 [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($PlanOnly -eq $Apply -or ![OperatingSystem]::IsWindows()) { throw 'Choose PlanOnly or Apply on Windows.' }
if (@($Drive|Select-Object -Unique).Count -ne $Drive.Count) { throw 'Duplicate drive targets are forbidden.' }
if ($PlanOnly -and ($ReviewedPlanPath -or $ExpectedPlanSha256)) { throw 'PlanOnly cannot consume an application plan.' }
if ($Apply -and (!$ReviewedPlanPath -or $ExpectedPlanSha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or !$ApplicationCursorEvidencePath)) { throw 'Apply requires an exact reviewed plan hash and current application-cursor evidence.' }

function Assert-NoReparsePath([string]$Path) {
 $current=[IO.Path]::GetFullPath($Path)
 while ($current) {
  if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Operator evidence namespace is a reparse point.' }
  $current=[IO.Path]::GetDirectoryName($current)
 }
}
function Read-OperatorFile([string]$Path) {
 Assert-NoReparsePath $Path
 $file=[IO.FileStream]::new([IO.Path]::GetFullPath($Path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 try {
  if ($file.Length -le 0 -or $file.Length -gt 1048576) { throw 'Operator evidence length is invalid.' }
  $bytes=[byte[]]::new([int]$file.Length);$file.ReadExactly($bytes)
  [pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Bytes=$bytes;
   Sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))}
 } finally { $file.Dispose() }
}
function Read-OperatorEvidence([string]$Path) {
 $file=Read-OperatorFile $Path
 [pscustomobject]@{Document=([Text.UTF8Encoding]::new($false,$true).GetString($file.Bytes).TrimStart([char]0xfeff)|ConvertFrom-Json -DateKind String);Sha256=$file.Sha256}
}
function Read-OperatorCode([string]$Path) {
 $file=Read-OperatorFile $Path
 [pscustomobject]@{Path=$file.Path;Name=[IO.Path]::GetFileName($file.Path);Sha256=$file.Sha256;
  Text=[Text.UTF8Encoding]::new($false,$true).GetString($file.Bytes).TrimStart([char]0xfeff)}
}
function Assert-OperatorEntryCode($Source,[string]$ParsedText) {
 if ($Source.Text -cne $ParsedText) { throw 'The parsed entry script differs from the pinned operator source.' }
}
function Import-VerifiedOperatorCode([object[]]$Sources) {
 if ($Sources.Count -ne 3) { throw 'Three exact operator sources are required.' }
 foreach ($source in $Sources) {
  if ((Read-OperatorCode $source.Path).Sha256 -cne $source.Sha256) { throw 'Operator code changed between review and load.' }
 }
 # Load exact verified buffers. A later path edit cannot change executed dependencies.
 $module=New-Module -Name ('FluxKnowledge.JournalResize.'+[guid]::NewGuid().ToString('N')) -ScriptBlock ([scriptblock]::Create($Sources[1].Text))
 Import-Module $module -Global
 Add-Type -TypeDefinition $Sources[2].Text
 return $module.Name
}
$EvidenceDirectory=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($EvidenceDirectory))
if (!$EvidenceDirectory.StartsWith('I:\Codex Workspaces\Operational Evidence\FluxKnowledge\',[StringComparison]::OrdinalIgnoreCase) -or
    !(Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) { throw 'Use an existing dedicated FluxKnowledge operational-evidence directory on I:.' }
Assert-NoReparsePath $EvidenceDirectory
$operatorFiles=@($PSCommandPath,(Join-Path $PSScriptRoot 'source-journal-resize.psm1'),(Join-Path $PSScriptRoot 'source-journal-operator.cs'))
$operatorSources=@($operatorFiles|ForEach-Object {Read-OperatorCode $_})
Assert-OperatorEntryCode $operatorSources[0] $MyInvocation.MyCommand.ScriptBlock.Ast.Extent.Text
$operatorHashes=@($operatorSources|Select-Object Name,Sha256)
$applicationCursors=@();$cursorEvidenceHash=$null
if ($ApplicationCursorEvidencePath) {
 $inputEvidence=Read-OperatorEvidence $ApplicationCursorEvidencePath
 $document=$inputEvidence.Document
 if ($document.version -ne 1 -or $document.completeApplicationCursorSnapshot -isnot [bool] -or $document.completeApplicationCursorSnapshot -ne $true -or [string]::IsNullOrWhiteSpace($document.source)) { throw 'A complete, attributable application cursor snapshot is required.' }
 $captured=[DateTimeOffset]::Parse($document.capturedAtUtc,[Globalization.CultureInfo]::InvariantCulture)
 if ($captured -lt [DateTimeOffset]::UtcNow.AddMinutes(-15) -or $captured -gt [DateTimeOffset]::UtcNow.AddSeconds(30)) { throw 'Application cursor evidence is stale or in the future.' }
 $applicationCursors=@($document.cursors)
 foreach ($point in $applicationCursors) {
  if ([string]::IsNullOrWhiteSpace($point.volumeIdentity) -or [string]$point.journalId -cnotmatch '^[0-9A-F]{16}$' -or
      [long]$point.nextUsn -lt 0) { throw 'Malformed application cursor evidence.' }
 }
 if ($applicationCursors.Count -eq 0 -and [string]::IsNullOrWhiteSpace($document.noApplicationCursorsReason)) { throw 'An empty application cursor set requires an explicit reason.' }
 $cursorEvidenceHash=$inputEvidence.Sha256
}

$reviewed=$null
if ($Apply) {
 $inputPlan=Read-OperatorEvidence $ReviewedPlanPath
 if ($inputPlan.Sha256 -ine $ExpectedPlanSha256) { throw 'Reviewed operator plan hash differs.' }
 $reviewed=$inputPlan.Document
 if ($reviewed.version -ne 1 -or $reviewed.mode -cne 'plan-only' -or $reviewed.ok -isnot [bool] -or $reviewed.ok -ne $true -or $null -ne $reviewed.error -or $reviewed.applicationCursorEvidenceSha256 -cne $cursorEvidenceHash -or
     [string]::Join('|',@($reviewed.plans.Before.Drive|Sort-Object)) -cne [string]::Join('|',@($Drive|Sort-Object)) -or
     ($reviewed.operatorFiles|ConvertTo-Json -Compress) -cne ($operatorHashes|ConvertTo-Json -Compress)) { throw 'Reviewed targets, operator code or application-cursor evidence changed.' }
 foreach ($point in $applicationCursors) {
  if (@($reviewed.plans|Where-Object {$_.Before.VolumeIdentity -ieq $point.volumeIdentity}).Count -ne 1) { throw 'Application cursor volume is absent from the reviewed targets.' }
 }
}
$operatorModuleName=Import-VerifiedOperatorCode $operatorSources
$receipt=[ordered]@{version=1;mode=if($Apply){'apply'}else{'plan-only'};startedAtUtc=[DateTimeOffset]::UtcNow;
 operatorFiles=$operatorHashes;applicationCursorEvidenceSha256=$cursorEvidenceHash;
 scope='Operator-only existing-journal parameters; no deletion, recreation, shrinking or automatic rollback. Exclude concurrent administrative journal operations.';
 plans=@();mutationAttempted=@();completed=@();ok=$false;error=$null}
$receiptPath=Join-Path $EvidenceDirectory ('journal-resize-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ')+'-'+[guid]::NewGuid().ToString('N')+'.json')
$receiptStream=[IO.FileStream]::new($receiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
$receiptState=[pscustomobject]@{Sha256=''}
function Save-OperatorReceipt {
 $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($receipt|ConvertTo-Json -Depth 20))
 $receiptStream.Position=0;$receiptStream.Write($bytes);$receiptStream.SetLength($bytes.Length);$receiptStream.Flush($true)
 $receiptState.Sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}
try {
 foreach ($target in $Drive) {
  $expected=if($Apply){@($reviewed.plans|Where-Object {$_.Before.Drive -ceq $target})}else{@()}
  if ($Apply -and $expected.Count -ne 1) { throw 'Reviewed drive target is ambiguous.' }
  $volume=[FluxSourceJournalOperatorVolume]::new($target,$(if($Apply){$expected[0].Before.VolumeIdentity}else{''}),[bool]$Apply)
  try {
   $before=$volume.Query()
   $points=@($applicationCursors|Where-Object {$_.volumeIdentity -ieq $before.VolumeIdentity})
   $plan=New-SourceJournalResizePlan -Before $before -ApplicationCursors $points
   if ($Apply) {
    if ($plan.ApplicationCursorCount -ne $expected[0].ApplicationCursorCount) { throw 'Reviewed application cursor membership differs.' }
    foreach ($point in $points) {
     if (@($expected[0].Cursors|Where-Object {$_.VolumeIdentity -ieq $point.volumeIdentity -and $_.JournalId -ceq $point.journalId -and [long]$_.NextUsn -eq [long]$point.nextUsn}).Count -ne 1) { throw 'A required application cursor is missing from the reviewed plan.' }
    }
    $plan=$expected[0]
    $receipt.plans+=@($plan);Save-OperatorReceipt
    $proof=Invoke-SourceJournalResizePlan -Plan $plan -ReadState {$volume.Query()} -WriteSizes {param($maximum,$delta)
     $receipt.mutationAttempted+=@($target);Save-OperatorReceipt
     $volume.Resize($maximum,$delta,$plan.Before.JournalId)
    } -ReadCursor {param($cursor) $volume.AssertReadable($cursor.JournalId,[long]$cursor.NextUsn)}
    $receipt.completed+=@($proof)
   } else {
    foreach ($cursor in $plan.Cursors) {$volume.AssertReadable($cursor.JournalId,[long]$cursor.NextUsn)}
    $receipt.plans+=@($plan)
   }
   Save-OperatorReceipt
  } finally {$volume.Dispose()}
 }
 foreach ($point in $applicationCursors) {
  if (@($receipt.plans|Where-Object {$_.Before.VolumeIdentity -ieq $point.volumeIdentity}).Count -ne 1) { throw 'Application cursor volume is absent from the selected targets.' }
 }
 $receipt.ok=$true;Save-OperatorReceipt
 [pscustomobject]@{ok=$true;mode=$receipt.mode;receipt=$receiptPath;receiptSha256=$receiptState.Sha256;drives=$Drive}|ConvertTo-Json
} catch {
 $receipt.ok=$false;$receipt.error=$_.Exception.Message;Save-OperatorReceipt
 throw "Journal operation stopped. Preserve $receiptPath. $($receipt.error)"
} finally {$receiptStream.Dispose()}
