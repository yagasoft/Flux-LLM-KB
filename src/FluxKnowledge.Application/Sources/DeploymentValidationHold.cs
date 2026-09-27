using FluxKnowledge.Application.Operations;
using System.Text.Json;

namespace FluxKnowledge.Application.Sources;

/// <summary>Temporarily keeps mutating hosted services quiescent while an IIS payload is validated.</summary>
public interface IDeploymentValidationHold
{
    bool IsHeld { get; }

    DeploymentHoldAdmission ReadAdmissionState() => new(IsHeld, null);

    ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken);
}

public sealed record DeploymentHoldAdmission(bool IsHeld, Guid? PermittedCorpusRebuildOperationId);

public sealed class FileDeploymentValidationHold(LiveRootLayout liveRoot) : IDeploymentValidationHold
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(250);
    private readonly string _holdPath = Path.Combine(liveRoot.RuntimeRoot, "deployment-validation-hold.json");

    public async ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken)
    {
        while (IsHeld)
        {
            await Task.Delay(PollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool IsHeld => ReadAdmissionState().IsHeld;

    public DeploymentHoldAdmission ReadAdmissionState()
    {
        try
        {
            using var stream = new FileStream(_holdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length is < 1 or > 4096) return new(true, null);
            using var document = JsonDocument.Parse(stream, new() { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                root.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var value) || value != 1 ||
                !root.TryGetProperty("releaseId", out var release) || release.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(release.GetString()) || release.GetString()!.Length > 256 ||
                !root.TryGetProperty("corpusRebuildOperationId", out var operation) || operation.ValueKind != JsonValueKind.String ||
                !operation.TryGetGuid(out var id) || id == Guid.Empty)
                return new(true, null);
            return new(true, id);
        }
        catch (FileNotFoundException) { return new(false, null); }
        catch (DirectoryNotFoundException) { return new(false, null); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or JsonException)
        { return new(true, null); }
    }
}

public static class DeploymentValidationHold
{
    public static async ValueTask WaitUntilPipelineAllowedAsync(this IDeploymentValidationHold hold, CancellationToken ct)
    {
        while (hold.ReadAdmissionState() is { IsHeld: true, PermittedCorpusRebuildOperationId: null })
            await Task.Delay(250, ct).ConfigureAwait(false);
    }
    public static IDeploymentValidationHold None { get; } = new ReleasedDeploymentValidationHold();

    private sealed class ReleasedDeploymentValidationHold : IDeploymentValidationHold
    {
        public bool IsHeld => false;

        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
