namespace FluxKnowledge.Infrastructure.SqlServer.Configuration;

public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";
    public const string CatalogName = "FluxKnowledge";
    public const string ProductionDataFilePath = "I:/FluxKnowledge/Sql/Data/FluxKnowledge.mdf";
    public const string ProductionLogFilePath = "I:/FluxKnowledge/Sql/Log/FluxKnowledge_log.ldf";

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
