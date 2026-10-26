using FluxKnowledge.Domain.Common;
using FluxKnowledge.Domain.Sources;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Sources;

public sealed class SourceWatchHintTests
{
    [Fact]
    public void Rename_endpoints_and_other_paths_survive_duplicate_signals()
    {
        var root = SourceRootId.New();
        var hints = new SourceWatchHintAccumulator(root);
        hints.Add(new(root, SourceWatchSignalKind.Renamed, DateTimeOffset.UnixEpoch,
            [new("old/file.txt"), new("new/file.txt")]));
        hints.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(1),
            [new("other.txt"), new("new/file.txt", true)]));

        var snapshot = hints.Snapshot();
        Assert.Equal(new[] { Path.Combine("new", "file.txt"), Path.Combine("old", "file.txt"), "other.txt" },
            snapshot.Paths!.Select(x => x.RelativePath));
        Assert.True(snapshot.Paths![0].IsSubtree);
        Assert.False(snapshot.PathsIncomplete);
    }

    [Fact]
    public void Coalesced_batch_keeps_the_earliest_time_for_maximum_debounce()
    {
        var root = SourceRootId.New();
        var hints = new SourceWatchHintAccumulator(root);
        hints.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch, [new("one.txt")]));
        hints.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(29), [new("one.txt")]));

        var signal = hints.Snapshot();
        var state = SourceWatchState.Empty(root).Observe(signal, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        Assert.Equal(DateTimeOffset.UnixEpoch, signal.FirstObservedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(29), signal.ObservedAtUtc);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), state.DueAtUtc);
    }

    [Fact]
    public void Capacity_exhaustion_keeps_a_bounded_recovery_marker_and_control_hint()
    {
        var root = SourceRootId.New();
        var hints = new SourceWatchHintAccumulator(root, maximumPaths: 2);
        hints.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch, [new("one.txt"), new("two.txt")]));
        hints.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch.AddSeconds(1),
            [new("three.txt")], RepositoryControlChanged: true));

        var snapshot = hints.Snapshot();
        Assert.Equal(2, snapshot.Paths!.Count);
        Assert.True(snapshot.PathsIncomplete);
        Assert.True(snapshot.RepositoryControlChanged);
        Assert.Equal(SourceWatchSignalKind.Overflow, snapshot.Kind);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("I:/outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("\\outside.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("bad\0name")]
    public void Untrusted_paths_become_recovery_hints_without_retaining_the_path(string path)
    {
        var root = SourceRootId.New();
        var hints = new SourceWatchHintAccumulator(root);
        hints.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch, [new(path)]));
        var snapshot = hints.Snapshot();
        Assert.Empty(snapshot.Paths!);
        Assert.True(snapshot.PathsIncomplete);
        Assert.Equal(SourceWatchSignalKind.Overflow, snapshot.Kind);
    }

    [Fact]
    public void Legacy_unknown_paths_remain_uncertain_but_control_only_changes_need_no_source_path()
    {
        var root = SourceRootId.New();
        var legacy = new SourceWatchHintAccumulator(root);
        legacy.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch));
        Assert.True(legacy.Snapshot().PathsIncomplete);
        var control = new SourceWatchHintAccumulator(root);
        control.Add(new(root, SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch, [], RepositoryControlChanged: true));
        Assert.True(control.Snapshot().RepositoryControlChanged);
        Assert.False(control.Snapshot().PathsIncomplete);
    }

    [Fact]
    public void A_different_root_cannot_be_folded_into_the_same_hint_batch()
    {
        var hints = new SourceWatchHintAccumulator(SourceRootId.New());
        Assert.Throws<DomainInvariantException>(() => hints.Add(new(SourceRootId.New(), SourceWatchSignalKind.Changed, DateTimeOffset.UnixEpoch)));
    }
}
