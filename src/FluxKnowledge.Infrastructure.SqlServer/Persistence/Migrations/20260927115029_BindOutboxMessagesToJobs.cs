using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindOutboxMessagesToJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JobId",
                table: "OutboxMessages",
                type: "uniqueidentifier",
                nullable: true);

            var backfillSql = """
                UPDATE [message]
                SET [JobId] = [job].[Id]
                FROM [OutboxMessages] AS [message]
                INNER JOIN [Jobs] AS [job]
                  ON [job].[PipelineRecordId] = [message].[PipelineRecordId]
                 AND [job].[SourceRevision] = [message].[SourceRevision]
                 AND [job].[Stage] = [message].[Stage]
                 AND [job].[Operation] = [message].[Operation] COLLATE Latin1_General_100_BIN2
                WHERE NOT EXISTS (
                    SELECT 1 FROM [Jobs] AS [other]
                    WHERE [other].[PipelineRecordId] = [job].[PipelineRecordId]
                      AND [other].[SourceRevision] = [job].[SourceRevision]
                      AND [other].[Stage] = [job].[Stage]
                      AND [other].[Operation] = [job].[Operation]
                      AND [other].[Id] <> [job].[Id]);
                IF EXISTS (SELECT 1 FROM [OutboxMessages] WHERE [JobId] IS NULL AND [DispatchedAtUtc] IS NULL)
                    THROW 51000, 'outbox-job-binding-migration-requires-unambiguous-pending-deliveries', 1;
                """;
            // Idempotent scripts group schema commands in a single batch. Compile
            // this new-column reference only after its ALTER TABLE has executed.
            migrationBuilder.Sql("EXEC(N'" + backfillSql.Replace("'", "''", StringComparison.Ordinal) + "');");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_JobId_SourceRevision",
                table: "OutboxMessages",
                columns: new[] { "JobId", "SourceRevision" });

            migrationBuilder.AddForeignKey(
                name: "FK_OutboxMessages_Jobs_JobId_SourceRevision",
                table: "OutboxMessages",
                columns: new[] { "JobId", "SourceRevision" },
                principalTable: "Jobs",
                principalColumns: new[] { "Id", "SourceRevision" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [Jobs]
                    GROUP BY [PipelineRecordId], [SourceRevision], [Stage], [Operation]
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51000, 'outbox-job-binding-downgrade-refuses-repeated-stage-jobs', 1;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_OutboxMessages_Jobs_JobId_SourceRevision",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_JobId_SourceRevision",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "OutboxMessages");
        }
    }
}
