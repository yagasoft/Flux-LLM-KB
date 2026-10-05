[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-repository-recovery-migration.psm1')

function Assert-Stream([string[]]$Rows) {
    $table=[Data.DataTable]::new()
    [void]$table.Columns.Add('RowJson', [string])
    foreach ($row in $Rows) { [void]$table.Rows.Add($row) }
    $reader=$table.CreateDataReader()
    try { $actual=[FluxKnowledge.Deployment.RetainedRowStream]::Read($reader) }
    finally { $reader.Dispose(); $table.Dispose() }
    # Independent framing expectation: V1 concatenates each SQL JSON object,
    # without an array wrapper, separator or date/binary reserialisation.
    $expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($Rows -join ''))))
    if ($actual.RowCount -ne $Rows.Count -or $actual.Fingerprint -cne $expected) {
        throw 'Streaming changed ordered V1 JSON bytes or row count.'
    }
}
Assert-Stream @()
Assert-Stream @('{"Id":1,"Value":null}', '{"Id":2,"Value":"AQID","Date":"2026-10-05T01:02:03.4567890+03:00"}')
# A surrogate pair crosses the fixed read buffer; a value much larger than
# that buffer must not be truncated or acquire a replacement character.
$supplementary=[char]::ConvertFromUtf32(0x1F642)
Assert-Stream @(('a'*8191)+$supplementary+('b'*32771), ('c'*2097157)+$supplementary)
Write-Output 'Retained row stream passed: empty input, exact V1 framing, offsets/binary/nulls, large values and split Unicode.'
