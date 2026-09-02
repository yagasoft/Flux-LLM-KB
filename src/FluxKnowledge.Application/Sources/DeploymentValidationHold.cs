using FluxKnowledge.Application.Operations;

namespace FluxKnowledge.Application.Sources;

/// <summary>Temporarily keeps mutating hosted services quiescent while an IIS payload is validated.</summary>
public interface IDeploymentValidationHold
{
    ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken);
}

public sealed class FileDeploymentValidationHold(LiveRootLayout liveRoot) : IDeploymentValidationHold
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(250);
    private readonly string _holdPath = Path.Combine(liveRoot.RuntimeRoot, "deployment-validation-hold.json");

    public async ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken)
    {
        while (IsHeld())
        {
            await Task.Delay(PollingInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsHeld()
    {
        try
        {
            using var stream = new FileStream(_holdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }
}

public static class DeploymentValidationHold
{
    public static IDeploymentValidationHold None { get; } = new ReleasedDeploymentValidationHold();

    private sealed class ReleasedDeploymentValidationHold : IDeploymentValidationHold
    {
        public ValueTask WaitUntilReleasedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
