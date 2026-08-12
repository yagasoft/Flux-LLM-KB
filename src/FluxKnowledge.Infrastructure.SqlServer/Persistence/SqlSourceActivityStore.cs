using FluxKnowledge.Application.Contracts;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

public sealed class SqlSourceActivityStore(
    IDbContextFactory<FluxKnowledgeDbContext> contextFactory,
    TimeProvider timeProvider) : ISourceActivityStore, ISourceCapabilityStore
{
    public async ValueTask<SourceActivity> FindOrCreateAsync(
        SourceActivityDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var writer = new SqlSourceActivityWriter(timeProvider);
        var activity = await writer.FindOrCreateAsync(context, draft, cancellationToken).ConfigureAwait(false);
        if (context.ChangeTracker.HasChanges())
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                context.ChangeTracker.Clear();
                var concurrent = await SqlSourceActivityWriter.FindExistingAsync(context, draft, cancellationToken).ConfigureAwait(false);
                if (concurrent is null)
                {
                    throw;
                }

                return SqlSourceActivityWriter.Restore(concurrent);
            }
        }
        return activity;
    }

    public async ValueTask<RegisteredSourceCapability> RegisterAsync(
        RegisteredSourceCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var runnable = capability.ExecutionClass == ExecutionClass.InProcess && capability.IsRunnable;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.SourceCapabilities.SingleOrDefaultAsync(value => value.Id == capability.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.ProcessorKind, capability.ProcessorKind, StringComparison.Ordinal) ||
                !string.Equals(existing.ProcessorVersion, capability.ProcessorVersion, StringComparison.Ordinal) ||
                existing.ExecutionClass != (int)capability.ExecutionClass ||
                !string.Equals(existing.ProcessorFingerprint, capability.ProcessorFingerprint, StringComparison.Ordinal) ||
                !string.Equals(existing.OutputContract, capability.OutputContract, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A source capability id cannot be rebound to a different descriptor.");
            }

            var acceptedClassifications = ToAcceptedClassificationsJson(capability.AcceptedClassification);
            if (!string.Equals(existing.AcceptedClassificationsJson, acceptedClassifications, StringComparison.Ordinal) ||
                existing.IsRunnable != runnable)
            {
                // Capability registration is the owner of runtime availability.  Keeping this
                // current repairs pre-force registrations that predate explicit OOXML classification.
                existing.AcceptedClassificationsJson = acceptedClassifications;
                existing.IsRunnable = runnable;
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return ToCapability(existing);
        }

        var now = timeProvider.GetUtcNow();
        context.SourceCapabilities.Add(new SourceCapabilityEntity
        {
            Id = capability.Id,
            ProcessorKind = capability.ProcessorKind,
            ProcessorVersion = capability.ProcessorVersion,
            ExecutionClass = (int)capability.ExecutionClass,
            AcceptedClassificationsJson = ToAcceptedClassificationsJson(capability.AcceptedClassification),
            OutputContract = capability.OutputContract,
            ProcessorFingerprint = capability.ProcessorFingerprint,
            IsRunnable = runnable,
            RegisteredBy = "local-process",
            RegisteredAtUtc = now
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return capability with { IsRunnable = runnable };
    }

    public async ValueTask<RegisteredSourceCapability?> FindAsync(Guid capabilityId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var capability = await context.SourceCapabilities.AsNoTracking().SingleOrDefaultAsync(value => value.Id == capabilityId, cancellationToken).ConfigureAwait(false);
        return capability is null ? null : ToCapability(capability);
    }

    private static string ToAcceptedClassificationsJson(string acceptedClassification) =>
        acceptedClassification == "AcceptedUtf8Text" ? "[\"text/plain\"]" :
        $"[\"{acceptedClassification}\"]";

    private static RegisteredSourceCapability ToCapability(SourceCapabilityEntity value) => new(
        value.Id,
        value.ProcessorKind,
        value.ProcessorVersion,
        (ExecutionClass)value.ExecutionClass,
        value.ProcessorFingerprint,
        value.IsRunnable,
        SourceActivityKind.TextExtraction,
        value.AcceptedClassificationsJson == "[\"text/plain\"]" ? "AcceptedUtf8Text" :
        value.AcceptedClassificationsJson.StartsWith("[\"", StringComparison.Ordinal) && value.AcceptedClassificationsJson.EndsWith("\"]", StringComparison.Ordinal)
            ? value.AcceptedClassificationsJson[2..^2] : "",
        value.OutputContract);
}
