using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlNativeCorpusActionStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    ISourceRootPathPolicy sourceRootPathPolicy,
    ILocalPrivateContentDisclosure disclosure) : INativeCorpusActionStore
{
    public async ValueTask<IReadOnlyList<NativeTargetVersion>> ResolveTargetsAsync(string action, string canonicalPayload, CancellationToken cancellationToken)
    {
        ValidatePayload(action, canonicalPayload);
        if (action == "root_create")
        {
            var admission = RootAdmission(canonicalPayload);
            return [new NativeTargetVersion(CanonicalPathTargetId(admission.CanonicalPath), "absent")];
        }
        var id = RequiredGuid(canonicalPayload, action == "job_retry" ? "jobId" : "rootId");
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (action == "job_retry")
        {
            var job = await context.SourceScanJobs.AsNoTracking().Where(value => value.Id == id).Select(value => new { value.Id, value.RowVersion, value.SourceScanRequestId }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (job is null || job.RowVersion.Length != 8) throw new NativeOperationException("target-not-found");
            var request = await context.SourceScanRequests.AsNoTracking().Where(value => value.Id == job.SourceScanRequestId).Select(value => new { value.Id, value.RowVersion }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var outbox = await context.SourceScanOutbox.AsNoTracking().Where(value => value.SourceScanRequestId == job.SourceScanRequestId).Select(value => new { value.Id, value.RowVersion }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (request is null || outbox is null || request.RowVersion.Length != 8 || outbox.RowVersion.Length != 8) throw new NativeOperationException("target-not-found");
            return [new NativeTargetVersion($"job:{job.Id:D}", Convert.ToBase64String(job.RowVersion)), new NativeTargetVersion($"request:{request.Id:D}", Convert.ToBase64String(request.RowVersion)), new NativeTargetVersion($"outbox:{outbox.Id:D}", Convert.ToBase64String(outbox.RowVersion))];
        }
        var version = await context.SourceRootConfigurations.AsNoTracking().Where(value => value.Id == id).Select(value => value.RowVersion).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (version is null || version.Length != 8) throw new NativeOperationException("target-not-found");
        var targets = new List<NativeTargetVersion> { new($"root:{id:D}", Convert.ToBase64String(version)) };
        var watch = await context.SourceRootWatchStates.AsNoTracking().Where(value => value.SourceRootId == id).Select(value => new { value.SourceRootId, value.RowVersion }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (action is "watcher_set" or "root_disable")
        {
            targets.Add(watch is null
                ? new NativeTargetVersion($"watch:{id:D}", "absent")
                : new NativeTargetVersion($"watch:{watch.SourceRootId:D}", Convert.ToBase64String(watch.RowVersion)));
            return targets;
        }
        if (action is "root_update" or "source_sync")
        {
            targets.AddRange(await ResolveActiveControlTargetsAsync(context, id, cancellationToken).ConfigureAwait(false));
        }
        return targets;
    }

    public ValueTask<NativeActionCommitOperation> CreateCommitOperationAsync(string action, string canonicalPayload, IReadOnlyList<NativeTargetVersion> targets, CancellationToken cancellationToken)
    {
        ValidatePayload(action, canonicalPayload);
        return ValueTask.FromResult<NativeActionCommitOperation>(new NativeCorpusMutationCommitOperation(action, canonicalPayload, action == "root_create" ? RootAdmission(canonicalPayload) : null));
    }

    private SourceRootPathValidation RootAdmission(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("path", out var path) || string.IsNullOrWhiteSpace(path.GetString()) || !root.TryGetProperty("displayName", out var displayName) || string.IsNullOrWhiteSpace(displayName.GetString())) throw new NativeOperationException("invalid-payload");
        try
        {
            return sourceRootPathPolicy.ValidateAndCanonicalise(new SourceRootCreateRequest(path.GetString()!, displayName.GetString()!, OptionalBool(root, "recursive", true), [], [], OptionalBool(root, "followLinks", false), OptionalLong(root, "maximumFileBytes", 16L * 1024 * 1024), [], TimeSpan.FromSeconds(OptionalLong(root, "reconciliationSeconds", 900)), "native-v1"));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            throw new NativeOperationException("source-root-path-not-allowed");
        }
    }

    private static async Task<IReadOnlyList<NativeTargetVersion>> ResolveActiveControlTargetsAsync(
        FluxKnowledgeDbContext context,
        Guid rootId,
        CancellationToken cancellationToken)
    {
        var requests = await context.SourceScanRequests.AsNoTracking()
            .Where(value => value.SourceRootId == rootId &&
                (value.State == (int)SourceScanRequestState.Held ||
                 value.State == (int)SourceScanRequestState.Released ||
                 value.State == (int)SourceScanRequestState.Running))
            .OrderBy(value => value.RequestedAtUtc).ThenBy(value => value.Id)
            .Select(value => new ActiveControlRequest(value.Id, value.RowVersion))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (requests.Length == 0)
        {
            return [new NativeTargetVersion($"active-controls:{rootId:D}", "absent")];
        }

        if (requests.Length > 40)
        {
            throw new NativeOperationException("operation-conflict");
        }

        var requestIds = requests.Select(value => value.Id).ToArray();
        var jobs = await context.SourceScanJobs.AsNoTracking()
            .Where(value => requestIds.Contains(value.SourceScanRequestId))
            .Select(value => new ActiveControlChild(value.SourceScanRequestId, value.Id, value.RowVersion))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var outbox = await context.SourceScanOutbox.AsNoTracking()
            .Where(value => requestIds.Contains(value.SourceScanRequestId))
            .Select(value => new ActiveControlChild(value.SourceScanRequestId, value.Id, value.RowVersion))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (jobs.Length != requests.Length || outbox.Length != requests.Length ||
            requests.Any(value => value.RowVersion.Length != 8) || jobs.Any(value => value.RowVersion.Length != 8) || outbox.Any(value => value.RowVersion.Length != 8))
        {
            throw new NativeOperationException("target-not-found");
        }

        var targets = new List<NativeTargetVersion>(1 + (requests.Length * 3));
        foreach (var request in requests)
        {
            var job = jobs.Single(value => value.SourceScanRequestId == request.Id);
            var dispatch = outbox.Single(value => value.SourceScanRequestId == request.Id);
            targets.Add(new NativeTargetVersion($"request:{request.Id:D}", Convert.ToBase64String(request.RowVersion)));
            targets.Add(new NativeTargetVersion($"job:{job.Id:D}", Convert.ToBase64String(job.RowVersion)));
            targets.Add(new NativeTargetVersion($"outbox:{dispatch.Id:D}", Convert.ToBase64String(dispatch.RowVersion)));
        }

        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            NativeOperationCanonicalization.SerializeTargets(NativeOperationCanonicalization.CanonicalizeTargets(targets)))));
        targets.Add(new NativeTargetVersion($"active-controls:{rootId:D}", signature));
        return targets;
    }

    private static string CanonicalPathTargetId(string canonicalPath) =>
        $"root-path:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)))}";

    private static Guid RequiredGuid(string payload, string property)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty(property, out var element) && Guid.TryParse(element.GetString(), out var id) ? id : throw new NativeOperationException("invalid-payload");
    }

    private void ValidatePayload(string action, string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new NativeOperationException("invalid-payload");
            var allowed = action switch
            {
                "root_create" => new[] { "path", "displayName", "recursive", "followLinks", "maximumFileBytes", "reconciliationSeconds" },
                "root_update" => new[] { "rootId", "displayName" },
                "root_disable" or "source_sync" => new[] { "rootId" },
                "watcher_set" => new[] { "rootId", "enabled" },
                "job_retry" => new[] { "jobId" },
                _ => throw new NativeOperationException("action-not-allowed")
            };
            if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal))) throw new NativeOperationException("invalid-payload");
            switch (action)
            {
                case "root_create":
                    _ = RequiredString(root, "path", 2048); RequireSafeDisplayName(RequiredString(root, "displayName", 256));
                    _ = OptionalBool(root, "recursive", true); _ = OptionalBool(root, "followLinks", false);
                    _ = OptionalLong(root, "maximumFileBytes", 16L * 1024 * 1024); _ = OptionalLong(root, "reconciliationSeconds", 900);
                    break;
                case "root_update": _ = RequiredGuid(root, "rootId"); RequireSafeDisplayName(RequiredString(root, "displayName", 256)); break;
                case "root_disable": case "source_sync": _ = RequiredGuid(root, "rootId"); break;
                case "watcher_set": _ = RequiredGuid(root, "rootId"); if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new NativeOperationException("invalid-payload"); break;
                case "job_retry": _ = RequiredGuid(root, "jobId"); break;
            }
        }
        catch (JsonException) { throw new NativeOperationException("invalid-payload"); }
    }

    private void RequireSafeDisplayName(string displayName)
    {
        var result = disclosure.Evaluate(displayName, LocalDisclosureKind.CorpusMetadata);
        if (result.Withheld)
        {
            throw new NativeOperationException(result.ReasonCode ?? "secret-content-withheld");
        }
    }

    private static string RequiredString(JsonElement root, string name, int maximum) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maximum ? text : throw new NativeOperationException("invalid-payload");
    private static Guid RequiredGuid(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : throw new NativeOperationException("invalid-payload");
    private static bool OptionalBool(JsonElement root, string name, bool defaultValue) => !root.TryGetProperty(name, out var value) ? defaultValue : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new NativeOperationException("invalid-payload");
    private static long OptionalLong(JsonElement root, string name, long defaultValue) => !root.TryGetProperty(name, out var value) ? defaultValue : value.TryGetInt64(out var number) && number is >= 1 and <= 1_073_741_824 ? number : throw new NativeOperationException("invalid-payload");

    private sealed record ActiveControlRequest(Guid Id, byte[] RowVersion);
    private sealed record ActiveControlChild(Guid SourceScanRequestId, Guid Id, byte[] RowVersion);
}
