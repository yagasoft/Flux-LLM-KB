using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Documents;
using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed class ScopedCorpusRetrievalTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Root_scope_finds_a_late_plain_text_chunk_without_leaking_another_root()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootA = Guid.NewGuid();
        var rootB = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootA, @"C:\corpus-a", "guide.txt",
                ["introductory text", "INV-2026/0042 marks the retained answer"]);
            AddPublishedText(context, rootB, @"C:\corpus-b", "other.txt",
                ["INV-2026/0042 belongs to another source"]);
            await context.SaveChangesAsync();
        }

        var reader = new SqlCorpusRetrievalReader(factory);
        var candidates = await reader.SearchAsync(
            "INV-2026/0042", new ResolvedCorpusScope("root", [rootA], null), 5,
            CancellationToken.None);

        var hit = Assert.Single(candidates);
        Assert.Equal(rootA, hit.RootId);
        Assert.Equal(@"C:\corpus-a\guide.txt", hit.SourceIdentity);
        Assert.Contains("INV-2026/0042", hit.Content, StringComparison.Ordinal);
        Assert.True(hit.StartOffset > 0);
    }

    [NativeSqlServerFact]
    public async Task Workspace_scope_includes_nested_roots_but_excludes_a_sibling_prefix()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var outerRoot = Guid.NewGuid();
        var nestedRoot = Guid.NewGuid();
        var siblingRoot = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, outerRoot, @"C:\work", "outer.txt", ["workspace marker outer"]);
            AddPublishedText(context, nestedRoot, @"C:\work\nested", "inner.txt", ["workspace marker inner"]);
            AddPublishedText(context, siblingRoot, @"C:\workshop", "outside.txt", ["workspace marker outside"]);
            await context.SaveChangesAsync();
        }

        var reader = new SqlCorpusRetrievalReader(factory);
        var scope = await reader.ResolveScopeAsync("workspace", null, @"C:\work", CancellationToken.None);
        Assert.NotNull(scope);
        Assert.Equal(new[] { outerRoot, nestedRoot }.Order(), scope.RootIds.Order());

        var candidates = await reader.SearchAsync("workspace marker", scope, 5, CancellationToken.None);
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, hit => Assert.True(hit.RootId == outerRoot || hit.RootId == nestedRoot));

        var differentCase = await reader.ResolveScopeAsync("workspace", null, @"C:\WORK", CancellationToken.None);
        Assert.NotNull(differentCase);
        var caseMatches = await reader.SearchAsync("workspace marker", differentCase, 5, CancellationToken.None);
        Assert.Equal(2, caseMatches.Count);

        var driveRoot = await reader.ResolveScopeAsync("workspace", null, @"C:\", CancellationToken.None);
        Assert.NotNull(driveRoot);
        var driveMatches = await reader.SearchAsync("workspace marker", driveRoot, 5, CancellationToken.None);
        Assert.Equal(3, driveMatches.Count);
    }

    [NativeSqlServerFact]
    public async Task Service_returns_an_exact_bounded_plain_text_passage_with_its_original_owner()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\invoices", "ledger.txt",
                ["The ordinary text has an early section. ", "INV-2026/0042 confirms payment."]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(
            new SqlCorpusRetrievalReader(factory), new TestEvidenceCodec(),
            new LocalPrivateContentDisclosure());
        var response = await service.SearchAsync(
            new CorpusSearchRequest("INV-2026/0042", 5, "root", rootId, null),
            CancellationToken.None);

        var hit = Assert.Single(response.Results);
        Assert.Equal("lexical", response.RetrievalMode);
        Assert.Equal(rootId, hit.RootId);
        Assert.Equal(@"C:\invoices\ledger.txt", hit.SourceIdentity);
        Assert.Equal("INV-2026/0042 confirms payment.", hit.Passage);
        Assert.Equal("synthetic-reference", hit.EvidenceRef);
        Assert.True(hit.StartOffset > 0);
    }

    [NativeSqlServerFact]
    public async Task Evidence_read_returns_bounded_neighbouring_text_and_rejects_a_deleted_record()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\evidence", "sample.txt",
                ["before ", "cited phrase", " after"]);
            await context.SaveChangesAsync();
        }

        var codec = new TestEvidenceCodec();
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory), codec,
            new LocalPrivateContentDisclosure());
        var hit = Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("cited phrase", 5, "root", rootId, null),
            CancellationToken.None)).Results);
        var passage = await service.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 13), CancellationToken.None);
        Assert.Equal("before cited phrase after", passage.Text);
        Assert.Equal(0, passage.StartOffset);
        Assert.Equal(hit.StartOffset, passage.CitedStart);
        Assert.Equal(hit.Length, passage.CitedLength);

        await using (var context = await factory.CreateDbContextAsync())
        {
            var record = await context.PipelineRecords.FindAsync(hit.PipelineRecordId);
            Assert.NotNull(record);
            record.IsDeleted = true;
            await context.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new CorpusReadRequest(hit.EvidenceRef, 13), CancellationToken.None));
        Assert.Equal("evidence-stale", error.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Punctuation_only_query_returns_an_empty_success()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var result = await service.SearchAsync(
            new CorpusSearchRequest("///", 5, "all", null, null), CancellationToken.None);
        Assert.Empty(result.Results);
    }

    [NativeSqlServerFact]
    public async Task Full_text_word_breaker_finds_an_inflected_ordinary_text_term()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\terms", "payment.txt",
                ["The payment settled yesterday."]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        CorpusSearchResponse? response = null;
        // SQL Server full-text population is asynchronous and may share the test instance
        // with other indexing tests, so allow it time to observe this new chunk.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            response = await service.SearchAsync(
                new CorpusSearchRequest("payments", 5, "root", rootId, null), CancellationToken.None);
            if (response.Results.Count > 0) break;
            await Task.Delay(200);
        }

        var hit = Assert.Single(response!.Results);
        Assert.Contains("lexical:full-text", hit.Explanation);
        Assert.Equal("The payment settled yesterday.", hit.Passage);
    }

    [NativeSqlServerFact]
    public async Task Canonical_OCR_provenance_cites_both_pages_crossed_by_a_passage()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        var metadata = JsonSerializer.Serialize(new DocumentProvenance(1,
        [
            new DocumentPageProvenance(0, 0, 9, "native", null, []),
            new DocumentPageProvenance(1, 11, 10, "ocr", 0, [])
        ]));
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\pages", "hybrid.pdf",
                ["FirstPage\n\nSecondPage"], metadata);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var hit = Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("Page\n\nSecond", 5, "root", rootId, null),
            CancellationToken.None)).Results);
        Assert.Contains(hit.Locations, location => location.Kind == "page" && location.PageNumber == 1);
        Assert.Contains(hit.Locations, location => location.Kind == "page" && location.PageNumber == 2);
        Assert.DoesNotContain("provenance-invalid", hit.Explanation);
    }

    [NativeSqlServerFact]
    public async Task Search_caps_one_logical_document_at_two_passages_and_fills_from_another()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, Guid.NewGuid(), @"C:\verbose", "first.txt",
                ["needle first ", "needle second ", "needle third "]);
            AddPublishedText(context, Guid.NewGuid(), @"C:\other", "second.txt",
                ["needle fourth"]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var response = await service.SearchAsync(
            new CorpusSearchRequest("needle", 3, "all", null, null), CancellationToken.None);
        Assert.Equal(3, response.Results.Count);
        Assert.Equal(2, response.Results.Count(hit => hit.Title == "first.txt"));
        Assert.Single(response.Results, hit => hit.Title == "second.txt");
    }

    [NativeSqlServerFact]
    public async Task Corpus_results_obey_the_same_credential_text_boundary_as_the_CLI_envelope()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\transport-parity", "safe.txt", ["parity marker safe content"]);
            AddPublishedText(context, rootId, @"C:\transport-parity", "withheld.txt", ["parity marker discusses a bearer token"]);
            await context.SaveChangesAsync();
        }
        var reader = new SqlCorpusRetrievalReader(factory);
        var codec = new TestEvidenceCodec();
        var service = new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure());
        var response = await service.SearchAsync(new CorpusSearchRequest("parity marker", 10, "root", rootId, null), CancellationToken.None);
        Assert.Equal("safe.txt", Assert.Single(response.Results).Title);
        var envelope = JsonSerializer.Serialize(new { ok = true, result = response, reasonCode = (string?)null, message = (string?)null, retryable = false });
        Assert.True(FluxKnowledge.Application.IntegrationV1.NativeV1EnvelopeProtector.TryRead(envelope, out _));

        var scope = new ResolvedCorpusScope("root", [rootId], null);
        var candidate = Assert.Single(await reader.SearchAsync("parity marker", scope, 10, CancellationToken.None),
            value => value.SourceIdentity.EndsWith("withheld.txt", StringComparison.Ordinal));
        var binding = new CorpusEvidenceBinding(1, candidate.RootId, candidate.OwnerSourceRevisionId,
            Hash(candidate.SourceIdentity), candidate.PipelineRecordId, candidate.PipelineRecordRevision,
            candidate.ArtifactId, candidate.ArtifactHash, candidate.ChunkId, candidate.ChunkHash, 0, 6);
        var error = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new CorpusReadRequest(codec.Encode(binding), 0), CancellationToken.None));
        Assert.Equal("content-withheld", error.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Workspace_metadata_obeys_the_transport_boundary_even_without_hits()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, Guid.NewGuid(), @"C:\docs\bearer token", "safe.txt", ["unrelated text"]);
            await context.SaveChangesAsync();
        }
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var error = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.SearchAsync(new CorpusSearchRequest("missing-unique-phrase", 5, "workspace", null,
                @"C:\docs\bearer token"), CancellationToken.None));
        Assert.Equal("content-withheld", error.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Search_never_reveals_a_secret_when_the_passage_starts_after_its_assignment_marker()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\secrets", "hidden.txt",
                [new string('x', 200) + " password=abc123 " + new string('y', 1200)]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var response = await service.SearchAsync(
            new CorpusSearchRequest("abc123", 5, "root", rootId, null), CancellationToken.None);
        Assert.Empty(response.Results);
    }

    [NativeSqlServerFact]
    public async Task Secret_assignment_split_across_chunks_is_withheld_even_on_zero_context_read()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\split-secret", "hidden.txt",
                [new string('x', 2000) + " password=", "abc123 visible"]);
            await context.SaveChangesAsync();
        }

        var reader = new SqlCorpusRetrievalReader(factory);
        var scope = await reader.ResolveScopeAsync("root", rootId, null, CancellationToken.None);
        Assert.NotNull(scope);
        var candidate = Assert.Single(await reader.SearchAsync("abc123", scope, 5, CancellationToken.None));
        var binding = new CorpusEvidenceBinding(1, candidate.RootId, candidate.OwnerSourceRevisionId,
            Hash(candidate.SourceIdentity), candidate.PipelineRecordId,
            candidate.PipelineRecordRevision, candidate.ArtifactId, candidate.ArtifactHash,
            candidate.ChunkId, candidate.ChunkHash, candidate.StartOffset, 6);
        var codec = new TestEvidenceCodec();
        var service = new CorpusRetrievalService(reader, codec, new LocalPrivateContentDisclosure());
        Assert.Empty((await service.SearchAsync(new CorpusSearchRequest("abc123", 5, "root", rootId, null),
            CancellationToken.None)).Results);

        var error = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new CorpusReadRequest(codec.Encode(binding), 0),
                CancellationToken.None));
        Assert.Equal("content-withheld", error.ReasonCode);
    }

    [NativeSqlServerFact]
    public async Task Long_credential_value_cannot_escape_a_clipped_disclosure_window()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        foreach (var valueUnit in new[] { "A", "A!", "A quoted value " })
        {
            var rootId = Guid.NewGuid();
            var value = string.Concat(Enumerable.Repeat(valueUnit, 5000 / valueUnit.Length + 1));
            var chunks = ("password=" + value + ".TOKEN.VALUE")
                .Chunk(1900).Select(static chunk => new string(chunk)).ToArray();
            await using (var context = await factory.CreateDbContextAsync())
            {
                AddPublishedText(context, rootId, @"C:\long-secret\" + rootId.ToString("N"),
                    "hidden.txt", chunks);
                await context.SaveChangesAsync();
            }

            var response = await service.SearchAsync(
                new CorpusSearchRequest("TOKEN", 5, "root", rootId, null), CancellationToken.None);
            Assert.Empty(response.Results);
        }
    }

    [NativeSqlServerFact]
    public async Task Long_document_search_and_read_are_not_withheld_by_the_disclosure_scan_limit()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        var chunks = Enumerable.Range(0, 100)
            .Select(index => index == 50
                ? "target phrase " + string.Concat(Enumerable.Repeat("safe ", 400)) + "\n"
                : string.Concat(Enumerable.Repeat("safe ", 402)) + "\n")
            .ToArray();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\long-safe", "book.txt", chunks);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var hit = Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("target phrase", 5, "root", rootId, null),
            CancellationToken.None)).Results);
        var passage = await service.ReadAsync(
            new CorpusReadRequest(hit.EvidenceRef, 4096), CancellationToken.None);
        Assert.Contains("target phrase", passage.Text);
        Assert.Equal(hit.StartOffset, passage.CitedStart);
    }

    [NativeSqlServerFact]
    public async Task Full_text_match_near_end_of_a_chunk_returns_the_matching_word()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\late-term", "article.txt",
                [new string('x', 1850) + " payment settled."]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        CorpusSearchResponse? response = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            response = await service.SearchAsync(
                new CorpusSearchRequest("payments", 5, "root", rootId, null), CancellationToken.None);
            if (response.Results.Count > 0) break;
            await Task.Delay(200);
        }

        var hit = Assert.Single(response!.Results);
        Assert.Contains("payment", hit.Passage);
        Assert.Contains("lexical:full-text", hit.Explanation);
    }

    [NativeSqlServerFact]
    public async Task Full_text_anchor_handles_short_and_irregular_terms_at_word_boundaries()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\lexical-anchor", "terms.txt",
                ["othermice " + new string('x', 1750) + " mice and ai tools"]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        foreach (var (query, expected) in new[] { ("mouse", "mice"), ("AI", "ai") })
        {
            CorpusSearchResponse? response = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                response = await service.SearchAsync(
                    new CorpusSearchRequest(query, 5, "root", rootId, null), CancellationToken.None);
                if (response.Results.Count > 0) break;
                await Task.Delay(200);
            }
            var hit = Assert.Single(response!.Results);
            Assert.Contains(expected, hit.Passage);
            Assert.Contains("lexical:full-text", hit.Explanation);
            Assert.DoesNotContain("othermice", hit.Passage);
        }
    }

    [NativeSqlServerFact]
    public async Task Full_text_anchor_preserves_accent_insensitive_case_variants()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\accents", "menu.txt",
                [new string('x', 1750) + " café menu"]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        CorpusSearchResponse? response = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            response = await service.SearchAsync(
                new CorpusSearchRequest("CAFÉ", 5, "root", rootId, null), CancellationToken.None);
            if (response.Results.Count > 0) break;
            await Task.Delay(200);
        }
        var hit = Assert.Single(response!.Results);
        Assert.Contains("café", hit.Passage);
        Assert.Contains("lexical:full-text", hit.Explanation);
    }

    [NativeSqlServerFact]
    public async Task Long_exact_query_returns_bounded_verified_evidence()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        var query = new string('q', 1400);
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\long-query", "article.txt", ["head " + query + " tail"]);
            await context.SaveChangesAsync();
        }

        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory),
            new TestEvidenceCodec(), new LocalPrivateContentDisclosure());
        var response = await service.SearchAsync(
            new CorpusSearchRequest(query, 5, "root", rootId, null), CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Equal(1024, hit.Length);
        Assert.Equal(new string('q', 1024), hit.Passage);
        Assert.Contains("passage-bounded", hit.Explanation);
    }

    [NativeSqlServerFact]
    public async Task Document_publication_replaces_selected_metadata_with_OCR_and_invalidates_evidence()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        (Guid InputId, Guid BranchId, Guid RecordId) selected;
        (Guid InputId, Guid BranchId, Guid RecordId) successor;
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = @"C:\documents", DisplayName = "Documents",
                State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                MaximumFileBytes = 1024 * 1024, AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 60, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            context.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = ownerId, SourceRootId = rootId,
                StableSourceIdentity = @"C:\documents\scan.pdf", Revision = 1,
                ContentSha256 = Hash("physical pdf"), CanonicalPath = @"C:\documents\scan.pdf",
                Classification = "PdfDocumentContainer", Extension = ".pdf",
                ByteLength = 12, DiscoveredAtUtc = now
            });
            selected = AddDocumentInput(context, rootId, ownerId, "selected metadata phrase", "first", now, originKind: 3);
            successor = AddDocumentInput(context, rootId, ownerId, "successor OCR phrase", "second", now);
            context.DocumentPublications.Add(new DocumentPublicationEntity
            {
                OwnerSourceRevisionId = ownerId, DocumentInputSourceRevisionId = selected.InputId,
                SourceProcessorBranchId = selected.BranchId, PipelineRecordId = selected.RecordId,
                PipelineRecordRevision = 1, ProcessorFingerprint = "first", PublishedAtUtc = now
            });
            await context.SaveChangesAsync();
        }

        var codec = new TestEvidenceCodec();
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory), codec,
            new LocalPrivateContentDisclosure());
        var original = Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("selected metadata phrase", 5, "root", rootId, null),
            CancellationToken.None)).Results);
        Assert.Equal(ownerId, original.OwnerSourceRevisionId);
        Assert.Equal(@"C:\documents\scan.pdf", original.SourceIdentity);
        Assert.Equal("metadata", original.ExtractionMethod);
        Assert.Empty((await service.SearchAsync(
            new CorpusSearchRequest("successor OCR phrase", 5, "root", rootId, null),
            CancellationToken.None)).Results);

        await using (var context = await factory.CreateDbContextAsync())
        {
            var publication = await context.DocumentPublications.FindAsync(ownerId);
            Assert.NotNull(publication);
            publication.DocumentInputSourceRevisionId = successor.InputId;
            publication.SourceProcessorBranchId = successor.BranchId;
            publication.PipelineRecordId = successor.RecordId;
            publication.ProcessorFingerprint = "second";
            await context.SaveChangesAsync();
        }

        var stale = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new CorpusReadRequest(original.EvidenceRef, 0), CancellationToken.None));
        Assert.Equal("evidence-stale", stale.ReasonCode);
        Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("successor OCR phrase", 5, "root", rootId, null),
            CancellationToken.None)).Results);
    }

    [NativeSqlServerFact]
    public async Task Suppression_revokes_a_reference_and_independent_archive_members_remain_distinct()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var rootId = Guid.NewGuid();
        await using (var context = await factory.CreateDbContextAsync())
        {
            AddPublishedText(context, rootId, @"C:\archive", "member-a.txt", ["archive marker a"], originKind: 1);
            AddPublishedText(context, rootId, @"C:\archive", "member-b.txt", ["archive marker b"], originKind: 1);
            await context.SaveChangesAsync();
        }
        var codec = new TestEvidenceCodec();
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory), codec,
            new LocalPrivateContentDisclosure());
        var hits = (await service.SearchAsync(
            new CorpusSearchRequest("archive marker", 5, "root", rootId, null),
            CancellationToken.None)).Results;
        Assert.Equal(2, hits.Count);
        Assert.Equal(2, hits.Select(hit => hit.OwnerSourceRevisionId).Distinct().Count());
        var first = Assert.Single(hits, hit => hit.Title == "member-a.txt");

        await using (var context = await factory.CreateDbContextAsync())
        {
            var revision = await context.SourceRevisions.FindAsync(first.OwnerSourceRevisionId);
            Assert.NotNull(revision);
            revision.SuppressedAtUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync();
        }
        var stale = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new CorpusReadRequest(first.EvidenceRef, 0), CancellationToken.None));
        Assert.Equal("evidence-stale", stale.ReasonCode);
        Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("archive marker", 5, "root", rootId, null),
            CancellationToken.None)).Results);
    }

    [NativeSqlServerFact]
    public async Task Unrooted_current_record_is_in_all_scope_and_old_revision_becomes_stale()
    {
        var factory = SqlTestData.CreateFactory(fixture);
        var identityId = Guid.NewGuid();
        var oldId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var context = await factory.CreateDbContextAsync())
        {
            context.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identityId, SourceKind = "import", StableKey = "unrooted-source",
                CreatedAtUtc = now
            });
            AddUnrootedRecord(context, identityId, oldId, 1, "unrooted marker old", now);
            await context.SaveChangesAsync();
        }
        var codec = new TestEvidenceCodec();
        var service = new CorpusRetrievalService(new SqlCorpusRetrievalReader(factory), codec,
            new LocalPrivateContentDisclosure());
        var original = Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("unrooted marker", 5, "all", null, null),
            CancellationToken.None)).Results);
        Assert.Null(original.RootId);
        Assert.Null(original.OwnerSourceRevisionId);

        await using (var context = await factory.CreateDbContextAsync())
        {
            AddUnrootedRecord(context, identityId, Guid.NewGuid(), 2,
                "unrooted marker current", now);
            await context.SaveChangesAsync();
        }
        var stale = await Assert.ThrowsAsync<FluxKnowledge.Application.IntegrationV1.NativeOperationException>(
            async () => await service.ReadAsync(new CorpusReadRequest(original.EvidenceRef, 0), CancellationToken.None));
        Assert.Equal("evidence-stale", stale.ReasonCode);
        var current = Assert.Single((await service.SearchAsync(
            new CorpusSearchRequest("unrooted marker", 5, "all", null, null),
            CancellationToken.None)).Results);
        Assert.Equal(2, current.PipelineRecordRevision);
    }

    private static void AddUnrootedRecord(
        FluxKnowledgeDbContext context, Guid sourceIdentityId, Guid recordId,
        long revision, string text, DateTimeOffset now)
    {
        var artifactId = Guid.NewGuid();
        var hash = Hash(text);
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId, SourceIdentityId = sourceIdentityId, Revision = revision,
            ContentHash = hash, RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true,
            RegisteredAtUtc = now
        });
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId, PipelineRecordId = recordId, SourceRevision = revision,
            Stage = (int)PipelineStage.CanonicalIndex, ContentHash = hash,
            ContentType = "text/plain", SearchText = text, CreatedAtUtc = now
        });
        context.TextChunks.Add(new TextChunkEntity
        {
            ArtifactId = artifactId, SourceRevision = revision, Ordinal = 0,
            StartOffset = 0, Length = text.Length, Content = text, ContentHash = hash
        });
    }

    private static (Guid InputId, Guid BranchId, Guid RecordId) AddDocumentInput(
        FluxKnowledgeDbContext context, Guid rootId, Guid ownerId, string text,
        string branchLabel, DateTimeOffset now, int originKind = 2)
    {
        var inputId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var sourceIdentityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var hash = Hash(text);
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = inputId, SourceRootId = rootId, ParentSourceRevisionId = ownerId,
            StableSourceIdentity = "internal-" + branchLabel, Revision = 1,
            ContentSha256 = hash, CanonicalPath = @"C:\internal\" + branchLabel,
            Classification = "DocumentProcessingInput", Extension = ".pdf",
            OriginKind = originKind, ByteLength = text.Length, DiscoveredAtUtc = now
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId, SourceRevisionId = ownerId, ActivityKind = 1,
            ExecutionClass = 1, ProcessorVersion = branchLabel,
            InputFingerprint = hash, State = 2, CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
        {
            Id = branchId, SourceActivityId = activityId, SourceRevisionId = ownerId,
            InputSha256 = Hash("physical pdf"), ProcessorVersion = branchLabel,
            ProcessorFingerprint = branchLabel, State = 2,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = sourceIdentityId, SourceKind = "local file",
            StableKey = "internal-" + branchLabel, CreatedAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId, SourceIdentityId = sourceIdentityId, SourceRevisionId = inputId,
            Revision = 1, ContentHash = hash, RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true,
            RegisteredAtUtc = now
        });
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId, PipelineRecordId = recordId, SourceRevision = 1,
            Stage = (int)PipelineStage.CanonicalIndex, ContentHash = hash,
            ContentType = "text/plain", SearchText = text, CreatedAtUtc = now
        });
        context.TextChunks.Add(new TextChunkEntity
        {
            ArtifactId = artifactId, SourceRevision = 1, Ordinal = 0,
            StartOffset = 0, Length = text.Length, Content = text, ContentHash = hash
        });
        return (inputId, branchId, recordId);
    }

    private sealed class TestEvidenceCodec : ICorpusEvidenceCodec
    {
        private readonly Dictionary<string, CorpusEvidenceBinding> _bindings = [];
        public string Encode(CorpusEvidenceBinding binding)
        {
            var reference = _bindings.Count == 0
                ? "synthetic-reference"
                : $"synthetic-reference-{_bindings.Count + 1}";
            _bindings.Add(reference, binding);
            return reference;
        }
        public CorpusEvidenceBinding Decode(string reference) =>
            _bindings.TryGetValue(reference, out var binding)
                ? binding
                : throw new NotSupportedException();
    }

    private static void AddPublishedText(
        FluxKnowledgeDbContext context, Guid rootId, string rootPath, string fileName,
        IReadOnlyList<string> chunks, string? metadataJson = null, int originKind = 0)
    {
        var now = DateTimeOffset.UtcNow;
        var sourceRevisionId = Guid.NewGuid();
        var sourceIdentityId = Guid.NewGuid();
        var pipelineRecordId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var path = Path.Combine(rootPath, fileName);
        var canonicalText = string.Concat(chunks);
        var contentHash = Hash(canonicalText);
        if (!context.SourceRootConfigurations.Local.Any(root => root.Id == rootId))
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = rootPath, DisplayName = rootId.ToString("N"),
                State = 0, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                MaximumFileBytes = 1024 * 1024, AllowedClassificationsJson = "[]",
                ReconciliationCadenceSeconds = 60, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = sourceRevisionId, SourceRootId = rootId, StableSourceIdentity = path,
            Revision = 1, ContentSha256 = contentHash, CanonicalPath = path,
            Classification = "AcceptedUtf8Text", Extension = ".txt", OriginKind = originKind,
            ByteLength = canonicalText.Length,
            DiscoveredAtUtc = now
        });
        context.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = sourceIdentityId, SourceKind = "local file", StableKey = path, CreatedAtUtc = now
        });
        context.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = pipelineRecordId, SourceIdentityId = sourceIdentityId,
            SourceRevisionId = sourceRevisionId, Revision = 1, ContentHash = contentHash,
            RootLineageRecordId = pipelineRecordId, CurrentStage = (int)PipelineStage.Publish,
            CompletionCriteriaMet = true, RegisteredAtUtc = now
        });
        context.Artifacts.Add(new ArtifactEntity
        {
            Id = artifactId, PipelineRecordId = pipelineRecordId, SourceRevision = 1,
            Stage = (int)PipelineStage.CanonicalIndex, ContentHash = contentHash,
            ContentType = "text/plain", SearchText = canonicalText,
            DocumentMetadataJson = metadataJson, CreatedAtUtc = now
        });
        var start = 0;
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var content = chunks[ordinal];
            context.TextChunks.Add(new TextChunkEntity
            {
                ArtifactId = artifactId, SourceRevision = 1, Ordinal = ordinal,
                StartOffset = start, Length = content.Length, Content = content,
                ContentHash = Hash(content)
            });
            start += content.Length;
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
