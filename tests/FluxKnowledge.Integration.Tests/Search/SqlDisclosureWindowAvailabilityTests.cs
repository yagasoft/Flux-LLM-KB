using System.Reflection;
using System.Text.Json;
using FluxKnowledge.Infrastructure.SqlServer.Persistence;
using FluxKnowledge.Infrastructure.SqlServer.Search;
using FluxKnowledge.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FluxKnowledge.Integration.Tests.Search;

public sealed class SqlDisclosureWindowAvailabilityTests(NativeSqlServerFixture fixture) : IClassFixture<NativeSqlServerFixture>
{
    [NativeSqlServerFact]
    public async Task Materialised_SQL_matches_native_window_construction_for_adversarial_UTF16_inputs()
    {
        var cases = Cases();
        Assert.Equal(220, cases.Count);
        var inputs = JsonSerializer.Serialize(cases.Select((item, index) => new
        {
            VectorId = index + 1, item.StartOffset, Length = item.Length,
            CanonicalHex = Hex(item.Text), ContentHex = Hex(item.Body)
        }));
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        FormattableString capture = $"""
            IF OBJECT_ID(N'tempdb..#WindowFixtures') IS NOT NULL DROP TABLE #WindowFixtures;
            SELECT raw.VectorId,raw.StartOffset,raw.Length,
                CONVERT(nvarchar(max),CONVERT(varbinary(max),raw.CanonicalHex,2)) AS Canonical,
                CONVERT(nvarchar(max),CONVERT(varbinary(max),raw.ContentHex,2)) AS Content
            INTO #WindowFixtures
            FROM OPENJSON({inputs}) WITH(VectorId bigint '$.VectorId',StartOffset bigint '$.StartOffset',Length bigint '$.Length',
                CanonicalHex varchar(max) '$.CanonicalHex',ContentHex varchar(max) '$.ContentHex') AS raw;
            SELECT raw.VectorId,raw.StartOffset,raw.Length,raw.Content,CONVERT(bigint,DATALENGTH(raw.Canonical))/2 AS CanonicalLength,
                w.WindowStart,SUBSTRING(raw.Canonical COLLATE Latin1_General_100_BIN2,w.WindowStart+1,16384) AS WindowText
            INTO #CorpusAnnWindows FROM #WindowFixtures AS raw
            CROSS APPLY(SELECT CASE WHEN raw.StartOffset>4096 THEN raw.StartOffset-4096 ELSE CONVERT(bigint,0) END AS WindowStart) AS w;
            DROP TABLE #WindowFixtures
            """;
        var rows = await context.Database.SqlQuery<Flag>(SqlDisclosureWindowAvailability.Select(capture, "i.VectorId")).ToArrayAsync();
        Assert.Equal(cases.Count, rows.Length);
        foreach (var row in rows)
            Assert.True(row.WindowAvailable == NativeAvailable(cases[checked((int)row.VectorId - 1)]),
                "SQL/native availability differs: " + cases[checked((int)row.VectorId - 1)].Name);
        // A constructed window can still contain credentials: actual disclosure remains a separate gate.
        Assert.True(rows[^1].WindowAvailable);
        Assert.True(new FluxKnowledge.Infrastructure.SqlServer.Visibility.LocalPrivateContentDisclosure().Evaluate(
            cases[^1].Text, FluxKnowledge.Application.Visibility.LocalDisclosureKind.RetainedDetail).Withheld);
    }

    [NativeSqlServerFact]
    public async Task Failed_capture_cleans_its_temporary_table_before_same_connection_retry()
    {
        await using var context = await SqlTestData.CreateFactory(fixture).CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        FormattableString failed = $"""
            SELECT CONVERT(bigint,1) AS VectorId,CONVERT(bigint,0) AS StartOffset,1 AS Length,N'x' AS Content,
                CONVERT(bigint,1) AS CanonicalLength,CONVERT(bigint,0) AS WindowStart,N'x' AS WindowText
            INTO #CorpusAnnWindows;
            THROW 50001, 'synthetic-capture-failure', 1
            """;
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => context.Database.SqlQuery<Flag>(
            SqlDisclosureWindowAvailability.Select(failed, "i.VectorId")).ToArrayAsync());
        Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID(N'tempdb..#CorpusAnnWindows') IS NULL THEN 0 ELSE 1 END AS Value").SingleAsync());
        FormattableString success = $"""
            SELECT CONVERT(bigint,1) AS VectorId,CONVERT(bigint,0) AS StartOffset,1 AS Length,N'x' AS Content,
                CONVERT(bigint,1) AS CanonicalLength,CONVERT(bigint,0) AS WindowStart,N'x' AS WindowText
            INTO #CorpusAnnWindows
            """;
        Assert.True(Assert.Single(await context.Database.SqlQuery<Flag>(
            SqlDisclosureWindowAvailability.Select(success, "i.VectorId")).ToArrayAsync()).WindowAvailable);
        Assert.Equal(0, await context.Database.SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID(N'tempdb..#CorpusAnnWindows') IS NULL THEN 0 ELSE 1 END AS Value").SingleAsync());
    }

