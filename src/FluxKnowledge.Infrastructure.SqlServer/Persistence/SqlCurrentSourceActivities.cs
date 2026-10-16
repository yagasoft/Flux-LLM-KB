using FluxKnowledge.Application.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence;

/// <summary>Current status excludes a policy predecessor only when its exact retained binding has a recognised durable successor.</summary>
public static class SqlCurrentSourceActivities
{
    public static IQueryable<SourceActivityEntity> Query(FluxKnowledgeDbContext context) =>
        context.SourceActivities.AsNoTracking()
        .Where(activity => activity.State != (int)FluxKnowledge.Domain.Sources.SourceActivityState.CancelledSuperseded || activity.Reason != RetainedDefaultPolicy.RelationshipKind)
        .Where(activity => !context.SourceActivityRelations.Any(relation =>
            relation.PredecessorActivityId == activity.Id && relation.RelationshipKind == RetainedDefaultPolicy.RelationshipKind &&
            relation.ReasonCode == RetainedDefaultPolicy.Version && context.SourceActivities.Any(successor =>
                successor.Id == relation.SuccessorActivityId && successor.SourceRevisionId == activity.SourceRevisionId &&
                successor.InputFingerprint == activity.InputFingerprint &&
                (successor.ProcessorVersion == RetainedDefaultPolicy.Version && successor.DescriptorFingerprint == RetainedDefaultPolicy.TextFingerprint ||
                 successor.ProcessorVersion == RetainedDefaultPolicy.UnsupportedBinary.ProcessorVersion && successor.DescriptorFingerprint == RetainedDefaultPolicy.UnsupportedBinary.ProcessorFingerprint ||
                 successor.ProcessorVersion == OoxmlStructuralTextProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == OoxmlStructuralTextProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == MediaMetadataRetainedProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == MediaMetadataRetainedProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == ZipArchiveRetainedProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == ZipArchiveRetainedProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == TarArchiveRetainedProcessor.Capability.ProcessorVersion && successor.DescriptorFingerprint == TarArchiveRetainedProcessor.Capability.ProcessorFingerprint ||
                 successor.ProcessorVersion == RetainedCsharpCodeProcessor.ProcessorVersion && successor.DescriptorFingerprint == RetainedCsharpCodeProcessor.Capability.ProcessorFingerprint))));
}
