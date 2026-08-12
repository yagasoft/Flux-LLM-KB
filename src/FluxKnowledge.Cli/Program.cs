using FluxKnowledge.Cli.Commands;

namespace FluxKnowledge.Cli;

internal static class CliProgram
{
    public static async Task<int> Main(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "csharp-code" => await LocalRetainedCsharpCodeCommand.ExecuteFromEnvironmentAsync(
                args.Skip(1).ToArray(),
                Console.Out,
                Console.Error),
            "provision-sql" => await ProvisionSqlCommand.ExecuteAsync(
                args.Skip(1).ToArray(),
                Console.Out,
                Console.Error),
            "validate-sql" => await ValidateSqlCommand.ExecuteAsync(Console.Out, Console.Error),
            _ => WriteUsage()
        };
    }

    private static int WriteUsage()
    {
        Console.Error.WriteLine("Usage: FluxKnowledge.Cli <csharp-code|provision-sql|validate-sql>");
        return 2;
    }
}
