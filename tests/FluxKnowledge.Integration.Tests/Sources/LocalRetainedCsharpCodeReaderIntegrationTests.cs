using System.Security.Cryptography;
using System.Xml.Linq;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

/// <summary>Generated-SQL coverage for trusted-local retained C# fact disclosure.</summary>
public sealed class LocalRetainedCsharpCodeReaderIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Read_returns_verified_retained_Csharp_facts_after_the_source_original_has_been_removed()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "namespace Local.Facts; public sealed class RetainedFact { public string Describe() => string.Empty; }");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-detail-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-original-{Guid.NewGuid():N}");
        var originalPath = Path.Combine(originalRoot, "RetainedFact.cs");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllTextAsync(originalPath, "this source original must never be reopened");
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, originalRoot, originalPath);
            File.Delete(originalPath);

            var factory = SqlTestData.CreateFactory(fixture);
            using var retained = new SqlRetainedSourceReader(factory, artifactRoot);
            var disclosure = new LocalPrivateContentDisclosure();
            var reader = new SqlLocalRetainedCsharpCodeReader(
                factory,
                new SqlLocalRetainedDetailReader(factory, retained, disclosure),
                disclosure,
                CreateEphemeralCursorCodec());

            var detail = await reader.ReadAsync(seeded.BranchId, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.False(File.Exists(originalPath));
            Assert.Equal(originalPath, detail!.LocalPath);
            Assert.Equal(hash, detail.ArtifactHash);
            Assert.Equal("success", detail.OutcomeCode);
            Assert.Contains(detail.Symbols, symbol => symbol.LocalName == "RetainedFact");
            Assert.Contains(detail.Symbols, symbol => symbol.RenderedSignature.Contains("Describe", StringComparison.Ordinal));
            Assert.NotEmpty(detail.References);
            Assert.Empty(detail.Diagnostics);

            var excerpt = await reader.ReadExcerptAsync(seeded.BranchId, CancellationToken.None);
            Assert.Equal(Encoding.UTF8.GetString(bytes), excerpt.Value);
            Assert.False(excerpt.Withheld);

            var search = await reader.SearchAsync("RetainedFact", 10, CancellationToken.None);
            var result = Assert.Single(search);
            Assert.Equal(seeded.BranchId, result.BranchId);
            Assert.Equal(originalPath, result.LocalPath);
            Assert.Equal(hash, result.ArtifactHash);

            var artifactPath = Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin");
            await File.WriteAllBytesAsync(artifactPath, bytes[..^1].Concat("X"u8.ToArray()).ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_returns_attempt_owned_syntax_invalid_diagnostics_without_a_document_or_source_original()
    {
        var bytes = Encoding.UTF8.GetBytes("public sealed class Broken {");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-blocked-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-blocked-original-{Guid.NewGuid():N}");
        var originalPath = Path.Combine(originalRoot, "Broken.cs");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllTextAsync(originalPath, "this source original must never be reopened");
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, originalRoot, originalPath);
            File.Delete(originalPath);

            using var reader = CreateReader(artifactRoot);
            var detail = await reader.Reader.ReadAsync(seeded.BranchId, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.False(File.Exists(originalPath));
            Assert.Equal("csharp-code-syntax-invalid", detail!.OutcomeCode);
            Assert.Empty(detail.Symbols);
            Assert.Empty(detail.References);
            Assert.NotEmpty(detail.Diagnostics);
            Assert.All(detail.Diagnostics, diagnostic => Assert.True(diagnostic.IsBlocked));
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_rejects_a_syntax_invalid_branch_with_an_extra_diagnostic_owned_by_another_attempt_before_general_disclosure()
    {
        var bytes = Encoding.UTF8.GetBytes("public sealed class MixedAttemptDiagnostics {");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-mixed-diagnostic-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceRoot, "MixedAttemptDiagnostics.cs");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, sourceRoot, sourcePath);
            var extraAttemptId = Guid.NewGuid();
            await using (var context = CreateContext())
            {
                var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
                var now = DateTimeOffset.UtcNow;
                context.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
                {
                    Id = extraAttemptId,
                    BranchId = seeded.BranchId,
                    LeaseGeneration = checked(branch.LeaseGeneration + 1),
                    StartedAtUtc = now,
                    FinishedAtUtc = now,
                    OutcomeCode = "foreign-attempt"
                });
                await context.SaveChangesAsync();

                await context.Database.ExecuteSqlRawAsync(
                    "DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_InsertFence] ON [dbo].[SourceProcessorCodeBlockedDiagnostics];");
                try
                {
                    context.SourceProcessorCodeBlockedDiagnostics.Add(new SourceProcessorCodeBlockedDiagnosticEntity
                    {
                        SourceProcessorBranchId = seeded.BranchId,
                        SourceProcessorAttemptId = extraAttemptId,
                        Ordinal = 255,
                        DiagnosticId = "CS9999",
                        Severity = 3,
                        SpanStartUtf16 = 0,
                        SpanLengthUtf16 = 1,
                        Representation = "scanned",
                        ScannedMessage = "foreign attempt diagnostic",
                        BlockedDiagnosticFingerprint = new string('f', 64)
                    });
                    await context.SaveChangesAsync();
                }
                finally
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_InsertFence] ON [dbo].[SourceProcessorCodeBlockedDiagnostics];");
                }
            }

            var detailReader = new RecordingRetainedDetailReader(new LocalRetainedDetailProjection(
                seeded.BranchId,
                Guid.Empty,
                new SourceRevisionId(seeded.RevisionId),
                sourcePath,
                hash,
                hash,
                bytes.Length,
                new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                [],
                []));
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                CreateEphemeralCursorCodec());

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
            Assert.Equal(0, detailReader.ReadCount);
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_withholds_a_secret_bearing_symbol_before_local_detail_or_search_serialisation()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "public sealed class RetainedSecretFact { public void Withheld(string value = \"secret-content-sentinel\") { } }");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-secret-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-secret-original-{Guid.NewGuid():N}");
        var originalPath = Path.Combine(originalRoot, "RetainedSecretFact.cs");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllTextAsync(originalPath, "this source original must never be reopened");
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, originalRoot, originalPath);
            File.Delete(originalPath);

            using var reader = CreateReader(artifactRoot);
            var detail = await reader.Reader.ReadAsync(seeded.BranchId, CancellationToken.None);
            var search = await reader.Reader.SearchAsync("RetainedSecretFact", 10, CancellationToken.None);

            Assert.NotNull(detail);
            Assert.False(File.Exists(originalPath));
            Assert.True(detail!.WithheldSymbolCount >= 1);
            Assert.DoesNotContain(detail.Symbols, symbol => symbol.LocalName == "Withheld");
            Assert.DoesNotContain("secret-content-sentinel", JsonSerializer.Serialize(detail), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-content-sentinel", JsonSerializer.Serialize(search), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Excerpt_refuses_a_retained_branch_without_an_exact_Csharp_receipt_after_the_source_original_is_removed()
    {
        var bytes = Encoding.UTF8.GetBytes("public sealed class UnrelatedRetainedBranch { }");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-excerpt-denial-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-excerpt-denial-original-{Guid.NewGuid():N}");
        var originalPath = Path.Combine(originalRoot, "UnrelatedRetainedBranch.cs");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllTextAsync(originalPath, "this source original must never be reopened");
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var branchId = await SeedRetainedBranchWithoutCsharpReceiptAsync(bytes, hash, originalRoot, originalPath);
            File.Delete(originalPath);

            using var reader = CreateReader(artifactRoot);

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                reader.Reader.ReadExcerptAsync(branchId, CancellationToken.None).AsTask());
            Assert.False(File.Exists(originalPath));
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_pages_all_persisted_fact_kinds_and_search_projects_the_matching_tail_symbol_after_retained_validation()
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(0, 300).Select(index =>
            $"public void Method{index:D3}() => global::System.Console.WriteLine(\"reference-{index:D3}\");"));
        var bytes = Encoding.UTF8.GetBytes($"public sealed class PagedFacts {{ {methods} public void TailSymbol() => global::System.Console.WriteLine(\"tail-reference\"); }}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-page-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-page-original-{Guid.NewGuid():N}");
        var originalPath = Path.Combine(originalRoot, "PagedFacts.cs");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllTextAsync(originalPath, "this source original must never be reopened");
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, originalRoot, originalPath);
            File.Delete(originalPath);

            using var reader = CreateReader(artifactRoot);
            var first = await reader.Reader.ReadAsync(seeded.BranchId, CancellationToken.None);

            Assert.NotNull(first);
            Assert.True(first!.PersistedSymbolCount > 256);
            Assert.True(first.PersistedReferenceCount > 256);
            Assert.NotNull(first.NextSymbolOrdinal);
            Assert.NotNull(first.NextReferenceOrdinal);
            Assert.DoesNotContain(first.Symbols, value => value.LocalName == "TailSymbol");

            var continuation = await reader.Reader.ReadPageAsync(
                seeded.BranchId,
                new LocalRetainedCsharpCodePageRequest(
                    first.NextSymbolOrdinal,
                    first.NextReferenceOrdinal,
                    first.NextDiagnosticOrdinal),
                CancellationToken.None);

            Assert.NotNull(continuation);
            Assert.Contains(continuation!.Symbols, value => value.LocalName == "TailSymbol");
            Assert.NotEmpty(continuation.References);
            Assert.False(File.Exists(originalPath));

            var search = await reader.Reader.SearchAsync("TailSymbol", 10, CancellationToken.None);
            var result = Assert.Single(search);
            Assert.Equal(seeded.BranchId, result.BranchId);
            Assert.Contains(result.Symbols, value => value.LocalName == "TailSymbol");
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Search_pages_the_actual_matching_reference_rows_without_silently_losing_the_tail()
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(0, 300).Select(index =>
            $"public void Method{index:D3}() => global::System.Console.WriteLine({index});"));
        var bytes = Encoding.UTF8.GetBytes($"public sealed class ReferenceOnlySearch {{ {methods} }}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-reference-search-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-reference-search-original-{Guid.NewGuid():N}");
        var originalPath = Path.Combine(originalRoot, "ReferenceOnlySearch.cs");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllTextAsync(originalPath, "this source original must never be reopened");
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, originalRoot, originalPath);
            File.Delete(originalPath);

            using var reader = CreateReader(artifactRoot);
            var first = await reader.Reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest("System.Console", 256, null),
                CancellationToken.None);

            Assert.Equal(32, first.Results.Sum(value => value.References.Count));
            Assert.All(first.Results, value => Assert.Empty(value.Symbols));
            Assert.NotNull(first.NextCursor);

            var total = first.Results.Sum(value => value.References.Count);
            var cursor = first.NextCursor;
            while (cursor is not null)
            {
                var continuation = await reader.Reader.SearchPageAsync(
                    new LocalRetainedCsharpCodeSearchPageRequest("System.Console", 256, cursor),
                    CancellationToken.None);
                Assert.Equal(seeded.BranchId, Assert.Single(continuation.Results).BranchId);
                Assert.InRange(continuation.Results.Sum(value => value.References.Count), 1, 32);
                total += continuation.Results.Sum(value => value.References.Count);
                cursor = continuation.NextCursor;
            }

            Assert.Equal(300, total);
            Assert.False(File.Exists(originalPath));
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(originalRoot)) Directory.Delete(originalRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_rejects_receipt_parser_handler_and_document_mismatches_before_general_retained_disclosure()
    {
        var bytes = Encoding.UTF8.GetBytes("public sealed class ExactReceiptFence { public void Run() { } }");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-exact-fence-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceRoot, "ExactReceiptFence.cs");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, sourceRoot, sourcePath);
            Guid activityId;
            await using (var context = CreateContext())
            {
                activityId = (await context.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId)).SourceActivityId;
            }

            var detailReader = new RecordingRetainedDetailReader(new LocalRetainedDetailProjection(
                seeded.BranchId,
                activityId,
                new SourceRevisionId(seeded.RevisionId),
                sourcePath,
                hash,
                hash,
                bytes.Length,
                new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                [],
                []));
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                CreateEphemeralCursorCodec());

            await SetReceiptParserAsync(new string('1', 64), restoreConstraint: false);
            await AssertFencedAsync();
            await SetReceiptParserAsync(RetainedCsharpCodeProcessor.ParserFingerprint, restoreConstraint: true);

            await SetReceiptHandlerAsync("foreign-handler", restoreConstraint: false);
            await AssertFencedAsync();
            await SetReceiptHandlerAsync(RetainedCsharpCodeProcessor.HandlerImplementationId, restoreConstraint: true);

            await SetReceiptActivityKindAsync((int)SourceActivityKind.DocumentParsing, restoreConstraint: false);
            await AssertFencedAsync();
            await SetReceiptActivityKindAsync((int)SourceActivityKind.CodeParsing, restoreConstraint: true);

            await using (var context = CreateContext())
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] NOCHECK CONSTRAINT ALL;
                    DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeDocuments_Immutable] ON [dbo].[SourceProcessorCodeDocuments];
                    UPDATE [dbo].[SourceProcessorCodeDocuments]
                    SET [ParserFingerprint] = {new string('2', 64)}
                    WHERE [SourceProcessorBranchId] = {seeded.BranchId};
                    ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeDocuments_Immutable] ON [dbo].[SourceProcessorCodeDocuments];
                    """);
            }
            await AssertFencedAsync();
            await using (var context = CreateContext())
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] NOCHECK CONSTRAINT ALL;
                    DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeDocuments_Immutable] ON [dbo].[SourceProcessorCodeDocuments];
                    UPDATE [dbo].[SourceProcessorCodeDocuments]
                    SET [ParserFingerprint] = {RetainedCsharpCodeProcessor.ParserFingerprint}
                    WHERE [SourceProcessorBranchId] = {seeded.BranchId};
                    ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeDocuments_Immutable] ON [dbo].[SourceProcessorCodeDocuments];
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] WITH CHECK CHECK CONSTRAINT ALL;
                    """);
            }

            async Task SetReceiptParserAsync(string value, bool restoreConstraint)
            {
                await using var context = CreateContext();
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] NOCHECK CONSTRAINT ALL;
                    DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable] ON [dbo].[SourceProcessorCodeCompletionReceipts];
                    UPDATE [dbo].[SourceProcessorCodeCompletionReceipts]
                    SET [ParserFingerprint] = {value}
                    WHERE [SourceProcessorBranchId] = {seeded.BranchId};
                    ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable] ON [dbo].[SourceProcessorCodeCompletionReceipts];
                    """);
                if (restoreConstraint)
                {
                    await RestoreReceiptDocumentConstraintAsync(context);
                }
            }

            async Task SetReceiptHandlerAsync(string value, bool restoreConstraint)
            {
                await using var context = CreateContext();
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] NOCHECK CONSTRAINT ALL;
                    DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable] ON [dbo].[SourceProcessorCodeCompletionReceipts];
                    UPDATE [dbo].[SourceProcessorCodeCompletionReceipts]
                    SET [HandlerImplementationId] = {value}
                    WHERE [SourceProcessorBranchId] = {seeded.BranchId};
                    ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable] ON [dbo].[SourceProcessorCodeCompletionReceipts];
                    """);
                if (restoreConstraint)
                {
                    await RestoreReceiptDocumentConstraintAsync(context);
                }
            }

            async Task SetReceiptActivityKindAsync(int value, bool restoreConstraint)
            {
                await using var context = CreateContext();
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] NOCHECK CONSTRAINT ALL;
                    DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable] ON [dbo].[SourceProcessorCodeCompletionReceipts];
                    UPDATE [dbo].[SourceProcessorCodeCompletionReceipts]
                    SET [ActivityKind] = {value}
                    WHERE [SourceProcessorBranchId] = {seeded.BranchId};
                    ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable] ON [dbo].[SourceProcessorCodeCompletionReceipts];
                    """);
                if (restoreConstraint)
                {
                    await RestoreReceiptDocumentConstraintAsync(context);
                }
            }

            static Task RestoreReceiptDocumentConstraintAsync(FluxKnowledgeDbContext context) =>
                context.Database.ExecuteSqlRawAsync("""
                    ALTER TABLE [dbo].[SourceProcessorCodeCompletionReceipts] WITH CHECK CHECK CONSTRAINT ALL;
                    """);

            async Task AssertFencedAsync()
            {
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
                Assert.Equal(0, detailReader.ReadCount);
            }
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Search_cursor_uses_authenticated_shared_key_ring_and_rejects_tamper_old_SHA_wrong_key_and_cross_query_before_retained_reads()
    {
        var marker = $"CursorTarget{Guid.NewGuid():N}";
        var methods = string.Join(Environment.NewLine, Enumerable.Range(0, 40).Select(index =>
            $"public void Method{index:D3}() => global::{marker}.WriteLine({index});"));
        var bytes = Encoding.UTF8.GetBytes($"public sealed class BoundSearchCursor {{ {methods} }}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-bound-cursor-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-bound-cursor-source-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceRoot, "BoundSearchCursor.cs");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.Combine(artifactRoot, "sha256", hash[..2]));
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, "sha256", hash[..2], $"{hash}.bin"), bytes);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, sourceRoot, sourcePath);
            var retainedDetail = new LocalRetainedDetailProjection(
                seeded.BranchId,
                Guid.Empty,
                new SourceRevisionId(seeded.RevisionId),
                sourcePath,
                hash,
                hash,
                bytes.Length,
                new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                [],
                []);
            var detailReader = new RecordingManyRetainedDetailReader(
                new Dictionary<Guid, LocalRetainedDetailProjection> { [seeded.BranchId] = retainedDetail });
            var keyRingRoot = Path.Combine(artifactRoot, "cursor-keys");
            var firstCodec = CreateCursorCodec(keyRingRoot);
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                firstCodec);
            var first = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(marker, 256, null),
                CancellationToken.None);
            var cursor = Assert.IsType<LocalRetainedCsharpCodeSearchCursor>(first.NextCursor);

            var tamperedToken = cursor.Token[..^1] + (cursor.Token[^1] == 'A' ? 'B' : 'A');
            await AssertCursorRejectedWithoutRetainedReadAsync(
                reader,
                detailReader,
                marker,
                new LocalRetainedCsharpCodeSearchCursor(tamperedToken));

            var nonCanonicalAlias = CreateNonCanonicalBase64UrlAlias(cursor.Token);
            Assert.NotEqual(cursor.Token, nonCanonicalAlias);
            Assert.Equal(DecodeBase64Url(cursor.Token), DecodeBase64Url(nonCanonicalAlias));
            await AssertCursorRejectedWithoutRetainedReadAsync(
                reader,
                detailReader,
                marker,
                new LocalRetainedCsharpCodeSearchCursor(nonCanonicalAlias));

            var oldPayload = Encoding.UTF8.GetBytes("{\"version\":1,\"queryFingerprint\":\"recomputed-old-sha-envelope\"}");
            var recomputedOldSha = new LocalRetainedCsharpCodeSearchCursor(
                $"{EncodeBase64Url(oldPayload)}.{EncodeBase64Url(SHA256.HashData(oldPayload))}");
            await AssertCursorRejectedWithoutRetainedReadAsync(
                reader,
                detailReader,
                marker,
                recomputedOldSha);

            await AssertCursorRejectedWithoutRetainedReadAsync(
                reader,
                detailReader,
                "Method",
                cursor);

            var wrongKeyCodec = CreateCursorCodec(Path.Combine(artifactRoot, "wrong-cursor-keys"));
            var wrongKeyReader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                wrongKeyCodec);
            await AssertCursorRejectedWithoutRetainedReadAsync(
                wrongKeyReader,
                detailReader,
                marker,
                cursor);

            var secondCodec = CreateCursorCodec(keyRingRoot);
            var sharedKeyReader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                secondCodec);
            detailReader.ResetCount();
            var continuation = await sharedKeyReader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(marker, 256, cursor),
                CancellationToken.None);
            Assert.Equal(8, continuation.Results.Sum(value => value.References.Count + value.Symbols.Count));
            Assert.Equal(1, detailReader.ReadCount);

            var cursorReferenceOrdinal = first.Results.SelectMany(value => value.References).Max(value => value.Ordinal);
            await using (var context = CreateContext())
            {
                await context.Database.ExecuteSqlRawAsync(
                    "DISABLE TRIGGER [dbo].[TR_SourceProcessorCodeReferences_Immutable] ON [dbo].[SourceProcessorCodeReferences];");
                try
                {
                    await context.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE [dbo].[SourceProcessorCodeReferences]
                        SET [TargetDisplay] = N'stale-cursor-fact'
                        WHERE [DocumentId] = {seeded.BranchId} AND [Ordinal] = {cursorReferenceOrdinal};
                        """);
                }
                finally
                {
                    await context.Database.ExecuteSqlRawAsync(
                        "ENABLE TRIGGER [dbo].[TR_SourceProcessorCodeReferences_Immutable] ON [dbo].[SourceProcessorCodeReferences];");
                }
            }
            await AssertCursorRejectedWithoutRetainedReadAsync(reader, detailReader, marker, cursor);

            static string EncodeBase64Url(byte[] value) =>
                Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            static byte[] DecodeBase64Url(string value)
            {
                var padded = value.Replace('-', '+').Replace('_', '/');
                padded += (padded.Length % 4) switch
                {
                    0 => string.Empty,
                    2 => "==",
                    3 => "=",
                    _ => throw new InvalidOperationException("The issued cursor token cannot have an invalid base64url length.")
                };
                return Convert.FromBase64String(padded);
            }

            static string CreateNonCanonicalBase64UrlAlias(string canonicalToken)
            {
                const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
                var ignoredPaddingBitMask = (canonicalToken.Length % 4) switch
                {
                    2 => 0b000011,
                    3 => 0b001111,
                    _ => throw new InvalidOperationException("The issued cursor token has no base64url padding bits to alias.")
                };
                var finalValue = alphabet.IndexOf(canonicalToken[^1]);
                Assert.InRange(finalValue, 0, alphabet.Length - 1);
                var aliasValue = (finalValue & ~ignoredPaddingBitMask) |
                                 ((finalValue + 1) & ignoredPaddingBitMask);
                return canonicalToken[..^1] + alphabet[aliasValue];
            }
        }
        finally
        {
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    private static LocalRetainedCsharpCodeSearchCursorCodec CreateEphemeralCursorCodec() =>
        new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Shared_key_ring_round_trips_between_app_pool_writer_and_cli_reader()
    {
        var keyRingRoot = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeSharedKeyRing_{Guid.NewGuid():N}");
        try
        {
            var writer = PrivatePcDataProtectionProviderFactory.CreateProviderForIsolatedTests(
                keyRingRoot,
                createIfMissing: true);
            var protectedValue = writer.CreateProtector("native-go-live-cross-token").Protect("cursor-token");

            var reader = PrivatePcDataProtectionProviderFactory.CreateProviderForIsolatedTests(
                keyRingRoot,
                createIfMissing: false);

            Assert.Equal("cursor-token", reader.CreateProtector("native-go-live-cross-token").Unprotect(protectedValue));
            var keyFile = Assert.Single(Directory.EnumerateFiles(keyRingRoot, "key-*.xml"));
            Assert.DoesNotContain("encryptedSecret", File.ReadAllText(keyFile), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(keyRingRoot)) Directory.Delete(keyRingRoot, recursive: true);
        }
    }

    [Fact]
    public void Cli_reader_does_not_generate_a_replacement_key_for_an_expired_ring()
    {
        var keyRingRoot = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeReadOnlyKeyRing_{Guid.NewGuid():N}");
        try
        {
            var writer = PrivatePcDataProtectionProviderFactory.CreateProviderForIsolatedTests(
                keyRingRoot,
                createIfMissing: true);
            _ = writer.CreateProtector("native-go-live-read-only").Protect("seed");
            var keyFile = Assert.Single(Directory.EnumerateFiles(keyRingRoot, "key-*.xml"));
            var key = XDocument.Load(keyFile);
            foreach (var element in key.Descendants().Where(element =>
                         element.Name.LocalName is "creationDate" or "activationDate" or "expirationDate"))
            {
                element.Value = DateTimeOffset.UtcNow.AddDays(-180).ToString("O");
            }
            key.Save(keyFile);

            var reader = PrivatePcDataProtectionProviderFactory.CreateProviderForIsolatedTests(
                keyRingRoot,
                createIfMissing: false);
            Assert.Throws<CryptographicException>(() =>
                reader.CreateProtector("native-go-live-read-only").Protect("reader-must-not-rotate"));

            Assert.Single(Directory.EnumerateFiles(keyRingRoot, "key-*.xml"));
        }
        finally
        {
            if (Directory.Exists(keyRingRoot)) Directory.Delete(keyRingRoot, recursive: true);
        }
    }

    private static LocalRetainedCsharpCodeSearchCursorCodec CreateCursorCodec(string keyRingRoot) =>
        PrivatePcDataProtectionProviderFactory.CreateCursorCodecForIsolatedTests(keyRingRoot);

    private static async Task AssertCursorRejectedWithoutRetainedReadAsync(
        SqlLocalRetainedCsharpCodeReader reader,
        RecordingManyRetainedDetailReader detailReader,
        string query,
        LocalRetainedCsharpCodeSearchCursor cursor)
    {
        detailReader.ResetCount();
        var failure = await Assert.ThrowsAsync<LocalRetainedCsharpCodeSearchCursorException>(() =>
            reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(query, 10, cursor),
                CancellationToken.None).AsTask());
        Assert.Equal("The retained C# search continuation is invalid.", failure.Message);
        Assert.Equal(0, detailReader.ReadCount);
    }

    [NativeSqlServerFact]
    public async Task Search_fails_closed_with_a_fixed_safe_error_when_the_cursor_key_provider_is_inaccessible()
    {
        const string secretSentinel = "key-provider-secret-sentinel";
        var detailReader = new RecordingManyRetainedDetailReader(
            new Dictionary<Guid, LocalRetainedDetailProjection>());
        var codec = new LocalRetainedCsharpCodeSearchCursorCodec(
            new InaccessibleDataProtectionProvider(secretSentinel));
        var reader = new SqlLocalRetainedCsharpCodeReader(
            SqlTestData.CreateFactory(fixture),
            detailReader,
            new LocalPrivateContentDisclosure(),
            codec);

        var failure = await Assert.ThrowsAsync<LocalRetainedCsharpCodeSearchCursorException>(() =>
            reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(
                    "cursor",
                    10,
                    new LocalRetainedCsharpCodeSearchCursor("opaque-token")),
                CancellationToken.None).AsTask());

        Assert.Equal("The retained C# search continuation is invalid.", failure.Message);
        Assert.DoesNotContain(secretSentinel, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, detailReader.ReadCount);
    }

    [NativeSqlServerFact]
    public async Task Private_PC_key_ring_factory_fails_closed_without_echoing_an_inaccessible_root()
    {
        var secretSentinel = $"key-root-secret-{Guid.NewGuid():N}";
        var blockingPath = Path.Combine(Path.GetTempPath(), secretSentinel);
        await File.WriteAllTextAsync(blockingPath, "not a directory");
        try
        {
            var detailReader = new RecordingManyRetainedDetailReader(
                new Dictionary<Guid, LocalRetainedDetailProjection>());
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                PrivatePcDataProtectionProviderFactory.CreateCursorCodecForIsolatedTests(blockingPath));

            var failure = await Assert.ThrowsAsync<LocalRetainedCsharpCodeSearchCursorException>(() =>
                reader.SearchPageAsync(
                    new LocalRetainedCsharpCodeSearchPageRequest(
                        "cursor",
                        10,
                        new LocalRetainedCsharpCodeSearchCursor("opaque-token")),
                    CancellationToken.None).AsTask());

            Assert.Equal("The retained C# search continuation is invalid.", failure.Message);
            Assert.DoesNotContain(secretSentinel, failure.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, detailReader.ReadCount);
        }
        finally
        {
            File.Delete(blockingPath);
        }
    }

    [NativeSqlServerFact]
    public async Task Search_uses_the_same_NFC_query_for_SQL_and_the_authenticated_cursor()
    {
        const string composed = "Caf\u00e9";
        const string decomposed = "Cafe\u0301";
        var methods = string.Join(Environment.NewLine, Enumerable.Range(0, 40).Select(index =>
            $"public void {composed}{index:D2}() {{ }}"));
        var bytes = Encoding.UTF8.GetBytes($"public sealed class CanonicalQuery {{ {methods} }}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-canonical-query-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceRoot, "CanonicalQuery.cs");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, sourceRoot, sourcePath);
            var detailReader = new RecordingManyRetainedDetailReader(
                new Dictionary<Guid, LocalRetainedDetailProjection>
                {
                    [seeded.BranchId] = new LocalRetainedDetailProjection(
                        seeded.BranchId,
                        Guid.Empty,
                        new SourceRevisionId(seeded.RevisionId),
                        sourcePath,
                        hash,
                        hash,
                        bytes.Length,
                        new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                        [],
                        [])
                });
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                CreateEphemeralCursorCodec());

            var first = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(decomposed, 32, null),
                CancellationToken.None);
            Assert.Equal(32, first.Results.Sum(value => value.Symbols.Count + value.References.Count));
            Assert.NotNull(first.NextCursor);

            var continuation = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(composed, 32, first.NextCursor),
                CancellationToken.None);
            Assert.Equal(8, continuation.Results.Sum(value => value.Symbols.Count + value.References.Count));
            Assert.Null(continuation.NextCursor);
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Search_caps_each_requested_256_fact_page_to_32_retained_validations_and_continues_without_loss()
    {
        var marker = $"BoundedValidationTarget{Guid.NewGuid():N}";
        var bytes = Encoding.UTF8.GetBytes(
            $"public sealed class SearchBound {{ public void Run() => global::{marker}.Invoke(); }}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-search-bound-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var details = new Dictionary<Guid, LocalRetainedDetailProjection>();
            for (var index = 0; index < 40; index++)
            {
                var branchRoot = Path.Combine(sourceRoot, index.ToString("D2"));
                var sourcePath = Path.Combine(branchRoot, $"SearchBound{index:D2}.cs");
                Directory.CreateDirectory(branchRoot);
                var seeded = await SeedAndCompleteAsync(bytes, hash, branchRoot, sourcePath);
                details.Add(seeded.BranchId, new LocalRetainedDetailProjection(
                    seeded.BranchId,
                    Guid.Empty,
                    new SourceRevisionId(seeded.RevisionId),
                    sourcePath,
                    hash,
                    hash,
                    bytes.Length,
                    new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                    [],
                    []));
            }

            var detailReader = new RecordingManyRetainedDetailReader(details);
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                CreateEphemeralCursorCodec());

            var first = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(marker, 256, null),
                CancellationToken.None);

            Assert.Equal(32, first.Results.Sum(value => value.References.Count + value.Symbols.Count));
            Assert.Equal(32, detailReader.ReadCount);
            Assert.NotNull(first.NextCursor);

            detailReader.ResetCount();
            var continuation = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(marker, 256, first.NextCursor),
                CancellationToken.None);

            Assert.Equal(8, continuation.Results.Sum(value => value.References.Count + value.Symbols.Count));
            Assert.Equal(8, detailReader.ReadCount);
            Assert.Null(continuation.NextCursor);
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Search_returns_a_usable_continuation_without_empty_groups_when_the_first_32_candidates_are_withheld_or_unavailable()
    {
        var marker = $"WithheldCursorTarget{Guid.NewGuid():N}";
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-withheld-page-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var details = new Dictionary<Guid, LocalRetainedDetailProjection>();
            for (var index = 0; index < 40; index++)
            {
                var target = $"{marker}{index:D2}";
                var bytes = Encoding.UTF8.GetBytes(
                    $"public sealed class WithheldPage{index:D2} {{ public void Run() => global::{target}.Invoke(); }}");
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var branchRoot = Path.Combine(sourceRoot, index.ToString("D2"));
                var sourcePath = Path.Combine(branchRoot, $"WithheldPage{index:D2}.cs");
                Directory.CreateDirectory(branchRoot);
                var seeded = await SeedAndCompleteAsync(bytes, hash, branchRoot, sourcePath);
                details.Add(seeded.BranchId, new LocalRetainedDetailProjection(
                    seeded.BranchId,
                    Guid.Empty,
                    new SourceRevisionId(seeded.RevisionId),
                    sourcePath,
                    hash,
                    hash,
                    bytes.Length,
                    new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                    [],
                    []));
            }

            (Guid BranchId, string TargetDisplay)[] firstPageFacts;
            await using (var context = CreateContext())
            {
                firstPageFacts = await context.SourceProcessorCodeReferences.AsNoTracking()
                    .Where(value => value.TargetDisplay.Contains(marker))
                    .OrderBy(value => value.DocumentId)
                    .ThenBy(value => value.Ordinal)
                    .Take(32)
                    .Select(value => new ValueTuple<Guid, string>(value.DocumentId, value.TargetDisplay))
                    .ToArrayAsync();
            }
            Assert.Equal(32, firstPageFacts.Length);
            foreach (var unavailableBranchId in firstPageFacts.Take(16).Select(value => value.BranchId))
            {
                Assert.True(details.Remove(unavailableBranchId));
            }

            var detailReader = new RecordingManyRetainedDetailReader(details);
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new SelectiveWithholdingDisclosure(firstPageFacts.Skip(16).Select(value => value.TargetDisplay)),
                CreateEphemeralCursorCodec());

            var first = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(marker, 10, null),
                CancellationToken.None);

            Assert.Empty(first.Results);
            Assert.Equal(32, detailReader.ReadCount);
            Assert.NotNull(first.NextCursor);

            detailReader.ResetCount();
            var continuation = await reader.SearchPageAsync(
                new LocalRetainedCsharpCodeSearchPageRequest(marker, 10, first.NextCursor),
                CancellationToken.None);

            Assert.Equal(8, continuation.Results.Sum(value => value.References.Count + value.Symbols.Count));
            Assert.Equal(8, detailReader.ReadCount);
            Assert.Null(continuation.NextCursor);
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Read_rejects_no_receipt_mixed_or_stale_Csharp_terminal_outcomes_before_invoking_the_general_retained_detail_reader()
    {
        var bytes = Encoding.UTF8.GetBytes("public sealed class DurableFence { public void Run() { } }");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"flux-retained-csharp-fence-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceRoot, "DurableFence.cs");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var seeded = await SeedAndCompleteAsync(bytes, hash, sourceRoot, sourcePath);
            Guid activityId;
            await using (var context = CreateContext())
            {
                var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
                activityId = branch.SourceActivityId;
                branch.State = (int)RetainedProcessorBranchState.Pending;
                await context.SaveChangesAsync();
            }

            var detailReader = new RecordingRetainedDetailReader(new LocalRetainedDetailProjection(
                seeded.BranchId,
                activityId,
                new SourceRevisionId(seeded.RevisionId),
                sourcePath,
                hash,
                hash,
                bytes.Length,
                new LocalRetainedContentHandle(seeded.BranchId, new SourceRevisionId(seeded.RevisionId)),
                [],
                []));
            var reader = new SqlLocalRetainedCsharpCodeReader(
                SqlTestData.CreateFactory(fixture),
                detailReader,
                new LocalPrivateContentDisclosure(),
                CreateEphemeralCursorCodec());

            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
            Assert.Equal(0, detailReader.ReadCount);

            await using (var context = CreateContext())
            {
                var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
                var activity = await context.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId);
                branch.State = (int)RetainedProcessorBranchState.Completed;
                activity.State = (int)SourceActivityState.FailedTerminal;
                activity.Reason = "csharp-code-syntax-invalid";
                await context.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
            Assert.Equal(0, detailReader.ReadCount);

            await using (var context = CreateContext())
            {
                var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
                var activity = await context.SourceActivities.SingleAsync(value => value.Id == branch.SourceActivityId);
                var receipt = await context.SourceProcessorCodeCompletionReceipts.SingleAsync(value => value.SourceProcessorBranchId == seeded.BranchId);
                var attempt = await context.SourceProcessorAttempts.SingleAsync(value => value.Id == receipt.SourceProcessorAttemptId);
                activity.State = (int)SourceActivityState.Completed;
                activity.Reason = null;
                attempt.OutcomeCode = "lease-expired-reconciled";
                await context.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
            Assert.Equal(0, detailReader.ReadCount);

            await using (var context = CreateContext())
            {
                var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == seeded.BranchId);
                var attempt = await context.SourceProcessorAttempts.SingleAsync(value => value.BranchId == seeded.BranchId);
                branch.CompletionReceiptFingerprint = new string('0', 64);
                attempt.OutcomeCode = "success";
                await context.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(seeded.BranchId, CancellationToken.None).AsTask());
            Assert.Equal(0, detailReader.ReadCount);

            var noReceiptRoot = Path.Combine(sourceRoot, "no-receipt");
            var noReceiptBranch = await SeedRetainedBranchWithoutCsharpReceiptAsync(
                bytes,
                hash,
                noReceiptRoot,
                Path.Combine(noReceiptRoot, "NoReceipt.cs"));
            Assert.Null(await reader.ReadAsync(noReceiptBranch, CancellationToken.None));
            Assert.Equal(0, detailReader.ReadCount);
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    private async Task<(Guid BranchId, Guid RevisionId)> SeedAndCompleteAsync(
        byte[] bytes,
        string hash,
        string sourceRoot,
        string originalPath)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var context = CreateContext())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = sourceRoot,
                DisplayName = "Retained C# local detail",
                State = 0,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                FollowLinks = false,
                MaximumFileBytes = 64L * 1024 * 1024,
                AllowedClassificationsJson = "[\"text/plain\"]",
                CrawlMode = 0,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            context.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = revisionId,
                SourceRootId = rootId,
                StableSourceIdentity = $"retained-csharp-detail:{revisionId:N}",
                Revision = 1,
                ContentSha256 = hash,
                CanonicalPath = originalPath,
                Classification = "AcceptedUtf8Text",
                Extension = ".cs",
                ByteLength = bytes.Length,
                DiscoveredAtUtc = now,
                DiscoveryEvidenceJson = "{}"
            });
            context.SourceArtifacts.Add(new SourceArtifactEntity
            {
                Id = Guid.NewGuid(),
                SourceRevisionId = revisionId,
                ContentSha256 = hash,
                StoreRelativePath = $"sha256\\{hash[..2]}\\{hash}.bin",
                ByteLength = bytes.Length,
                ChecksumVerifiedAtUtc = now,
                ReferenceCount = 1
            });
            context.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activityId,
                SourceRevisionId = revisionId,
                ActivityKind = (int)SourceActivityKind.CodeParsing,
                ExecutionClass = (int)ExecutionClass.InProcess,
                ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion,
                InputFingerprint = hash,
                DescriptorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                State = (int)SourceActivityState.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = branchId,
                SourceActivityId = activityId,
                SourceRevisionId = revisionId,
                InputSha256 = hash,
                ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion,
                ProcessorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
                State = (int)RetainedProcessorBranchState.Pending,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await context.SaveChangesAsync();
        }

        var factory = SqlTestData.CreateFactory(fixture);
        var store = new SqlRetainedProcessorBranchStore(factory, TimeProvider.System);
        var claim = Assert.Single(await store.ClaimCsharpCodeAsync(
            "retained-csharp-local-detail",
            1,
            RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint,
            CancellationToken.None));
        var completion = await new RetainedCsharpCodeProcessor(
            new MemoryReader(new SourceRevisionId(revisionId), bytes),
            new LocalPrivateContentDisclosure()).ProcessAsync(claim, CancellationToken.None);
        Assert.True((await store.CompleteRetainedCsharpCodeAsync(claim, completion, CancellationToken.None)).IsCommitted);
        return (branchId, revisionId);
    }

    private async Task<Guid> SeedRetainedBranchWithoutCsharpReceiptAsync(
        byte[] bytes,
        string hash,
        string sourceRoot,
        string originalPath)
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = rootId,
            CanonicalPath = sourceRoot,
            DisplayName = "Unrelated retained local detail",
            State = 0,
            Recursive = true,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[\"application/octet-stream\"]",
            CrawlMode = 0,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId,
            SourceRootId = rootId,
            StableSourceIdentity = $"unrelated-retained:{revisionId:N}",
            Revision = 1,
            ContentSha256 = hash,
            CanonicalPath = originalPath,
            Classification = "AcceptedUtf8Text",
            Extension = ".txt",
            ByteLength = bytes.Length,
            DiscoveredAtUtc = now,
            DiscoveryEvidenceJson = "{}"
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(),
            SourceRevisionId = revisionId,
            ContentSha256 = hash,
            StoreRelativePath = $"sha256\\{hash[..2]}\\{hash}.bin",
            ByteLength = bytes.Length,
            ChecksumVerifiedAtUtc = now,
            ReferenceCount = 1
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId,
            SourceRevisionId = revisionId,
            ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability,
            ProcessorVersion = "phase-5-retained-ooxml-v1",
            InputFingerprint = hash,
            State = (int)SourceActivityState.DeferredUnsupported,
            Reason = "ooxml-parser-unavailable",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId,
            SourceActivityId = activityId,
            SourceRevisionId = revisionId,
            InputSha256 = hash,
            ProcessorVersion = "phase-5-retained-ooxml-v1",
            ProcessorFingerprint = new string('d', 64),
            State = (int)RetainedProcessorBranchState.Blocked,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return branchId;
    }

    private FluxKnowledgeDbContext CreateContext() => new(
        new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);

    private ReaderLease CreateReader(string artifactRoot)
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var retainedSourceReader = new SqlRetainedSourceReader(factory, artifactRoot);
        var disclosure = new LocalPrivateContentDisclosure();
        return new ReaderLease(new SqlLocalRetainedCsharpCodeReader(
            factory,
            new SqlLocalRetainedDetailReader(factory, retainedSourceReader, disclosure),
            disclosure,
            CreateEphemeralCursorCodec()), retainedSourceReader);
    }

    private sealed class ReaderLease(SqlLocalRetainedCsharpCodeReader reader, SqlRetainedSourceReader retainedSourceReader) : IDisposable
    {
        public SqlLocalRetainedCsharpCodeReader Reader { get; } = reader;

        public void Dispose() => retainedSourceReader.Dispose();
    }

    private sealed class MemoryReader(SourceRevisionId revisionId, byte[] bytes) : IRetainedSourceReader
    {
        private readonly string _hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RetainedSourceBytes(revisionId, bytes, _hash, bytes.Length));

        public ValueTask<Utf8FileSource> ReadUtf8Async(
            SourceRevisionId sourceRevisionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRetainedDetailReader(LocalRetainedDetailProjection detail) : ILocalRetainedDetailReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult<LocalRetainedDetailProjection?>(branchId == detail.BranchId ? detail : null);
        }

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalDisclosureResult(null, true, "secret-content-withheld"));
    }

    private sealed class RecordingManyRetainedDetailReader(
        IReadOnlyDictionary<Guid, LocalRetainedDetailProjection> details) : ILocalRetainedDetailReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<LocalRetainedDetailProjection?> ReadAsync(Guid branchId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return ValueTask.FromResult(details.TryGetValue(branchId, out var detail) ? detail : null);
        }

        public ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void ResetCount() => ReadCount = 0;
    }

    private sealed class SelectiveWithholdingDisclosure(IEnumerable<string> withheldValues)
        : ILocalPrivateContentDisclosure
    {
        private readonly HashSet<string> _withheldValues = new(withheldValues, StringComparer.Ordinal);

        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) =>
            kind == LocalDisclosureKind.Reference && _withheldValues.Contains(value)
                ? new LocalDisclosureResult(null, true, "secret-content-withheld")
                : new LocalDisclosureResult(value, false, null);
    }

    private sealed class InaccessibleDataProtectionProvider(string secretSentinel) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) =>
            throw new UnauthorizedAccessException(secretSentinel);
    }
}
