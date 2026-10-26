using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Application.Ports;

namespace FluxKnowledge.Integrations.Files;

internal sealed record SourceJournalPolicy(int Version, string PipeName, string[] ApprovedVolumes, bool RepairEnabled = false)
{
    internal static SourceJournalPolicy Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (OperatingSystem.IsWindows()) SourceAccessPipeProtocol.RejectDuplicateProperties(document.RootElement);
        var policy = JsonSerializer.Deserialize<SourceJournalPolicy>(bytes, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true, MaxDepth = 8
        }) ?? throw new InvalidDataException("SourceJournalPolicyInvalid");
        SourceJournalPipeProtocol.ValidatePipeName(policy.PipeName);
        if (policy.Version != 1 || policy.ApprovedVolumes.Length is 0 or > 16 ||
            policy.ApprovedVolumes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != policy.ApprovedVolumes.Length ||
            policy.ApprovedVolumes.Any(x => x != WindowsSourceChangeJournalReader.CanonicalVolume(x)))
            throw new InvalidDataException("SourceJournalPolicyInvalid");
        return policy;
    }
}

[SupportedOSPlatform("windows")]
internal static class SourceJournalPipeServer
{
    internal static async Task RunAsync(SourceJournalPolicy policy, string indexerSid, Action validateStorage,
        CancellationToken ct, SecurityIdentifier? owner = null)
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        var serverOwner = owner ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetOwner(serverOwner);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(new SecurityIdentifier(indexerSid), SourceAccessPipeProtocol.ClientRights, AccessControlType.Allow));
        security.AddAccessRule(new(serverOwner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(policy.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 65536, security, HandleInheritability.None, 0);
        var reader = new WindowsSourceChangeJournalReader();
        while (!ct.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(SourceJournalPipeProtocol.Timeout);
            try
            {
                validateStorage();
                var request = await SourceJournalPipeProtocol.ReadAsync<SourceJournalPipeProtocol.Request>(pipe, deadline.Token).ConfigureAwait(false);
                string? sid = null;
                pipe.RunAsClient(() => { using var caller = WindowsIdentity.GetCurrent(); sid = caller.User?.Value; });
                SourceJournalPipeProtocol.Response response;
                if (sid != indexerSid || !policy.ApprovedVolumes.Any(v => SourceJournalPipeProtocol.IsAllowed(request, v)))
                    response = new(1, null, null, "not-approved");
                else
                {
                    try
                    {
                        response = request.Operation == "query"
                            ? new(1, await reader.QueryAsync(request.Cursor.VolumeIdentity, deadline.Token).ConfigureAwait(false), null, null)
                            : new(1, null, await reader.ReadAsync(request.Cursor, request.MaximumBytes, deadline.Token).ConfigureAwait(false), null);
                    }
                    catch (SourceJournalGapException) { response = new(1, null, null, "gap"); }
                    catch (IOException) { response = new(1, null, null, "unavailable"); }
                }
                validateStorage();
                await SourceJournalPipeProtocol.WriteAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                var acknowledged = new byte[1];
                await pipe.ReadExactlyAsync(acknowledged, deadline.Token).ConfigureAwait(false);
                if (acknowledged[0] != 1) throw new InvalidDataException("SourceJournalAcknowledgementInvalid");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or System.ComponentModel.Win32Exception) { }
            finally { pipe.Disconnect(); }
        }
    }
}
