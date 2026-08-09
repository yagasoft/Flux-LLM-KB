using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class CorpusProjectionIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task Corpus_page_includes_direct_and_source_backed_records_without_counting_an_offer_as_indexed()
    {
        var now = DateTimeOffset.UtcNow; var direct = Guid.NewGuid(); var source = Guid.NewGuid(); var revision = Guid.NewGuid(); var root = Guid.NewGuid();
        await using (var db = Context())
        {
            db.SourceRootConfigurations.Add(Root(root, now));
            db.SourceIdentities.AddRange(Identity(direct, "direct"), Identity(source, "source"));
            db.SourceRevisions.Add(new SourceRevisionEntity { Id = revision, SourceRootId = root, StableSourceIdentity = "source", Revision = 1, ContentSha256 = new string('a',64), CanonicalPath = "C:\\corpus\\deferred.txt", Classification = "DeferredCapability", Extension = ".txt", DiscoveredAtUtc = now });
            var sourceRecord = Record(source, source, now); sourceRecord.SourceRevisionId = revision;
            db.PipelineRecords.AddRange(Record(direct, direct, now), sourceRecord);
            db.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = revision, ActivityKind = 1, ExecutionClass = 1, ProcessorVersion = "v1", InputFingerprint = new string('a',64), State = (int)SourceActivityState.DeferredUnsupported, CreatedAtUtc = now, UpdatedAtUtc = now });
            await db.SaveChangesAsync();
        }
        var page = await new SqlCorpusProjectionReader(Factory()).ReadPageAsync(new CorpusQuery(PageSize: 50), CancellationToken.None);
        Assert.Contains(page.Items, item => item.Location == "Direct"); Assert.Contains(page.Items, item => item.SourceActivityState == "Deferred"); Assert.DoesNotContain(page.Items, item => item.SourceActivityState == "Indexed" && item.ResultingPipelineRecordId is null && item.SourceRevisionId is not null);
    }

    [NativeSqlServerFact]
    public async Task Activity_state_uses_the_activity_linked_to_the_record_when_a_revision_has_multiple_activities()
    {
        var now = DateTimeOffset.UtcNow; var root = Guid.NewGuid(); var identity = Guid.NewGuid(); var otherIdentity = Guid.NewGuid(); var revision = Guid.NewGuid(); var record = Guid.NewGuid(); var other = Guid.NewGuid();
        await using (var db = Context())
        {
            db.SourceRootConfigurations.Add(Root(root, now)); db.SourceIdentities.AddRange(Identity(identity, "source"), Identity(otherIdentity, "other"));
            db.SourceRevisions.Add(new SourceRevisionEntity { Id = revision, SourceRootId = root, StableSourceIdentity = "source", Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = "C:\\corpus\\entry.txt", Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = now });
            var sourceRecord = Record(record, identity, now); sourceRecord.SourceRevisionId = revision; db.PipelineRecords.AddRange(sourceRecord, Record(other, otherIdentity, now));
            db.SourceActivities.AddRange(Activity(revision, SourceActivityState.Completed, other, now.AddMinutes(1)), Activity(revision, SourceActivityState.Completed, record, now));
            await db.SaveChangesAsync();
        }
        var page = await new SqlCorpusProjectionReader(Factory()).ReadPageAsync(new CorpusQuery(), CancellationToken.None);
        Assert.Equal("Indexed", Assert.Single(page.Items, item => item.PipelineRecordId == record).SourceActivityState);
    }

    [NativeSqlServerFact]
    public async Task Detail_preview_is_bounded_and_related_events_use_the_sql_long_identity()
    {
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); await using (var db = Context()) { db.SourceIdentities.Add(Identity(id,"direct")); db.PipelineRecords.Add(Record(id,id,now)); db.Artifacts.Add(new ArtifactEntity { Id=Guid.NewGuid(), PipelineRecordId=id, SourceRevision=1, Stage=0, ContentHash=new string('b',64), ContentType="text/plain", SearchText=new string('x',9000), CreatedAtUtc=now }); db.AuditEvents.Add(new AuditEventEntity { PipelineRecordId=id, EventType="pipeline.created", Actor="test", DetailsJson="{}", OccurredAtUtc=now }); await db.SaveChangesAsync(); }
        var detail = await new SqlCorpusProjectionReader(Factory()).ReadDetailAsync(id, CancellationToken.None);
        Assert.NotNull(detail); Assert.Null(detail!.IndexedTextPreview); Assert.All(detail.RelatedEventIds, value => Assert.True(value > 0));
    }

    [NativeSqlServerFact]
    public async Task Detail_preview_uses_only_the_linked_current_eligible_text_artifact()
    {
        var now = DateTimeOffset.UtcNow; var root = Guid.NewGuid(); var identity = Guid.NewGuid(); var revision = Guid.NewGuid(); var record = Guid.NewGuid();
        await using (var db = Context())
        {
            db.SourceRootConfigurations.Add(Root(root, now));
            db.SourceIdentities.Add(Identity(identity, "C:\\corpus\\private-name.txt"));
            db.SourceRevisions.Add(new SourceRevisionEntity { Id = revision, SourceRootId = root, StableSourceIdentity = "private-name", Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = "C:\\corpus\\safe\\entry.txt", Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = now });
            var sourceRecord = Record(record, identity, now); sourceRecord.SourceRevisionId = revision;
            db.PipelineRecords.Add(sourceRecord);
            db.SourceActivities.Add(Activity(revision, SourceActivityState.Completed, record, now));
            var historical = Record(Guid.NewGuid(), identity, now); historical.Revision = 2;
            db.PipelineRecords.Add(historical);
            db.Artifacts.AddRange(
                new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = record, SourceRevision = 1, Stage = 0, ContentHash = new string('b', 64), ContentType = "text/plain", SearchText = new string('x', 9_000), CreatedAtUtc = now },
                new ArtifactEntity { Id = Guid.NewGuid(), PipelineRecordId = historical.Id, SourceRevision = 2, Stage = 0, ContentHash = new string('c', 64), ContentType = "text/plain", SearchText = "wrong revision", CreatedAtUtc = now.AddMinutes(1) });
            await db.SaveChangesAsync();
        }
        var detail = await new SqlCorpusProjectionReader(Factory()).ReadDetailAsync(record, CancellationToken.None);
        Assert.NotNull(detail); Assert.Equal(8_192, detail!.IndexedTextPreview!.Length); Assert.Equal("safe\\entry.txt", detail.Entry); Assert.Equal("private-name.txt", detail.SourceIdentity);
    }

    [NativeSqlServerFact]
    public async Task Full_text_candidates_remain_subject_to_filters_and_cursor_keyset()
    {
        var now = DateTimeOffset.UtcNow; var first = Guid.NewGuid(); var second = Guid.NewGuid(); var excluded = Guid.NewGuid();
        await using (var db = Context())
        {
            db.SourceIdentities.AddRange(Identity(first, "first"), Identity(second, "second"), Identity(excluded, "excluded"));
            var deleted = Record(excluded, excluded, now); deleted.IsDeleted = true;
            db.PipelineRecords.AddRange(Record(first, first, now), Record(second, second, now), deleted);
            db.Artifacts.AddRange(Artifact(first, now, "needle one"), Artifact(second, now, "needle two"), Artifact(excluded, now, "needle excluded"));
            db.AuditEvents.AddRange(new AuditEventEntity { PipelineRecordId = first, EventType = "pipeline.created", Actor = "test", DetailsJson = "{}", OccurredAtUtc = now }, new AuditEventEntity { PipelineRecordId = second, EventType = "pipeline.created", Actor = "test", DetailsJson = "{}", OccurredAtUtc = now });
            await db.SaveChangesAsync();
        }
        var reader = new SqlCorpusProjectionReader(Factory()); var firstPage = await reader.ReadPageAsync(new CorpusQuery(new CorpusFilters(Search: "needle"), PageSize: 1), CancellationToken.None);
        Assert.Single(firstPage.Items); Assert.NotNull(firstPage.NextCursor); Assert.DoesNotContain(firstPage.Items, item => item.PipelineRecordId == excluded);
        var secondPage = await reader.ReadPageAsync(new CorpusQuery(new CorpusFilters(Search: "needle"), PageSize: 1, Cursor: firstPage.NextCursor), CancellationToken.None);
        Assert.Single(secondPage.Items); Assert.NotEqual(firstPage.Items[0].PipelineRecordId, secondPage.Items[0].PipelineRecordId);
    }

    [NativeSqlServerFact]
    public async Task Folders_are_root_relative_stable_without_original_files_and_count_latest_activity_without_sibling_leaks()
    {
        var now = DateTimeOffset.UtcNow; var root = Guid.NewGuid(); var indexed = Guid.NewGuid(); var deferred = Guid.NewGuid(); var blocked = Guid.NewGuid(); var failed = Guid.NewGuid(); var sibling = Guid.NewGuid();
        await using (var db = Context())
        {
            db.SourceRootConfigurations.Add(Root(root, now));
            foreach (var pair in new[] { (indexed, "C:\\corpus\\alpha\\one.txt"), (deferred, "C:\\corpus\\alpha\\two.pdf"), (blocked, "C:\\corpus\\alpha\\nested\\three.txt"), (failed, "C:\\corpus\\alpha\\nested\\four.txt"), (sibling, "C:\\corpus\\alphabet\\leak.txt") }) db.SourceRevisions.Add(new SourceRevisionEntity { Id=pair.Item1, SourceRootId=root, StableSourceIdentity=pair.Item1.ToString("N"), Revision=1, ContentSha256=new string('d',64), CanonicalPath=pair.Item2, Classification="AcceptedUtf8Text", Extension=".txt", DiscoveredAtUtc=now });
            db.SourceActivities.AddRange(Activity(indexed, SourceActivityState.Completed, Guid.NewGuid(), now), Activity(deferred, SourceActivityState.DeferredUnsupported, null, now), Activity(blocked, SourceActivityState.DeferredPolicy, null, now), Activity(failed, SourceActivityState.FailedTerminal, null, now), Activity(sibling, SourceActivityState.DeferredUnsupported, null, now));
            await db.SaveChangesAsync();
        }
        var reader = new SqlCorpusProjectionReader(Factory()); var top = await reader.ReadFoldersAsync(root, null, CancellationToken.None); var alpha = Assert.Single(top, folder => folder.RelativePath == "alpha");
        Assert.Equal("alpha", alpha.RelativePath); Assert.Equal(1, alpha.CurrentCount); Assert.Equal(1, alpha.DeferredCount); Assert.Equal(1, alpha.BlockedCount); Assert.Equal(1, alpha.FailedCount);
        var nested = Assert.Single(await reader.ReadFoldersAsync(root, "alpha", CancellationToken.None)); Assert.Equal("alpha\\nested", nested.RelativePath); Assert.Equal(1, nested.BlockedCount); Assert.Equal(1, nested.FailedCount);
    }

    [NativeSqlServerFact]
    public async Task Selected_folder_emits_only_its_direct_child_with_the_selected_path_once()
    {
        var now = DateTimeOffset.UtcNow; var root = Guid.NewGuid();
        await using (var db = Context()) { db.SourceRootConfigurations.Add(Root(root, now)); db.SourceRevisions.Add(Revision(root, "C:\\corpus\\foo\\bar\\entry.txt", now)); await db.SaveChangesAsync(); }
        var folder = Assert.Single(await new SqlCorpusProjectionReader(Factory()).ReadFoldersAsync(root, "foo", CancellationToken.None));
        Assert.Equal("foo\\bar", folder.RelativePath);
    }

    [NativeSqlServerFact]
    public async Task Source_locations_include_the_safe_root_display_name_when_folders_match()
    {
        var now = DateTimeOffset.UtcNow; var firstRoot = Guid.NewGuid(); var secondRoot = Guid.NewGuid(); var firstIdentity = Guid.NewGuid(); var secondIdentity = Guid.NewGuid(); var firstRevision = Guid.NewGuid(); var secondRevision = Guid.NewGuid(); var firstRecord = Guid.NewGuid(); var secondRecord = Guid.NewGuid();
        await using (var db = Context())
        {
            var firstRootEntity = Root(firstRoot, now); firstRootEntity.DisplayName = "First root"; var secondRootEntity = Root(secondRoot, now); secondRootEntity.DisplayName = "Second root"; secondRootEntity.CanonicalPath = "D:\\other";
            db.SourceRootConfigurations.AddRange(firstRootEntity, secondRootEntity); db.SourceIdentities.AddRange(Identity(firstIdentity, "first"), Identity(secondIdentity, "second"));
            db.SourceRevisions.AddRange(Revision(firstRoot, "C:\\corpus\\shared\\entry.txt", now, firstRevision), Revision(secondRoot, "D:\\other\\shared\\entry.txt", now, secondRevision));
            var first = Record(firstRecord, firstIdentity, now); first.SourceRevisionId = firstRevision; var second = Record(secondRecord, secondIdentity, now); second.SourceRevisionId = secondRevision; db.PipelineRecords.AddRange(first, second); await db.SaveChangesAsync();
        }
        var entries = (await new SqlCorpusProjectionReader(Factory()).ReadPageAsync(new CorpusQuery(), CancellationToken.None)).Items;
        Assert.Equal("First root\\shared", Assert.Single(entries, entry => entry.PipelineRecordId == firstRecord).Location);
        Assert.Equal("Second root\\shared", Assert.Single(entries, entry => entry.PipelineRecordId == secondRecord).Location);
        Assert.DoesNotContain(entries, entry => entry.Location.Contains("C:\\") || entry.Location.Contains("D:\\"));
    }

    [NativeSqlServerFact]
    public async Task Folder_segment_matching_treats_like_metacharacters_as_literals()
    {
        var now = DateTimeOffset.UtcNow; var root = Guid.NewGuid();
        await using (var db = Context()) { db.SourceRootConfigurations.Add(Root(root, now)); db.SourceRevisions.AddRange(
            Revision(root, "C:\\corpus\\a%\\nested\\one.txt", now), Revision(root, "C:\\corpus\\abc\\nested\\two.txt", now)); await db.SaveChangesAsync(); }
        var rows = await new SqlCorpusProjectionReader(Factory()).ReadFoldersAsync(root, "a%", CancellationToken.None);
        var row = Assert.Single(rows); Assert.Equal("a%\\nested", row.RelativePath); Assert.Equal(1, row.CurrentCount);
    }

    private FluxKnowledgeDbContext Context() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);
    private IDbContextFactory<FluxKnowledgeDbContext> Factory() => SqlTestData.CreateFactory(fixture);
    private static SourceIdentityEntity Identity(Guid id, string key) => new() { Id=id, SourceKind="local file", StableKey=key, CreatedAtUtc=DateTimeOffset.UtcNow };
    private static PipelineRecordEntity Record(Guid id, Guid identity, DateTimeOffset now) => new() { Id=id, SourceIdentityId=identity, Revision=1, ContentHash=new string('c',64), RootLineageRecordId=id, RegisteredAtUtc=now };
    private static SourceActivityEntity Activity(Guid revision, SourceActivityState state, Guid? record, DateTimeOffset now) => new() { Id=Guid.NewGuid(), SourceRevisionId=revision, ActivityKind=1, ExecutionClass=1, ProcessorVersion="v1", InputFingerprint=(record?.ToString("N") ?? "pending").PadRight(64, 'd'), State=(int)state, ResultingPipelineRecordId=record, CreatedAtUtc=now, UpdatedAtUtc=now };
    private static SourceRootConfigurationEntity Root(Guid id, DateTimeOffset now) => new() { Id=id, CanonicalPath="C:\\corpus", DisplayName="Corpus", State=(int)SourceRootState.Enabled, Recursive=true, IncludePatternsJson="[]", ExcludePatternsJson="[]", MaximumFileBytes=1024, AllowedClassificationsJson="[]", ReconciliationCadenceSeconds=900, CreatedAtUtc=now, UpdatedAtUtc=now };
    private static SourceRevisionEntity Revision(Guid root, string path, DateTimeOffset now, Guid? id = null) => new() { Id = id ?? Guid.NewGuid(), SourceRootId = root, StableSourceIdentity = Guid.NewGuid().ToString("N"), Revision = 1, ContentSha256 = new string('a', 64), CanonicalPath = path, Classification = "AcceptedUtf8Text", Extension = ".txt", DiscoveredAtUtc = now };
    private static ArtifactEntity Artifact(Guid record, DateTimeOffset now, string text) => new() { Id = Guid.NewGuid(), PipelineRecordId = record, SourceRevision = 1, Stage = 0, ContentHash = new string('b', 64), ContentType = "text/plain", SearchText = text, CreatedAtUtc = now };
}
