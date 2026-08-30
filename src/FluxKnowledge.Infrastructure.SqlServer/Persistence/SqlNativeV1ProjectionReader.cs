using System.Text.Json;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Code;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.IntegrationV1.Operations;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>SQL-backed, retained-only native v1 projection boundary. It never opens an original source path.</summary>
public sealed class SqlNativeV1ProjectionReader(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    ILocalPrivateContentDisclosure disclosure,
    ILocalRetainedDetailReader retainedDetails,
    INativeV1CursorCodec cursorCodec) : INativeV1ProjectionReader
{
    public async ValueTask<object> ReadCorpusAsync(NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return query.View switch
        {
            "roots" => await ReadRootsAsync(context, query, cancellationToken).ConfigureAwait(false),
            "branches" => await ReadBranchesAsync(context, query, cancellationToken).ConfigureAwait(false),
            "processors" => await ReadProcessorsAsync(context, query, cancellationToken).ConfigureAwait(false),
            "jobs" => await ReadJobsAsync(context, query, cancellationToken).ConfigureAwait(false),
            "assets" => await ReadAssetsAsync(context, query, cancellationToken).ConfigureAwait(false),
            "detail" => await ReadBranchDetailAsync(context, retainedDetails, query.BranchId, cancellationToken).ConfigureAwait(false),
            _ => throw new NativeOperationException("view-not-allowed")
        };
    }

    public async ValueTask<object> ReadCodeAsync(NativeCodeQuery query, CancellationToken cancellationToken)
    {
        var canonicalQuery = query.Query is null
            ? null
            : NativeV1ContractLimits.CanonicalizeOptionalCodeQuery(query.Query);
        query = query with { Query = canonicalQuery };
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (query.View == "status")
        {
            return new { completed = await context.SourceProcessorCodeCompletionReceipts.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false), branches = await context.SourceProcessorBranches.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false) };
        }
        if (query.View == "symbols")
        {
            var source = context.SourceProcessorCodeSymbols.AsNoTracking()
                .Where(value => query.BranchId == null || value.DocumentId == query.BranchId);
            if (query.Continuation is { Id: Guid afterDocumentId, Ordinal: int afterOrdinal })
            {
                source = source.Where(value => value.DocumentId.CompareTo(afterDocumentId) > 0 ||
                    (value.DocumentId == afterDocumentId && value.Ordinal > afterOrdinal));
            }
            var symbols = await source.OrderBy(value => value.DocumentId).ThenBy(value => value.Ordinal).Take(query.Limit + 1)
                .Select(value => new { value.DocumentId, value.Ordinal, value.DeclarationKindCode, value.QualifiedName, value.RenderedSignature }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return CodePage(query, symbols);
        }
        var needle = NativeV1ContractLimits.CanonicalizeCodeQuery(query.Query);
        var matchesSource = context.SourceProcessorCodeSymbols.AsNoTracking()
            .Where(value => value.QualifiedName.Contains(needle) && (query.BranchId == null || value.DocumentId == query.BranchId));
        if (query.Continuation is { Id: Guid afterMatchDocumentId, Ordinal: int afterMatchOrdinal })
        {
            matchesSource = matchesSource.Where(value => value.DocumentId.CompareTo(afterMatchDocumentId) > 0 ||
                (value.DocumentId == afterMatchDocumentId && value.Ordinal > afterMatchOrdinal));
        }
        var matches = await matchesSource.OrderBy(value => value.DocumentId).ThenBy(value => value.Ordinal).Take(query.Limit + 1)
            .Select(value => new { value.DocumentId, value.Ordinal, value.DeclarationKindCode, value.QualifiedName, value.RenderedSignature }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return CodePage(query, matches);
    }

    public async ValueTask<object> ReadStatusAsync(NativeOperationsStatus query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return query.View switch
        {
            "overview" => new { persistence = "available", sources = await context.SourceRootConfigurations.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false), jobs = await context.SourceScanJobs.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false), capabilities = new[] { new { name = "native-v1", state = "available", reasonCode = (string?)null } } },
            "sources" => new { items = (await context.SourceRootConfigurations.AsNoTracking().OrderBy(value => value.Id).Take(query.Limit).Select(value => new { value.Id, value.DisplayName, value.State, value.LastScanCompletedAtUtc }).ToArrayAsync(cancellationToken).ConfigureAwait(false)).Select(value => new { value.Id, displayName = Safe(value.DisplayName, LocalDisclosureKind.CorpusMetadata), value.State, value.LastScanCompletedAtUtc }).ToArray() },
            "jobs" => new { items = await context.SourceScanJobs.AsNoTracking().OrderByDescending(value => value.UpdatedAtUtc).Take(query.Limit).Select(value => new { value.Id, value.State, value.AttemptCount, value.DueAtUtc }).ToArrayAsync(cancellationToken).ConfigureAwait(false) },
            "workers" => new { items = await context.NativeWorkerInstances.AsNoTracking().OrderByDescending(value => value.LastHeartbeatAtUtc).Take(query.Limit).Select(value => new { value.InstanceId, value.State, value.LastHeartbeatAtUtc }).ToArrayAsync(cancellationToken).ConfigureAwait(false) },
            "processors" => new { items = await context.SourceCapabilities.AsNoTracking().OrderBy(value => value.ProcessorKind).Take(query.Limit).Select(value => new { value.ProcessorKind, value.ProcessorVersion, value.IsRunnable }).ToArrayAsync(cancellationToken).ConfigureAwait(false) },
            "recovery" => new { activeGeneration = await context.IndexState.AsNoTracking().Select(value => value.ActiveIndexGenerationId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false), state = "available" },
            _ => throw new NativeOperationException("view-not-allowed")
        };
    }

    public async ValueTask<object> ReadAuditAsync(NativeAuditQuery query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var source = context.AuditEvents.AsNoTracking()
            .Where(value => (query.RootId == null || value.SourceRootId == query.RootId) &&
                (query.JobId == null || value.SourceScanRequestId == query.JobId));
        if (query.Continuation is { Timestamp: DateTimeOffset afterOccurredAt, Sequence: long afterId })
        {
            source = source.Where(value => value.OccurredAtUtc < afterOccurredAt ||
                (value.OccurredAtUtc == afterOccurredAt && value.Id < afterId));
        }
        var events = await source.OrderByDescending(value => value.OccurredAtUtc).ThenByDescending(value => value.Id).Take(query.Limit + 1)
            .Select(value => new { value.Id, value.OccurredAtUtc, value.EventType, value.EventFamily, value.Severity, value.SourceRootId, value.SourceScanRequestId, value.DetailsJson }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var page = events.Take(query.Limit).ToArray();
        var nextCursor = events.Length > query.Limit
            ? cursorCodec.Encode(
                NativeV1CursorBindings.Audit(query),
                new NativeV1CursorPosition(Timestamp: page[^1].OccurredAtUtc, Sequence: page[^1].Id))
            : null;
        return new
        {
            items = page.Select(value => new { value.Id, value.OccurredAtUtc, value.EventType, value.EventFamily, value.Severity, value.SourceRootId, value.SourceScanRequestId, details = Safe(value.DetailsJson, LocalDisclosureKind.AuditEvidence) }).ToArray(),
            nextCursor
        };
    }

    private async ValueTask<object> ReadRootsAsync(FluxKnowledgeDbContext context, NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        var source = context.SourceRootConfigurations.AsNoTracking()
            .Where(value => query.RootId == null || value.Id == query.RootId);
        if (query.Continuation is { Id: Guid afterId })
        {
            source = source.Where(value => value.Id.CompareTo(afterId) > 0);
        }
        var values = await source.OrderBy(value => value.Id).Take(query.Limit + 1)
            .Select(value => new { value.Id, value.DisplayName, value.State, value.ConfigurationRevision, value.LastScanCompletedAtUtc })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var page = values.Take(query.Limit).ToArray();
        return new
        {
            items = page.Select(value => new
            {
                value.Id,
                displayName = Safe(value.DisplayName, LocalDisclosureKind.CorpusMetadata),
                value.State,
                value.ConfigurationRevision,
                value.LastScanCompletedAtUtc
            }).ToArray(),
            nextCursor = values.Length > query.Limit
                ? cursorCodec.Encode(NativeV1CursorBindings.Corpus(query), new NativeV1CursorPosition(page[^1].Id))
                : null
        };
    }

    private async ValueTask<object> ReadAssetsAsync(FluxKnowledgeDbContext context, NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        var source = context.SourceRevisions.AsNoTracking()
            .Where(value => query.RootId == null || value.SourceRootId == query.RootId);
        if (query.Continuation is { Id: Guid afterId, Timestamp: DateTimeOffset afterDiscoveredAt })
        {
            source = source.Where(value => value.DiscoveredAtUtc < afterDiscoveredAt ||
                (value.DiscoveredAtUtc == afterDiscoveredAt && value.Id.CompareTo(afterId) < 0));
        }
        var values = await source.OrderByDescending(value => value.DiscoveredAtUtc).ThenByDescending(value => value.Id).Take(query.Limit + 1)
            .Select(value => new { value.Id, value.ContentSha256, value.ByteLength, value.DiscoveredAtUtc, value.SourceRootId })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return CorpusPage(query, values, value => new NativeV1CursorPosition(value.Id, value.DiscoveredAtUtc));
    }

    private async ValueTask<object> ReadBranchesAsync(FluxKnowledgeDbContext context, NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        var source = context.SourceProcessorBranches.AsNoTracking()
            .Where(value => query.BranchId == null || value.Id == query.BranchId)
            .Where(value => query.RootId == null || context.SourceRevisions.Any(revision =>
                revision.Id == value.SourceRevisionId && revision.SourceRootId == query.RootId));
        if (query.Continuation is { Id: Guid afterId, Timestamp: DateTimeOffset afterUpdatedAt })
        {
            source = source.Where(value => value.UpdatedAtUtc < afterUpdatedAt ||
                (value.UpdatedAtUtc == afterUpdatedAt && value.Id.CompareTo(afterId) < 0));
        }
        var values = await source.OrderByDescending(value => value.UpdatedAtUtc).ThenByDescending(value => value.Id).Take(query.Limit + 1)
            .Select(value => new { value.Id, value.SourceRevisionId, value.State, value.AttemptCount, value.UpdatedAtUtc, value.CompletionReceiptFingerprint })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return CorpusPage(query, values, value => new NativeV1CursorPosition(value.Id, value.UpdatedAtUtc));
    }

    private async ValueTask<object> ReadProcessorsAsync(FluxKnowledgeDbContext context, NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        var source = context.SourceCapabilities.AsNoTracking();
        if (query.Continuation is { Id: Guid afterId })
        {
            source = source.Where(value => value.Id.CompareTo(afterId) > 0);
        }
        var values = await source.OrderBy(value => value.Id).Take(query.Limit + 1)
            .Select(value => new { value.Id, value.ProcessorKind, value.ProcessorVersion, value.IsRunnable, value.RegisteredAtUtc })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return CorpusPage(query, values, value => new NativeV1CursorPosition(value.Id));
    }

    private async ValueTask<object> ReadJobsAsync(FluxKnowledgeDbContext context, NativeCorpusQuery query, CancellationToken cancellationToken)
    {
        var source = context.SourceScanJobs.AsNoTracking()
            .Where(value => query.JobId == null || value.Id == query.JobId)
            .Where(value => query.RootId == null || value.SourceScanRequest.SourceRootId == query.RootId);
        if (query.Continuation is { Id: Guid afterId, Timestamp: DateTimeOffset afterUpdatedAt })
        {
            source = source.Where(value => value.UpdatedAtUtc < afterUpdatedAt ||
                (value.UpdatedAtUtc == afterUpdatedAt && value.Id.CompareTo(afterId) < 0));
        }
        var values = await source.OrderByDescending(value => value.UpdatedAtUtc).ThenByDescending(value => value.Id).Take(query.Limit + 1)
            .Select(value => new { value.Id, value.State, value.DueAtUtc, value.AttemptCount, value.LeaseGeneration, value.UpdatedAtUtc })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return CorpusPage(query, values, value => new NativeV1CursorPosition(value.Id, value.UpdatedAtUtc));
    }

    private object CorpusPage<T>(NativeCorpusQuery query, T[] values, Func<T, NativeV1CursorPosition> position)
    {
        var page = values.Take(query.Limit).ToArray();
        return new
        {
            items = page,
            nextCursor = values.Length > query.Limit
                ? cursorCodec.Encode(NativeV1CursorBindings.Corpus(query), position(page[^1]))
                : null
        };
    }

    private object Safe(string value, LocalDisclosureKind kind)
    {
        var result = disclosure.Evaluate(value, kind);
        return result.Withheld ? new { withheld = true, reasonCode = result.ReasonCode } : new { withheld = false, value = result.Value };
    }

    private object Symbol(dynamic value) => new
    {
        value.DocumentId,
        value.Ordinal,
        value.DeclarationKindCode,
        qualifiedName = Safe(value.QualifiedName, LocalDisclosureKind.Symbol),
        renderedSignature = Safe(value.RenderedSignature, LocalDisclosureKind.Symbol)
    };

    private object CodePage<T>(NativeCodeQuery query, T[] values) where T : class
    {
        var page = values.Take(query.Limit).ToArray();
        var last = page.LastOrDefault();
        return new
        {
            items = page.Select(Symbol).ToArray(),
            nextCursor = values.Length > query.Limit && last is not null
                ? cursorCodec.Encode(
                    NativeV1CursorBindings.Code(query),
                    new NativeV1CursorPosition((Guid)((dynamic)last).DocumentId, Ordinal: (int)((dynamic)last).Ordinal))
                : null
        };
    }

    private static async ValueTask<object> ReadBranchDetailAsync(FluxKnowledgeDbContext context, ILocalRetainedDetailReader retainedDetails, Guid? branchId, CancellationToken cancellationToken)
    {
        if (branchId is null) throw new NativeOperationException("invalid-query");
        var branch = await context.SourceProcessorBranches.AsNoTracking().Where(value => value.Id == branchId).Select(value => new { value.Id, value.SourceRevisionId, value.InputSha256, value.State, value.AttemptCount, value.CompletionReceiptFingerprint }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (branch is null) return new { reasonCode = "retained-branch-not-found" };
        try
        {
            var retained = await retainedDetails.ReadAsync(branch.Id, cancellationToken).ConfigureAwait(false);
            return retained is null ? new { reasonCode = "retained-branch-not-found" } : new { item = branch, provenance = new { retained.ArtifactHash, retained.ArtifactByteLength } };
        }
        catch (FileNotFoundException) { return new { reasonCode = "retained-artifact-missing" }; }
        catch (InvalidDataException) { return new { reasonCode = "retained-artifact-checksum-invalid" }; }
    }
}
