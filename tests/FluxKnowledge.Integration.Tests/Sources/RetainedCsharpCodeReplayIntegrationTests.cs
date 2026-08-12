using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Disposable-SQL contract tests for durable retained C# completion ownership.</summary>
public sealed class RetainedCsharpCodeReplayIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Additive_migration_upgrades_the_previous_schema_with_csharp_hard_denials_and_restrictive_fact_tables()
    {
        await using var database = await fixture.CreateRetainedCsharpPreviousMigrationDatabaseAsync();
        await using var context = database.CreateContext();
        await context.GetService<IMigrator>().MigrateAsync();

        Assert.Contains("csharp-code-syntax-invalid", await context.OperatorActionHardDenials.Select(value => value.ReasonCode).ToListAsync());
        Assert.True(await context.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] = N'SourceProcessorCodeBlockedDiagnostics'").SingleAsync() == 1);
        Assert.True(await context.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.foreign_keys WHERE [name] = N'FK_SourceProcessorCodeBlockedDiagnostics_SourceProcessorAttempts_SourceProcessorBranchId_SourceProcessorAttemptId' AND [delete_referential_action] = 0").SingleAsync() == 1);
    }

    [NativeSqlServerFact]
    public async Task Csharp_claims_materialise_the_persisted_attempt_identity()
    {
        var bytes = Encoding.UTF8.GetBytes("class C { }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = new SqlRetainedProcessorBranchStore(
            SqlTestData.CreateFactory(fixture),
            TimeProvider.System);

        var claims = await store.ClaimCsharpCodeAsync(
            "retained-csharp-test-owner",
            1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None);

        var claim = Assert.Single(claims);
        await using var verification = CreateContext();
        var attempt = await verification.SourceProcessorAttempts.SingleAsync(value => value.Id == claim.AttemptId);
        Assert.Equal(seeded.BranchId, claim.BranchId);
        Assert.Equal(seeded.BranchId, attempt.BranchId);
        Assert.Equal(claim.LeaseGeneration, attempt.LeaseGeneration);
    }

    [NativeSqlServerFact]
    public async Task Successful_completion_is_atomic_and_receipt_first_replay_survives_the_original_lease()
    {
        var bytes = Encoding.UTF8.GetBytes("namespace N; public sealed class C { public void M() { } }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = new SqlRetainedProcessorBranchStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var claim = Assert.Single(await store.ClaimCsharpCodeAsync("retained-csharp-success", 1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        var completion = await ProcessAsync(claim, bytes);

        var first = await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None);
        var replay = await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None);
        var conflict = await store.CompleteRetainedCsharpCodeAsync(
            claim,
            CreateValidConflictingSuccess(completion),
            CancellationToken.None);

        Assert.True(first.IsCommitted); Assert.False(first.IsReplay);
        Assert.True(replay.IsCommitted); Assert.True(replay.IsReplay);
        Assert.False(conflict.IsCommitted); Assert.Equal("csharp-code-completion-conflict", conflict.OutcomeCode);
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceProcessorCodeDocuments.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Single(await verification.SourceProcessorCodeCompletionReceipts.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Equal((int)RetainedProcessorBranchState.Completed,
            (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).State);
    }

    [NativeSqlServerFact]
    public async Task Syntax_invalid_completion_replays_the_original_attempt_owned_blocked_diagnostics_without_a_document()
    {
        var bytes = Encoding.UTF8.GetBytes("class C {");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var store = new SqlRetainedProcessorBranchStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var claim = Assert.Single(await store.ClaimCsharpCodeAsync("retained-csharp-syntax", 1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        var completion = await ProcessAsync(claim, bytes);
        Assert.Equal("csharp-code-syntax-invalid", completion.OutcomeCode);

        Assert.True((await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None)).IsCommitted);
        var replay = await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None);

        Assert.True(replay.IsReplay);
        await using var verification = CreateContext();
        Assert.Empty(await verification.SourceProcessorCodeDocuments.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.All(await verification.SourceProcessorCodeBlockedDiagnostics.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync(), value =>
            Assert.Equal(claim.AttemptId, value.SourceProcessorAttemptId));
        var receipt = Assert.Single(await verification.SourceProcessorCodeCompletionReceipts.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Null(receipt.DocumentId); Assert.Equal(claim.AttemptId, receipt.SourceProcessorAttemptId);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_completion_reuses_one_receipt_without_duplicate_csharp_facts()
    {
        var bytes = Encoding.UTF8.GetBytes("namespace C; public sealed class Concurrent { public int Value => 1; }");
        var seeded = await SeedCsharpBranchAsync(bytes);
        var firstStore = new SqlRetainedProcessorBranchStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var secondStore = new SqlRetainedProcessorBranchStore(SqlTestData.CreateFactory(fixture), TimeProvider.System);
        var claim = Assert.Single(await firstStore.ClaimCsharpCodeAsync("retained-csharp-race", 1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, CancellationToken.None));
        var completion = await ProcessAsync(claim, bytes);

        var results = await Task.WhenAll(
            firstStore.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None).AsTask(),
            secondStore.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None).AsTask());

        Assert.All(results, result => Assert.True(result.IsCommitted));
        Assert.Single(results, result => !result.IsReplay);
        Assert.Single(results, result => result.IsReplay);
        await using var verification = CreateContext();
        Assert.Single(await verification.SourceProcessorCodeCompletionReceipts.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
        Assert.Single(await verification.SourceProcessorCodeDocuments.Where(value => value.SourceProcessorBranchId == seeded.BranchId).ToListAsync());
    }

    [NativeSqlServerFact]
    public async Task Hosted_replan_reads_only_the_verified_retained_artifact_and_fences_the_legacy_holding_route()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-csharp-retained-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("namespace R; public sealed class RetainedOnly { }");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(root, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(root, relative), bytes);
            var seeded = await SeedDeferredCsharpAsync(hash, bytes.Length, relative);
            var factory = SqlTestData.CreateFactory(fixture);
            var reader = new SqlRetainedSourceReader(factory, root);
            var activation = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([new RetainedCsharpCodeCapabilityHandler()])),
                new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), reader,
                new ZipArchiveRetainedProcessor(new SqlRetainedArtifactWriter(factory, root)),
                new RetainedProcessorOptions { CsharpCodeEnabled = true, ArchiveZipExpandEnabled = false }, TimeProvider.System,
                csharpProcessor: new RetainedCsharpCodeProcessor(reader, new LocalPrivateContentDisclosure()));

            var result = await activation.RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.CompletedBranches);
            await using var verification = CreateContext();
            Assert.Equal((int)SourceActivityState.CancelledSuperseded, (await verification.SourceActivities.SingleAsync(value => value.Id == seeded.ActivityId)).State);
            var successor = await verification.SourceActivities.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId && value.Id != seeded.ActivityId);
            Assert.Equal((int)SourceActivityKind.CodeParsing, successor.ActivityKind);
            Assert.Equal(RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, successor.DescriptorFingerprint);
            Assert.Single(await verification.SourceProcessorCodeDocuments.Where(value => value.SourceRevisionId == seeded.RevisionId).ToListAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private async Task<(Guid BranchId, Guid RevisionId)> SeedCsharpBranchAsync(byte[] bytes)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var branchId = Guid.NewGuid();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\retained-csharp\\{rootId:N}", DisplayName = "C# test", State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 64L * 1024 * 1024, AllowedClassificationsJson = "[\"text/plain\"]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"retained-csharp:{revisionId:N}", Revision = 1, ContentSha256 = hash, CanonicalPath = "C:\\source-original-must-not-be-read.cs", Classification = "AcceptedUtf8Text", Extension = ".cs", ByteLength = bytes.Length, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = $"sha256\\{hash[..2]}\\{hash}.bin", ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.CodeParsing, ExecutionClass = 0, ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion, InputFingerprint = hash, DescriptorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, State = (int)SourceActivityState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = branchId, SourceActivityId = activityId, SourceRevisionId = revisionId, InputSha256 = hash, ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion, ProcessorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, State = (int)RetainedProcessorBranchState.Pending, CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (branchId, revisionId);
    }

    private async Task<(Guid ActivityId, Guid RevisionId)> SeedDeferredCsharpAsync(string hash, int byteLength, string relativePath)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var activityId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\retained-csharp-holding\\{rootId:N}", DisplayName = "C# holding", State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 64L * 1024 * 1024, AllowedClassificationsJson = "[\"text/plain\"]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"retained-csharp-holding:{revisionId:N}", Revision = 1, ContentSha256 = hash, CanonicalPath = "C:\\source-original-must-not-be-read.cs", Classification = "AcceptedUtf8Text", Extension = ".cs", ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing, ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion, InputFingerprint = hash, RequiredCapability = RetainedCsharpCodeProcessor.ProcessorKind, State = (int)SourceActivityState.DeferredUnsupported, Reason = "csharp-code-writer-not-ready", CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (activityId, revisionId);
    }

    private static ValueTask<RetainedCsharpCodeCompletion> ProcessAsync(RetainedCsharpCodeClaim claim, byte[] bytes) =>
        new RetainedCsharpCodeProcessor(new MemoryReader(claim.SourceRevisionId, bytes), new LocalPrivateContentDisclosure()).ProcessAsync(claim, CancellationToken.None);

    private static RetainedCsharpCodeCompletion CreateValidConflictingSuccess(RetainedCsharpCodeCompletion completion)
    {
        var symbols = completion.Symbols.ToArray();
        var symbol = symbols[0] with { LocalName = symbols[0].LocalName + "Changed" };
        symbol = symbol with
        {
            SymbolFingerprint = RetainedCsharpCodeProcessor.ComputeSymbolFingerprint(
                completion.DocumentFingerprint!, symbol.Ordinal, symbol.DeclarationKindCode, symbol.LocalName,
                symbol.QualifiedName, symbol.RenderedSignature, symbol.Modifiers, symbol.LexicalParentOrdinal,
                symbol.SpanStartUtf16, symbol.SpanLengthUtf16)
        };
        symbols[0] = symbol;
        return completion with
        {
            Symbols = symbols,
            CompletionFingerprint = RetainedCsharpCodeProcessor.ComputeCompletionFingerprint(
                completion.DocumentFingerprint!, completion.ParserFingerprint,
                symbols.Select(value => value.SymbolFingerprint),
                completion.References.Select(value => value.ReferenceFingerprint),
                completion.Diagnostics.Select(value => value.DiagnosticFingerprint),
                completion.WithheldSymbolCount, completion.WithheldReferenceCount,
                completion.WithheldDiagnosticCount, completion.ReceiptDiagnosticCodes)
        };
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);

    private sealed class MemoryReader(SourceRevisionId revisionId, byte[] bytes) : IRetainedSourceReader
    {
        private readonly string _hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RetainedSourceBytes(revisionId, bytes, _hash, bytes.Length));
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
