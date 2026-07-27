using FluxKnowledge.Infrastructure.SqlServer.Configuration;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Provisioning;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddSingleton<ISqlServerReadinessValidator>(
            provider => provider.GetRequiredService<SqlServerReadinessValidator>());
        void ConfigureDatabase(DbContextOptionsBuilder builder) =>
            builder.UseSqlServer(
                options.ConnectionString,
                sqlServer => sqlServer.EnableRetryOnFailure());

        services.AddDbContextFactory<FluxKnowledgeDbContext>(ConfigureDatabase);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDerivedIndexRecoveryStore, SqlDerivedIndexRecoveryStore>();
        services.AddScoped<ILexicalSearch, SqlFullTextSearch>();
        services.AddScoped<ISearchHydrator, SqlSearchHydrator>();

        return services;
    }
}
