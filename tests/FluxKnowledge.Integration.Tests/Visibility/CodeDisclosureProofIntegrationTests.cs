using System.Data.Common;
using System.Text.Json;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Search;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Visibility;

[Collection("sql-full-text")]
public sealed class CodeDisclosureProofIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Whole_current_source_returns_the_method_despite_a_remote_protected_expression()
    {
        const string relative = "src/FluxKnowledge.Infrastructure.SqlServer/Persistence/SqlSourceScanStore.cs";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative))) directory = directory.Parent;
        Assert.NotNull(directory);
        var text = (await File.ReadAllTextAsync(Path.Combine(directory.FullName, relative))).Replace("\r\n", "\n", StringComparison.Ordinal);
        var method = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .Single(value => value.Identifier.ValueText == "OwnsRootScanAsync").ToString();
        var factory = SqlTestData.CreateFactory(fixture);
        await SqlTestData.ClearPipelineAsync(fixture);
        var root = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var chunks = Enumerable.Range(0, (text.Length + 511) / 512)
                .Select(index => text.Substring(index * 512, Math.Min(512, text.Length - index * 512))).ToArray();
            ScopedCorpusRetrievalTests.AddPublishedText(db, root, $@"C:\proof\{root:N}", "SqlSourceScanStore.cs", chunks);
            await db.SaveChangesAsync();
        }
        var disclosure = new LocalPrivateContentDisclosure();
        var store = new SqlCodeDisclosureProofStore(factory);
        var candidate = Assert.IsType<CodeDisclosureArtifact>(await store.ReadNextAsync(CancellationToken.None));
        var built = new CsharpDisclosureProofBuilder(disclosure).Build(candidate.ArtifactId, candidate.Text!, CancellationToken.None);
        Assert.Equal(text, candidate.Text);
        Assert.Equal(CodeDisclosureProofState.Ready, built.State);
        Assert.True(await store.CommitAsync(candidate, built, CancellationToken.None));
        var rawReader = new SqlCorpusRetrievalReader(factory);
        var rawCandidate = Assert.Single(await rawReader.SearchAsync("private static async Task<bool> OwnsRootScanAsync",
            new("root", [root], null), 20, CancellationToken.None), value => value.Content.Contains("private static async Task<bool> OwnsRootScanAsync", StringComparison.Ordinal));
        var directBinding = new CorpusEvidenceBinding(2, rawCandidate.RootId, rawCandidate.OwnerSourceRevisionId,
            CodeDisclosureIntegrity.Hash(rawCandidate.SourceIdentity),rawCandidate.PipelineRecordId,rawCandidate.PipelineRecordRevision,
            rawCandidate.ArtifactId,rawCandidate.ArtifactHash,rawCandidate.ChunkId,rawCandidate.ChunkHash,
            rawCandidate.StartOffset,rawCandidate.Length,rawCandidate.CorpusEpoch);
        var directRead = Assert.IsType<EligibleContext>(await rawReader.ReadAsync(directBinding,0,CancellationToken.None));
        Assert.NotNull(directRead.DisclosureText);
        Assert.False(disclosure.EvaluateCodeGuard(directRead.DisclosureText,LocalDisclosureKind.RetainedDetail,directRead.GuardProof,
            directRead.StartOffset,directRead.Text.Length).Withheld);
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new ScopedCorpusRetrievalTests.TestEvidenceCodec(), disclosure);
        var declarations = await service.SearchAsync(new("private static async Task<bool> OwnsRootScanAsync", 5, "root", root, null), CancellationToken.None);
        Assert.Contains(declarations.Results, hit => hit.Passage.Contains("OwnsRootScanAsync", StringComparison.Ordinal));
        var body = Assert.Single((await service.SearchAsync(new("var lease = ownership.Lease;", 5, "root", root, null), CancellationToken.None)).Results);
        var read = await service.ReadAsync(new(body.EvidenceRef, 2048), CancellationToken.None);
        Assert.Contains(method, read.Text, StringComparison.Ordinal);
        Assert.Equal(text.Substring(read.StartOffset, read.Text.Length), read.Text);
        Assert.True(read.StartOffset > 16_384);
    }

    [NativeSqlServerFact]
    public async Task Model_free_backfill_enables_late_method_search_and_exact_context_read()
    {
        var (factory, root, artifact, method) = await SeedAsync();
        var codec = new ScopedCorpusRetrievalTests.TestEvidenceCodec();
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory), codec,
            new LocalPrivateContentDisclosure());
        var before = Assert.Single((await service.SearchAsync(new("LateAnswer", 5, "root", root, null), CancellationToken.None)).Results);
        var refusal = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new(before.EvidenceRef, 4096), CancellationToken.None));
        Assert.Equal("content-withheld", refusal.ReasonCode);
        var store = new SqlCodeDisclosureProofStore(factory);
        var candidate = Assert.IsType<CodeDisclosureArtifact>(
            await store.ReadNextAsync(CancellationToken.None));
        Assert.Equal(artifact, candidate.ArtifactId);
        var proof = new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure())
            .Build(candidate.ArtifactId, candidate.Text!, CancellationToken.None);
        Assert.True(await store.CommitAsync(candidate, proof, CancellationToken.None));
        Assert.True(await store.CommitAsync(candidate, proof, CancellationToken.None));
        var hit = Assert.Single((await service.SearchAsync(new("LateAnswer", 5, "root", root, null), CancellationToken.None)).Results);
        var read = await service.ReadAsync(new(hit.EvidenceRef, 4096), CancellationToken.None);
        Assert.Contains(method, read.Text, StringComparison.Ordinal);
        Assert.Equal(hit.StartOffset, read.CitedStart);
        Assert.True(read.StartOffset > 16_384);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Equal(candidate.CanonicalHash, (await db.Artifacts.FindAsync(artifact))!.ContentHash);
        Assert.Empty(await db.Vectors.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Hold_deletion_corruption_and_missing_rows_cannot_publish_or_enable_partial_proof()
    {
        var (factory, root, artifact, _) = await SeedAsync(includeProtectedPrefix: true);
        var hold = new Hold();
        var store = new SqlCodeDisclosureProofStore(factory, hold);
        var candidate = (await store.ReadNextAsync(CancellationToken.None))!;
        var proof = new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure())
            .Build(artifact, candidate.Text!, CancellationToken.None);
        hold.IsHeld = true;
        Assert.Null(await store.ReadNextAsync(CancellationToken.None));
        Assert.False(await store.CommitAsync(candidate, proof, CancellationToken.None));
        hold.IsHeld = false;
        Assert.True(await store.CommitAsync(candidate, proof, CancellationToken.None));
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [CanonicalCodeDisclosureSpans] WHERE [ArtifactId] = {artifact} AND [Kind] = 1");
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new ScopedCorpusRetrievalTests.TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        Assert.Empty((await service.SearchAsync(new("LateAnswer", 5, "root", root, null), CancellationToken.None)).Results);
        await db.PipelineRecords.Where(record => record.Id == candidate.PipelineRecordId)
            .ExecuteUpdateAsync(set => set.SetProperty(record => record.IsDeleted, true));
        Assert.False(await store.CommitAsync(candidate, proof, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Cancelled_and_concurrent_writes_leave_one_immutable_complete_projection_and_cleanup_cascades()
    {
        var (factory, _, artifact, _) = await SeedAsync();
        var store = new SqlCodeDisclosureProofStore(factory);
        var candidate = (await store.ReadNextAsync(CancellationToken.None))!;
        var proof = new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure())
            .Build(artifact, candidate.Text!, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.CommitAsync(candidate, proof, cancellation.Token));
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.CommitAsync(candidate, proof, CancellationToken.None).AsTask()));
        Assert.All(results, Assert.True);
        Assert.Null(await store.ReadNextAsync(CancellationToken.None));
        await using var db = await factory.CreateDbContextAsync();
        var header = Assert.Single(await db.CanonicalCodeDisclosureProofs.ToArrayAsync());
        Assert.Equal(proof.Checksum, header.Checksum);
        Assert.Equal(proof.Spans.Count, await db.CanonicalCodeDisclosureSpans.CountAsync());
        header.State = (int)CodeDisclosureProofState.Invalid;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        await db.TextChunks.Where(value => value.ArtifactId == artifact).ExecuteDeleteAsync();
        await db.Artifacts.Where(value => value.Id == artifact).ExecuteDeleteAsync();
        Assert.Empty(await db.CanonicalCodeDisclosureProofs.ToArrayAsync());
        Assert.Empty(await db.CanonicalCodeDisclosureSpans.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_insert_failure_or_a_new_hold_rolls_back_header_and_every_span(bool introduceHold)
    {
        var (factory, _, artifact, _) = await SeedAsync();
        var candidate = (await new SqlCodeDisclosureProofStore(factory).ReadNextAsync(CancellationToken.None))!;
        var proof = new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure())
            .Build(artifact, candidate.Text!, CancellationToken.None);
        var hold = new Hold();
        var interceptor = new AfterProofInsert(() =>
        {
            if (introduceHold) hold.IsHeld = true;
            else throw new InjectedProofFailure();
        });
        var observedFactory = new ObservedFactory(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(fixture.ConnectionString).AddInterceptors(interceptor).Options);
        var store = new SqlCodeDisclosureProofStore(observedFactory, hold);
        if (introduceHold) Assert.False(await store.CommitAsync(candidate, proof, CancellationToken.None));
        else
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(async () =>
                await store.CommitAsync(candidate, proof, CancellationToken.None));
            Assert.IsType<InjectedProofFailure>(failure.InnerException);
        }
        Assert.True(interceptor.Observed);
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalCodeDisclosureProofs.ToArrayAsync());
        Assert.Empty(await db.CanonicalCodeDisclosureSpans.ToArrayAsync());
        hold.IsHeld = false;
        Assert.True(await new SqlCodeDisclosureProofStore(factory, hold).CommitAsync(candidate, proof, CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Changed_canonical_identity_policy_or_suppression_cannot_publish_stale_proof()
    {
        var (factory, root, artifact, _) = await SeedAsync();
        var store = new SqlCodeDisclosureProofStore(factory);
        var candidate = (await store.ReadNextAsync(CancellationToken.None))!;
        var proof = new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure())
            .Build(artifact, candidate.Text!, CancellationToken.None);
        foreach (var corrupt in new[]
        {
            proof with { CanonicalHash = new string('a', 64) },
            proof with { Fingerprint = "previous-policy" },
            proof with { Spans = [proof.Spans[0] with { Start = proof.Spans[0].Start + 1 }, ..proof.Spans.Skip(1)] }
        })
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.CommitAsync(candidate, corrupt, CancellationToken.None));
        await using var db = await factory.CreateDbContextAsync();
        Assert.Empty(await db.CanonicalCodeDisclosureProofs.ToArrayAsync());
        await db.SourceRootConfigurations.Where(value => value.Id == root)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.State, 2));
        Assert.False(await store.CommitAsync(candidate, proof, CancellationToken.None));
        await db.SourceRootConfigurations.Where(value => value.Id == root)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.State, 0));
        var revision = await db.PipelineRecords.Where(value => value.Id == candidate.PipelineRecordId)
            .Select(value => value.SourceRevisionId).SingleAsync();
        await db.SourceRevisions.Where(value => value.Id == revision)
            .ExecuteUpdateAsync(set => set.SetProperty(value => value.SuppressedAtUtc, DateTimeOffset.UtcNow));
        Assert.False(await store.CommitAsync(candidate, proof, CancellationToken.None));
        Assert.Empty(await db.CanonicalCodeDisclosureSpans.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Additive_migration_and_repeated_upgrade_preserve_canonical_chunks_vectors_and_generation()
    {
        await using var pipeline = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(
            fixture, "The retained migration answer is forty two.", new PassageBuilder(new MigrationWords()));
        await using var db = await pipeline.Factory.CreateDbContextAsync();
        var before = await CanonicalSnapshotAsync(db);
        // This generated disposable catalog alone is moved to the prior schema.
        await db.GetService<IMigrator>().MigrateAsync("20260927202655_AddCorpusRebuildSupersession");
        await db.Database.MigrateAsync();
        Assert.Equal(before, await CanonicalSnapshotAsync(db));
        Assert.Empty(await db.CanonicalCodeDisclosureProofs.ToArrayAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(before, await CanonicalSnapshotAsync(db));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [NativeSqlServerFact]
    public async Task Old_policy_is_ignored_and_invalid_terminal_proof_is_not_requeued()
    {
        var (factory, _, artifact, _) = await SeedAsync(canonicalOverride: "class C {");
        await using var db = await factory.CreateDbContextAsync();
        var canonical = await db.Artifacts.SingleAsync(value => value.Id == artifact);
        const string previous = "previous-disclosure-policy";
        db.CanonicalCodeDisclosureProofs.Add(new CanonicalCodeDisclosureProofEntity
        {
            ArtifactId = artifact, CanonicalHash = canonical.ContentHash,
            CanonicalLength = canonical.SearchText!.Length, Fingerprint = previous,
            State = (int)CodeDisclosureProofState.Unsupported, SpanCount = 0,
            Checksum = CodeDisclosureIntegrity.ProofChecksum(artifact, canonical.ContentHash, previous,
                CodeDisclosureProofState.Unsupported, canonical.SearchText.Length, [])
        });
        await db.SaveChangesAsync();
        var store = new SqlCodeDisclosureProofStore(factory);
        var candidate = (await store.ReadNextAsync(CancellationToken.None))!;
        Assert.Equal(artifact, candidate.ArtifactId);
        var proof = new CsharpDisclosureProofBuilder(new LocalPrivateContentDisclosure())
            .Build(artifact, candidate.Text!, CancellationToken.None);
        Assert.Equal(CodeDisclosureProofState.Invalid, proof.State);
        Assert.True(await store.CommitAsync(candidate, proof, CancellationToken.None));
        Assert.Null(await store.ReadNextAsync(CancellationToken.None));
        Assert.Equal(2, await db.CanonicalCodeDisclosureProofs.CountAsync());
        Assert.Empty(await db.CanonicalCodeDisclosureSpans.ToArrayAsync());
    }

    private static async Task<string> CanonicalSnapshotAsync(FluxKnowledgeDbContext db) => JsonSerializer.Serialize(new
    {
        Artifacts = await db.Artifacts.AsNoTracking().OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.ContentHash, value.SearchText, value.SourceRevision }).ToArrayAsync(),
        Chunks = await db.TextChunks.AsNoTracking().OrderBy(value => value.Id)
            .Select(value => new { value.Id, value.ArtifactId, value.Content, value.ContentHash, value.SearchInputHash }).ToArrayAsync(),
        Vectors = await db.Vectors.AsNoTracking().OrderBy(value => value.VectorId).ToArrayAsync(),
        Index = await db.IndexState.AsNoTracking().ToArrayAsync(),
        Jobs = await db.Jobs.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync()
    });

    private async Task<(IDbContextFactory<FluxKnowledgeDbContext> Factory, Guid Root, Guid Artifact, string Method)> SeedAsync(
        bool includeProtectedPrefix = false, string? canonicalOverride = null)
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await SqlTestData.ClearPipelineAsync(fixture);
        await using var db = await factory.CreateDbContextAsync();
        const string method = "public int LateAnswer() {\nreturn 42;\n}";
        var prefix = (includeProtectedPrefix ? "class Before { const string value = \"eyJwYXNzd29yZCI6InN5bnRoZXRpYyJ9\"; }\n" : "") +
            "// 😀\n" + string.Concat(Enumerable.Repeat("// safe padding\n", 1_500)) +
            "namespace Sample { class C {\n";
        var following = "\npublic int Other() {\n" +
            string.Concat(Enumerable.Repeat("var harmless = 1;\n", 300)) + "return 1;\n}\n} }";
        var chunks = new List<string>();
        for (var start = 0; start < prefix.Length; start += 1_024)
            chunks.Add(prefix.Substring(start, Math.Min(1_024, prefix.Length - start)));
        chunks.Add(method);
        for (var start = 0; start < following.Length; start += 1_024)
            chunks.Add(following.Substring(start, Math.Min(1_024, following.Length - start)));
        if (canonicalOverride is not null) { chunks.Clear(); chunks.Add(canonicalOverride); }
        var root = Guid.NewGuid();
        ScopedCorpusRetrievalTests.AddPublishedText(db, root, $@"C:\proof\{root:N}", "late.cs", chunks);
        var artifact = db.Artifacts.Local.Single().Id;
        await db.SaveChangesAsync();
        return (factory, root, artifact, method);
    }

    private sealed class Hold : IDeploymentValidationHold
    {
        public bool IsHeld { get; set; }
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ObservedFactory(DbContextOptions<FluxKnowledgeDbContext> options)
        : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(options);
    }

    private sealed class AfterProofInsert(Action afterInsert) : DbCommandInterceptor
    {
        public bool Observed { get; private set; }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Observed && command.CommandText.Contains("CanonicalCodeDisclosureSpans", StringComparison.Ordinal) &&
                (command.CommandText.Contains("INSERT", StringComparison.Ordinal) || command.CommandText.Contains("MERGE", StringComparison.Ordinal)))
            {
                Observed = true;
                try { afterInsert(); }
                catch { result.Dispose(); throw; }
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InjectedProofFailure : Exception;
    private sealed class MigrationWords : IPassageTokenizer
    {
        public string Fingerprint => "synthetic-proof-migration-v1";
        public int CountTokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
