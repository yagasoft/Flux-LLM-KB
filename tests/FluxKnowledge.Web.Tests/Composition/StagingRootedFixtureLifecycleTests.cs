using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Web;
using FluxKnowledge.Web.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FluxKnowledge.Web.Tests.Composition;

public sealed class StagingRootedFixtureLifecycleTests
{
    [Fact]
    public async Task Disposal_joins_a_foreground_operation_and_concurrent_disposers_share_settlement()
    {
        var token=Guid.NewGuid().ToString("N");
        var settingsRoot=@"I:\Codex Workspaces\Staging\FluxKnowledge\FixtureLifecycle-"+token;
        var output=@"I:\Codex Workspaces\Operational Evidence\FluxKnowledge\scoped-semantic-search-20261029\staging-expanded-gpu-api-20261110\indexing-fixture-unit-"+token;
        var factory=new BlockingFactory();
        try
        {
            Directory.CreateDirectory(Path.Combine(settingsRoot,"Config"));
            var path=Path.Combine(settingsRoot,"Config","staging.json");
            File.WriteAllText(path,JsonSerializer.Serialize(new {SqlConnectionString="Server=localhost;Database=FluxKnowledge_Staging_FixtureLifecycle;User ID=stage;Password=synthetic",Origin="http://127.0.0.1:5147",CpuSemantic=true,DashboardReadBenchmark=true,IndexPublicationMode="Segmented",CpuRerankerThreads=1}));
            var fixture=new StagingRootedIndexFixture(new Scopes(factory),StagingHostSettings.Load(path),()=>true);
            var foreground=fixture.PrepareAsync(Guid.NewGuid(),output,default);
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued=fixture.PreparePublicationAsync(default);
            var first=fixture.DisposeAsync().AsTask();var second=fixture.DisposeAsync().AsTask();
            Assert.Same(first,second);Assert.False(first.IsCompleted);
            factory.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(()=>foreground);
            await Assert.ThrowsAsync<ObjectDisposedException>(()=>queued);
            await first;
            await Assert.ThrowsAsync<ObjectDisposedException>(()=>fixture.PrepareAsync(Guid.NewGuid(),output+"-closed",default));
            Assert.False(Directory.Exists(output+"-closed"));
        }
        finally
        {
            factory.Release.TrySetResult();
            foreach(var owned in new[]{settingsRoot,output})
            {
                var absolute=Path.GetFullPath(owned);
                if(absolute!=owned || !absolute.EndsWith(token,StringComparison.Ordinal))throw new InvalidOperationException("owned-lifecycle-test-cleanup-target");
                if(Directory.Exists(absolute))Directory.Delete(absolute,true);
            }
        }
    }

    private sealed class BlockingFactory : IDbContextFactory<FluxKnowledgeDbContext>
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FluxKnowledgeDbContext CreateDbContext()=>throw new NotSupportedException();
        public async Task<FluxKnowledgeDbContext> CreateDbContextAsync(CancellationToken ct=default)
        {Entered.TrySetResult();await Release.Task;throw new IOException("synthetic-before-any-SQL");}
    }
    private sealed class Scopes(BlockingFactory factory) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()=>new Scope(factory);
    }
    private sealed class Scope(BlockingFactory factory) : IServiceScope,IAsyncDisposable,IServiceProvider
    {
        public IServiceProvider ServiceProvider=>this;
        public object? GetService(Type type)=>type==typeof(IDbContextFactory<FluxKnowledgeDbContext>)?factory:null;
        public void Dispose(){}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
