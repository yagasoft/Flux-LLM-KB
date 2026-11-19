using Microsoft.Data.SqlClient;
using System.Data;
using System.Runtime.CompilerServices;

namespace FluxKnowledge.Infrastructure.SqlServer.Search;

internal static class SqlWindowEligibilityBinding
{
    internal static FormattableString Bind(FormattableString query,string ruleVersion)
    {
        var arguments=query.GetArguments();
        var rule=new SqlParameter("windowEligibilityRule",SqlDbType.NVarChar,128) { Value=ruleVersion };
        var predicate="""
            [eligibility].[TextChunkId] IS NOT NULL AND [eligibility].[ArtifactId]=[chunk].[ArtifactId]
            AND [eligibility].[CanonicalSourceRevision]=[artifact].[SourceRevision]
            AND [eligibility].[CanonicalContentHash] COLLATE Latin1_General_100_BIN2=[artifact].[ContentHash] COLLATE Latin1_General_100_BIN2
            AND [eligibility].[CanonicalLength]=CONVERT(bigint,DATALENGTH([artifact].[SearchText]))/2
            AND [eligibility].[ChunkSourceRevision]=[chunk].[SourceRevision]
            AND [eligibility].[ChunkContentHash] COLLATE Latin1_General_100_BIN2=[chunk].[ContentHash] COLLATE Latin1_General_100_BIN2
            AND [eligibility].[StartOffset]=[chunk].[StartOffset] AND [eligibility].[Length]=[chunk].[Length]
            AND [eligibility].[RuleVersion]=@rule
            """.Replace("@rule","{"+arguments.Length+"}",StringComparison.Ordinal);
        return FormattableStringFactory.Create(query.Format.Replace("/*window-proof-matches*/",predicate,StringComparison.Ordinal),
            arguments.Concat([rule]).ToArray());
    }
}
