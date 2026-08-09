using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Jobs;
using FluxKnowledge.Domain.Pipeline;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.Inference;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Infrastructure.Usearch.Search;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class SqlToUsearchRebuildTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Rebuild_from_sql_keeps_a_retained_source_pipeline_record_searchable()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "baseline source");
        var retainedRecordId = await environment.AddRetainedAndPumpAsync("retained rebuild source");
        var active = await environment.ActiveGenerationAsync();
        Directory.Delete(environment.IndexRoot, recursive: true);

        _ = await environment.Builder.RebuildFromSqlAsync(active.Id, CancellationToken.None);
        var query = await environment.Embeddings.CreateEmbeddingAsync("retained rebuild", CancellationToken.None);
        var matches = await environment.Reader.SearchAsync(query.Values, 10, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();

        var retainedVectorId = await (
                from vector in context.Vectors
                join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
                where artifact.PipelineRecordId == retainedRecordId
                select vector.VectorId)
            .SingleAsync();
        var rebuiltMembership = await context.IndexGenerationVectors
            .Where(member => member.GenerationId == active.Id)
            .Select(member => member.VectorId)
            .ToListAsync();
        var returnedVectorIds = matches.Select(match => match.VectorId).ToArray();
        var returnedRecordByVectorId = await (
                from vector in context.Vectors
                join chunk in context.TextChunks on vector.TextChunkId equals chunk.Id
                join artifact in context.Artifacts on chunk.ArtifactId equals artifact.Id
                where returnedVectorIds.Contains(vector.VectorId)
                select new { vector.VectorId, artifact.PipelineRecordId })
            .ToDictionaryAsync(value => value.VectorId, value => value.PipelineRecordId);

        Assert.Contains(retainedVectorId, rebuiltMembership);
        Assert.Contains(matches, match => match.VectorId == retainedVectorId);
        Assert.Equal(retainedRecordId, returnedRecordByVectorId[retainedVectorId]);
    }

    [NativeSqlServerFact]
    public async Task Publish_snapshot_keeps_an_older_unsuppressed_retained_record_when_a_later_retained_revision_is_suppressed()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "retained publish snapshot");
        var now = DateTimeOffset.UtcNow;
        var suppressedRevisionId = Guid.NewGuid();
        var olderRecordId = await environment.AddRetainedAndPumpAsync("retained publish source");
        IReadOnlyList<CanonicalVector> expectedMembership;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var olderRecord = await context.PipelineRecords.SingleAsync(record => record.Id == olderRecordId);
            Assert.NotNull(olderRecord.SourceRevisionId);
        }
        expectedMembership = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        Assert.NotEmpty(expectedMembership);

        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var olderRecord = await context.PipelineRecords.SingleAsync(record => record.Id == olderRecordId);
            var olderRevision = await context.SourceRevisions.SingleAsync(revision => revision.Id == olderRecord.SourceRevisionId!.Value);
            context.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = suppressedRevisionId, SourceRootId = olderRevision.SourceRootId,
                StableSourceIdentity = olderRevision.StableSourceIdentity, Revision = olderRevision.Revision + 1,
                ContentSha256 = olderRecord.ContentHash, CanonicalPath = "C:\\retained-publish\\two.txt",
                Classification = "AcceptedUtf8Text", Extension = ".txt", ByteLength = 1, DiscoveredAtUtc = now,
                SuppressedAtUtc = now, DiscoveryEvidenceJson = "{}"
            });
            context.PipelineRecords.Add(new PipelineRecordEntity
            {
                Id = Guid.NewGuid(), SourceIdentityId = olderRecord.SourceIdentityId, SourceRevisionId = suppressedRevisionId,
                Revision = olderRecord.Revision + 1, ContentHash = olderRecord.ContentHash,
                RootLineageRecordId = olderRecord.RootLineageRecordId, ParentRevisionRecordId = olderRecord.Id,
                CurrentStage = (int)PipelineStage.Publish, RegisteredAtUtc = now
            });
            await context.SaveChangesAsync();
        }

        var candidate = new IndexGenerationCandidateSnapshot(
            new IndexGenerationDescriptor(
                Guid.NewGuid(),
                expectedMembership[0].ModelFingerprint,
                expectedMembership[0].Dimensions,
                "retained-publish-snapshot",
                UsearchGenerationValidator.ComputeChecksum(
                    expectedMembership[0].ModelFingerprint,
                    expectedMembership[0].Dimensions,
                    expectedMembership),
                expectedMembership.Count),
            expectedMembership);
        var request = await ClaimPublishAsync(environment, candidate);

        _ = await new SqlStageTransitionStore(environment.Factory).TransitionAsync(request, CancellationToken.None);

        Assert.Equal(candidate.Generation.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var verification = await environment.Factory.CreateDbContextAsync();
        var members = await verification.IndexGenerationVectors
            .Where(member => member.GenerationId == candidate.Generation.Id)
            .Select(member => member.VectorId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(expectedMembership.Select(vector => vector.VectorId).OrderBy(id => id), members);
    }

    [NativeSqlServerFact]
    public async Task Rebuild_after_index_root_deletion_uses_sql_membership_and_keeps_the_active_generation_searchable()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first document");
        var active = await environment.ActiveGenerationAsync();
        var query = await environment.Embeddings.CreateEmbeddingAsync("first", CancellationToken.None);

        Directory.Delete(environment.IndexRoot, recursive: true);
        var rebuilt = await environment.Builder.RebuildFromSqlAsync(active.Id, CancellationToken.None);
        var pointer = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        var matches = await environment.Reader.SearchAsync(query.Values, 5, CancellationToken.None);

        Assert.Equal(active.Id, rebuilt.Id);
        Assert.Equal(active.Id, pointer);
        Assert.True(File.Exists(Path.Combine(rebuilt.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.NotEmpty(matches);
        Assert.Equal(active.VectorCount, rebuilt.VectorCount);
    }

    [NativeSqlServerFact]
    public async Task Candidate_validation_failure_preserves_the_prior_active_pointer_and_immutable_directory()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first document");
        await environment.AddAndPumpAsync("second document");
        var active = await environment.ActiveGenerationAsync();
        var activePath = active.IndexPath;
        var failing = new UsearchGenerationBuilder(
            environment.Store,
            new UsearchIndexOptions(environment.IndexRoot),
            new ThrowingValidator());

        await Assert.ThrowsAsync<IndexGenerationValidationException>(
            async () => await failing.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(activePath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Hosted_pipeline_persists_canonical_chunks_stable_vectors_membership_and_active_generation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "cafe\u0301\r\nline\r");
        var receipt = environment.LastReceipt;
        Assert.NotNull(receipt);
        await using var context = await environment.Factory.CreateDbContextAsync();

        var canonical = await context.Artifacts.SingleAsync(artifact =>
            artifact.PipelineRecordId == receipt!.PipelineRecordId.Value &&
            artifact.Stage == (int)FluxKnowledge.Domain.Pipeline.PipelineStage.CanonicalIndex);
        var chunks = await context.TextChunks.Where(chunk => chunk.ArtifactId == canonical.Id).OrderBy(chunk => chunk.Ordinal).ToListAsync();
        var vectors = await context.Vectors.OrderBy(vector => vector.VectorId).ToListAsync();
        var active = await context.IndexState.SingleAsync(state => state.Id == 1);
        var membership = await context.IndexGenerationVectors.Where(member => member.GenerationId == active.ActiveIndexGenerationId).ToListAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == receipt.PipelineRecordId.Value);

        Assert.Equal("café\nline\n", canonical.SearchText);
        Assert.NotEmpty(chunks);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, vector => Assert.True(vector.VectorId > 0));
        Assert.NotNull(active.ActiveIndexGenerationId);
        Assert.Equal((int)PipelineStage.Publish, record.CurrentStage);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal(vectors.Select(vector => vector.VectorId).Order(), membership.Select(member => member.VectorId).Order());
        var generation = await context.IndexGenerations.SingleAsync(generation => generation.Id == active.ActiveIndexGenerationId);
        Assert.True(File.Exists(Path.Combine(generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Valid_active_generation_with_a_recognised_unplaced_embed_draft_remains_healthy()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.AsNoTracking().SingleAsync(generation =>
            generation.Id != active.Id && generation.IndexPath == string.Empty);
        var activePath = active.IndexPath;
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        var recoveryStore = provider.GetRequiredService<IDerivedIndexRecoveryStore>();

        await coordinator.RunOnceAsync(CancellationToken.None);

        var snapshot = await recoveryStore.ReadActiveAsync(CancellationToken.None);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.DoesNotContain(string.Empty, snapshot.ReferencedIndexPaths);
        Assert.Contains(draft.Id, snapshot.ReferencedGenerationIds);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(activePath, (await environment.Store.GetGenerationAsync(active.Id, CancellationToken.None))!.IndexPath);
        Assert.Equal(string.Empty, (await context.IndexGenerations.SingleAsync(generation => generation.Id == draft.Id)).IndexPath);
    }

    [NativeSqlServerFact]
    public async Task Zero_vector_unplaced_embed_draft_with_valid_provenance_remains_healthy()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.AsNoTracking().SingleAsync(generation =>
            generation.Id != active.Id && generation.IndexPath == string.Empty && generation.VectorCount == 0);
        var vectorReferenceCount = await context.Vectors.CountAsync(vector => vector.IndexGenerationId == draft.Id);
        var membershipCount = await context.IndexGenerationVectors.CountAsync(item => item.GenerationId == draft.Id);
        var activePath = active.IndexPath;
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();
        var recoveryStore = provider.GetRequiredService<IDerivedIndexRecoveryStore>();

        await coordinator.RunOnceAsync(CancellationToken.None);

        var snapshot = await recoveryStore.ReadActiveAsync(CancellationToken.None);
        Assert.Equal(0, vectorReferenceCount);
        Assert.Equal(0, membershipCount);
        Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
        Assert.DoesNotContain(string.Empty, snapshot.ReferencedIndexPaths);
        Assert.Contains(draft.Id, snapshot.ReferencedGenerationIds);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(activePath, (await environment.Store.GetGenerationAsync(active.Id, CancellationToken.None))!.IndexPath);
    }

    [NativeSqlServerFact]
    public async Task Zero_vector_unplaced_embed_draft_allows_each_publish_worker_state()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var draft = await context.IndexGenerations.SingleAsync(candidate =>
            candidate.Id != active.Id && candidate.IndexPath == string.Empty && candidate.VectorCount == 0);
        var artifact = await context.Artifacts.SingleAsync(candidate =>
            candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draft.Id.ToString("D"));
        var publishJob = await context.Jobs.SingleAsync(job =>
            job.PipelineRecordId == artifact.PipelineRecordId &&
            job.SourceRevision == artifact.SourceRevision &&
            job.Stage == (int)PipelineStage.Publish);
        var publishOutbox = await context.OutboxMessages.SingleAsync(message =>
            message.PipelineRecordId == artifact.PipelineRecordId &&
            message.SourceRevision == artifact.SourceRevision &&
            message.Stage == (int)PipelineStage.Publish);

        foreach (var state in new[]
                 {
                     PublicJobState.WorkerQueued,
                     PublicJobState.WorkerProcessing,
                     PublicJobState.Completed,
                     PublicJobState.Failed
                 })
        {
            publishJob.PublicState = (int)state;
            publishOutbox.DispatchedAtUtc = state is PublicJobState.Completed or PublicJobState.Failed
                ? DateTimeOffset.UtcNow
                : null;
            await context.SaveChangesAsync();
            using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
            var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

            await coordinator.RunOnceAsync(CancellationToken.None);

            Assert.Equal(DerivedIndexRecoveryState.Healthy, coordinator.Snapshot.State);
            Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        }
    }

    [NativeSqlServerFact]
    public async Task Unrecognised_nonzero_embed_draft_variants_require_operator_action_without_mutation()
    {
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
            state.ActiveIndexGenerationId = draftId;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).IndexPath = " ";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).ValidatedAtUtc = DateTimeOffset.UtcNow;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).MetadataChecksum = new string('1', 64);
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var vectorId = await context.Vectors.Where(vector => vector.IndexGenerationId == draftId)
                .Select(vector => vector.VectorId)
                .SingleAsync();
            context.IndexGenerationVectors.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.IndexGenerationVectorEntity
            {
                GenerationId = draftId,
                VectorId = vectorId
            });
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            artifact.SearchText = Guid.NewGuid().ToString("D");
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            artifact.ContentType = "application/vnd.fluxknowledge.not-an-embedding-set";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            artifact.Stage = -1;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.PipelineRecords.SingleAsync(record => record.Id == artifact.PipelineRecordId)).CurrentStage =
                (int)PipelineStage.Embed;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Embed)).PublicState = (int)PublicJobState.WorkerQueued;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).Operation = "not-a-publish-operation";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).PublicState = (int)PublicJobState.GpuQueued;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Embed)).DispatchedAtUtc = null;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).PublicState = (int)PublicJobState.Completed;
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Publish)).DispatchedAtUtc = null;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.Jobs.SingleAsync(job =>
                    job.PipelineRecordId == artifact.PipelineRecordId &&
                    job.SourceRevision == artifact.SourceRevision &&
                    job.Stage == (int)PipelineStage.Publish)).PublicState = (int)PublicJobState.WorkerProcessing;
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Publish)).DispatchedAtUtc = DateTimeOffset.UtcNow;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            (await context.OutboxMessages.SingleAsync(message =>
                    message.PipelineRecordId == artifact.PipelineRecordId &&
                    message.SourceRevision == artifact.SourceRevision &&
                    message.Stage == (int)PipelineStage.Publish)).DispatchGeneration++;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).VectorCount++;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == draftId)).ModelFingerprint =
                "incompatible-draft-fingerprint";
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == draftId)).Dimensions++;
        });
        await AssertUnrecognisedNonzeroDraftAsync(async (context, draftId) =>
        {
            (await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == draftId)).IsDeleted = true;
        });
    }

    [NativeSqlServerFact]
    public async Task Cross_record_vector_provenance_in_an_unplaced_draft_requires_operator_action_without_mutation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync("second source");
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var drafts = await context.IndexGenerations
                .Where(candidate => candidate.IndexPath == string.Empty && candidate.VectorCount > 0)
                .OrderBy(candidate => candidate.CreatedAtUtc)
                .ToListAsync();
            var target = drafts[0];
            var source = drafts[1];
            var sourceVector = await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == source.Id);
            context.Vectors.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.VectorEntity
            {
                TextChunkId = sourceVector.TextChunkId,
                ModelFingerprint = sourceVector.ModelFingerprint,
                Dimensions = sourceVector.Dimensions,
                Values = sourceVector.Values.ToArray(),
                TextChunkContentHash = sourceVector.TextChunkContentHash,
                PayloadChecksum = sourceVector.PayloadChecksum,
                SourceRevision = sourceVector.SourceRevision,
                IsDeleted = false,
                IndexGenerationId = target.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            target.VectorCount++;
            draftId = target.Id;
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    [NativeSqlServerFact]
    public async Task Source_revision_mismatch_in_an_unplaced_draft_requires_operator_action_without_mutation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAtPathAsync("second source revision", "initial.txt");
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var drafts = await context.IndexGenerations
                .Where(candidate => candidate.IndexPath == string.Empty && candidate.VectorCount > 0)
                .ToListAsync();
            var draftArtifacts = await context.Artifacts
                .Where(candidate => candidate.Stage == (int)PipelineStage.Embed)
                .ToListAsync();
            var ordered = drafts
                .Select(draft => new
                {
                    Draft = draft,
                    Artifact = draftArtifacts.Single(candidate =>
                        candidate.SearchText == draft.Id.ToString("D"))
                })
                .OrderBy(candidate => candidate.Artifact.SourceRevision)
                .ToArray();
            Assert.Equal(2, ordered.Length);
            Assert.Equal(1, ordered[0].Artifact.SourceRevision);
            Assert.Equal(2, ordered[1].Artifact.SourceRevision);

            var target = ordered[0];
            var source = ordered[1];
            var sourceVector = await context.Vectors.SingleAsync(vector => vector.IndexGenerationId == source.Draft.Id);
            context.Vectors.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.VectorEntity
            {
                TextChunkId = sourceVector.TextChunkId,
                ModelFingerprint = sourceVector.ModelFingerprint,
                Dimensions = sourceVector.Dimensions,
                Values = sourceVector.Values.ToArray(),
                TextChunkContentHash = sourceVector.TextChunkContentHash,
                PayloadChecksum = sourceVector.PayloadChecksum,
                SourceRevision = sourceVector.SourceRevision,
                IsDeleted = false,
                IndexGenerationId = target.Draft.Id,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            target.Draft.VectorCount++;
            draftId = target.Draft.Id;
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    [NativeSqlServerFact]
    public async Task Missing_zero_vector_embed_evidence_requires_operator_action_without_mutation()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync(candidate =>
                candidate.IndexPath == string.Empty && candidate.VectorCount == 0);
            var artifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draft.Id.ToString("D"));
            artifact.ContentHash = Convert.ToHexStringLower(SHA256.HashData("not-empty"u8));
            draftId = draft.Id;
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    [NativeSqlServerFact]
    public async Task Unrecognised_zero_vector_embed_draft_variants_require_operator_action_without_mutation()
    {
        await AssertUnrecognisedZeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).ModelFingerprint =
                "incompatible-zero-draft-fingerprint";
        });
        await AssertUnrecognisedZeroDraftAsync(async (context, draftId) =>
        {
            (await context.IndexGenerations.SingleAsync(candidate => candidate.Id == draftId)).Dimensions++;
        });
        await AssertUnrecognisedZeroDraftAsync(async (context, draftId) =>
        {
            var embedArtifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.Stage == (int)PipelineStage.Embed && candidate.SearchText == draftId.ToString("D"));
            var canonicalArtifact = await context.Artifacts.SingleAsync(candidate =>
                candidate.PipelineRecordId == embedArtifact.PipelineRecordId &&
                candidate.SourceRevision == embedArtifact.SourceRevision &&
                candidate.Stage == (int)PipelineStage.CanonicalIndex);
            context.TextChunks.Add(new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.TextChunkEntity
            {
                ArtifactId = canonicalArtifact.Id,
                SourceRevision = embedArtifact.SourceRevision,
                Ordinal = 0,
                StartOffset = 0,
                Length = 1,
                Content = "x",
                ContentHash = Convert.ToHexStringLower(SHA256.HashData("x"u8))
            });
        });
    }

    [NativeSqlServerFact]
    public async Task Worker_produced_vector_round_trips_through_hybrid_search_and_preserves_stale_chunk_protection()
    {
        const string sourceText = "restart the native worker safely";
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, sourceText);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var vector = await context.Vectors.SingleAsync();
        var chunk = await context.TextChunks.SingleAsync(candidate => candidate.Id == vector.TextChunkId);

        Assert.Equal(chunk.ContentHash, vector.TextChunkContentHash);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(vector.Values)),
            vector.PayloadChecksum);
        Assert.NotEqual(vector.TextChunkContentHash, vector.PayloadChecksum);

        var lexical = new SqlFullTextSearch(environment.Factory);
        IReadOnlyList<RankedCandidate> lexicalCandidates = [];
        var fullTextDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (lexicalCandidates.Count == 0 && DateTimeOffset.UtcNow < fullTextDeadline)
        {
            lexicalCandidates = await lexical.SearchAsync("restart", 5, CancellationToken.None);
            if (lexicalCandidates.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        Assert.Contains(lexicalCandidates, candidate => candidate.VectorId == vector.VectorId);

        var search = new HybridSearchService(
            lexical,
            new UsearchNearestNeighbourQuery(environment.Embeddings, environment.Reader),
            new SqlSearchHydrator(environment.Factory),
            environment.Store);
        var response = await search.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);
        var hit = Assert.Single(response.Results);
        Assert.Contains(sourceText, hit.Snippet, StringComparison.Ordinal);
        Assert.Contains(hit.Explanation, item => item.StartsWith("lexical:", StringComparison.Ordinal));
        Assert.Contains(hit.Explanation, item => item.StartsWith("semantic:", StringComparison.Ordinal));

        vector.TextChunkContentHash = new string('f', 64);
        await context.SaveChangesAsync();

        var staleResponse = await search.SearchAsync(
            new SearchRequest("restart", 5, "local_first", null, null, null),
            CancellationToken.None);

        Assert.Empty(staleResponse.Results);
    }

    [NativeSqlServerFact]
    public async Task Second_corpus_publish_retains_vectors_from_two_independent_current_sources()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "alpha source");
        await environment.AddAndPumpAsync("bravo source");
        await using var context = await environment.Factory.CreateDbContextAsync();
        var activeId = (await context.IndexState.SingleAsync(state => state.Id == 1)).ActiveIndexGenerationId;
        var membership = await context.IndexGenerationVectors
            .Where(member => member.GenerationId == activeId)
            .Select(member => member.VectorId)
            .OrderBy(id => id)
            .ToListAsync();
        var allVectors = await context.Vectors.OrderBy(vector => vector.VectorId).Select(vector => vector.VectorId).ToListAsync();

        Assert.Equal(allVectors, membership);
        Assert.True(membership.Count >= 2);
    }

    [NativeSqlServerFact]
    public async Task Prebuilt_snapshot_is_superseded_by_a_newer_publish_without_pointer_regression()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        var stale = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await environment.AddAndPumpAsync("second source");
        var active = await environment.ActiveGenerationAsync();
        var transition = new SqlStageTransitionStore(environment.Factory);
        var request = await ClaimPublishAsync(environment, stale);

        var result = await transition.TransitionAsync(
            request,
            CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(result.ExistingTransition);
        Assert.True(record.CompletionCriteriaMet);
        Assert.NotEqual(stale.Generation.Id, active.Id);
        Assert.True(File.Exists(Path.Combine(stale.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
    }

    [NativeSqlServerFact]
    public async Task Completed_publish_replay_does_not_duplicate_membership_or_replace_a_valid_placement()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "replay source");
        var active = await environment.ActiveGenerationAsync();
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var transition = new SqlStageTransitionStore(environment.Factory);
        var request = await ClaimPublishAsync(environment, candidate);
        var first = await transition.TransitionAsync(request, CancellationToken.None);
        var replay = await transition.TransitionAsync(request, CancellationToken.None);
        await using var context = await environment.Factory.CreateDbContextAsync();
        var members = await context.IndexGenerationVectors.Where(member => member.GenerationId == active.Id).ToListAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(first.ExistingTransition);
        Assert.True(replay.ExistingTransition);
        Assert.True(record.CompletionCriteriaMet);
        Assert.Equal(first.ArtifactId, replay.ArtifactId);
        Assert.Equal(active.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(members.Select(member => member.VectorId).Distinct().Count(), members.Count);
        Assert.True(File.Exists(Path.Combine(candidate.Generation.IndexPath, UsearchGenerationValidator.IndexFileName)));
    }

    [NativeSqlServerFact]
    public async Task Failed_terminal_publish_rolls_back_the_completion_flag()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "failed publish source");
        var active = await environment.ActiveGenerationAsync();
        var vectors = await environment.Store.ReadEligibleVectorsAsync(CancellationToken.None);
        var incompatible = active with { IndexPath = active.IndexPath + "-incompatible" };
        var request = await ClaimPublishAsync(
            environment,
            new IndexGenerationCandidateSnapshot(incompatible, vectors));

        await Assert.ThrowsAsync<IndexGenerationStaleException>(
            async () => await new SqlStageTransitionStore(environment.Factory)
                .TransitionAsync(request, CancellationToken.None));

        await using var context = await environment.Factory.CreateDbContextAsync();
        var record = await context.PipelineRecords.SingleAsync(candidate =>
            candidate.Id == request.CurrentJob.PipelineRecordId.Value);

        Assert.False(record.CompletionCriteriaMet);
        Assert.DoesNotContain(
            await context.Artifacts.ToListAsync(),
            artifact => artifact.Id == request.Artifact.Id);
    }

    [NativeSqlServerFact]
    public async Task Concurrent_same_candidate_activation_creates_one_generation_and_membership_snapshot()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "concurrent source");
        await PrepareUntrackedCandidateAsync(environment);
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        var barrier = new ActivationBarrier();
        var first = new SqlStageTransitionStore(environment.Factory, barrier);
        var second = new SqlStageTransitionStore(environment.Factory, barrier);
        var firstRequest = await ClaimPublishAsync(environment, candidate);
        var secondRequest = await ClaimPublishAsync(environment, candidate);

        var firstTransition = first.TransitionAsync(firstRequest, CancellationToken.None).AsTask();
        var secondTransition = second.TransitionAsync(secondRequest, CancellationToken.None).AsTask();
        var barrierReached = await Task.WhenAny(
            barrier.ArtifactWritten.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));
        barrier.Release();
        var transitions = await Task.WhenAll(firstTransition, secondTransition);

        Assert.Same(barrier.ArtifactWritten.Task, barrierReached);
        Assert.All(transitions, transition => Assert.False(transition.ExistingTransition));
        Assert.Equal(candidate.Generation.Id, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        await using var context = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await context.IndexGenerations.Where(generation => generation.Id == candidate.Generation.Id).ToListAsync());
        Assert.Equal(candidate.Vectors.Count, await context.IndexGenerationVectors.CountAsync(
            membership => membership.GenerationId == candidate.Generation.Id));
    }

    [NativeSqlServerFact]
    public async Task Existing_generation_with_empty_membership_is_repaired_idempotently()
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "empty membership source");
        var candidate = await environment.Builder.BuildAndPlaceAsync(Guid.NewGuid(), CancellationToken.None);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            await context.IndexGenerationVectors
                .Where(membership => membership.GenerationId == candidate.Generation.Id)
                .ExecuteDeleteAsync();
        }

        await new SqlStageTransitionStore(environment.Factory).TransitionAsync(
            await ClaimPublishAsync(environment, candidate),
            CancellationToken.None);

        await using var verification = await environment.Factory.CreateDbContextAsync();
        Assert.Single(await verification.IndexGenerations.Where(generation => generation.Id == candidate.Generation.Id).ToListAsync());
        Assert.Equal(candidate.Vectors.Count, await verification.IndexGenerationVectors.CountAsync(
            membership => membership.GenerationId == candidate.Generation.Id));
    }

    private static async Task PrepareUntrackedCandidateAsync(PipelineEnvironment environment)
    {
        var active = await environment.ActiveGenerationAsync();
        await using var context = await environment.Factory.CreateDbContextAsync();
        var origin = new FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities.IndexGenerationEntity
        {
            Id = Guid.NewGuid(),
            ModelFingerprint = active.ModelFingerprint,
            Dimensions = active.Dimensions,
            IndexPath = active.IndexPath,
            MetadataChecksum = active.MetadataChecksum,
            VectorCount = active.VectorCount,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ValidatedAtUtc = DateTimeOffset.UtcNow
        };
        context.IndexGenerations.Add(origin);
        await context.SaveChangesAsync();
        var vectors = await context.Vectors.ToListAsync();
        foreach (var vector in vectors)
        {
            vector.IndexGenerationId = origin.Id;
        }
        var state = await context.IndexState.SingleAsync(candidate => candidate.Id == 1);
        state.ActiveIndexGenerationId = null;
        await context.SaveChangesAsync();
        await context.IndexGenerationVectors
            .Where(membership => membership.GenerationId == active.Id)
            .ExecuteDeleteAsync();
        await context.IndexGenerations
            .Where(generation => generation.Id == active.Id)
            .ExecuteDeleteAsync();
    }

    private async Task<StageTransitionRequest> ClaimPublishAsync(
        PipelineEnvironment environment,
        IndexGenerationCandidateSnapshot candidate)
    {
        var now = DateTimeOffset.UtcNow;
        await SqlTestData.SeedWorkItemAsync(
            _fixture,
            now,
            PublicJobState.WorkerQueued,
            leaseExpiresAtUtc: null,
            stage: PipelineStage.Publish,
            operation: PipelineOperations.Publish);
        var outbox = await new SqlOutboxStore(environment.Factory).ClaimNextDueAsync(
            "task-5-publish-dispatcher",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            [PipelineOperations.Publish],
            CancellationToken.None);
        Assert.NotNull(outbox);
        var job = await new SqlJobClaimStore(environment.Factory).ClaimForDispatchAsync(
            outbox!,
            "task-5-publish-worker",
            now.AddMinutes(1),
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        Assert.NotNull(job);
        return new StageTransitionRequest(
            outbox!,
            job!,
            new StageArtifact(
                Guid.NewGuid(),
                PipelineStage.Publish,
                candidate.Generation.MetadataChecksum,
                "application/vnd.fluxknowledge.usearch-generation",
                candidate.Generation.Id.ToString("N"),
                now),
            null,
            null,
            nameof(SqlToUsearchRebuildTests),
            new IndexingStageOutput(
                ActivateGeneration: candidate.Generation,
                ActivateMembership: candidate.Vectors));
    }

    private async Task AssertUnrecognisedNonzeroDraftAsync(
        Func<FluxKnowledgeDbContext, Guid, Task> mutate)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync(candidate =>
                candidate.IndexPath == string.Empty && candidate.VectorCount > 0);
            draftId = draft.Id;
            await mutate(context, draftId);
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    private async Task AssertUnrecognisedZeroDraftAsync(
        Func<FluxKnowledgeDbContext, Guid, Task> mutate)
    {
        await using var environment = await PipelineEnvironment.CreateAsync(_fixture, "first source");
        await environment.AddAndPumpAsync(string.Empty);
        Guid draftId;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draft = await context.IndexGenerations.SingleAsync(candidate =>
                candidate.IndexPath == string.Empty && candidate.VectorCount == 0);
            draftId = draft.Id;
            await mutate(context, draftId);
            await context.SaveChangesAsync();
        }

        await AssertUnrecognisedDraftDoesNotMutateAsync(environment, draftId);
    }

    private static async Task AssertUnrecognisedDraftDoesNotMutateAsync(
        PipelineEnvironment environment,
        Guid draftId)
    {
        var activeIdBefore = await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None);
        Assert.NotNull(activeIdBefore);
        var activeBefore = await environment.Store.GetGenerationAsync(activeIdBefore!.Value, CancellationToken.None);
        Assert.NotNull(activeBefore);
        DraftState draftBefore;
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            draftBefore = await context.IndexGenerations.AsNoTracking()
                .Where(candidate => candidate.Id == draftId)
                .Select(candidate => new DraftState(
                    candidate.ModelFingerprint,
                    candidate.Dimensions,
                    candidate.IndexPath,
                    candidate.MetadataChecksum,
                    candidate.VectorCount,
                    candidate.ValidatedAtUtc))
                .SingleAsync();
        }
        var evidenceBefore = await ReadRecoveryEvidenceSnapshotAsync(environment.Factory);

        var staging = Path.Combine(environment.IndexRoot, "staging");
        var quarantine = Path.Combine(environment.IndexRoot, "quarantine");
        var stagingBefore = ReadDirectoryEntries(staging);
        var quarantineBefore = ReadDirectoryEntries(quarantine);
        using var provider = CreateRecoveryProvider(environment.Factory, environment.IndexRoot);
        var coordinator = provider.GetRequiredService<DerivedIndexRecoveryCoordinator>();

        await coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DerivedIndexRecoveryState.OperatorActionRequired, coordinator.Snapshot.State);
        Assert.Equal(DerivedIndexRecoveryFailureCategory.ConfigurationInvalid, coordinator.Snapshot.FailureCategory);
        Assert.Null(coordinator.Snapshot.NextRetryAtUtc);
        Assert.Equal(activeIdBefore, await environment.Store.GetActiveGenerationIdAsync(CancellationToken.None));
        Assert.Equal(activeBefore!.IndexPath,
            (await environment.Store.GetGenerationAsync(activeIdBefore.Value, CancellationToken.None))!.IndexPath);
        await using (var context = await environment.Factory.CreateDbContextAsync())
        {
            var draftAfter = await context.IndexGenerations.AsNoTracking()
                .Where(candidate => candidate.Id == draftId)
                .Select(candidate => new DraftState(
                    candidate.ModelFingerprint,
                    candidate.Dimensions,
                    candidate.IndexPath,
                    candidate.MetadataChecksum,
                    candidate.VectorCount,
                    candidate.ValidatedAtUtc))
                .SingleAsync();
            Assert.Equal(draftBefore, draftAfter);
        }
        var evidenceAfter = await ReadRecoveryEvidenceSnapshotAsync(environment.Factory);
        AssertRecoveryEvidenceUnchanged(evidenceBefore, evidenceAfter);

        Assert.Equal(stagingBefore, ReadDirectoryEntries(staging));
        Assert.Equal(quarantineBefore, ReadDirectoryEntries(quarantine));
    }

    private static string[] ReadDirectoryEntries(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(path, entry))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static ServiceProvider CreateRecoveryProvider(
        IDbContextFactory<FluxKnowledgeDbContext> factory,
        string root)
    {
        var services = new ServiceCollection();
        services.AddSingleton(factory);
        services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
        services.AddScoped<SqlPipelineStore>();
        services.AddScoped<IIndexGenerationStore>(provider => provider.GetRequiredService<SqlPipelineStore>());
        services.AddSingleton(UsearchIndexOptions.FromConfiguredRoot(root));
        services.AddSingleton<UsearchGenerationValidator>();
        services.AddScoped<UsearchGenerationBuilder>();
        services.AddSingleton<DerivedIndexFileSystem>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DerivedIndexRecoveryCoordinator>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record DraftState(
        string ModelFingerprint,
        int Dimensions,
        string IndexPath,
        string MetadataChecksum,
        long VectorCount,
        DateTimeOffset? ValidatedAtUtc);

    private static async Task<RecoveryEvidenceSnapshot> ReadRecoveryEvidenceSnapshotAsync(
        IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        await using var context = await factory.CreateDbContextAsync();
        var indexStates = await context.IndexState.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var generations = await context.IndexGenerations.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var records = await context.PipelineRecords.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var artifacts = await context.Artifacts.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var chunks = await context.TextChunks.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var vectors = await context.Vectors.AsNoTracking().OrderBy(candidate => candidate.VectorId).ToListAsync();
        var memberships = await context.IndexGenerationVectors.AsNoTracking()
            .OrderBy(candidate => candidate.GenerationId)
            .ThenBy(candidate => candidate.VectorId)
            .ToListAsync();
        var jobs = await context.Jobs.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();
        var outbox = await context.OutboxMessages.AsNoTracking().OrderBy(candidate => candidate.Id).ToListAsync();

        return new RecoveryEvidenceSnapshot(
            indexStates.Select(candidate => new IndexStateEvidence(
                candidate.Id,
                candidate.ActiveIndexGenerationId,
                candidate.UpdatedAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            generations.Select(candidate => new GenerationEvidence(
                candidate.Id,
                candidate.ModelFingerprint,
                candidate.Dimensions,
                candidate.IndexPath,
                candidate.MetadataChecksum,
                candidate.VectorCount,
                candidate.CreatedAtUtc,
                candidate.ValidatedAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            records.Select(candidate => new PipelineRecordEvidence(
                candidate.Id,
                candidate.SourceIdentityId,
                candidate.Revision,
                candidate.ContentHash,
                candidate.RootLineageRecordId,
                candidate.ParentRevisionRecordId,
                candidate.CurrentStage,
                candidate.CompletionCriteriaMet,
                candidate.IsDeleted,
                candidate.RegisteredAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            artifacts.Select(candidate => new ArtifactEvidence(
                candidate.Id,
                candidate.PipelineRecordId,
                candidate.SourceRevision,
                candidate.Stage,
                candidate.ContentHash,
                candidate.ContentType,
                candidate.SearchText,
                candidate.CreatedAtUtc)).ToArray(),
            chunks.Select(candidate => new ChunkEvidence(
                candidate.Id,
                candidate.ArtifactId,
                candidate.SourceRevision,
                candidate.Ordinal,
                candidate.StartOffset,
                candidate.Length,
                HashText(candidate.Content),
                candidate.ContentHash)).ToArray(),
            vectors.Select(candidate => new VectorEvidence(
                candidate.VectorId,
                candidate.TextChunkId,
                candidate.ModelFingerprint,
                candidate.Dimensions,
                HashBytes(candidate.Values),
                candidate.TextChunkContentHash,
                candidate.PayloadChecksum,
                candidate.SourceRevision,
                candidate.IsDeleted,
                candidate.IndexGenerationId,
                candidate.CreatedAtUtc,
                HashBytes(candidate.RowVersion))).ToArray(),
            memberships.Select(candidate => new MembershipEvidence(candidate.GenerationId, candidate.VectorId)).ToArray(),
            jobs.Select(candidate => new JobEvidence(
                candidate.Id,
                candidate.PipelineRecordId,
                candidate.SourceRevision,
                candidate.Stage,
                candidate.Operation,
                candidate.PublicState,
                candidate.DueAtUtc,
                candidate.AttemptCount,
                candidate.LeaseOwner,
                candidate.LeaseExpiresAtUtc,
                candidate.LeaseGeneration,
                HashText(candidate.Reason),
                HashText(candidate.ErrorDetails),
                HashBytes(candidate.RowVersion))).ToArray(),
            outbox.Select(candidate => new OutboxEvidence(
                candidate.Id,
                candidate.PipelineRecordId,
                candidate.SourceRevision,
                candidate.Stage,
                candidate.Operation,
                candidate.DispatchGeneration,
                candidate.IdempotencyKey,
                candidate.DueAtUtc,
                candidate.CreatedAtUtc,
                candidate.DispatchedAtUtc,
                candidate.LeaseOwner,
                candidate.LeaseExpiresAtUtc,
                candidate.LeaseGeneration,
                HashBytes(candidate.RowVersion))).ToArray());
    }

    private static void AssertRecoveryEvidenceUnchanged(
        RecoveryEvidenceSnapshot before,
        RecoveryEvidenceSnapshot after)
    {
        Assert.Equal(before.IndexStates, after.IndexStates);
        Assert.Equal(before.Generations, after.Generations);
        Assert.Equal(before.PipelineRecords, after.PipelineRecords);
        Assert.Equal(before.Artifacts, after.Artifacts);
        Assert.Equal(before.Chunks, after.Chunks);
        Assert.Equal(before.Vectors, after.Vectors);
        Assert.Equal(before.Memberships, after.Memberships);
        Assert.Equal(before.Jobs, after.Jobs);
        Assert.Equal(before.Outbox, after.Outbox);
    }

    private static string HashBytes(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static string? HashText(string? value) =>
        value is null ? null : HashBytes(System.Text.Encoding.UTF8.GetBytes(value));

    private sealed record RecoveryEvidenceSnapshot(
        IReadOnlyList<IndexStateEvidence> IndexStates,
        IReadOnlyList<GenerationEvidence> Generations,
        IReadOnlyList<PipelineRecordEvidence> PipelineRecords,
        IReadOnlyList<ArtifactEvidence> Artifacts,
        IReadOnlyList<ChunkEvidence> Chunks,
        IReadOnlyList<VectorEvidence> Vectors,
        IReadOnlyList<MembershipEvidence> Memberships,
        IReadOnlyList<JobEvidence> Jobs,
        IReadOnlyList<OutboxEvidence> Outbox);

    private sealed record IndexStateEvidence(
        int Id,
        Guid? ActiveGenerationId,
        DateTimeOffset UpdatedAtUtc,
        string RowVersion);

    private sealed record GenerationEvidence(
        Guid Id,
        string ModelFingerprint,
        int Dimensions,
        string IndexPath,
        string MetadataChecksum,
        long VectorCount,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? ValidatedAtUtc,
        string RowVersion);

    private sealed record PipelineRecordEvidence(
        Guid Id,
        Guid SourceIdentityId,
        long Revision,
        string ContentHash,
        Guid RootLineageRecordId,
        Guid? ParentRevisionRecordId,
        int CurrentStage,
        bool CompletionCriteriaMet,
        bool IsDeleted,
        DateTimeOffset RegisteredAtUtc,
        string RowVersion);

    private sealed record ArtifactEvidence(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string ContentHash,
        string ContentType,
        string SearchText,
        DateTimeOffset CreatedAtUtc);

    private sealed record ChunkEvidence(
        long Id,
        Guid ArtifactId,
        long SourceRevision,
        int Ordinal,
        int StartOffset,
        int Length,
        string? ContentHashEvidence,
        string ContentHash);

    private sealed record VectorEvidence(
        long Id,
        long TextChunkId,
        string ModelFingerprint,
        int Dimensions,
        string ValuesHash,
        string TextChunkContentHash,
        string PayloadChecksum,
        long SourceRevision,
        bool IsDeleted,
        Guid GenerationId,
        DateTimeOffset CreatedAtUtc,
        string RowVersion);

    private sealed record MembershipEvidence(Guid GenerationId, long VectorId);

    private sealed record JobEvidence(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string Operation,
        int PublicState,
        DateTimeOffset DueAtUtc,
        int AttemptCount,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        long LeaseGeneration,
        string? ReasonHash,
        string? ErrorDetailsHash,
        string RowVersion);

    private sealed record OutboxEvidence(
        Guid Id,
        Guid PipelineRecordId,
        long SourceRevision,
        int Stage,
        string Operation,
        long DispatchGeneration,
        string IdempotencyKey,
        DateTimeOffset DueAtUtc,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? DispatchedAtUtc,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        long LeaseGeneration,
        string RowVersion);

    private sealed class ThrowingValidator : UsearchGenerationValidator
    {
        public override void Validate(string directory, IndexGenerationDescriptor expected, IReadOnlyList<CanonicalVector> vectors) =>
            throw new IndexGenerationValidationException("injected candidate validation failure");
    }

    private sealed class ActivationBarrier : IStageTransitionFailureInjector
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ArtifactWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AfterArtifactWrittenAsync(CancellationToken cancellationToken)
        {
            ArtifactWritten.TrySetResult(true);

            return new ValueTask(_release.Task.WaitAsync(cancellationToken));
        }

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class PipelineEnvironment : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _ingressRoot;
        private readonly string _artifactRoot;
        private PipelineEnvironment(ServiceProvider provider, string ingressRoot, string artifactRoot, string indexRoot, IDbContextFactory<FluxKnowledgeDbContext> factory)
        {
            _provider = provider; _ingressRoot = ingressRoot; _artifactRoot = artifactRoot; IndexRoot = indexRoot; Factory = factory;
            Store = new SqlPipelineStore(factory); Builder = _provider.GetRequiredService<UsearchGenerationBuilder>();
            Reader = _provider.GetRequiredService<UsearchAnnIndex>(); Embeddings = _provider.GetRequiredService<IEmbeddingProvider>();
        }
        public string IndexRoot { get; }
        public IDbContextFactory<FluxKnowledgeDbContext> Factory { get; }
        public SqlPipelineStore Store { get; }
        public UsearchGenerationBuilder Builder { get; }
        public UsearchAnnIndex Reader { get; }
        public IEmbeddingProvider Embeddings { get; }
        public FluxKnowledge.Application.Contracts.RegisterUtf8FileResult? LastReceipt { get; private set; }

        public static async Task<PipelineEnvironment> CreateAsync(NativeSqlServerFixture fixture, string text)
        {
            await SqlTestData.ClearPipelineAsync(fixture);
            var ingress = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIngress_{Guid.NewGuid():N}");
            var artifact = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeRetained_{Guid.NewGuid():N}");
            var index = Path.Combine(Path.GetTempPath(), $"FluxKnowledgeIndexes_{Guid.NewGuid():N}");
            Directory.CreateDirectory(ingress);
            Directory.CreateDirectory(artifact);
            var services = new ServiceCollection();
            services.AddSingleton(SqlTestData.CreateFactory(fixture));
            services.AddSingleton<IUtf8FileSourceReader>(new Utf8FileSourceReader(new LocalIngressOptions([ingress])));
            services.AddScoped<IRetainedSourceReader>(provider => new SqlRetainedSourceReader(
                provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>(), artifact));
            services.AddFluxKnowledgeOutboxWorkers();
            services.AddSingleton<IEmbeddingProvider, DeterministicTokenHashEmbeddingProvider>();
            services.AddFluxKnowledgeUsearch(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Usearch:RootPath"] = index }).Build());
            services.AddScoped<IStageWorker, CanonicalIndexStageWorker>();
            services.AddScoped<IStageWorker, EmbedStageWorker>();
            services.AddScoped<IStageWorker, PublishStageWorker>();
            var provider = services.BuildServiceProvider();
            var environment = new PipelineEnvironment(provider, ingress, artifact, index, provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>());
            await environment.AddAndPumpAtPathAsync(text, "initial.txt");
            return environment;
        }

        public async Task AddAndPumpAsync(string text)
        {
            await AddAndPumpAtPathAsync(text, $"{Guid.NewGuid():N}.txt");
        }

        public async Task AddAndPumpAtPathAsync(string text, string fileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            {
                throw new ArgumentException("The test ingress file name must not contain a directory.", nameof(fileName));
            }

            var path = Path.Combine(_ingressRoot, fileName);
            await File.WriteAllTextAsync(path, text);
            using var scope = _provider.CreateScope();
            LastReceipt = await scope.ServiceProvider.GetRequiredService<RegisterUtf8FileHandler>().HandleAsync(new(path, "native-sql-test", null), CancellationToken.None);
            await _provider.GetRequiredService<OutboxPumpService>().PumpOnceAsync(CancellationToken.None);
        }

        public async Task<Guid> AddRetainedAndPumpAsync(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var rootId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(_artifactRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(_artifactRoot, relative), bytes);
            var activity = SourceActivity.Create(new SourceRevisionId(revisionId), SourceActivityKind.TextExtraction,
                ExecutionClass.InProcess, "phase-3a-v1", hash, null, null);
            var now = DateTimeOffset.UtcNow;
            await using (var context = await Factory.CreateDbContextAsync())
            {
                context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {
                    Id = rootId, CanonicalPath = $"C:\\retained-rebuild\\{rootId:N}", DisplayName = "Retained", State = 0,
                    Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false,
                    MaximumFileBytes = 16 * 1024 * 1024, AllowedClassificationsJson = "[]", CrawlMode = 0,
                    ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                context.SourceRevisions.Add(new SourceRevisionEntity
                {
                    Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"retained:{revisionId:N}", Revision = 1,
                    ContentSha256 = hash, CanonicalPath = $"C:\\retained-rebuild\\{revisionId:N}.txt", Classification = "AcceptedUtf8Text",
                    Extension = ".txt", ByteLength = bytes.Length, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
                });
                context.SourceArtifacts.Add(new SourceArtifactEntity
                {
                    Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relative,
                    ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
                });
                context.SourceActivities.Add(new SourceActivityEntity
                {
                    Id = activity.Id.Value, SourceRevisionId = revisionId, ActivityKind = (int)activity.Kind,
                    ExecutionClass = (int)activity.ExecutionClass, ProcessorVersion = activity.ProcessorVersion,
                    InputFingerprint = activity.InputFingerprint, State = (int)activity.State, CreatedAtUtc = now, UpdatedAtUtc = now
                });
                await context.SaveChangesAsync();
            }
            using (var scope = _provider.CreateScope())
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<RetainedTextActivityPlanner>()
                    .PlanAsync(activity, CancellationToken.None));
            }
            await _provider.GetRequiredService<OutboxPumpService>().PumpOnceAsync(CancellationToken.None);
            await using var verification = await Factory.CreateDbContextAsync();
            return (await verification.PipelineRecords.SingleAsync(record => record.SourceRevisionId == revisionId)).Id;
        }

        public async Task<IndexGenerationDescriptor> ActiveGenerationAsync()
        {
            var id = await Store.GetActiveGenerationIdAsync(CancellationToken.None);
            Assert.NotNull(id);
            return (await Store.GetGenerationAsync(id!.Value, CancellationToken.None))!;
        }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            if (Directory.Exists(_ingressRoot)) Directory.Delete(_ingressRoot, true);
            if (Directory.Exists(_artifactRoot)) Directory.Delete(_artifactRoot, true);
            if (Directory.Exists(IndexRoot)) Directory.Delete(IndexRoot, true);
            return ValueTask.CompletedTask;
        }
    }
}
