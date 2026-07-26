using System.Security.Cryptography;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Workers;
using FluxKnowledge.Domain.Common;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Pipeline;

public sealed class RegisterUtf8FileHandlerTests
{
    private readonly RecordingRegistrationStore _store = new();
    private readonly StubUtf8FileSourceReader _reader = new();
    private readonly RegisterUtf8FileHandler _handler;

    public RegisterUtf8FileHandlerTests()
    {
        _handler = new RegisterUtf8FileHandler(_reader, _store);
    }

    [Fact]
    public async Task Same_file_revision_creates_one_record_job_and_outbox_message()
    {
        var command = new RegisterUtf8FileCommand("C:/ingress/a.txt", "test", "a.txt");

        var first = await _handler.HandleAsync(command, CancellationToken.None);
        var second = await _handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(first.PipelineRecordId, second.PipelineRecordId);
        Assert.Equal(first.InitialJobId, second.InitialJobId);
        Assert.Equal(first.InitialDispatchMessageId, second.InitialDispatchMessageId);
        Assert.False(first.ExistingReceipt);
        Assert.True(second.ExistingReceipt);
        Assert.Single(_store.Records);
        Assert.Single(_store.Jobs);
        Assert.Single(_store.OutboxMessages);
    }

    [Fact]
    public async Task Changed_bytes_create_a_linked_new_revision_without_overwriting_the_first()
    {
        var command = new RegisterUtf8FileCommand("C:/ingress/a.txt", "test", "a.txt");
        var first = await _handler.HandleAsync(command, CancellationToken.None);
        _reader.SetContent("changed");

        var second = await _handler.HandleAsync(command, CancellationToken.None);

        Assert.NotEqual(first.PipelineRecordId, second.PipelineRecordId);
        Assert.False(second.ExistingReceipt);
        Assert.Collection(
            _store.Records,
            original =>
            {
                Assert.Equal(1, original.Revision);
                Assert.Equal(original.PipelineRecordId, original.RootLineageRecordId);
                Assert.Null(original.ParentRevisionRecordId);
            },
            revision =>
            {
                Assert.Equal(2, revision.Revision);
                Assert.Equal(_store.Records[0].RootLineageRecordId, revision.RootLineageRecordId);
                Assert.Equal(_store.Records[0].PipelineRecordId, revision.ParentRevisionRecordId);
            });
        Assert.Equal(2, _store.Jobs.Count);
        Assert.Equal(2, _store.OutboxMessages.Count);
    }

    [Fact]
    public async Task New_durable_registration_notifies_the_pump_only_after_the_receipt_exists()
    {
        var wakeSignal = new RecordingWakeSignal(_store);
        var handler = new RegisterUtf8FileHandler(_reader, _store, wakeSignal);
        var command = new RegisterUtf8FileCommand("C:/ingress/a.txt", "test", "a.txt");

        await handler.HandleAsync(command, CancellationToken.None);
        await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(1, wakeSignal.NotificationCount);
        Assert.True(wakeSignal.DurableReceiptExistedAtNotification);
    }

    private sealed class StubUtf8FileSourceReader : IUtf8FileSourceReader
    {
        private byte[] _bytes = "first"u8.ToArray();

        public void SetContent(string value)
        {
            _bytes = System.Text.Encoding.UTF8.GetBytes(value);
        }

        public ValueTask<Utf8FileSource> ReadAsync(
            string suppliedPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new Utf8FileSource(
                    "C:\\ingress\\a.txt",
                    _bytes,
                    System.Text.Encoding.UTF8.GetString(_bytes),
                    Convert.ToHexStringLower(SHA256.HashData(_bytes))));
        }
    }

    private sealed class RecordingRegistrationStore : IRegistrationStore
    {
        private readonly Dictionary<string, RegistrationReceipt> _latest =
            new(StringComparer.OrdinalIgnoreCase);

        public List<RegisteredPipelineRecord> Records { get; } = [];
        public List<JobId> Jobs { get; } = [];
        public List<DispatchMessageId> OutboxMessages { get; } = [];

        public ValueTask<RegistrationReceipt> RegisterAsync(
            Utf8FileRegistration registration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_latest.TryGetValue(registration.CanonicalPath, out var latest) &&
                string.Equals(latest.ContentHash, registration.ContentHash, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(latest with { ExistingReceipt = true });
            }

            var recordId = PipelineRecordId.New();
            var revision = _latest.TryGetValue(registration.CanonicalPath, out latest)
                ? latest.Revision + 1
                : 1;
            var rootId = latest?.RootLineageRecordId ?? recordId;
            var receipt = new RegistrationReceipt(
                recordId,
                JobId.New(),
                DispatchMessageId.New(),
                revision,
                registration.ContentHash,
                rootId,
                latest?.PipelineRecordId,
                false);
            _latest[registration.CanonicalPath] = receipt;
            Records.Add(
                new RegisteredPipelineRecord(
                    receipt.PipelineRecordId,
                    receipt.Revision,
                    receipt.ContentHash,
                    receipt.RootLineageRecordId,
                    receipt.ParentRevisionRecordId));
            Jobs.Add(receipt.InitialJobId);
            OutboxMessages.Add(receipt.InitialDispatchMessageId);
            return ValueTask.FromResult(receipt);
        }
    }

    private sealed record RegisteredPipelineRecord(
        PipelineRecordId PipelineRecordId,
        long Revision,
        string ContentHash,
        PipelineRecordId RootLineageRecordId,
        PipelineRecordId? ParentRevisionRecordId);

    private sealed class RecordingWakeSignal(RecordingRegistrationStore store)
        : IOutboxWakeSignal
    {
        public int NotificationCount { get; private set; }
        public bool DurableReceiptExistedAtNotification { get; private set; }

        public void Notify()
        {
            NotificationCount++;
            DurableReceiptExistedAtNotification = store.OutboxMessages.Count > 0;
        }

        public ValueTask WaitAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
