using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class OoxmlStructuralTextProcessorTests
{
    [Fact]
    public void Ooxml_handler_exposes_the_approved_runnable_descriptor()
    {
        var descriptor = OoxmlStructuralTextProcessor.Capability;

        Assert.Equal(new Guid("3d72bf21-5358-482d-a6a9-576ff23012a3"), descriptor.Id);
        Assert.Equal("document-ooxml-structural-extract", descriptor.ProcessorKind);
        Assert.Equal("phase-5-ooxml-structural-v1", descriptor.ProcessorVersion);
        Assert.Equal("phase-5-ooxml-retained-structural-v1", descriptor.ProcessorFingerprint);
        Assert.Equal(SourceActivityKind.TextExtraction, descriptor.AcceptedActivityKind);
        Assert.Equal("OoxmlDocumentContainer", descriptor.AcceptedClassification);
    }

    [Fact]
    public void Ooxml_replay_requires_an_explicit_opt_in()
    {
        Assert.False(new RetainedProcessorOptions().OoxmlDocumentStructuralExtractEnabled);
    }

    [Theory]
    [InlineData("word/document.xml", "<w:document xmlns:w='w'><w:p><w:r><w:t>Hello Word</w:t></w:r></w:p></w:document>", "Hello Word")]
    [InlineData("ppt/slides/slide1.xml", "<p:sld xmlns:p='p'><p:txBody><p:p><p:r><p:t>Hello Slide</p:t></p:r></p:p></p:txBody></p:sld>", "Hello Slide")]
    public async Task Valid_ooxml_structural_part_is_retained_as_an_opaque_text_child(string part, string xml, string expectedText)
    {
        var archive = CreateZip(part, xml);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var completion = await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length),
            new RetainedProcessorOptions(), CancellationToken.None);

        var child = Assert.Single(completion.Members);
        Assert.Equal(2, child.OriginKind);
        Assert.Equal(".txt", child.Extension);
        Assert.Contains(expectedText, writer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedText, child.SyntheticLocator, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ooxml_rejects_dtd_before_writing_a_child()
    {
        var archive = CreateZip("word/document.xml", "<!DOCTYPE x [<!ENTITY a 'unsafe'>]><x>&a;</x>");
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-xml-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Generic_zip_with_an_allowlisted_path_is_not_an_ooxml_package()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open())) writer.Write("<w:document xmlns:w='w'><w:t>not a package</w:t></w:document>");
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(new RecordingWriter()).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
    }

    [Fact]
    public async Task Encrypted_compound_office_wrapper_is_blocked_as_encrypted_without_writing_a_child()
    {
        var bytes = CreateEncryptedCompoundOfficeWrapper();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-encrypted", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Malformed_compound_office_wrapper_is_not_treated_as_encrypted()
    {
        var bytes = CreateEncryptedCompoundOfficeWrapper();
        bytes[28] = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V3_difat_backed_encrypted_compound_office_wrapper_at_the_input_limit_is_blocked_without_writing_a_child()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat((128 * 1024 * 1024 - 512) / 512);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-encrypted", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V4_encrypted_compound_office_wrapper_is_blocked_without_writing_a_child()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-encrypted", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_an_out_of_range_root_ministream_start_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 116, 4), 4u);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 120, 8), 4096UL);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_a_root_ministream_allocation_colliding_with_encryption_info_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 116, 4), 2u);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 120, 8), 4096UL);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V4_encrypted_compound_office_wrapper_with_a_valid_root_ministream_allocation_is_blocked_without_writing_a_child()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        Array.Resize(ref bytes, 6 * 4096);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 116, 4), 4u);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 120, 8), 4096UL);
        BitConverter.TryWriteBytes(bytes.AsSpan(4096 + (4 * 4), 4), 0xfffffffeU);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-encrypted", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_small_streams_in_regular_fat_sectors_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3CompoundOfficeWrapperWithSmallStreamsInRegularFatSectors();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_name_only_zero_length_streams_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 128 + 116, 4), 0xfffffffeU);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 128 + 120, 8), 0UL);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 256 + 116, 4), 0xfffffffeU);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 256 + 120, 8), 0UL);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_accepts_uninitialised_high_stream_size_dwords()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 124, 4), uint.MaxValue);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 128 + 124, 4), uint.MaxValue);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 256 + 124, 4), uint.MaxValue);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-encrypted", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_a_free_regular_stream_start_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 128 + 116, 4), 0xffffffffU);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_a_free_regular_stream_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan(4096 + (2 * 4), 4), 0xffffffffU);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_an_out_of_range_regular_stream_start_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 128 + 116, 4), 4u);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_duplicate_regular_stream_allocations_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 256 + 116, 4), 2u);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_a_looping_regular_stream_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 4096) + 128 + 120, 8), 8192UL);
        BitConverter.TryWriteBytes(bytes.AsSpan(4096 + (2 * 4), 4), 2u);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_a_free_ministream_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan(512 + (2 * 512), 4), 0xffffffffU);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_an_out_of_range_ministream_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan(512 + (2 * 512), 4), 16u);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_duplicate_ministream_allocations_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 256 + 116, 4), 0u);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_a_looping_ministream_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan(512 + (2 * 512), 4), 0u);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task V3_compound_office_wrapper_with_an_unallocated_root_ministream_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 116, 4), 0xfffffffeU);

        await AssertInvalidCompoundOfficeWrapperAsync(bytes);
    }

    [Fact]
    public async Task Encrypted_compound_office_wrapper_with_a_dataspaces_storage_is_blocked_without_writing_a_child()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16, includeDataSpaces: true);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-encrypted", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_dataspaces_as_a_stream_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16, includeDataSpaces: true);
        bytes[(2 * 512) + 256 + 66] = 2;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_a_dataspaces_directory_loop_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16, includeDataSpaces: true);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 256 + 76, 4), 2u);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V3_difat_loop_is_not_treated_as_an_encrypted_compound_office_wrapper()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(14_000);
        var firstDifatSector = BitConverter.ToUInt32(bytes, 68);
        BitConverter.TryWriteBytes(bytes.AsSpan(512 + ((int)firstDifatSector * 512) + (127 * 4), 4), firstDifatSector);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V3_duplicate_fat_reference_is_not_treated_as_an_encrypted_compound_office_wrapper()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(14_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(80, 4), 0u);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_a_malformed_unused_directory_entry_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + (2 * 128) + 68, 4), 0u);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_root_storage_outside_the_first_directory_entry_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        ClearCompoundDirectoryEntry(bytes.AsSpan(2 * 512, 128));
        WriteCompoundDirectoryEntry(bytes.AsSpan((2 * 512) + 256, 128), "Root Entry", 5);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_unrepresented_physical_sectors_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        Array.Resize(ref bytes, 512 + (130 * 512));
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_nonfree_fat_entries_beyond_the_input_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan(512 + (16 * 4), 4), 0xfffffffeU);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_a_nonzero_header_clsid_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        bytes[8] = 1;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_a_minifat_directory_collision_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan(60, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(64, 4), 1u);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_an_orphaned_encryption_info_stream_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * 512) + 76, 4), 0xffffffffU);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Compound_office_wrapper_with_an_overlength_directory_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(64, directorySectorCount: 33);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V4_compound_office_wrapper_with_an_overlength_directory_chain_is_not_treated_as_encrypted()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper(directorySectorCount: 33);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task V4_directory_sector_count_mismatch_is_not_treated_as_an_encrypted_compound_office_wrapper()
    {
        var bytes = CreateV4EncryptedCompoundOfficeWrapper();
        BitConverter.TryWriteBytes(bytes.AsSpan(40, 4), 2u);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Ooxml_with_a_nonrepresentable_local_header_offset_is_rejected_before_writing_a_child()
    {
        var archive = PatchCentralLocalHeaderOffsets(CreateZip("word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>"), uint.MaxValue);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Ooxml_with_a_nonrepresentable_local_header_offset_is_recorded_as_a_terminal_failure()
    {
        var archive = PatchCentralLocalHeaderOffsets(CreateZip("word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>"), uint.MaxValue);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var branches = new ClaimingOoxmlBranches(claim);
        var writer = new RecordingWriter();
        var processor = new OoxmlStructuralTextProcessor(writer);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new RecordingCapabilityStore(), new LocalSourceCapabilityHandlerRegistry([new OoxmlStructuralTextCapabilityHandler()])),
            branches,
            new LegacyReader(claim.SourceRevisionId, archive, hash),
            new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = true },
            TimeProvider.System,
            ooxmlProcessor: processor);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedBranches);
        Assert.Equal("office-document-container-invalid", branches.Failure?.OutcomeCode);
        Assert.Null(branches.RetryOutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Unsafe_local_header_on_an_unselected_part_is_rejected_before_any_xml_read_or_child_write()
    {
        var archive = PatchLocalEntryFlags(CreateWordPackageWithAdditionalBinary("customXml/opaque.bin"), "customXml/opaque.bin", 0x0020);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Valid_deflate_option_flag_is_accepted_for_an_ooxml_package()
    {
        var archive = PatchGeneralPurposeFlags(CreateZip("word/document.xml", "<w:document xmlns:w='w'><w:t>deflate option</w:t></w:document>"), 0x0002);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.Contains("deflate option", writer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presentation_slides_are_extracted_in_the_presentation_relationship_order()
    {
        var archive = CreatePresentationWithSlidesInRelationshipOrder();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.True(writer.Text.IndexOf("slide two", StringComparison.Ordinal) < writer.Text.IndexOf("slide ten", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workbook_sheets_follow_workbook_relationship_order_and_exclude_formula_text()
    {
        var archive = CreateWorkbookWithSheetsInRelationshipOrder();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.True(writer.Text.IndexOf("sheet two", StringComparison.Ordinal) < writer.Text.IndexOf("sheet ten", StringComparison.Ordinal));
        Assert.DoesNotContain("formula-private-sentinel", writer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workbook_resolves_shared_string_cells_in_sheet_order_without_emitting_the_shared_string_table()
    {
        var archive = CreateWorkbookWithSharedStringReferences();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.Equal("second shared cell\nfirst shared cell\n", writer.Text);
        Assert.DoesNotContain("\n0\n", writer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n1\n", writer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Main_part_with_a_forged_content_type_is_not_a_genuine_ooxml_package()
    {
        var archive = CreateForgedWordContentTypePackage();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(new RecordingWriter()).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
    }

    [Fact]
    public async Task Forged_ooxml_namespaces_are_rejected_before_writing_a_child()
    {
        var archive = CreateRawZip(
            ("[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>"),
            ("_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>"),
            ("word/document.xml", "<w:document xmlns:w='forged'><w:t>private forged text</w:t></w:document>"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Foreign_namespace_text_inside_a_genuine_word_part_is_not_extracted()
    {
        var archive = CreateRawZip(
            ("[Content_Types].xml", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>"),
            ("_rels/.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>"),
            ("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main' xmlns:x='urn:forged'><w:body><x:t>private foreign text</x:t></w:body></w:document>"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(new RecordingWriter()).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-part-unsupported", error.OutcomeCode);
    }

    [Fact]
    public async Task Legacy_cfb_is_redesignated_from_the_private_retained_reader_even_when_ooxml_is_disabled()
    {
        var bytes = new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1, 0x00 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var candidate = new RetainedProcessorPromotionCandidate(Guid.NewGuid(), SourceRevisionId.New(), hash, ".XLS");
        var branches = new LegacyDesignationBranches(candidate);
        var capabilities = new RecordingCapabilityStore();
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(capabilities, new LocalSourceCapabilityHandlerRegistry([new ZipArchiveRetainedProcessor(null!)])),
            branches, new LegacyReader(candidate.SourceRevisionId, bytes, hash), new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions(), TimeProvider.System);

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.Equal("document-office-legacy-structural-extract", result.Capability);
        Assert.Equal(1, branches.Designations);
        Assert.Empty(capabilities.Registered);
        Assert.False(branches.PromoteOrClaimCalled);
    }

    [Fact]
    public async Task Overlimit_ooxml_is_inspected_and_promoted_without_buffering_retained_bytes()
    {
        var candidate = new RetainedProcessorPromotionCandidate(Guid.NewGuid(), SourceRevisionId.New(), new string('a', 64), ".docx");
        var branches = new InspectionBranches(candidate);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new RecordingCapabilityStore(), new LocalSourceCapabilityHandlerRegistry([new OoxmlStructuralTextCapabilityHandler()])),
            branches, new OverlimitInspectionReader(candidate.SourceRevisionId, candidate.InputSha256), new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = true }, TimeProvider.System,
            ooxmlProcessor: new OoxmlStructuralTextProcessor(null!));

        var result = await activation.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.PromotedBranches);
        Assert.Equal(OoxmlStructuralTextProcessor.Capability, branches.PromotedCapability);
    }

    [Fact]
    public async Task Hosted_service_automatically_runs_the_explicit_ooxml_processor_loop()
    {
        var bytes = CreateZip("word/document.xml", "<w:document xmlns:w='w'><w:t>hosted</w:t></w:document>");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var candidate = new RetainedProcessorPromotionCandidate(Guid.NewGuid(), SourceRevisionId.New(), hash, ".docx");
        var branches = new HostedBranches(candidate);
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new RecordingCapabilityStore(), new LocalSourceCapabilityHandlerRegistry([new OoxmlStructuralTextCapabilityHandler()])),
            branches, new LegacyReader(candidate.SourceRevisionId, bytes, hash), new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = true }, TimeProvider.System,
            ooxmlProcessor: new OoxmlStructuralTextProcessor(null!));
        var hosted = new RetainedProcessorActivationHostedService(new SingleActivationScopeFactory(activation));

        await hosted.StartAsync(CancellationToken.None);
        try
        {
            await branches.Promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }

        Assert.Equal(OoxmlStructuralTextProcessor.Capability, branches.PromotedCapability);
    }

    [Fact]
    public async Task Ooxml_cancellation_records_a_fenced_retry_without_processing_source_data()
    {
        var branches = new CancellationBranches();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var activation = new RetainedProcessorActivationService(
            new SourceCapabilityService(new RecordingCapabilityStore(), new LocalSourceCapabilityHandlerRegistry([new OoxmlStructuralTextCapabilityHandler()])),
            branches, new CancellingReader(cancellation.Token), new ZipArchiveRetainedProcessor(null!),
            new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = true }, TimeProvider.System,
            ooxmlProcessor: new OoxmlStructuralTextProcessor(null!));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await activation.RunOnceAsync(cancellation.Token));

        Assert.Equal("processor-cancelled", branches.RetryOutcome);
    }

    [Fact]
    public async Task Unselected_xml_counts_against_the_package_wide_element_bound_before_any_child_write()
    {
        var archive = CreateWordPackageWithAdditionalXml("customXml/item1.xml", "<root>" + string.Concat(Enumerable.Range(0, 200_000).Select(index => $"<x>{index:D6}</x>")) + "</root>");
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-element-limit", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Claimed_input_larger_than_128_mib_is_rejected_before_opening_the_ooxml_container()
    {
        var bytes = new byte[] { 1 };
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, 128L * 1024 * 1024 + 1), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-input-too-large", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Theory]
    [MemberData(nameof(HostilePackageCases))]
    public async Task Hostile_ooxml_package_is_rejected_before_any_child_write(string expectedOutcome, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal(expectedOutcome, error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    public static TheoryData<string, byte[]> HostilePackageCases()
    {
        var cases = new TheoryData<string, byte[]>();
        cases.Add("office-document-container-invalid", CreateWordPackageWithAdditionalXml("../traversal.xml", "<x/>"));
        cases.Add("office-document-container-invalid", CreateWordPackageWithAdditionalXml("/rooted.xml", "<x/>"));
        cases.Add("office-document-container-invalid", CreateWordPackageWithAdditionalXml("customXml/alternate:stream.xml", "<x/>"));
        cases.Add("office-document-container-invalid", CreateWordPackageWithDuplicateEntry());
        cases.Add("office-document-container-invalid", CreateWordPackageWithAdditionalXml(new string('p', 509) + ".xml", "<x/>"));
        cases.Add("office-document-container-invalid", CreatePackageWithEntryCount(513));
        cases.Add("office-document-expanded-xml-limit", CreateWordPackageWithAdditionalXml("customXml/ratio.xml", "<x>" + new string('x', 20_000) + "</x>"));
        cases.Add("office-document-depth-limit", CreateWordPackageWithAdditionalXml("customXml/depth.xml", "<x>" + string.Concat(Enumerable.Repeat("<x>", 128)) + "z" + string.Concat(Enumerable.Repeat("</x>", 128)) + "</x>"));
        cases.Add("office-document-part-unsupported", CreateWordPackageWithRelationships(8_193));
        cases.Add("office-document-encrypted", PatchGeneralPurposeFlags(CreateWordPackageWithAdditionalXml("customXml/encrypted.xml", "<x/>"), 0x0001));
        cases.Add("office-document-container-invalid", PatchCentralEntryFlags(CreateWordPackageWithAdditionalBinary("customXml/opaque.bin"), "customXml/opaque.bin", 0x0020));
        cases.Add("office-document-container-invalid", PatchExternalAttributes(CreateWordPackageWithAdditionalXml("customXml/link.xml", "<x/>"), 0xA000));
        cases.Add("office-document-container-invalid", PatchWindowsReparsePoint(CreateWordPackageWithAdditionalXml("customXml/reparse.xml", "<x/>")));
        cases.Add("office-document-container-invalid", PatchCompressionMethod(CreateWordPackageWithAdditionalXml("customXml/compression.xml", "<x/>"), 99));
        cases.Add("office-document-container-invalid", PatchMultiVolumeEndRecord(CreateWordPackageWithAdditionalXml("customXml/multi.xml", "<x/>")));
        cases.Add("office-document-part-unsupported", CreateWordPackageWithExternalRelationship());
        return cases;
    }

    [Fact]
    public async Task Exact_32_mib_workbook_text_is_split_into_two_private_16_mib_children()
    {
        var archive = CreateWorkbookWithTwoMaximumTextSheets();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new SegmentLengthWriter();

        var completion = await new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None);

        Assert.Equal(2, completion.Members.Count);
        Assert.Equal([16 * 1024 * 1024, 16 * 1024 * 1024], writer.Lengths);
        Assert.All(completion.Members, member => Assert.Equal(16L * 1024 * 1024, member.ByteLength));
    }

    [Fact]
    public async Task Package_wide_256_mib_expanded_xml_limit_is_rejected_from_central_directory_metadata_before_xml_open()
    {
        var archive = PatchEveryHeaderSize(CreateWordPackageWithManyXmlParts(9), 32 * 1024 * 1024);
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-expanded-xml-limit", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    [Fact]
    public async Task Selected_structural_part_larger_than_32_mib_is_rejected_before_any_child_write()
    {
        var archive = CreateWordPackageWithLargeMainPart();
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, archive, hash, archive.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-part-unsupported", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    private static byte[] CreateZip(string name, string value)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            var main = name.StartsWith("word/", StringComparison.Ordinal) ? "/word/document.xml" : name.StartsWith("xl/", StringComparison.Ordinal) ? "/xl/workbook.xml" : "/ppt/presentation.xml";
            var type = main.StartsWith("/word/", StringComparison.Ordinal) ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" :
                main.StartsWith("/xl/", StringComparison.Ordinal) ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" :
                "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
            var selectedType = name.StartsWith("ppt/", StringComparison.Ordinal) ? "application/vnd.openxmlformats-officedocument.presentationml.slide+xml" : string.Empty;
            WriteEntry(archive, "[Content_Types].xml", $"<Types><Override PartName='{main}' ContentType='{type}'/>{(selectedType.Length == 0 ? string.Empty : $"<Override PartName='/{name}' ContentType='{selectedType}'/>")}</Types>");
            WriteEntry(archive, "_rels/.rels", $"<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='{main[1..]}' /></Relationships>");
            if (name.StartsWith("xl/", StringComparison.Ordinal))
            {
                WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r='r'><sheets /></workbook>");
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships />");
            }
            if (name.StartsWith("ppt/", StringComparison.Ordinal))
            {
                WriteEntry(archive, "ppt/presentation.xml", "<p:presentation xmlns:p='p' xmlns:r='r'><p:sldIdLst><p:sldId r:id='rId1' /></p:sldIdLst></p:presentation>");
                WriteEntry(archive, "ppt/_rels/presentation.xml.rels", $"<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='{name[4..]}' /></Relationships>");
            }
            WriteEntry(archive, name, value);
        }
        return buffer.ToArray();
    }

    private static byte[] CreatePresentationWithSlidesInRelationshipOrder()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/ppt/presentation.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml'/><Override PartName='/ppt/slides/slide2.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.slide+xml'/><Override PartName='/ppt/slides/slide10.xml' ContentType='application/vnd.openxmlformats-officedocument.presentationml.slide+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='ppt/presentation.xml' /></Relationships>");
            WriteEntry(archive, "ppt/presentation.xml", "<p:presentation xmlns:p='p'><p:sldIdLst><p:sldId id='2' r:id='rId2' xmlns:r='r'/><p:sldId id='10' r:id='rId10' xmlns:r='r'/></p:sldIdLst></p:presentation>");
            WriteEntry(archive, "ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide2.xml'/><Relationship Id='rId10' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide' Target='slides/slide10.xml'/></Relationships>");
            WriteEntry(archive, "ppt/slides/slide2.xml", "<p:sld xmlns:p='p'><p:txBody><p:p><p:r><p:t>slide two</p:t></p:r></p:p></p:txBody></p:sld>");
            WriteEntry(archive, "ppt/slides/slide10.xml", "<p:sld xmlns:p='p'><p:txBody><p:p><p:r><p:t>slide ten</p:t></p:r></p:p></p:txBody></p:sld>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWorkbookWithSheetsInRelationshipOrder()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/worksheets/sheet10.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>");
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r='r'><sheets><sheet r:id='rId2'/><sheet r:id='rId10'/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/><Relationship Id='rId10' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet10.xml'/></Relationships>");
            WriteEntry(archive, "xl/worksheets/sheet2.xml", "<worksheet><c><f>formula-private-sentinel</f><v>sheet two</v></c></worksheet>");
            WriteEntry(archive, "xl/worksheets/sheet10.xml", "<worksheet><c><v>sheet ten</v></c></worksheet>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWorkbookWithSharedStringReferences()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/sharedStrings.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml'/><Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>");
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r='r'><sheets><sheet r:id='rId2'/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/><Relationship Id='rIdShared' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings' Target='sharedStrings.xml'/></Relationships>");
            WriteEntry(archive, "xl/sharedStrings.xml", "<sst><si><t>first shared cell</t></si><si><t>second shared cell</t></si></sst>");
            WriteEntry(archive, "xl/worksheets/sheet2.xml", "<worksheet><sheetData><row><c t='s'><v>1</v></c><c t='s'><v>0</v></c></row></sheetData></worksheet>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateForgedWordContentTypePackage()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/not-office.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>forged</w:t></w:document>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithAdditionalXml(string path, string xml)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            WriteEntry(archive, path, xml);
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithAdditionalBinary(string path)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            using var entry = archive.CreateEntry(path).Open();
            entry.WriteByte(1);
        }
        return buffer.ToArray();
    }

    private static byte[] CreateRawZip(params (string Name, string Value)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var (name, value) in entries) WriteRawEntry(archive, name, value);
        return buffer.ToArray();
    }

    private static byte[] CreateWorkbookWithTwoMaximumTextSheets()
    {
        var text = new string('x', 16 * 1024 * 1024 - 1);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>");
            WriteEntry(archive, "xl/workbook.xml", "<workbook xmlns:r='r'><sheets><sheet r:id='rId1'/><sheet r:id='rId2'/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/></Relationships>");
            WriteEntry(archive, "xl/worksheets/sheet1.xml", $"<worksheet><c><v>{text}</v></c></worksheet>", CompressionLevel.NoCompression);
            WriteEntry(archive, "xl/worksheets/sheet2.xml", $"<worksheet><c><v>{text}</v></c></worksheet>", CompressionLevel.NoCompression);
        }
        return buffer.ToArray();
    }

    private static byte[] CreatePackageWithEntryCount(int count)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            for (var index = 3; index < count; index++) WriteEntry(archive, $"customXml/{index:D3}.xml", "<x/>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithDuplicateEntry()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            WriteEntry(archive, "customXml/duplicate.xml", "<x/>");
            WriteEntry(archive, "customXml/duplicate.xml", "<x/>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithRelationships(int count)
    {
        var relationships = new StringBuilder("<Relationships>");
        for (var index = 0; index < count; index++)
            relationships.Append($"<Relationship Id='r{index}' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml' Target='customXml/item{index}.xml'/>");
        relationships.Append("</Relationships>");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            WriteEntry(archive, "word/_rels/document.xml.rels", relationships.ToString());
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithManyXmlParts(int count)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            for (var index = 0; index < count; index++) WriteEntry(archive, $"customXml/item{index}.xml", "<x/>");
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithLargeMainPart()
    {
        var text = new string('x', 32 * 1024 * 1024);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", $"<w:document xmlns:w='w'><w:t>{text}</w:t></w:document>", CompressionLevel.NoCompression);
        }
        return buffer.ToArray();
    }

    private static byte[] CreateWordPackageWithExternalRelationship()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", "<w:document xmlns:w='w'><w:t>safe</w:t></w:document>");
            WriteEntry(archive, "word/_rels/document.xml.rels", "<Relationships><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink' Target='https://example.invalid/' TargetMode='External'/></Relationships>");
        }
        return buffer.ToArray();
    }

    private static byte[] PatchGeneralPurposeFlags(byte[] archive, ushort flags) => PatchEveryHeader(archive, (bytes, offset, central) =>
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + (central ? 8 : 6), 2), flags));

    private static byte[] PatchCentralEntryFlags(byte[] archive, string name, ushort flags)
    {
        var copy = archive.ToArray();
        for (var offset = 0; offset <= copy.Length - 46; offset++)
        {
            if (BitConverter.ToUInt32(copy, offset) != 0x02014b50) continue;
            var length = BitConverter.ToUInt16(copy, offset + 28);
            if (Encoding.UTF8.GetString(copy, offset + 46, length) != name) continue;
            BitConverter.TryWriteBytes(copy.AsSpan(offset + 8, 2), flags);
            return copy;
        }
        throw new Xunit.Sdk.XunitException($"Missing central entry '{name}'.");
    }

    private static byte[] PatchLocalEntryFlags(byte[] archive, string name, ushort flags)
    {
        var copy = archive.ToArray();
        for (var offset = 0; offset <= copy.Length - 46; offset++)
        {
            if (BitConverter.ToUInt32(copy, offset) != 0x02014b50) continue;
            var length = BitConverter.ToUInt16(copy, offset + 28);
            if (Encoding.UTF8.GetString(copy, offset + 46, length) != name) continue;
            var localOffset = checked((int)BitConverter.ToUInt32(copy, offset + 42));
            BitConverter.TryWriteBytes(copy.AsSpan(localOffset + 6, 2), flags);
            return copy;
        }
        throw new Xunit.Sdk.XunitException($"Missing local entry '{name}'.");
    }

    private static byte[] PatchCentralLocalHeaderOffsets(byte[] archive, uint localHeaderOffset) => PatchEveryHeader(archive, (bytes, offset, central) =>
    {
        if (central) BitConverter.TryWriteBytes(bytes.AsSpan(offset + 42, 4), localHeaderOffset);
    });

    private static async Task AssertInvalidCompoundOfficeWrapperAsync(byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var claim = new RetainedProcessorClaim(Guid.NewGuid(), SourceRevisionId.New(), "parent", hash, "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        var writer = new RecordingWriter();

        var error = await Assert.ThrowsAsync<RetainedProcessorException>(() => new OoxmlStructuralTextProcessor(writer).ProcessAsync(
            claim, new RetainedSourceBytes(claim.SourceRevisionId, bytes, hash, bytes.Length), new RetainedProcessorOptions(), CancellationToken.None).AsTask());

        Assert.Equal("office-document-container-invalid", error.OutcomeCode);
        Assert.Equal(0, writer.BytesWritten);
    }

    private static byte[] CreateEncryptedCompoundOfficeWrapper() => CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);

    private static byte[] CreateV3CompoundOfficeWrapperWithSmallStreamsInRegularFatSectors()
    {
        const int sectorSize = 512;
        const uint endOfChain = 0xfffffffe;
        var bytes = CreateV3EncryptedCompoundOfficeWrapperWithDifat(16);
        BitConverter.TryWriteBytes(bytes.AsSpan(60, 4), endOfChain);
        BitConverter.TryWriteBytes(bytes.AsSpan(64, 4), 0u);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * sectorSize) + 116, 4), endOfChain);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * sectorSize) + 120, 8), 0UL);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * sectorSize) + 128 + 116, 4), 5u);
        BitConverter.TryWriteBytes(bytes.AsSpan((2 * sectorSize) + 256 + 116, 4), 6u);
        WriteFatValue(bytes, sectorSize, sectorSize / 4, 5, endOfChain);
        WriteFatValue(bytes, sectorSize, sectorSize / 4, 6, endOfChain);
        return bytes;
    }

    private static byte[] CreateV3EncryptedCompoundOfficeWrapperWithDifat(int availableSectors, bool includeDataSpaces = false, int directorySectorCount = 1)
    {
        const int sectorSize = 512;
        const uint freeSector = 0xffffffff;
        const uint endOfChain = 0xfffffffe;
        const uint fatSector = 0xfffffffdu;
        const uint difatSector = 0xfffffffcu;
        const int fatEntriesPerSector = sectorSize / 4;
        var fatSectorCount = checked((availableSectors + fatEntriesPerSector - 1) / fatEntriesPerSector);
        var difatSectorCount = fatSectorCount <= 109 ? 0 : checked((fatSectorCount - 109 + 126) / 127);
        var directorySector = checked(fatSectorCount + difatSectorCount);
        var finalDirectorySector = checked(directorySector + directorySectorCount - 1);
        var miniFatSector = checked(finalDirectorySector + 1);
        var rootMiniStreamSector = checked(miniFatSector + 1);
        const int rootMiniStreamSectorCount = 2;
        const ulong rootMiniStreamSize = 1024;
        var finalRootMiniStreamSector = checked(rootMiniStreamSector + rootMiniStreamSectorCount - 1);
        if (availableSectors <= finalRootMiniStreamSector) throw new Xunit.Sdk.XunitException("The CFB test fixture needs room for a directory, MiniFAT and root mini-stream.");

        var bytes = new byte[checked(512 + (availableSectors * sectorSize))];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 2), (ushort)0x003e);
        BitConverter.TryWriteBytes(bytes.AsSpan(26, 2), (ushort)3);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 2), (ushort)0xfffe);
        BitConverter.TryWriteBytes(bytes.AsSpan(30, 2), (ushort)9);
        BitConverter.TryWriteBytes(bytes.AsSpan(32, 2), (ushort)6);
        BitConverter.TryWriteBytes(bytes.AsSpan(44, 4), checked((uint)fatSectorCount));
        BitConverter.TryWriteBytes(bytes.AsSpan(48, 4), checked((uint)directorySector));
        BitConverter.TryWriteBytes(bytes.AsSpan(56, 4), 4096u);
        BitConverter.TryWriteBytes(bytes.AsSpan(60, 4), checked((uint)miniFatSector));
        BitConverter.TryWriteBytes(bytes.AsSpan(64, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(68, 4), difatSectorCount == 0 ? endOfChain : checked((uint)fatSectorCount));
        BitConverter.TryWriteBytes(bytes.AsSpan(72, 4), checked((uint)difatSectorCount));
        for (var index = 0; index < 109; index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(76 + (index * 4), 4), index < fatSectorCount ? checked((uint)index) : freeSector);

        for (var index = 0; index < fatSectorCount; index++) bytes.AsSpan(512 + (index * sectorSize), sectorSize).Fill(0xff);
        for (var index = 0; index < fatSectorCount; index++) WriteFatValue(bytes, sectorSize, fatEntriesPerSector, index, fatSector);
        for (var index = 0; index < difatSectorCount; index++)
        {
            var sector = fatSectorCount + index;
            var difat = bytes.AsSpan(512 + (sector * sectorSize), sectorSize);
            difat.Fill(0xff);
            for (var slot = 0; slot < 127; slot++)
            {
                var fatIndex = 109 + (index * 127) + slot;
                BitConverter.TryWriteBytes(difat.Slice(slot * 4, 4), fatIndex < fatSectorCount ? checked((uint)fatIndex) : freeSector);
            }
            BitConverter.TryWriteBytes(difat.Slice(127 * 4, 4), index + 1 < difatSectorCount ? checked((uint)(sector + 1)) : endOfChain);
            WriteFatValue(bytes, sectorSize, fatEntriesPerSector, sector, difatSector);
        }
        for (var index = 0; index < directorySectorCount; index++)
        {
            var currentSector = directorySector + index;
            WriteFatValue(bytes, sectorSize, fatEntriesPerSector, currentSector, index + 1 < directorySectorCount ? checked((uint)(currentSector + 1)) : endOfChain);
        }
        WriteFatValue(bytes, sectorSize, fatEntriesPerSector, miniFatSector, endOfChain);
        WriteFatValue(bytes, sectorSize, fatEntriesPerSector, rootMiniStreamSector, checked((uint)(rootMiniStreamSector + 1)));
        WriteFatValue(bytes, sectorSize, fatEntriesPerSector, rootMiniStreamSector + 1, endOfChain);

        var miniFat = bytes.AsSpan(512 + (miniFatSector * sectorSize), sectorSize);
        miniFat.Fill(0xff);
        for (var miniSector = 0; miniSector < 16; miniSector++)
            BitConverter.TryWriteBytes(miniFat.Slice(miniSector * 4, 4), miniSector is 7 or 15 ? endOfChain : checked((uint)(miniSector + 1)));

        var directory = bytes.AsSpan(512 + (directorySector * sectorSize), sectorSize);
        InitialiseUnusedCompoundDirectoryEntries(directory);
        WriteCompoundDirectoryEntry(directory, "Root Entry", 5, checked((uint)rootMiniStreamSector), rootMiniStreamSize);
        BitConverter.TryWriteBytes(directory.Slice(76, 4), 1u);
        WriteCompoundDirectoryEntry(directory.Slice(128), "EncryptionInfo", 2, 0u, 512UL);
        if (includeDataSpaces)
        {
            BitConverter.TryWriteBytes(directory.Slice(128 + 72, 4), 2u);
            WriteCompoundDirectoryEntry(directory.Slice(256), "\u0006DataSpaces", 1);
            BitConverter.TryWriteBytes(directory.Slice(256 + 72, 4), 3u);
            WriteCompoundDirectoryEntry(directory.Slice(384), "EncryptedPackage", 2, 8u, 512UL);
        }
        else
        {
            BitConverter.TryWriteBytes(directory.Slice(128 + 72, 4), 2u);
            WriteCompoundDirectoryEntry(directory.Slice(256), "EncryptedPackage", 2, 8u, 512UL);
        }
        for (var index = 1; index < directorySectorCount; index++)
            InitialiseUnusedCompoundDirectoryEntries(bytes.AsSpan(512 + ((directorySector + index) * sectorSize), sectorSize));
        return bytes;
    }

    private static byte[] CreateV4EncryptedCompoundOfficeWrapper(int directorySectorCount = 1)
    {
        const int sectorSize = 4096;
        const uint freeSector = 0xffffffff;
        const uint endOfChain = 0xfffffffe;
        var encryptionInfoSector = checked(directorySectorCount + 1);
        var encryptedPackageSector = checked(encryptionInfoSector + 1);
        var bytes = new byte[checked((encryptedPackageSector + 2) * sectorSize)];
        new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }.CopyTo(bytes, 0);
        BitConverter.TryWriteBytes(bytes.AsSpan(24, 2), (ushort)0x003e);
        BitConverter.TryWriteBytes(bytes.AsSpan(26, 2), (ushort)4);
        BitConverter.TryWriteBytes(bytes.AsSpan(28, 2), (ushort)0xfffe);
        BitConverter.TryWriteBytes(bytes.AsSpan(30, 2), (ushort)12);
        BitConverter.TryWriteBytes(bytes.AsSpan(32, 2), (ushort)6);
        BitConverter.TryWriteBytes(bytes.AsSpan(40, 4), checked((uint)directorySectorCount));
        BitConverter.TryWriteBytes(bytes.AsSpan(44, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(48, 4), 1u);
        BitConverter.TryWriteBytes(bytes.AsSpan(56, 4), 4096u);
        BitConverter.TryWriteBytes(bytes.AsSpan(60, 4), endOfChain);
        BitConverter.TryWriteBytes(bytes.AsSpan(68, 4), endOfChain);
        for (var index = 0; index < 109; index++) BitConverter.TryWriteBytes(bytes.AsSpan(76 + (index * 4), 4), index == 0 ? 0u : freeSector);

        var fat = bytes.AsSpan(sectorSize, sectorSize);
        fat.Fill(0xff);
        BitConverter.TryWriteBytes(fat, 0xfffffffdu);
        for (var index = 0; index < directorySectorCount; index++)
            BitConverter.TryWriteBytes(fat.Slice((index + 1) * 4, 4), index + 1 < directorySectorCount ? checked((uint)(index + 2)) : endOfChain);
        BitConverter.TryWriteBytes(fat.Slice(encryptionInfoSector * 4, 4), endOfChain);
        BitConverter.TryWriteBytes(fat.Slice(encryptedPackageSector * 4, 4), endOfChain);
        var directory = bytes.AsSpan(2 * sectorSize, sectorSize);
        InitialiseUnusedCompoundDirectoryEntries(directory);
        WriteCompoundDirectoryEntry(directory, "Root Entry", 5);
        BitConverter.TryWriteBytes(directory.Slice(76, 4), 1u);
        WriteCompoundDirectoryEntry(directory.Slice(128), "EncryptionInfo", 2, checked((uint)encryptionInfoSector), sectorSize);
        BitConverter.TryWriteBytes(directory.Slice(128 + 72, 4), 2u);
        WriteCompoundDirectoryEntry(directory.Slice(256), "EncryptedPackage", 2, checked((uint)encryptedPackageSector), sectorSize);
        for (var index = 1; index < directorySectorCount; index++)
            InitialiseUnusedCompoundDirectoryEntries(bytes.AsSpan((index + 1) * sectorSize, sectorSize));
        return bytes;
    }

    private static void WriteFatValue(byte[] bytes, int sectorSize, int entriesPerFatSector, int valueSector, uint value)
    {
        var fatSector = valueSector / entriesPerFatSector;
        var entry = valueSector % entriesPerFatSector;
        BitConverter.TryWriteBytes(bytes.AsSpan(512 + (fatSector * sectorSize) + (entry * 4), 4), value);
    }

    private static void WriteCompoundDirectoryEntry(Span<byte> entry, string name, byte objectType, uint startSector = 0xfffffffe, ulong streamSize = 0)
    {
        Encoding.Unicode.GetBytes(name + "\0", entry);
        BitConverter.TryWriteBytes(entry.Slice(64, 2), checked((ushort)((name.Length + 1) * 2)));
        entry[66] = objectType;
        entry[67] = 1;
        BitConverter.TryWriteBytes(entry.Slice(68, 4), 0xffffffffU);
        BitConverter.TryWriteBytes(entry.Slice(72, 4), 0xffffffffU);
        BitConverter.TryWriteBytes(entry.Slice(76, 4), 0xffffffffU);
        BitConverter.TryWriteBytes(entry.Slice(116, 4), startSector);
        BitConverter.TryWriteBytes(entry.Slice(120, 8), streamSize);
    }

    private static void InitialiseUnusedCompoundDirectoryEntries(Span<byte> directory)
    {
        for (var offset = 0; offset < directory.Length; offset += 128)
            ClearCompoundDirectoryEntry(directory.Slice(offset, 128));
    }

    private static void ClearCompoundDirectoryEntry(Span<byte> entry)
    {
        entry.Clear();
        BitConverter.TryWriteBytes(entry.Slice(68, 4), 0xffffffffU);
        BitConverter.TryWriteBytes(entry.Slice(72, 4), 0xffffffffU);
        BitConverter.TryWriteBytes(entry.Slice(76, 4), 0xffffffffU);
    }

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

    private static byte[] PatchEveryHeaderSize(byte[] archive, uint size) => PatchEveryHeader(archive, (bytes, offset, central) =>
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + (central ? 20 : 18), 4), size);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + (central ? 24 : 22), 4), size);
    });

    private static void WriteEntry(ZipArchive archive, string name, string value, CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name, compressionLevel).Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(CanonicaliseOoxmlFixture(value));
    }

    private static void WriteRawEntry(ZipArchive archive, string name, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(value);
    }

    private static string CanonicaliseOoxmlFixture(string value) => value
        .Replace("<Types>", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>", StringComparison.Ordinal)
        .Replace("<Relationships>", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>", StringComparison.Ordinal)
        .Replace("xmlns:w='w'", "xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'", StringComparison.Ordinal)
        .Replace("xmlns:p='p'", "xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main'", StringComparison.Ordinal)
        .Replace("xmlns:r='r'", "xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'", StringComparison.Ordinal)
        .Replace("<workbook xmlns:r=", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r=", StringComparison.Ordinal)
        .Replace("<worksheet>", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'>", StringComparison.Ordinal)
        .Replace("<sst>", "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'>", StringComparison.Ordinal);

    private sealed class RecordingWriter : IRetainedArtifactWriter
    {
        public int BytesWritten { get; private set; }
        public string Text { get; private set; } = string.Empty;
        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId parentSourceRevisionId, Stream content, long maximumByteLength, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            BytesWritten += bytes.Length;
            Text += new UTF8Encoding(false, true).GetString(bytes);
            return new RetainedArtifactWriteReceipt(Convert.ToHexStringLower(SHA256.HashData(bytes)), "sha256\\test\\child.bin", bytes.Length, true, false);
        }
    }

    private sealed class SegmentLengthWriter : IRetainedArtifactWriter
    {
        public List<int> Lengths { get; } = [];

        public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(SourceRevisionId parentSourceRevisionId, Stream content, long maximumByteLength, CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream();
            await content.CopyToAsync(stream, cancellationToken);
            var bytes = stream.ToArray();
            Lengths.Add(bytes.Length);
            return new RetainedArtifactWriteReceipt(Convert.ToHexStringLower(SHA256.HashData(bytes)), "sha256\\test\\child.bin", bytes.Length, true, false);
        }
    }

    private sealed class RecordingCapabilityStore : ISourceCapabilityStore
    {
        public List<RegisteredSourceCapability> Registered { get; } = [];
        public ValueTask<RegisteredSourceCapability> RegisterAsync(RegisteredSourceCapability capability, CancellationToken cancellationToken)
        {
            Registered.Add(capability);
            return ValueTask.FromResult(capability);
        }

        public ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RegisteredSourceCapability?>(null);
    }

    private sealed class LegacyReader(SourceRevisionId sourceRevisionId, byte[] bytes, string hash) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(sourceRevisionId, requestedRevisionId);
            return ValueTask.FromResult(new RetainedSourceBytes(sourceRevisionId, bytes, hash, bytes.Length));
        }

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Legacy designation must not decode retained bytes as text.");
    }

    private sealed class LegacyDesignationBranches(RetainedProcessorPromotionCandidate candidate) : IRetainedProcessorBranchStore
    {
        public int Designations { get; private set; }
        public bool PromoteOrClaimCalled { get; private set; }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken)
        {
            PromoteOrClaimCalled = true;
            return ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);
        }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadLegacyOfficeDesignationCandidatesAsync(int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([candidate]);

        public ValueTask<bool> DesignateLegacyOfficeAsync(RetainedProcessorPromotionCandidate value, CancellationToken cancellationToken)
        {
            Assert.Equal(candidate, value);
            Designations++;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken cancellationToken)
        {
            PromoteOrClaimCalled = true;
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken)
        {
            PromoteOrClaimCalled = true;
            return ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]);
        }

        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class ClaimingOoxmlBranches(RetainedProcessorClaim claim) : IRetainedProcessorBranchStore
    {
        public RetainedProcessorFailure? Failure { get; private set; }
        public string? RetryOutcomeCode { get; private set; }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);

        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([claim]);

        public ValueTask<bool> CommitAsync(RetainedProcessorClaim value, RetainedProcessorCompletion completion, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> RetryAsync(RetainedProcessorClaim value, string outcomeCode, CancellationToken cancellationToken)
        {
            RetryOutcomeCode = outcomeCode;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> FailAsync(RetainedProcessorClaim value, RetainedProcessorFailure failure, CancellationToken cancellationToken)
        {
            Failure = failure;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class OverlimitInspectionReader(SourceRevisionId sourceRevisionId, string hash) : IRetainedSourceReader
    {
        public ValueTask<RetainedArtifactInspection> InspectAsync(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken)
        {
            Assert.Equal(sourceRevisionId, requestedRevisionId);
            return ValueTask.FromResult(new RetainedArtifactInspection(sourceRevisionId, hash, 128L * 1024 * 1024 + 1));
        }

        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Over-limit OOXML promotion must not buffer retained bytes.");

        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId requestedRevisionId, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("OOXML promotion must not decode retained bytes as text.");
    }

    private sealed class InspectionBranches(RetainedProcessorPromotionCandidate candidate) : IRetainedProcessorBranchStore
    {
        public SourceCapabilityDescriptor? PromotedCapability { get; private set; }
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([candidate]);
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken cancellationToken)
        {
            Assert.Equal(candidate, value);
            PromotedCapability = capability;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]);
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class HostedBranches(RetainedProcessorPromotionCandidate candidate) : IRetainedProcessorBranchStore
    {
        private int _readCount;
        public TaskCompletionSource Promoted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SourceCapabilityDescriptor? PromotedCapability { get; private set; }

        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>(Interlocked.Increment(ref _readCount) == 1 ? [candidate] : []);
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken cancellationToken)
        {
            Assert.Equal(candidate, value);
            PromotedCapability = capability;
            Promoted.TrySetResult();
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([]);
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class CancellingReader(CancellationToken cancellationToken) : IRetainedSourceReader
    {
        public ValueTask<RetainedSourceBytes> ReadBytesAsync(SourceRevisionId sourceRevisionId, CancellationToken ignored) =>
            ValueTask.FromException<RetainedSourceBytes>(new OperationCanceledException(cancellationToken));
        public ValueTask<Utf8FileSource> ReadUtf8Async(SourceRevisionId sourceRevisionId, CancellationToken ignored) =>
            ValueTask.FromException<Utf8FileSource>(new OperationCanceledException(cancellationToken));
    }

    private sealed class CancellationBranches : IRetainedProcessorBranchStore
    {
        private readonly RetainedProcessorClaim _claim = new(Guid.NewGuid(), SourceRevisionId.New(), "parent", new string('a', 64), "owner", 1, DateTimeOffset.UtcNow.AddMinutes(5));
        public string? RetryOutcome { get; private set; }
        public ValueTask<IReadOnlyList<RetainedProcessorPromotionCandidate>> ReadPromotionCandidatesAsync(int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorPromotionCandidate>>([]);
        public ValueTask<bool> PromoteAsync(RetainedProcessorPromotionCandidate value, SourceCapabilityDescriptor capability, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> BlockPromotionAsync(RetainedProcessorPromotionCandidate value, string outcomeCode, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<IReadOnlyList<RetainedProcessorClaim>> ClaimAsync(string leaseOwner, int maximumCount, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RetainedProcessorClaim>>([_claim]);
        public ValueTask<bool> CommitAsync(RetainedProcessorClaim claim, RetainedProcessorCompletion completion, CancellationToken cancellationToken) => ValueTask.FromResult(false);
        public ValueTask<bool> RetryAsync(RetainedProcessorClaim claim, string outcomeCode, CancellationToken cancellationToken)
        {
            RetryOutcome = outcomeCode;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> FailAsync(RetainedProcessorClaim claim, RetainedProcessorFailure failure, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class SingleActivationScopeFactory(RetainedProcessorActivationService activation) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new SingleActivationScope(activation);
    }

    private sealed class SingleActivationScope(RetainedProcessorActivationService activation) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new SingleActivationServiceProvider(activation);
        public void Dispose() { }
    }

    private sealed class SingleActivationServiceProvider(RetainedProcessorActivationService activation) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(RetainedProcessorActivationService) ? activation : null;
    }
}
