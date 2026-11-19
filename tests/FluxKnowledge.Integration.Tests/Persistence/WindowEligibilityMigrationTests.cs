using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Integration.Tests.Indexing;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Diagnostics;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Persistence;

[Collection("sql-full-text")]
public sealed class WindowEligibilityMigrationTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Exact_typed_deployment_preserves_canonical_data_and_never_backfills_in_DDL()
    {
        await using var environment=await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A canonical migration control.",segmented:true);
        await environment.AddRetainedAndPumpAsync("An independently owned retained migration control.");
        await using var db=environment.Factory.CreateDbContext();
        await SqlSourceDeletionPendingQueueIndex.EnsureAsync(db,default);
        var artifacts=await db.Artifacts.OrderBy(value => value.Id).Select(value => new {value.Id,value.ContentHash,value.SearchText}).ToArrayAsync();
        var chunks=await db.TextChunks.OrderBy(value => value.Id).Select(value => new {value.Id,value.ArtifactId,value.Content,value.ContentHash}).ToArrayAsync();
        var vectors=await db.Vectors.OrderBy(value => value.VectorId).Select(value => new {value.VectorId,value.TextChunkId,value.PayloadChecksum}).ToArrayAsync();
        var publications=await db.DocumentPublications.OrderBy(value => value.PipelineRecordId).Select(value => new {value.PipelineRecordId,value.OwnerSourceRevisionId}).ToArrayAsync();
        Assert.NotEmpty(chunks);
        var migrations=db.Database.GetMigrations().ToArray();
        var position=Array.FindIndex(migrations,value => value.EndsWith("_PersistPassageWindowEligibility",StringComparison.Ordinal));
        Assert.True(position>0);
        var area=Path.Combine(@"I:\Codex Workspaces\Staging\FluxKnowledge","WindowEligibilitySql-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            await db.GetService<IMigrator>().MigrateAsync(migrations[position-1]);
            var source=FluxKnowledge.Testing.RepositoryFiles.Root();
            var start=new ProcessStartInfo("pwsh") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[] {"-NoProfile","-File",Path.Combine(source,"tests/native/passage-window-eligibility-migration-sql.ps1"),"-SourceRoot",source,"-ReceiptDirectory",area})
                start.ArgumentList.Add(argument);
            start.Environment["FLUXKNOWLEDGE_WINDOW_ELIGIBILITY_DISPOSABLE_SQL"]=fixture.ConnectionString;
            using var process=Process.Start(start)!;
            var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try{await process.WaitForExitAsync(deadline.Token);}catch{process.Kill(true);await process.WaitForExitAsync();throw;}
            Assert.True(process.ExitCode==0,await output+await errors);
            Assert.Equal(artifacts,await db.Artifacts.OrderBy(value => value.Id).Select(value => new {value.Id,value.ContentHash,value.SearchText}).ToArrayAsync());
            Assert.Equal(chunks,await db.TextChunks.OrderBy(value => value.Id).Select(value => new {value.Id,value.ArtifactId,value.Content,value.ContentHash}).ToArrayAsync());
            Assert.Equal(vectors,await db.Vectors.OrderBy(value => value.VectorId).Select(value => new {value.VectorId,value.TextChunkId,value.PayloadChecksum}).ToArrayAsync());
            Assert.Equal(publications,await db.DocumentPublications.OrderBy(value => value.PipelineRecordId).Select(value => new {value.PipelineRecordId,value.OwnerSourceRevisionId}).ToArrayAsync());
            Assert.Empty(await db.TextChunkWindowEligibilities.ToArrayAsync());
            Assert.Empty(await db.TextChunkWindowBackfillStates.ToArrayAsync());
        }
        finally{await db.Database.MigrateAsync();}
    }
}
