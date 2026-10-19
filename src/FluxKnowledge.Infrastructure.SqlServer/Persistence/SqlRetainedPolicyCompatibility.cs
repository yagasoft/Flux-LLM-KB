using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Read-only activation fence. An ordinary update must not strand old runnable processor work.</summary>
public static class SqlRetainedPolicyCompatibility
{
    public static async ValueTask<bool> HasNoLegacyRunnableWorkAsync(IDbContextFactory<FluxKnowledgeDbContext> factory, CancellationToken token)
    {
        string[] legacy = [OoxmlStructuralTextProcessor.LegacyProcessorFingerprint, OoxmlStructuralTextProcessor.PreviousProcessorFingerprint, OoxmlStructuralTextProcessor.Version3ProcessorFingerprint,
            MediaMetadataRetainedProcessor.LegacyProcessorFingerprint,
            ZipArchiveRetainedProcessor.LegacyProcessorFingerprint, "phase-5-zip-retained-archive-v1", TarArchiveRetainedProcessor.LegacyProcessorFingerprint,
            RetainedCsharpCodeProcessor.LegacyDescriptorFingerprint];
        await using var db = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
        if (await db.SourceProcessorBranches.AsNoTracking().AnyAsync(value => legacy.Contains(value.ProcessorFingerprint) &&
                (value.State == (int)RetainedProcessorBranchState.Pending || value.State == (int)RetainedProcessorBranchState.Running), token)) return false;
        if (await db.SourceActivities.AsNoTracking().AnyAsync(value => legacy.Contains(value.DescriptorFingerprint) &&
                value.ExecutionClass == (int)ExecutionClass.InProcess &&
                (value.State == (int)SourceActivityState.Pending || value.State == (int)SourceActivityState.Running) &&
                !db.SourceProcessorBranches.Any(branch => branch.SourceActivityId == value.Id), token)) return false;
        return !await db.SourceProcessorForceRequests.AsNoTracking().AnyAsync(value => legacy.Contains(value.DescriptorFingerprint) &&
            (value.State == (byte)OoxmlForceRequestState.Requested || value.State == (byte)OoxmlForceRequestState.Claimed), token);
    }
}
