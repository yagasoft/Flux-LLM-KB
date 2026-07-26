using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FluxKnowledge.Infrastructure.SqlServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFluxKnowledgeSqlServer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = SqlServerOptions.ForProduction(
            configuration.GetConnectionString(SqlServerOptions.CatalogName) ?? string.Empty,
            SqlServerOptions.ProductionDataFilePath,
            SqlServerOptions.ProductionLogFilePath);
        SqlServerOptionsValidator.ThrowIfInvalid(options);

        services.AddSingleton(options);
        services.AddSingleton<SqlServerReadinessValidator>();
        void ConfigureDatabase(DbContextOptionsBuilder builder) =>
            builder.UseSqlServer(
                options.ConnectionString,
                sqlServer => sqlServer.EnableRetryOnFailure());

        services.AddDbContextFactory<FluxKnowledgeDbContext>(ConfigureDatabase);

        return services;
    }
}
