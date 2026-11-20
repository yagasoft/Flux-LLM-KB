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
    /// <summary>Checks that the trusted helper explicitly refuses a volume excluded by the app.</summary>
    public async ValueTask<bool> IsVolumeRefusedAsync(string volumeIdentity, CancellationToken cancellationToken)
    {
        if (volumeIdentity.Length != 49 || !volumeIdentity.StartsWith(@"\\?\VOLUME{", StringComparison.Ordinal) ||
            !volumeIdentity.EndsWith(@"}\", StringComparison.Ordinal) ||
            !Guid.TryParseExact(volumeIdentity.AsSpan(11, 36), "D", out _) ||
            volumeIdentity != volumeIdentity.ToUpperInvariant() ||
            _options.ApprovedVolumes.Contains(volumeIdentity, StringComparer.OrdinalIgnoreCase))
            return false;

        try
        {
            _ = await SendAsync("query", new(volumeIdentity, 0, 0), 65536, cancellationToken, requireApproved: false)
                .ConfigureAwait(false);
            return false;
        }
        catch (IOException failure)
        {
            return failure.Message == "SourceJournalHelperRefused";
        }
    }

    private async Task<SourceJournalPipeProtocol.Response> SendAsync(string operation, JournalCursor cursor, int bytes,
        CancellationToken ct, bool requireApproved = true)
    {
        var pipeName = _options.PipeName ?? throw new IOException("SourceJournalPipeNotConfigured");
        SourceJournalPipeProtocol.ValidatePipeName(pipeName);
        if (requireApproved && !_options.ApprovedVolumes.Contains(cursor.VolumeIdentity, StringComparer.OrdinalIgnoreCase))
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
        if (response.Error == "cursor-rejected")
            throw new SourceJournalGapException("SourceJournalCursorRejected", new Win32Exception(87));
        if (response.Error == "gap") throw new SourceJournalGapException("SourceJournalContinuityLost");
        if (response.Error == "not-approved") throw new IOException("SourceJournalHelperRefused");
        if (response.Error is "journal-inactive" or "journal-deleting" or "volume-unavailable" or "volume-access-denied")
            throw new IOException(response.Error);
        if (response.Error is not null) throw new IOException("SourceJournalHelperUnavailable");
        return response;
    }
}
