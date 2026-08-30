using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Infrastructure.SqlServer.Configuration;

public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";
    public const string CatalogName = "FluxKnowledge";
    public static string ProductionDataFilePath => LiveRootLayout.Production.SqlDataFilePath;
    public static string ProductionLogFilePath => LiveRootLayout.Production.SqlLogFilePath;

    public string ConnectionString { get; init; } = string.Empty;

    public string DataFilePath { get; init; } = ProductionDataFilePath;

    public string LogFilePath { get; init; } = ProductionLogFilePath;

    public static SqlServerOptions ForProduction(
        string connectionString,
        string dataFilePath,
        string logFilePath) =>
        new()
        {
            ConnectionString = connectionString,
            DataFilePath = dataFilePath,
            LogFilePath = logFilePath
        };
}
