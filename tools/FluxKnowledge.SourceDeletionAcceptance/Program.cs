using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

if (args is ["verify-read-classifier"])
{
    Exercise.VerifyReadClassifier();
    return;
}

// Deliberately restricted to this disposable acceptance deployment. No production
// configuration, fallback binding, model provider, or model acquisition is loaded.
const string root = @"E:\Codex Workspaces\Staging\FluxKnowledge\SourceDeletion-20261014";
const string catalog = "FluxKnowledge_Staging_SourceDeletion_20261014";
var settingsPath = Path.Combine(root, "Config", "staging.json");
for (var path = root; path is not null; path = Path.GetDirectoryName(path))
    if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        throw new InvalidOperationException("Linked acceptance storage is forbidden.");
if (File.GetAttributes(settingsPath).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("Linked settings are forbidden.");
var settings = JsonSerializer.Deserialize<StageSettings>(File.ReadAllText(settingsPath))!;
var binding = new SqlConnectionStringBuilder(settings.SqlConnectionString);
if (binding.DataSource != "localhost" || binding.InitialCatalog != catalog || binding.IntegratedSecurity ||
    binding.UserID != "fluxknowledge_staging_source_deletion_20261014" || binding.AttachDBFilename.Length != 0 ||
    binding.FailoverPartner.Length != 0 || settings.Origin != "http://127.0.0.1:5142")
    throw new InvalidOperationException("The exact task-owned acceptance binding is required.");
await using (var connection = new SqlConnection(binding.ConnectionString))
{
    await connection.OpenAsync();
    await using var command = new SqlCommand("""
        SELECT DB_NAME(), IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator'),
          HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER'), HAS_DBACCESS('FluxKnowledge');
        SELECT type_desc, physical_name FROM sys.database_files;
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    if (!await reader.ReadAsync() || reader.GetString(0) != catalog ||
        Enumerable.Range(1,4).Any(i => reader.IsDBNull(i) || reader.GetInt32(i) != 0))
        throw new InvalidOperationException("The runtime principal must be restricted and denied production access.");
    await reader.NextResultAsync();
    var files = new Dictionary<string,string>();
    while (await reader.ReadAsync()) files.Add(reader.GetString(0), reader.GetString(1));
    if (files.Count != 2 || files["ROWS"] != Path.Combine(root,"Data","Sql","Data","FluxKnowledge.mdf") ||
        files["LOG"] != Path.Combine(root,"Data","Sql","Log","FluxKnowledge_log.ldf"))
        throw new InvalidOperationException("The database file manifest does not match this deployment.");
}
var factory = new StageFactory(binding.ConnectionString);
var mode = args.SingleOrDefault() ?? "inspect";
if (mode is "exercise" or "exercise-restart" or "exercise-retry") { await Exercise.RunAsync(root,settings.Origin,factory,mode == "exercise-restart",mode == "exercise-retry"); return; }
if (mode == "profile") { await BatchProfile.RunAsync(root,binding,factory); return; }
if (mode == "verify-preservation") { await Exercise.VerifyPreservationOnlyAsync(root,factory); return; }
if (mode == "inspect")
{
    await using var context = factory.CreateDbContext();
    Console.WriteLine(JsonSerializer.Serialize(new { catalog,
        roots = await context.SourceRootConfigurations.Select(r => new { r.Id,r.DisplayName,r.State }).ToArrayAsync(),
        operations = await context.SourceDeletionOperations.Select(o => new { o.Id,o.SourceRootId,o.State,o.Phase,o.ResumePhase,
            o.MembershipRowsDeleted,o.MembershipVectorCursor,o.ReasonCode,o.LeaseId,o.LeaseExpiresAtUtc }).ToArrayAsync(),
        cleanup = await context.SourceDeletionCleanupItems.GroupBy(i => new { i.SourceDeletionOperationId,i.State })
            .Select(g => new { g.Key.SourceDeletionOperationId,g.Key.State,count = g.Count() }).ToArrayAsync(),
        memberships = await context.IndexGenerationVectors.LongCountAsync() }));
    return;
}
if (mode is not ("seed-small" or "seed-scale" or "seed-restart" or "seed-failure")) throw new ArgumentException("Unknown acceptance mode.");
var scale = mode == "seed-scale";
var ownedCount = scale ? 3434 : mode == "seed-restart" ? 100 : 2;
var otherCount = scale ? 12442 : 2;
var generationCount = scale ? 1443 : 3;
var historicalOwned = scale ? 4_847_721L : mode == "seed-restart" ? 300 : 6;
var historicalOther = scale ? 17_952_279L : 6;
var now = DateTimeOffset.UtcNow;
var fixtureId = Guid.NewGuid();
var fixtureDirectory = Path.Combine(root,"Sources",fixtureId.ToString("N"));
var indexRoot = Path.Combine(root,"Data","Index");
var retainedRoot = Path.Combine(root,"Data","Retained");
foreach (var directory in new[] { fixtureDirectory,indexRoot,retainedRoot })
{
    for (var path = directory; path is not null; path = Path.GetDirectoryName(path))
        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Linked acceptance storage is forbidden.");
    Directory.CreateDirectory(directory);
}
await using (var precondition = factory.CreateDbContext())
    if (await precondition.SourceDeletionOperations.AnyAsync(o => o.State == 0 || o.State == 1))
        throw new InvalidOperationException("Finish runnable synthetic operations before seeding. Attention evidence is preserved; no automatic repair or reset is performed.");
var rootIds = new[] { Guid.NewGuid(),Guid.NewGuid() };
var generationIds = Enumerable.Range(0,generationCount).Select(_ => Guid.NewGuid()).ToArray();
var recordIds = new List<Guid[]>();
var chunkSpecs = new List<ChunkSpec[]>();
var originals = new List<object>();
await using (var setup = factory.CreateDbContext())
{
    for (var source = 0; source < 2; source++)
    {
        var sourceDirectory = Path.Combine(fixtureDirectory,source == 0 ? "delete" : "preserve");
        Directory.CreateDirectory(sourceDirectory);
        setup.SourceRootConfigurations.Add(new() { Id = rootIds[source],CanonicalPath = sourceDirectory,
            DisplayName = "Deletion acceptance " + (source == 0 ? "target " : "control ") + fixtureId.ToString("N"),
            State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[\"*.never-ingest\"]",
            ExcludePatternsJson = "[]",AllowedClassificationsJson = "[\"text/plain\"]",MaximumFileBytes = 1048576,
            ReconciliationCadenceSeconds = 86400,LastScanCompletedAtUtc = now,ConfigurationRevision = 1,CreatedAtUtc = now,UpdatedAtUtc = now });
        var records = Enumerable.Range(0,source == 0 && scale ? 11 : 1).Select(_ => Guid.NewGuid()).ToArray();
        recordIds.Add(records);
        var sourceCount = source == 0 ? ownedCount : otherCount;
        var sourceChunks = new ChunkSpec[sourceCount];
        chunkSpecs.Add(sourceChunks);
        for (var recordOrdinal = 0; recordOrdinal < records.Length; recordOrdinal++)
        {
            var record = records[recordOrdinal];
            var revision = Guid.NewGuid(); var identity = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            var parts = Enumerable.Range(0,sourceCount).Where(i => i % records.Length == recordOrdinal)
                .Select(i => (ordinal: i,text: "Synthetic acceptance passage " + source + " " + i)).ToArray();
            var text = string.Join('\n',parts.Select(p => p.text)) + "\n";
            var offset = 0;
            foreach (var part in parts)
            {
                sourceChunks[part.ordinal] = new(artifactId,part.ordinal/records.Length,offset,part.text,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(part.text))));
                offset += part.text.Length+1;
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var original = Path.Combine(sourceDirectory,record.ToString("N") + ".txt");
            var relative = Path.Combine("sha256",hash[..2],hash + ".bin");
            await File.WriteAllBytesAsync(original,bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(retainedRoot,relative))!);
            await File.WriteAllBytesAsync(Path.Combine(retainedRoot,relative),bytes);
            originals.Add(new { path = original,sha256 = hash,source,retained = relative });
            setup.SourceRevisions.Add(new() { Id = revision,SourceRootId = rootIds[source],StableSourceIdentity = "acceptance:" + revision,
                Revision = 1,ContentSha256 = hash,CanonicalPath = original,Classification = "AcceptedUtf8Text",Extension = ".txt",
                ByteLength = bytes.Length,DiscoveredAtUtc = now,DiscoveryEvidenceJson = "{}" });
            setup.SourceIdentities.Add(new() { Id = identity,SourceKind = "local file",StableKey = "acceptance:" + identity,CreatedAtUtc = now });
            setup.PipelineRecords.Add(new() { Id = record,SourceIdentityId = identity,SourceRevisionId = revision,Revision = 1,
                ContentHash = hash,RootLineageRecordId = record,CurrentStage = 5,CompletionCriteriaMet = true,RegisteredAtUtc = now });
            setup.Artifacts.Add(new() { Id = artifactId,PipelineRecordId = record,SourceRevision = 1,Stage = 3,
                ContentHash = hash,ContentType = "text/plain",SearchText = text,CreatedAtUtc = now });
            setup.SourceArtifacts.Add(new() { Id = Guid.NewGuid(),SourceRevisionId = revision,ContentSha256 = hash,
                StoreRelativePath = relative,ByteLength = bytes.Length,ChecksumVerifiedAtUtc = now,ReferenceCount = 1 });
        }
    }
    setup.IndexGenerations.AddRange(generationIds.Select(id => new IndexGenerationEntity { Id = id,
        ModelFingerprint = "source-deletion-synthetic:1",Dimensions = 1,IndexPath = Path.Combine(indexRoot,"generations",id.ToString("N")),
        MetadataChecksum = new string('a',64),VectorCount = ownedCount+otherCount,CreatedAtUtc = now,ValidatedAtUtc = now }));
    var state = await setup.IndexState.SingleAsync(); state.CorpusVersion++; state.EmptyCatalogueValidatedAtUtc = null;
    await setup.SaveChangesAsync();
}
foreach (var id in generationIds)
{
    var directory = Path.Combine(indexRoot,"generations",id.ToString("N"));
    Directory.CreateDirectory(Path.Combine(directory,"partial"));
    await File.WriteAllTextAsync(Path.Combine(directory,"synthetic-history.txt"),"Synthetic cleanup-only generation; not a query index.");
}
var vectorIds = new List<long[]>();
for (var source = 0; source < 2; source++)
{
    var ids = new List<long>();
    var count = source == 0 ? ownedCount : otherCount;
    for (var start = 0; start < count; start += 500)
    {
        await using var setup = factory.CreateDbContext();
        var batch = new List<VectorEntity>();
        foreach (var ordinal in Enumerable.Range(start,Math.Min(500,count-start)))
        {
            var spec = chunkSpecs[source][ordinal];
            var chunk = new TextChunkEntity { ArtifactId = spec.ArtifactId,SourceRevision = 1,Ordinal = spec.Ordinal,StartOffset = spec.Offset,
                Length = spec.Text.Length,Content = spec.Text,ContentHash = spec.Hash };
            var values = BitConverter.GetBytes(1.0f);
            batch.Add(new() { TextChunk = chunk,SourceRevision = 1,ModelFingerprint = "source-deletion-synthetic:1",Dimensions = 1,
                Values = values,TextChunkContentHash = spec.Hash,PayloadChecksum = Convert.ToHexStringLower(SHA256.HashData(values)),
                IndexGenerationId = generationIds[0],CreatedAtUtc = now });
        }
        setup.Vectors.AddRange(batch); await setup.SaveChangesAsync(); ids.AddRange(batch.Select(v => v.VectorId));
    }
    vectorIds.Add(ids.ToArray());
}
// An explicit survivor-only historical generation must remain untouched, even
// while every mixed generation below loses the target's memberships and files.
var untouchedGenerationId = Guid.NewGuid();
var untouchedDirectory = Path.Combine(indexRoot,"generations",untouchedGenerationId.ToString("N"));
var untouchedMarker = Encoding.UTF8.GetBytes("Untouched synthetic survivor-only generation " + fixtureId.ToString("N"));
Directory.CreateDirectory(untouchedDirectory);
await File.WriteAllBytesAsync(Path.Combine(untouchedDirectory,"untouched-history.txt"),untouchedMarker);
var untouchedGeneration = new IndexGenerationEntity { Id = untouchedGenerationId,
    ModelFingerprint = "source-deletion-synthetic:1",Dimensions = 1,IndexPath = untouchedDirectory,
    MetadataChecksum = new string('b',64),VectorCount = 1,CreatedAtUtc = now,ValidatedAtUtc = now };
await using (var setup = factory.CreateDbContext())
{
    setup.IndexGenerations.Add(untouchedGeneration);
    setup.IndexGenerationVectors.Add(new() { GenerationId = untouchedGenerationId,VectorId = vectorIds[1][0] });
    await setup.SaveChangesAsync();
}
await using (var connection = new SqlConnection(binding.ConnectionString))
{
    await connection.OpenAsync();
    await using (var command = new SqlCommand("CREATE TABLE #acceptance_vectors (VectorId bigint NOT NULL, Ordinal int NOT NULL, Source int NOT NULL); CREATE TABLE #acceptance_generations (GenerationId uniqueidentifier NOT NULL, Ordinal int NOT NULL);",connection))
        await command.ExecuteNonQueryAsync();
    var vectors = new DataTable(); vectors.Columns.Add("VectorId",typeof(long)); vectors.Columns.Add("Ordinal",typeof(int)); vectors.Columns.Add("Source",typeof(int));
    for (var source = 0; source < 2; source++) for (var ordinal = 0; ordinal < vectorIds[source].Length; ordinal++) vectors.Rows.Add(vectorIds[source][ordinal],ordinal,source);
    var generations = new DataTable(); generations.Columns.Add("GenerationId",typeof(Guid)); generations.Columns.Add("Ordinal",typeof(int));
    for (var ordinal = 0; ordinal < generationIds.Length; ordinal++) generations.Rows.Add(generationIds[ordinal],ordinal);
    using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#acceptance_vectors" }) await bulk.WriteToServerAsync(vectors);
    using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#acceptance_generations" }) await bulk.WriteToServerAsync(generations);
    // Setup only. Separate generation-sized transactions keep seed work bounded.
    foreach (var ordinal in Enumerable.Range(0,generationCount))
    {
        await using var command = new SqlCommand("""
            INSERT INTO IndexGenerationVectors (GenerationId,VectorId)
            SELECT g.GenerationId,v.VectorId FROM #acceptance_vectors v CROSS JOIN #acceptance_generations g
            WHERE g.Ordinal=@generation AND
              ((v.Source=0 AND (g.Ordinal+v.Ordinal)%@generations < @ownedBase + CASE WHEN v.Ordinal < @ownedRemainder THEN 1 ELSE 0 END)
              OR (v.Source=1 AND CONVERT(bigint,v.Ordinal)*@generations+g.Ordinal < @otherRows));
            """,connection) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("@generation",ordinal); command.Parameters.AddWithValue("@generations",generationCount);
        command.Parameters.AddWithValue("@ownedBase",historicalOwned/ownedCount); command.Parameters.AddWithValue("@ownedRemainder",historicalOwned%ownedCount);
        command.Parameters.AddWithValue("@otherRows",historicalOther); await command.ExecuteNonQueryAsync();
        if ((ordinal+1)%100 == 0 || ordinal+1 == generationCount)
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "seed-history",generationsCommitted = ordinal+1,generationCount }));
    }
}
var publisher = new UsearchGenerationBuilder(new SqlPipelineStore(factory,TimeProvider.System),new UsearchIndexOptions(indexRoot),new UsearchGenerationValidator());
var candidate = await publisher.BuildAndPlaceAsync(Guid.NewGuid(),default);
await using (var setup = factory.CreateDbContext())
{
    var descriptor = candidate.Generation;
    setup.IndexGenerations.Add(new() { Id = descriptor.Id,ModelFingerprint = descriptor.ModelFingerprint,Dimensions = descriptor.Dimensions,
        IndexPath = descriptor.IndexPath,MetadataChecksum = descriptor.MetadataChecksum,VectorCount = descriptor.VectorCount,
        CreatedAtUtc = now,ValidatedAtUtc = now,CorpusEpoch = descriptor.CorpusStamp?.CorpusEpoch,CorpusVersion = descriptor.CorpusStamp?.CorpusVersion });
    setup.IndexGenerationVectors.AddRange(candidate.Vectors.Select(v => new IndexGenerationVectorEntity { GenerationId = descriptor.Id,VectorId = v.VectorId }));
    var state = await setup.IndexState.SingleAsync(); state.ActiveIndexGenerationId = descriptor.Id;
    await setup.SaveChangesAsync();
}
var evidenceDirectory = Path.Combine(root,"Recovery","acceptance",fixtureId.ToString("N")); Directory.CreateDirectory(evidenceDirectory);
var manifest = new { fixtureId,mode,rootIds,ownedCount,otherCount,generationCount,generationIds,historicalOwned,historicalOther,
    activeGeneration = candidate.Generation.Id,originals,createdAtUtc = now,
    untouchedGenerationId,untouchedVectorId = vectorIds[1][0],untouchedRowVersion = Convert.ToHexStringLower(untouchedGeneration.RowVersion),
    untouchedMarkerSha256 = Convert.ToHexStringLower(SHA256.HashData(untouchedMarker)) };
await File.WriteAllTextAsync(Path.Combine(evidenceDirectory,"manifest.json"),JsonSerializer.Serialize(manifest,new JsonSerializerOptions { WriteIndented = true }));
if (mode == "seed-failure")
{
    using var fixture = JsonDocument.Parse(JsonSerializer.Serialize(manifest));
    var first = fixture.RootElement.GetProperty("originals").EnumerateArray().First(o => o.GetProperty("source").GetInt32() == 0);
    var hash = first.GetProperty("sha256").GetString();
    await using var check = factory.CreateDbContext();
    if (await check.SourceArtifacts.AnyAsync(a => a.ContentSha256 == hash && check.SourceRevisions.Any(r =>
        r.Id == a.SourceRevisionId && r.SourceRootId != rootIds[0]))) throw new InvalidOperationException("Refusing to inject a failure into shared content.");
    await File.WriteAllTextAsync(Path.Combine(retainedRoot,first.GetProperty("retained").GetString()!),"Invalid synthetic acceptance payload.");
}
Console.WriteLine(JsonSerializer.Serialize(new { fixtureId,rootIds,ownedCount,otherCount,generationCount,historicalOwned,historicalOther,
    activeMembers = candidate.Vectors.Count,evidenceDirectory }));

sealed record StageSettings(string SqlConnectionString,string Origin);
sealed record ChunkSpec(Guid ArtifactId,int Ordinal,int Offset,string Text,string Hash);
sealed class StageFactory(string binding) : IDbContextFactory<FluxKnowledgeDbContext>
{
    public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(binding).Options);
}
