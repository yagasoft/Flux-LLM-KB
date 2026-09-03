using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Sources;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class ZipArchiveRetainedProcessorTests
{
    [Fact]
    public void Archive_member_identity_uses_a_fingerprint_not_the_raw_entry_name()
    {
        var identity = ArchiveMemberIdentity.Create("parent-stable", "docs/readme.txt");

        Assert.DoesNotContain("readme", identity.SyntheticLocator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docs", identity.SyntheticLocator, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("retained-archive-member:", identity.SyntheticLocator, StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_zip_handler_exposes_the_approved_runnable_descriptor()
    {
        var descriptor = ZipArchiveRetainedProcessor.Capability;

        Assert.Equal(new Guid("b4a06e5d-6f01-4f73-9722-79b6df4e85c3"), descriptor.Id);
        Assert.Equal("archive-zip-expand", descriptor.ProcessorKind);
        Assert.Equal("phase-5-zip-v1", descriptor.ProcessorVersion);
        Assert.Equal("phase-5-zip-retained-archive-v1", descriptor.ProcessorFingerprint);
        Assert.Equal("ArchiveZip", descriptor.AcceptedClassification);
        Assert.Equal("retained:archive-zip-expand", descriptor.OutputContract);
    }

    [Fact]
    public void Archive_zip_replay_requires_an_explicit_opt_in()
    {
        Assert.False(new RetainedProcessorOptions().ArchiveZipExpandEnabled);
    }

    [Fact]
    public async Task Disabled_activation_reconciles_durable_force_requests_before_any_processor_registration_or_source_read()
    {
        var capabilities = new RecordingCapabilities();
        var branches = new RecordingReconciliationBranches();
        var processor = new ZipArchiveRetainedProcessor(null!);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(capabilities, new LocalSourceCapabilityHandlerRegistry([processor])), branches,
            new ThrowingReader(), processor, new RetainedProcessorOptions(), TimeProvider.System);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.True(branches.Reconciled);
        Assert.False(branches.OoxmlDescriptorEnabled);
        Assert.Empty(capabilities.Registered);
        Assert.False(branches.OtherOperationCalled);
    }

    [Fact]
    public async Task Disabled_activation_does_not_register_promote_claim_or_replay()
    {
        var capabilities = new RecordingCapabilities();
        var branches = new ThrowingBranches();
        var processor = new ZipArchiveRetainedProcessor(null!);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(capabilities, new LocalSourceCapabilityHandlerRegistry([processor])), branches,
            new ThrowingReader(), processor, new RetainedProcessorOptions { ArchiveZipExpandEnabled = false }, TimeProvider.System);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Empty(capabilities.Registered);
        Assert.False(branches.Called);
    }

    [Fact]
    public async Task Hosted_zip_activation_preserves_the_configured_shared_sixteen_claim_limit()
    {
        var branches = new RecordingClaimBudgetBranches(forceClaimCount: 0);
        var processor = new ZipArchiveRetainedProcessor(null!);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(
                new RecordingCapabilities(),
                new LocalSourceCapabilityHandlerRegistry([processor])),
            branches,
            new MissingRetainedReader(),
            processor,
            new RetainedProcessorOptions
            {
                ArchiveZipExpandEnabled = true,
                CsharpCodeEnabled = false,
                AutomaticReplayBatchSize = 16
            },
            TimeProvider.System);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Null(branches.ForceMaximumCount);
        Assert.Equal(16, branches.OrdinaryMaximumCount);
    }

    [Fact]
    public async Task Hosted_tar_activation_preserves_the_configured_shared_sixteen_claim_limit()
    {
        var branches = new RecordingClaimBudgetBranches(forceClaimCount: 0);
        var processor = new TarArchiveRetainedProcessor(null!);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(
                new RecordingCapabilities(),
                new LocalSourceCapabilityHandlerRegistry([processor])),
            branches,
            new MissingRetainedReader(),
            new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions
            {
                ArchiveTarExpandEnabled = true,
                CsharpCodeEnabled = false,
                AutomaticReplayBatchSize = 16
            },
            TimeProvider.System,
            tarProcessor: processor);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Null(branches.ForceMaximumCount);
        Assert.Equal(16, branches.OrdinaryMaximumCount);
    }

    [Fact]
    public async Task Hosted_ooxml_force_and_ordinary_claims_share_the_configured_sixteen_claim_budget()
    {
        const int forceClaimCount = 6;
        var branches = new RecordingClaimBudgetBranches(forceClaimCount);
        var processor = new OoxmlStructuralTextProcessor(null!);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(
                new RecordingCapabilities(),
                new LocalSourceCapabilityHandlerRegistry([new OoxmlStructuralTextCapabilityHandler()])),
            branches,
            new MissingRetainedReader(),
            new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions
            {
                OoxmlDocumentStructuralExtractEnabled = true,
                CsharpCodeEnabled = false,
                AutomaticReplayBatchSize = 16
            },
            TimeProvider.System,
            ooxmlProcessor: processor);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(16, branches.ForceMaximumCount);
        Assert.Equal(10, branches.OrdinaryMaximumCount);
        Assert.Equal(16, forceClaimCount + branches.OrdinaryMaximumCount);
    }

    [Fact]
    public async Task Activation_records_processor_cancelled_retry_through_the_retained_reader_seam()
    {
        var branches = new ClaimingBranches();
        using var cancellation = new CancellationTokenSource();
        var activation = CreateEnabledActivation(branches, new FailingReader(() => new OperationCanceledException(cancellation.Token)));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await activation.RunOnceAsync(cancellation.Token));

        Assert.Equal("processor-cancelled", branches.RetryOutcomeCode);
    }

    [Fact]
    public async Task Activation_records_transient_retained_reader_failure_for_replay_without_blocking()
    {
        var branches = new ClaimingBranches();
        var activation = CreateEnabledActivation(branches, new FailingReader(() => new IOException("transient private store failure")));

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.ClaimedBranches);
        Assert.Equal(1, result.FailedBranches);
        Assert.Equal("retained-artifact-transient", branches.RetryOutcomeCode);
        Assert.Null(branches.Failure);
    }

    [Fact]
    public async Task Archive_member_is_written_through_a_bounded_stream_without_a_full_member_buffer()
    {
        var writer = new RecordingStreamWriter();
        var processor = new ZipArchiveRetainedProcessor(writer);
        var zip = CreateZip("documents/large.txt", new string('x', 1024 * 1024));
        var hash = Convert.ToHexStringLower(SHA256.HashData(zip));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var completion = await processor.ProcessAsync(
            claim,
            new RetainedSourceBytes(claim.SourceRevisionId, zip, hash, zip.Length),
            new RetainedProcessorOptions { MaximumCompressionRatio = 10_000 },
            CancellationToken.None);

        Assert.Single(completion.Members);
        Assert.Equal(1024 * 1024, writer.BytesWritten);
        Assert.InRange(writer.MaximumReadSize, 1, 128 * 1024);
    }

    [Fact]
    public async Task Office_style_zip_member_at_the_measured_169_to_1_limit_is_processed()
    {
        var content = "<PageContents><Text>" + new string('x', 12_065) + "</Text></PageContents>";
        var archive = CreateZip("visio/pages/page1.xml", content);
        Assert.Equal(169, CompressionRatio(archive, "visio/pages/page1.xml"));
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var completion = await new ZipArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.Single(completion.Members);
        Assert.True(writer.BytesWritten > 0);
    }

    [Fact]
    public async Task Office_style_zip_member_just_above_the_169_to_1_limit_is_rejected_before_any_member_is_written()
    {
        var content = "<PageContents><Text>" + new string('x', 12_288) + "</Text></PageContents>";
        var archive = CreateZip("visio/pages/page1.xml", content);
        Assert.Equal(172, CompressionRatio(archive, "visio/pages/page1.xml"));
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new ZipArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("archive-compression-ratio-limit", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Theory]
    [MemberData(nameof(RejectedZipCases))]
    public async Task Unsafe_zip_is_rejected_before_any_member_is_written(string expectedCode, byte[] archive, RetainedProcessorOptions options)
    {
        var writer = new RecordingStreamWriter();
        var processor = new ZipArchiveRetainedProcessor(writer);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(async () =>
            await processor.ProcessAsync(claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), options, CancellationToken.None));

        Assert.Equal(expectedCode, failure.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    public static TheoryData<string, byte[], RetainedProcessorOptions> RejectedZipCases()
    {
        var defaultOptions = new RetainedProcessorOptions();
        var cases = new TheoryData<string, byte[], RetainedProcessorOptions>();
        cases.Add("archive-entry-path-invalid", CreateZip("../traversal.txt", "x"), defaultOptions);
        cases.Add("archive-entry-path-invalid", CreateZip("/rooted.txt", "x"), defaultOptions);
        cases.Add("archive-entry-path-invalid", CreateZip("folder/file:stream.txt", "x"), defaultOptions);
        cases.Add("archive-member-identity-conflict", CreateZip([("same.txt", "one"), ("same.txt", "two")]), defaultOptions);
        cases.Add("archive-entry-count-limit", CreateZip(Enumerable.Range(0, 257).Select(index => ($"{index}.txt", "x"))), defaultOptions);
        cases.Add("archive-entry-path-invalid", CreateZip(new string('p', 509) + ".txt", "x"), defaultOptions);
        cases.Add("archive-compression-ratio-limit", CreateZip("ratio.txt", new string('x', 16_001)), defaultOptions);
        cases.Add("nested-archive-depth-limit", CreateZip("nested.zip", CreateZip("inner.txt", "x")), defaultOptions);
        cases.Add("archive-entry-link-invalid", PatchExternalAttributes(CreateZip("link.txt", "x"), 0xA000), defaultOptions);
        cases.Add("archive-entry-link-invalid", PatchWindowsReparsePoint(CreateZip("reparse.txt", "x")), defaultOptions);
        cases.Add("archive-entry-encrypted", PatchGeneralPurposeFlags(CreateZip("encrypted.txt", "x"), 0x0001), defaultOptions);
        cases.Add("archive-entry-compression-unsupported", PatchCompressionMethod(CreateZip("unsupported.txt", "x"), 99), defaultOptions);
        cases.Add("archive-entry-unsupported", PatchMultiVolumeEndRecord(CreateZip("volume.txt", "x")), defaultOptions);

        return cases;
    }

    [Fact]
    public async Task Archive_larger_than_64_mib_is_rejected_without_opening_a_member()
    {
        var bytes = new byte[64 * 1024 * 1024 + 1];
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await new ZipArchiveRetainedProcessor(writer)
            .ProcessAsync(claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None));

        Assert.Equal("archive-input-too-large", failure.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Archive_expanding_beyond_128_mib_is_rejected_before_any_member_is_written()
    {
        var archive = CreateZip([("first.txt", new string('x', 64 * 1024 * 1024)), ("second.txt", new string('y', 64 * 1024 * 1024 + 1))]);
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var options = new RetainedProcessorOptions { MaximumCompressionRatio = 100_000, MaximumMemberBytes = 128L * 1024 * 1024 };

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await new ZipArchiveRetainedProcessor(writer)
            .ProcessAsync(claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), options, CancellationToken.None));

        Assert.Equal("archive-expanded-total-limit", failure.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Archive_member_larger_than_16_mib_is_rejected_before_any_member_is_written()
    {
        var archive = CreateZip("large.txt", new string('x', 16 * 1024 * 1024 + 1));
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var options = new RetainedProcessorOptions { MaximumCompressionRatio = 100_000 };

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await new ZipArchiveRetainedProcessor(writer)
            .ProcessAsync(claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), options, CancellationToken.None));

        Assert.Equal("archive-member-size-limit", failure.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Nested_member_after_a_safe_member_is_preflighted_before_any_artifact_write()
    {
        var archive = CreateZip(new (string Name, byte[] Content)[] { ("safe.txt", Encoding.UTF8.GetBytes("safe")), ("nested.zip", CreateZip("inner.txt", "x")) });
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var failure = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await new ZipArchiveRetainedProcessor(writer)
            .ProcessAsync(claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None));

        Assert.Equal("nested-archive-depth-limit", failure.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Theory]
    [InlineData((ushort)0x0000)]
    [InlineData((ushort)0x0002)]
    [InlineData((ushort)0x0004)]
    [InlineData((ushort)0x0006)]
    public async Task Valid_deflate_option_flags_are_accepted_for_deflated_members(ushort flags)
    {
        var archive = PatchGeneralPurposeFlags(CreateZip("deflated.txt", "safe"), flags);
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var completion = await new ZipArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.Single(completion.Members);
        Assert.Equal(4, writer.BytesWritten);
    }

    [Theory]
    [InlineData((ushort)0x0002)]
    [InlineData((ushort)0x0004)]
    [InlineData((ushort)0x0006)]
    public async Task Deflate_option_flags_on_stored_members_are_rejected_before_any_member_is_written(ushort flags)
    {
        var archive = PatchCompressionMethod(PatchGeneralPurposeFlags(CreateZip("stored.txt", "safe"), flags), 0);
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new ZipArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("archive-entry-unsupported", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Deflate_option_flags_with_an_unsupported_compression_method_are_rejected_before_any_member_is_written()
    {
        var archive = PatchCompressionMethod(PatchGeneralPurposeFlags(CreateZip("unsupported.txt", "safe"), 0x0006), 99);
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new ZipArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("archive-entry-compression-unsupported", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Archive_with_a_nonrepresentable_local_header_offset_is_rejected_before_any_member_is_written()
    {
        var archive = PatchLocalHeaderOffset(CreateZip("safe.txt", "safe"), uint.MaxValue);
        var writer = new RecordingStreamWriter();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new ZipArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("archive-entry-unsupported", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Archive_with_a_nonrepresentable_local_header_offset_is_recorded_as_a_terminal_failure()
    {
        var archive = PatchLocalHeaderOffset(CreateZip("safe.txt", "safe"), uint.MaxValue);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var branches = new ClaimingBranches(claim);
        var processor = new ZipArchiveRetainedProcessor(new RecordingStreamWriter());
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new RecordingCapabilities(), new LocalSourceCapabilityHandlerRegistry([processor])),
            branches,
            new RetainedBytesReader(new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length)),
            processor,
            new RetainedProcessorOptions { ArchiveZipExpandEnabled = true },
            TimeProvider.System);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedBranches);
        Assert.Equal("archive-entry-unsupported", branches.Failure?.OutcomeCode);
        Assert.Null(branches.RetryOutcomeCode);
    }

    private sealed class RecordingCapabilities : ISourceCapabilityStore
    {
        public List<RegisteredSourceCapability> Registered { get; } = [];
        public ValueTask<RegisteredSourceCapability> RegisterAsync(RegisteredSourceCapability capability, CancellationToken cancellationToken)
        { Registered.Add(capability); return ValueTask.FromResult(capability); }
        public ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken) => ValueTask.FromResult<RegisteredSourceCapability?>(null);
    }

    private RetainedProcessorActivationService CreateEnabledActivation(ClaimingBranches branches, IRetainedSourceReader reader) => new(
        new SourceCapabilityService(new RecordingCapabilities(), new LocalSourceCapabilityHandlerRegistry([new ZipArchiveRetainedProcessor(null!)])),
        branches, reader, new ZipArchiveRetainedProcessor(null!), new RetainedProcessorOptions { ArchiveZipExpandEnabled = true }, TimeProvider.System);

    private sealed class ThrowingBranches : IRetainedProcessorBranchStore
    {
        public bool Called { get; private set; }
        private void Hit() { Called = true; throw new Xunit.Sdk.XunitException("Disabled activation must not access branches."); }
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate candidate, SourceCapabilityDescriptor capability, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate candidate, string outcomeCode, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) { Hit(); return default; }
    }

    private sealed class ThrowingReader : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Disabled activation must not read retained bytes.");
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Disabled activation must not read retained bytes.");
    }

    private sealed class MissingRetainedReader : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            ValueTask.FromException<RetainedSourceBytes>(new FileNotFoundException("Synthetic retained artifact is absent."));

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            ValueTask.FromException<Utf8FileSource>(new FileNotFoundException("Synthetic retained artifact is absent."));
    }

    private sealed class FailingReader(Func<Exception> exception) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) => ValueTask.FromException<RetainedSourceBytes>(exception());
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) => ValueTask.FromException<Utf8FileSource>(exception());
    }

    private sealed class RecordingReconciliationBranches : IRetainedProcessorBranchStore
    {
        public bool Reconciled { get; private set; }
        public bool OoxmlDescriptorEnabled { get; private set; } = true;
        public bool OtherOperationCalled { get; private set; }
        public ValueTask<int> ReconcileForceRequestsAsync(bool ooxmlDescriptorEnabled, CancellationToken cancellationToken)
        {
            Reconciled = true;
            OoxmlDescriptorEnabled = ooxmlDescriptorEnabled;
            return ValueTask.FromResult(0);
        }
        private void Hit() { OtherOperationCalled = true; throw new Xunit.Sdk.XunitException("Disabled activation must run only force reconciliation."); }
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate candidate, SourceCapabilityDescriptor capability, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate candidate, string outcomeCode, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) { Hit(); return default; }
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) { Hit(); return default; }
    }

    private sealed class ClaimingBranches : IRetainedProcessorBranchStore
    {
        private readonly RetainedProcessorClaim _claim;

        public ClaimingBranches(RetainedProcessorClaim? claim = null) =>
            _claim = claim ?? new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", new string('a', 64), "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        public string? RetryOutcomeCode { get; private set; }
        public RetainedProcessorFailure? Failure { get; private set; }
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate candidate, SourceCapabilityDescriptor capability, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate candidate, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([_claim]);
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) { RetryOutcomeCode = outcomeCode; return ValueTask.FromResult(true); }
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) { Failure = failure; return ValueTask.FromResult(true); }
    }

    private sealed class RecordingClaimBudgetBranches : IRetainedProcessorBranchStore
    {
        private readonly RetainedProcessorClaim[] _forceClaims;

        public RecordingClaimBudgetBranches(int forceClaimCount) =>
            _forceClaims = Enumerable.Range(0, forceClaimCount)
                .Select(_ => new RetainedProcessorClaim(
                    Guid.NewGuid(),
                    SourceRevisionId.New(),
                    "parent",
                    new string('a', 64),
                    "owner",
                    1,
                    DateTimeOffset.UtcNow.AddMinutes(5)))
                .ToArray();

        public int? ForceMaximumCount { get; private set; }
        public int OrdinaryMaximumCount { get; private set; }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);

        public ValueTask<bool> PromoteAsync(
            RetainedProcessorPromotionCandidate candidate,
            SourceCapabilityDescriptor capability,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No promotion candidate was returned.");

        public ValueTask<bool> BlockPromotionAsync(
            RetainedProcessorPromotionCandidate candidate,
            string outcomeCode,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("No promotion candidate was returned.");

        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimForceAsync(
            string leaseOwner,
            int maximumCount,
            string processorFingerprint,
            CancellationToken cancellationToken)
        {
            ForceMaximumCount = maximumCount;
            return ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>(_forceClaims);
        }

        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(
            string leaseOwner,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            OrdinaryMaximumCount = maximumCount;
            return ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]);
        }

        public ValueTask<bool> CommitAsync(
            RetainedProcessorClaim claim,
            RetainedProcessorCompletion completion,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("The synthetic retained artifact is absent.");

        public ValueTask<bool> RetryAsync(
            RetainedProcessorClaim claim,
            string outcomeCode,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("A missing retained artifact is terminal.");

        public ValueTask<bool> FailAsync(
            RetainedProcessorClaim claim,
            RetainedProcessorFailure failure,
            CancellationToken cancellationToken)
        {
            Assert.Equal("retained-artifact-missing", failure.OutcomeCode);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingStreamWriter : IRetainedArtifactWriter
    {
        public int BytesWritten { get; private set; }
        public int MaximumReadSize { get; private set; }

        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(
            SourceRevisionId parentSourceRevisionId,
            Stream content,
            long maximumByteLength,
            CancellationToken cancellationToken)
        {
            Assert.IsNotType<MemoryStream>(content);
            var buffer = new byte[128 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var prefix = new List<byte>(4);
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                BytesWritten += read;
                MaximumReadSize = Math.Max(MaximumReadSize, read);
                Assert.True(BytesWritten <= maximumByteLength);
                hash.AppendData(buffer, 0, read);
                foreach (var value in buffer.AsSpan(0, read))
                {
                    if (prefix.Count < 4) prefix.Add(value);
                }
            }

            return new RetainedArtifactWriteReceipt(
                Convert.ToHexStringLower(hash.GetHashAndReset()),
                "sha256\\test\\streamed.bin",
                BytesWritten,
                IsUtf8Text: true,
                IsNestedArchive: ZipArchiveRetainedProcessor.IsZipSignature(prefix.ToArray()));
        }
    }

    private static byte[] CreateZip(string entryName, string content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open())) writer.Write(content);
        return buffer.ToArray();
    }

    private static int CompressionRatio(byte[] archive, string entryName)
    {
        using var package = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
        var entry = Assert.Single(package.Entries, value => value.FullName == entryName);
        return (int)Math.Ceiling((double)entry.Length / entry.CompressedLength);
    }

    private static byte[] CreateZip(string entryName, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry(entryName).Open()) entry.Write(content);
        return buffer.ToArray();
    }

    private static byte[] CreateZip(IEnumerable<(string Name, string Content)> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(content);
        }
        return buffer.ToArray();
    }

    private static byte[] CreateZip(IEnumerable<(string Name, byte[] Content)> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            using (var stream = archive.CreateEntry(name).Open()) stream.Write(content);
        }
        return buffer.ToArray();
    }

    private static byte[] PatchGeneralPurposeFlags(byte[] archive, ushort flags) => PatchEveryHeader(archive, (bytes, offset, central) =>
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + (central ? 8 : 6), 2), flags));

    private static byte[] PatchCompressionMethod(byte[] archive, ushort method) => PatchEveryHeader(archive, (bytes, offset, central) =>
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + (central ? 10 : 8), 2), method));

    private static byte[] PatchExternalAttributes(byte[] archive, uint attributes) => PatchEveryHeader(archive, (bytes, offset, central) =>
    {
        if (central) BitConverter.TryWriteBytes(bytes.AsSpan(offset + 38, 4), attributes << 16);
    });

    private static byte[] PatchWindowsReparsePoint(byte[] archive) => PatchEveryHeader(archive, (bytes, offset, central) =>
    {
        if (central) BitConverter.TryWriteBytes(bytes.AsSpan(offset + 38, 4), 0x00000400u);
    });

    private static byte[] PatchMultiVolumeEndRecord(byte[] archive)
    {
        var copy = archive.ToArray();
        for (var offset = copy.Length - 22; offset >= 0; offset--)
        {
            if (BitConverter.ToUInt32(copy, offset) == 0x06054b50)
            {
                BitConverter.TryWriteBytes(copy.AsSpan(offset + 4, 2), (ushort)1);
                break;
            }
        }
        return copy;
    }

    private static byte[] PatchLocalHeaderOffset(byte[] archive, uint localHeaderOffset) => PatchEveryHeader(archive, (bytes, offset, central) =>
    {
        if (central) BitConverter.TryWriteBytes(bytes.AsSpan(offset + 42, 4), localHeaderOffset);
    });

    private static byte[] PatchEveryHeader(byte[] archive, Action<byte[], int, bool> patch)
    {
        var copy = archive.ToArray();
        for (var offset = 0; offset <= copy.Length - 4; offset++)
        {
            var signature = BitConverter.ToUInt32(copy, offset);
            if (signature == 0x04034b50) patch(copy, offset, false);
            if (signature == 0x02014b50) patch(copy, offset, true);
        }
        return copy;
    }

    private sealed class RetainedBytesReader(RetainedSourceBytes retained) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(retained.SourceRevisionId, sourceRevisionId);
            return ValueTask.FromResult(retained);
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Archive processing must not decode retained ZIP bytes as text.");
    }
}
