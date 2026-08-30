using System.Text.Json;
using FluxKnowledge.Application.Operations;
using FluxKnowledge.Integrations.Windows.NativeGoLive;

namespace FluxKnowledge.Integrations.Codex;

/// <summary>Read-only lifecycle contract used by normal status composition.</summary>
internal interface ICodexMarketplaceLifecycle
{
    ValueTask<CodexMarketplaceLifecycleStatus> ObserveAsync(CodexMarketplaceIdentity identity, CancellationToken cancellationToken);
}

public sealed record CodexMarketplaceIdentity(string MarketplaceRoot, string MarketplaceName, string PluginName);

internal enum CodexMarketplaceLifecycleState { Registered, Missing, Foreign, Unavailable }

internal sealed record CodexMarketplaceLifecycleStatus(CodexMarketplaceLifecycleState State, string? Reason = null);

/// <summary>Safe default for normal Web, MCP and CLI status composition.</summary>
internal sealed class UnavailableCodexMarketplaceLifecycle : ICodexMarketplaceLifecycle
{
    public static UnavailableCodexMarketplaceLifecycle Instance { get; } = new();

    private UnavailableCodexMarketplaceLifecycle()
    {
    }

    public ValueTask<CodexMarketplaceLifecycleStatus> ObserveAsync(
        CodexMarketplaceIdentity identity,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CodexMarketplaceLifecycleStatus(
            CodexMarketplaceLifecycleState.Unavailable,
            "lifecycle-unavailable"));
}

/// <summary>
/// Sanitised preflight evidence produced before the irreversible operation. Only the expected
/// source path and a structural hash of unrelated configuration are retained.
/// </summary>
internal sealed record CodexMarketplacePreflight(
    CodexMarketplaceLifecycleState State,
    string? ExistingSourceRoot,
    string UnrelatedConfigurationStructuralHash);

internal sealed record NativeCodexMarketplaceCommandResult(
    int ExitCode,
    string StandardOutput,
    string UnrelatedConfigurationStructuralHash);

/// <summary>Typed seam for the only two Codex process actions permitted during native go-live.</summary>
internal interface INativeCodexMarketplaceCommandRunner
{
    ValueTask<NativeCodexMarketplaceCommandResult> AddFluxKnowledgeMarketplaceAsync(
        string marketplaceRoot,
        CancellationToken cancellationToken);

    ValueTask<NativeCodexMarketplaceCommandResult> ListMarketplacesJsonAsync(
        CancellationToken cancellationToken);
}

internal sealed record NativeCodexMarketplaceRegistration(bool Changed, bool IsHealthy, string? Reason)
{
    internal static NativeCodexMarketplaceRegistration Healthy(bool changed) => new(changed, true, null);
    internal static NativeCodexMarketplaceRegistration Refused(string reason) => new(false, false, reason);
    internal static NativeCodexMarketplaceRegistration FailedAfterMutation(string reason) => new(true, false, reason);
}

