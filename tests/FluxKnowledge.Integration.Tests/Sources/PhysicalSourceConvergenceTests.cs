using System.Text.Json;
using System.Reflection;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.IntegrationV1;
using FluxKnowledge.Application.IntegrationV1.Corpus;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Search;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Sources;

[Collection("sql-full-text")]
public sealed class PhysicalSourceConvergenceTests(NativeSqlServerFixture fixture)
    : IClassFixture<NativeSqlServerFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => SqlTestData.ClearPhase3SourceDataAsync(fixture);
    public Task DisposeAsync() => Task.CompletedTask;

    [NativeSqlServerFact]
    public async Task An_ordinary_file_under_a_follow_links_root_still_ingests_without_authorising_history_suppression()
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString, followLinks: true);
        var original = await source.CaptureAsync("available.txt");
        await File.WriteAllTextAsync(source.Path("available.txt"), "Changed ordinary file.");
        var current = await source.CaptureAsync("available.txt");
        Assert.NotEqual(original, current);
        await using var verify = source.Factory.CreateDbContext();
        var revisions = await verify.SourceRevisions.ToArrayAsync();
        Assert.Equal(2, revisions.Length);
        Assert.All(revisions, value => Assert.Null(value.SuppressedAtUtc));
        Assert.Equal(2, await verify.SourceArtifacts.CountAsync());
    }

    [NativeSqlServerTheory]
    [InlineData("edit")]
    [InlineData("replacement")]
    [InlineData("rename")]
    [InlineData("reversion")]
    public async Task A_verified_file_capture_resolves_only_its_own_history_despite_an_unavailable_sibling(string change)
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var original = await source.CaptureAsync("available.txt");
        var unrelated = await source.CaptureAsync("unavailable.txt");
        source.Files.MakeCloudOnlyFile(source.Path("unavailable.txt"));
        var currentName = "available.txt";
        if (change == "edit") await File.WriteAllTextAsync(source.Path(currentName), "Revised available evidence.");
        else if (change == "replacement")
        {
            File.Move(source.Path(currentName), System.IO.Path.Combine(source.Storage.Root, "previous-source.txt"));
            await File.WriteAllTextAsync(source.Path(currentName), "Replacement available evidence.");
        }
        else
        {
            currentName = "renamed.txt";
            File.Move(source.Path("available.txt"), source.Path(currentName));
        }
        var successor = await source.CaptureAsync(currentName);
        if (change == "reversion")
        {
            File.Move(source.Path(currentName), source.Path("available.txt"));
            successor = await source.CaptureAsync("available.txt");
            Assert.Equal(original, successor);
        }

        await using var verify = source.Factory.CreateDbContext();
        var rows = await verify.SourceRevisions.Where(value => value.SourceRootId == source.Claim.SourceRoot.Id.Value).ToArrayAsync();
        var current = Assert.Single(rows, value => value.Id != unrelated.Value && value.SuppressedAtUtc is null);
        Assert.Equal(successor.Value, current.Id);
        Assert.Null(rows.Single(value => value.Id == unrelated.Value).SuppressedAtUtc);
        Assert.All(rows.Where(value => value.Id != successor.Value && value.Id != unrelated.Value), value => Assert.NotNull(value.SuppressedAtUtc));
        Assert.Equal(rows.Length, await verify.SourceArtifacts.CountAsync(value => rows.Select(row => row.Id).Contains(value.SourceRevisionId)));
        Assert.True((await verify.SourceScanRequests.SingleAsync(value => value.Id == source.Claim.ScanRequest.Id.Value)).ErrorFileCount > 0);
        Assert.Equal((int)SourceScanRequestState.Running,
            (await verify.SourceScanRequests.SingleAsync(value => value.Id == source.Claim.ScanRequest.Id.Value)).State);
    }

    [NativeSqlServerTheory]
    [InlineData("configuration")]
    [InlineData("follow-links")]
    [InlineData("paused")]
    [InlineData("scan-generation")]
    [InlineData("expired-scan")]
    public async Task An_owned_capture_cannot_change_bindings_after_its_root_or_scan_authority_changes(string change)
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var original = await source.CaptureAsync("available.txt");
        await File.WriteAllTextAsync(source.Path("available.txt"), "Changed capture.");
        var snapshot = await source.SnapshotAsync("available.txt");
        var receipt = await source.Artifacts.PutFileAsync(snapshot, new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default);
        try
        {
            await using (var mutate = source.Factory.CreateDbContext())
            {
                if (change is "configuration" or "paused" or "follow-links")
                {
                    var root = await mutate.SourceRootConfigurations.SingleAsync();
                    if (change == "configuration") root.ConfigurationRevision++;
                    else if (change == "follow-links") root.FollowLinks = true;
                    else root.State = (int)SourceRootState.Paused;
                }
                else
                {
                    var job = await mutate.SourceScanJobs.SingleAsync(value => value.Id == source.Claim.ControlJobId);
                    if (change == "scan-generation") job.LeaseGeneration++;
                    else job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                await mutate.SaveChangesAsync();
            }
            await Assert.ThrowsAnyAsync<InvalidDataException>(() => source.Scans.ConvergeRevisionAndArtifactAsync(
                source.Claim.SourceRoot, snapshot, receipt, default).AsTask());
        }
        finally { await receipt.PublicationLease!.DisposeAsync(); }
        await using var verify = source.Factory.CreateDbContext();
        var only = Assert.Single(await verify.SourceRevisions.ToArrayAsync());
        Assert.Equal(original.Value, only.Id);
        Assert.Null(only.SuppressedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task Immutable_bytes_without_a_live_source_capture_do_not_authorise_suppression()
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var original = await source.CaptureAsync("available.txt");
        await File.WriteAllTextAsync(source.Path("available.txt"), "Unowned revised evidence.");
        var snapshot = await source.SnapshotAsync("available.txt");
        var receipt = await source.Artifacts.PutAsync(snapshot.ClassificationBuffer,
            new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default);
        try { await source.Scans.ConvergeRevisionAndArtifactAsync(source.Claim.SourceRoot, snapshot, receipt, default); }
        finally { if (receipt.PublicationLease is not null) await receipt.PublicationLease.DisposeAsync(); }
        await using var verify = source.Factory.CreateDbContext();
        Assert.Null((await verify.SourceRevisions.SingleAsync(value => value.Id == original.Value)).SuppressedAtUtc);
        Assert.Equal(2, await verify.SourceRevisions.CountAsync(value => value.SuppressedAtUtc == null));
    }

    [NativeSqlServerFact]
    public async Task An_identical_byte_replacement_is_not_relabelled_as_the_old_physical_identity()
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var original = await source.CaptureAsync("available.txt");
        var bytes = await File.ReadAllBytesAsync(source.Path("available.txt"));
        File.Move(source.Path("available.txt"), System.IO.Path.Combine(source.Storage.Root, "previous-source.txt"));
        await File.WriteAllBytesAsync(source.Path("available.txt"), bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CaptureAsync("available.txt"));
        await using var verify = source.Factory.CreateDbContext();
        var row = Assert.Single(await verify.SourceRevisions.ToArrayAsync());
        Assert.Equal(original.Value, row.Id);
        Assert.NotEqual(row.StableSourceIdentity, PhysicalFileIdentity.Get(source.Path("available.txt")));
        Assert.Null(row.SuppressedAtUtc);
    }

    [NativeSqlServerFact]
    public async Task A_different_identity_with_conflicting_path_casing_prevents_positive_supersession()
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var original = await source.CaptureAsync("available.txt");
        await using (var setup = source.Factory.CreateDbContext())
        {
            setup.SourceRevisions.Add(new SourceRevisionEntity
            {
                Id = Guid.NewGuid(), SourceRootId = source.Claim.SourceRoot.Id.Value,
                CanonicalPath = source.Path("AVAILABLE.txt"), StableSourceIdentity = "historical:other-physical-file", Revision = 1,
                ContentSha256 = new string('c', 64), ByteLength = 4, Extension = ".txt", Classification = "AcceptedUtf8Text",
                DiscoveredAtUtc = DateTimeOffset.UtcNow
            });
            await setup.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(source.Path("available.txt"), "Revised available evidence.");
        await Assert.ThrowsAsync<InvalidDataException>(() => source.CaptureAsync("available.txt"));
        await using var verify = source.Factory.CreateDbContext();
        var preserved = await verify.SourceRevisions.ToArrayAsync();
        Assert.Equal(2, preserved.Length);
        Assert.All(preserved, value => Assert.Null(value.SuppressedAtUtc));
        Assert.Contains(preserved, value => value.Id == original.Value);
    }

    [NativeSqlServerFact]
    public async Task A_forged_derived_row_cannot_be_adopted_as_a_physical_file()
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var snapshot = await source.SnapshotAsync("available.txt");
        var parentId = Guid.NewGuid(); var childId = Guid.NewGuid();
        await using (var setup = source.Factory.CreateDbContext())
        {
            setup.SourceRevisions.AddRange(
                new SourceRevisionEntity
                {
                    Id = parentId, SourceRootId = source.Claim.SourceRoot.Id.Value,
                    CanonicalPath = source.Path("historical-parent.txt"), StableSourceIdentity = "historical:parent", Revision = 1,
                    ContentSha256 = new string('d', 64), ByteLength = 4, Extension = ".txt", Classification = "AcceptedUtf8Text",
                    DiscoveredAtUtc = DateTimeOffset.UtcNow
                },
                new SourceRevisionEntity
                {
                    Id = childId, SourceRootId = source.Claim.SourceRoot.Id.Value, ParentSourceRevisionId = parentId, OriginKind = 2,
                    CanonicalPath = snapshot.CanonicalPath, StableSourceIdentity = snapshot.StableSourceIdentity, Revision = 1,
                    ContentSha256 = snapshot.ContentSha256, ByteLength = snapshot.ByteLength, Extension = ".txt", Classification = "AcceptedUtf8Text",
                    DiscoveredAtUtc = DateTimeOffset.UtcNow
                });
            await setup.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => source.CaptureAsync("available.txt"));
        await using var verify = source.Factory.CreateDbContext();
        Assert.Equal(2, await verify.SourceRevisions.CountAsync());
        var child = await verify.SourceRevisions.SingleAsync(value => value.Id == childId);
        Assert.Equal(2, child.OriginKind);
        Assert.Equal(parentId, child.ParentSourceRevisionId);
        Assert.Empty(await verify.SourceArtifacts.ToArrayAsync());
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_mutation_after_SQL_save_rolls_back_selection_and_is_rechecked_on_retry(bool retry)
    {
        await using var source = await Source.CreateAsync(fixture.ConnectionString);
        var original = await source.CaptureAsync("available.txt");
        await File.WriteAllTextAsync(source.Path("available.txt"), "Revised available evidence.");
        var snapshot = await source.SnapshotAsync("available.txt");
        long beforeVersion;
        await using (var before = source.Factory.CreateDbContext()) beforeVersion = (await before.IndexState.SingleAsync()).CorpusVersion;
        var afterSave = new AfterSave(() => File.SetLastWriteTimeUtc(snapshot.CanonicalPath, snapshot.LastWriteAtUtc.AddSeconds(1).UtcDateTime), retry);
        var factory = new Factory(fixture.ConnectionString, afterSave);
        var receipt = await source.Artifacts.PutFileAsync(snapshot, new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default);
        try
        {
            await Assert.ThrowsAsync<SourceSnapshotChangedException>(() => new SqlSourceScanStore(factory, TimeProvider.System)
                .ConvergeRevisionAndArtifactAsync(source.Claim.SourceRoot, snapshot, receipt, default).AsTask());
        }
        finally { await receipt.PublicationLease!.DisposeAsync(); }
        Assert.Equal(1, afterSave.Saves);
        if (retry) Assert.True(factory.ContextsCreated >= 4, "The fresh-context convergence attempt must rerun after the injected SQL race.");
        await using var verify = source.Factory.CreateDbContext();
        var only = Assert.Single(await verify.SourceRevisions.ToArrayAsync());
        Assert.Equal(original.Value, only.Id);
        Assert.Null(only.SuppressedAtUtc);
        Assert.Equal(beforeVersion, (await verify.IndexState.SingleAsync()).CorpusVersion);
        Assert.Single(await verify.SourceArtifacts.ToArrayAsync());
    }

    private sealed class Source : IAsyncDisposable
    {
        public CloudSourceFixture Files { get; } = new();
        public CloudSourceFixture Storage { get; } = new();
        public Factory Factory { get; }
        public SqlSourceScanStore Scans { get; }
        public ContentAddressedSourceArtifactStore Artifacts { get; }
        public ClaimedSourceScan Claim { get; private set; } = null!;
        public string Path(string name) => System.IO.Path.Combine(Files.Root, name);

        private Source(string connectionString)
        {
            Factory = new(connectionString);
            Scans = new(Factory, TimeProvider.System);
            Artifacts = new(Storage.Root);
            File.WriteAllText(Path("available.txt"), "Original available evidence.");
            File.WriteAllText(Path("unavailable.txt"), "Unrelated retained evidence.");
        }

        public static async Task<Source> CreateAsync(string connectionString, bool followLinks = false)
        {
            var source = new Source(connectionString);
            try
            {
                var commands = new NativeCorpusCommandService(
                    new NativeOperationService(new SqlNativeOperationStore(source.Factory, TimeProvider.System), []),
                    new SqlNativeCorpusActionStore(source.Factory,
                        new SourceRootPathPolicy(new LocalIngressOptions([source.Files.Root])), new LocalPrivateContentDisclosure()));
                var mutation = new NativeCorpusMutation("root_create", JsonSerializer.SerializeToElement(new
                { path = source.Files.Root, displayName = "Positive physical capture", recursive = false, followLinks, maximumFileBytes = 1024 }));
                var preview = await commands.PreviewAsync(mutation, "test", default);
                await commands.CommitAsync(mutation, preview.ConfirmationId, $"physical-capture:{Guid.NewGuid():N}", "test", default);
                source.Claim = Assert.IsType<ClaimedSourceScan>(await source.Scans.ClaimNextReleasedAsync(
                    "physical-capture-test", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default));
                return source;
            }
            catch { await source.DisposeAsync(); throw; }
        }

        public async Task<SourceDiscoveredFile> SnapshotAsync(string name)
        {
            var enumerator = new LocalSourceEnumerator();
            var files = new List<SourceDiscoveredFile>();
            await foreach (var file in enumerator.EnumerateAsync(Claim.SourceRoot, default)) files.Add(file);
            await Scans.RecordEnumerationEvidenceAsync(Claim.ScanRequest.Id, enumerator.LastEvidence, default);
            return Assert.Single(files, value => value.RelativePath == name) with
            { ScanOwnership = new(Claim.ScanRequest.Id, Claim.ScanRequest.Lease!) };
        }

        public async Task<SourceRevisionId> CaptureAsync(string name)
        {
            var snapshot = await SnapshotAsync(name);
            var receipt = await Artifacts.PutFileAsync(snapshot, new(snapshot.ContentSha256, "text/plain", snapshot.ByteLength), default);
            try { return await Scans.ConvergeRevisionAndArtifactAsync(Claim.SourceRoot, snapshot, receipt, default); }
            finally { if (receipt.PublicationLease is not null) await receipt.PublicationLease.DisposeAsync(); }
        }

        public ValueTask DisposeAsync()
        {
            Artifacts.Dispose(); Files.Dispose(); Storage.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AfterSave(Action mutation, bool retry) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Saves == 1)
            {
                mutation();
                if (retry) throw SqlRace();
            }
            return ValueTask.FromResult(result);
        }

        private static SqlException SqlRace()
        {
            var error = (SqlError)Activator.CreateInstance(typeof(SqlError), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [1205, (byte)0, (byte)14, "server", "test", string.Empty, 1, 0, null], culture: null)!;
            var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: null, culture: null)!;
            typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(errors, [error]);
            return (SqlException)typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2).Invoke(null, [errors, "server"])!;
        }
    }

    private sealed class Factory(string connectionString, params IInterceptor[] interceptors) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public int ContextsCreated { get; private set; }
        public FluxKnowledgeDbContext CreateDbContext()
        {
            ContextsCreated++;
            return new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).AddInterceptors(interceptors).Options);
        }
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken token = default) => Task.FromResult(CreateDbContext());
    }
}
