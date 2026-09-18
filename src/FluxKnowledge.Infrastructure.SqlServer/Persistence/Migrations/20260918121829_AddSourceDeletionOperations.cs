using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceDeletionOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAtUtc",
                table: "IndexGenerations",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceDeletionOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    PipelineRecordCount = table.Column<int>(type: "int", nullable: false),
                    SourceArtifactCount = table.Column<int>(type: "int", nullable: false),
                    SharedArtifactCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceDeletionOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceDeletionCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceDeletionOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageKind = table.Column<int>(type: "int", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ByteLength = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceDeletionCleanupItems", x => x.Id);
                    table.CheckConstraint("CK_SourceDeletionCleanupItems_ByteLength", "[ByteLength] IS NULL OR [ByteLength] >= 0");
                    table.CheckConstraint("CK_SourceDeletionCleanupItems_ContentSha256", "[ContentSha256] IS NULL OR ([ContentSha256] NOT LIKE '%[^0-9a-f]%' AND LEN([ContentSha256]) = 64)");
                    table.CheckConstraint("CK_SourceDeletionCleanupItems_State", "[State] IN (0, 1, 2)");
                    table.CheckConstraint("CK_SourceDeletionCleanupItems_StorageKind", "[StorageKind] IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_SourceDeletionCleanupItems_SourceDeletionOperations_SourceDeletionOperationId",
                        column: x => x.SourceDeletionOperationId,
                        principalTable: "SourceDeletionOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceDeletionCleanupItems_SourceDeletionOperationId_StorageKind_RelativePath",
                table: "SourceDeletionCleanupItems",
                columns: new[] { "SourceDeletionOperationId", "StorageKind", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceDeletionOperations_SourceRootId",
                table: "SourceDeletionOperations",
                column: "SourceRootId",
                unique: true);

            ReplaceImmutableTriggerForDeletion(migrationBuilder, "TR_SourceProcessorCodeDocuments_Immutable", "SourceProcessorCodeDocuments", "Retained C# code documents are immutable.", """
                SELECT 1 FROM [dbo].[SourceDeletionOperations] AS [operation]
                INNER JOIN [dbo].[SourceRevisions] AS [revision] ON [revision].[SourceRootId] = [operation].[SourceRootId]
                WHERE [operation].[State] = 1 AND [revision].[Id] = [deleted].[SourceRevisionId]
                """);
            ReplaceImmutableTriggerForDeletion(migrationBuilder, "TR_SourceProcessorCodeSymbols_Immutable", "SourceProcessorCodeSymbols", "Retained C# symbol facts are immutable.", SourceDeletionEligibilityThroughDocument());
            ReplaceImmutableTriggerForDeletion(migrationBuilder, "TR_SourceProcessorCodeReferences_Immutable", "SourceProcessorCodeReferences", "Retained C# reference facts are immutable.", SourceDeletionEligibilityThroughDocument());
            ReplaceImmutableTriggerForDeletion(migrationBuilder, "TR_SourceProcessorCodeDiagnostics_Immutable", "SourceProcessorCodeDiagnostics", "Retained C# diagnostic facts are immutable.", SourceDeletionEligibilityThroughDocument());
            ReplaceImmutableTriggerForDeletion(migrationBuilder, "TR_SourceProcessorCodeCompletionReceipts_Immutable", "SourceProcessorCodeCompletionReceipts", "Retained C# completion receipts are immutable.", """
                SELECT 1 FROM [dbo].[SourceDeletionOperations] AS [operation]
                INNER JOIN [dbo].[SourceRevisions] AS [revision] ON [revision].[SourceRootId] = [operation].[SourceRootId]
                WHERE [operation].[State] = 1 AND [revision].[Id] = [deleted].[SourceRevisionId]
                """);
            ReplaceImmutableTriggerForDeletion(migrationBuilder, "TR_SourceProcessorCodeBlockedDiagnostics_Immutable", "SourceProcessorCodeBlockedDiagnostics", "Retained C# blocked diagnostics are immutable.", """
                SELECT 1 FROM [dbo].[SourceDeletionOperations] AS [operation]
                INNER JOIN [dbo].[SourceRevisions] AS [revision] ON [revision].[SourceRootId] = [operation].[SourceRootId]
                INNER JOIN [dbo].[SourceProcessorBranches] AS [branch] ON [branch].[SourceRevisionId] = [revision].[Id]
                WHERE [operation].[State] = 1 AND [branch].[Id] = [deleted].[SourceProcessorBranchId]
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RestoreImmutableTrigger(migrationBuilder, "TR_SourceProcessorCodeBlockedDiagnostics_Immutable", "SourceProcessorCodeBlockedDiagnostics", "Retained C# blocked diagnostics are immutable.");
            RestoreImmutableTrigger(migrationBuilder, "TR_SourceProcessorCodeCompletionReceipts_Immutable", "SourceProcessorCodeCompletionReceipts", "Retained C# completion receipts are immutable.");
            RestoreImmutableTrigger(migrationBuilder, "TR_SourceProcessorCodeDiagnostics_Immutable", "SourceProcessorCodeDiagnostics", "Retained C# diagnostic facts are immutable.");
            RestoreImmutableTrigger(migrationBuilder, "TR_SourceProcessorCodeReferences_Immutable", "SourceProcessorCodeReferences", "Retained C# reference facts are immutable.");
            RestoreImmutableTrigger(migrationBuilder, "TR_SourceProcessorCodeSymbols_Immutable", "SourceProcessorCodeSymbols", "Retained C# symbol facts are immutable.");
            RestoreImmutableTrigger(migrationBuilder, "TR_SourceProcessorCodeDocuments_Immutable", "SourceProcessorCodeDocuments", "Retained C# code documents are immutable.");
            migrationBuilder.DropTable(
                name: "SourceDeletionCleanupItems");

            migrationBuilder.DropTable(
                name: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "RetiredAtUtc",
                table: "IndexGenerations");
        }

        private static string SourceDeletionEligibilityThroughDocument() =>
            """
            SELECT 1 FROM [dbo].[SourceDeletionOperations] AS [operation]
            INNER JOIN [dbo].[SourceRevisions] AS [revision] ON [revision].[SourceRootId] = [operation].[SourceRootId]
            INNER JOIN [dbo].[SourceProcessorCodeDocuments] AS [document] ON [document].[SourceRevisionId] = [revision].[Id]
            WHERE [operation].[State] = 1 AND [document].[SourceProcessorBranchId] = [deleted].[DocumentId]
            """;

        private static void ReplaceImmutableTriggerForDeletion(MigrationBuilder migrationBuilder, string trigger, string table, string message, string deletionEligibilitySql) =>
            migrationBuilder.Sql($"""
                ALTER TRIGGER [dbo].[{trigger}]
                ON [dbo].[{table}]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM [inserted]) OR EXISTS (
                        SELECT 1 FROM [deleted] AS [deleted]
                        WHERE NOT EXISTS ({deletionEligibilitySql})
                    )
                        THROW 51000, '{message}', 1;
                END;
                """);

        private static void RestoreImmutableTrigger(MigrationBuilder migrationBuilder, string trigger, string table, string message) =>
            migrationBuilder.Sql($"""
                ALTER TRIGGER [dbo].[{trigger}]
                ON [dbo].[{table}]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, '{message}', 1;
                END;
                """);
    }
}
