using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Visibility;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Cli.Commands;

/// <summary>Read-only local-process commands for durable retained C# facts.</summary>
public static class LocalRetainedCsharpCodeCommand
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static void ValidateProductionStorageOverrides(
        string? retainedRoot,
        string? privateConfigRoot)
    {
        var layout = LiveRootLayout.Production;
        _ = LiveRootLayout.RequireExactProductionPathOverride(
            retainedRoot,
            layout.RetainedRoot,
            "FLUXKNOWLEDGE_SOURCE_ARTIFACT_ROOT");
        _ = LiveRootLayout.RequireExactProductionPathOverride(
            privateConfigRoot,
            layout.ConfigRoot,
            PrivatePcDataProtectionProviderFactory.LocalApplicationDataRootEnvironmentVariable);
    }

    public static async Task<int> ExecuteFromEnvironmentAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = ReaderLease.CreateFromEnvironment();
            return await ExecuteAsync(args, lease.Reader, output, error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or SqlException or InvalidDataException)
        {
            await error.WriteLineAsync("Trusted-local retained C# code details are unavailable.").ConfigureAwait(false);
            return 1;
        }
    }

    public static async Task<int> ExecuteAsync(
        string[] args,
        ILocalRetainedCsharpCodeReader reader,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            return await WriteUsageAsync(error).ConfigureAwait(false);
        }

        try
        {
            return args[0] switch
            {
                "detail" => await WriteDetailAsync(args, reader, output, error, cancellationToken).ConfigureAwait(false),
                "diagnostics" => await WriteDiagnosticsAsync(args, reader, output, error, cancellationToken).ConfigureAwait(false),
                "search" => await WriteSearchAsync(args, reader, output, error, cancellationToken).ConfigureAwait(false),
                _ => await WriteUsageAsync(error).ConfigureAwait(false)
            };
        }
        catch (LocalRetainedCsharpCodeSearchCursorException)
        {
            await error.WriteLineAsync("Retained C# search continuation is invalid.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidDataException or SqlException)
        {
            await error.WriteLineAsync("Trusted-local retained C# code details are unavailable.").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> WriteDetailAsync(
        string[] args,
        ILocalRetainedCsharpCodeReader reader,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryReadDetailArguments(args, out var branchId, out var pageRequest))
        {
            return await WriteUsageAsync(error).ConfigureAwait(false);
        }

        var detail = await reader.ReadPageAsync(branchId, pageRequest, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            await error.WriteLineAsync("No retained C# code detail was found.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(detail, SerializerOptions)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteDiagnosticsAsync(
        string[] args,
        ILocalRetainedCsharpCodeReader reader,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryReadBranchId(args, out var branchId))
        {
            return await WriteUsageAsync(error).ConfigureAwait(false);
        }

        var detail = await reader.ReadAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            await error.WriteLineAsync("No retained C# code diagnostics were found.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            detail.BranchId,
            detail.LocalPath,
            detail.ArtifactHash,
            detail.OutcomeCode,
            detail.WithheldDiagnosticCount,
            detail.Diagnostics
        }, SerializerOptions)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> WriteSearchAsync(
        string[] args,
        ILocalRetainedCsharpCodeReader reader,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryReadSearchArguments(args, out var query, out var cursor))
        {
            return await WriteUsageAsync(error).ConfigureAwait(false);
        }

        var page = await reader.SearchPageAsync(
            new LocalRetainedCsharpCodeSearchPageRequest(query, 10, cursor),
            cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(page, SerializerOptions)).ConfigureAwait(false);
        return 0;
    }

    private static bool TryReadBranchId(string[] args, out Guid branchId)
    {
        if (args.Length != 2)
        {
            branchId = default;
            return false;
        }

        return Guid.TryParse(args[1], out branchId);
    }

    private static bool TryReadDetailArguments(
        string[] args,
        out Guid branchId,
        out LocalRetainedCsharpCodePageRequest pageRequest)
    {
        branchId = default;
        pageRequest = LocalRetainedCsharpCodePageRequest.First;
        if ((args.Length != 2 && args.Length != 5) || !Guid.TryParse(args[1], out branchId))
        {
            return false;
        }

        if (args.Length == 2)
        {
            return true;
        }

        if (!TryReadNonNegativeOrdinal(args[2], out var symbolAfterOrdinal) ||
            !TryReadNonNegativeOrdinal(args[3], out var referenceAfterOrdinal) ||
            !TryReadNonNegativeOrdinal(args[4], out var diagnosticAfterOrdinal))
        {
            return false;
        }

        pageRequest = new LocalRetainedCsharpCodePageRequest(
            symbolAfterOrdinal,
            referenceAfterOrdinal,
            diagnosticAfterOrdinal);
        return true;
    }

    private static bool TryReadNonNegativeOrdinal(string value, out int ordinal) =>
        int.TryParse(value, out ordinal) && ordinal >= 0;

    private static bool TryReadSearchArguments(
        string[] args,
        out string query,
        out LocalRetainedCsharpCodeSearchCursor? cursor)
    {
        query = string.Empty;
        cursor = null;
        if ((args.Length != 2 && args.Length != 3) || string.IsNullOrWhiteSpace(args[1]))
        {
            return false;
        }

        query = args[1];
        if (args.Length == 2)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(args[2]))
        {
            return false;
        }

        cursor = new LocalRetainedCsharpCodeSearchCursor(args[2]);
        return true;
    }

    private static async Task<int> WriteUsageAsync(TextWriter error)
    {
        await error.WriteLineAsync(
            "Usage: FluxKnowledge.Cli csharp-code detail <branch-id> [symbol-after reference-after diagnostic-after] | diagnostics <branch-id> | search <query> [opaque-cursor] (read-only local commands).").ConfigureAwait(false);
        return 2;
    }

    private sealed class ReaderLease(ILocalRetainedCsharpCodeReader reader, SqlRetainedSourceReader retainedSourceReader) : IDisposable
    {
        public ILocalRetainedCsharpCodeReader Reader { get; } = reader;

        public static ReaderLease CreateFromEnvironment()
        {
            var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__FluxKnowledge");
            var configuredArtifactRoot = Environment.GetEnvironmentVariable("FLUXKNOWLEDGE_SOURCE_ARTIFACT_ROOT");
            var configuredPrivateRoot = Environment.GetEnvironmentVariable(
                PrivatePcDataProtectionProviderFactory.LocalApplicationDataRootEnvironmentVariable);
            ValidateProductionStorageOverrides(configuredArtifactRoot, configuredPrivateRoot);
            var artifactRoot = LiveRootLayout.Production.RetainedRoot;
            if (string.IsNullOrWhiteSpace(connectionString) || !Directory.Exists(artifactRoot))
            {
                throw new InvalidOperationException("Trusted-local retained C# code configuration is unavailable.");
            }

            var factory = new CliDbContextFactory(connectionString);
            var disclosure = new LocalPrivateContentDisclosure();
            var retainedSourceReader = new SqlRetainedSourceReader(factory, artifactRoot);
            var retainedDetailReader = new SqlLocalRetainedDetailReader(factory, retainedSourceReader, disclosure);
            var cursorCodec = PrivatePcDataProtectionProviderFactory.CreateCursorCodec();
            return new ReaderLease(
                new SqlLocalRetainedCsharpCodeReader(factory, retainedDetailReader, disclosure, cursorCodec),
                retainedSourceReader);
        }

        public void Dispose() => retainedSourceReader.Dispose();
    }

    private sealed class CliDbContextFactory(string connectionString) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        private readonly DbContextOptions<FluxKnowledgeDbContext> _options = new DbContextOptionsBuilder<FluxKnowledgeDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        public FluxKnowledgeDbContext CreateDbContext() => new(_options);

        public Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
