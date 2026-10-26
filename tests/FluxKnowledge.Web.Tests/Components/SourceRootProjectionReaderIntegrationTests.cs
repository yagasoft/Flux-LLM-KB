using System.Data.Common;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Components.Status;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class SourceRootProjectionReaderIntegrationTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Summary_publication_times_preserve_exact_record_binding_and_document_precedence()
    {
        var now = DateTimeOffset.UtcNow;
        var root = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        (string Name, PipelineStage? Stage, bool Complete, bool Deleted, long? Revision, bool OtherSource, int Publication, DateTimeOffset? Expected)[] cases =
        [
            ("publish", PipelineStage.Publish, true, false, 1, false, 0, now),
            ("no-artifact", (PipelineStage?)null, true, false, (long?)1, false, 0, (DateTimeOffset?)null),
            ("other-stage", PipelineStage.Extract, true, false, (long?)1, false, 0, (DateTimeOffset?)null),
            ("incomplete", PipelineStage.Publish, false, false, (long?)1, false, 0, (DateTimeOffset?)null),
            ("deleted", PipelineStage.Publish, true, true, (long?)1, false, 0, (DateTimeOffset?)null),
            ("unversioned-activity", PipelineStage.Publish, true, false, (long?)null, false, 0, (DateTimeOffset?)now),
            ("wrong-source", PipelineStage.Publish, true, false, (long?)1, true, 0, (DateTimeOffset?)null),
            ("valid-document", PipelineStage.Publish, true, false, (long?)1, false, 1, (DateTimeOffset?)now.AddMinutes(-10)),
            ("stale-document", PipelineStage.Publish, true, false, (long?)1, false, 2, (DateTimeOffset?)now)
        ];
        var expected = new Dictionary<Guid, SourceLogicalFileState>();
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"I:\\publication-summary\\{root:N}", DisplayName = "Publication summary",
                State = (int)SourceRootState.Enabled, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            foreach (var item in cases)
            {
                var source = Revision(Guid.NewGuid(), root, item.Name + ".txt", now, false);
                setup.SourceRevisions.Add(source);
                var record = AddRecord(source.Id, item.Complete, item.Deleted);
                if (item.OtherSource)
                {
                    var other = Revision(Guid.NewGuid(), root, "suppressed-other.txt", now, true);
                    setup.SourceRevisions.Add(other);
                    record.SourceRevisionId = other.Id;
                }
                var activity = Activity(source.Id, SourceActivityState.Completed, record.Id);
                activity.ResultingPipelineRecordRevision = item.Revision;
                activity.DescriptorFingerprint = new string('b', 64);
                setup.SourceActivities.Add(activity);
                // Multiple activities/branches must not multiply the logical file.
                foreach (var fingerprint in new[] { 'b', 'c' })
                {
                    var branchActivity = activity;
                    if (fingerprint == 'c')
                    {
                        branchActivity = Activity(source.Id, SourceActivityState.Completed, record.Id);
                        branchActivity.ResultingPipelineRecordRevision = item.Revision;
                        branchActivity.DescriptorFingerprint = new string('c', 64);
                        setup.SourceActivities.Add(branchActivity);
                    }
                    setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
                    {
                        Id = Guid.NewGuid(), SourceRevisionId = source.Id, SourceActivityId = branchActivity.Id,
                        InputSha256 = source.ContentSha256, ProcessorVersion = "summary-test", ProcessorFingerprint = new string(fingerprint, 64),
                        State = (int)RetainedProcessorBranchState.Completed, CreatedAtUtc = now, UpdatedAtUtc = now
                    });
                }
                if (item.Stage is { } stage)
                    setup.Artifacts.Add(new ArtifactEntity
                    {
                        Id = Guid.NewGuid(), PipelineRecordId = record.Id, SourceRevision = 1, Stage = (int)stage,
                        ContentHash = record.ContentHash, ContentType = "text/plain", SearchText = "test", CreatedAtUtc = now
                    });
                if (item.Publication != 0)
                {
                    var input = Revision(Guid.NewGuid(), root, item.Name + "-input.txt", now, false);
                    input.OriginKind = 2; input.ParentSourceRevisionId = source.Id;
                    setup.SourceRevisions.Add(input);
                    var published = AddRecord(input.Id, item.Publication == 1, false);
                    setup.DocumentPublications.Add(new DocumentPublicationEntity
                    {
                        OwnerSourceRevisionId = source.Id, DocumentInputSourceRevisionId = input.Id,
                        SourceProcessorBranchId = setup.SourceProcessorBranches.Local.First(branch => branch.SourceRevisionId == source.Id).Id,
                        PipelineRecordId = published.Id, PipelineRecordRevision = 1, ProcessorFingerprint = new string('b', 64),
                        PublishedAtUtc = now.AddMinutes(-10)
                    });
                }
                var selectedBranch = item.Publication == 0 ? (Guid?)null
                    : setup.SourceProcessorBranches.Local.First(branch => branch.SourceRevisionId == source.Id).Id;
                var category = item.Complete && !item.Deleted && !item.OtherSource ? "Published" : "Pending";
                expected.Add(source.Id, new(root, source.Id, source.CanonicalPath, null, category, null,
                    selectedBranch, item.Expected, source.Classification));
            }
            await setup.SaveChangesAsync();

            PipelineRecordEntity AddRecord(Guid source, bool complete, bool deleted)
            {
                var identity = new SourceIdentityEntity { Id = Guid.NewGuid(), SourceKind = "summary-test", StableKey = Guid.NewGuid().ToString("N"), CreatedAtUtc = now };
                var record = new PipelineRecordEntity
                {
                    Id = Guid.NewGuid(), SourceIdentityId = identity.Id, SourceRevisionId = source, Revision = 1,
                    ContentHash = new string('a', 64), CurrentStage = (int)PipelineStage.Publish,
                    CompletionCriteriaMet = complete, IsDeleted = deleted, RegisteredAtUtc = now
                };
                record.RootLineageRecordId = record.Id;
                setup.SourceIdentities.Add(identity); setup.PipelineRecords.Add(record);
                return record;
            }
        }
        await using var context = factory.CreateDbContext();
        var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(context, [root], default))[root];
        Assert.Equal(cases.Length, summary.Known);
        Assert.Equal(cases.Length, summary.Files.Count);
        Assert.Equal(expected.Values.OrderBy(file => file.SourceRevisionId), summary.Files.OrderBy(file => file.SourceRevisionId));
        Assert.Equal(expected.Values.Count(file => file.Category == "Published"), summary.Indexed);
        Assert.Equal(expected.Values.Count(file => file.Category == "Pending"), summary.Pending);
        Assert.Equal(0, summary.Deferred + summary.Blocked + summary.Error + summary.Provisional + summary.MetadataOnly + summary.WithoutText);
        Assert.Equal(now, summary.LastProgressAtUtc);
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_controls_cannot_replace_ordinary_scan_summaries_or_active_scan_counts(bool completed)
    {
        var root = Guid.NewGuid(); var verification = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = root, CanonicalPath = $"I:\\projection-verification\\{root:N}",
                DisplayName = "Verification projection", State = (int)SourceRootState.Enabled, ConfigurationRevision = 1,
                LastScanCompletedAtUtc = now.AddMinutes(-1), CreatedAtUtc = now, UpdatedAtUtc = now });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity { Id = Guid.NewGuid(), SourceRootId = root, RequestedBy = "ordinary",
                RequestedAtUtc = now.AddMinutes(-1), IsReleased = true, State = (int)SourceScanRequestState.Completed,
                DiscoveredFileCount = 40, ErrorFileCount = 7 });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity { Id = verification, SourceRootId = root,
                RequestKind = SqlSourceVerificationStore.RequestKind, VerificationRunId = Guid.NewGuid(), RequestedBy = "verification",
                RequestedAtUtc = now, IsReleased = true, State = (int)(completed ? SourceScanRequestState.Completed : SourceScanRequestState.Running),
                DiscoveredFileCount = 999, ErrorFileCount = 999 });
            setup.SourceScanJobs.Add(new SourceScanJobEntity { Id = Guid.NewGuid(), SourceScanRequestId = verification,
                State = (int)(completed ? SourceScanJobState.Completed : SourceScanJobState.Running), LeaseOwner = "verification",
                LeaseGeneration = 1, LeaseExpiresAtUtc = now.AddMinutes(10), DueAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal("Completed", detail.ScanState);
        Assert.Equal(40, detail.DiscoveredCount);
        Assert.Equal(7, detail.LastScanIssueCount);
        Assert.Equal(7, (await reader.ReadRootsAsync(default)).Single(x => x.Id == root).LastScanIssueCount);
        await using var db = factory.CreateDbContext();
        var overview = await new SqlPipelineActivityReader(null, TimeProvider.System).ReadAsync(db, now, default);
        Assert.Equal(0, overview.ActiveScans);
        Assert.Equal(7, overview.Sources.Single(x => x.Id == root).ScanErrors);
        Assert.False(overview.Sources.Single(x => x.Id == root).IsScanning);
    }

    [NativeSqlServerFact]
    public async Task Empty_root_avoids_derived_branch_queries_and_returns_empty_file_counts()
    {
        var root = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\empty-source-summary\\{root:N}", DisplayName = "Empty source",
                State = (int)SourceRootState.Enabled, ConfigurationRevision = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        var trace = new EmptySummaryCommandTrace();
        await using var context = new FluxKnowledgeDbContext(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(_fixture.ConnectionString).AddInterceptors(trace).Options);
        var summary = await SourceRootProjectionReader.ReadStateSummariesAsync(context, [root], default);
        Assert.Empty(summary);
        Assert.Equal(0, trace.BranchQueries);
        Assert.Equal(1, trace.RevisionQueries);
    }

    private sealed class EmptySummaryCommandTrace : DbCommandInterceptor
    {
        public int BranchQueries { get; private set; }
        public int RevisionQueries { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[SourceProcessorBranches]", StringComparison.Ordinal)) BranchQueries++;
            if (command.CommandText.Contains("[SourceRevisions]", StringComparison.Ordinal)) RevisionQueries++;
            return ValueTask.FromResult(result);
        }
    }

    [NativeSqlServerTheory]
    [InlineData(SourceActivityState.Pending)]
    [InlineData(SourceActivityState.Completed)]
    public async Task Policy_successor_removes_the_old_block_from_file_status_and_current_reasons_without_deleting_history(SourceActivityState state)
    {
        var root = Guid.NewGuid(); var revision = Guid.NewGuid(); var branch = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var old = Activity(revision, SourceActivityState.FailedTerminal);
        old.ActivityKind = (int)SourceActivityKind.CodeParsing;
        old.ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion;
        old.DescriptorFingerprint = RetainedCsharpCodeProcessor.LegacyDescriptorFingerprint;
        old.Reason = "csharp-code-signature-limit";
        var current = Activity(revision, state);
        current.ActivityKind = (int)SourceActivityKind.CodeParsing;
        current.ProcessorVersion = RetainedCsharpCodeProcessor.ProcessorVersion;
        current.DescriptorFingerprint = RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint;
        await using (var db = factory.CreateDbContext())
        {
            db.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = root, CanonicalPath = $"E:\\source-projection-tests\\{root:N}",
                DisplayName = "Policy projection", State = (int)SourceRootState.Enabled, ConfigurationRevision = 1,
                MaximumFileBytes = SourcePolicyDefaults.MaximumTextBytes, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceRevisions.Add(Revision(revision, root, "source.cs", now, false));
            db.SourceActivities.AddRange(old, current);
            db.SourceProcessorBranches.Add(new SourceProcessorBranchEntity { Id = branch, SourceRevisionId = revision, SourceActivityId = old.Id,
                InputSha256 = old.InputFingerprint, ProcessorVersion = old.ProcessorVersion, ProcessorFingerprint = old.DescriptorFingerprint,
                State = (int)RetainedProcessorBranchState.Blocked, LeaseGeneration = 1, AttemptCount = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity { Id = Guid.NewGuid(), BranchId = branch, LeaseGeneration = 1,
                StartedAtUtc = now, FinishedAtUtc = now, OutcomeCode = old.Reason });
            db.SourceActivityRelations.Add(new SourceActivityRelationEntity { Id = Guid.NewGuid(), PredecessorActivityId = old.Id,
                SuccessorActivityId = current.Id, RelationshipKind = RetainedDefaultPolicy.RelationshipKind,
                ReasonCode = RetainedDefaultPolicy.Version, CreatedAtUtc = now });
            await db.SaveChangesAsync();
        }
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal(0, detail.BlockedCount);
        Assert.DoesNotContain(detail.DeferredOrBlockedReasons, value => value.Reason == old.Reason);
        var file = Assert.Single(detail.Files);
        Assert.NotEqual("Blocked", file.Status);
        Assert.NotEqual(old.Reason, file.Reason);
        await using var verification = factory.CreateDbContext();
        Assert.Equal((int)RetainedProcessorBranchState.Blocked, (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == branch)).State);
        Assert.Equal(old.Reason, (await verification.SourceProcessorAttempts.SingleAsync(value => value.BranchId == branch)).OutcomeCode);
    }

    [NativeSqlServerTheory]
    [InlineData(SourceScanRequestState.Running)]
    [InlineData(SourceScanRequestState.Released)]
    public async Task Verified_recovery_clears_current_failure_without_erasing_scan_history_or_hiding_unrelated_failures(SourceScanRequestState nextScanState)
    {
        var now = DateTimeOffset.UtcNow;
        var root = Guid.NewGuid();
        var source = Guid.NewGuid();
        var record = Guid.NewGuid();
        var identity = Guid.NewGuid();
        var otherSource = Guid.NewGuid();
        var otherRecord = Guid.NewGuid();
        var otherIdentity = Guid.NewGuid();
        var scan = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\source-projection-tests\\{root:N}", DisplayName = "Recovery",
                State = (int)SourceRootState.Enabled, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now, MaximumFileBytes = 10000,
                ReconciliationCadenceSeconds = 900
            });
            setup.SourceRevisions.Add(Revision(source, root, "recovering.txt", now, false));
            var other = Revision(otherSource, root, "another-input.txt", now, false);
            other.OriginKind = 2;
            other.ParentSourceRevisionId = source;
            setup.SourceRevisions.Add(other);
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identity, SourceKind = "projection-test", StableKey = $"recovery:{root:N}", CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = record, SourceIdentityId = identity, SourceRevisionId = source, Revision = 1,
                ContentHash = new string('a', 64), RootLineageRecordId = record,
                CurrentStage = (int)PipelineStage.Publish, RegisteredAtUtc = now
            });
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = otherIdentity, SourceKind = "projection-test", StableKey = $"other:{root:N}", CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = otherRecord, SourceIdentityId = otherIdentity, SourceRevisionId = otherSource, Revision = 1,
                ContentHash = new string('a', 64), RootLineageRecordId = otherRecord,
                CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true, RegisteredAtUtc = now
            });
            var activity = Activity(source, SourceActivityState.FailedTerminal, record);
            activity.ResultingPipelineRecordRevision = 1;
            setup.SourceActivities.Add(activity);
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = scan, SourceRootId = root, RequestedBy = "test", RequestedAtUtc = now,
                IsReleased = true, State = (int)SourceScanRequestState.Completed,
                DiscoveredFileCount = 1, ErrorFileCount = 7, AuditEvidenceJson = "{\"retainedHistory\":true,\"enumerationErrors\":[{\"Kind\":\"permission\",\"RelativePath\":\"old-scan-item.txt\",\"Detail\":\"SourcePathDenied\"}]}"
            });
            await setup.SaveChangesAsync();
        }
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));
        async Task<int> ReadOverviewScanErrorsAsync()
        {
            await using var context = factory.CreateDbContext();
            var summary = await new SqlPipelineActivityReader(null, TimeProvider.System)
                .ReadAsync(context, DateTimeOffset.UtcNow, default);
            return summary.Sources.Single(value => value.Id == root).ScanErrors;
        }
        var before = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal(1, before.ErrorCount);
        Assert.Equal(7, before.LastScanIssueCount);
        Assert.Equal(7, await ReadOverviewScanErrorsAsync());
        Assert.Equal(7, before.ScanIssues.ReportedCount);
        Assert.False(before.ScanIssues.CauseCountsComplete);
        Assert.Equal("old-scan-item.txt", Assert.Single(Assert.Single(before.ScanIssues.Groups).SamplePaths));
        Assert.Equal("recovering.txt", Assert.Single(Assert.Single(before.CurrentProcessingIssues).AffectedPaths));

        await using (var update = factory.CreateDbContext())
        {
            (await update.PipelineRecords.SingleAsync(value => value.Id == record)).CompletionCriteriaMet = true;
            await update.SaveChangesAsync();
        }
        var recovered = Assert.IsType<SourceRootDetailProjection>(await reader.ReadRootAsync(root, default));
        Assert.Equal(0, recovered.ErrorCount);
        Assert.Equal(1, recovered.IndexedCount);
        Assert.Equal("Indexed", Assert.Single(recovered.Files).Status);
        Assert.Null(Assert.Single(recovered.Files).Reason);
        Assert.Equal(record, Assert.Single(recovered.Files).CorpusPipelineRecordId);
        Assert.Empty(recovered.DeferredOrBlockedReasons);
        Assert.Empty(recovered.CurrentProcessingIssues);
        Assert.Equal(0, (await reader.ReadRootsAsync(default)).Single(value => value.Id == root).ErrorCount);

        var nextScan = Guid.NewGuid();
        await using (var update = factory.CreateDbContext())
        {
            update.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = nextScan, SourceRootId = root, RequestedBy = "test", RequestedAtUtc = now.AddMinutes(1),
                IsReleased = true, State = (int)nextScanState
            });
            await update.SaveChangesAsync();
        }
        Assert.Equal(7, (await reader.ReadRootAsync(root, default))!.LastScanIssueCount);
        Assert.Equal(7, (await reader.ReadRootsAsync(default)).Single(value => value.Id == root).LastScanIssueCount);
        Assert.Equal(7, await ReadOverviewScanErrorsAsync());

        await using (var update = factory.CreateDbContext())
        {
            (await update.SourceScanRequests.SingleAsync(value => value.Id == nextScan)).State = (int)SourceScanRequestState.Completed;
            await update.SaveChangesAsync();
        }
        Assert.Equal(0, (await reader.ReadRootAsync(root, default))!.LastScanIssueCount);
        Assert.Equal(0, await ReadOverviewScanErrorsAsync());
        Assert.Empty((await reader.ReadRootAsync(root, default))!.ScanIssues.Groups);
        await using (var update = factory.CreateDbContext())
        {
            // A valid completed record for another source cannot resolve this failed item.
            (await update.SourceActivities.SingleAsync(value => value.SourceRevisionId == source)).ResultingPipelineRecordId = otherRecord;
            await update.SaveChangesAsync();
        }
        Assert.Equal(1, (await reader.ReadRootAsync(root, default))!.ErrorCount);
        await using (var update = factory.CreateDbContext())
        {
            (await update.SourceActivities.SingleAsync(value => value.SourceRevisionId == source)).ResultingPipelineRecordId = record;
            var independentFailure = Activity(source, SourceActivityState.FailedTerminal);
            independentFailure.ProcessorVersion = "independent-v2";
            update.SourceActivities.Add(independentFailure);
            await update.SaveChangesAsync();
        }
        Assert.Equal(1, (await reader.ReadRootAsync(root, default))!.ErrorCount);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(7, (await verify.SourceScanRequests.SingleAsync(value => value.Id == scan)).ErrorFileCount);
        Assert.Equal(2, await verify.SourceActivities.CountAsync(value => value.SourceRevisionId == source &&
            value.State == (int)SourceActivityState.FailedTerminal));
    }

    [NativeSqlServerFact]
    public async Task Pending_selected_replacement_overrides_a_valid_older_publication_in_both_file_and_summary_views()
    {
        var now = DateTimeOffset.UtcNow;
        var root = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\pending-logical-tests\\{root:N}", DisplayName = "Pending replacement",
                State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]", MaximumFileBytes = 10000, ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(Revision(owner, root, "replacement.pdf", now.AddHours(-1), false));
            AddPublishedChild(setup, owner, 2, PdfDocumentProcessor.Capability, now.AddMinutes(-10), "published");
            var pending = AddChild(setup, owner, 2, ImageDocumentInputProcessor.Capability, now.AddMinutes(-1), "pending");
            setup.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = pending.RecordId, SourceRevision = 1,
                Stage = (int)PipelineStage.Publish, Operation = "publish", PublicState = (int)PublicJobState.WorkerQueued,
                DueAtUtc = now
            });
            await setup.SaveChangesAsync();
        }
        await using (var context = factory.CreateDbContext())
        {
            var summary = (await SourceRootProjectionReader.ReadStateSummariesAsync(context, [root], CancellationToken.None))[root];
            Assert.Equal(1, summary.Known);
            Assert.Equal(1, summary.Pending);
            Assert.Equal(0, summary.Indexed);
            Assert.Null(summary.LastProgressAtUtc);
        }
        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(), new LocalSourceCapabilityHandlerRegistry([]));
        var detail = await reader.ReadRootAsync(root, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("Pending", Assert.Single(detail.Files).Status);
        Assert.Equal(0, detail.IndexedCount);
    }

    [NativeSqlServerFact]
    public async Task Read_fence_covers_member_completion_and_graph_deletion_when_the_versioned_parent_commits_atomically()
    {
        var now = DateTimeOffset.UtcNow;
        var root = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        Guid branchId;
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = root, CanonicalPath = $"E:\\read-fence-tests\\{root:N}", DisplayName = "Read fence",
                State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]", MaximumFileBytes = 10000, ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(Revision(owner, root, "document.pdf", now, false));
            branchId = AddChild(setup, owner, 2, PdfDocumentProcessor.Capability, now, "fence").BranchId;
            setup.SourceDeletionOperations.Add(new SourceDeletionOperationEntity { Id = Guid.NewGuid(), SourceRootId = root,
                Phase = "draining", CreatedAtUtc = now, UpdatedAtUtc = now });
            await setup.SaveChangesAsync();
        }
        await using var reader = factory.CreateDbContext();
        await using var writer = factory.CreateDbContext();
        var beforeMembers = await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None);
        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            writer.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity { Id = Guid.NewGuid(), BranchId = branchId,
                MemberFingerprint = new string('d', 64), Disposition = "skipped", ReasonCode = "test", CreatedAtUtc = now });
            var branch = await writer.SourceProcessorBranches.SingleAsync(b => b.Id == branchId);
            branch.CompletedMemberCount++;
            branch.UpdatedAtUtc = now.AddSeconds(1);
            await writer.SaveChangesAsync();
            Assert.False((await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None)).IsStable);
            await transaction.CommitAsync();
        }
        Assert.False(beforeMembers.Accepts(await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None)));
        var beforeDeletion = await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None);
        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            await writer.SourceProcessorBranchMembers.Where(member => member.BranchId == branchId).ExecuteDeleteAsync();
            var operation = await writer.SourceDeletionOperations.SingleAsync(d => d.SourceRootId == root);
            operation.Phase = "cleanup-files";
            operation.UpdatedAtUtc = now.AddSeconds(2);
            await writer.SaveChangesAsync();
            Assert.False((await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None)).IsStable);
            await transaction.CommitAsync();
        }
        Assert.False(beforeDeletion.Accepts(await SqlPipelineActivityQuery.ReadFenceAsync(reader, CancellationToken.None)));
        Assert.Empty(await reader.SourceProcessorBranchMembers.Where(member => member.BranchId == branchId).ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Published_Visio_owner_projects_only_the_current_document_result()
    {
        var now = DateTimeOffset.Parse("2026-09-21T00:00:00+00:00");
        var rootId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var inputId = Guid.NewGuid();
        var branchActivityId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var hash = new string('a', 64);
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = $"E:\\source-projection-visio-tests\\{rootId:N}",
                DisplayName = "Published Visio projection", State = (int)SourceRootState.Enabled,
                Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[]", MaximumFileBytes = 16L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.AddRange(
                new SourceRevisionEntity
                {
                    Id = ownerId, SourceRootId = rootId, StableSourceIdentity = $"visio-owner:{ownerId:N}",
                    Revision = 1, ContentSha256 = hash,
                    CanonicalPath = $"E:\\source-projection-visio-tests\\{rootId:N}\\architecture.vsdx",
                    Classification = "DeferredCapability", Extension = ".vsdx", ByteLength = 4,
                    DiscoveredAtUtc = now
                },
                new SourceRevisionEntity
                {
                    Id = inputId, SourceRootId = rootId, StableSourceIdentity = $"visio-input:{inputId:N}",
                    Revision = 1, ContentSha256 = hash, CanonicalPath = string.Empty,
                    ParentSourceRevisionId = ownerId, Classification = DocumentProcessingInput.VisioClassification,
                    Extension = ".vsdx", OriginKind = 2, ByteLength = 4, DiscoveredAtUtc = now
                });
            setup.SourceActivities.AddRange(
                new SourceActivityEntity
                {
                    Id = Guid.NewGuid(), SourceRevisionId = ownerId,
                    ActivityKind = (int)SourceActivityKind.DocumentParsing,
                    ExecutionClass = (int)ExecutionClass.DeferredCapability,
                    ProcessorVersion = "phase-6-vsdx-structural-v1", InputFingerprint = hash,
                    State = (int)SourceActivityState.DeferredUnsupported,
                    Reason = "vsdx-structural-extraction-pending", CreatedAtUtc = now, UpdatedAtUtc = now
                },
                new SourceActivityEntity
                {
                    Id = branchActivityId, SourceRevisionId = ownerId,
                    ActivityKind = (int)SourceActivityKind.TextExtraction,
                    ExecutionClass = (int)ExecutionClass.InProcess,
                    ProcessorVersion = DocumentProcessingInput.Visio.ParentProcessorVersion,
                    InputFingerprint = hash, State = (int)SourceActivityState.Completed,
                    CreatedAtUtc = now, UpdatedAtUtc = now
                });
            setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
            {
                Id = branchId, SourceActivityId = branchActivityId, SourceRevisionId = ownerId,
                InputSha256 = hash, ProcessorVersion = DocumentProcessingInput.Visio.ParentProcessorVersion,
                ProcessorFingerprint = DocumentProcessingInput.Visio.ParentProcessorFingerprint,
                State = (int)RetainedProcessorBranchState.Completed, CompletedMemberCount = 1,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = identityId, SourceKind = "retained local source",
                StableKey = $"visio-input:{inputId:N}", CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId, SourceIdentityId = identityId, SourceRevisionId = inputId,
                Revision = 1, ContentHash = hash, RootLineageRecordId = recordId,
                CurrentStage = (int)PipelineStage.Publish, CompletionCriteriaMet = true,
                RegisteredAtUtc = now
            });
            setup.DocumentPublications.Add(new DocumentPublicationEntity
            {
                OwnerSourceRevisionId = ownerId, DocumentInputSourceRevisionId = inputId,
                SourceProcessorBranchId = branchId, PipelineRecordId = recordId,
                PipelineRecordRevision = 1,
                ProcessorFingerprint = DocumentProcessingInput.Visio.ParentProcessorFingerprint,
                PublishedAtUtc = now
            });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, RequestKind = 0, RequestedBy = "test",
                RequestedAtUtc = now, IsReleased = true, ReleasedAtUtc = now,
                State = (int)SourceScanRequestState.Completed, DiscoveredFileCount = 1
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));
        var list = Assert.Single(await reader.ReadRootsAsync(CancellationToken.None), value => value.Id == rootId);
        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, list.IndexedCount);
        Assert.Equal(0, list.DeferredCount);
        Assert.Equal(1, detail.IndexedCount);
        Assert.Equal(0, detail.DeferredCount);
        var file = Assert.Single(detail.Files);
        Assert.Equal("architecture.vsdx", file.FileName);
        Assert.Equal("VsdxDocumentContainer", file.Classification);
        Assert.Equal("Indexed", file.Status);
        Assert.Null(file.Reason);
        Assert.Equal(recordId, file.CorpusPipelineRecordId);
        Assert.DoesNotContain(detail.DeferredOrBlockedReasons,
            value => value.Reason == "vsdx-structural-extraction-pending");
    }

    [NativeSqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Logical_document_statuses_show_no_content_metadata_only_and_failed_replacement_truthfully(int officeGeneration)
    {
        var now = DateTimeOffset.Parse("2026-09-21T04:00:00+00:00");
        var rootId = Guid.NewGuid();
        var noContentOwnerId = Guid.NewGuid();
        var metadataOwnerId = Guid.NewGuid();
        var failedOwnerId = Guid.NewGuid();
        var blockedOwnerId = Guid.NewGuid();
        var publishedOfficeOwnerId = Guid.NewGuid();
        var office = officeGeneration == 4 ? OoxmlStructuralTextProcessor.Capability :
            OoxmlStructuralTextProcessor.Capability with
            {
                ProcessorVersion = $"phase-5-ooxml-structural-v{officeGeneration}",
                ProcessorFingerprint = $"phase-5-ooxml-retained-structural-v{officeGeneration}"
            };
        Guid blockedLastGoodRecordId;
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = $"E:\\logical-status-tests\\{rootId:N}", DisplayName = "Logical status",
                State = (int)SourceRootState.Enabled, Recursive = true, IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]", AllowedClassificationsJson = "[]", MaximumFileBytes = 256L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
            setup.SourceRevisions.AddRange(
                Owner(noContentOwnerId, "empty.docx", "OoxmlDocumentContainer"),
                Owner(metadataOwnerId, "scan.png", "DeferredCapability"),
                Owner(failedOwnerId, "replacement.pdf", "PdfDocumentContainer"),
                Owner(blockedOwnerId, "blocked-before-child.pdf", "PdfDocumentContainer"),
                Owner(publishedOfficeOwnerId, "published.docx", "OoxmlDocumentContainer"));

            var noContentActivity = BranchActivity(noContentOwnerId, office);
            var noContentBranch = Branch(noContentOwnerId, noContentActivity.Id, office, now);
            noContentBranch.CompletedMemberCount = 0;
            setup.SourceActivities.Add(noContentActivity);
            setup.SourceProcessorBranches.Add(noContentBranch);
            setup.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
            {
                Id = Guid.NewGuid(), BranchId = noContentBranch.Id, MemberFingerprint = new string('1', 64),
                Disposition = "skipped", ReasonCode = "office-document-no-extractable-text", CreatedAtUtc = now
            });

            AddPublishedChild(
                setup, metadataOwnerId, OriginKind: 3, MediaMetadataRetainedProcessor.Capability, now, "metadata");
            AddPublishedChild(
                setup, publishedOfficeOwnerId, OriginKind: 2, office, now, "published-office");
            AddPublishedChild(
                setup, failedOwnerId, OriginKind: 2, PdfDocumentProcessor.Capability, now, "last-good");
            var failedChild = AddChild(
                setup, failedOwnerId, OriginKind: 2, ImageDocumentInputProcessor.Capability, now.AddMinutes(1), "failed");
            setup.Jobs.Add(new JobEntity
            {
                Id = Guid.NewGuid(), PipelineRecordId = failedChild.RecordId, SourceRevision = 1,
                Stage = (int)PipelineStage.Extract, Operation = "extract-document",
                PublicState = (int)PublicJobState.Failed, Reason = "document-ocr-provider-refused", DueAtUtc = now
            });
            var lastGoodBeforeBlocked = AddPublishedChild(
                setup, blockedOwnerId, OriginKind: 2, PdfDocumentProcessor.Capability, now, "last-good");
            blockedLastGoodRecordId = lastGoodBeforeBlocked.RecordId;
            var blockedActivity = BranchActivity(blockedOwnerId, ImageDocumentInputProcessor.Capability);
            var blockedBranch = Branch(
                blockedOwnerId,
                blockedActivity.Id,
                ImageDocumentInputProcessor.Capability,
                now.AddMinutes(1));
            blockedBranch.State = (int)RetainedProcessorBranchState.Blocked;
            blockedBranch.CompletedMemberCount = 0;
            blockedBranch.LeaseGeneration = 1;
            blockedBranch.AttemptCount = 1;
            blockedActivity.CreatedAtUtc = now.AddMinutes(1);
            blockedActivity.UpdatedAtUtc = now.AddMinutes(1);
            setup.SourceActivities.Add(blockedActivity);
            setup.SourceProcessorBranches.Add(blockedBranch);
            setup.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
            {
                Id = Guid.NewGuid(), BranchId = blockedBranch.Id, LeaseGeneration = 1,
                StartedAtUtc = now.AddMinutes(1), FinishedAtUtc = now.AddMinutes(1),
                OutcomeCode = "retained-artifact-checksum-invalid"
            });
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(), SourceRootId = rootId, RequestKind = 0, RequestedBy = "test", RequestedAtUtc = now,
                IsReleased = true, ReleasedAtUtc = now, State = (int)SourceScanRequestState.Completed, DiscoveredFileCount = 5
            });
            await setup.SaveChangesAsync();

            SourceRevisionEntity Owner(Guid id, string fileName, string classification) => new()
            {
                Id = id, SourceRootId = rootId, StableSourceIdentity = $"owner:{id:N}", Revision = 1,
                ContentSha256 = new string('a', 64), CanonicalPath = $"E:\\logical-status-tests\\{rootId:N}\\{fileName}",
                Classification = classification, Extension = Path.GetExtension(fileName), ByteLength = 4, DiscoveredAtUtc = now
            };
        }

        var reader = new SourceRootProjectionReader(factory, new NoPathPolicy(), new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));
        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        var noContent = Assert.Single(detail.Files, value => value.FileName == "empty.docx");
        Assert.Equal("Completed", noContent.Status);
        Assert.Equal("office-document-no-extractable-text", noContent.Reason);
        Assert.Null(noContent.CorpusPipelineRecordId);
        var metadata = Assert.Single(detail.Files, value => value.FileName == "scan.png");
        Assert.Equal("Metadata only", metadata.Status);
        Assert.Equal("media-metadata-only", metadata.Reason);
        var failed = Assert.Single(detail.Files, value => value.FileName == "replacement.pdf");
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("document-ocr-provider-refused", failed.Reason);
        Assert.NotNull(failed.CorpusPipelineRecordId);
        var blocked = Assert.Single(detail.Files, value => value.FileName == "blocked-before-child.pdf");
        Assert.Equal("Blocked", blocked.Status);
        Assert.Equal("retained-artifact-checksum-invalid", blocked.Reason);
        Assert.Equal(blockedLastGoodRecordId, blocked.CorpusPipelineRecordId);
        var publishedOffice = Assert.Single(detail.Files, value => value.FileName == "published.docx");
        Assert.Equal("Indexed", publishedOffice.Status);
        Assert.NotNull(publishedOffice.CorpusPipelineRecordId);
        Assert.DoesNotContain(detail.CurrentProcessingIssues.SelectMany(value => value.AffectedPaths), value => value == "published.docx");
        Assert.Equal(1, detail.IndexedCount);
        Assert.Equal(1, detail.BlockedCount);
        Assert.Equal(1, detail.ErrorCount);
        var failedGroup = Assert.Single(detail.CurrentProcessingIssues, group => group.State == "Failed");
        Assert.Equal("document-ocr-provider-refused", failedGroup.Reason);
        Assert.Equal("replacement.pdf", Assert.Single(failedGroup.AffectedPaths));
    }

    [NativeSqlServerFact]
    public async Task Source_root_projections_derive_visible_counts_from_unsuppressed_activity_state_and_terminal_evidence()
    {
        var rootId = Guid.NewGuid();
        var completedRevisionId = Guid.NewGuid();
        var deferredRevisionId = Guid.NewGuid();
        var blockedRevisionId = Guid.NewGuid();
        var failedRevisionId = Guid.NewGuid();
        var suppressedRevisionId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-09T09:00:00+00:00");
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}",
                DisplayName = "Source projection",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[\"text/plain\"]",
                MaximumFileBytes = 16L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.AddRange(
                Revision(completedRevisionId, rootId, "completed.txt", now, false),
                Revision(deferredRevisionId, rootId, "deferred.pdf", now, false),
                Revision(blockedRevisionId, rootId, "blocked.cs", now, false),
                Revision(failedRevisionId, rootId, "failed.txt", now, false),
                Revision(suppressedRevisionId, rootId, "historic.txt", now, true));
            var sourceIdentityId = Guid.NewGuid();
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceIdentityId,
                SourceKind = "projection-test",
                StableKey = $"projection:{recordId:N}",
                CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = sourceIdentityId,
                SourceRevisionId = completedRevisionId,
                Revision = 1,
                ContentHash = new string('a', 64),
                RootLineageRecordId = recordId,
                CurrentStage = (int)PipelineStage.Publish,
                CompletionCriteriaMet = true,
                RegisteredAtUtc = now
            });
            setup.SourceActivities.AddRange(
                Activity(completedRevisionId, SourceActivityState.Completed, recordId),
                Activity(deferredRevisionId, SourceActivityState.DeferredUnsupported),
                Activity(blockedRevisionId, SourceActivityState.DeferredPolicy),
                Activity(failedRevisionId, SourceActivityState.FailedTerminal),
                Activity(suppressedRevisionId, SourceActivityState.Completed));
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(),
                SourceRootId = rootId,
                RequestKind = 0,
                RequestedBy = "test",
                RequestedAtUtc = now,
                IsReleased = true,
                ReleasedAtUtc = now,
                State = (int)SourceScanRequestState.Completed,
                DiscoveredFileCount = 5,
                ErrorFileCount = 1
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(
            factory,
            new NoPathPolicy(),
            new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));

        var list = Assert.Single(await reader.ReadRootsAsync(CancellationToken.None), value => value.Id == rootId);
        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, list.IndexedCount);
        Assert.Equal(1, list.DeferredCount);
        Assert.Equal(1, list.BlockedCount);
        Assert.Equal(1, list.ErrorCount);
        Assert.Equal(1, list.LastScanIssueCount);
        Assert.Equal(5, detail.DiscoveredCount);
        Assert.Equal(1, detail.IndexedCount);
        Assert.Equal(1, detail.DeferredCount);
        Assert.Equal(1, detail.BlockedCount);
        Assert.Equal(1, detail.ErrorCount);
        Assert.Equal(1, detail.LastScanIssueCount);
        Assert.Collection(
            detail.Files,
            file =>
            {
                Assert.Equal("blocked.cs", file.FileName);
                Assert.Equal("Blocked", file.Status);
                Assert.Null(file.CorpusPipelineRecordId);
            },
            file =>
            {
                Assert.Equal("completed.txt", file.FileName);
                Assert.Equal("Indexed", file.Status);
                Assert.Equal(recordId, file.CorpusPipelineRecordId);
            },
            file =>
            {
                Assert.Equal("deferred.pdf", file.FileName);
                Assert.Equal("Deferred", file.Status);
                Assert.Null(file.CorpusPipelineRecordId);
            },
            file =>
            {
                Assert.Equal("failed.txt", file.FileName);
                Assert.Equal("Failed", file.Status);
                Assert.Null(file.CorpusPipelineRecordId);
            });
    }

    [NativeSqlServerFact]
    public async Task Source_root_detail_projects_a_legacy_pdf_generic_reason_without_mutating_the_stored_activity()
    {
        var rootId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-03T21:00:00+00:00");
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}",
                DisplayName = "Legacy PDF projection",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[\"text/plain\"]",
                MaximumFileBytes = 16 * 1024 * 1024,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = revisionId,
                SourceRootId = rootId,
                StableSourceIdentity = $"legacy-pdf:{revisionId:N}",
                Revision = 1,
                ContentSha256 = new string('b', 64),
                CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}\\legacy.pdf",
                Classification = "DeferredCapability",
                Extension = ".pdf",
                ByteLength = 1,
                DiscoveredAtUtc = now,
                DiscoveryEvidenceJson = "{}"
            });
            setup.SourceActivities.Add(new SourceActivityEntity
            {
                Id = activityId,
                SourceRevisionId = revisionId,
                ActivityKind = (int)SourceActivityKind.DocumentParsing,
                ExecutionClass = (int)ExecutionClass.DeferredCapability,
                ProcessorVersion = "phase-3a-v1",
                InputFingerprint = new string('b', 64),
                State = (int)SourceActivityState.DeferredUnsupported,
                Reason = "Binary signature requires a capability that is not registered.",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(
            factory,
            new NoPathPolicy(),
            new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));

        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Contains(detail.DeferredOrBlockedReasons, value =>
            value.State == nameof(SourceActivityState.DeferredUnsupported) &&
            value.Reason == "pdf-parser-unavailable" && value.Count == 1);
        await using var verification = factory.CreateDbContext();
        Assert.Equal("Binary signature requires a capability that is not registered.",
            (await verification.SourceActivities.SingleAsync(value => value.Id == activityId)).Reason);
    }

    [NativeSqlServerFact]
    public async Task Source_root_projection_counts_terminal_processor_owned_branches_once_and_projects_their_attempt_reasons()
    {
        var rootId = Guid.NewGuid();
        var indexedRevisionId = Guid.NewGuid();
        var deferredRevisionIds = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        var blockedRevisionIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var recordId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-03T22:00:00+00:00");
        var expectedBlockedReasons = new[]
        {
            "office-document-part-unsupported",
            "office-document-container-invalid",
            "archive-compression-ratio-limit"
        };
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        var pendingActivities = new List<SourceActivityEntity>();
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"E:\\source-projection-processor-branch-tests\\{rootId:N}",
                DisplayName = "Processor branch projection",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[\"text/plain\"]",
                MaximumFileBytes = 16L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.Add(Revision(indexedRevisionId, rootId, "indexed.txt", now, false));
            setup.SourceRevisions.AddRange(deferredRevisionIds.Select((id, index) => Revision(id, rootId, $"deferred-{index:D2}.txt", now, false)));
            setup.SourceRevisions.AddRange(blockedRevisionIds.Select((id, index) => Revision(id, rootId, $"blocked-{index:D2}.docx", now, false)));
            var sourceIdentityId = Guid.NewGuid();
            setup.SourceIdentities.Add(new SourceIdentityEntity
            {
                Id = sourceIdentityId,
                SourceKind = "projection-processor-branch-test",
                StableKey = $"projection-processor-branch:{recordId:N}",
                CreatedAtUtc = now
            });
            setup.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = recordId,
                SourceIdentityId = sourceIdentityId,
                SourceRevisionId = indexedRevisionId,
                Revision = 1,
                ContentHash = new string('c', 64),
                RootLineageRecordId = recordId,
                CurrentStage = (int)PipelineStage.Publish,
                CompletionCriteriaMet = true,
                RegisteredAtUtc = now
            });
            setup.SourceActivities.Add(Activity(indexedRevisionId, SourceActivityState.Completed, recordId));
            setup.SourceActivities.AddRange(deferredRevisionIds.Select(id => Activity(id, SourceActivityState.DeferredUnsupported)));
            pendingActivities.AddRange(blockedRevisionIds.Select(id => Activity(id, SourceActivityState.Pending)));
            setup.SourceActivities.AddRange(pendingActivities);
            for (var index = 0; index < pendingActivities.Count; index++)
            {
                var branchId = Guid.NewGuid();
                setup.SourceProcessorBranches.Add(new SourceProcessorBranchEntity
                {
                    Id = branchId,
                    SourceActivityId = pendingActivities[index].Id,
                    SourceRevisionId = blockedRevisionIds[index],
                    InputSha256 = new string('c', 64),
                    ProcessorVersion = "phase-5-compat-v1",
                    ProcessorFingerprint = "projection-processor-branch-test",
                    State = (int)RetainedProcessorBranchState.Blocked,
                    LeaseGeneration = 1,
                    AttemptCount = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                setup.SourceProcessorAttempts.Add(new SourceProcessorAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    BranchId = branchId,
                    LeaseGeneration = 1,
                    StartedAtUtc = now,
                    FinishedAtUtc = now,
                    OutcomeCode = expectedBlockedReasons[index]
                });
            }
            setup.SourceScanRequests.Add(new SourceScanRequestEntity
            {
                Id = Guid.NewGuid(),
                SourceRootId = rootId,
                RequestKind = 0,
                RequestedBy = "test",
                RequestedAtUtc = now,
                IsReleased = true,
                ReleasedAtUtc = now,
                State = (int)SourceScanRequestState.Completed,
                DiscoveredFileCount = 13,
                ErrorFileCount = 0
            });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(
            factory,
            new NoPathPolicy(),
            new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));

        var list = Assert.Single(await reader.ReadRootsAsync(CancellationToken.None), value => value.Id == rootId);
        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, list.IndexedCount);
        Assert.Equal(9, list.DeferredCount);
        Assert.Equal(3, list.BlockedCount);
        Assert.Equal(0, list.ErrorCount);
        Assert.Equal(13, detail.DiscoveredCount);
        Assert.Equal(1, detail.IndexedCount);
        Assert.Equal(9, detail.DeferredCount);
        Assert.Equal(3, detail.BlockedCount);
        Assert.Equal(0, detail.ErrorCount);
        foreach (var reason in expectedBlockedReasons)
        {
            Assert.Contains(detail.DeferredOrBlockedReasons, value =>
                value.State == "Blocked" && value.Reason == reason && value.Count == 1);
        }

        await using var verification = factory.CreateDbContext();
        Assert.All(await verification.SourceActivities.Where(value => pendingActivities.Select(activity => activity.Id).Contains(value.Id)).ToArrayAsync(),
            value => Assert.Equal((int)SourceActivityState.Pending, value.State));
        Assert.All(await verification.SourceProcessorBranches.Where(value => blockedRevisionIds.Contains(value.SourceRevisionId)).ToArrayAsync(),
            value => Assert.Equal((int)RetainedProcessorBranchState.Blocked, value.State));
    }

    [NativeSqlServerFact]
    public async Task Source_root_detail_excludes_suppressed_activity_and_processor_branch_reasons()
    {
        var rootId = Guid.NewGuid();
        var currentRevisionId = Guid.NewGuid();
        var suppressedBranchRevisionId = Guid.NewGuid();
        var suppressedActivityRevisionId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-09-03T23:00:00+00:00");
        var factory = new TestDbContextFactory(_fixture.ConnectionString);
        await using (var setup = factory.CreateDbContext())
        {
            setup.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId,
                CanonicalPath = $"E:\\source-projection-suppressed-reason-tests\\{rootId:N}",
                DisplayName = "Suppressed reason projection",
                State = (int)SourceRootState.Enabled,
                Recursive = true,
                IncludePatternsJson = "[]",
                ExcludePatternsJson = "[]",
                AllowedClassificationsJson = "[\"text/plain\"]",
                MaximumFileBytes = 16L * 1024 * 1024,
                ReconciliationCadenceSeconds = 900,
                ConfigurationRevision = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            setup.SourceRevisions.AddRange(
                Revision(currentRevisionId, rootId, "current.vsdx", now, false),
                Revision(suppressedBranchRevisionId, rootId, "historic.docx", now, true),
                Revision(suppressedActivityRevisionId, rootId, "historic.pdf", now, true));

            var currentActivity = Activity(currentRevisionId, SourceActivityState.Pending);
            var suppressedBranchActivity = Activity(suppressedBranchRevisionId, SourceActivityState.Pending);
            var suppressedDeferredActivity = Activity(suppressedActivityRevisionId, SourceActivityState.DeferredUnsupported);
            suppressedDeferredActivity.Reason = "historic-pdf-parser-unavailable";
            setup.SourceActivities.AddRange(currentActivity, suppressedBranchActivity, suppressedDeferredActivity);

            var currentBranchId = Guid.NewGuid();
            var suppressedBranchId = Guid.NewGuid();
            setup.SourceProcessorBranches.AddRange(
                new SourceProcessorBranchEntity
                {
                    Id = currentBranchId,
                    SourceActivityId = currentActivity.Id,
                    SourceRevisionId = currentRevisionId,
                    InputSha256 = new string('d', 64),
                    ProcessorVersion = "phase-5-compat-v1",
                    ProcessorFingerprint = "suppressed-reason-projection-test",
                    State = (int)RetainedProcessorBranchState.Blocked,
                    LeaseGeneration = 1,
                    AttemptCount = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new SourceProcessorBranchEntity
                {
                    Id = suppressedBranchId,
                    SourceActivityId = suppressedBranchActivity.Id,
                    SourceRevisionId = suppressedBranchRevisionId,
                    InputSha256 = new string('d', 64),
                    ProcessorVersion = "phase-5-compat-v1",
                    ProcessorFingerprint = "suppressed-reason-projection-test",
                    State = (int)RetainedProcessorBranchState.Blocked,
                    LeaseGeneration = 1,
                    AttemptCount = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            setup.SourceProcessorAttempts.AddRange(
                new SourceProcessorAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    BranchId = currentBranchId,
                    LeaseGeneration = 1,
                    StartedAtUtc = now,
                    FinishedAtUtc = now,
                    OutcomeCode = "archive-member-not-utf8"
                },
                new SourceProcessorAttemptEntity
                {
                    Id = Guid.NewGuid(),
                    BranchId = suppressedBranchId,
                    LeaseGeneration = 1,
                    StartedAtUtc = now,
                    FinishedAtUtc = now,
                    OutcomeCode = "office-document-part-unsupported"
                });
            await setup.SaveChangesAsync();
        }

        var reader = new SourceRootProjectionReader(
            factory,
            new NoPathPolicy(),
            new NoEnumeration(),
            new LocalSourceCapabilityHandlerRegistry([]));

        var detail = await reader.ReadRootAsync(rootId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(1, detail.BlockedCount);
        Assert.Contains(detail.DeferredOrBlockedReasons, value =>
            value.State == "Blocked" && value.Reason == "archive-member-not-utf8" && value.Count == 1);
        Assert.DoesNotContain(detail.DeferredOrBlockedReasons, value =>
            value.Reason == "office-document-part-unsupported" || value.Reason == "historic-pdf-parser-unavailable");
    }

    private static SourceActivityEntity BranchActivity(
        Guid ownerId,
        SourceCapabilityDescriptor capability) => new()
    {
        Id = Guid.NewGuid(), SourceRevisionId = ownerId, ActivityKind = (int)capability.AcceptedActivityKind,
        ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = capability.ProcessorVersion,
        DescriptorFingerprint = capability.ProcessorFingerprint, InputFingerprint = new string('a', 64),
        State = (int)SourceActivityState.Completed, CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch
    };

    private static SourceProcessorBranchEntity Branch(
        Guid ownerId,
        Guid activityId,
        SourceCapabilityDescriptor capability,
        DateTimeOffset createdAtUtc) => new()
    {
        Id = Guid.NewGuid(), SourceActivityId = activityId, SourceRevisionId = ownerId,
        InputSha256 = new string('a', 64), ProcessorVersion = capability.ProcessorVersion,
        ProcessorFingerprint = capability.ProcessorFingerprint, State = (int)RetainedProcessorBranchState.Completed,
        CompletedMemberCount = 1, CreatedAtUtc = createdAtUtc, UpdatedAtUtc = createdAtUtc
    };

    private static LogicalChild AddChild(
        FluxKnowledgeDbContext setup,
        Guid ownerId,
        int OriginKind,
        SourceCapabilityDescriptor capability,
        DateTimeOffset createdAtUtc,
        string name)
    {
        var childId = Guid.NewGuid();
        var activity = BranchActivity(ownerId, capability);
        var branch = Branch(ownerId, activity.Id, capability, createdAtUtc);
        var childActivityId = Guid.NewGuid();
        var identityId = Guid.NewGuid();
        var recordId = Guid.NewGuid();
        var childHash = new string(name switch { "metadata" => 'b', "last-good" => 'c', _ => 'd' }, 64);
        setup.SourceActivities.AddRange(activity, new SourceActivityEntity
        {
            Id = childActivityId, SourceRevisionId = childId, ActivityKind = (int)SourceActivityKind.TextExtraction,
            ExecutionClass = (int)ExecutionClass.InProcess, ProcessorVersion = "phase-3a-v1",
            InputFingerprint = childHash, State = (int)SourceActivityState.Completed,
            ResultingPipelineRecordId = recordId, ResultingPipelineRecordRevision = 1,
            CreatedAtUtc = createdAtUtc, UpdatedAtUtc = createdAtUtc
        });
        setup.SourceProcessorBranches.Add(branch);
        setup.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = childId, SourceRootId = setup.SourceRevisions.Local.Single(value => value.Id == ownerId).SourceRootId,
            StableSourceIdentity = $"child:{name}:{childId:N}", Revision = 1, ContentSha256 = childHash,
            CanonicalPath = $"retained://{name}/{childId:N}", ParentSourceRevisionId = ownerId, Classification = "AcceptedUtf8Text",
            Extension = OriginKind == 3 ? ".json" : ".txt", OriginKind = OriginKind, ByteLength = 4,
            DiscoveredAtUtc = createdAtUtc
        });
        setup.SourceProcessorBranchMembers.Add(new SourceProcessorBranchMemberEntity
        {
            Id = Guid.NewGuid(), BranchId = branch.Id, MemberFingerprint = new string('c', 64),
            ChildSourceRevisionId = childId, ChildSourceActivityId = childActivityId,
            Disposition = "completed", ByteLength = 4, CreatedAtUtc = createdAtUtc
        });
        setup.SourceIdentities.Add(new SourceIdentityEntity
        {
            Id = identityId, SourceKind = "retained local source", StableKey = $"logical:{name}:{childId:N}",
            CreatedAtUtc = createdAtUtc
        });
        setup.PipelineRecords.Add(new PipelineRecordEntity
        {
            Id = recordId, SourceIdentityId = identityId, SourceRevisionId = childId, Revision = 1,
            ContentHash = childHash, RootLineageRecordId = recordId,
            CurrentStage = (int)PipelineStage.Publish, RegisteredAtUtc = createdAtUtc
        });
        return new LogicalChild(branch.Id, childId, recordId);
    }

    private static LogicalChild AddPublishedChild(
        FluxKnowledgeDbContext setup,
        Guid ownerId,
        int OriginKind,
        SourceCapabilityDescriptor capability,
        DateTimeOffset createdAtUtc,
        string name)
    {
        var child = AddChild(setup, ownerId, OriginKind, capability, createdAtUtc, name);
        setup.PipelineRecords.Local.Single(record => record.Id == child.RecordId).CompletionCriteriaMet = true;
        setup.DocumentPublications.Add(new DocumentPublicationEntity
        {
            OwnerSourceRevisionId = ownerId, DocumentInputSourceRevisionId = child.ChildId,
            SourceProcessorBranchId = child.BranchId, PipelineRecordId = child.RecordId,
            PipelineRecordRevision = 1, ProcessorFingerprint = capability.ProcessorFingerprint,
            PublishedAtUtc = createdAtUtc
        });
        return child;
    }

    private static SourceRevisionEntity Revision(
        Guid id,
        Guid rootId,
        string path,
        DateTimeOffset now,
        bool suppressed) => new()
    {
        Id = id,
        SourceRootId = rootId,
        StableSourceIdentity = $"identity:{id:N}",
        Revision = 1,
        ContentSha256 = new string('a', 64),
        CanonicalPath = $"E:\\source-projection-tests\\{rootId:N}\\{path}",
        Classification = "AcceptedUtf8Text",
        Extension = Path.GetExtension(path),
        ByteLength = 1,
        DiscoveredAtUtc = now,
        DiscoveryEvidenceJson = "{}",
        SuppressedAtUtc = suppressed ? now : null
    };

    private static SourceActivityEntity Activity(
        Guid sourceRevisionId,
        SourceActivityState state,
        Guid? resultingPipelineRecordId = null) => new()
    {
        Id = Guid.NewGuid(),
        SourceRevisionId = sourceRevisionId,
        ActivityKind = (int)SourceActivityKind.TextExtraction,
        ExecutionClass = (int)(state == SourceActivityState.DeferredUnsupported
            ? ExecutionClass.DeferredCapability
            : ExecutionClass.InProcess),
        ProcessorVersion = "phase-3a-v1",
        InputFingerprint = new string('a', 64),
        State = (int)state,
        ResultingPipelineRecordId = resultingPipelineRecordId,
        ResultingPipelineRecordRevision = resultingPipelineRecordId is null ? null : 1,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch
    };

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options =
            new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoPathPolicy : ISourceRootPathPolicy
    {
        public SourceRootPathValidation ValidateAndCanonicalise(SourceRootCreateRequest request) =>
            throw new NotSupportedException();
    }

    private sealed class NoEnumeration : ISourceFileEnumerator
    {
        public IReadOnlyList<SourceEnumerationEvidence> LastEvidence { get; } = [];

        public async IAsyncEnumerable<SourceDiscoveredFile> EnumerateAsync(
            SourceRootConfiguration sourceRoot,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed record LogicalChild(Guid BranchId, Guid ChildId, Guid RecordId);
}
