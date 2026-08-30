using FluxKnowledge.Application.IntegrationV1;
using Xunit;

namespace FluxKnowledge.Domain.Tests.IntegrationV1;

public sealed class NativeV1FacadeContractTests
{
    [Fact]
    public void Native_v1_query_facade_is_available_from_the_application_contract()
    {
        var facadeType = typeof(NativeOperationService).Assembly.GetType(
            "FluxKnowledge.Application.IntegrationV1.NativeV1Facade");

        Assert.NotNull(facadeType);
    }
}
