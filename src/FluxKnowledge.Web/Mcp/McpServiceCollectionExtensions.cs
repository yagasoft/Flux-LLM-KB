using FluxKnowledge.Application.Mcp;
using FluxKnowledge.Web.NativeV1;

namespace FluxKnowledge.Web.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ReadonlyMcpRetryExecutor>();
        services.AddSingleton<NativeV1RequestMapper>();
        return services;
    }
}
