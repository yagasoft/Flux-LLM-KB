using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;

namespace FluxKnowledge.Web;

internal static class StagingLegacyFixtureCleanup
{
    internal static async Task<object> RunAsync(IServiceProvider provider,StagingHostSettings settings,
        StagingRootedIndexFixture fixture,string planPath,CancellationToken token)
    {
        var full=Path.GetFullPath(planPath);
        const string evidence=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\";
        if(!full.StartsWith(evidence,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("owned-legacy-cleanup-plan-required");
        for(var part=full;!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
            if((File.Exists(part)||Directory.Exists(part)) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("linked-legacy-cleanup-plan");
        using var input=new FileStream(PhysicalFileIdentity.OpenReadNoFollow(full),FileAccess.Read);
        if(input.Length is <1 or >2097152)throw new InvalidOperationException("legacy-cleanup-plan-byte-bound");
        var plan=await JsonSerializer.DeserializeAsync<LegacyStagingRecoveryPlan>(input,new JsonSerializerOptions
        {UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=32},token)
            ??throw new InvalidOperationException("legacy-cleanup-plan-invalid");
        if(plan.Scope.Catalogue!=settings.CatalogName || !Same(plan.Scope.RecoveryRoot,settings.Layout.RecoveryRoot) || !Same(plan.Scope.IndexRoot,settings.Layout.IndexRoot))
            throw new InvalidOperationException("legacy-cleanup-plan-settings-mismatch");
        fixture.RequireRecoveredLegacyScope(plan.Scope);
        var factory=provider.GetRequiredService<IDbContextFactory<FluxKnowledgeDbContext>>();
        Console.WriteLine("Preparing bounded, hash-held legacy staging recovery proof; no model or HTTP startup.");
        using var preparation=CancellationTokenSource.CreateLinkedTokenSource(token);preparation.CancelAfter(TimeSpan.FromMinutes(8));
        var proof=await LegacyStagingRecoveryProof.CreateAsync(factory,plan,preparation.Token);
        var audit=new SqlIndexRecoveryReferences(factory,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        Exception? primary=null;var failures=new List<Exception>();object? result=null;var auditAttempted=false;
        try
        {
            auditAttempted=true;
            var captured=await audit.AuditLegacyStagingAsync(proof,token);
            Console.WriteLine(JsonSerializer.Serialize(new{phase="target-restricted-audit-sealed",captured.InventoryChecksum,pins=captured.Pins.Count}));
            var retirement=new SqlSegmentRetirementOperations(factory,new WindowsIndexProjectionFileStore(settings.Layout.IndexRoot),
                settings.Layout.IndexRoot,settings.Layout.RecoveryRoot,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe(),proof);
            result=await fixture.CleanupAsync(token,retirement);
        }
        catch(Exception error){primary=error;}
        finally
        {
            if(auditAttempted)
            {
                try
                {
                    using var invalidation=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await using var writer=await audit.BeginWriteAsync(settings.Layout.RecoveryRoot,invalidation.Token,ownerExitSufficient:true);
                    await writer.CompleteAsync(invalidation.Token);
                    Console.WriteLine("Target-restricted SQL seal invalidated through native recovery writer protocol.");
                }
                catch(Exception error){failures.Add(error);}
            }
            try{await proof.DisposeAsync();}catch(Exception error){failures.Add(error);}
        }
        if(primary is not null)
        {
            if(failures.Count!=0)primary.Data["LegacyCleanupSettlementFailures"]=new AggregateException(failures);
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
        if(failures.Count!=0)throw new AggregateException(failures);
        return result??throw new InvalidOperationException("legacy-cleanup-result-not-observed");
    }
    private static bool Same(string left,string right)=>string.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);
}
