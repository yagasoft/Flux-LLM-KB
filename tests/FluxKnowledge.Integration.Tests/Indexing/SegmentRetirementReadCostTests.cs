using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Persistence.Entities;
using FluxKnowledge.Infrastructure.Usearch;
using FluxKnowledge.Integrations.Windows;
using FluxKnowledge.Integrations.Files;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace FluxKnowledge.Integration.Tests.Indexing;

[Collection("sql-full-text")]
public sealed class SegmentRetirementReadCostTests(NativeSqlServerFixture fixture, ITestOutputHelper output) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hundreds_of_shared_segments_use_bounded_commands_and_preserve_only_unshared_retirement(bool caseVariant)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "An original shared segment.", segmented: true);
        var previous = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("A separate current keeper.", "keeper.txt");
        var current = await environment.ActiveGenerationAsync();
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, default);
        await using var setup = environment.Factory.CreateDbContext();
        var templateId = await setup.IndexGenerationSegments.Where(value => value.GenerationId == previous.Id).Select(value => value.SegmentId).SingleAsync();
        var template = await setup.IndexSegments.AsNoTracking().SingleAsync(value => value.Id == templateId);
        var shared = Enumerable.Range(0, 513).Select(number => Hash("shared-" + number)).ToArray();
        var unshared = Enumerable.Range(0, 2).Select(number => Hash("unshared-" + number)).ToArray();
        var keeper = Guid.NewGuid();
        setup.IndexGenerations.Add(new() { Id = keeper, FormatVersion = 2, SegmentCount = 514, CorpusEpoch = previous.CorpusStamp!.CorpusEpoch,
            CorpusVersion = previous.CorpusStamp.CorpusVersion, ModelFingerprint = previous.ModelFingerprint, Dimensions = previous.Dimensions,
            IndexPath = Path.Combine(environment.IndexRoot,"generations",keeper.ToString("N")), MetadataChecksum = previous.MetadataChecksum,
            VectorCount = previous.VectorCount, CreatedAtUtc = DateTimeOffset.UtcNow });
        foreach (var id in shared.Concat(unshared)) setup.IndexSegments.Add(new()
        {
            Id = id, ModelFingerprint = template.ModelFingerprint, Dimensions = template.Dimensions, NativeFormat = template.NativeFormat,
            DirectoryPath = Path.Combine(environment.IndexRoot, "segments", id), MembershipChecksum = template.MembershipChecksum,
            FileChecksum = template.FileChecksum, FileLength = template.FileLength, VectorCount = template.VectorCount,
            State = 1, CreatedAtUtc = template.CreatedAtUtc, SealedAtUtc = template.SealedAtUtc
        });
        await setup.SaveChangesAsync();
        var ordinal = 10;
        foreach (var id in shared.Concat(unshared)) setup.IndexGenerationSegments.Add(new()
        { GenerationId = previous.Id, SegmentId = caseVariant && shared.Contains(id) ? id.ToUpperInvariant() : id, Ordinal = ordinal++ });
        setup.IndexGenerationSegments.Add(new() { GenerationId = keeper, SegmentId = templateId, Ordinal = 0 });
        ordinal = 10;
        foreach (var id in shared) setup.IndexGenerationSegments.Add(new() { GenerationId = keeper, SegmentId = id, Ordinal = ordinal++ });
        await setup.SaveChangesAsync();
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @copies TABLE(Id uniqueidentifier);
            INSERT @copies SELECT TOP(50) NEWID() FROM sys.all_objects;
            INSERT IndexGenerations(Id,FormatVersion,SegmentCount,CorpusEpoch,CorpusVersion,ModelFingerprint,Dimensions,IndexPath,MetadataChecksum,VectorCount,CreatedAtUtc)
            SELECT copies.Id,2,source.SegmentCount,source.CorpusEpoch,source.CorpusVersion,source.ModelFingerprint,source.Dimensions,
                CONCAT({environment.IndexRoot},N'\generations\',REPLACE(CONVERT(nvarchar(36),copies.Id),N'-',N'')),source.MetadataChecksum,source.VectorCount,SYSDATETIMEOFFSET()
            FROM @copies copies CROSS JOIN IndexGenerations source WHERE source.Id={keeper};
            INSERT IndexGenerationSegments(GenerationId,SegmentId,Ordinal)
            SELECT copies.Id,member.SegmentId,member.Ordinal FROM @copies copies CROSS JOIN IndexGenerationSegments member WHERE member.GenerationId={keeper};
            """);
        var recovery = Path.Combine(environment.IndexRoot, "shared-retirement-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment, recovery);
        var commands = new Commands();
        var factory = new Factory(fixture.ConnectionString, commands);
        var operations = new SqlSegmentRetirementOperations(factory, new WindowsIndexProjectionFileStore(environment.IndexRoot),
            environment.IndexRoot, recovery, TimeProvider.System, new WindowsInteractiveGpuOwnerProbe());
        var operation = Guid.NewGuid();
        var plan = await operations.MarkManifestAsync(previous.Id, operation, default);
        Assert.Equal(unshared.Order(), plan.Segments.Select(value => value.Id).Order());
        Assert.Equal(0, await setup.IndexGenerationSegments.CountAsync(value => value.GenerationId == previous.Id));
        Assert.Equal(1, await setup.IndexSegments.Where(value => value.Id == templateId).Select(value => value.State).SingleAsync());
        Assert.All(await setup.IndexSegments.Where(value => shared.Contains(value.Id)).AsNoTracking().ToArrayAsync(), value => Assert.Equal(1, value.State));
        Assert.Equal(514, await setup.IndexGenerationSegments.CountAsync(value => value.GenerationId == keeper));
        Assert.Equal(current.Id, (await environment.ActiveGenerationAsync()).Id);
        var commandCount = commands.Count;
        await using var replay = environment.Factory.CreateDbContext();
        await replay.Database.OpenConnectionAsync();
        var connection = (SqlConnection)replay.Database.GetDbConnection();
        var messages = new List<string>(); connection.InfoMessage += (_, item) => messages.Add(item.Message);
        await replay.Database.ExecuteSqlRawAsync("SET STATISTICS IO ON; SET STATISTICS XML ON;");
        long referenceRows = 0;
        foreach (var captured in commands.SharedReads)
        {
            await using var command = connection.CreateCommand(); command.CommandText = captured.Sql;
            foreach (var parameter in captured.Parameters) command.Parameters.Add(((ICloneable)parameter).Clone());
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                    if (reader.FieldCount == 1 && reader.GetValue(0) is string xml && xml.StartsWith("<ShowPlanXML", StringComparison.Ordinal))
                    {
                        var planXml = XDocument.Parse(xml); XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
                        var access = planXml.Descendants(ns + "RelOp").Where(op => op.Elements().Any(element => (string?)element.Element(ns + "Object")?.Attribute("Table") == "[IndexGenerationSegments]"));
                        referenceRows += access.Sum(op => op.Descendants(ns + "RunTimeCountersPerThread").Sum(counter => long.Parse((string?)counter.Attribute("ActualRowsRead") ?? (string?)counter.Attribute("ActualRows") ?? "0", CultureInfo.InvariantCulture)));
                    }
            } while (await reader.NextResultAsync());
        }
        var reads = messages.SelectMany(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+),"))
            .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        await replay.Database.ExecuteSqlRawAsync("SET STATISTICS XML OFF; SET STATISTICS IO OFF;");
        output.WriteLine($"manifest_shared_segments=514 case_variant={caseVariant} commands={commandCount} reference_reads={reads} reference_rows_read={referenceRows}");
        Assert.InRange(reads, 1, 514 * 30);
        Assert.InRange(referenceRows, 1, 514 * 8);
        Assert.InRange(commandCount, 1, 100);
        var again = await operations.MarkManifestAsync(previous.Id, operation, default);
        Assert.Equal(plan.Segments.Select(value => value.Id), again.Segments.Select(value => value.Id));
    }

    [NativeSqlServerTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_detachment_rolls_back_or_replays_the_committed_exact_operation(bool committed)
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture, "An interrupted manifest.", segmented: true);
        var previous = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("A replacement remains current.", "initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true, default);
        var recovery = Path.Combine(environment.IndexRoot,"interrupted-shared-recovery"); Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment,recovery);
        var beforeRead = new OnShared((_,_) => throw new OperationCanceledException("Simulated interruption after detachment."));
        var afterCommit = new LostAcknowledgement();
        var factory = committed ? new Factory(fixture.ConnectionString,afterCommit) : new Factory(fixture.ConnectionString,beforeRead);
        var operation = Guid.NewGuid();
        var store = new SqlSegmentRetirementOperations(factory,new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MarkManifestAsync(previous.Id,operation,default).AsTask());
        await using var check = environment.Factory.CreateDbContext();
        var row = await check.IndexGenerations.AsNoTracking().SingleAsync(value => value.Id == previous.Id);
        Assert.Equal(committed ? operation : (Guid?)null,row.RetirementOperationId);
        Assert.Equal(committed ? 0 : 1,await check.IndexGenerationSegments.CountAsync(value => value.GenerationId == previous.Id));
        var clean = new SqlSegmentRetirementOperations(environment.Factory,new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        var first = await clean.MarkManifestAsync(previous.Id,operation,default);
        var second = await clean.MarkManifestAsync(previous.Id,operation,default);
        Assert.Equal(first.Segments.Select(value => value.Id),second.Segments.Select(value => value.Id));
        Assert.True(File.Exists(Path.Combine(previous.IndexPath,"manifest.json")));
    }

    [NativeSqlServerFact]
    public async Task A_missing_unshared_row_still_refuses_and_rolls_back_the_detachment()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"A missing row must not be skipped.",segmented:true);
        var previous = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("The current replacement survives.","initial.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        await using var setup = environment.Factory.CreateDbContext();
        var id = await setup.IndexGenerationSegments.Where(value => value.GenerationId == previous.Id).Select(value => value.SegmentId).SingleAsync();
        var recovery = Path.Combine(environment.IndexRoot,"missing-shared-recovery");Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment,recovery);
        var injection = new OnShared(async (command,ct) =>
        {
            await using var mutation = command.Connection!.CreateCommand();mutation.Transaction=command.Transaction;
            mutation.CommandText="DELETE IndexSegmentVectors WHERE SegmentId=@id; DELETE IndexSegments WHERE Id=@id;";
            var parameter=mutation.CreateParameter();parameter.ParameterName="@id";parameter.DbType=DbType.AnsiStringFixedLength;parameter.Size=64;parameter.Value=id;mutation.Parameters.Add(parameter);
            await mutation.ExecuteNonQueryAsync(ct);
        });
        var store = new SqlSegmentRetirementOperations(new Factory(fixture.ConnectionString,injection),new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkManifestAsync(previous.Id,Guid.NewGuid(),default).AsTask());
        Assert.True(injection.Fired);
        Assert.True(await setup.IndexSegments.AnyAsync(value => value.Id==id));
        Assert.True(await setup.IndexSegmentVectors.AnyAsync(value => value.SegmentId==id));
        Assert.True(await setup.IndexGenerationSegments.AnyAsync(value => value.GenerationId==previous.Id && value.SegmentId==id));
    }

    [NativeSqlServerFact]
    public async Task A_competing_publication_cannot_enter_between_detachment_and_shared_reference_capture()
    {
        await using var environment = await SqlToUsearchRebuildTests.PipelineEnvironment.CreateAsync(fixture,"The publication fence covers shared lookup.",segmented:true);
        var previous = await environment.ActiveGenerationAsync();
        await environment.AddAndPumpAtPathAsync("A second current keeper.","keeper.txt");
        await environment.Services.GetRequiredService<UsearchSegmentCache>().SweepAsync(true,default);
        var recovery=Path.Combine(environment.IndexRoot,"competing-shared-recovery");Directory.CreateDirectory(recovery);
        await SqlToUsearchRebuildTests.AuditRecoveryAsync(environment,recovery);
        var contender = new OnShared(async (_,ct) =>
        {
            await using var other=environment.Factory.CreateDbContext();other.Database.SetCommandTimeout(1);
            await using var transaction=await other.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
            var failure=await Assert.ThrowsAsync<SqlException>(() => SqlPublishedPassageSelection.AcquireFenceAsync(other,ct));
            Assert.True(failure.Number is -2 or 1222);
        });
        var store=new SqlSegmentRetirementOperations(new Factory(fixture.ConnectionString,contender),new WindowsIndexProjectionFileStore(environment.IndexRoot),environment.IndexRoot,recovery,TimeProvider.System,new WindowsInteractiveGpuOwnerProbe());
        await store.MarkManifestAsync(previous.Id,Guid.NewGuid(),default);
        Assert.True(contender.Fired);
        await using var after=environment.Factory.CreateDbContext();
        await using var owned=await after.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlPublishedPassageSelection.AcquireFenceAsync(after,default);
        await owned.CommitAsync();
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Captured(string Sql, DbParameter[] Parameters);
    private sealed class Commands : DbCommandInterceptor
    {
        internal int Count { get; private set; }
        internal List<Captured> SharedReads { get; } = [];
        private void Record(DbCommand command)
        {
            Count++;
            if (command.CommandText.Contains("segment-retirement-shared-segments", StringComparison.Ordinal) ||
                command.CommandText.Contains("[IndexGenerationSegments]", StringComparison.Ordinal) && command.CommandText.Contains("EXISTS", StringComparison.Ordinal) &&
                command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is string text && text.Length == 64))
                SharedReads.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>().Select(value => (DbParameter)((ICloneable)value).Clone()).ToArray()));
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Record(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) { Record(command); return ValueTask.FromResult(result); }
    }
    private sealed class OnShared(Func<DbCommand,CancellationToken,Task> action) : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(!Fired && command.CommandText.Contains("segment-retirement-shared-segments",StringComparison.Ordinal))
            {Fired=true;await action(command,cancellationToken);}
            return result;
        }
    }
    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        private bool _fired;
        public override Task TransactionCommittedAsync(DbTransaction transaction,TransactionEndEventData data,CancellationToken cancellationToken=default)
        {
            if(!_fired){_fired=true;throw new OperationCanceledException("The committed acknowledgement was lost.");}
            return Task.CompletedTask;
        }
    }
    private sealed class Factory(string connection, params IInterceptor[] commands) : IDbContextFactory<FluxKnowledgeDbContext>
    {
        public FluxKnowledgeDbContext CreateDbContext() => new(new DbContextOptionsBuilder<FluxKnowledgeDbContext>().UseSqlServer(connection).AddInterceptors(commands).Options);
    }
}
