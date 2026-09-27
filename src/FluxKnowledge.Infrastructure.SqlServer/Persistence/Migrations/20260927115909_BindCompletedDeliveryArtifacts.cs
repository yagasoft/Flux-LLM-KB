using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindCompletedDeliveryArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompletedArtifactId",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: true);
            var backfillSql = """
                UPDATE [message] SET [CompletedArtifactId] = [artifact].[Id]
                FROM [OutboxMessages] AS [message]
                INNER JOIN [Jobs] AS [job] ON [job].[Id] = [message].[JobId]
                  AND [job].[PipelineRecordId] = [message].[PipelineRecordId]
                  AND [job].[SourceRevision] = [message].[SourceRevision]
                  AND [job].[Stage] = [message].[Stage]
                  AND [job].[Operation] = [message].[Operation] COLLATE Latin1_General_100_BIN2
                INNER JOIN [Artifacts] AS [artifact]
                  ON [artifact].[PipelineRecordId] = [message].[PipelineRecordId]
                 AND [artifact].[SourceRevision] = [message].[SourceRevision]
                 AND [artifact].[Stage] = [message].[Stage]
                WHERE [message].[DispatchedAtUtc] IS NOT NULL AND [job].[PublicState] = 4
                  AND NOT EXISTS (
                    SELECT 1 FROM [Jobs] AS [other]
                    WHERE [other].[PipelineRecordId] = [job].[PipelineRecordId]
                      AND [other].[SourceRevision] = [job].[SourceRevision]
                      AND [other].[Stage] = [job].[Stage]
                      AND [other].[Id] <> [job].[Id]);
                """;
            // Defer new-column compilation in the reviewed idempotent script.
            migrationBuilder.Sql("EXEC(N'" + backfillSql.Replace("'", "''", StringComparison.Ordinal) + "');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [Jobs]
                    GROUP BY [PipelineRecordId], [SourceRevision], [Stage]
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51000, 'completed-delivery-artifact-downgrade-refuses-repeated-stage-jobs', 1;
                """);
            migrationBuilder.DropColumn(
                name: "CompletedArtifactId",
                table: "OutboxMessages");
        }
    }
}
