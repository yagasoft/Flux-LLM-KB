using FluxKnowledge.Application.Operations;
using FluxKnowledge.Domain.Outlook;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Outlook;

public sealed class SqlOutlookSpoolRootPreflightTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Disabled_stale_external_profile_fails_closed_without_inspecting_the_external_root()
    {
        await ClearAsync();
        var layout = LiveRootLayout.CreateForIsolatedTests(
            Path.Combine(Path.GetTempPath(), $"flux-outlook-policy-{Guid.NewGuid():N}"));
        var externalRoot = Path.Combine(Path.GetTempPath(), $"flux-outlook-external-tripwire-{Guid.NewGuid():N}");
        await SeedProfileAsync(externalRoot, enabled: false);
        var inspector = new RecordingInspector(externalRoot);
        var preflight = CreatePreflight(layout, inspector);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            preflight.ValidateAsync(CancellationToken.None).AsTask());

        Assert.Contains("canonical safe spool root", failure.Message, StringComparison.Ordinal);
        Assert.Empty(inspector.Paths);
        Assert.DoesNotContain(externalRoot, failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [NativeSqlServerFact]
    public async Task Exact_canonical_profile_passes_after_every_existing_ancestor_is_inspected_no_follow()
    {
        await ClearAsync();
        var layout = LiveRootLayout.CreateForIsolatedTests(
            Path.Combine(Path.GetTempPath(), $"flux-outlook-policy-{Guid.NewGuid():N}"));
        await SeedProfileAsync(layout.SpoolRoot, enabled: true);
        var inspector = new RecordingInspector();

        await CreatePreflight(layout, inspector).ValidateAsync(CancellationToken.None);

        Assert.Equal(layout.Root, inspector.Paths[0], ignoreCase: true);
        Assert.Equal(layout.SpoolRoot, inspector.Paths[^1], ignoreCase: true);
        Assert.All(inspector.Paths, path => Assert.StartsWith(layout.Root, path, StringComparison.OrdinalIgnoreCase));
    }

    [NativeSqlServerFact]
    public async Task Canonical_profile_with_a_reparse_ancestor_fails_before_spool_use()
    {
        await ClearAsync();
        var layout = LiveRootLayout.CreateForIsolatedTests(
            Path.Combine(Path.GetTempPath(), $"flux-outlook-policy-{Guid.NewGuid():N}"));
        await SeedProfileAsync(layout.SpoolRoot, enabled: false);
        var reparseAncestor = layout.RuntimeRoot;
        var inspector = new RecordingInspector(reparsePath: reparseAncestor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreatePreflight(layout, inspector).ValidateAsync(CancellationToken.None).AsTask());

        Assert.Contains(reparseAncestor, inspector.Paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(layout.SpoolRoot, inspector.Paths, StringComparer.OrdinalIgnoreCase);
    }

    private SqlOutlookSpoolRootPreflight CreatePreflight(
        LiveRootLayout layout,
        ILiveRootPathInspector inspector) =>
        new(
            SqlTestData.CreateFactory(_fixture),
            new PersistedOutlookSpoolRootPolicy(layout, new LiveRootStorageSafety(layout, inspector)));

    private async Task SeedProfileAsync(string spoolRoot, bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        var sourceRootId = Guid.NewGuid();
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
        {
            Id = sourceRootId,
            CanonicalPath = $"C:\\outlook-preflight-tests\\{sourceRootId:N}",
            DisplayName = "Outlook preflight test",
            State = (int)SourceRootState.Paused,
            Recursive = false,
            IncludePatternsJson = "[]",
            ExcludePatternsJson = "[]",
            FollowLinks = false,
            MaximumFileBytes = 1024,
            AllowedClassificationsJson = "[]",
            CrawlMode = 0,
            ReconciliationCadenceSeconds = 900,
            ConfigurationRevision = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity
        {
            Id = Guid.NewGuid(),
            SourceRootId = sourceRootId,
            DisplayName = "Persisted profile",
            SpoolRoot = spoolRoot,
            IncrementalBasis = (int)OutlookIncrementalBasis.LastModificationTime,
            State = enabled ? (int)OutlookCaptureState.Ready : (int)OutlookCaptureState.Disabled,
            IsEnabled = enabled,
            ConfigurationRevision = 1,
            CadenceTicks = TimeSpan.FromMinutes(15).Ticks,
            MaximumOverlapTicks = TimeSpan.FromMinutes(5).Ticks,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        await context.SaveChangesAsync();
    }

    private async Task ClearAsync()
    {
        await using var context = await SqlTestData.CreateFactory(_fixture).CreateDbContextAsync();
        await context.OutlookCaptureProfiles.ExecuteDeleteAsync();
        await context.SourceRootConfigurations.ExecuteDeleteAsync();
    }

    private sealed class RecordingInspector(
        string? forbiddenPrefix = null,
        string? reparsePath = null) : ILiveRootPathInspector
    {
        public List<string> Paths { get; } = [];

        public LiveRootPathInspection Inspect(string path)
        {
            if (forbiddenPrefix is not null && path.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The external tripwire was inspected.");
            }

            Paths.Add(path);
            return string.Equals(path, reparsePath, StringComparison.OrdinalIgnoreCase)
                ? new LiveRootPathInspection(true, true, Path.Combine(Path.GetTempPath(), "outside"))
                : new LiveRootPathInspection(true, false, path);
        }
    }
}
