using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Application.Visibility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FluxKnowledge.Infrastructure.SqlServer.Workers;

/// <summary>Builds optional disclosure evidence without replaying publication or model work.</summary>
public sealed class CodeDisclosureProofRecoveryService(
    ICodeDisclosureProofStore store, CsharpDisclosureProofBuilder builder,
    IDeploymentValidationHold hold, ILogger<CodeDisclosureProofRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await hold.WaitUntilReleasedAsync(stoppingToken).ConfigureAwait(false);
                var progressed = false;
                try
                {
                    var artifact = await store.ReadNextAsync(stoppingToken).ConfigureAwait(false);
                    if (artifact is not null)
                    {
                        var proof = artifact.Text is null ? Terminal(artifact, CodeDisclosureProofState.Unsupported) :
                            builder.Build(artifact.ArtifactId, artifact.Text, stoppingToken);
                        if (proof.CanonicalHash != artifact.CanonicalHash)
                            proof = Terminal(artifact, CodeDisclosureProofState.Invalid);
                        progressed = await store.CommitAsync(artifact, proof, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    // No source text, parser diagnostic or database parameter is logged.
                    logger.LogWarning("Code disclosure projection deferred after {FailureType}; canonical publication is unchanged.",
                        exception.GetType().Name);
                }
                await Task.Delay(progressed ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(15), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static CodeDisclosureProof Terminal(CodeDisclosureArtifact artifact, CodeDisclosureProofState state) =>
        new(artifact.ArtifactId, artifact.CanonicalHash, artifact.CanonicalLength, CodeDisclosureIntegrity.Fingerprint,
            state, [], CodeDisclosureIntegrity.ProofChecksum(artifact.ArtifactId, artifact.CanonicalHash,
                CodeDisclosureIntegrity.Fingerprint, state, artifact.CanonicalLength, []));
}
