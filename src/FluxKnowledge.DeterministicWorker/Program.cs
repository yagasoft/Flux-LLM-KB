namespace FluxKnowledge.DeterministicWorker;

public static class Program
{
    public static Task<int> Main(string[] args) => DeterministicWorkerProtocolLoop.RunAsync(args, CancellationToken.None);
}
