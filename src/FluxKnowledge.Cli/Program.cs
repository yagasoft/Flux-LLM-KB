using FluxKnowledge.Cli.Commands;

return args.FirstOrDefault() switch
{
    "provision-sql" => await ProvisionSqlCommand.ExecuteAsync(
        args.Skip(1).ToArray(),
        Console.Out,
        Console.Error),
    "validate-sql" => await ValidateSqlCommand.ExecuteAsync(Console.Out, Console.Error),
    _ => WriteUsage()
};

static int WriteUsage()
{
    Console.Error.WriteLine("Usage: FluxKnowledge.Cli <provision-sql|validate-sql>");
    return 2;
}
