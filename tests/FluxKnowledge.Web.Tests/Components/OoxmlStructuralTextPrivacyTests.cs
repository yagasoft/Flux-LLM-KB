using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Indexing;
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

public sealed class OoxmlStructuralTextPrivacyTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    private readonly NativeSqlServerFixture _fixture = fixture;

    [NativeSqlServerFact]
    public async Task Ooxml_text_private_artifact_path_and_source_path_are_absent_from_public_projections_rest_audit_and_status()
    {
        const string textSentinel = "ooxml-private-text-sentinel";
        const string sourcePathSentinel = "ooxml-private-source-path-sentinel";
        var privateRoot = Path.Combine(Path.GetTempPath(), $"ooxml-private-root-sentinel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(privateRoot);
        try
        {
            var bytes = CreateDocx(textSentinel);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var relative = Path.Combine("sha256", hash[..2], $"{hash}.bin");
            Directory.CreateDirectory(Path.Combine(privateRoot, "sha256", hash[..2]));
            await File.WriteAllBytesAsync(Path.Combine(privateRoot, relative), bytes);
            var (rootId, revisionId) = await SeedAsync(hash, bytes.Length, relative, sourcePathSentinel);

            var dbFactory = new ContextFactory(_fixture.ConnectionString);
            var writer = new SqlRetainedArtifactWriter(dbFactory, privateRoot);
            var zip = new ZipArchiveRetainedProcessor(writer);
            var ooxml = new OoxmlStructuralTextProcessor(writer);
            var feed = new StatusEventFeed();
            await using var subscription = feed.Subscribe();
            var activation = new RetainedProcessorActivationService(
                new SourceCapabilityService(new SqlSourceActivityStore(dbFactory, TimeProvider.System), new LocalSourceCapabilityHandlerRegistry([zip, new OoxmlStructuralTextCapabilityHandler()])),
                new SqlRetainedProcessorBranchStore(dbFactory, TimeProvider.System), new SqlRetainedSourceReader(dbFactory, privateRoot), zip,
                new RetainedProcessorOptions { OoxmlDocumentStructuralExtractEnabled = true }, TimeProvider.System, statusEvents: feed, ooxmlProcessor: ooxml);

            Assert.Equal(1, (await activation.RunOnceAsync(CancellationToken.None)).CompletedBranches);
            var status = await subscription.Reader.ReadAsync();
            var sources = await new SourceRootProjectionReader(dbFactory, null!, null!, null!).ReadRootsAsync(CancellationToken.None);
            var source = await new SourceRootProjectionReader(dbFactory, null!, null!, null!).ReadRootAsync(rootId, CancellationToken.None);
            var corpus = await new SqlCorpusProjectionReader(dbFactory).ReadPageAsync(new CorpusQuery(), CancellationToken.None);
            var events = await new SqlOperatorEventProjectionReader(dbFactory).ReadPageAsync(new OperatorEventQuery(), CancellationToken.None);
            var rest = await ReadPipelineJsonAsync(dbFactory);
            await using var verification = CreateContext();
            var audit = await verification.AuditEvents.SingleAsync(value => value.SourceRevisionId == revisionId && value.EventType == "retained_processor.completed");
            var publicJson = JsonSerializer.Serialize(new { sources, source, corpus, events, rest, audit, status });

            Assert.DoesNotContain(textSentinel, publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(sourcePathSentinel, publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateRoot, publicJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("{\"sanitised\":true}", Assert.Single(events.Items).Details);
            Assert.Contains("ooxml", audit.DetailsJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(privateRoot)) Directory.Delete(privateRoot, recursive: true);
        }
    }

    private async Task<(Guid RootId, Guid RevisionId)> SeedAsync(string hash, int length, string relative, string sourcePath)
    {
        var rootId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using var context = CreateContext();
        context.SourceRootConfigurations.Add(new SourceRootConfigurationEntity { Id = rootId, CanonicalPath = "C:\\phase5-public-root", DisplayName = "OOXML", State = 0,
            Recursive = true, IncludePatternsJson = "[]", ExcludePatternsJson = "[]", FollowLinks = false, MaximumFileBytes = 128L * 1024 * 1024,
            AllowedClassificationsJson = "[]", CrawlMode = 0, ReconciliationCadenceSeconds = 900, ConfigurationRevision = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        context.SourceRevisions.Add(new SourceRevisionEntity { Id = revisionId, SourceRootId = rootId, StableSourceIdentity = $"ooxml:{revisionId:N}", Revision = 1,
            ContentSha256 = hash, CanonicalPath = sourcePath + ".docx", Classification = "DeferredCapability", Extension = ".docx", ByteLength = length, DiscoveredAtUtc = now, DiscoveryEvidenceJson = "{}" });
        context.SourceArtifacts.Add(new SourceArtifactEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ContentSha256 = hash, StoreRelativePath = relative, ByteLength = length, ChecksumVerifiedAtUtc = now, ReferenceCount = 1 });
        context.SourceActivities.Add(new SourceActivityEntity { Id = Guid.NewGuid(), SourceRevisionId = revisionId, ActivityKind = (int)SourceActivityKind.DocumentParsing,
            ExecutionClass = (int)ExecutionClass.DeferredCapability, ProcessorVersion = "phase-3a-v1", InputFingerprint = hash, RequiredCapability = "local-source-capability",
            State = (int)SourceActivityState.DeferredUnsupported, Reason = "deferred", CreatedAtUtc = now, UpdatedAtUtc = now });
        await context.SaveChangesAsync();
        return (rootId, revisionId);
    }

    private static byte[] CreateDocx(string text)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>");
            WriteEntry(archive, "_rels/.rels", "<Relationships><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>");
            WriteEntry(archive, "word/document.xml", $"<w:document xmlns:w='w'><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
        }
        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(value
            .Replace("<Types>", "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'>", StringComparison.Ordinal)
            .Replace("<Relationships>", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>", StringComparison.Ordinal)
            .Replace("xmlns:w='w'", "xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'", StringComparison.Ordinal));
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
}
