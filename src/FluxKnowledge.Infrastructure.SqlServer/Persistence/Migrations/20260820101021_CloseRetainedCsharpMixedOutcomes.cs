using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CloseRetainedCsharpMixedOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (
                    SELECT 1
                    FROM [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt]
                    WHERE ([receipt].[OutcomeCode] = N'csharp-code-syntax-invalid'
                           AND EXISTS (
                               SELECT 1
                               FROM [dbo].[SourceProcessorCodeDocuments] AS [document]
                               WHERE [document].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId]))
                       OR ([receipt].[OutcomeCode] = N'success'
                           AND EXISTS (
                               SELECT 1
                               FROM [dbo].[SourceProcessorCodeBlockedDiagnostics] AS [diagnostic]
                               WHERE [diagnostic].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId]))
                )
                    THROW 51000, 'Retained C# mixed outcomes must be repaired before closure migration.', 1;
                """);

            migrationBuilder.Sql(
                """
                CREATE OR ALTER TRIGGER [dbo].[TR_SourceProcessorCodeDocuments_InsertFence]
                ON [dbo].[SourceProcessorCodeDocuments]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT [branch].[Id]
                    FROM [dbo].[SourceProcessorBranches] AS [branch] WITH (UPDLOCK, HOLDLOCK)
                    INNER JOIN inserted AS [document]
                        ON [document].[SourceProcessorBranchId] = [branch].[Id];
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS [document]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt] WITH (UPDLOCK, HOLDLOCK)
                            ON [receipt].[SourceProcessorBranchId] = [document].[SourceProcessorBranchId])
                        THROW 51000, 'Retained C# code documents cannot be inserted after a receipt.', 1;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE OR ALTER TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_OutcomeFence]
                ON [dbo].[SourceProcessorCodeCompletionReceipts]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT [branch].[Id]
                    FROM [dbo].[SourceProcessorBranches] AS [branch] WITH (UPDLOCK, HOLDLOCK)
                    INNER JOIN inserted AS [receipt]
                        ON [receipt].[SourceProcessorBranchId] = [branch].[Id];
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS [receipt]
                        WHERE [receipt].[OutcomeCode] = N'success'
                          AND EXISTS (
                              SELECT 1
                              FROM [dbo].[SourceProcessorCodeBlockedDiagnostics] AS [diagnostic] WITH (UPDLOCK, HOLDLOCK)
                              WHERE [diagnostic].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId]))
                        THROW 51000, 'Retained C# success receipts cannot close a branch with blocked diagnostics.', 1;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE OR ALTER TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_InsertFence]
                ON [dbo].[SourceProcessorCodeBlockedDiagnostics]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT [branch].[Id]
                    FROM [dbo].[SourceProcessorBranches] AS [branch] WITH (UPDLOCK, HOLDLOCK)
                    INNER JOIN inserted AS [diagnostic]
                        ON [diagnostic].[SourceProcessorBranchId] = [branch].[Id];
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS [fact]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt] WITH (UPDLOCK, HOLDLOCK)
                            ON [receipt].[SourceProcessorBranchId] = [fact].[SourceProcessorBranchId])
                        THROW 51000, 'Retained C# blocked diagnostics cannot be appended after a receipt.', 1;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE OR ALTER TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_InsertFence]
                ON [dbo].[SourceProcessorCodeBlockedDiagnostics]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS [fact]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt]
                            ON [receipt].[SourceProcessorBranchId] = [fact].[SourceProcessorBranchId])
                        THROW 51000, 'Retained C# blocked diagnostics cannot be appended after a receipt.', 1;
                END;
                """);
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeCompletionReceipts_OutcomeFence];");
            migrationBuilder.Sql(
                "DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeDocuments_InsertFence];");
        }
    }
}
