[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $SourceRoot 'scripts/deploy/source-journal-resize.psm1') -Force
function New-State {
 [pscustomobject]@{Drive='I:';VolumeIdentity='\\?\Volume{00000000-0000-0000-0000-000000000001}\';FileSystem='NTFS';JournalId='000000000000002A';FirstUsn=100L;LowestValidUsn=0L;NextUsn=1000L;MaximumSize=32MB;AllocationDelta=8MB;AvailableFreeBytes=4GB;IsDeleting=$false}
}
function Assert-Refused([scriptblock]$Action,[string]$Name) {
 $refused=$false; try { & $Action | Out-Null } catch { $refused=$true }
 if (!$refused) { throw "Unsafe operator boundary accepted: $Name" }
}
$state=New-State
$cursor=[pscustomobject]@{VolumeIdentity=$state.VolumeIdentity;JournalId=$state.JournalId;NextUsn=500L}
$plan=New-SourceJournalResizePlan -Before $state -ApplicationCursors @($cursor)
if (!$plan.ChangeRequired -or $plan.MaximumSize -ne 1GB -or $plan.AllocationDelta -ne 128MB) { throw 'Wrong requested capacity.' }
foreach ($case in @(
 @{Field='Drive';Value='C:'},@{Field='FileSystem';Value='ReFS'},@{Field='JournalId';Value='0000000000000000'},
 @{Field='IsDeleting';Value=$true},@{Field='AvailableFreeBytes';Value=(2GB-1)},@{Field='NextUsn';Value=99L},
 @{Field='MaximumSize';Value=0L},@{Field='AllocationDelta';Value=0L},@{Field='VolumeIdentity';Value='I:\'})) {
 $bad=New-State; $bad.($case.Field)=$case.Value
 Assert-Refused { New-SourceJournalResizePlan -Before $bad -ApplicationCursors @($cursor) } $case.Field
}
foreach ($case in @(@{JournalId='000000000000002B';NextUsn=500L},@{JournalId=$state.JournalId;NextUsn=99L},@{JournalId=$state.JournalId;NextUsn=1001L})) {
 $bad=[pscustomobject]@{VolumeIdentity=$state.VolumeIdentity;JournalId=$case.JournalId;NextUsn=$case.NextUsn}
 Assert-Refused { New-SourceJournalResizePlan -Before $state -ApplicationCursors @($bad) } 'application cursor'
}
$large=New-State;$large.MaximumSize=2GB;$large.AllocationDelta=256MB;$large.AvailableFreeBytes=1MB
$preserved=New-SourceJournalResizePlan -Before $large
if ($preserved.ChangeRequired -or $preserved.MaximumSize -ne 2GB -or $preserved.AllocationDelta -ne 256MB) { throw 'Larger configuration was not preserved.' }

$script:live=New-State;$script:writes=0;$script:reads=0
$readState={ $script:live }
$readCursor={param($point) $script:reads++; if ($point.NextUsn -lt $script:live.FirstUsn) { throw 'Lost history.' }}
$writeSizes={param($maximum,$delta) $script:writes++;$script:live.MaximumSize=$maximum;$script:live.AllocationDelta=$delta;$script:live.NextUsn+=10}
$proof=Invoke-SourceJournalResizePlan -Plan $plan -ReadState $readState -WriteSizes $writeSizes -ReadCursor $readCursor
if ($script:writes -ne 1 -or $script:reads -ne 4 -or !$proof.CursorReadabilityVerified -or $proof.After.JournalId -cne $state.JournalId) { throw 'Verified in-place resize failed.' }

foreach ($field in @('JournalId','VolumeIdentity','MaximumSize','AllocationDelta','AvailableFreeBytes','FirstUsn')) {
 $script:live=New-State;$script:writes=0
 switch ($field) {
  'JournalId' {$script:live.JournalId='000000000000002B'}
  'VolumeIdentity' {$script:live.VolumeIdentity='\\?\Volume{00000000-0000-0000-0000-000000000002}\'}
  'MaximumSize' {$script:live.MaximumSize=64MB}
  'AllocationDelta' {$script:live.AllocationDelta=16MB}
  'AvailableFreeBytes' {$script:live.AvailableFreeBytes=1GB}
  'FirstUsn' {$script:live.FirstUsn=501L}
 }
 Assert-Refused { Invoke-SourceJournalResizePlan -Plan $plan -ReadState $readState -WriteSizes $writeSizes -ReadCursor $readCursor } "pre-mutation $field drift"
 if ($script:writes) { throw 'Mutation occurred after a failed precondition.' }
}
$script:live=New-State;$script:writes=0
$replaceDuringWrite={param($maximum,$delta) &$writeSizes $maximum $delta; $script:live.JournalId='000000000000002B'}
Assert-Refused { Invoke-SourceJournalResizePlan -Plan $plan -ReadState $readState -WriteSizes $replaceDuringWrite -ReadCursor $readCursor } 'journal replaced during mutation'
if ($script:writes -ne 1) { throw 'Unexpected retry or rollback mutation.' }
$script:live=New-State;$script:writes=0
Assert-Refused { Invoke-SourceJournalResizePlan -Plan $plan -ReadState $readState -WriteSizes $writeSizes -ReadCursor {throw 'Cursor probe refused.'} } 'unreadable cursor'
if ($script:writes) { throw 'Mutation occurred despite unreadable cursor.' }

# Execute the real wrapper's code-admission functions with synthetic dependencies.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $SourceRoot 'scripts/deploy/resize-source-journals.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Operator wrapper does not parse.' }
foreach ($name in @('Assert-NoReparsePath','Read-OperatorFile','Read-OperatorCode','Assert-OperatorEntryCode','Import-VerifiedOperatorCode')) {
 $function=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
 if ($function.Count -ne 1) { throw "Missing real admission function: $name" }
 . ([scriptblock]::Create($function[0].Extent.Text))
}
$ground='I:\Codex Workspaces\Staging\FluxKnowledge'
$fixture=[IO.Path]::GetFullPath((Join-Path $ground ('JournalOperatorContract-'+[guid]::NewGuid().ToString('N'))))
if (!$fixture.StartsWith($ground+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped test ground.' }
$null=[IO.Directory]::CreateDirectory($fixture)
$loadedModule=$null
try {
 $typeName='FluxJournalOperatorContract'+[guid]::NewGuid().ToString('N')
 $paths=@((Join-Path $fixture 'entry.ps1'),(Join-Path $fixture 'logic.psm1'),(Join-Path $fixture 'adapter.cs'))
 [IO.File]::WriteAllText($paths[0],'param()')
 [IO.File]::WriteAllText($paths[1],"function Get-FluxJournalOperatorContractValue {42}; Export-ModuleMember -Function Get-FluxJournalOperatorContractValue")
 [IO.File]::WriteAllText($paths[2],"public static class $typeName { public static int Value() {return 42;} }")
 $sources=@($paths|ForEach-Object {Read-OperatorCode $_})
 Assert-OperatorEntryCode $sources[0] 'param()'
 Assert-Refused {Assert-OperatorEntryCode $sources[0] 'param(); throw "different parsed code"'} 'parsed entry differs from pinned source'
 foreach ($index in @(1,2)) {
  [IO.File]::AppendAllText($paths[$index],"`n// changed after hash")
  Assert-Refused {Import-VerifiedOperatorCode $sources} 'dependency changed after hash before load'
  if ($typeName -as [type]) { throw 'Unreviewed native dependency was loaded.' }
  [IO.File]::WriteAllText($paths[$index],$sources[$index].Text)
 }
 $loadedModule=Import-VerifiedOperatorCode $sources
 if ((Get-FluxJournalOperatorContractValue) -ne 42 -or !$($typeName -as [type])) { throw 'Pinned code buffers were not loaded.' }

 # Exercise the actual two-drive Apply loop and durable failure receipt. Only
 # native adapter construction is substituted; no real volume is opened.
 $operation=@($ast.EndBlock.Statements|Where-Object {
  $_ -is [Management.Automation.Language.TryStatementAst] -and $_.Body.Extent.Text.Contains('foreach ($target in $Drive)')
 })
 if($operation.Count -ne 1){throw 'The operator execution boundary is ambiguous.'}
 $constructors=@($operation[0].FindAll({param($node)
  $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Static -and
  $node.Expression.Extent.Text -ceq '[FluxSourceJournalOperatorVolume]' -and $node.Member.Value -ceq 'new'
 },$true))
 if($constructors.Count -ne 1){throw 'The native adapter constructor port is ambiguous.'}
 $text=$operation[0].Extent.Text;$offset=$constructors[0].Extent.StartOffset-$operation[0].Extent.StartOffset
 $text=$text.Substring(0,$offset)+'(& $volumeFactory $target $expected[0].Before.VolumeIdentity ([bool]$Apply))'+
  $text.Substring($offset+$constructors[0].Extent.Text.Length)
 $save=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and
  $node.Name -ceq 'Save-OperatorReceipt'},$true))
 if($save.Count -ne 1){throw 'The real receipt flush function is missing.'}
 . ([scriptblock]::Create($save[0].Extent.Text))
 $partialCalls=@{Writes=[Collections.Generic.List[string]]::new();Disposed=[Collections.Generic.List[string]]::new();Reads=0}
 $partialStates=@{'E:'=(New-State);'I:'=(New-State)}
 $partialStates['E:'].Drive='E:'
 $partialStates['I:'].VolumeIdentity='\\?\Volume{00000000-0000-0000-0000-000000000002}\'
 $volumeFactory={param($driveName,$identity,$write)
  if(!$write -or !$partialStates.ContainsKey($driveName) -or $partialStates[$driveName].VolumeIdentity -cne $identity){throw 'Wrong synthetic volume binding.'}
  $adapter=[pscustomobject]@{Drive=$driveName;State=$partialStates[$driveName];Calls=$partialCalls}
  $adapter|Add-Member ScriptMethod Query { $this.State|Select-Object * }
  $adapter|Add-Member ScriptMethod AssertReadable {param($journal,$position)
   $this.Calls.Reads++;if($journal -cne $this.State.JournalId -or $position -lt $this.State.FirstUsn -or $position -gt $this.State.NextUsn){throw 'Synthetic cursor lost.'}
  }
  $adapter|Add-Member ScriptMethod Resize {param($maximum,$delta,$journal)
   $this.Calls.Writes.Add($this.Drive)
   if($this.Drive -ceq 'I:'){throw 'injected-second-volume-resize-failure'}
   if($maximum -ne 1GB -or $delta -ne 128MB -or $journal -cne $this.State.JournalId){throw 'Wrong synthetic resize.'}
   $this.State.MaximumSize=$maximum;$this.State.AllocationDelta=$delta;$this.State.NextUsn+=10
  }
  $adapter|Add-Member ScriptMethod Dispose { $this.Calls.Disposed.Add($this.Drive) }
  $adapter
 }
 $Drive=@('E:','I:');$Apply=$true;$applicationCursors=@()
 $reviewed=[pscustomobject]@{plans=@($Drive|ForEach-Object {New-SourceJournalResizePlan -Before $partialStates[$_]})}
 $receipt=[ordered]@{version=1;mode='apply';plans=@();mutationAttempted=@();completed=@();ok=$false;error=$null}
 $receiptPath=Join-Path $fixture 'partial-apply.json';$receiptState=[pscustomobject]@{Sha256=''}
 $receiptStream=[IO.FileStream]::new($receiptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
 $reported=$null;try{. ([scriptblock]::Create($text))}catch{$reported=$_.Exception.Message}
 $persisted=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
 if($reported -notlike '*injected-second-volume-resize-failure*' -or $persisted.ok -or !$persisted.error -or
  ($persisted.mutationAttempted -join '|') -cne 'E:|I:' -or $persisted.completed.Count -ne 1 -or
  $persisted.completed[0].After.Drive -cne 'E:' -or !$persisted.completed[0].CursorReadabilityVerified -or
  $partialStates['E:'].MaximumSize -ne 1GB -or $partialStates['I:'].MaximumSize -ne 32MB -or
  ($partialCalls.Writes -join '|') -cne 'E:|I:' -or ($partialCalls.Disposed -join '|') -cne 'E:|I:'){
  throw 'Two-drive failure lost the first verified resize, retried/rolled it back or omitted durable failure evidence.'
 }
} finally {
 if ($loadedModule) {Remove-Module $loadedModule -Force}
 if (![IO.Path]::GetFullPath($fixture).StartsWith($ground+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe fixture cleanup.'}
 Remove-Item -LiteralPath $fixture -Recurse -Force
}

$snapshotGate=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if ($document.version')},$true))
$planGate=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if ($reviewed.version')},$true))
if ($snapshotGate.Count -ne 1 -or $planGate.Count -ne 1) {throw 'Receipt gates are ambiguous.'}
$snapshotCheck=[scriptblock]::Create($snapshotGate[0].Extent.Text)
foreach ($value in @($false,'false','true',1,$null)) {
 $document=[pscustomobject]@{version=1;completeApplicationCursorSnapshot=$value;source='owned test fixture'}
 Assert-Refused {&$snapshotCheck} 'non-Boolean or false complete snapshot'
}
$document=[pscustomobject]@{version=1;completeApplicationCursorSnapshot=$true;source='owned test fixture'};&$snapshotCheck
$planCheck=[scriptblock]::Create($planGate[0].Extent.Text)
$cursorEvidenceHash='matched';$Drive=@('I:');$operatorHashes=@([pscustomobject]@{Name='pinned';Sha256='expected'})
foreach ($value in @($false,'true','false',1,$null)) {
 $reviewed=[pscustomobject]@{version=1;mode='plan-only';ok=$value;error=$null;applicationCursorEvidenceSha256=$cursorEvidenceHash;plans=@([pscustomobject]@{Before=[pscustomobject]@{Drive='I:'}});operatorFiles=$operatorHashes}
 Assert-Refused {&$planCheck} 'unsuccessful reviewed plan'
}
$reviewed.ok=$true;&$planCheck
$reviewed.error='previous failure';Assert-Refused {&$planCheck} 'reviewed plan with an error'
[pscustomobject]@{ok=$true;scope='Simulated operator boundaries only; no native journal mutation';targetMaximumBytes=1GB;targetAllocationDeltaBytes=128MB}|ConvertTo-Json
