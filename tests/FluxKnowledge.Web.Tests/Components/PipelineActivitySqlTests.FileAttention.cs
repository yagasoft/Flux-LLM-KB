using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.OperatorActions;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed partial class PipelineActivitySqlTests
{
    [NativeSqlServerFact]
    public async Task Attention_rules_and_file_preferences_are_reversible_without_changing_inventory()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var otherRoot = await AddActivityRootAsync(now);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var binary = Guid.NewGuid();
        var otherBinary = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var otherFailed = Guid.NewGuid();
        var provisional = Guid.NewGuid();
        await using (var setup = factory.CreateDbContext())
        {
            Add(binary, root, "sample.bin", "Unknown", SourceActivityState.DeferredUnsupported, "binary-format-parser-unavailable");
            Add(otherBinary, otherRoot, "other.bin", "Unknown", SourceActivityState.DeferredUnsupported, "binary-format-parser-unavailable");
            Add(otherFailed, otherRoot, "other.pdf", "PdfDocumentContainer", SourceActivityState.FailedTerminal, "pdf-document-container-invalid");
            Add(failed, root, "failed.pdf", "PdfDocumentContainer", SourceActivityState.FailedTerminal, "pdf-document-container-invalid");
            Add(Guid.NewGuid(), root, "legacy.y", "AcceptedUtf8Text", SourceActivityState.DeferredPolicy, "File extension is unknown to local source ingestion.");
            Add(provisional, root, "uncertain.bin", "Unknown", SourceActivityState.DeferredUnsupported, "binary-format-parser-unavailable");
            var conflicting = Revision(Guid.NewGuid(), root, "UNCERTAIN.bin", now, false);
            conflicting.ContentSha256 = new string('b', 64);
            conflicting.Revision = 2;
            setup.SourceRevisions.Add(conflicting);
            await setup.SaveChangesAsync();

            void Add(Guid revision, Guid owner, string name, string classification, SourceActivityState state, string reason)
            {
                var file = Revision(revision, owner, name, now, false);
                file.Classification = classification;
                setup.SourceRevisions.Add(file);
                var activity = Activity(revision, state);
                activity.Reason = reason;
                setup.SourceActivities.Add(activity);
            }
        }

        var reader = SnapshotReader();
        var active = new PipelineFileFilter(Attention: PipelineFileAttentionScope.Current);
        var ignored = new PipelineFileFilter(Attention: PipelineFileAttentionScope.Ignored);
        var before = await reader.ReadPipelineFilesAsync(active, root, 0, default);
        Assert.Equal(4, before.TotalCount);
        Assert.False(Assert.Single(before.Items, item => item.Category == "Unconfirmed").CanIgnore);
        Assert.False(Assert.Single(before.Items, item => item.FullPath.EndsWith("legacy.y", StringComparison.Ordinal)).IsBinaryIssue);
        byte[] beforeVersion;
        await using (var check = factory.CreateDbContext())
            beforeVersion = (await check.SourceRevisions.AsNoTracking().SingleAsync(file => file.Id == binary)).RowVersion;

        var events = new StatusEventFeed();
        var denied = new FileAttentionPreferences(factory, new TestLocalOperator(false), events, TimeProvider.System);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.SetFileIgnoredAsync(root, binary, true, null, default).AsTask());
        Assert.Empty((await reader.ReadPipelineFilesAsync(ignored, root, 0, default)).Items);
        var preferences = new FileAttentionPreferences(factory, new TestLocalOperator(), events, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => preferences.SetFileIgnoredAsync(otherRoot, provisional, true, null, default).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => preferences.SetFileIgnoredAsync(root, provisional, true, null, default).AsTask());
        Assert.Contains((await reader.ReadPipelineFilesAsync(active, root, 0, default)).Items, item => item.Category == "Unconfirmed");
        await preferences.SetFileIgnoredAsync(root, binary, true, null, default);
        await preferences.SetRuleIgnoredAsync(root, PipelineAttentionRuleKind.Binary, null, true, null, default);
        var reopened = new FileAttentionPreferences(factory, new TestLocalOperator(), events, TimeProvider.System);
        var ignoredPage = await reader.ReadPipelineFilesAsync(ignored, root, 0, default);
        Assert.Equal(2, ignoredPage.TotalCount);
        var ignoredBinary = Assert.Single(ignoredPage.Items, item => item.SourceRevisionId == binary);
        Assert.True(ignoredBinary.IsIndividuallyIgnored);
        Assert.False(Assert.Single(ignoredPage.Items, item => item.SourceRevisionId == failed).IsIndividuallyIgnored);
        Assert.Equal(2, (await reader.ReadPipelineFilesAsync(active, root, 0, default)).TotalCount);
        Assert.Contains((await reader.ReadPipelineFilesAsync(active, otherRoot, 0, default)).Items, item => item.SourceRevisionId == otherBinary);
        Assert.Equal(4, (await reader.ReadPipelineFilesAsync(new(), root, 0, default)).TotalCount);
        var summary = await reader.ReadOverviewAsync(default);
        Assert.Equal(new PipelineFileAttentionCounts(root, 1, 0, 0, 1, 2), Assert.Single(summary.Activity.FileAttention, counts => counts.SourceRootId == root));
        Assert.Equal(4, Assert.Single(summary.Activity.Sources, source => source.Id == root).KnownFiles);
        Assert.DoesNotContain(summary.Activity.FileCauses, cause => cause.SourceRootId == root && cause.Reason == "pdf-document-container-invalid");

        await reopened.SetFileIgnoredAsync(root, binary, false, ignoredBinary.IgnoreVersion, default);
        var underRule = Assert.Single((await reader.ReadPipelineFilesAsync(ignored, root, 0, default)).Items, item => item.SourceRevisionId == binary);
        Assert.False(underRule.IsIndividuallyIgnored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SetFileIgnoredAsync(root, binary, true, ignoredBinary.IgnoreVersion, default).AsTask());
        var rule = Assert.Single(await reopened.ReadRulesAsync(root, default), rule => rule.Kind == PipelineAttentionRuleKind.Binary);
        await reopened.SetRuleIgnoredAsync(root, rule.Kind, rule.Reason, false, rule.Version, default);
        Assert.Equal(0, (await reader.ReadPipelineFilesAsync(ignored, root, 0, default)).TotalCount);
        Assert.Equal(4, (await reader.ReadPipelineFilesAsync(active, root, 0, default)).TotalCount);
        await reopened.SetRuleIgnoredAsync(null, PipelineAttentionRuleKind.Reason, "pdf-document-container-invalid", true, null, default);
        var global = Assert.Single(await reopened.ReadRulesAsync(otherRoot, default));
        Assert.Null(global.SourceRootId);
        Assert.Equal(otherFailed, Assert.Single((await reader.ReadPipelineFilesAsync(ignored, otherRoot, 0, default)).Items).SourceRevisionId);
        await reopened.SetRuleIgnoredAsync(null, global.Kind, global.Reason, false, global.Version, default);
        Assert.Equal(0, (await reader.ReadPipelineFilesAsync(ignored, otherRoot, 0, default)).TotalCount);
        await reopened.SetRuleIgnoredAsync(root, rule.Kind, rule.Reason, true, null, default);
        Assert.Equal(2, (await reader.ReadPipelineFilesAsync(ignored, root, 0, default)).TotalCount);
        await using (var check = factory.CreateDbContext())
            Assert.Equal(beforeVersion, (await check.SourceRevisions.AsNoTracking().SingleAsync(file => file.Id == binary)).RowVersion);
    }

    [NativeSqlServerFact]
    public async Task File_reason_counts_cover_the_whole_scope_before_reason_filter_and_paging()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var otherRoot = await AddActivityRootAsync(now);
        await using (var setup = new TestDbContextFactory(_fixture.ConnectionString).CreateDbContext())
        {
            for (var index = 0; index < 34; index++)
            {
                var id = Guid.NewGuid();
                var file = Revision(id, index == 33 ? otherRoot : root, $"issue-{index:D2}.txt", now, false);
                if (index == 29) file.Classification = "PdfDocumentContainer";
                setup.SourceRevisions.Add(file);
                var activity = Activity(id, SourceActivityState.FailedTerminal);
                activity.Reason = index < 30 ? "pdf-document-container-invalid" : "office-document-xml-invalid";
                setup.SourceActivities.Add(activity);
            }
            await setup.SaveChangesAsync();
        }

        var reader = SnapshotReader();
        var first = await reader.ReadPipelineFilesAsync(new("Failed"), root, 0, default);
        Assert.Equal(33, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.DoesNotContain(first.Items, file => file.IsBinaryIssue);
        Assert.True(first.HasBinaryIssues);
        Assert.Equal(30, Assert.Single(first.Reasons, group => group.Reason == "pdf-document-container-invalid").Count);
        Assert.Equal(3, Assert.Single(first.Reasons, group => group.Reason == "office-document-xml-invalid").Count);
        var filtered = await reader.ReadPipelineFilesAsync(new("Failed", Reason: "office-document-xml-invalid"), root, 1, default);
        Assert.Equal(3, filtered.TotalCount);
        Assert.Equal(0, filtered.Page);
        Assert.False(filtered.HasBinaryIssues);
        Assert.Equal(first.Reasons, filtered.Reasons);
        Assert.All(filtered.Items, item => Assert.Equal("office-document-xml-invalid", item.Reason));
    }

    [NativeSqlServerFact]
    public async Task Ignoring_one_file_does_not_wait_for_another_files_activity_in_the_same_source()
    {
        await EnableOwnedSnapshotAsync();
        var now = DateTimeOffset.UtcNow;
        var root = await AddActivityRootAsync(now);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var chosen = Revision(Guid.NewGuid(), root, "selected.txt", now, false);
        var unrelated = Revision(Guid.NewGuid(), root, "unrelated.txt", now, false);
        var selectedActivity = Activity(chosen.Id, SourceActivityState.FailedTerminal);
        var unrelatedActivity = Activity(unrelated.Id, SourceActivityState.FailedTerminal);
        await using (var seed = factory.CreateDbContext())
        {
            seed.SourceRevisions.AddRange(chosen, unrelated);
            seed.SourceActivities.AddRange(selectedActivity, unrelatedActivity);
            await seed.SaveChangesAsync();
        }

        var preferences = new FileAttentionPreferences(factory, new TestLocalOperator(), new StatusEventFeed(), TimeProvider.System);
        await using (var writer = factory.CreateDbContext())
        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            await writer.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [SourceActivities] SET [UpdatedAtUtc] = {now} WHERE [Id] = {unrelatedActivity.Id}
                """);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await preferences.SetFileIgnoredAsync(root, chosen.Id, true, null, timeout.Token);
            await transaction.RollbackAsync();
        }

        var page = await SnapshotReader().ReadPipelineFilesAsync(new(Attention: PipelineFileAttentionScope.Ignored), root, 0, default);
        Assert.Equal(chosen.Id, Assert.Single(page.Items).SourceRevisionId);
    }

    private sealed class TestLocalOperator(bool enabled = true) : ILocalOperatorPolicy
    {
        public bool CanManage => enabled;
        public ValueTask EnsureMutationAllowedAsync(CancellationToken cancellationToken) =>
            enabled ? ValueTask.CompletedTask : ValueTask.FromException(new UnauthorizedAccessException());
    }
}
