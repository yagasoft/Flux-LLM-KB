using System.Net;
using System.Net.Http.Json;
using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Pipeline;
using FluxKnowledge.Domain.Common;
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
}
