using System.Security.Cryptography;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
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

/// <summary>Proves that a real retained-only media run cannot surface private inputs through public projections.</summary>
public sealed class MediaMetadataProcessorPrivacyTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Media_manifest_private_artifact_path_source_secret_and_status_are_absent_from_public_serialisations()
    {
        const string sourceSecret = "media-private-api-key-sentinel";
        var privateRoot = Path.Combine(Path.GetTempPath(), $"media-private-root-sentinel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = Png();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relative), bytes);
            var (rootId, revisionId) = await SeedAsync(hash, bytes.Length, relative, sourceSecret);

            var factory = new ContextFactory(_fixture.ConnectionString);
            var writer = new SqlRetainedArtifactWriter(factory, privateRoot);
            var zip = new ZipArchiveRetainedProcessor(writer);
            var media = new MediaMetadataRetainedProcessor(writer, new LocalPrivateContentDisclosure());
            var feed = new StatusEventFeed();
            await using var subscription = feed.Subscribe();
            var activation = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, new MediaMetadataCapabilityHandler()])),
                new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), new SqlRetainedSourceReader(factory, privateRoot), zip,
                new RetainedProcessorOptions { MediaMetadataEnabled = true }, TimeProvider.System, statusEvents: feed, mediaMetadataProcessor: media);

            Assert.Equal(1, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            var status = await subscription.Reader.ReadAsync();
            var sources = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootsAsync(CancellationToken.None);
            var source = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootAsync(rootId, CancellationToken.None);
            var corpus = await new SqlCorpusProjectionReader(factory).ReadPageAsync(new CorpusQuery(), CancellationToken.None);
            var events = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(new OperatorEventQuery(new OperatorEventFilters(SourceRootId: rootId)), CancellationToken.None);
            var rest = await ReadPipelineJsonAsync(factory);
            await using var verification = CreateContext();
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == revisionId && value.EventType == "retained_processor.completed");
            var publicJson = JsonSerializer.Serialize(new { sources, source, corpus, events, rest, audit, status });

            Assert.DoesNotContain(sourceSecret, publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateRoot, publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("{\"sanitised\":true}", Assert.Single(events.Items).Details);
            Assert.Contains("media_metadata", audit.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    [NativeSqlServerFact]
    public async Task Metadata_bearing_parser_fields_are_absent_from_durable_manifest_trusted_detail_and_public_outputs()
    {
        var privateRoot = Path.Combine(Path.GetTempPath(), $"media-metadata-fields-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = Png();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relative), bytes);
            var (rootId, revisionId) = await SeedAsync(hash, bytes.Length, relative, "media-safe-source");

            var factory = new ContextFactory(_fixture.ConnectionString);
            var writer = new SqlRetainedArtifactWriter(factory, privateRoot);
            var zip = new ZipArchiveRetainedProcessor(writer);
            var media = new MediaMetadataRetainedProcessor(writer, new LocalPrivateContentDisclosure(), new MetadataBearingParser());
            var feed = new StatusEventFeed();
            await using var subscription = feed.Subscribe();
            var activation = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(factory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, new MediaMetadataCapabilityHandler()])),
                new SqlRetainedProcessorBranchStore(factory, TimeProvider.System), new SqlRetainedSourceReader(factory, privateRoot), zip,
                new RetainedProcessorOptions { MediaMetadataEnabled = true }, TimeProvider.System, statusEvents: feed, mediaMetadataProcessor: media);

            Assert.Equal(1, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            var status = await subscription.Reader.ReadAsync();
            var sources = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootsAsync(CancellationToken.None);
            var source = await new SourceRootProjectionReader(factory, null!, null!, null!).ReadRootAsync(rootId, CancellationToken.None);
            var corpus = await new SqlCorpusProjectionReader(factory).ReadPageAsync(new CorpusQuery(), CancellationToken.None);
            var events = await new SqlOperatorEventProjectionReader(factory).ReadPageAsync(new OperatorEventQuery(new OperatorEventFilters(SourceRootId: rootId)), CancellationToken.None);
            var rest = await ReadPipelineJsonAsync(factory);
            await using var verification = CreateContext();
            var branchId = await verification.SourceProcessorBranches.Where(value => value.SourceRevisionId == revisionId).Select(value => value.Id).SingleAsync();
            var childId = await verification.SourceProcessorBranchMembers.Where(value => value.BranchId == branchId).Select(value => value.ChildSourceRevisionId).SingleAsync();
            var childArtifact = await verification.SourceArtifacts.SingleAsync(value => value.SourceRevisionId == childId);
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == revisionId && value.EventType == "retained_processor.completed");
            var manifest = await File.ReadAllTextAsync(Path.Combine(privateRoot, childArtifact.StoreRelativePath));
            var detail = await new SqlLocalRetainedDetailReader(factory, new SqlRetainedSourceReader(factory, privateRoot), new LocalPrivateContentDisclosure())
                .ReadAsync(branchId, CancellationToken.None);
            var trustedDetailJson = JsonSerializer.Serialize(detail);
            var publicJson = JsonSerializer.Serialize(new { sources, source, corpus, events, rest, audit, status });

            Assert.Equal("{\"schema\":\"media-metadata-v1\",\"format\":\"png\",\"container\":\"png\",\"dimensions\":{\"width\":1,\"height\":1},\"duration_ms\":null,\"audio\":null}", manifest);
            foreach (var sentinel in MetadataBearingParser.ForbiddenSentinels)
            {
                Assert.DoesNotContain(sentinel, manifest, StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, trustedDetailJson, StringComparison.Ordinal);
                Assert.DoesNotContain(sentinel, publicJson, StringComparison.Ordinal);
            }
            Assert.Equal("{\"sanitised\":true}", Assert.Single(events.Items).Details);
            Assert.Contains("media_metadata", audit.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    private async Task<(Guid RootId, Guid RevisionId)> SeedAsync(string hash, int length, string relative, string sourceSecret)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = $"C:\\phase5-media-public-root-{rootId:N}", DisplayName = "Media", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 64L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"media:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = $"C:\\source-original\\{sourceSecret}.png", Classification = "MediaMetadata", Extension = ".png", ByteLength = length, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relative, ByteLength = length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.TextExtraction,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (rootId, revisionId);
    }

    private static async Task<string> ReadPipelineJsonAsync(IDbContextFactory<FluxKnowledgeDbContext> factory)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IProjectionReader>(new SqlProjectionReader(factory, new HealthyRecoveryStatus(), new SqlGpuSchedulerStore(factory)));
        builder.Services.AddScoped<RegisterUtf8FileHandler>(); builder.Services.AddSingleton<IUtf8FileSourceReader, UnusedUtf8Reader>();
        builder.Services.AddSingleton<IRegistrationStore, UnusedRegistrationStore>(); builder.Services.AddSingleton<IStatusEventPublisher, StatusEventFeed>();
        await using var app = builder.Build(); app.MapFluxKnowledgePipelineRecords(); await app.StartAsync();
        return await app.GetTestClient().GetStringAsync("/api/pipeline-records");
    }

    private FluxKnowledgeDbContext CreateContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(_fixture.ConnectionString).Options);
    private sealed class ContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connectionString).Options;
        public FluxKnowledgeDbContext CreateDbContext() => new(_options);
        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class HealthyRecoveryStatus : IDerivedIndexRecoveryStatus { public DerivedIndexRecoverySnapshot Snapshot { get; } = new(DerivedIndexRecoveryState.Healthy, null, null, null, null, 0); }
    private sealed class UnusedUtf8Reader : IUtf8FileSourceReader { public ValueTask<Utf8FileSource> ReadAsync(string suppliedPath, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class UnusedRegistrationStore : IRegistrationStore { public ValueTask<RegistrationReceipt> RegisterAsync(Utf8FileRegistration registration, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class MetadataBearingParser : IMediaMetadataParser
    {
        public static IReadOnlyList<string> ForbiddenSentinels { get; } = ["media-exif-sentinel", "media-gps-sentinel", "media-title-sentinel"];
        public MediaMetadataParserPreflight Preflight() => new(true, null);

        public MediaMetadataParseResult Parse(Stream stream, MediaMetadataFormat format, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(MediaMetadataFormat.Png, format);
            Assert.Equal(0x89, stream.ReadByte());
            return MediaMetadataParseResult.Png(1, 1,
            [
                new MediaMetadataIgnoredField("Exif.UserComment", ForbiddenSentinels[0]),
                new MediaMetadataIgnoredField("Gps.Latitude", ForbiddenSentinels[1]),
                new MediaMetadataIgnoredField("Xmp.Title", ForbiddenSentinels[2])
            ]);
        }
    }
    private static byte[] Png() => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Jj7kAAAAASUVORK5CYII=");
}
