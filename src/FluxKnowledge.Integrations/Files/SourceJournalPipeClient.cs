using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceJournalPipeClient : ISourceChangeJournalReader
{
    private readonly SourceJournalOptions _options;
    private readonly SecurityIdentifier _expectedOwner;
    public SourceJournalPipeClient(SourceJournalOptions options) : this(options, new(WellKnownSidType.LocalSystemSid, null)) { }
    internal SourceJournalPipeClient(SourceJournalOptions options, SecurityIdentifier expectedOwner)
    {
        _options = options;
        _expectedOwner = expectedOwner;
    }

    public async ValueTask<JournalCapability> QueryAsync(string volumeIdentity, CancellationToken cancellationToken)
        => (await SendAsync("query", new(volumeIdentity, 0, 0), 65536, cancellationToken).ConfigureAwait(false)).Capability
            ?? throw new IOException("SourceJournalCapabilityMissing");
    public async ValueTask<JournalPage> ReadAsync(JournalCursor cursor, int maximumBytes, CancellationToken cancellationToken)
        => (await SendAsync("read", cursor, maximumBytes, cancellationToken).ConfigureAwait(false)).Page
            ?? throw new IOException("SourceJournalPageMissing");
    private async Task<SourceJournalPipeProtocol.Response> SendAsync(string operation, JournalCursor cursor, int bytes, CancellationToken ct)
    {
        var pipeName = _options.PipeName ?? throw new IOException("SourceJournalPipeNotConfigured");
        SourceJournalPipeProtocol.ValidatePipeName(pipeName);
        if (!_options.ApprovedVolumes.Contains(cursor.VolumeIdentity, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("SourceJournalVolumeNotApproved");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var absoluteDeadline = DateTimeOffset.UtcNow.Add(SourceJournalPipeProtocol.Timeout).UtcTicks;
        deadline.CancelAfter(SourceJournalPipeProtocol.Timeout);
        await using var pipe = new NamedPipeClientStream(".", pipeName, SourceAccessPipeProtocol.ClientRights,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (WindowsSourcePipeSecurity.OwnerOf(pipe.SafePipeHandle) != _expectedOwner)
                throw new IOException("SourceJournalHelperUnauthenticated");
        }
        catch (Win32Exception error) { throw new IOException("SourceJournalHelperUnauthenticated", error); }
        deadline.Token.ThrowIfCancellationRequested();
        await SourceJournalPipeProtocol.WriteAsync(pipe, new SourceJournalPipeProtocol.Request(1,
            absoluteDeadline, operation, cursor, bytes), deadline.Token).ConfigureAwait(false);
        var response = await SourceJournalPipeProtocol.ReadAsync<SourceJournalPipeProtocol.Response>(pipe, deadline.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
        await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
        if (response.Version != 1) throw new InvalidDataException("SourceJournalProtocolMismatch");
        if (response.Error == "gap") throw new SourceJournalGapException("SourceJournalContinuityLost");
        if (response.Error is not null) throw new IOException("SourceJournalHelperUnavailable");
        return response;
    }
}
