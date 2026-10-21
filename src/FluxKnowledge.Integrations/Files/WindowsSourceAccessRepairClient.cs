using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using Microsoft.Win32.SafeHandles;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
public sealed class WindowsSourceAccessRepairClient : ISourceAccessRepairClient
{
    private readonly string _pipeName;
    private readonly SecurityIdentifier _expectedOwner;
    public WindowsSourceAccessRepairClient(string pipeName) : this(pipeName, new(WellKnownSidType.LocalSystemSid, null)) { }
    internal WindowsSourceAccessRepairClient(string pipeName, SecurityIdentifier expectedOwner)
    {
        SourceAccessPipeProtocol.ValidatePipeName(pipeName);
        _pipeName = pipeName;
        _expectedOwner = expectedOwner;
    }
    public SourceAccessRepairOutcome TryRepair(SourceAccessRepairRequest request, CancellationToken cancellationToken) =>
        RepairAsync(request, cancellationToken).GetAwaiter().GetResult();

    private async Task<SourceAccessRepairOutcome> RepairAsync(SourceAccessRepairRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absoluteDeadline = DateTime.UtcNow.Add(SourceAccessPipeProtocol.RequestTimeout).Ticks;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SourceAccessPipeProtocol.RequestTimeout);
        var requestSent = false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, SourceAccessPipeProtocol.ClientRights,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            // Authenticate before disclosing a private source path. Identification
            // permits token checks without delegating the caller's filesystem access.
            if (OwnerOf(pipe.SafePipeHandle) != _expectedOwner) return SourceAccessRepairOutcome.Unavailable;
            deadline.Token.ThrowIfCancellationRequested();
            requestSent = true;
            await SourceAccessPipeProtocol.WriteAsync(pipe, new SourceAccessPipeProtocol.Request(1,
                absoluteDeadline, request), deadline.Token).ConfigureAwait(false);
            var response = await SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Response>(pipe, deadline.Token).ConfigureAwait(false);
            if (response.Version != 1 || !Enum.IsDefined(response.Outcome)) return SourceAccessRepairOutcome.Unconfirmed;
            // DisconnectNamedPipe discards unread bytes. Confirm receipt before the
            // server reuses its instance; a lost ACK cannot erase a verified result.
            try { await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false); }
            catch (IOException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            return response.Outcome;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return requestSent ? SourceAccessRepairOutcome.Unconfirmed : SourceAccessRepairOutcome.Unavailable; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException)
        { return requestSent ? SourceAccessRepairOutcome.Unconfirmed : SourceAccessRepairOutcome.Unavailable; }
    }

    private static SecurityIdentifier OwnerOf(SafePipeHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 1, out var owner, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception(checked((int)error));
        try { return new SecurityIdentifier(owner); }
        finally { _ = LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int type, uint information, out IntPtr owner,
        out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
