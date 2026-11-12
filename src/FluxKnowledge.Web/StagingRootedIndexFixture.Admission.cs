using System.Security.Cryptography;
using System.Text;
using FluxKnowledge.Application.Ports;
using FluxKnowledge.Application.Sources;
using FluxKnowledge.Domain.Sources;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Workers;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal sealed partial class StagingRootedIndexFixture
{
    private async Task AdmitScannedSourcesAsync(IServiceProvider provider,string directory,CancellationToken ct)
    {
        var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
        await ValidateClosedAsync(factory,ct);
        var admission=provider.GetRequiredService<SourceWorkAdmission>();
        if(admission.ReadAdmittedRoots().Count!=0)throw new InvalidOperationException("fixture-requires-closed-source-admission");
        await using var db=await factory.CreateDbContextAsync(ct);
        var revisions=await db.SourceRevisions.AsNoTracking().Where(r=>r.SourceRootId==_root).OrderBy(r=>r.Id).Take(3).ToArrayAsync(ct);
        var expected=new[]{("backlog.txt",2048),("publication.txt",32)}.ToDictionary(x=>x.Item1,x=>Encoding.UTF8.GetBytes(Source(_operation,x.Item2)));
        if(revisions.Length!=2 || !revisions.Select(r=>Path.GetFileName(r.CanonicalPath)).Order().SequenceEqual(expected.Keys.Order()) ||
            revisions.Any(r=>r.OriginKind!=0 || r.Revision!=1 || r.DiscoveredAtUtc<_created || r.SuppressedAtUtc is not null ||
                r.Classification!="AcceptedUtf8Text" || !string.Equals(r.CanonicalPath,Path.Combine(directory,Path.GetFileName(r.CanonicalPath)),StringComparison.OrdinalIgnoreCase) ||
                !expected.TryGetValue(Path.GetFileName(r.CanonicalPath),out var bytes) || r.ByteLength!=bytes.Length ||
                !string.Equals(r.ContentSha256,Convert.ToHexStringLower(SHA256.HashData(bytes)),StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("exact-owned-retained-sources-required");
        var ids=revisions.Select(r=>r.Id).ToArray();
        var activities=await db.SourceActivities.AsNoTracking().Where(a=>ids.Contains(a.SourceRevisionId)).OrderBy(a=>a.Id).Take(3).ToArrayAsync(ct);
        if(activities.Length!=2 || activities.Select(a=>a.SourceRevisionId).Distinct().Count()!=2 ||
            activities.Any(a=>a.CreatedAtUtc<_created || a.State!=(int)SourceActivityState.Pending || a.ExecutionClass!=(int)ExecutionClass.InProcess ||
                a.ActivityKind!=(int)SourceActivityKind.TextExtraction || a.ProcessorVersion!=SourceScanWorker.TextProcessorVersion ||
                a.ResultingPipelineRecordId is not null || a.InputFingerprint!=revisions.Single(r=>r.Id==a.SourceRevisionId).ContentSha256) ||
            await db.PipelineRecords.AnyAsync(r=>r.SourceRevisionId!=null && ids.Contains(r.SourceRevisionId.Value),ct))
            throw new InvalidOperationException("exact-unlinked-fixture-activities-required");
        // Benchmark composition intentionally starts no recovery worker. Reuse the native
        // per-source physical gate; never run global recovery or offer foreign activities.
        SourceWorkAdmittedRoot? binding=null;
        foreach(var revision in revisions)
        {
            var verified=await SourceCommandAdmission.PrepareAsync(factory,new(revision.Id),revision.ContentSha256,settings.Layout.RetainedRoot,ct);
            var current=verified.ReadAdmittedRoots().Single();
            if(current.RootId!=_root || binding is not null && current!=binding)
                throw new InvalidOperationException("fixture-source-admission-binding-changed");
            binding=current;
        }
        await ValidateClosedAsync(factory,ct);
        if(await StampAsync(factory,ct)!=_stamp || binding is null || admission.ReadAdmittedRoots().Count!=0 ||
            !await db.SourceRootConfigurations.AsNoTracking().AnyAsync(r=>r.Id==binding.RootId &&
                r.ConfigurationRevision==binding.ConfigurationRevision && r.State==(int)SourceRootState.Enabled && r.CanonicalPath==directory,ct))
            throw new InvalidOperationException("fixture-source-admission-authority-changed");
        admission.Admit(binding.RootId,binding.ConfigurationRevision);
        Journal("source-admission",new {rootId=_root,binding,unlinkedActivities=activities.Length,recordsBeforeAdmission=0,foreignRootsAdmitted=0});
        var planner=provider.GetRequiredService<RetainedTextActivityPlanner>();
        foreach(var row in activities)
        {
            var activity=SourceActivity.Restore(new(row.Id),new(row.SourceRevisionId),(SourceActivityKind)row.ActivityKind,
                (ExecutionClass)row.ExecutionClass,row.ProcessorVersion,row.InputFingerprint,row.RequiredCapability,
                (SourceActivityState)row.State,row.Reason,descriptorFingerprint:row.DescriptorFingerprint);
            if(!await planner.PlanAsync(activity,ct))throw new InvalidOperationException("native-fixture-registration-refused");
        }
    }
}