    private static bool NativeAvailable(Case item)
    {
        if (item.StartOffset < 0 || item.StartOffset > item.Text.Length || item.Length is < 1 or > 2048 ||
            item.Length > item.Text.Length-item.StartOffset || item.Body.Length != item.Length ||
            !item.Text.AsSpan((int)item.StartOffset,item.Length).SequenceEqual(item.Body.AsSpan())) return false;
        var origin = Math.Max(0,(int)item.StartOffset-4096);
        var text = item.Text.Substring(origin,Math.Min(16384,item.Text.Length-origin));
        var start=(int)item.StartOffset; var end=start+item.Length;
        if(start>origin && char.IsLowSurrogate(text[start-origin])) start++;
        if(end<origin+text.Length && end>start && char.IsHighSurrogate(text[end-origin-1])) end--;
        var method=typeof(SqlCorpusRetrievalReader).GetMethod("BuildDisclosureWindow",BindingFlags.Static|BindingFlags.NonPublic)!;
        object?[] args=[text,origin,start,end,origin==0,origin+text.Length==item.Text.Length,0];
        return method.Invoke(null,args) is not null;
    }

    private static string Hex(string text)
    {
        var bytes = new byte[text.Length*2];
        for(var index=0;index<text.Length;index++) { bytes[index*2]=(byte)text[index]; bytes[index*2+1]=(byte)(text[index]>>8); }
        return Convert.ToHexString(bytes);
    }

    private static List<Case> Cases()
    {
        var cases=new List<Case>();
        void Add(string name,string text,long start,int length,string? body=null) => cases.Add(new(name,text,start,length,
            body??(start>=0&&start<=text.Length&&length>=0&&length<=text.Length-start?text.Substring((int)start,length):"x")));
        Add("short-document","ordinary body   ",0,16); Add("trailing-space-mismatch","abc ",0,4,"abc");
        Add("same-length-body-mismatch","abcd",0,4,"abce"); Add("empty","",0,0); Add("negative-start","abc",-1,1);
        Add("huge-start","abc",long.MaxValue,1); Add("past-end","abc",2,2); Add("oversized-chunk",new('x',3000),0,2049);
        foreach(var newline in new[]{"\r","\n","\r\n"}) Add("normal-lines-"+Hex(newline),string.Join(newline,Enumerable.Repeat(new string('x',120),300)),7000,500);
        Add("long-line-no-boundary",new('x',40000),7000,700); Add("complete-long-line-document",new('x',16000),5000,700);
        foreach(var at in new[]{2047,2048,2049,6843,6844,6845,16382,16383,16384})
        { var chars=new string('x',40000).ToCharArray(); var origin=7000-4096; chars[origin+100]='\n'; chars[origin+at]='\n'; Add("boundary-"+at,new(chars),7000,700); }
        foreach(var at in new[]{2047,2048,2049})
        { var chars=new string('x',40000).ToCharArray(); var origin=7000-4096; chars[origin+9000]='\r'; chars[origin+at]='\n'; Add("left-boundary-"+at,new(chars),7000,700); }
        { var chars=new string('x',40000).ToCharArray(); var origin=7000-4096; chars[7000]='\udc00'; chars[origin+2048]='\n'; chars[origin+9000]='\n'; Add("low-surrogate-shifts-left-halo",new(chars),7000,700); }
        { var chars=new string('x',40000).ToCharArray(); var origin=7000-4096; chars[7699]='\ud800'; chars[origin+100]='\n'; chars[origin+6843]='\n'; Add("high-surrogate-shifts-right-halo",new(chars),7000,700); }
        Add("embedded-nul","ordinary\0text\nbody",0,13); Add("minimum-long-start","abc",long.MinValue,1); Add("maximum-int-start","abc",int.MaxValue,1);
        var surrogate=string.Join('\n',Enumerable.Repeat(new string('x',99)+"😀",300));
        for(var at=98;at<105;at++) Add("surrogate-"+at,surrogate,at,100);
        Add("lone-low-at-start","\udc00abc\n",0,1); Add("lone-high-at-end","abc\ud800\n",0,4);
        var random=new Random(1719);
        for(var test=0;test<180;test++)
        {
            var chars=Enumerable.Repeat('x',random.Next(100,40000)).ToArray();
            for(var index=0;index<chars.Length/400;index++) chars[random.Next(chars.Length)]=index%2==0?'\r':'\n';
            var start=random.Next(chars.Length); var length=random.Next(1,Math.Min(2048,chars.Length-start)+1);
            Add("generated-"+test,new(chars),start,length);
        }
        const string credential="password=exampleSyntheticValue";
        Add("credential-window-still-available",credential,0,credential.Length);
        return cases;
    }

    private sealed record Case(string Name,string Text,long StartOffset,int Length,string Body);
    private sealed class Flag
    {
        public long VectorId { get; init; }
        public bool WindowAvailable { get; init; }
    }
}
