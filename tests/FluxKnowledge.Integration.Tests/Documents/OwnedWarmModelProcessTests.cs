using System.Diagnostics;
using System.Text;
using FluxKnowledge.Integrations.Documents;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Documents;

public sealed class OwnedWarmModelProcessTests
{
    [Fact]
    public async Task Reuses_the_same_contained_child_with_a_fresh_authorization_per_request()
    {
        var residentId = Guid.NewGuid();
        await using var worker = OwnedWarmModelProcess.Start(Child(), residentId, new string('a', 64));
        var requests = new List<Guid>();
        var first = await RunAsync();
        var second = await RunAsync();
        Assert.Equal(residentId, first.ResidentId);
        Assert.Equal(first.ResidentId, second.ResidentId);
        Assert.Equal(first.Process, second.Process);
        Assert.Equal("ocr", second.Model);
        Assert.NotEqual(first.ExecutionId, second.ExecutionId);
        Assert.Equal(new[] { first.ExecutionId, second.ExecutionId }, requests);
        using var observed = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(observed);
        Assert.True(observed.ActiveProcesses > 0);
        Assert.True(await worker.StopAndConfirmAsync(default));
        Assert.Equal(0u, observed.ActiveProcesses);

        async ValueTask<OwnedWarmModelIdle> RunAsync()
        {
            var id = Guid.NewGuid();
            return await worker.RunAsync(id, @"I:\warm-test\input.json", @"I:\warm-test\output.json",
                DateTimeOffset.UtcNow.AddSeconds(15), (context, _) =>
                {
                    Assert.Equal(residentId, context.ResidentId);
                    Assert.Equal(id, context.ExecutionId);
                    requests.Add(id);
                    return ValueTask.FromResult(true);
                }, default);
        }
    }

    [Fact]
    public async Task Rejects_a_mismatched_acknowledgement_and_proves_the_child_is_empty_before_release()
    {
        var residentId = Guid.NewGuid();
        await using var worker = OwnedWarmModelProcess.Start(Child(staleAcknowledgement: true), residentId, new string('a', 64));
        using var observed = OwnedOcrJob.TryOpen(residentId);
        Assert.NotNull(observed);
        await Assert.ThrowsAsync<InvalidDataException>(() => worker.RunAsync(Guid.NewGuid(),
            @"I:\warm-test\input.json", @"I:\warm-test\output.json", DateTimeOffset.UtcNow.AddSeconds(15),
            (_, _) => ValueTask.FromResult(true), default).AsTask());
        Assert.True(await worker.StopAndConfirmAsync(default));
        Assert.Equal(0u, observed.ActiveProcesses);
    }

    [Fact]
    public async Task Refused_work_does_not_send_a_command_to_the_native_child()
    {
        await using var worker = OwnedWarmModelProcess.Start(Child(), Guid.NewGuid(), new string('a', 64));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.RunAsync(Guid.NewGuid(), @"I:\warm-test\input.json",
            @"I:\warm-test\output.json", deadline, (_, _) => ValueTask.FromResult(false), default).AsTask());
        var allowed = await worker.RunAsync(Guid.NewGuid(), @"I:\warm-test\input.json", @"I:\warm-test\output.json",
            deadline, (_, _) => ValueTask.FromResult(true), default);
        Assert.Equal("ocr", allowed.Model);
    }

    private static ProcessStartInfo Child(bool staleAcknowledgement = false)
    {
        var script = """
            while ($frame = [Console]::ReadLine()) {
                $request = ConvertFrom-Json -InputObject $frame
                if ($request.operation -eq 'shutdown') { exit 0 }
                $id = $request.execution_id
                if ($id -notmatch '^[a-f0-9]{32}$') { exit 95 }
                __CHANGE_ACK__
                [Console]::WriteLine((@{execution_id=$id;status='idle';model='ocr'} | ConvertTo-Json -Compress))
            }
            """.Replace("__CHANGE_ACK__", staleAcknowledgement ? "$id = '00000000000000000000000000000000'" : "");
        var start = new ProcessStartInfo("powershell.exe");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }
}
