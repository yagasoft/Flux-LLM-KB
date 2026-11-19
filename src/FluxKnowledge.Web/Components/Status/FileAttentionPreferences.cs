using System.Data;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Web.Components.OperatorActions;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web.Components.Status;

public interface IFileAttentionPreferences
{
    bool CanManage { get; }
    ValueTask SetFileIgnoredAsync(Guid sourceRootId, Guid sourceRevisionId, bool ignored, string? expectedVersion,
        CancellationToken cancellationToken);
    ValueTask SetRuleIgnoredAsync(Guid? sourceRootId, PipelineAttentionRuleKind kind, string? reason, bool ignored,
        string? expectedVersion, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<PipelineAttentionRule>> ReadRulesAsync(Guid? sourceRootId, CancellationToken cancellationToken);
}

/// <summary>Local operator preferences for the attention projection, using existing versioned native state.</summary>
public sealed class FileAttentionPreferences(IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    ILocalOperatorPolicy operatorPolicy, IStatusEventPublisher events, TimeProvider clock,
    IDeploymentValidationHold? validationHold = null) : IFileAttentionPreferences
{
    private const string FilePrefix = "file-attention:v1:file:";
    private const string RulePrefix = "file-attention:v1:rule:";

    public bool CanManage => operatorPolicy.CanManage;

    public ValueTask SetFileIgnoredAsync(Guid sourceRootId, Guid sourceRevisionId, bool ignored, string? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (sourceRootId == Guid.Empty || sourceRevisionId == Guid.Empty)
            throw new ArgumentException("Choose a current file and source.");
        return SetAsync(FileKey(sourceRootId, sourceRevisionId), ignored, expectedVersion, null,
            async (context, token) =>
            {
                if (!await IsCurrentBindingAsync(context, sourceRootId, sourceRevisionId, token).ConfigureAwait(false))
                    throw new InvalidOperationException("This file issue has changed. Refresh the file list.");
            }, cancellationToken);
    }

    public ValueTask SetRuleIgnoredAsync(Guid? sourceRootId, PipelineAttentionRuleKind kind, string? reason, bool ignored,
        string? expectedVersion, CancellationToken cancellationToken)
    {
        if (sourceRootId == Guid.Empty || kind switch
            {
                PipelineAttentionRuleKind.Binary => reason is not null,
                PipelineAttentionRuleKind.Reason => !FileAttentionIssue.IsValidRuleReason(reason),
                _ => true
            })
            throw new ArgumentException("Choose a recognised issue category or a recorded reason.");
        return SetAsync(RuleKey(sourceRootId, kind, reason), ignored, expectedVersion, reason,
            async (context, token) =>
            {
                if (sourceRootId is { } root && !await context.SourceRootConfigurations.AsNoTracking()
                    .AnyAsync(source => source.Id == root && source.State != (int)SourceRootState.Deleting, token).ConfigureAwait(false))
                    throw new InvalidOperationException("That source is no longer available. Refresh source status.");
            }, cancellationToken, rule: true);
    }

    public async ValueTask<IReadOnlyList<PipelineAttentionRule>> ReadRulesAsync(Guid? sourceRootId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await ReadRulesAsync(context, sourceRootId, cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(rule => rule.SourceRootId.HasValue).ThenBy(rule => rule.SourceRootId)
            .ThenBy(rule => rule.Label, StringComparer.Ordinal).ThenBy(rule => rule.Reason, StringComparer.Ordinal).ToArray();
    }

    private async ValueTask SetAsync(string key, bool ignored, string? expectedVersion, string? reason,
        Func<FluxKnowledgeDbContext, CancellationToken, Task> validateTarget, CancellationToken cancellationToken, bool rule = false)
    {
        await operatorPolicy.EnsureMutationAllowedAsync(cancellationToken).ConfigureAwait(false);
        EnsureAdmitted();
        var expected = DecodeVersion(expectedVersion);
        await using var execution = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var changed = await execution.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            var state = await context.NativeOperationFenceTargets.FromSqlInterpolated($"""
                SELECT * FROM [NativeOperationFenceTargets] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetId] = {key}
                """).SingleOrDefaultAsync(token).ConfigureAwait(false);
            var desired = (ignored ? "1" : "0") + reason;
            if (state?.Value == desired || state is null && !ignored && expected is null) return false;

            // A rule selector is only exposed for a new rule when no active rule
            // applies to that target and scope. An inactive rule can be re-added.
            var ruleInactive = rule && expected is null && state?.Value == "0" + reason;
            if (!ruleInactive && (state is null ? expected is not null : expected is null || !state.RowVersion.SequenceEqual(expected)))
                throw new InvalidOperationException("The attention preference changed. Refresh the file list and try again.");
            if (ignored) await validateTarget(context, token).ConfigureAwait(false);
            EnsureAdmitted();

            if (state is null)
                context.NativeOperationFenceTargets.Add(new NativeOperationFenceTargetEntity { TargetId = key, Value = desired });
            else
                state.Value = desired;

            await context.SaveChangesAsync(token).ConfigureAwait(false);
            try
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
            catch (System.Data.Common.DbException exception)
            {
                // An unacknowledged commit may already be durable. Retry can
                // overwrite an intervening remove/re-add of the same rule.
                throw new InvalidOperationException("The attention preference commit was not confirmed. Refresh the file list.", exception);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (changed)
            await events.PublishAsync(new StatusChanged(null, "file-attention", clock.GetUtcNow()), CancellationToken.None)
                .ConfigureAwait(false);
    }

    private static async Task<bool> IsCurrentBindingAsync(FluxKnowledgeDbContext context, Guid root, Guid revisionId,
        CancellationToken cancellationToken)
    {
        // A preference binds exact retained identity only. The attention read
        // independently checks whether current outcomes and claims can be hidden.
        var current = context.SourceRevisions.AsNoTracking().Where(revision => revision.SourceRootId == root &&
            revision.SuppressedAtUtc == null && (revision.OriginKind == 0 ||
                revision.OriginKind == 1 && revision.ParentSourceRevision != null &&
                revision.ParentSourceRevision.SourceRootId == root && revision.ParentSourceRevision.OriginKind == 0 &&
                revision.ParentSourceRevision.SuppressedAtUtc == null));
        var chosen = await current.Where(revision => revision.Id == revisionId &&
                revision.SourceRoot.State != (int)SourceRootState.Deleting)
            .Select(revision => new { revision.StableSourceIdentity, revision.CanonicalPath, revision.OriginKind })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (chosen is null) return false;

        var key = FileBindingPath(chosen.OriginKind, chosen.CanonicalPath);
        var peers = await current.Where(revision => revision.Id != revisionId &&
            (revision.StableSourceIdentity == chosen.StableSourceIdentity ||
             EF.Functions.Collate(revision.CanonicalPath, "Latin1_General_100_CI_AS_KS_WS") == chosen.CanonicalPath ||
             revision.CanonicalPath.ToUpper() == key))
            .Select(revision => new { revision.StableSourceIdentity, revision.CanonicalPath, revision.OriginKind })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return peers.All(peer => peer.StableSourceIdentity != chosen.StableSourceIdentity &&
            FileBindingPath(peer.OriginKind, peer.CanonicalPath) != key);
    }

    // Physical path bindings are case-insensitive. Opaque archive-member identities are exact.
    private static string FileBindingPath(int originKind, string path) => originKind == 0 ? path.ToUpperInvariant() : path;

    private void EnsureAdmitted()
    {
        if (validationHold?.ReadAdmissionState().IsHeld == true)
            throw new InvalidOperationException("A deployment validation hold is active. Refresh status before changing attention preferences.");
    }

    internal static async Task<IReadOnlyList<PipelineFileSample>> ApplyAsync(FluxKnowledgeDbContext context,
        IReadOnlyList<PipelineFileSample> files, CancellationToken cancellationToken)
    {
        var roots = files.Where(file => file.File.CanIgnore).Select(file => file.File.SourceRootId).ToHashSet();
        if (roots.Count == 0) return files;
        var onlyRoot = roots.Count == 1 ? roots.Single() : (Guid?)null;
        var rules = await ReadRulesAsync(context, onlyRoot, cancellationToken).ConfigureAwait(false);
        var ruleKeys = rules.Select(rule => (rule.SourceRootId, rule.Kind, rule.Reason)).ToHashSet();
        var filePrefix = onlyRoot is { } root ? FilePrefix + root.ToString("N") + ":" : FilePrefix;
        var heads = await context.NativeOperationFenceTargets.AsNoTracking().Where(state => state.TargetId.StartsWith(filePrefix))
            .ToDictionaryAsync(state => state.TargetId, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        if (rules.Length == 0 && heads.Count == 0) return files;

        return files.Select(sample =>
        {
            var file = sample.File;
            if (!file.CanIgnore) return sample;
            var own = heads.GetValueOrDefault(FileKey(file.SourceRootId, file.SourceRevisionId));
            var individual = own?.Value == "1";
            bool HasRule(PipelineAttentionRuleKind kind, string? reason) =>
                ruleKeys.Contains((null, kind, reason)) || ruleKeys.Contains((file.SourceRootId, kind, reason));
            var byRule = file.IsBinaryIssue && HasRule(PipelineAttentionRuleKind.Binary, null) ||
                sample.RecordedReasons.Count > 0 && sample.RecordedReasons.All(reason => HasRule(PipelineAttentionRuleKind.Reason, reason));
            return sample with { File = file with
            {
                IsIndividuallyIgnored = individual,
                IsIgnored = individual || byRule,
                IgnoreVersion = own is null ? null : Convert.ToBase64String(own.RowVersion)
            } };
        }).ToArray();
    }

    private static async Task<PipelineAttentionRule[]> ReadRulesAsync(FluxKnowledgeDbContext context, Guid? sourceRootId,
        CancellationToken cancellationToken)
    {
        var query = context.NativeOperationFenceTargets.AsNoTracking()
            .Where(state => state.TargetId.StartsWith(RulePrefix) && state.Value.StartsWith("1"));
        if (sourceRootId is { } root)
        {
            var sourcePrefix = RulePrefix + root.ToString("N") + ":";
            var globalPrefix = RulePrefix + "all:";
            query = query.Where(state => state.TargetId.StartsWith(sourcePrefix) || state.TargetId.StartsWith(globalPrefix));
        }
        var rows = await query.ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ParseRule).OfType<PipelineAttentionRule>().ToArray();
    }

    private static PipelineAttentionRule? ParseRule(NativeOperationFenceTargetEntity state)
    {
        var pieces = state.TargetId[RulePrefix.Length..].Split(':');
        if (pieces.Length != 3) return null;
        Guid? root = pieces[0] == "all" ? null : Guid.TryParseExact(pieces[0], "N", out var id) ? id : Guid.Empty;
        if (root == Guid.Empty) return null;
        var version = Convert.ToBase64String(state.RowVersion);
        if (pieces[1] == "binary" && state.Value == "1" && state.TargetId == RuleKey(root, PipelineAttentionRuleKind.Binary, null))
            return new(root, PipelineAttentionRuleKind.Binary, null, "Binary files", version);
        var reason = state.Value[1..];
        if (pieces[1] == "reason" && FileAttentionIssue.IsValidRuleReason(reason) &&
            state.TargetId == RuleKey(root, PipelineAttentionRuleKind.Reason, reason))
            return new(root, PipelineAttentionRuleKind.Reason, reason, FileAttentionIssue.Label(reason), version);
        return null;
    }

    private static string FileKey(Guid root, Guid revision) => $"{FilePrefix}{root:N}:{revision:N}";

    private static string RuleKey(Guid? root, PipelineAttentionRuleKind kind, string? reason)
    {
        var scope = root?.ToString("N") ?? "all";
        // The selector is persisted verbatim; hashing only bounds the SQL key and
        // keeps the full reason case sensitive, including for a case-insensitive DB.
        return kind == PipelineAttentionRuleKind.Binary
            ? $"{RulePrefix}{scope}:binary:1"
            : $"{RulePrefix}{scope}:reason:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reason!)))}";
    }

    private static byte[]? DecodeVersion(string? version)
    {
        if (version is null) return null;
        Span<byte> result = stackalloc byte[8];
        if (!Convert.TryFromBase64String(version, result, out var written) || written != result.Length)
            throw new ArgumentException("The attention preference version is invalid.", nameof(version));
        return result.ToArray();
    }
}