/// <summary>
/// Go-live-only marketplace adapter. Construction is possible only with Task 2's claimed native
/// authority capability; normal composition receives no process runner and cannot construct it.
/// </summary>
internal sealed class NativeCodexMarketplaceLifecycleAdapter
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(15);

    private readonly NativeGoLiveProvisioningCapability _capability;
    private readonly NativeGoLiveCodexIdentity _expectedIdentity;
    private readonly NativeCodexPluginManifestWriter _writer;
    private readonly INativeCodexMarketplaceCommandRunner _runner;
    private readonly CodexMarketplacePreflight _preflight;
    private readonly TimeSpan _commandTimeout;

    internal NativeCodexMarketplaceLifecycleAdapter(
        NativeGoLiveProvisioningCapability capability,
        NativeGoLiveCodexIdentity expectedIdentity,
        NativeCodexPluginManifestWriter writer,
        INativeCodexMarketplaceCommandRunner runner,
        CodexMarketplacePreflight preflight,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(preflight);
        capability.EnsureClaimed();
        var expectedPaths = CodexRegistrationPaths.FromNativeGoLive(expectedIdentity);

        var timeout = commandTimeout ?? DefaultCommandTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), "The Codex command timeout must be positive and at most one minute.");
        }

        _capability = capability;
        _expectedIdentity = expectedPaths.NativeGoLiveIdentity;
        _writer = writer;
        _runner = runner;
        _preflight = preflight;
        _commandTimeout = timeout;
    }

    internal async ValueTask<NativeCodexMarketplaceRegistration> RegisterAsync(
        NativeGoLiveCodexIdentity identity,
        CancellationToken cancellationToken)
    {
        _capability.EnsureClaimed();
        if (!CodexMarketplaceIdentityPolicy.Same(identity, _expectedIdentity))
        {
            return NativeCodexMarketplaceRegistration.Refused("foreign-registration");
        }

        switch (_preflight.State)
        {
            case CodexMarketplaceLifecycleState.Foreign:
                return NativeCodexMarketplaceRegistration.Refused("foreign-registration");
            case CodexMarketplaceLifecycleState.Unavailable:
                return NativeCodexMarketplaceRegistration.Refused("lifecycle-unavailable");
            case CodexMarketplaceLifecycleState.Registered:
                return SamePath(_preflight.ExistingSourceRoot, identity.MarketplaceRoot)
                    ? NativeCodexMarketplaceRegistration.Healthy(changed: false)
                    : NativeCodexMarketplaceRegistration.Refused("foreign-registration");
            case CodexMarketplaceLifecycleState.Missing:
                break;
            default:
                return NativeCodexMarketplaceRegistration.Refused("lifecycle-unavailable");
        }

        if (!IsStructuralHash(_preflight.UnrelatedConfigurationStructuralHash))
        {
            return NativeCodexMarketplaceRegistration.Refused("configuration-structural-hash-invalid");
        }

        await _writer.WriteAsync(identity.MarketplaceRoot, cancellationToken).ConfigureAwait(false);

        NativeCodexMarketplaceCommandResult added;
        try
        {
            added = await RunBoundedAsync(
                token => _runner.AddFluxKnowledgeMarketplaceAsync(identity.MarketplaceRoot, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NativeCodexMarketplaceRegistration.FailedAfterMutation("marketplace-add-timeout");
        }

        if (added.ExitCode != 0)
        {
            return NativeCodexMarketplaceRegistration.FailedAfterMutation("marketplace-add-failed");
        }

        NativeCodexMarketplaceCommandResult listed;
        try
        {
            listed = await RunBoundedAsync(
                _runner.ListMarketplacesJsonAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NativeCodexMarketplaceRegistration.FailedAfterMutation("marketplace-verification-timeout");
        }

        if (listed.ExitCode != 0 || !ContainsExactSource(listed.StandardOutput, identity))
        {
            return NativeCodexMarketplaceRegistration.FailedAfterMutation("marketplace-verification-failed");
        }

        if (!string.Equals(
                _preflight.UnrelatedConfigurationStructuralHash,
                listed.UnrelatedConfigurationStructuralHash,
                StringComparison.Ordinal))
        {
            return NativeCodexMarketplaceRegistration.FailedAfterMutation("unrelated-configuration-changed");
        }

        return NativeCodexMarketplaceRegistration.Healthy(changed: true);
    }

    private async ValueTask<NativeCodexMarketplaceCommandResult> RunBoundedAsync(
        Func<CancellationToken, ValueTask<NativeCodexMarketplaceCommandResult>> operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_commandTimeout);
        return await operation(timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    private static bool ContainsExactSource(string json, NativeGoLiveCodexIdentity identity)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("marketplaces", out var marketplaces) ||
                marketplaces.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var matchingName = marketplaces.EnumerateArray()
                .Where(entry => entry.ValueKind == JsonValueKind.Object &&
                    StringProperty(entry, "name") == identity.MarketplaceName)
                .ToArray();
            if (matchingName.Length != 1 ||
                !matchingName[0].TryGetProperty("source", out var source) ||
                StringProperty(source, "source") != "local")
            {
                return false;
            }

            return SamePath(StringProperty(source, "path"), identity.MarketplaceRoot);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsStructuralHash(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal static class CodexMarketplaceIdentityPolicy
{
    internal static bool Same(CodexMarketplaceIdentity left, CodexMarketplaceIdentity right) =>
        Same(
            new NativeGoLiveCodexIdentity(left.MarketplaceRoot, left.MarketplaceName, left.PluginName),
            new NativeGoLiveCodexIdentity(right.MarketplaceRoot, right.MarketplaceName, right.PluginName));

    internal static bool Same(NativeGoLiveCodexIdentity left, NativeGoLiveCodexIdentity right) =>
        IsWellFormed(left) &&
        IsWellFormed(right) &&
        SamePath(left.MarketplaceRoot, right.MarketplaceRoot) &&
        string.Equals(left.MarketplaceName, right.MarketplaceName, StringComparison.Ordinal) &&
        string.Equals(left.PluginName, right.PluginName, StringComparison.Ordinal);

    internal static bool IsWellFormed(NativeGoLiveCodexIdentity identity) =>
        identity is not null &&
        !string.IsNullOrWhiteSpace(identity.MarketplaceRoot) &&
        Path.IsPathFullyQualified(identity.MarketplaceRoot) &&
        !identity.MarketplaceRoot.StartsWith(@"\\", StringComparison.Ordinal) &&
        !identity.MarketplaceRoot.StartsWith("//", StringComparison.Ordinal) &&
        string.Equals(identity.MarketplaceName, CodexRegistrationPaths.MarketplaceName, StringComparison.Ordinal) &&
        string.Equals(identity.PluginName, CodexRegistrationPaths.PluginName, StringComparison.Ordinal);

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
