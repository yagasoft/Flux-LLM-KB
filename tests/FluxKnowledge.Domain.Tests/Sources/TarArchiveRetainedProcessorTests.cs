using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class TarArchiveRetainedProcessorTests
{
    [Theory]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Gnu)]
    public async Task Ustar_and_gnu_retained_tar_members_are_streamed_with_a_private_fingerprint(TarEntryFormat format)
    {
        var archive = CreateTar(format, TarEntryType.RegularFile, "docs/readme.txt", "retained text");
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingStreamWriter();

        var completion = await new TarArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        var member = Assert.Single(completion.Members);
        Assert.Equal(Encoding.UTF8.GetByteCount("retained text"), member.ByteLength);
        Assert.DoesNotContain("readme", member.Identity.SyntheticLocator, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(member.ByteLength, writer.BytesWritten);
    }

    [Fact]
    public void Archive_tar_handler_is_explicitly_disabled_by_default()
    {
        Assert.False(new RetainedProcessorOptions().ArchiveTarExpandEnabled);
        Assert.Equal("archive-tar-expand", TarArchiveRetainedProcessor.Capability.ProcessorKind);
        Assert.Equal("retained:archive-tar-expand", TarArchiveRetainedProcessor.Capability.OutputContract);
    }

    [Fact]
    public void Signature_confirmed_tar_is_deferred_for_the_explicit_retained_tar_capability()
    {
        var archive = CreateTar(TarEntryFormat.Ustar, TarEntryType.RegularFile, "member.txt", "text");

        var result = SourceClassifier.Classify("retained.tar", archive, archive.Length);

        Assert.Equal(SourceClassification.DeferredCapability, result.Classification);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("folder/file:stream.txt")]
    public async Task Unsafe_tar_paths_are_rejected_before_a_member_is_written(string path)
    {
        var archive = CreateTar(TarEntryFormat.Ustar, TarEntryType.RegularFile, path, "x");
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingStreamWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await new TarArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None));

        Assert.Equal("archive-entry-path-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.CharacterDevice)]
    [InlineData(TarEntryType.BlockDevice)]
    [InlineData(TarEntryType.Fifo)]
    public async Task Link_device_and_fifo_tar_entries_are_rejected(TarEntryType entryType)
    {
        var archive = CreateTar(TarEntryFormat.Ustar, entryType, "unsafe", null);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(async () => await new TarArchiveRetainedProcessor(new RecordingStreamWriter()).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None));

        Assert.Equal("archive-entry-link-invalid", error.OutcomeCode);
    }

    [Fact]
    public async Task Tar_entry_count_and_member_size_bounds_are_rejected_before_writes()
    {
        var entries = Enumerable.Range(0, 257).Select(index => ($"{index}.txt", "x"));
        var tooMany = CreateTar(TarEntryFormat.Ustar, entries);
        var big = CreateTar(TarEntryFormat.Ustar, TarEntryType.RegularFile, "large.txt", new string('x', 16 * 1024 * 1024 + 1));
        var writer = new RecordingStreamWriter();

        var tooManyError = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(tooMany, writer));
        var bigError = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(big, writer));

        Assert.Equal("archive-entry-count-limit", tooManyError.OutcomeCode);
        Assert.Equal("archive-member-size-limit", bigError.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Theory]
    [InlineData("unsafe-directory", "../private-directory/")]
    [InlineData("unsafe-directory", "\\private-directory\\")]
    [InlineData("unsafe-directory", "directory:stream/")]
    public async Task Unsafe_tar_directory_path_is_rejected_and_has_one_sanitised_member_outcome(string _, string path)
    {
        var archive = CreateTar(TarEntryFormat.Ustar, TarEntryType.Directory, path, null);
        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(archive, new RecordingStreamWriter()));

        Assert.Equal("archive-entry-path-invalid", error.OutcomeCode);
        var outcome = Assert.Single(error.MemberOutcomes);
        Assert.Equal("archive-entry-path-invalid", outcome.ReasonCode);
        Assert.DoesNotContain("private", outcome.MemberFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AdditionalRejectedTarCases))]
    public async Task Tar_hostile_inputs_use_fixed_sanitised_outcomes_without_member_writes(
        string expectedCode, byte[] archive, RetainedProcessorOptions options)
    {
        var writer = new RecordingStreamWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(archive, writer, options));

        Assert.Equal(expectedCode, error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
        Assert.All(error.MemberOutcomes, outcome =>
        {
            Assert.Equal("blocked", outcome.Disposition);
            Assert.DoesNotContain("sentinel", outcome.MemberFingerprint, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static TheoryData<string, byte[], RetainedProcessorOptions> AdditionalRejectedTarCases()
    {
        var options = new RetainedProcessorOptions();
        return new TheoryData<string, byte[], RetainedProcessorOptions>
        {
            { "archive-member-identity-conflict", CreateTar(TarEntryFormat.Ustar, [("duplicate.txt", "one"), ("duplicate.txt", "two")]), options },
            { "nested-archive-depth-limit", CreateTarBytes(TarEntryFormat.Ustar, TarEntryType.RegularFile, "nested.tar", CreateTar(TarEntryFormat.Ustar, TarEntryType.RegularFile, "inner.txt", "x")), options },
            { "archive-entry-link-invalid", CreateTar(TarEntryFormat.Ustar, TarEntryType.HardLink, "hard-link-sentinel", null), options },
            { "archive-entry-unsupported", PatchEntryType(CreateTar(TarEntryFormat.Gnu, TarEntryType.RegularFile, "sparse-sentinel", "x"), (byte)'S'), options }
        };
    }

    [Fact]
    public async Task Uncompressed_tar_member_remains_within_the_inherited_100_to_1_ratio_bound()
    {
        var archive = CreateTar(TarEntryFormat.Ustar, TarEntryType.RegularFile, "ratio-sentinel.txt", new string('x', 50_000));

        var completion = await ProcessAsync(archive, new RecordingStreamWriter(), new RetainedProcessorOptions { MaximumCompressionRatio = 100 });

        Assert.Single(completion.Members);
    }

    [Fact]
    public async Task Embedded_nul_in_a_raw_tar_name_is_rejected_with_an_opaque_sanitised_outcome()
    {
        var archive = PatchRawName(CreateTar(TarEntryFormat.Ustar, TarEntryType.RegularFile, "safe.txt", "x"), "safe\0private-nul-sentinel.txt");
        var writer = new RecordingStreamWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(archive, writer));

        Assert.Equal("archive-entry-path-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
        var outcome = Assert.Single(error.MemberOutcomes);
        Assert.Equal("archive-entry-path-invalid", outcome.ReasonCode);
        Assert.DoesNotContain("private-nul-sentinel", outcome.MemberFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Archive_larger_than_64_mib_is_rejected_without_opening_a_tar_member()
    {
        var bytes = new byte[64 * 1024 * 1024 + 1];
        var writer = new RecordingStreamWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(bytes, writer));

        Assert.Equal("archive-input-too-large", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Logical_tar_path_of_513_characters_is_rejected_before_writes()
    {
        var archive = CreateTar(TarEntryFormat.Gnu, TarEntryType.RegularFile, new string('p', 509) + ".txt", "x");
        var writer = new RecordingStreamWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(archive, writer));

        Assert.Equal("archive-entry-path-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Tar_expanding_beyond_128_mib_is_rejected_before_any_member_is_written()
    {
        var archive = CreateTarWithRepeatedMembers(("first.txt", 64L * 1024 * 1024), ("second.txt", 64L * 1024 * 1024 + 1));
        var writer = new RecordingStreamWriter();
        var options = new RetainedProcessorOptions
        {
            MaximumCompressedInputBytes = 129L * 1024 * 1024,
            MaximumMemberBytes = 128L * 1024 * 1024
        };

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => ProcessAsync(archive, writer, options));

        Assert.Equal("archive-expanded-total-limit", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    private static Task<RetainedProcessorCompletion> ProcessAsync(byte[] archive, IRetainedArtifactWriter writer) =>
        ProcessAsync(archive, writer, new RetainedProcessorOptions());

    private static async Task<RetainedProcessorCompletion> ProcessAsync(byte[] archive, IRetainedArtifactWriter writer, RetainedProcessorOptions options)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        return await new TarArchiveRetainedProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), options, CancellationToken.None);
    }

    private static byte[] CreateTar(TarEntryFormat format, TarEntryType entryType, string name, string? content)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, format, leaveOpen: true);
        var entry = CreateEntry(format, entryType, name);
        if (content is not null) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        if (entryType is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "outside";
        writer.WriteEntry(entry);
        return buffer.ToArray();
    }

    private static byte[] CreateTarBytes(TarEntryFormat format, TarEntryType entryType, string name, byte[] content)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, format, leaveOpen: true);
        var entry = CreateEntry(format, entryType, name);
        entry.DataStream = new MemoryStream(content);
        writer.WriteEntry(entry);
        return buffer.ToArray();
    }

    private static byte[] CreateTar(TarEntryFormat format, IEnumerable<(string Name, string Content)> entries)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, format, leaveOpen: true);
        foreach (var (name, content) in entries)
        {
            var entry = CreateEntry(format, TarEntryType.RegularFile, name);
            entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            writer.WriteEntry(entry);
        }
        return buffer.ToArray();
    }

    private static byte[] CreateTarWithRepeatedMembers(params (string Name, long Length)[] entries)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, TarEntryFormat.Ustar, leaveOpen: true);
        foreach (var (name, length) in entries)
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new RepeatingReadStream(length) };
            writer.WriteEntry(entry);
        }
        return buffer.ToArray();
    }

    private static TarEntry CreateEntry(TarEntryFormat format, TarEntryType entryType, string name) => format switch
    {
        TarEntryFormat.Ustar => new UstarTarEntry(entryType, name),
        TarEntryFormat.Gnu => new GnuTarEntry(entryType, name),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static byte[] PatchEntryType(byte[] archive, byte entryType)
    {
        archive[156] = entryType;
        Array.Fill(archive, (byte)' ', 148, 8);
        var checksum = archive.AsSpan(0, 512).ToArray().Sum(value => value);
        var octal = Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ");
        octal.CopyTo(archive, 148);
        return archive;
    }

    private static byte[] PatchRawName(byte[] archive, string rawName)
    {
        var encoded = Encoding.ASCII.GetBytes(rawName);
        Array.Clear(archive, 0, 100);
        encoded.CopyTo(archive, 0);
        return RecalculateFirstHeaderChecksum(archive);
    }

    private static byte[] RecalculateFirstHeaderChecksum(byte[] archive)
    {
        Array.Fill(archive, (byte)' ', 148, 8);
        var checksum = archive.AsSpan(0, 512).ToArray().Sum(value => value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(archive, 148);
        return archive;
    }

    private sealed class RecordingStreamWriter : IRetainedArtifactWriter
    {
        public int BytesWritten { get; private set; }

        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId parentSourceRevisionId, Stream content, long maximumByteLength, CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                BytesWritten += read;
                hash.AppendData(buffer, 0, read);
            }
            return new RetainedArtifactWriteReceipt(Convert.ToHexStringLower(hash.GetHashAndReset()), "sha256\\test\\streamed.bin", BytesWritten, true, false);
        }
    }

    private sealed class RepeatingReadStream : Stream
    {
        private readonly long _length;
        private long _remaining;
        public RepeatingReadStream(long length)
        {
            _length = length;
            _remaining = length;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, _remaining);
            buffer.AsSpan(offset, read).Fill((byte)'x');
            _remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
