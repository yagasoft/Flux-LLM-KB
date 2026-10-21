using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed class DerivedChildRevisionIntegrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    private readonly FluxKnowledge.Testing.TestResources _resources = new();
    public Task InitializeAsync() => SqlTestData.ClearPipelineAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerTheory]
    [InlineData("ooxml", false)]
    [InlineData("ooxml", true)]
    [InlineData("pdf", false)]
    [InlineData("media", false)]
    [InlineData("media", true)]
    public async Task Two_parent_revisions_publish_separate_children_with_their_parent_revision_and_correct_artifact(
        string kind, bool changedOutput)
    {
        var root = CreatePrivateRoot();
        try
        {
            var first = await SeedAsync(root, kind, Container(kind, "first", false));
            var second = await SeedAsync(root, kind, Container(kind, "second", changedOutput), first.RootId, revision: 2);
            var store = CreateStore();
            var firstClaim = await PromoteAndClaimAsync(store, first, kind);
            var firstCompletion = await ProcessAsync(root, kind, firstClaim);
            try { Assert.True(await store.CommitAsync(firstClaim, firstCompletion, CancellationToken.None)); }
            finally { await ReleaseAsync(firstCompletion); }
            var secondClaim = await PromoteAndClaimAsync(store, second, kind);
            var secondCompletion = await ProcessAsync(root, kind, secondClaim);
            try { Assert.True(await store.CommitAsync(secondClaim, secondCompletion, CancellationToken.None)); }
            finally { await ReleaseAsync(secondCompletion); }

            await using var context = CreateContext();
            var children = await context.SourceRevisions.Where(value => value.ParentSourceRevisionId == first.RevisionId ||
                value.ParentSourceRevisionId == second.RevisionId).ToArrayAsync();
            Assert.Equal(2, children.Length);
            var firstChild = Assert.Single(children, value => value.ParentSourceRevisionId == first.RevisionId);
            var secondChild = Assert.Single(children, value => value.ParentSourceRevisionId == second.RevisionId);
            Assert.Equal(1, firstChild.Revision);
            Assert.Equal(2, secondChild.Revision);
            Assert.Equal(firstChild.StableSourceIdentity, secondChild.StableSourceIdentity);
            Assert.NotEqual(firstChild.CanonicalPath, secondChild.CanonicalPath);
            if (kind != "pdf")
            {
                if (changedOutput) Assert.NotEqual(firstChild.ContentSha256, secondChild.ContentSha256);
                else Assert.Equal(firstChild.ContentSha256, secondChild.ContentSha256);
            }
            foreach (var child in children)
            {
                var artifact = await context.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == child.Id);
                Assert.Equal(child.ContentSha256, artifact.ContentSha256);
                Assert.Equal(child.ByteLength, artifact.ByteLength);
                Assert.Equal(child.ContentSha256, Hash(await File.ReadAllBytesAsync(Path.Combine(root, artifact.StoreRelativePath))));
                Assert.Single(await context.SourceActivities.Where(value => value.SourceRevisionId == child.Id).ToArrayAsync());
                var branch = await context.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == child.ParentSourceRevisionId);
                Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
                Assert.Equal(child.Id, (await context.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id)).ChildSourceRevisionId);
                if (kind == "pdf")
                {
                    var parentArtifact = await context.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == child.ParentSourceRevisionId);
                    Assert.Equal(parentArtifact.StoreRelativePath, artifact.StoreRelativePath);
                    Assert.Equal(DocumentProcessingInput.PdfClassification, child.Classification);
                }
            }
            Assert.Equal(secondCompletion.ReceiptFingerprint,
                (await context.SourceProcessorBranches.SingleAsync(value => value.Id == secondClaim.BranchId)).CompletionReceiptFingerprint);
            if (kind != "pdf" && !changedOutput) Assert.Equal(firstCompletion.ReceiptFingerprint, secondCompletion.ReceiptFingerprint);
        }
        finally { DeletePrivateRoot(root); }
    }

    [NativeSqlServerTheory]
    [InlineData("ooxml")]
    [InlineData("pdf")]
    [InlineData("media")]
    public async Task Concurrent_replay_reuses_the_exact_legacy_child_and_its_artifact_activity_and_receipt(string kind)
    {
        var root = CreatePrivateRoot();
        try
        {
            var seeded = await SeedAsync(root, kind, Container(kind, "legacy", false), revision: 2);
            var store = CreateStore();
            var claim = await PromoteAndClaimAsync(store, seeded, kind);
            var completion = await ProcessAsync(root, kind, claim);
            try
            {
                var member = Assert.Single(completion.Members);
                var childId = Guid.NewGuid();
                var artifactId = Guid.NewGuid();
                var activityId = Guid.NewGuid();
                var legacyPath = kind switch
                {
                    "ooxml" => $"retained-office-structural-text:{member.MemberFingerprint}",
                    "pdf" => $"retained-document-input:{member.MemberFingerprint}",
                    _ => $"retained-media-metadata:{member.MemberFingerprint}"
                };
                var now = DateTimeOffset.UtcNow;
                await using (var setup = CreateContext())
                {
                    setup.SourceRevisions.Add(new SourceRevisionEntity
                    {
                        Id = childId, SourceRootId = seeded.RootId, ParentSourceRevisionId = seeded.RevisionId,
                        StableSourceIdentity = member.StableSourceIdentity, CanonicalPath = legacyPath, Revision = 1,
                        ContentSha256 = member.ContentSha256, ByteLength = member.ByteLength, OriginKind = member.OriginKind,
                        Classification = member.Classification, Extension = member.Extension, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
                    });
                    var parentArtifact = await setup.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == seeded.RevisionId);
                    setup.SourceArtifacts.Add(new SourceArtifactEntity
                    {
                        Id = artifactId, SourceRevisionId = childId, ContentSha256 = member.ContentSha256, ByteLength = member.ByteLength,
                        StoreRelativePath = kind == "pdf" ? parentArtifact.StoreRelativePath : member.StoreRelativePath,
                        ChecksumVerifiedAtUtc = now, ReferenceCount = 1
                    });
                    setup.SourceActivities.Add(new SourceActivityEntity
                    {
                        Id = activityId, SourceRevisionId = childId,
                        ActivityKind = (int)(kind == "pdf" ? SourceActivityKind.DocumentParsing : SourceActivityKind.TextExtraction),
                        ProcessorVersion = kind == "pdf" ? DocumentProcessingInput.PdfProcessorVersion : "phase-3a-v1",
                        DescriptorFingerprint = kind == "pdf" ? DocumentProcessingInput.PdfProcessorFingerprint : SourceActivityEntity.LegacyDescriptorFingerprint,
                        InputFingerprint = member.ContentSha256, State = (int)SourceActivityState.Pending,
                        ExecutionClass = (int)ExecutionClass.InProcess, CreatedAtUtc = now, UpdatedAtUtc = now
                    });
                    await setup.SaveChangesAsync();
                }

                var commits = await Task.WhenAll(store.CommitAsync(claim, completion, CancellationToken.None).AsTask(),
                    store.CommitAsync(claim, completion, CancellationToken.None).AsTask());
                Assert.Single(commits, value => value);
                Assert.False(await store.CommitAsync(claim, completion, CancellationToken.None));
                await using var verification = CreateContext();
                var child = Assert.Single(await verification.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.RevisionId).ToArrayAsync());
                Assert.Equal(childId, child.Id);
                Assert.Equal(legacyPath, child.CanonicalPath);
                Assert.Equal(1, child.Revision);
                Assert.Equal(artifactId, (await verification.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == childId)).Id);
                Assert.Equal(activityId, (await verification.SourceActivities.SingleAsync(value => value.SourceRevisionId == childId)).Id);
                var binding = await verification.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == claim.BranchId);
                Assert.Equal(childId, binding.ChildSourceRevisionId);
                Assert.Equal(activityId, binding.ChildSourceActivityId);
                Assert.Equal(completion.ReceiptFingerprint,
                    (await verification.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId)).CompletionReceiptFingerprint);
            }
            finally { await ReleaseAsync(completion); }
        }
        finally { DeletePrivateRoot(root); }
    }

    [NativeSqlServerFact]
    public async Task Ooxml_no_content_preserves_its_terminal_receipt_without_creating_a_child()
    {
        var root = CreatePrivateRoot();
        try
        {
            var seeded = await SeedAsync(root, "ooxml", Container("ooxml", "empty", false), revision: 2);
            var store = CreateStore();
            var claim = await PromoteAndClaimAsync(store, seeded, "ooxml");
            var completion = await ProcessAsync(root, "ooxml", claim);
            Assert.Empty(completion.Members);
            Assert.Equal("office-document-no-extractable-text", Assert.Single(completion.MemberOutcomes!).ReasonCode);

            Assert.True(await store.CommitAsync(claim, completion, CancellationToken.None));

            await using var context = CreateContext();
            Assert.Empty(await context.SourceRevisions.Where(value => value.ParentSourceRevisionId == seeded.RevisionId).ToArrayAsync());
            var branch = await context.SourceProcessorBranches.SingleAsync(value => value.Id == claim.BranchId);
            Assert.Equal((int)RetainedProcessorBranchState.Completed, branch.State);
            Assert.Equal(completion.ReceiptFingerprint, branch.CompletionReceiptFingerprint);
            var disposition = await context.SourceProcessorBranchMembers.SingleAsync(value => value.BranchId == branch.Id);
            Assert.Equal("skipped", disposition.Disposition);
            Assert.Null(disposition.ChildSourceRevisionId);
            Assert.Null(disposition.ChildSourceActivityId);
        }
        finally { DeletePrivateRoot(root); }
    }

    [NativeSqlServerTheory]
    [InlineData("ooxml", false, false)]
    [InlineData("ooxml", true, false)]
    [InlineData("media", false, false)]
    [InlineData("media", true, false)]
    [InlineData("ooxml", false, true)]
    [InlineData("ooxml", true, true)]
    public async Task Exact_derived_child_identity_collision_rolls_back_blocks_only_its_item_and_allows_healthy_work(
        string kind, bool stableIdentityCollision, bool forceRequest)
    {
        var root = CreatePrivateRoot();
        try
        {
            var failed = await SeedAsync(root, kind, Container(kind, "failed", false));
            var healthy = await SeedAsync(root, kind, Container(kind, "healthy", true), failed.RootId, stableIdentity: "healthy-parent");
            Guid? forceRequestId = null;
            if (forceRequest)
            {
                const string retryReason = "derived-collision-isolated-retry";
                var store = CreateStore();
                var claim = await PromoteAndClaimAsync(store, failed, kind);
                Assert.True(await store.FailAsync(claim, new RetainedProcessorFailure(retryReason, []), CancellationToken.None));
                var capability = OoxmlStructuralTextProcessor.Capability;
                await new SourceCapabilityService(new SqlSourceActivityStore(new ContextFactory(fixture.ConnectionString), TimeProvider.System),
                    new LocalSourceCapabilityHandlerRegistry([new OoxmlStructuralTextCapabilityHandler()])).RegisterAsync(capability, CancellationToken.None);
                await using (var policy = CreateContext())
                {
                    if (!await policy.OperatorActionCapabilityPolicies.AnyAsync(value => value.DescriptorId == capability.Id &&
                        value.ReasonCode == retryReason && value.ActionKind == "retry"))
                    {
                        policy.OperatorActionCapabilityPolicies.Add(new OperatorActionCapabilityPolicyEntity
                        {
                            PolicyId = Guid.NewGuid(), PolicyRevision = 1, DescriptorId = capability.Id,
                            DescriptorFingerprint = capability.ProcessorFingerprint, DescriptorVersion = capability.ProcessorVersion,
                            SafetyContractId = "retained-binding", HandlerId = "retained-processor-branch-store", ActionKind = "retry", ReasonCode = retryReason
                        });
                        await policy.SaveChangesAsync();
                    }
                }
                var action = Assert.Single(await store.ListForceEligibleOoxmlActionsAsync(100, CancellationToken.None),
                    value => value.BranchId == claim.BranchId);
                forceRequestId = (await store.RequestForceAsync(new OoxmlForceRequestCommand(action.ActionId, Guid.NewGuid(),
                    action.RequestFingerprint, action.BlockedRowVersionToken), CancellationToken.None)).RequestId;
            }
            var expected = await ProcessAsync(root, kind, new RetainedProcessorClaim(Guid.NewGuid(), new SourceRevisionId(failed.RevisionId),
                failed.StableIdentity, failed.Hash, "prediction", 1, DateTimeOffset.UtcNow.AddMinutes(5)));
            var member = Assert.Single(expected.Members);
            await ReleaseAsync(expected);
            var sentinelId = Guid.NewGuid();
            await using (var setup = CreateContext())
            {
                setup.SourceRevisions.Add(new SourceRevisionEntity
                {
                    Id = sentinelId, SourceRootId = failed.RootId, ParentSourceRevisionId = healthy.RevisionId,
                    StableSourceIdentity = stableIdentityCollision ? member.StableSourceIdentity : $"sentinel:{sentinelId:N}",
                    CanonicalPath = stableIdentityCollision ? $"retained-other:{sentinelId:N}" : member.SyntheticLocator,
                    Revision = 1, ContentSha256 = member.ContentSha256, ByteLength = member.ByteLength, OriginKind = member.OriginKind,
                    Classification = member.Classification, Extension = member.Extension, DiscoveredAtUtc = DateTimeOffset.UtcNow, DiscoveryEvidenceJson = "{}"
                });
                await setup.SaveChangesAsync();
            }

            var result = await CreateActivation(root, kind).RunOnceAsync(CancellationToken.None);

            Assert.Equal(1, result.FailedBranches);
            Assert.Equal(1, result.CompletedBranches);
            await using var context = CreateContext();
            var branch = await context.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == failed.RevisionId);
            Assert.Equal((int)RetainedProcessorBranchState.Blocked, branch.State);
            Assert.Equal("retained-child-identity-conflict",
                (await context.SourceProcessorAttempts.SingleAsync(value => value.BranchId == branch.Id && value.LeaseGeneration == branch.LeaseGeneration)).OutcomeCode);
            Assert.Empty(await context.SourceProcessorBranchMembers.Where(value => value.BranchId == branch.Id).ToArrayAsync());
            Assert.Empty(await context.SourceRevisions.Where(value => value.ParentSourceRevisionId == failed.RevisionId).ToArrayAsync());
            Assert.Equal((int)RetainedProcessorBranchState.Completed,
                (await context.SourceProcessorBranches.SingleAsync(value => value.SourceRevisionId == healthy.RevisionId)).State);
            var rootSourceIds = context.SourceRevisions.Where(value => value.SourceRootId == failed.RootId).Select(value => value.Id);
            Assert.Equal(3, await context.SourceArtifacts.CountAsync(value => rootSourceIds.Contains(value.SourceRevisionId)));
            Assert.Equal(member.ContentSha256, (await context.SourceRevisions.SingleAsync(value => value.Id == sentinelId)).ContentSha256);
            if (forceRequestId is not null)
            {
                var request = await context.SourceProcessorForceRequests.SingleAsync(value => value.Id == forceRequestId);
                Assert.Equal((byte)OoxmlForceRequestState.Blocked, request.State);
                Assert.Equal("retained-child-identity-conflict", request.TerminalReasonCode);
                Assert.NotNull(request.TerminalAtUtc);
                Assert.Equal(OoxmlForceRequestIdentity.CreateTerminalReceiptFingerprint(request.Id, "retained-child-identity-conflict"),
                    request.TerminalReceiptFingerprint);
                Assert.Equal(branch.Id, request.ForceAttemptBranchId);
                Assert.Equal(branch.LeaseGeneration, request.ForceAttemptLeaseGeneration);
                Assert.Null(branch.LeaseOwner);
                Assert.Null(branch.LeaseExpiresAtUtc);
            }
        }
        finally { DeletePrivateRoot(root); }
    }

    private SqlRetainedProcessorBranchStore CreateStore() => new(new ContextFactory(fixture.ConnectionString), TimeProvider.System);
    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);

    private static SourceCapabilityDescriptor Capability(string kind) => kind switch
    {
        "ooxml" => OoxmlStructuralTextProcessor.Capability,
        "pdf" => PdfDocumentProcessor.Capability,
        _ => MediaMetadataRetainedProcessor.Capability
    };

    private async Task<RetainedProcessorClaim> PromoteAndClaimAsync(SqlRetainedProcessorBranchStore store, Seeded seeded, string kind)
    {
        Assert.True(await store.PromoteAsync(new RetainedProcessorPromotionCandidate(seeded.ActivityId,
            new SourceRevisionId(seeded.RevisionId), seeded.Hash), Capability(kind), CancellationToken.None));
        return Assert.Single(await store.ClaimAsync("derived-revision-test", 1, Capability(kind).ProcessorFingerprint, CancellationToken.None));
    }

    private async Task<RetainedProcessorCompletion> ProcessAsync(string root, string kind, RetainedProcessorClaim claim)
    {
        var factory = new ContextFactory(fixture.ConnectionString);
        var writer = new SqlRetainedArtifactWriter(factory, root);
        var retained = await _resources.Own(new SqlRetainedSourceReader(factory, root)).ReadBytesAsync(claim.SourceRevisionId, CancellationToken.None);
        return kind switch
        {
            "ooxml" => await new OoxmlStructuralTextProcessor(writer).ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None),
            "pdf" => await new PdfDocumentProcessor(writer).ProcessAsync(claim, retained, new RetainedProcessorOptions(), CancellationToken.None),
            _ => await new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure()).ProcessAsync(claim, retained, CancellationToken.None)
        };
    }

    private RetainedProcessorActivationService CreateActivation(string root, string kind)
    {
        var factory = new ContextFactory(fixture.ConnectionString);
        var writer = new SqlRetainedArtifactWriter(factory, root);
        var registry = new LocalSourceCapabilityHandlerRegistry(kind == "ooxml"
            ? [new OoxmlStructuralTextCapabilityHandler()] : [new MediaMetadataCapabilityHandler()]);
        return new RetainedProcessorActivationService(new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), registry),
            CreateStore(), _resources.Own(new SqlRetainedSourceReader(factory, root)), new ZipArchiveRetainedProcessor(writer),
            new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = kind == "ooxml", MediaMetadataEnabled = kind == "media" },
            TimeProvider.System, ooxmlProcessor: kind == "ooxml" ? new OoxmlStructuralTextProcessor(writer) : null,
            mediaMetadataProcessor: kind == "media" ? new MediaMetadataRetainedProcessor(writer, new AllowingDisclosure()) : null);
    }

    private async Task<Seeded> SeedAsync(string root, string kind, byte[] bytes, Guid? sourceRootId = null, int revision = 1, string stableIdentity = "same-parent")
    {
        var rootId = sourceRootId ?? Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var activityId = Guid.NewGuid();
        var hash = Hash(bytes);
        var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relativePath))!);
        await File.WriteAllBytesAsync(Path.Combine(root, relativePath), bytes);
        var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        if (sourceRootId is null)
            context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
            {
                Id = rootId, CanonicalPath = $"I:\\missing-derived-source\\{rootId:N}", DisplayName = "Test", State = 0,
                Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", MaximumFileBytes = 64L * 1024 * 1024,
                AllowedClassificationsJson = "[]", ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            });
        var extension = kind == "ooxml" ? ".docx" : kind == "pdf" ? ".pdf" : ".png";
        context.SourceRevisions.Add(new SourceRevisionEntity
        {
            Id = revisionId, SourceRootId = rootId, StableSourceIdentity = stableIdentity, Revision = revision,
            CanonicalPath = $"I:\\missing-derived-source\\{rootId:N}\\{stableIdentity}{extension}", ContentSha256 = hash,
            ByteLength = bytes.Length, Classification = Capability(kind).AcceptedClassification, Extension = extension, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}"
        });
        context.SourceArtifacts.Add(new SourceArtifactEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath,
            ByteLength = bytes.Length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1
        });
        context.SourceActivities.Add(new SourceActivityEntity
        {
            Id = activityId, SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash,
            RequiredCapability = "local-source-capability", State = (int)SourceActivityState.DeferredUnsupported,
            CreatedAtUtc = now, UpdatedAtUtc = now
        });
        context.DeferredCapabilities.Add(new DeferredCapabilityEntity
        {
            Id = Guid.NewGuid(), SourceRevisionId = revisionId, ArtifactFingerprint = hash,
            RequiredCapability = "local-source-capability", Provenance = "test", CreatedAtUtc = now
        });
        await context.SaveChangesAsync();
        return new Seeded(rootId, revisionId, activityId, stableIdentity, hash);
    }

    private static byte[] Container(string kind, string metadata, bool changedOutput)
    {
        if (kind == "pdf") return Encoding.UTF8.GetBytes($"%PDF-1.7\n%{metadata}\n%%EOF");
        if (kind == "media")
        {
            // Valid RGBA PNGs differ only in a tEXt comment, or also in width/pixel data.
            return Convert.FromBase64String(changedOutput
                ? "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADnRFWHRDb21tZW50AHNlY29uZI4XPKUAAAAOSURBVHicY/jPwPAfhAER9wP948X17wAAAABJRU5ErkJggg=="
                : metadata == "first"
                    ? "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADXRFWHRDb21tZW50AGZpcnN0Qp7m9QAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAASUVORK5CYII="
                    : "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADnRFWHRDb21tZW50AHNlY29uZI4XPKUAAAANSURBVHicY/jPwPAfAAUAAf+JmT0dAAAAAElFTkSuQmCC");
        }
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            static void Entry(ZipArchive zip, string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Entry(zip, "[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            Entry(zip, "_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            var text = metadata == "empty" ? string.Empty : changedOutput ? "Changed text" : "Same text";
            Entry(zip, "word/document.xml", $"<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><!--{metadata}--><w:t>{text}</w:t></w:document>");
        }
        return buffer.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string CreatePrivateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"flux-derived-revision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
    private void DeletePrivateRoot(string root)
    {
        var resolved = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("flux-derived-revision-", Path.GetFileName(resolved), StringComparison.Ordinal);
        Assert.Equal(0, (int)(File.GetAttributes(resolved) & FileAttributes.ReparsePoint));
        _resources.Dispose();
        Directory.Delete(resolved, recursive: true);
    }
    private static async ValueTask ReleaseAsync(RetainedProcessorCompletion completion)
    {
        foreach (var lease in completion.Members.Select(value => value.PublicationLease).OfType<ISourceArtifactPublicationLease>().Distinct())
            await lease.DisposeAsync();
    }
    private sealed record Seeded(Guid RootId, Guid RevisionId, Guid ActivityId, string StableIdentity, string Hash);
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options);
    }
    private sealed class AllowingDisclosure : ILocalPrivateContentDisclosure
    {
        public LocalDisclosureResult Evaluate(string value, LocalDisclosureKind kind) => new(value, false, null);
    }
}
