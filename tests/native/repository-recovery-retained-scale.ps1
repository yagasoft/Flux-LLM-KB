[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot, [Parameter(Mandatory)][Guid]$PipelineRecordId)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $SourceRoot 'scripts/deploy/incremental-repository-recovery-migration.psm1')
$connectionText=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_RECOVERY_DISPOSABLE_SQL')
$connectionText=[regex]::Replace($connectionText,'(?i)(^|;)\s*Trust Server Certificate\s*=','$1TrustServerCertificate=')
$connectionText=[regex]::Replace($connectionText,'(?i)(^|;)\s*Connect Retry Count\s*=','$1ConnectRetryCount=')
$builder=[Data.SqlClient.SqlConnectionStringBuilder]::new($connectionText)
if ($builder.InitialCatalog -cnotmatch '^FluxKnowledge_Phase1Tests_[0-9a-f]{32}$') { throw 'Scale verification requires a generated disposable catalogue.' }
$builder.Pooling=$false; $connectionText=$builder.ConnectionString
$evidencePath=[Environment]::GetEnvironmentVariable('FLUXKNOWLEDGE_RECOVERY_SCALE_EVIDENCE')
if (!$evidencePath) { throw 'An explicit private scale-evidence file is required.' }
$connection=[Data.SqlClient.SqlConnection]::new($connectionText)
try {
    $connection.Open(); $command=$connection.CreateCommand()
    try {
        $command.CommandTimeout=300
        $command.CommandText=@'
SET NOCOUNT ON;
IF (SELECT COUNT_BIG(*) FROM dbo.Vectors)<>0 OR (SELECT COUNT_BIG(*) FROM dbo.IndexGenerations)<>0
    THROW 51000, 'Scale fixture must start empty.', 1;
SELECT TOP (120000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
INTO #Numbers FROM sys.all_objects a CROSS JOIN sys.all_objects b;
DECLARE @artifact uniqueidentifier=NEWID(), @epoch uniqueidentifier=NEWID();
INSERT INTO Artifacts (Id,PipelineRecordId,SourceRevision,Stage,ContentHash,ContentType,SearchText,CreatedAtUtc)
VALUES (@artifact,@pipeline,1,3,REPLICATE('a',64),N'text/plain',N'synthetic scale fixture',SYSDATETIMEOFFSET());
INSERT INTO IndexGenerations (Id,ModelFingerprint,Dimensions,IndexPath,MetadataChecksum,VectorCount,CreatedAtUtc,CorpusEpoch,CorpusVersion)
SELECT NEWID(),N'synthetic:1024',1024,N'synthetic-scale',REPLICATE('b',64),0,SYSDATETIMEOFFSET(),@epoch,n FROM #Numbers WHERE n<=2596;
SELECT Id,ROW_NUMBER() OVER (ORDER BY Id) AS n INTO #Generations FROM IndexGenerations;
INSERT INTO TextChunks (ArtifactId,SourceRevision,Ordinal,Content,StartOffset,Length,ContentHash)
SELECT @artifact,1,n-1,N'synthetic',0,9,REPLICATE('a',64) FROM #Numbers WHERE n<=50000;
DECLARE @generation uniqueidentifier=(SELECT Id FROM #Generations WHERE n=1);
DECLARE @values varbinary(max)=CONVERT(varbinary(max),REPLICATE(CONVERT(varchar(max),'z'),4096));
DECLARE @checksum varchar(64)=LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@values),2));
INSERT INTO Vectors (TextChunkId,ModelFingerprint,SourceRevision,IndexGenerationId,Dimensions,[Values],TextChunkContentHash,PayloadChecksum,IsDeleted,CreatedAtUtc)
SELECT Id,N'synthetic:1024',1,@generation,1024,@values,REPLICATE('a',64),@checksum,0,SYSDATETIMEOFFSET()
FROM TextChunks WHERE ArtifactId=@artifact;
SELECT VectorId,ROW_NUMBER() OVER (ORDER BY VectorId) AS n INTO #Vectors FROM Vectors;
INSERT INTO IndexGenerationVectors (GenerationId,VectorId)
SELECT @generation,VectorId FROM #Vectors;
-- Repeated version memberships, rather than 16 million vectors/documents.
INSERT INTO IndexGenerationVectors (GenerationId,VectorId)
SELECT g.Id,v.VectorId FROM #Generations g CROSS JOIN #Vectors v
WHERE g.n>1 AND v.n<=6144 AND ((g.n-2)*6144+v.n)<=15934056-50000;
UPDATE g SET VectorCount=counts.n FROM IndexGenerations g
CROSS APPLY (SELECT COUNT_BIG(*) AS n FROM IndexGenerationVectors m WHERE m.GenerationId=g.Id) counts;
INSERT INTO GpuSchedulerOperationReceipts (OperationId,OperationKind,Accepted,Committed,WakeReasons,AdmissionDisposition,CreatedAtUtc)
SELECT NEWID(),N'admission',0,0,1,1,SYSDATETIMEOFFSET() FROM #Numbers;
SELECT (SELECT COUNT_BIG(*) FROM Vectors) AS Vectors,
       (SELECT COUNT_BIG(*) FROM IndexGenerations) AS Generations,
       (SELECT COUNT_BIG(*) FROM IndexGenerationVectors) AS Memberships,
       (SELECT COUNT_BIG(*) FROM GpuSchedulerOperationReceipts) AS ReplayReceipts,
       (SELECT MIN(Dimensions) FROM Vectors) AS Dimensions,
       (SELECT AVG(CONVERT(bigint,DATALENGTH([Values]))) FROM Vectors) AS VectorBytes
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
'@
        [void]$command.Parameters.Add('@pipeline',[Data.SqlDbType]::UniqueIdentifier)
        $command.Parameters['@pipeline'].Value=$PipelineRecordId
        $counts=[string]$command.ExecuteScalar() | ConvertFrom-Json
    } finally { $command.Dispose() }
} finally { $connection.Dispose() }
if ($counts.Vectors -ne 50000 -or $counts.Generations -ne 2596 -or $counts.Memberships -ne 15934056 -or $counts.ReplayReceipts -ne 120000 -or $counts.Dimensions -ne 1024 -or $counts.VectorBytes -ne 4096) { throw 'Scale fixture cardinality differs from the representative target.' }
Write-Output ('Representative fixture ready: '+($counts | ConvertTo-Json -Compress))
$observations=[Collections.Generic.List[object]]::new()
$baseline=$null
for ($pass=1; $pass -le 3; $pass++) {
    $process=[Diagnostics.Process]::GetCurrentProcess(); $process.Refresh()
    $beforeMemory=$process.WorkingSet64
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $current=if ($null -eq $baseline) {
        Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText
    } else {
        Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $baseline.Projection
    }
    if ($null -eq $baseline) { $baseline=$current } else { Assert-RepositoryRecoveryRetainedState $baseline $current }
    if ($current.Tables.Vectors.RowCount -ne 50000 -or $current.Tables.IndexGenerationVectors.RowCount -ne 15934056) { throw 'Streaming omitted vectors or memberships.' }
    $clock.Stop(); $process.Refresh()
    $observations.Add([ordered]@{pass=$pass; elapsed_seconds=$clock.Elapsed.TotalSeconds; working_set_before_bytes=$beforeMemory;
        working_set_after_bytes=$process.WorkingSet64; peak_working_set_bytes=$process.PeakWorkingSet64})
    Write-Output ('Scale pass verified: '+($observations[-1] | ConvertTo-Json -Compress))
    [ordered]@{observed_at_utc=[DateTime]::UtcNow.ToString('O'); disposable=$true; counts=$counts; passes=$observations.ToArray();
        memberships_sha256=$current.Tables.IndexGenerationVectors.Fingerprint; vectors_sha256=$current.Tables.Vectors.Fingerprint;
        table_count=$current.Tables.Count; projection_version=$current.Projection.Version; complete=($pass -eq 3)} |
        ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $evidencePath -Encoding utf8
}
# A high-ordinal membership change must still fail the proof after all passes.
$connection=[Data.SqlClient.SqlConnection]::new($connectionText)
try {
    $connection.Open(); $command=$connection.CreateCommand()
    try {
        $command.CommandText='DELETE TOP (1) FROM IndexGenerationVectors WHERE GenerationId=(SELECT TOP (1) GenerationId FROM IndexGenerationVectors ORDER BY GenerationId DESC);'
        if ($command.ExecuteNonQuery() -ne 1) { throw 'Late-membership mutation did not change exactly one link.' }
    } finally { $command.Dispose() }
} finally { $connection.Dispose() }
$changed=Get-RepositoryRecoveryRetainedState -ConnectionString $connectionText -Projection $baseline.Projection
$refused=$false
try { Assert-RepositoryRecoveryRetainedState $baseline $changed } catch { if ($_.Exception.Message -notmatch 'retained-state-changed') { throw }; $refused=$true }
if (!$refused) { throw 'A late membership deletion escaped verification.' }
$receipt=Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
$receipt | Add-Member -NotePropertyName late_membership_mutation_refused -NotePropertyValue $true
$receipt | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $evidencePath -Encoding utf8
Write-Output 'Representative full-table streaming and late membership mutation verification passed.'
