using System.Runtime.CompilerServices;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

/// <summary>Window construction only. A true flag grants no disclosure or publication authority.</summary>
internal static class SqlDisclosureWindowAvailability
{
    internal const string PolicyId = "context0-fullchunk-utf16-16384-halo2048-v1";

    // Input i contains one already materialised, bounded canonical window per vector.
    internal const string Apply = """
        CROSS APPLY (SELECT CONVERT(bigint,DATALENGTH(i.WindowText))/2 AS N,
         CASE WHEN i.StartOffset BETWEEN 0 AND 2147483647 THEN i.StartOffset-i.WindowStart ELSE CONVERT(bigint,0) END AS S,
         CASE WHEN i.Length BETWEEN 1 AND 2048 THEN i.Length ELSE CONVERT(bigint,0) END AS L) AS pos
        CROSS APPLY (SELECT pos.S+pos.L AS E,
         CASE WHEN i.CanonicalLength BETWEEN 0 AND 2147483647 AND i.StartOffset BETWEEN 0 AND 2147483647
         AND i.StartOffset<=i.CanonicalLength AND i.Length BETWEEN 1 AND 2048
         AND i.Length<=i.CanonicalLength-CASE WHEN i.StartOffset BETWEEN 0 AND 2147483647 THEN i.StartOffset ELSE 0 END
         AND DATALENGTH(i.Content)=pos.L*2 THEN 1 ELSE 0 END AS Valid) AS shape
        CROSS APPLY (SELECT pos.S+CASE WHEN pos.S>0 AND
         UNICODE(SUBSTRING(i.WindowText COLLATE Latin1_General_100_BIN2,pos.S+1,1)) BETWEEN 56320 AND 57343
         THEN 1 ELSE 0 END AS S) AS adjustedStart
        CROSS APPLY (SELECT shape.E-CASE WHEN shape.E<pos.N AND shape.E>adjustedStart.S AND
         UNICODE(SUBSTRING(i.WindowText COLLATE Latin1_General_100_BIN2,shape.E,1)) BETWEEN 55296 AND 56319
         THEN 1 ELSE 0 END AS E) AS adjustedEnd
        CROSS APPLY (SELECT CASE WHEN adjustedStart.S>2048 THEN adjustedStart.S-2048 ELSE CONVERT(bigint,0) END AS A,
         CASE WHEN adjustedEnd.E+2048<pos.N THEN adjustedEnd.E+2048 ELSE pos.N END AS B) AS halo
        CROSS APPLY (SELECT SUBSTRING(i.WindowText COLLATE Latin1_General_100_BIN2,1,CASE WHEN halo.A>0 THEN halo.A ELSE 0 END) AS Prefix,
         SUBSTRING(i.WindowText COLLATE Latin1_General_100_BIN2,halo.B+1,CASE WHEN pos.N-halo.B-1>0 THEN pos.N-halo.B-1 ELSE 0 END) AS Suffix) AS lines
        CROSS APPLY (SELECT CAST(CASE WHEN shape.Valid=1 AND i.WindowText IS NOT NULL AND pos.N<=16384
         AND pos.S>=0 AND shape.E<=pos.N
         AND CONVERT(varbinary(max),SUBSTRING(i.WindowText COLLATE Latin1_General_100_BIN2,pos.S+1,pos.L))=CONVERT(varbinary(max),i.Content)
         AND (i.WindowStart=0 OR CHARINDEX(NCHAR(13),lines.Prefix COLLATE Latin1_General_100_BIN2)>0 OR CHARINDEX(NCHAR(10),lines.Prefix COLLATE Latin1_General_100_BIN2)>0)
         AND (i.WindowStart+pos.N=i.CanonicalLength OR CHARINDEX(NCHAR(13),lines.Suffix COLLATE Latin1_General_100_BIN2)>0 OR CHARINDEX(NCHAR(10),lines.Suffix COLLATE Latin1_General_100_BIN2)>0)
         THEN 1 ELSE 0 END AS bit) AS Available) AS availability
        """;

    // Callers supply fixed column lists, never request SQL. Preserve parameterised capture arguments.
    internal static FormattableString Select(FormattableString capture, string columns) =>
        FormattableStringFactory.Create($"""
            SET NOCOUNT ON;
            IF OBJECT_ID(N'tempdb..#CorpusAnnWindows') IS NOT NULL DROP TABLE #CorpusAnnWindows;
            BEGIN TRY
                {capture.Format};
                SELECT {columns}, availability.Available AS WindowAvailable
                FROM #CorpusAnnWindows AS i
                {Apply}
                ORDER BY i.VectorId;
                DROP TABLE #CorpusAnnWindows;
            END TRY
            BEGIN CATCH
                IF OBJECT_ID(N'tempdb..#CorpusAnnWindows') IS NOT NULL DROP TABLE #CorpusAnnWindows;
                THROW;
            END CATCH
            """, capture.GetArguments());
}
