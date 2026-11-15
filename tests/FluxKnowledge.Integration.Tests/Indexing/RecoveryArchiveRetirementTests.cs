using System.Security.Cryptography;
using FluxKnowledge.Application.Gpu;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Indexing;

public sealed class RecoveryArchiveRetirementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FluxRecoveryRetirement_" + Guid.NewGuid().ToString("N"));
    private readonly RecoveryArchiveRetirementPlan _plan;

    public RecoveryArchiveRetirementTests()
    {
        Directory.CreateDirectory(_root);
        var recovery = Path.Combine(_root, "Recovery");
        var old = Path.Combine(recovery, "IncrementalUpdates", "20261022T211724Z-cbb3ec8eacf3");
        var current = Path.Combine(recovery, "IncrementalUpdates", "20261115T121345Z-64e7e17c1dc0");
        var app = Path.Combine(_root, "App");
        Directory.CreateDirectory(old); Directory.CreateDirectory(current); Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(old, "old.json"), "{}");
        File.WriteAllText(Path.Combine(current, "manifest.json"), "{}");
        var entry = Path.Combine(app, "FluxKnowledge.Web.dll"); File.WriteAllText(entry, "synthetic, never executed");
        var candidate = Path.Combine(current, "candidate"); Directory.CreateDirectory(candidate);
        File.Copy(entry, Path.Combine(candidate, "FluxKnowledge.Web.dll"));
        Directory.CreateDirectory(Path.Combine(_root, "Index")); Directory.CreateDirectory(Path.Combine(_root, "Quarantine"));
        _plan = new(1, Guid.NewGuid(), Guid.NewGuid(), recovery, Path.Combine(_root, "Index"), entry,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry))),
            Path.GetRelativePath(recovery, Path.Combine(candidate, "FluxKnowledge.Web.dll")),
            Path.Combine(_root, "Quarantine"), PhysicalFileIdentity.GetDirectory(recovery).IdentityFingerprint,
            Guid.NewGuid(), 1, null, null, null, [new(222, DateTimeOffset.UtcNow, new('a', 64))], [],
            [new(Path.GetRelativePath(recovery, old), PhysicalFileIdentity.GetDirectory(old).IdentityFingerprint)],
            [Path.GetRelativePath(recovery, current)]);
    }

    [Fact]
    public async Task Exact_moves_preserve_the_running_release_and_resume_without_copying_archive_bytes()
    {
        var before = PhysicalFileIdentity.GetDirectory(Path.Combine(_plan.RecoveryRoot, _plan.Moves[0].RelativePath));
        await RecoveryArchiveRetirement.MoveAsync(_plan, () => ValueTask.CompletedTask, CancellationToken.None);
        await RecoveryArchiveRetirement.MoveAsync(_plan, () => ValueTask.CompletedTask, CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(_plan.RecoveryRoot, _plan.Moves[0].RelativePath)));
        Assert.Equal(before.IdentityFingerprint, PhysicalFileIdentity.GetDirectory(
            Path.Combine(_plan.QuarantineRoot, _plan.Moves[0].RelativePath)).IdentityFingerprint);
        Assert.True(File.Exists(_plan.ApplicationEntry));
        Assert.True(File.Exists(Path.Combine(_plan.RecoveryRoot, _plan.RetainedApplicationEntry)));
    }

    [Theory]
    [InlineData("changed-app")]
    [InlineData("unplanned-packet")]
    [InlineData("both-copies")]
    [InlineData("neither-copy")]
    [InlineData("retire-current")]
    [InlineData("escape")]
    [InlineData("overlapping-quarantine")]
    public async Task Drift_and_unsafe_dispositions_are_refused_before_a_move(string fault)
    {
        var plan = _plan;
        var source = Path.Combine(plan.RecoveryRoot, plan.Moves[0].RelativePath);
        if (fault == "changed-app") File.AppendAllText(plan.ApplicationEntry, "changed");
        if (fault == "unplanned-packet") Directory.CreateDirectory(Path.Combine(plan.RecoveryRoot, "unexpected"));
        if (fault == "both-copies") Directory.CreateDirectory(Path.Combine(plan.QuarantineRoot, plan.Moves[0].RelativePath));
        if (fault == "neither-copy") Directory.Delete(source, true);
        if (fault == "retire-current") plan = plan with { RetainedPackets = [], Moves = [.. plan.Moves, new(plan.RetainedPackets[0], "unknown")] };
        if (fault == "escape") plan = plan with { Moves = [new("../outside", "unknown")] };
        if (fault == "overlapping-quarantine") plan = plan with { QuarantineRoot = Path.Combine(plan.RecoveryRoot, "quarantine") };
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await RecoveryArchiveRetirement.MoveAsync(plan, () => ValueTask.CompletedTask, CancellationToken.None));
        if (fault != "neither-copy") Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task Session_loss_before_rename_leaves_the_original_archive_in_place()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoveryArchiveRetirement.MoveAsync(
            _plan, () => throw new InvalidOperationException("session-lost"), CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(_plan.RecoveryRoot, _plan.Moves[0].RelativePath)));
    }

    [Fact]
    public async Task Session_loss_after_rename_resumes_from_the_durable_intent_and_exact_directory_identity()
    {
        var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoveryArchiveRetirement.MoveAsync(_plan,
            () => ++checks == 3 ? throw new InvalidOperationException("session-lost") : ValueTask.CompletedTask, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(_plan.RecoveryRoot, _plan.Moves[0].RelativePath)));
        await RecoveryArchiveRetirement.MoveAsync(_plan, () => ValueTask.CompletedTask, CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(_plan.QuarantineRoot, _plan.Moves[0].RelativePath)));
    }

    [Fact]
    public async Task Optional_current_rollback_bytes_can_be_quarantined_last_after_the_complete_live_payload_is_verified()
    {
        var plan = WithCurrentRollbackRetired();
        await RecoveryArchiveRetirement.MoveAsync(plan, () => ValueTask.CompletedTask, default);
        Assert.Equal(0, new RecoveryIndexReferenceAudit(plan.IndexRoot).Capture(plan.RecoveryRoot).PacketCount);
        Assert.True(File.Exists(plan.ApplicationEntry));
        Assert.True(File.Exists(Path.Combine(plan.QuarantineRoot, plan.RetainedApplicationEntry)));
    }

    [Fact]
    public async Task Runtime_drift_stops_the_last_rollback_rename_while_preserving_the_move_journal()
    {
        var plan = WithCurrentRollbackRetired(); var checks = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoveryArchiveRetirement.MoveAsync(plan, () =>
        {
            if (++checks == 4) File.WriteAllText(Path.Combine(Path.GetDirectoryName(plan.ApplicationEntry)!, "changed.dll"), "drift");
            return ValueTask.CompletedTask;
        }, default));
        Assert.True(File.Exists(Path.Combine(plan.RecoveryRoot, plan.RetainedApplicationEntry)));
        Assert.True(Directory.Exists(Path.Combine(plan.QuarantineRoot, plan.Moves[0].RelativePath)));
    }

    [Fact]
    public async Task A_deployment_hold_refuses_retirement_before_any_archive_is_moved()
    {
        var hold = Path.Combine(_root, "deployment-validation-hold.json"); File.WriteAllText(hold, "{}");
        var plan = _plan with { RequiredAbsentPaths = [hold] };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoveryArchiveRetirement.MoveAsync(plan, () => ValueTask.CompletedTask, default));
        Assert.True(Directory.Exists(Path.Combine(plan.RecoveryRoot, plan.Moves[0].RelativePath)));
    }

    [Fact]
    public async Task Retention_snapshots_cannot_be_retired_as_application_archives()
    {
        var retention = Path.Combine(_plan.RecoveryRoot, "IndexRetention"); Directory.CreateDirectory(retention);
        var plan = _plan with { Moves = [.. _plan.Moves, new("IndexRetention", PhysicalFileIdentity.GetDirectory(retention).IdentityFingerprint)] };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await RecoveryArchiveRetirement.MoveAsync(plan, () => ValueTask.CompletedTask, default));
        Assert.True(Directory.Exists(Path.Combine(plan.RecoveryRoot, plan.Moves[0].RelativePath)));
        Assert.True(Directory.Exists(retention));
    }

    private RecoveryArchiveRetirementPlan WithCurrentRollbackRetired()
    {
        var packet = _plan.RetainedPackets.Single();
        var app = Path.GetDirectoryName(_plan.ApplicationEntry)!;
        return _plan with { RetainedPackets = [], Moves = [.. _plan.Moves,
            new(packet, PhysicalFileIdentity.GetDirectory(Path.Combine(_plan.RecoveryRoot, packet)).IdentityFingerprint)],
            RuntimeSnapshots = [new(app, [new(Path.GetFileName(_plan.ApplicationEntry), new FileInfo(_plan.ApplicationEntry).Length, _plan.ApplicationSha256)])] };
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
