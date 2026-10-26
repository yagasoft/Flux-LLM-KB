Set-StrictMode -Version Latest

function New-SourceJournalResizePlan {
 [CmdletBinding()]
 param([Parameter(Mandatory)]$Before,[object[]]$ApplicationCursors=@())
 if ($Before.Drive -cnotin @('E:','I:') -or $Before.FileSystem -ine 'NTFS' -or $Before.IsDeleting) { throw 'Only the existing E:/I: NTFS journals are approved.' }
 $volume=[string]$Before.VolumeIdentity
 $parsedVolume=[guid]::Empty
 if ($volume.Length -ne 49 -or !$volume.StartsWith('\\?\Volume{',[StringComparison]::OrdinalIgnoreCase) -or !$volume.EndsWith('}\') -or
     ![guid]::TryParseExact($volume.Substring(11,36),'D',[ref]$parsedVolume) -or $parsedVolume -eq [guid]::Empty) { throw 'An exact canonical volume identity is required.' }
 if ([string]$Before.JournalId -cnotmatch '^[0-9A-F]{16}$' -or [Convert]::ToUInt64($Before.JournalId,16) -eq 0 -or
     [long]$Before.FirstUsn -lt 0 -or [long]$Before.LowestValidUsn -lt 0 -or [long]$Before.NextUsn -lt [math]::Max([long]$Before.FirstUsn,[long]$Before.LowestValidUsn) -or
     [long]$Before.MaximumSize -le 0 -or [long]$Before.AllocationDelta -le 0 -or [long]$Before.AvailableFreeBytes -lt 0) { throw 'Existing journal state is invalid or unavailable.' }
 $maximum=[math]::Max([long]$Before.MaximumSize,1GB)
 $delta=[math]::Max([long]$Before.AllocationDelta,128MB)
 $change=$maximum -ne [long]$Before.MaximumSize -or $delta -ne [long]$Before.AllocationDelta
 if ($change -and [long]$Before.AvailableFreeBytes -lt 2GB) { throw 'At least 2 GiB free is required before changing journal parameters.' }
 $cursors=@($ApplicationCursors)+@([pscustomobject]@{VolumeIdentity=$volume;JournalId=$Before.JournalId;NextUsn=[long]$Before.NextUsn})
 foreach ($cursor in $cursors) {
  if ($cursor.VolumeIdentity -ine $volume -or $cursor.JournalId -cne $Before.JournalId -or [long]$cursor.NextUsn -lt [math]::Max([long]$Before.FirstUsn,[long]$Before.LowestValidUsn) -or
      [long]$cursor.NextUsn -gt [long]$Before.NextUsn) { throw 'A required journal cursor is outside the current history.' }
 }
 [pscustomobject]@{Before=($Before|Select-Object *);MaximumSize=[long]$maximum;AllocationDelta=[long]$delta;ChangeRequired=$change;
  ApplicationCursorCount=$ApplicationCursors.Count;Cursors=@($cursors|Sort-Object NextUsn -Unique)}
}

function Invoke-SourceJournalResizePlan {
 [CmdletBinding()]
 param([Parameter(Mandatory)]$Plan,[Parameter(Mandatory)][scriptblock]$ReadState,
  [Parameter(Mandatory)][scriptblock]$WriteSizes,[Parameter(Mandatory)][scriptblock]$ReadCursor)
 $before=& $ReadState
 foreach ($field in @('Drive','VolumeIdentity','FileSystem','JournalId','MaximumSize','AllocationDelta')) {
  if ([string]$before.$field -cne [string]$Plan.Before.$field) { throw "Journal resize precondition drift: $field. No mutation permitted." }
 }
 $fresh=New-SourceJournalResizePlan -Before $before -ApplicationCursors $Plan.Cursors
 if ($fresh.MaximumSize -ne $Plan.MaximumSize -or $fresh.AllocationDelta -ne $Plan.AllocationDelta -or $fresh.ChangeRequired -ne $Plan.ChangeRequired) { throw 'Reviewed journal resize intent changed.' }
 foreach ($cursor in $Plan.Cursors) { & $ReadCursor $cursor | Out-Null }
 if ($Plan.ChangeRequired) { & $WriteSizes $Plan.MaximumSize $Plan.AllocationDelta | Out-Null }
 $after=& $ReadState
 $null=New-SourceJournalResizePlan -Before $after -ApplicationCursors $Plan.Cursors
 if ($after.Drive -cne $Plan.Before.Drive -or $after.VolumeIdentity -cne $Plan.Before.VolumeIdentity -or $after.JournalId -cne $Plan.Before.JournalId -or
     [long]$after.NextUsn -lt [long]$Plan.Before.NextUsn -or [long]$after.MaximumSize -ne $Plan.MaximumSize -or [long]$after.AllocationDelta -ne $Plan.AllocationDelta) {
  throw 'Journal resize outcome differs from the reviewed state. Preserve evidence; do not recreate, shrink or retry automatically.'
 }
 foreach ($cursor in $Plan.Cursors) { & $ReadCursor $cursor | Out-Null }
 [pscustomobject]@{Before=$Plan.Before;After=($after|Select-Object *);Changed=[bool]$Plan.ChangeRequired;
  ApplicationCursorCount=$Plan.ApplicationCursorCount;CursorReadabilityVerified=$true;VerifiedAtUtc=[DateTimeOffset]::UtcNow}
}

Export-ModuleMember -Function New-SourceJournalResizePlan,Invoke-SourceJournalResizePlan
