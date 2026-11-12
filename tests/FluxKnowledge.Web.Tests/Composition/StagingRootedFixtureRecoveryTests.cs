using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Integration.Tests.Support;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

[Collection("sql-hosted-recovery")]
public sealed class StagingRootedFixtureRecoveryTests : IAsyncLifetime
{
    private readonly NativeSqlServerFixture _sql=new();
    public Task InitializeAsync()=>_sql.InitializeAsync();
    public Task DisposeAsync()=>_sql.DisposeAsync();

    [NativeSqlServerFact]
    public async Task Torn_original_receipt_can_recover_again_after_native_deletion_using_its_durable_binding()
    {
        var token=Guid.NewGuid().ToString("N");var operation=Guid.NewGuid();var rootId=Guid.NewGuid();
        var settingsRoot=@"I:\Codex Workspaces\Staging\FluxKnowledge\RecoveryCase-"+token;
        var evidence=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110";
        var output=Path.Combine(evidence,"indexing-fixture-unit-"+token);var window=Path.Combine(evidence,"window-unit-"+token);
        var settings=StagingHostSettings.Create(settingsRoot,"Server=localhost;Database=FluxKnowledge_Staging_RecoveryCase;User ID=stage;Password=synthetic","http://127.0.0.1:5147");
        var directory=Path.Combine(settings.SourcesRoot,"controlled-index-"+operation.ToString("N"));
        var created=DateTimeOffset.UtcNow.AddMinutes(-1);var birth=DateTimeOffset.UnixEpoch;const int exitedPid=int.MaxValue;
        var factory=new Factory(_sql.ConnectionString);
        try
        {
            Directory.CreateDirectory(output);Directory.CreateDirectory(window);
            File.WriteAllText(Path.Combine(output,"0001-before-intake.json"),JsonSerializer.Serialize(new
            {operation,createdUtc=created,directory,hostProcessId=exitedPid,hostStartedUtc=birth,baseline=new {Roots=Array.Empty<StagingFixtureRoot>(),Deletions=Array.Empty<StagingFixtureDeletion>()}}));
            const string torn="{\"rootId\":";
            File.WriteAllText(Path.Combine(output,"0002-root-created.json"),torn);
            File.WriteAllText(Path.Combine(window,"owned-host-started.json"),JsonSerializer.Serialize(new
            {id=exitedPid,startedUtc=birth,operationId=operation,assignedOwnedJob=true,noBreakaway=true,killOnLastJobHandleClose=true,activeProcessLimit=1}));
            File.WriteAllText(Path.Combine(window,"owned-host-settlement.json"),JsonSerializer.Serialize(new
            {id=exitedPid,startedUtc=birth,actualExitObserved=true,ownedJobEmpty=true}));
            await using(var db=factory.CreateDbContext())
            {
                db.SourceRootConfigurations.Add(new SourceRootConfigurationEntity
                {Id=rootId,CanonicalPath=directory,DisplayName="Controlled search indexing fixture",State=0,MaximumFileBytes=4*1024*1024,
                    ReconciliationCadenceSeconds=604800,ConfigurationRevision=1,CreatedAtUtc=created.AddSeconds(1),UpdatedAtUtc=created.AddSeconds(1)});
                await db.SaveChangesAsync();
            }
            await using(var first=new StagingRootedIndexFixture(new Scopes(factory),settings,()=>true))
                await first.RecoverAsync(output,window,default);
            // Reproduce the exact recovery metadata boundary after native deletion:
            // the root is gone, the operation is terminal, and input cleanup has not run.
            await using(var db=factory.CreateDbContext())
            {
                db.SourceRootConfigurations.Remove(await db.SourceRootConfigurations.SingleAsync(r=>r.Id==rootId));
                db.SourceDeletionOperations.Add(new SourceDeletionOperationEntity
                {Id=Guid.NewGuid(),SourceRootId=rootId,State=3,Phase="completed",CreatedAtUtc=created.AddSeconds(2),UpdatedAtUtc=created.AddSeconds(2)});
                await db.SaveChangesAsync();
            }
            await using(var second=new StagingRootedIndexFixture(new Scopes(factory),settings,()=>true))
                await second.RecoverAsync(output,window,default);
            var bindings=Directory.GetFiles(output,"*-recovered-after-exit.json");Assert.Equal(2,bindings.Length);
            foreach(var path in bindings)
            {
                using var receipt=JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal(rootId,receipt.RootElement.GetProperty("rootId").GetGuid());
                Assert.Equal(operation,receipt.RootElement.GetProperty("operation").GetGuid());
            }
            Assert.Equal(torn,File.ReadAllText(Path.Combine(output,"0002-root-created.json")));
        }
        finally
        {
            foreach(var owned in new[]{settingsRoot,output,window})
            {
                if(Path.GetFullPath(owned)!=owned || !owned.EndsWith(token,StringComparison.Ordinal))throw new InvalidOperationException("owned-recovery-test-cleanup-target");
                if(Directory.Exists(owned))Directory.Delete(owned,true);
            }
        }
    }

    private sealed class Factory(string connection):IDbContextFactory<FluxKnowledgeDbContext>
    {public FluxKnowledgeDbContext CreateDbContext()=>new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).Options);}
    private sealed class Scopes(Factory factory):IServiceScopeFactory
    {public IServiceScope CreateScope()=>new Scope(factory);}
    private sealed class Scope(Factory factory):IServiceScope,IServiceProvider
    {
        public IServiceProvider ServiceProvider=>this;
        public object? GetService(Type type)=>type==typeof(IDbContextFactory<FluxKnowledgeDbContext>)?factory:null;
        public void Dispose(){}
    }
}
