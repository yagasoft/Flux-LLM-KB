using FluxKnowledge.Application.Mcp;

namespace FluxKnowledge.Web.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ReadonlyMcpRetryExecutor>();
        return services;
    }
}
