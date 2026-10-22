using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

[SupportedOSPlatform("windows")]
internal static class SourceAccessPipeServer
{
    internal static async Task ServeOneAsync(SourceAccessPolicy policy, WindowsSourceAccessBroker broker, SecurityIdentifier owner,
        CancellationToken cancellationToken)
    {
        using var pipe = Create(policy, owner);
        await ServeConnectionAsync(pipe, policy, broker, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunAsync(SourceAccessPolicy policy, WindowsSourceAccessBroker broker, CancellationToken cancellationToken,
        SecurityIdentifier? owner = null)
    {
        using var pipe = Create(policy, owner ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        while (!cancellationToken.IsCancellationRequested)
        {
            await ServeConnectionAsync(pipe, policy, broker, cancellationToken).ConfigureAwait(false);
            // A client EOF can mark the stream broken. It must still be
            // disconnected before the same server instance accepts again.
            pipe.Disconnect();
        }
    }

    internal static NamedPipeServerStream Create(SourceAccessPolicy policy, SecurityIdentifier owner)
    {
        SourceAccessPipeProtocol.ValidatePipeName(policy.PipeName);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(new SecurityIdentifier(policy.IndexerSid), SourceAccessPipeProtocol.ClientRights, AccessControlType.Allow));
        security.AddAccessRule(new(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(policy.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security, HandleInheritability.None, (PipeAccessRights)0);
    }

    private static async Task ServeConnectionAsync(NamedPipeServerStream pipe, SourceAccessPolicy policy, WindowsSourceAccessBroker broker,
        CancellationToken cancellationToken)
    {
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        var connectionDeadline = DateTime.UtcNow.Add(SourceAccessPipeProtocol.RequestTimeout).Ticks;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SourceAccessPipeProtocol.RequestTimeout);
        try
        {
            var request = await SourceAccessPipeProtocol.ReadAsync<SourceAccessPipeProtocol.Request>(pipe, deadline.Token).ConfigureAwait(false);
            using WindowsIdentity indexer = ClientIdentity(pipe);
            var remaining = Math.Min(request.DeadlineUtcTicks, connectionDeadline) - DateTime.UtcNow.Ticks;
            SourceAccessRepairOutcome outcome;
            if (request.Version != 1 || request.Item is null || remaining <= 0 ||
                remaining > SourceAccessPipeProtocol.RequestTimeout.Ticks || indexer.User?.Value != policy.IndexerSid)
                outcome = SourceAccessRepairOutcome.NotApproved;
            else
            {
                deadline.CancelAfter(TimeSpan.FromTicks(remaining));
                outcome = broker.Repair(request.Item, indexer, deadline.Token, Math.Min(request.DeadlineUtcTicks, connectionDeadline));
            }
            await SourceAccessPipeProtocol.WriteAsync(pipe, new SourceAccessPipeProtocol.Response(1, outcome), deadline.Token).ConfigureAwait(false);
            var acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, deadline.Token).ConfigureAwait(false);
            if (acknowledgement[0] != 1) throw new InvalidDataException("SourceAccessAcknowledgementInvalid");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Win32Exception or ArgumentException) { }
    }

    private static WindowsIdentity ClientIdentity(NamedPipeServerStream pipe)
    {
        WindowsIdentity? identity = null;
        pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate));
        return identity ?? throw new UnauthorizedAccessException("SourceAccessClientIdentityUnavailable");
    }
}
