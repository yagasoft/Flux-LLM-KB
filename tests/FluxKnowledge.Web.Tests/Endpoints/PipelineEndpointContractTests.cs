using System.Net;
using System.Net.Http.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Common;
using FluxKnowledge.Web.Components.Status;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Endpoints;

public sealed class PipelineEndpointContractTests : IClassFixture<PipelineEndpointContractTests.PipelineApplicationFactory>
{
    private readonly HttpClient _client;

    public PipelineEndpointContractTests(PipelineApplicationFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Utf8_file_registration_returns_accepted_receipt()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/pipeline-records/utf8-file",
            new RegisterUtf8FileCommand("C:/ingress/known.txt", "test", "known.txt"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<RegisterUtf8FileResult>();
        Assert.NotNull(receipt);
    }

    [Fact]
    public async Task Pipeline_records_endpoint_serialises_the_SQL_projection()
    {
        var records = await _client.GetFromJsonAsync<List<PipelineRecordProjection>>("/api/pipeline-records");

        var record = Assert.Single(records!);
        Assert.Equal("C:/ingress/known.txt", record.SourceIdentity);
        Assert.Equal("WorkerQueued", record.Status);
        Assert.Equal("0123456789ab", record.ContentHashPrefix);
    }

    [Fact]
    public async Task Pipeline_record_endpoint_returns_the_matching_projection_or_not_found()
    {
        var known = Guid.Parse("00000000-0000-0000-0000-000000000004");

        var record = await _client.GetFromJsonAsync<PipelineRecordProjection>($"/api/pipeline-records/{known}");
        using var missing = await _client.GetAsync("/api/pipeline-records/00000000-0000-0000-0000-000000000005");

        Assert.NotNull(record);
        Assert.Equal(known, record.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public sealed class PipelineApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:FluxKnowledge", "Server=unreachable.invalid;Initial Catalog=FluxKnowledge;Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
            builder.UseSetting("LocalIngress:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting("Usearch:RootPath", Path.Combine(Path.GetTempPath(), "FluxKnowledgePipelineEndpointTests"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IUtf8FileSourceReader, FakeUtf8FileSourceReader>();
                services.AddSingleton<IRegistrationStore, FakeRegistrationStore>();
                services.AddSingleton<IProjectionReader, FakeProjectionReader>();
            });
        }
    }

    private sealed class FakeUtf8FileSourceReader : IUtf8FileSourceReader
    {
        public ValueTask<Utf8FileSource> ReadAsync(string suppliedPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Utf8FileSource(suppliedPath, [], "known", new string('a', 64)));
    }

    private sealed class FakeRegistrationStore : IRegistrationStore
    {
        public ValueTask<RegistrationReceipt> RegisterAsync(Utf8FileRegistration registration, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RegistrationReceipt(
                new PipelineRecordId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                new JobId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
                new DispatchMessageId(Guid.Parse("00000000-0000-0000-0000-000000000003")),
                1,
                registration.ContentHash,
                new PipelineRecordId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                null,
                false));
    }

    private sealed class FakeProjectionReader : IProjectionReader
    {
        private static readonly PipelineRecordProjection Record = new(
            Guid.Parse("00000000-0000-0000-0000-000000000004"),
            "C:/ingress/known.txt",
            1,
            "Extract",
            "WorkerQueued",
            "0123456789ab",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        public ValueTask<OverviewProjection> ReadOverviewAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OverviewProjection(0, 0, 0, 0, 0, 0, 0, "none"));

        public ValueTask<IReadOnlyList<PipelineRecordProjection>> ReadPipelineRecordsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PipelineRecordProjection>>([Record]);

        public ValueTask<PipelineRecordProjection?> ReadPipelineRecordAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<PipelineRecordProjection?>(id == Record.Id ? Record : null);
    }
}
