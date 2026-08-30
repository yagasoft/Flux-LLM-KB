using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Integrations.Files;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Resolves the parent revision's private artifact root before streaming a generated child artifact.</summary>
public sealed class SqlRetainedArtifactWriter(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    string artifactRoot,
    IEnumerable<string>? protectedRoots = null,
    PersistedOutlookSpoolRootPolicy? outlookSpoolPolicy = null) : IRetainedArtifactWriter
{
    private readonly string _artifactRoot = ContentAddressedSourceArtifactStore.ValidateRoot(artifactRoot, protectedRoots);
    private readonly string[] _protectedRoots = protectedRoots?.ToArray() ?? [];

    public async ValueTask<RetainedArtifactWriteReceipt> WriteAsync(
        SourceRevisionId parentSourceRevisionId,
        Stream content,
        long maximumByteLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentSourceRevisionId);
        ArgumentNullException.ThrowIfNull(content);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sourceRootId = await context.SourceRevisions.AsNoTracking()
            .Where(revision => revision.Id == parentSourceRevisionId.Value)
            .Select(revision => (Guid?)revision.SourceRootId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retained artifact parent revision does not exist.");
        var privateRoot = await context.OutlookCaptureProfiles.AsNoTracking()
            .Where(profile => profile.SourceRootId == sourceRootId)
            .Select(profile => profile.SpoolRoot)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (privateRoot is not null && string.IsNullOrWhiteSpace(privateRoot))
        {
            throw new InvalidDataException("The parent private artifact root is invalid.");
        }

        var selectedRoot = privateRoot is null
            ? _artifactRoot
            : outlookSpoolPolicy?.RequireCanonicalBeforeIo(privateRoot)
                ?? throw new InvalidDataException("The persisted Outlook spool root is unavailable.");
        var selectedProtectedRoots = privateRoot is null
            ? _protectedRoots
            : [.. _protectedRoots, _artifactRoot];
        using var store = new ContentAddressedSourceArtifactStore(selectedRoot, selectedProtectedRoots);
        using var classified = new BoundedClassifyingReadStream(content, maximumByteLength);
        var receipt = await store.PutStreamAsync(classified, maximumByteLength, cancellationToken).ConfigureAwait(false);
        return new RetainedArtifactWriteReceipt(
            receipt.ContentSha256,
            receipt.StoreRelativePath,
            receipt.ByteLength,
            classified.IsUtf8Text,
            classified.IsNestedArchive);
    }

    private sealed class BoundedClassifyingReadStream(Stream inner, long maximumByteLength) : Stream
    {
        private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly List<byte> _prefix = [];
        private long _length;
        private bool _completed;

        public bool IsUtf8Text { get; private set; } = true;
        public bool IsNestedArchive { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Observe(inner.Read(buffer, offset, count), buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Observe(read, buffer.Span);
        }

        private int Observe(int read, ReadOnlySpan<byte> buffer)
        {
            if (read == 0)
            {
                CompleteTextValidation();
                return 0;
            }
            if (read > maximumByteLength - _length)
            {
                throw new RetainedProcessorException("archive-member-size-limit");
            }

            var content = buffer[..read];
            _length += read;
            foreach (var value in content)
            {
                if (_prefix.Count < 512)
                {
                    _prefix.Add(value);
                }
            }
            IsNestedArchive = ZipArchiveRetainedProcessor.IsZipSignature(_prefix.ToArray()) ||
                TarArchiveRetainedProcessor.IsTarSignature(_prefix.ToArray());
            if (IsNestedArchive)
            {
                throw new RetainedProcessorException("nested-archive-depth-limit");
            }

            ValidateText(content, flush: false);
            return read;
        }

        private void CompleteTextValidation()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            ValidateText(ReadOnlySpan<byte>.Empty, flush: true);
        }

        private void ValidateText(ReadOnlySpan<byte> content, bool flush)
        {
            var chars = new char[new UTF8Encoding(false, true).GetMaxCharCount(content.Length)];
            try
            {
                _decoder.Convert(content, chars, flush, out _, out _, out _);
            }
            catch (DecoderFallbackException)
            {
                IsUtf8Text = false;
                throw new RetainedProcessorException("archive-member-not-utf8");
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
