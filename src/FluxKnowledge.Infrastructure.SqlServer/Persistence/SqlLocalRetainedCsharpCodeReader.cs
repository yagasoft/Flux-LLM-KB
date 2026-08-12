using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>SQL-backed retained C# facts that first establish the immutable retained-artifact binding.</summary>
public sealed class SqlLocalRetainedCsharpCodeReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    ILocalRetainedDetailReader retainedDetailReader,
    ILocalPrivateContentDisclosure disclosure,
    LocalRetainedCsharpCodeSearchCursorCodec cursorCodec) : ILocalRetainedCsharpCodeReader
{
    private const int MaximumFacts = 256;
    private const int MaximumSearchFacts = 32;
    private const int MaximumQueryCharacters = 256;

    public ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadAsync(
        Guid branchId,
        CancellationToken cancellationToken) =>
        ReadPageAsync(branchId, LocalRetainedCsharpCodePageRequest.First, cancellationToken);

    public async ValueTask<LocalRetainedCsharpCodeDetailProjection?> ReadPageAsync(
        Guid branchId,
        LocalRetainedCsharpCodePageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pageRequest);
        ValidatePageRequest(pageRequest);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var binding = await ReadCurrentBindingAsync(context, branchId, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            return null;
        }

        // ReadCurrentBindingAsync has already validated the exact receipt, parser, handler and
        // outcome-specific document shape. Only then may the generic retained-detail reader
        // perform retained-byte integrity checks and disclose trusted-local metadata.
        var retainedDetail = await retainedDetailReader.ReadAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (retainedDetail is null)
        {
            return null;
        }

        ValidateRetainedDetail(binding, retainedDetail);
        var receipt = binding.Receipt;

        var symbols = Array.Empty<LocalRetainedCsharpSymbolProjection>();
        var references = Array.Empty<LocalRetainedCsharpReferenceProjection>();
        var diagnostics = Array.Empty<LocalRetainedCsharpDiagnosticProjection>();
        var additionalWithheldSymbols = 0;
        var additionalWithheldReferences = 0;
        var additionalWithheldDiagnostics = 0;

        if (receipt.OutcomeCode == "success")
        {
            var documentId = branchId;
            var document = binding.Document!;

            var persistedSymbols = await context.SourceProcessorCodeSymbols.AsNoTracking()
                .Where(value => value.DocumentId == documentId)
                .Where(value => !pageRequest.SymbolAfterOrdinal.HasValue || value.Ordinal > pageRequest.SymbolAfterOrdinal.Value)
                .OrderBy(value => value.Ordinal)
                .Take(MaximumFacts + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var persistedReferences = await context.SourceProcessorCodeReferences.AsNoTracking()
                .Where(value => value.DocumentId == documentId)
                .Where(value => !pageRequest.ReferenceAfterOrdinal.HasValue || value.Ordinal > pageRequest.ReferenceAfterOrdinal.Value)
                .OrderBy(value => value.Ordinal)
                .Take(MaximumFacts + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var persistedDiagnostics = await context.SourceProcessorCodeDiagnostics.AsNoTracking()
                .Where(value => value.DocumentId == documentId)
                .Where(value => !pageRequest.DiagnosticAfterOrdinal.HasValue || value.Ordinal > pageRequest.DiagnosticAfterOrdinal.Value)
                .OrderBy(value => value.Ordinal)
                .Take(MaximumFacts + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            var symbolPage = TakePage(persistedSymbols);
            var referencePage = TakePage(persistedReferences);
            var diagnosticPage = TakePage(persistedDiagnostics);
            (symbols, additionalWithheldSymbols) = ProjectSymbols(symbolPage.Values);
            (references, additionalWithheldReferences) = ProjectReferences(referencePage.Values);
            (diagnostics, additionalWithheldDiagnostics) = ProjectDiagnostics(diagnosticPage.Values, false);

            return new LocalRetainedCsharpCodeDetailProjection(
                branchId,
                retainedDetail.SourceRevisionId,
                retainedDetail.LocalPath,
                retainedDetail.ArtifactHash,
                retainedDetail.ArtifactByteLength,
                receipt.OutcomeCode,
                receipt.CompletionFingerprint,
                receipt.DocumentFingerprint,
                checked(receipt.WithheldSymbolCount + additionalWithheldSymbols),
                checked(receipt.WithheldReferenceCount + additionalWithheldReferences),
                checked(receipt.WithheldDiagnosticCount + additionalWithheldDiagnostics),
                symbols,
                references,
                diagnostics)
            {
                PersistedSymbolCount = document.SymbolCount,
                PersistedReferenceCount = document.ReferenceCount,
                PersistedDiagnosticCount = document.DiagnosticsCount,
                NextSymbolOrdinal = symbolPage.NextOrdinal,
                NextReferenceOrdinal = referencePage.NextOrdinal,
                NextDiagnosticOrdinal = diagnosticPage.NextOrdinal
            };
        }

        var persistedBlockedDiagnostics = await context.SourceProcessorCodeBlockedDiagnostics.AsNoTracking()
            .Where(value => value.SourceProcessorBranchId == branchId && value.SourceProcessorAttemptId == receipt.SourceProcessorAttemptId)
            .Where(value => !pageRequest.DiagnosticAfterOrdinal.HasValue || value.Ordinal > pageRequest.DiagnosticAfterOrdinal.Value)
            .OrderBy(value => value.Ordinal)
            .Take(MaximumFacts + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var blockedDiagnosticPage = TakePage(persistedBlockedDiagnostics);
        (diagnostics, additionalWithheldDiagnostics) = ProjectBlockedDiagnostics(blockedDiagnosticPage.Values);

        return new LocalRetainedCsharpCodeDetailProjection(
            branchId,
            retainedDetail.SourceRevisionId,
            retainedDetail.LocalPath,
            retainedDetail.ArtifactHash,
            retainedDetail.ArtifactByteLength,
            receipt.OutcomeCode,
            receipt.CompletionFingerprint,
            receipt.DocumentFingerprint,
            checked(receipt.WithheldSymbolCount + additionalWithheldSymbols),
            checked(receipt.WithheldReferenceCount + additionalWithheldReferences),
            checked(receipt.WithheldDiagnosticCount + additionalWithheldDiagnostics),
            symbols,
            references,
            diagnostics)
        {
            PersistedSymbolCount = 0,
            PersistedReferenceCount = 0,
            PersistedDiagnosticCount = receipt.BlockedDiagnosticsCount,
            NextDiagnosticOrdinal = blockedDiagnosticPage.NextOrdinal
        };
    }

    public async ValueTask<IReadOnlyList<LocalRetainedCsharpCodeSearchProjection>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken) =>
        (await SearchPageAsync(
            new LocalRetainedCsharpCodeSearchPageRequest(query, limit, null),
            cancellationToken).ConfigureAwait(false)).Results;

    public async ValueTask<LocalRetainedCsharpCodeSearchPage> SearchPageAsync(
        LocalRetainedCsharpCodeSearchPageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pageRequest);
        ArgumentNullException.ThrowIfNull(pageRequest.Query);
        if (pageRequest.Query.Length > MaximumQueryCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(pageRequest), "The local C# search query exceeds the fixed bound.");
        }

        var boundedLimit = Math.Clamp(pageRequest.Limit, 1, MaximumSearchFacts);
        var canonicalQuery = LocalRetainedCsharpCodeSearchCursorCodec.CanonicaliseQuery(pageRequest.Query);
        if (string.IsNullOrWhiteSpace(canonicalQuery))
        {
            return new LocalRetainedCsharpCodeSearchPage([], null);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<Guid, SearchVerifiedDetail?>();
        LocalRetainedCsharpCodeSearchCursorPosition? cursorPosition = null;
        if (pageRequest.Cursor is not null)
        {
            cursorPosition = await cursorCodec.ValidateAsync(
                    context,
                    canonicalQuery,
                    pageRequest.Cursor,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var candidates = await ReadSearchCandidatesAsync(
                context,
                canonicalQuery,
                cursorPosition,
                MaximumSearchFacts + 1,
                cancellationToken)
            .ConfigureAwait(false);

        var groups = new Dictionary<Guid, SearchResultAccumulator>();
        var order = new List<Guid>();
        var emittedFactCount = 0;
        var processedCandidateCount = 0;
        SearchCandidate? lastProcessed = null;
        foreach (var candidate in candidates.Take(MaximumSearchFacts))
        {
            processedCandidateCount++;
            if (!details.TryGetValue(candidate.BranchId, out var detail))
            {
                detail = await ReadSearchVerifiedDetailAsync(context, candidate.BranchId, cancellationToken)
                    .ConfigureAwait(false);
                details.Add(candidate.BranchId, detail);
            }

            if (detail is null || detail.OutcomeCode != "success")
            {
                lastProcessed = candidate;
                continue;
            }

            if (!groups.TryGetValue(candidate.BranchId, out var group))
            {
                group = new SearchResultAccumulator(candidate.BranchId, detail.LocalPath, detail.ArtifactHash);
                groups.Add(candidate.BranchId, group);
                order.Add(candidate.BranchId);
            }

            var emitted = candidate.FactKind switch
            {
                (int)LocalRetainedCsharpCodeSearchFactKind.Symbol => AddSearchSymbol(group, candidate),
                (int)LocalRetainedCsharpCodeSearchFactKind.Reference => AddSearchReference(group, candidate),
                _ => throw new InvalidDataException("The retained C# search cursor has an unsupported fact kind.")
            };
            lastProcessed = candidate;
            if (!emitted)
            {
                continue;
            }

            emittedFactCount++;
            if (emittedFactCount == boundedLimit)
            {
                break;
            }
        }

        var results = order
            .Where(branchId => groups[branchId].Symbols.Count != 0 || groups[branchId].References.Count != 0)
            .Select(branchId => groups[branchId].ToProjection())
            .ToArray();
        var hasMore = processedCandidateCount < candidates.Length;
        return new LocalRetainedCsharpCodeSearchPage(
            results,
            hasMore && lastProcessed is not null
                ? await cursorCodec.CreateAsync(
                    context,
                    canonicalQuery,
                    lastProcessed.BranchId,
                    (LocalRetainedCsharpCodeSearchFactKind)lastProcessed.FactKind,
                    lastProcessed.Ordinal,
                    cancellationToken).ConfigureAwait(false)
                : null);
    }

    public async ValueTask<LocalDisclosureResult> ReadExcerptAsync(Guid branchId, CancellationToken cancellationToken)
    {
        if (await ReadAsync(branchId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new FileNotFoundException("The retained C# receipt is unavailable for this branch.");
        }

        return await retainedDetailReader.ReadExcerptAsync(branchId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes the terminal C# receipt and the branch/activity/attempt state before any
    /// general retained-detail projection can disclose path, checksum or attempt evidence.
    /// </summary>
    private static async ValueTask<DurableCsharpBinding?> ReadCurrentBindingAsync(
        FluxKnowledgeDbContext context,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var receipt = await context.SourceProcessorCodeCompletionReceipts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.SourceProcessorBranchId == branchId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        var identity = await (
            from branch in context.SourceProcessorBranches.AsNoTracking()
            join activity in context.SourceActivities.AsNoTracking() on branch.SourceActivityId equals activity.Id
            join revision in context.SourceRevisions.AsNoTracking() on branch.SourceRevisionId equals revision.Id
            where branch.Id == branchId
            select new
            {
                branch.SourceRevisionId,
                branch.InputSha256,
                branch.ProcessorVersion,
                branch.ProcessorFingerprint,
                branch.State,
                branch.LeaseOwner,
                branch.LeaseExpiresAtUtc,
                branch.LeaseGeneration,
                branch.CompletionReceiptFingerprint,
                ActivitySourceRevisionId = activity.SourceRevisionId,
                activity.ActivityKind,
                activity.ExecutionClass,
                ActivityProcessorVersion = activity.ProcessorVersion,
                ActivityDescriptorFingerprint = activity.DescriptorFingerprint,
                activity.InputFingerprint,
                ActivityState = activity.State,
                ActivityReason = activity.Reason,
                revision.Extension,
                revision.Classification,
                RevisionContentSha256 = revision.ContentSha256
            }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var attempt = await context.SourceProcessorAttempts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == receipt.SourceProcessorAttemptId, cancellationToken)
            .ConfigureAwait(false);
        var document = await context.SourceProcessorCodeDocuments.AsNoTracking()
            .SingleOrDefaultAsync(value => value.SourceProcessorBranchId == branchId, cancellationToken)
            .ConfigureAwait(false);

        if (identity is null || attempt is null ||
            attempt.BranchId != branchId ||
            attempt.LeaseGeneration != identity.LeaseGeneration ||
            attempt.FinishedAtUtc is null ||
            identity.SourceRevisionId != receipt.SourceRevisionId ||
            identity.ActivitySourceRevisionId != receipt.SourceRevisionId ||
            !string.Equals(identity.InputSha256, receipt.RetainedArtifactSha256, StringComparison.Ordinal) ||
            !string.Equals(identity.InputFingerprint, receipt.RetainedArtifactSha256, StringComparison.Ordinal) ||
            !string.Equals(identity.RevisionContentSha256, receipt.RetainedArtifactSha256, StringComparison.Ordinal) ||
            !string.Equals(identity.Extension, ".cs", StringComparison.Ordinal) ||
            !string.Equals(identity.Classification, "AcceptedUtf8Text", StringComparison.Ordinal) ||
            identity.ActivityKind != (int)SourceActivityKind.CodeParsing ||
            identity.ExecutionClass != (int)ExecutionClass.InProcess ||
            !string.Equals(identity.ProcessorVersion, RetainedCsharpCodeProcessor.ProcessorVersion, StringComparison.Ordinal) ||
            !string.Equals(identity.ActivityProcessorVersion, RetainedCsharpCodeProcessor.ProcessorVersion, StringComparison.Ordinal) ||
            !string.Equals(identity.ProcessorFingerprint, RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal) ||
            !string.Equals(identity.ActivityDescriptorFingerprint, RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal) ||
            !string.Equals(identity.CompletionReceiptFingerprint, receipt.CompletionFingerprint, StringComparison.Ordinal) ||
            receipt.ActivityKind != (int)SourceActivityKind.CodeParsing ||
            !string.Equals(receipt.ProcessorVersion, RetainedCsharpCodeProcessor.ProcessorVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.DescriptorFingerprint, RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint, StringComparison.Ordinal) ||
            !string.Equals(receipt.ParserFingerprint, RetainedCsharpCodeProcessor.ParserFingerprint, StringComparison.Ordinal) ||
            !string.Equals(receipt.HandlerImplementationId, RetainedCsharpCodeProcessor.HandlerImplementationId, StringComparison.Ordinal) ||
            identity.LeaseOwner is not null || identity.LeaseExpiresAtUtc is not null)
        {
            throw new InvalidDataException("The retained C# durable terminal binding is invalid.");
        }

        var isCurrentSuccess = receipt.OutcomeCode == "success" &&
            identity.State == (int)RetainedProcessorBranchState.Completed &&
            identity.ActivityState == (int)SourceActivityState.Completed &&
            identity.ActivityReason is null &&
            string.Equals(attempt.OutcomeCode, "success", StringComparison.Ordinal);
        var isCurrentSyntaxBlock = receipt.OutcomeCode == "csharp-code-syntax-invalid" &&
            identity.State == (int)RetainedProcessorBranchState.Blocked &&
            identity.ActivityState == (int)SourceActivityState.FailedTerminal &&
            string.Equals(identity.ActivityReason, receipt.OutcomeCode, StringComparison.Ordinal) &&
            string.Equals(attempt.OutcomeCode, receipt.OutcomeCode, StringComparison.Ordinal);
        if (!isCurrentSuccess && !isCurrentSyntaxBlock)
        {
            throw new InvalidDataException("The retained C# receipt is not the branch's current terminal outcome.");
        }

        if (isCurrentSuccess)
        {
            var blockedDiagnosticExists = await context.SourceProcessorCodeBlockedDiagnostics.AsNoTracking()
                .AnyAsync(value => value.SourceProcessorBranchId == branchId, cancellationToken)
                .ConfigureAwait(false);
            if (receipt.DocumentId != branchId || receipt.DocumentFingerprint is null || document is null ||
                blockedDiagnosticExists || receipt.BlockedDiagnosticsCount != 0 ||
                document.SourceRevisionId != identity.SourceRevisionId ||
                !string.Equals(document.RetainedArtifactSha256, identity.InputSha256, StringComparison.Ordinal) ||
                !string.Equals(document.DescriptorFingerprint, receipt.DescriptorFingerprint, StringComparison.Ordinal) ||
                !string.Equals(document.ParserFingerprint, receipt.ParserFingerprint, StringComparison.Ordinal) ||
                !string.Equals(document.HandlerImplementationId, receipt.HandlerImplementationId, StringComparison.Ordinal) ||
                document.LeaseGeneration != identity.LeaseGeneration ||
                document.WithheldSymbolCount != receipt.WithheldSymbolCount ||
                document.WithheldReferenceCount != receipt.WithheldReferenceCount ||
                document.WithheldDiagnosticCount != receipt.WithheldDiagnosticCount ||
                document.ReceiptDiagnosticCodeCount != receipt.ReceiptDiagnosticCodeCount ||
                !string.Equals(document.DocumentFingerprint, receipt.DocumentFingerprint, StringComparison.Ordinal) ||
                !string.Equals(document.CompletionFingerprint, receipt.CompletionFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The retained C# success document does not match its receipt.");
            }
        }
        else
        {
            var blockedDiagnosticCounts = await context.SourceProcessorCodeBlockedDiagnostics.AsNoTracking()
                .Where(value => value.SourceProcessorBranchId == branchId)
                .GroupBy(_ => 1)
                .Select(values => new
                {
                    Total = values.Count(),
                    Owned = values.Count(value => value.SourceProcessorAttemptId == receipt.SourceProcessorAttemptId)
                })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            var blockedDiagnosticCount = blockedDiagnosticCounts?.Owned ?? 0;
            var branchBlockedDiagnosticCount = blockedDiagnosticCounts?.Total ?? 0;
            if (receipt.DocumentId is not null || receipt.DocumentFingerprint is not null || document is not null ||
                blockedDiagnosticCount != receipt.BlockedDiagnosticsCount ||
                branchBlockedDiagnosticCount != receipt.BlockedDiagnosticsCount)
            {
                throw new InvalidDataException("The retained C# blocked document shape does not match its receipt.");
            }
        }

        return new DurableCsharpBinding(receipt, identity.SourceRevisionId, identity.InputSha256, document);
    }

    private static void ValidateRetainedDetail(
        DurableCsharpBinding binding,
        LocalRetainedDetailProjection retainedDetail)
    {
        if (binding.SourceRevisionId != retainedDetail.SourceRevisionId.Value ||
            !string.Equals(binding.InputSha256, retainedDetail.InputHash, StringComparison.Ordinal) ||
            !string.Equals(binding.InputSha256, retainedDetail.ArtifactHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The retained C# receipt does not match its verified retained branch.");
        }
    }

    private async ValueTask<SearchVerifiedDetail?> ReadSearchVerifiedDetailAsync(
        FluxKnowledgeDbContext context,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var binding = await ReadCurrentBindingAsync(context, branchId, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            return null;
        }

        var retainedDetail = await retainedDetailReader.ReadAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (retainedDetail is null)
        {
            return null;
        }

        ValidateRetainedDetail(binding, retainedDetail);
        return new SearchVerifiedDetail(
            retainedDetail.LocalPath,
            retainedDetail.ArtifactHash,
            binding.Receipt.OutcomeCode);
    }

    private static void ValidatePageRequest(LocalRetainedCsharpCodePageRequest pageRequest)
    {
        if (pageRequest.SymbolAfterOrdinal is < 0 ||
            pageRequest.ReferenceAfterOrdinal is < 0 ||
            pageRequest.DiagnosticAfterOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageRequest), "A retained C# fact continuation must be a non-negative ordinal.");
        }
    }

    private static Task<SearchCandidate[]> ReadSearchCandidatesAsync(
        FluxKnowledgeDbContext context,
        string query,
        LocalRetainedCsharpCodeSearchCursorPosition? cursor,
        int take,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (@take)
                [facts].[BranchId], [facts].[FactKind], [facts].[Ordinal], [facts].[DeclarationKindCode],
                [facts].[LocalName], [facts].[QualifiedName], [facts].[RenderedSignature], [facts].[Modifiers],
                [facts].[LexicalParentOrdinal], [facts].[RelationshipKindCode], [facts].[SourceSymbolOrdinal],
                [facts].[TargetDisplay], [facts].[SpanStartUtf16], [facts].[SpanLengthUtf16]
            FROM
            (
                SELECT [symbols].[DocumentId] AS [BranchId], CAST(1 AS int) AS [FactKind], [symbols].[Ordinal],
                    [symbols].[DeclarationKindCode], [symbols].[LocalName], [symbols].[QualifiedName],
                    [symbols].[RenderedSignature], [symbols].[Modifiers], [symbols].[LexicalParentOrdinal],
                    CAST(0 AS int) AS [RelationshipKindCode], CAST(NULL AS int) AS [SourceSymbolOrdinal],
                    CAST(NULL AS nvarchar(max)) AS [TargetDisplay], [symbols].[SpanStartUtf16], [symbols].[SpanLengthUtf16]
                FROM [SourceProcessorCodeSymbols] AS [symbols]
                WHERE CHARINDEX(@query, [symbols].[LocalName]) > 0
                   OR CHARINDEX(@query, [symbols].[QualifiedName]) > 0
                   OR CHARINDEX(@query, [symbols].[RenderedSignature]) > 0
                UNION ALL
                SELECT [references].[DocumentId] AS [BranchId], CAST(2 AS int) AS [FactKind], [references].[Ordinal],
                    CAST(0 AS int) AS [DeclarationKindCode], CAST(NULL AS nvarchar(1024)) AS [LocalName],
                    CAST(NULL AS nvarchar(max)) AS [QualifiedName], CAST(NULL AS nvarchar(max)) AS [RenderedSignature],
                    CAST(NULL AS nvarchar(512)) AS [Modifiers], CAST(-1 AS int) AS [LexicalParentOrdinal],
                    [references].[RelationshipKindCode], [references].[SourceSymbolOrdinal], [references].[TargetDisplay],
                    [references].[SpanStartUtf16], [references].[SpanLengthUtf16]
                FROM [SourceProcessorCodeReferences] AS [references]
                WHERE CHARINDEX(@query, [references].[TargetDisplay]) > 0
            ) AS [facts]
            WHERE @hasCursor = 0
               OR [facts].[BranchId] > @cursorBranchId
               OR ([facts].[BranchId] = @cursorBranchId AND
                   ([facts].[FactKind] > @cursorFactKind OR
                    ([facts].[FactKind] = @cursorFactKind AND [facts].[Ordinal] > @cursorOrdinal)))
            ORDER BY [facts].[BranchId], [facts].[FactKind], [facts].[Ordinal];
            """;
        return context.Database.SqlQueryRaw<SearchCandidate>(
                sql,
                new SqlParameter("@take", take),
                new SqlParameter("@query", query),
                new SqlParameter("@hasCursor", cursor is null ? 0 : 1),
                new SqlParameter("@cursorBranchId", cursor?.BranchId ?? Guid.Empty),
                new SqlParameter("@cursorFactKind", cursor is null ? 0 : (int)cursor.FactKind),
                new SqlParameter("@cursorOrdinal", cursor?.Ordinal ?? -1))
            .ToArrayAsync(cancellationToken);
    }

    private bool AddSearchSymbol(SearchResultAccumulator group, SearchCandidate candidate)
    {
        var value = ProjectSymbols(
        [
            new SourceProcessorCodeSymbolEntity
            {
                DocumentId = candidate.BranchId,
                Ordinal = candidate.Ordinal,
                DeclarationKindCode = candidate.DeclarationKindCode,
                LocalName = candidate.LocalName!,
                QualifiedName = candidate.QualifiedName!,
                RenderedSignature = candidate.RenderedSignature!,
                Modifiers = candidate.Modifiers!,
                LexicalParentOrdinal = candidate.LexicalParentOrdinal,
                SpanStartUtf16 = candidate.SpanStartUtf16,
                SpanLengthUtf16 = candidate.SpanLengthUtf16
            }
        ]).Values;
        if (value.Length == 0)
        {
            return false;
        }

        group.Symbols.Add(value[0]);
        return true;
    }

    private bool AddSearchReference(SearchResultAccumulator group, SearchCandidate candidate)
    {
        var value = ProjectReferences(
        [
            new SourceProcessorCodeReferenceEntity
            {
                DocumentId = candidate.BranchId,
                Ordinal = candidate.Ordinal,
                RelationshipKindCode = candidate.RelationshipKindCode,
                SourceSymbolOrdinal = candidate.SourceSymbolOrdinal,
                TargetDisplay = candidate.TargetDisplay!,
                SpanStartUtf16 = candidate.SpanStartUtf16,
                SpanLengthUtf16 = candidate.SpanLengthUtf16
            }
        ]).Values;
        if (value.Length == 0)
        {
            return false;
        }

        group.References.Add(value[0]);
        return true;
    }

    private static FactPage<SourceProcessorCodeSymbolEntity> TakePage(SourceProcessorCodeSymbolEntity[] values) =>
        TakePage(values, value => value.Ordinal);

    private static FactPage<SourceProcessorCodeReferenceEntity> TakePage(SourceProcessorCodeReferenceEntity[] values) =>
        TakePage(values, value => value.Ordinal);

    private static FactPage<SourceProcessorCodeDiagnosticEntity> TakePage(SourceProcessorCodeDiagnosticEntity[] values) =>
        TakePage(values, value => value.Ordinal);

    private static FactPage<SourceProcessorCodeBlockedDiagnosticEntity> TakePage(SourceProcessorCodeBlockedDiagnosticEntity[] values) =>
        TakePage(values, value => value.Ordinal);

    private static FactPage<T> TakePage<T>(T[] values, Func<T, int> ordinal)
    {
        var hasMore = values.Length > MaximumFacts;
        var pageValues = hasMore ? values[..MaximumFacts] : values;
        return new FactPage<T>(pageValues, hasMore ? ordinal(pageValues[^1]) : null);
    }

    private (LocalRetainedCsharpSymbolProjection[] Values, int WithheldCount) ProjectSymbols(
        IReadOnlyList<SourceProcessorCodeSymbolEntity> symbols)
    {
        var values = new List<LocalRetainedCsharpSymbolProjection>(symbols.Count);
        var withheld = 0;
        foreach (var symbol in symbols)
        {
            var decision = disclosure.Evaluate(
                string.Concat(symbol.LocalName, '\n', symbol.QualifiedName, '\n', symbol.RenderedSignature, '\n', symbol.Modifiers),
                LocalDisclosureKind.Symbol);
            if (decision.Withheld)
            {
                withheld++;
                continue;
            }

            values.Add(new LocalRetainedCsharpSymbolProjection(
                symbol.Ordinal,
                symbol.DeclarationKindCode,
                symbol.LocalName,
                symbol.QualifiedName,
                symbol.RenderedSignature,
                symbol.Modifiers,
                symbol.LexicalParentOrdinal,
                symbol.SpanStartUtf16,
                symbol.SpanLengthUtf16));
        }

        return (values.ToArray(), withheld);
    }

    private (LocalRetainedCsharpReferenceProjection[] Values, int WithheldCount) ProjectReferences(
        IReadOnlyList<SourceProcessorCodeReferenceEntity> references)
    {
        var values = new List<LocalRetainedCsharpReferenceProjection>(references.Count);
        var withheld = 0;
        foreach (var reference in references)
        {
            if (disclosure.Evaluate(reference.TargetDisplay, LocalDisclosureKind.Reference).Withheld)
            {
                withheld++;
                continue;
            }

            values.Add(new LocalRetainedCsharpReferenceProjection(
                reference.Ordinal,
                reference.RelationshipKindCode,
                reference.SourceSymbolOrdinal,
                reference.TargetDisplay,
                reference.SpanStartUtf16,
                reference.SpanLengthUtf16));
        }

        return (values.ToArray(), withheld);
    }

    private (LocalRetainedCsharpDiagnosticProjection[] Values, int WithheldCount) ProjectDiagnostics(
        IReadOnlyList<SourceProcessorCodeDiagnosticEntity> diagnostics,
        bool blocked) =>
        ProjectDiagnostics(
            diagnostics.Select(value => new PersistedDiagnostic(
                value.Ordinal,
                value.DiagnosticId,
                value.Severity,
                value.SpanStartUtf16,
                value.SpanLengthUtf16,
                value.Representation,
                value.ScannedMessage,
                value.WithheldReason)),
            blocked);

    private (LocalRetainedCsharpDiagnosticProjection[] Values, int WithheldCount) ProjectBlockedDiagnostics(
        IReadOnlyList<SourceProcessorCodeBlockedDiagnosticEntity> diagnostics) =>
        ProjectDiagnostics(
            diagnostics.Select(value => new PersistedDiagnostic(
                value.Ordinal,
                value.DiagnosticId,
                value.Severity,
                value.SpanStartUtf16,
                value.SpanLengthUtf16,
                value.Representation,
                value.ScannedMessage,
                value.WithheldReason)),
            true);

    private (LocalRetainedCsharpDiagnosticProjection[] Values, int WithheldCount) ProjectDiagnostics(
        IEnumerable<PersistedDiagnostic> diagnostics,
        bool blocked)
    {
        var values = new List<LocalRetainedCsharpDiagnosticProjection>();
        var withheld = 0;
        foreach (var diagnostic in diagnostics)
        {
            if (string.Equals(diagnostic.Representation, "withheld", StringComparison.Ordinal))
            {
                if (!string.Equals(diagnostic.WithheldReason, "secret-content-withheld", StringComparison.Ordinal) ||
                    diagnostic.ScannedMessage is not null)
                {
                    throw new InvalidDataException("The retained C# diagnostic withholding record is invalid.");
                }

                values.Add(new LocalRetainedCsharpDiagnosticProjection(
                    diagnostic.Ordinal,
                    diagnostic.DiagnosticId,
                    diagnostic.Severity,
                    diagnostic.SpanStartUtf16,
                    diagnostic.SpanLengthUtf16,
                    null,
                    true,
                    diagnostic.WithheldReason,
                    blocked));
                continue;
            }

            if (!string.Equals(diagnostic.Representation, "scanned", StringComparison.Ordinal) ||
                diagnostic.ScannedMessage is null || diagnostic.WithheldReason is not null)
            {
                throw new InvalidDataException("The retained C# diagnostic representation is invalid.");
            }

            var decision = disclosure.Evaluate(diagnostic.ScannedMessage, LocalDisclosureKind.Diagnostic);
            if (decision.Withheld)
            {
                withheld++;
                values.Add(new LocalRetainedCsharpDiagnosticProjection(
                    diagnostic.Ordinal,
                    diagnostic.DiagnosticId,
                    diagnostic.Severity,
                    diagnostic.SpanStartUtf16,
                    diagnostic.SpanLengthUtf16,
                    null,
                    true,
                    "secret-content-withheld",
                    blocked));
                continue;
            }

            values.Add(new LocalRetainedCsharpDiagnosticProjection(
                diagnostic.Ordinal,
                diagnostic.DiagnosticId,
                diagnostic.Severity,
                diagnostic.SpanStartUtf16,
                diagnostic.SpanLengthUtf16,
                decision.Value,
                false,
                null,
                blocked));
        }

        return (values.ToArray(), withheld);
    }

    private sealed record PersistedDiagnostic(
        int Ordinal,
        string DiagnosticId,
        byte Severity,
        int SpanStartUtf16,
        int SpanLengthUtf16,
        string Representation,
        string? ScannedMessage,
        string? WithheldReason);

    private sealed record FactPage<T>(T[] Values, int? NextOrdinal);

    private sealed record DurableCsharpBinding(
        SourceProcessorCodeCompletionReceiptEntity Receipt,
        Guid SourceRevisionId,
        string InputSha256,
        SourceProcessorCodeDocumentEntity? Document);

    private sealed record SearchVerifiedDetail(
        string LocalPath,
        string ArtifactHash,
        string OutcomeCode);

    private sealed class SearchCandidate
    {
        public Guid BranchId { get; init; }
        public int FactKind { get; init; }
        public int Ordinal { get; init; }
        public int DeclarationKindCode { get; init; }
        public string? LocalName { get; init; }
        public string? QualifiedName { get; init; }
        public string? RenderedSignature { get; init; }
        public string? Modifiers { get; init; }
        public int LexicalParentOrdinal { get; init; }
        public int RelationshipKindCode { get; init; }
        public int? SourceSymbolOrdinal { get; init; }
        public string? TargetDisplay { get; init; }
        public int SpanStartUtf16 { get; init; }
        public int SpanLengthUtf16 { get; init; }
    }

    private sealed class SearchResultAccumulator(Guid branchId, string localPath, string artifactHash)
    {
        public List<LocalRetainedCsharpSymbolProjection> Symbols { get; } = [];
        public List<LocalRetainedCsharpReferenceProjection> References { get; } = [];

        public LocalRetainedCsharpCodeSearchProjection ToProjection() =>
            new(branchId, localPath, artifactHash, Symbols)
            {
                References = References
            };
    }
}
