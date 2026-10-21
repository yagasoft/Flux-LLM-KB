using System.Diagnostics;
using System.Text.Json;

namespace FluxKnowledge.Integration.Tests.Support;

// Opt-in diagnostics contain generated catalogue names and timings, never connections or SQL.
internal sealed class SqlFixtureTimings(string? outputDirectory)
{
    internal const string EnvironmentVariable = "FLUXKNOWLEDGE_TEST_TIMINGS_ROOT";
    private static readonly object WriteLock = new();

    internal async Task MeasureAsync(string phase, string catalog, Func<Task> action)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await action().ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            await action().ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            var record = JsonSerializer.Serialize(new
            {
                phase,
                catalog,
                seconds = Stopwatch.GetElapsedTime(started).TotalSeconds,
                succeeded
            });
            lock (WriteLock)
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    File.AppendAllText(Path.Combine(outputDirectory, $"sql-fixtures-{Environment.ProcessId}.jsonl"),
                        record + Environment.NewLine);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Profiling must not turn successful creation into an abandoned
                    // catalogue or replace a failure that drives fixture cleanup.
                    Console.Error.WriteLine("SQL fixture timing output is unavailable; timing evidence is incomplete.");
                }
            }
        }
    }
}
