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
            "validate-sql" => await ValidateSqlCommand.ExecuteAsync(Console.Out, Console.Error),
            "fresh-start" => await FreshStartCommand.ExecuteAsync(
                args,
                Console.Out,
                Console.Error),
            "knowledge" or "code" or "corpus" or "operations" => await NativeV1Command.ExecuteFromEnvironmentAsync(
                args,
                Console.In,
                Console.Out,
                Console.Error),
            "codex" => await CodexPluginCommand.ExecuteFromEnvironmentAsync(
                args.Skip(1).ToArray(),
                Console.Out,
                Console.Error),
            _ => WriteUsage()
        };
    }

    private static int WriteUsage()
    {
        Console.Error.WriteLine("Usage: FluxKnowledge.Cli <knowledge|code|corpus|operations|codex|csharp-code|fresh-start|validate-sql>");
        return 2;
    }
}
