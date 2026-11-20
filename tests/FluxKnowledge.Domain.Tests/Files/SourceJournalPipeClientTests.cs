using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Files;

[SupportedOSPlatform("windows")]
public sealed class SourceJournalPipeClientTests
{
    private const string Volume = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";

    [Theory]
    [InlineData("not-approved", "SourceJournalHelperRefused")]
    [InlineData("journal-inactive", "journal-inactive")]
    [InlineData("journal-deleting", "journal-deleting")]
    [InlineData("unexpected-code", "SourceJournalHelperUnavailable")]
    public async Task Authenticated_helper_errors_are_bounded_and_preserve_operator_action(string wireError, string expected)
    {
        var options = Options();
        var client = new SourceJournalPipeClient(options, WindowsIdentity.GetCurrent().User!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = CreateUserOwnedServer(options.PipeName!);
        var server = ReplyAsync(pipe, deadline.Token, error: wireError);
        var failure = await Assert.ThrowsAsync<IOException>(() => client.QueryAsync(Volume, deadline.Token).AsTask());
        Assert.Equal(expected, failure.Message);
        Assert.True(await server > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_client_rejects_a_non_SYSTEM_server_before_sending_any_request(bool read)
    {
        var options = Options();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = CreateUserOwnedServer(options.PipeName!);
        var server = ReplyAsync(pipe, deadline.Token);
        var client = new SourceJournalPipeClient(options);
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (read) _ = await client.ReadAsync(new(Volume, 7, 100), 65536, deadline.Token);
            else _ = await client.QueryAsync(Volume, deadline.Token);
        });
        var requestBytes = await server;
        Assert.IsAssignableFrom<IOException>(failure);
        Assert.Equal(0, requestBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restricted_indexer_uses_only_granted_client_rights(bool read)
    {
        var options = Options();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = CreateUserOwnedServer(options.PipeName!);
        var server = ReplyAsync(pipe, deadline.Token);
        var client = new SourceJournalPipeClient(options, WindowsIdentity.GetCurrent().User!);
        using var token = SourceAccessBrokerTests.RestrictedToken();
        await WindowsIdentity.RunImpersonatedAsync(token, async () =>
        {
            if (read) Assert.Equal(999, (await client.ReadAsync(new(Volume, 7, 100), 65536, deadline.Token)).Next.NextUsn);
            else Assert.Equal(999, (await client.QueryAsync(Volume, deadline.Token)).NextUsn);
        });
        Assert.True(await server > 0);
    }

    [Fact]
    public async Task Caller_cancellation_while_connecting_does_not_poison_a_later_request()
    {
        var options = Options();
        var client = new SourceJournalPipeClient(options, WindowsIdentity.GetCurrent().User!);
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync(Volume, cancelled.Token).AsTask());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = CreateUserOwnedServer(options.PipeName!);
        var server = ReplyAsync(pipe, deadline.Token);
        Assert.Equal(999, (await client.QueryAsync(Volume, deadline.Token)).NextUsn);
        Assert.True(await server > 0);
    }

    [Fact]
    public async Task Unresponsive_server_hits_client_deadline_and_next_request_reconnects()
    {
        var options = Options();
        var client = new SourceJournalPipeClient(options, WindowsIdentity.GetCurrent().User!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var pipe = CreateUserOwnedServer(options.PipeName!))
        {
            var pending = client.QueryAsync(Volume, deadline.Token).AsTask();
            await pipe.WaitForConnectionAsync(deadline.Token);
            _ = await SourceJournalPipeProtocol.ReadAsync<SourceJournalPipeProtocol.Request>(pipe, deadline.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(deadline.IsCancellationRequested);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], deadline.Token));
        }
        using var recovered = CreateUserOwnedServer(options.PipeName!);
        var server = ReplyAsync(recovered, deadline.Token);
        Assert.Equal(999, (await client.QueryAsync(Volume, deadline.Token)).NextUsn);
        Assert.True(await server > 0);
    }

    [Fact]
    public async Task Transmitted_deadline_includes_connection_wait()
    {
        var options = Options();
        var client = new SourceJournalPipeClient(options, WindowsIdentity.GetCurrent().User!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var started = DateTimeOffset.UtcNow.UtcTicks;
        var request = client.QueryAsync(Volume, deadline.Token).AsTask();
        await Task.Delay(500, deadline.Token);
        using var pipe = CreateUserOwnedServer(options.PipeName!);
        SourceJournalPipeProtocol.Request? received = null;
        var server = ReplyAsync(pipe, deadline.Token, value => received = value);
        _ = await request;
        Assert.True(await server > 0);
        Assert.NotNull(received);
        Assert.InRange(received.DeadlineUtcTicks, started,
            started + SourceJournalPipeProtocol.Timeout.Ticks + TimeSpan.FromMilliseconds(100).Ticks);
    }

    [Theory]
    [InlineData("not-approved", true)]
    [InlineData(null, false)]
    [InlineData("volume-unavailable", false)]
    public async Task Removed_volume_probe_requires_an_explicit_refusal_from_the_trusted_helper(string? wireError, bool expected)
    {
        const string removed = @"\\?\VOLUME{22222222-2222-2222-2222-222222222222}\";
        var options = Options();
        var client = new SourceJournalPipeClient(options, WindowsIdentity.GetCurrent().User!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = CreateUserOwnedServer(options.PipeName!);
        SourceJournalPipeProtocol.Request? request = null;
        var server = ReplyAsync(pipe, deadline.Token, value => request = value, wireError);
        Assert.Equal(expected, await client.IsVolumeRefusedAsync(removed, deadline.Token));
        Assert.True(await server > 0);
        Assert.NotNull(request);
        Assert.Equal("query", request.Operation);
        Assert.Equal(removed, request.Cursor.VolumeIdentity);
    }
    private static SourceJournalOptions Options() => new()
    {
        Enabled = true,
        PipeName = "FluxKnowledge.SourceJournal.test-" + Guid.NewGuid().ToString("N"),
        ApprovedVolumes = [Volume]
    };

    private static NamedPipeServerStream CreateUserOwnedServer(string name)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(WindowsIdentity.GetCurrent().User!, SourceAccessPipeProtocol.ClientRights,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 65536, security, HandleInheritability.None, 0);
    }

    private static async Task<int> ReplyAsync(NamedPipeServerStream pipe, CancellationToken ct,
        Action<SourceJournalPipeProtocol.Request>? observe = null, string? error = null)
    {
        await pipe.WaitForConnectionAsync(ct);
        var header = new byte[4];
        var first = await pipe.ReadAsync(header.AsMemory(0, 1), ct);
        if (first == 0) return 0;
        await pipe.ReadExactlyAsync(header.AsMemory(1), ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 1, SourceJournalPipeProtocol.MaximumFrameBytes);
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        observe?.Invoke(JsonSerializer.Deserialize<SourceJournalPipeProtocol.Request>(body)!);
        var cursor = new JournalCursor(Volume, 7, 100);
        var response = error is null ? new SourceJournalPipeProtocol.Response(1,
            new(Volume, 7, 0, 999, 0, 33554432, 8388608), new(cursor, cursor with { NextUsn = 999 }, []), null)
            : new(1, null, null, error);
        await SourceJournalPipeProtocol.WriteAsync(pipe, response, ct);
        await pipe.ReadExactlyAsync(new byte[1], ct);
        return length + 4;
    }
}
