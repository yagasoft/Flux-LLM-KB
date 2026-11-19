using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

internal static class OutlookWatchedSourceCommand
{
    public static async Task<int> ExecuteAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var bind = args is ["bind", "--configuration", _];
        var suspend = args is ["suspend", "--profile", _];
        if (!bind && !suspend)
        {
            await error.WriteLineAsync("Usage: outlook-watched-source bind --configuration <private.json> | suspend --profile <guid>").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var connection = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("The Outlook capture database binding is missing.");
            var store = new SqlOutlookWatchedPublicationStore(new ContextFactory(connection));
            if (suspend)
            {
                if (!Guid.TryParse(args[2], out var profileId) || profileId == Guid.Empty)
                    throw new ArgumentException("A capture profile ID is required.");
                await store.SuspendAndRestoreLegacyAsync(profileId, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { ProfileId = profileId, Suspended = true })).ConfigureAwait(false);
                return 0;
            }

            await using var configuration = File.OpenRead(args[2]);
            var target = await JsonSerializer.DeserializeAsync<Binding>(configuration, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Outlook watched source configuration is missing.");
            if (target.ProfileId == Guid.Empty || target.SourceRootId == Guid.Empty)
                throw new InvalidDataException("The Outlook capture and source IDs are required.");

            if (!IsExportDrive(target.WatchRoot) || !IsExportDrive(target.StageRoot))
                throw new InvalidDataException("The private Outlook export directories must be on I:.");

            await store.BindAsync(target.ProfileId, target.SourceRootId, target.WatchRoot, target.StageRoot, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { target.ProfileId, target.SourceRootId, Bound = true })).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException
            or UnauthorizedAccessException or JsonException or SqlException)
        {
            await error.WriteLineAsync("Outlook watched source operation failed. Check the profile, dedicated source, path access and database.").ConfigureAwait(false);
            return 1;
        }
    }

    private static bool IsExportDrive(string path) =>
        !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetPathRoot(path), @"I:\", StringComparison.OrdinalIgnoreCase);

    private sealed record Binding(Guid ProfileId, Guid SourceRootId, string WatchRoot, string StageRoot);

    private sealed class ContextFactory(string connection) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connection, options => options.EnableRetryOnFailure()).Options);
    }
}
