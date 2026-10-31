using FluxKnowledge.Application.Documents;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

public sealed partial class DeletedSourceRecoveryIntegrationTests
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_command_does_not_admit_an_unknown_or_denied_presence(bool denied)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        var observations = new ControlledObservations
        {
            PresenceError = denied ? new UnauthorizedAccessException("fixture-denied") : new IOException("fixture-cloud-unavailable")
        };
        await Assert.ThrowsAsync<SourceWorkDeferredException>(() => SourceCommandAdmission.PrepareWithObservationsAsync(
            scenario.Factory, new(source.Id), source.ContentSha256, observations, default).AsTask());
        Assert.Empty(await db.SourceRootWatchStates.ToArrayAsync());
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
    }

    [NativeSqlServerTheory]
    [InlineData("outside")]
    [InlineData("directory")]
    [InlineData("replaced-root")]
    public async Task Exact_command_refuses_invalid_path_or_substituted_root_authority(string change)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        if (change == "replaced-root")
        {
            Directory.Move(scenario.Files, scenario.Files + "-previous");
            Directory.CreateDirectory(scenario.Files);
        }
        else
        {
            var path = change == "directory" ? scenario.Files : Path.Combine(Path.GetDirectoryName(scenario.Files)!, "outside.txt");
            await db.SourceRevisions.Where(x => x.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.CanonicalPath, path));
        }
        await Assert.ThrowsAsync<SourceWorkDeferredException>(() => SourceCommandAdmission.PrepareAsync(scenario.Factory,
            new(source.Id), source.ContentSha256, Path.Combine(scenario.Files, "unused-retained"), default).AsTask());
        Assert.Empty(await db.SourceRootWatchStates.ToArrayAsync());
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_command_respects_covering_pending_debt_without_acknowledging_it(bool wholeRoot)
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        var id = await scenario.PendingAsync(wholeRoot ? "." : "keeper.txt", wholeRoot);
        await Assert.ThrowsAsync<SourceWorkDeferredException>(() => SourceCommandAdmission.PrepareAsync(scenario.Factory,
            new(source.Id), source.ContentSha256, Path.Combine(scenario.Files, "unused-retained"), default).AsTask());
        var pending = await db.SourcePendingChanges.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(1, pending.Generation);
        Assert.Equal(1 | SourcePendingReasons.RemovalPending, pending.ReasonFlags);
    }

    [NativeSqlServerFact]
    public async Task Exact_command_preparation_does_not_sweep_siblings_or_renew_their_pending_generations()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        var otherPending = await scenario.PendingAsync(Path.Combine("removed", "one.txt"), false);
        await using var db = scenario.Factory.CreateDbContext();
        db.SourceRevisions.AddRange(Enumerable.Range(0, 150).Select(i => Synthetic(scenario, $"unrelated-{i}.txt")));
        await db.SaveChangesAsync();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        var gate = await SourceCommandAdmission.PrepareAsync(scenario.Factory, new(source.Id), source.ContentSha256,
            Path.Combine(scenario.Files, "unused-retained"), default);
        Assert.True(gate.IsAdmitted(source.SourceRootId, scenario.Claim.SourceRoot.ConfigurationRevision));
        var pending = await db.SourcePendingChanges.AsNoTracking().SingleAsync(x => x.Id == otherPending);
        Assert.Equal(1, pending.Generation);
        Assert.Equal(1 | SourcePendingReasons.RemovalPending, pending.ReasonFlags);
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
        Assert.False(await db.SourcePendingChanges.AnyAsync(x => x.RelativePath == "."));
    }

    [NativeSqlServerFact]
    public async Task A_missing_exact_command_records_only_its_advisory_delete_and_never_suppresses_siblings()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "removed", "one.txt"));
        File.Delete(source.CanonicalPath);
        await Assert.ThrowsAsync<SourceWorkDeferredException>(() => SourceCommandAdmission.PrepareAsync(scenario.Factory,
            new(source.Id), source.ContentSha256, Path.Combine(scenario.Files, "unused-retained"), default).AsTask());
        Assert.False(await db.SourceRevisions.AnyAsync(x => x.SuppressedAtUtc != null));
        var hint = await db.SourceRootWatchStates.SingleAsync();
        Assert.Contains("one.txt", hint.HintsJson);
        Assert.DoesNotContain("keeper.txt", hint.HintsJson);
        Assert.Empty(await db.SourcePendingChanges.ToArrayAsync());
    }

    [NativeSqlServerFact]
    public async Task Deferred_Visio_preparation_returns_non_success_before_extraction_so_the_desktop_can_continue()
    {
        await using var scenario = await Scenario.CreateAsync(fixture.ConnectionString);
        await using var db = scenario.Factory.CreateDbContext();
        var source = await db.SourceRevisions.SingleAsync(x => x.CanonicalPath == Path.Combine(scenario.Files, "keeper.txt"));
        File.Delete(source.CanonicalPath);
        var retainedRoot = Path.Combine(scenario.Files, "unused-retained");
        Directory.CreateDirectory(retainedRoot);
        using var reader = new SqlRetainedSourceReader(scenario.Factory, retainedRoot);
        using var output = new StringWriter();
        var extractor = new RefuseExtraction();
        var result = await VisioDocumentPipelineRunner.ExecuteAsync(new(new(source.Id), source.ContentSha256,
            VisioDocumentInputProcessor.Capability.ProcessorFingerprint), scenario.Factory, reader, extractor, output, default, retainedRoot);
        Assert.Equal(1, result);
        Assert.Equal(0, extractor.Calls);
        Assert.Contains("source-work-not-admitted", output.ToString());
    }

    private sealed class RefuseExtraction : IVisioDocumentExtractor
    {
        internal int Calls { get; private set; }
        public string? GetUnavailableReason() => null;
        public ValueTask<VisioDocumentResult> ExtractAsync(RetainedSourceBytes retained, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("Extraction must not start before source preparation.");
        }
    }
}
