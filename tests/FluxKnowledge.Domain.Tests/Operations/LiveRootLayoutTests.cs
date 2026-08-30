using FluxKnowledge.Application.Operations;
using Xunit;

namespace FluxKnowledge.Domain.Tests.Operations;

public sealed class LiveRootLayoutTests
{
    [Fact]
    public void Production_layout_places_every_app_owned_location_beneath_the_exact_live_root()
    {
        var layout = LiveRootLayout.Production;

        Assert.Equal(@"I:\FluxKnowledge", layout.Root);
        Assert.Equal(@"I:\FluxKnowledge\App", layout.ApplicationRoot);
        Assert.Equal(@"I:\FluxKnowledge\Config", layout.ConfigRoot);
        Assert.Equal(@"I:\FluxKnowledge\Data", layout.DataRoot);
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql", layout.SqlRoot);
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql\Data\FluxKnowledge.mdf", layout.SqlDataFilePath);
        Assert.Equal(@"I:\FluxKnowledge\Data\Sql\Log\FluxKnowledge_log.ldf", layout.SqlLogFilePath);
        Assert.Equal(@"I:\FluxKnowledge\Data\Index", layout.IndexRoot);
        Assert.Equal(@"I:\FluxKnowledge\Data\Retained", layout.RetainedRoot);
        Assert.Equal(@"I:\FluxKnowledge\Runtime", layout.RuntimeRoot);
        Assert.Equal(@"I:\FluxKnowledge\Runtime\Spool", layout.SpoolRoot);
        Assert.Equal(@"I:\FluxKnowledge\Runtime\Temp", layout.TempRoot);
        Assert.Equal(@"I:\FluxKnowledge\Runtime\Logs", layout.LogsRoot);
        Assert.Equal(@"I:\FluxKnowledge\CodexPlugin", layout.CodexPluginRoot);
        Assert.Equal(@"I:\FluxKnowledge\Recovery", layout.RecoveryRoot);
        Assert.All(
            layout.AppOwnedLocations,
            location => Assert.StartsWith(layout.Root + @"\", location, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"I:\FluxKnowledge\Data\..\Config")]
    [InlineData(@"I:\Foreign\escape.txt")]
    [InlineData(@"..\FluxKnowledge\escape.txt")]
    public void Traversal_root_escape_and_relative_paths_fail_before_path_inspection(string candidate)
    {
        var inspector = new RecordingPathInspector();

        var result = LiveRootLayout.Production.ValidateOwnedPath(candidate, inspector);

        Assert.False(result.IsValid);
        Assert.Equal(0, inspector.Inspections);
    }

    [Fact]
    public void A_reparse_point_that_resolves_outside_the_root_is_rejected()
    {
        var layout = LiveRootLayout.Production;
        var inspector = new RecordingPathInspector(new Dictionary<string, LiveRootPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [@"I:\FluxKnowledge\Data"] = new(true, true, @"C:\foreign-data")
        });

        var result = layout.ValidateOwnedPath(layout.IndexRoot, inspector);

        Assert.False(result.IsValid);
        Assert.Equal("reparse-point-not-allowed", result.Reason);
    }

    [Fact]
    public void Storage_safety_requires_the_canonical_live_root_to_exist_before_IO()
    {
        var layout = LiveRootLayout.Production;
        var inspector = new RecordingPathInspector(new Dictionary<string, LiveRootPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [layout.Root] = new(false, false, null)
        });
        var safety = new LiveRootStorageSafety(layout, inspector);

        var exception = Assert.Throws<InvalidOperationException>(() => safety.ValidateBeforeIo(layout.IndexRoot));

        Assert.Contains("live root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Storage_safety_rejects_a_live_root_that_disappears_during_validation()
    {
        var layout = LiveRootLayout.Production;
        var safety = new LiveRootStorageSafety(layout, new DisappearingRootInspector(layout.Root));

        Assert.Throws<InvalidOperationException>(() => safety.ValidateBeforeIo(layout.IndexRoot));
    }

    [Theory]
    [InlineData(true, @"I:\FluxKnowledge\Data")]
    [InlineData(false, @"I:\FluxKnowledge\Data")]
    public void Storage_safety_rejects_reparse_and_foreign_existing_ancestors_before_IO(
        bool isReparsePoint,
        string unsafeAncestor)
    {
        var layout = LiveRootLayout.Production;
        var inspector = new RecordingPathInspector(new Dictionary<string, LiveRootPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [unsafeAncestor] = isReparsePoint
                ? new(true, true, unsafeAncestor)
                : new(true, false, @"C:\foreign-live-root")
        });
        var safety = new LiveRootStorageSafety(layout, inspector);

        Assert.Throws<InvalidOperationException>(() => safety.ValidateBeforeIo(layout.IndexRoot));
    }

    private sealed class RecordingPathInspector(
        IReadOnlyDictionary<string, LiveRootPathInspection>? inspections = null) : ILiveRootPathInspector
    {
        public int Inspections { get; private set; }

        public LiveRootPathInspection Inspect(string path)
        {
            Inspections++;
            return inspections?.TryGetValue(path, out var result) == true
                ? result
                : new LiveRootPathInspection(true, false, path);
        }
    }

    private sealed class DisappearingRootInspector(string root) : ILiveRootPathInspector
    {
        private int _rootInspections;

        public LiveRootPathInspection Inspect(string path)
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref _rootInspections) > 1)
            {
                return new(false, false, null);
            }

            return new(true, false, path);
        }
    }
}
