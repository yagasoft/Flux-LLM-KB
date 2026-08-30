using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web.Components.Status;
using FluxKnowledge.Web.Components.Sources;
using FluxKnowledge.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Components;

public sealed class TarArchiveProcessorPrivacyTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private const string MemberNameSentinel = "confidential-member-sentinel.txt";

    [NativeSqlServerFact]
    public async Task Production_tar_transition_keeps_private_root_and_member_name_out_of_public_projection_rest_and_status_feed_serialisations()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"private-spool-sentinel-{Guid.NewGuid():N}");
        var fallbackRoot = Path.Combine(Path.GetTempPath(), $"tar-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        Directory.CreateDirectory(fallbackRoot);
        try
        {
            var tar = CreateTar(MemberNameSentinel, "private retained TAR member");
            var hash = Convert.ToHexStringLower(SHA256.HashData(tar));
            var relativePath = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relativePath), tar);
            var (rootId, revisionId) = await SeedDeferredTarAsync(hash, tar.Length, relativePath, privateRoot);

            var factory = new TestDbContextFactory(fixture.ConnectionString);
            var policy = PersistedOutlookSpoolRootPolicy.CreateForIsolatedTests(privateRoot);
            var writer = new SqlRetainedArtifactWriter(factory, fallbackRoot, outlookSpoolPolicy: policy);
            var zip = new ZipArchiveRetainedProcessor(writer);
            var processor = new TarArchiveRetainedProcessor(writer);
            var statusFeed = new StatusEventFeed();
            await using var statusSubscription = statusFeed.Subscribe();
            var activation = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, processor])),
                new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), new SqlRetainedSourceReader(factory, privateRoot, policy), zip,
                new RetainedProcessorOptions { ArchiveTarExpandEnabled = true }, TimeProvider.System, processor, statusFeed);

            Assert.Equal(1, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            var publishedStatus = await statusSubscription.Reader.ReadAsync();

            var sources = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootsAsync(CancellationToken.None);
            var source = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootAsync(rootId, CancellationToken.None);
            var corpus = await new SqlCorpusProjectionReader(factory).ReadPageAsync(new CorpusQuery(), CancellationToken.None);
            var events = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(new OperatorEventQuery(), CancellationToken.None);
            var audit = await ReadAuditAsync(revisionId);
            var rest = await ReadPipelineEndpointJsonAsync(factory);
            var serialised = JsonSerializer.Serialize(new { sources, source, corpus, events, audit, rest, publishedStatus });

            Assert.DoesNotContain(privateRoot, serialised, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MemberNameSentinel, serialised, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("{\"sanitised\":true}", Assert.Single(events.Items).Details);
            Assert.Contains("archive_tar", audit.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
            if (Directory.Exists(fallbackRoot)) Directory.Delete(fallbackRoot, recursive: true);
        }
    }

    private static async Task<string> ReadPipelineEndpointJsonAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IProjectionReader>(new SqlProjectionReader(
            factory, new HealthyRecoveryStatus(), new SqlGpuSchedulerStore(factory)));
        builder.Services.AddScoped<RegisterUtf8FileHandler>();
        builder.Services.AddSingleton<IUtf8FileSourceReader, UnusedUtf8Reader>();
        builder.Services.AddSingleton<IRegistrationStore, UnusedRegistrationStore>();
        builder.Services.AddSingleton<IStatusEventPublisher, StatusEventFeed>();
        await using var app = builder.Build();
        app.MapFluxKnowledgePipelineRecords();
        await app.StartAsync();
        return await app.GetTestClient().GetStringAsync("/api/pipeline-records");
    }

    private async Task<(Guid RootId, Guid RevisionId)> SeedDeferredTarAsync(string hash, int byteLength, string relativePath, string privateRoot)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = "C:\\phase5-public-root", DisplayName = "Public retained TAR", State = (int)SourceRootState.Enabled,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.OutlookCaptureProfiles.Add(new OutlookCaptureProfileEntity { Id = Guid.NewGuid(), SourceRootId = rootId, DisplayName = "Private retained root", SpoolRoot = privateRoot,
            IncrementalBasis = 0, State = 0, IsEnabled = false, ConfigurationRevision = 1, CadenceTicks = TimeSpan.FromMinutes(5).Ticks,
            MaximumOverlapTicks = TimeSpan.FromMinutes(1).Ticks, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"tar-private:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = "C:\\missing-source-original-sentinel.tar", Classification = "DeferredCapability", Extension = ".tar", ByteLength = byteLength, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relativePath, ByteLength = byteLength, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (rootId, revisionId);
    }

    private async Task<AuditEventEntity> ReadAuditAsync(Guid revisionId)
    {
        await using var context = CreateContext();
        return await context.AuditEvents.SingleAsync(value => value.SourceRevisionId == revisionId && value.EventType == "retained_processor.completed");
    }

    private static byte[] CreateTar(string name, string content)
    {
        using var buffer = new MemoryStream();
        using var writer = new TarWriter(buffer, TarEntryFormat.Ustar, leaveOpen: true);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
        writer.WriteEntry(entry);
        return buffer.ToArray();
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(fixture.ConnectionString).Options);

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class HealthyRecoveryStatus : IDerivedIndexRecoveryStatus
    {
        public DerivedIndexRecoverySnapshot Snapshot { get; } = new(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0);
    }

    private sealed class UnusedUtf8Reader : IUtf8FileSourceReader
    {
        public ValueTask<Utf8FileSource> ReadAsync(string suppliedPath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedRegistrationStore : IRegistrationStore
    {
        public ValueTask<RegistrationReceipt> RegisterAsync(Utf8FileRegistration registration, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
