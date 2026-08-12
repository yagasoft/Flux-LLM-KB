using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenRetainedCsharpLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_DocumentId",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeSymbols_Bounds",
                table: "SourceProcessorCodeSymbols");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeReferences_Bounds",
                table: "SourceProcessorCodeReferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeDocuments_Counts",
                table: "SourceProcessorCodeDocuments");

            migrationBuilder.DropIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_DocumentId",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeCompletionReceipts_Outcome",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "HandlerImplementationId",
                table: "SourceProcessorCodeCompletionReceipts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceProcessorCodeDocuments_SourceProcessorBranchId_SourceRevisionId_RetainedArtifactSha256_DescriptorFingerprint_ParserFin~",
                table: "SourceProcessorCodeDocuments",
                columns: new[] { "SourceProcessorBranchId", "SourceRevisionId", "RetainedArtifactSha256", "DescriptorFingerprint", "ParserFingerprint", "HandlerImplementationId", "WithheldSymbolCount", "WithheldReferenceCount", "WithheldDiagnosticCount", "ReceiptDiagnosticCodeCount", "DocumentFingerprint", "CompletionFingerprint" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeSymbols_Bounds",
                table: "SourceProcessorCodeSymbols",
                sql: "[Ordinal] >= 0 AND [DeclarationKindCode] BETWEEN 1 AND 20 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND [LexicalParentOrdinal] >= -1 AND [LexicalParentOrdinal] < [Ordinal]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeReferences_Bounds",
                table: "SourceProcessorCodeReferences",
                sql: "[Ordinal] >= 0 AND [RelationshipKindCode] BETWEEN 1 AND 7 AND ([SourceSymbolOrdinal] IS NULL OR [SourceSymbolOrdinal] >= 0) AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeDocuments_Counts",
                table: "SourceProcessorCodeDocuments",
                sql: "[DecodedCharacterCount] >= 0 AND [LineCount] >= 1 AND [LineCount] <= [DecodedCharacterCount] + 1 AND [SymbolCount] >= 0 AND [ReferenceCount] >= 0 AND [DiagnosticsCount] BETWEEN 0 AND 256 AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256 AND [DiagnosticsCount] = [ReceiptDiagnosticCodeCount]");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_DocumentId_SourceRevisionId_RetainedArtifactSha256_DescriptorFingerprint_ParserFingerp~",
                table: "SourceProcessorCodeCompletionReceipts",
                columns: new[] { "DocumentId", "SourceRevisionId", "RetainedArtifactSha256", "DescriptorFingerprint", "ParserFingerprint", "HandlerImplementationId", "WithheldSymbolCount", "WithheldReferenceCount", "WithheldDiagnosticCount", "ReceiptDiagnosticCodeCount", "DocumentFingerprint", "CompletionFingerprint" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeCompletionReceipts_DocumentBranchEquality",
                table: "SourceProcessorCodeCompletionReceipts",
                sql: "[DocumentId] IS NULL OR [DocumentId] = [SourceProcessorBranchId]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeCompletionReceipts_Outcome",
                table: "SourceProcessorCodeCompletionReceipts",
                sql: "(([OutcomeCode] = N'csharp-code-syntax-invalid' AND [DocumentId] IS NULL AND [DocumentFingerprint] IS NULL AND [BlockedDiagnosticsCount] BETWEEN 0 AND 256 AND [WithheldSymbolCount] = 0 AND [WithheldReferenceCount] = 0) OR ([OutcomeCode] = N'success' AND [DocumentId] IS NOT NULL AND [DocumentFingerprint] IS NOT NULL AND [BlockedDiagnosticsCount] = 0)) AND [ActivityKind] = 5 AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodesWire] LIKE CONVERT(varchar(3), [ReceiptDiagnosticCodeCount]) + ';%'");

            migrationBuilder.AddForeignKey(
                name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_SuccessIdentity",
                table: "SourceProcessorCodeCompletionReceipts",
                columns: new[] { "DocumentId", "SourceRevisionId", "RetainedArtifactSha256", "DescriptorFingerprint", "ParserFingerprint", "HandlerImplementationId", "WithheldSymbolCount", "WithheldReferenceCount", "WithheldDiagnosticCount", "ReceiptDiagnosticCodeCount", "DocumentFingerprint", "CompletionFingerprint" },
                principalTable: "SourceProcessorCodeDocuments",
                principalColumns: new[] { "SourceProcessorBranchId", "SourceRevisionId", "RetainedArtifactSha256", "DescriptorFingerprint", "ParserFingerprint", "HandlerImplementationId", "WithheldSymbolCount", "WithheldReferenceCount", "WithheldDiagnosticCount", "ReceiptDiagnosticCodeCount", "DocumentFingerprint", "CompletionFingerprint" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeDocuments_Immutable]
                ON [dbo].[SourceProcessorCodeDocuments]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Retained C# code documents are immutable.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeSymbols_Immutable]
                ON [dbo].[SourceProcessorCodeSymbols]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Retained C# symbol facts are immutable.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeReferences_Immutable]
                ON [dbo].[SourceProcessorCodeReferences]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Retained C# reference facts are immutable.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeDiagnostics_Immutable]
                ON [dbo].[SourceProcessorCodeDiagnostics]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Retained C# diagnostic facts are immutable.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable]
                ON [dbo].[SourceProcessorCodeCompletionReceipts]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Retained C# completion receipts are immutable.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_Immutable]
                ON [dbo].[SourceProcessorCodeBlockedDiagnostics]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Retained C# blocked diagnostics are immutable.', 1;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeSymbols_InsertFence]
                ON [dbo].[SourceProcessorCodeSymbols]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS [fact]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt]
                            ON [receipt].[SourceProcessorBranchId] = [fact].[DocumentId])
                        THROW 51000, 'Retained C# symbol facts cannot be appended after a receipt.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeReferences_InsertFence]
                ON [dbo].[SourceProcessorCodeReferences]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS [fact]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt]
                            ON [receipt].[SourceProcessorBranchId] = [fact].[DocumentId])
                        THROW 51000, 'Retained C# reference facts cannot be appended after a receipt.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeDiagnostics_InsertFence]
                ON [dbo].[SourceProcessorCodeDiagnostics]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS [fact]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt]
                            ON [receipt].[SourceProcessorBranchId] = [fact].[DocumentId])
                        THROW 51000, 'Retained C# diagnostics cannot be appended after a receipt.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_InsertFence]
                ON [dbo].[SourceProcessorCodeBlockedDiagnostics]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (
                        SELECT 1 FROM inserted AS [fact]
                        INNER JOIN [dbo].[SourceProcessorCodeCompletionReceipts] AS [receipt]
                            ON [receipt].[SourceProcessorBranchId] = [fact].[SourceProcessorBranchId])
                        THROW 51000, 'Retained C# blocked diagnostics cannot be appended after a receipt.', 1;
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER [dbo].[TR_SourceProcessorCodeCompletionReceipts_Closure]
                ON [dbo].[SourceProcessorCodeCompletionReceipts]
                AFTER INSERT
                AS
                BEGIN
                    SET NOCOUNT ON;

                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS [receipt]
                        INNER JOIN [dbo].[SourceProcessorCodeDocuments] AS [document]
                            ON [document].[SourceProcessorBranchId] = [receipt].[DocumentId]
                        WHERE [receipt].[OutcomeCode] = N'success'
                          AND (
                              [document].[SymbolCount] <> (SELECT COUNT(*) FROM [dbo].[SourceProcessorCodeSymbols] AS [symbol] WHERE [symbol].[DocumentId] = [document].[SourceProcessorBranchId])
                              OR [document].[ReferenceCount] <> (SELECT COUNT(*) FROM [dbo].[SourceProcessorCodeReferences] AS [reference] WHERE [reference].[DocumentId] = [document].[SourceProcessorBranchId])
                              OR [document].[DiagnosticsCount] <> (SELECT COUNT(*) FROM [dbo].[SourceProcessorCodeDiagnostics] AS [diagnostic] WHERE [diagnostic].[DocumentId] = [document].[SourceProcessorBranchId])
                              OR [document].[WithheldDiagnosticCount] <> (SELECT COUNT(*) FROM [dbo].[SourceProcessorCodeDiagnostics] AS [diagnostic] WHERE [diagnostic].[DocumentId] = [document].[SourceProcessorBranchId] AND [diagnostic].[Representation] = N'withheld')
                              OR ([document].[SymbolCount] > 0 AND EXISTS (
                                  SELECT 1 FROM (
                                      SELECT MIN([Ordinal]) AS [MinimumOrdinal], MAX([Ordinal]) AS [MaximumOrdinal]
                                      FROM [dbo].[SourceProcessorCodeSymbols] AS [symbol]
                                      WHERE [symbol].[DocumentId] = [document].[SourceProcessorBranchId]) AS [ordinals]
                                  WHERE [ordinals].[MinimumOrdinal] <> 0 OR [ordinals].[MaximumOrdinal] <> [document].[SymbolCount] - 1))
                              OR ([document].[ReferenceCount] > 0 AND EXISTS (
                                  SELECT 1 FROM (
                                      SELECT MIN([Ordinal]) AS [MinimumOrdinal], MAX([Ordinal]) AS [MaximumOrdinal]
                                      FROM [dbo].[SourceProcessorCodeReferences] AS [reference]
                                      WHERE [reference].[DocumentId] = [document].[SourceProcessorBranchId]) AS [ordinals]
                                  WHERE [ordinals].[MinimumOrdinal] <> 0 OR [ordinals].[MaximumOrdinal] <> [document].[ReferenceCount] - 1))
                              OR ([document].[DiagnosticsCount] > 0 AND EXISTS (
                                  SELECT 1 FROM (
                                      SELECT MIN([Ordinal]) AS [MinimumOrdinal], MAX([Ordinal]) AS [MaximumOrdinal]
                                      FROM [dbo].[SourceProcessorCodeDiagnostics] AS [diagnostic]
                                      WHERE [diagnostic].[DocumentId] = [document].[SourceProcessorBranchId]) AS [ordinals]
                                  WHERE [ordinals].[MinimumOrdinal] <> 0 OR [ordinals].[MaximumOrdinal] <> [document].[DiagnosticsCount] - 1))
                          ))
                        THROW 51000, 'Retained C# success receipt does not close an exact contiguous fact set.', 1;

                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS [receipt]
                        WHERE [receipt].[OutcomeCode] = N'csharp-code-syntax-invalid'
                          AND (
                              EXISTS (SELECT 1 FROM [dbo].[SourceProcessorCodeDocuments] AS [document] WHERE [document].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId])
                              OR [receipt].[BlockedDiagnosticsCount] <> (
                                  SELECT COUNT(*) FROM [dbo].[SourceProcessorCodeBlockedDiagnostics] AS [diagnostic]
                                  WHERE [diagnostic].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId]
                                    AND [diagnostic].[SourceProcessorAttemptId] = [receipt].[SourceProcessorAttemptId])
                              OR [receipt].[WithheldDiagnosticCount] <> (
                                  SELECT COUNT(*) FROM [dbo].[SourceProcessorCodeBlockedDiagnostics] AS [diagnostic]
                                  WHERE [diagnostic].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId]
                                    AND [diagnostic].[SourceProcessorAttemptId] = [receipt].[SourceProcessorAttemptId]
                                    AND [diagnostic].[Representation] = N'withheld')
                              OR ([receipt].[BlockedDiagnosticsCount] > 0 AND EXISTS (
                                  SELECT 1 FROM (
                                      SELECT MIN([Ordinal]) AS [MinimumOrdinal], MAX([Ordinal]) AS [MaximumOrdinal]
                                      FROM [dbo].[SourceProcessorCodeBlockedDiagnostics] AS [diagnostic]
                                      WHERE [diagnostic].[SourceProcessorBranchId] = [receipt].[SourceProcessorBranchId]
                                        AND [diagnostic].[SourceProcessorAttemptId] = [receipt].[SourceProcessorAttemptId]) AS [ordinals]
                                  WHERE [ordinals].[MinimumOrdinal] <> 0 OR [ordinals].[MaximumOrdinal] <> [receipt].[BlockedDiagnosticsCount] - 1))
                          ))
                        THROW 51000, 'Retained C# blocked receipt does not close an exact contiguous diagnostic set.', 1;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeCompletionReceipts_Closure];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_InsertFence];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeDiagnostics_InsertFence];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeReferences_InsertFence];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeSymbols_InsertFence];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeBlockedDiagnostics_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeCompletionReceipts_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeDiagnostics_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeReferences_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeSymbols_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_SourceProcessorCodeDocuments_Immutable];");

            migrationBuilder.DropForeignKey(
                name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_SuccessIdentity",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeSymbols_Bounds",
                table: "SourceProcessorCodeSymbols");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeReferences_Bounds",
                table: "SourceProcessorCodeReferences");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceProcessorCodeDocuments_SourceProcessorBranchId_SourceRevisionId_RetainedArtifactSha256_DescriptorFingerprint_ParserFin~",
                table: "SourceProcessorCodeDocuments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeDocuments_Counts",
                table: "SourceProcessorCodeDocuments");

            migrationBuilder.DropIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_DocumentId_SourceRevisionId_RetainedArtifactSha256_DescriptorFingerprint_ParserFingerp~",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "HandlerImplementationId",
                table: "SourceProcessorCodeCompletionReceipts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(256)",
                oldMaxLength: 256,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeCompletionReceipts_DocumentBranchEquality",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorCodeCompletionReceipts_Outcome",
                table: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeSymbols_Bounds",
                table: "SourceProcessorCodeSymbols",
                sql: "[Ordinal] >= 0 AND [DeclarationKindCode] >= 0 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND [LexicalParentOrdinal] >= -1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeReferences_Bounds",
                table: "SourceProcessorCodeReferences",
                sql: "[Ordinal] >= 0 AND [RelationshipKindCode] >= 0 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeDocuments_Counts",
                table: "SourceProcessorCodeDocuments",
                sql: "[DecodedCharacterCount] >= 0 AND [LineCount] >= 0 AND [SymbolCount] >= 0 AND [ReferenceCount] >= 0 AND [DiagnosticsCount] BETWEEN 0 AND 256 AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_DocumentId",
                table: "SourceProcessorCodeCompletionReceipts",
                column: "DocumentId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorCodeCompletionReceipts_Outcome",
                table: "SourceProcessorCodeCompletionReceipts",
                sql: "(([OutcomeCode] = N'csharp-code-syntax-invalid' AND [DocumentId] IS NULL AND [DocumentFingerprint] IS NULL AND [BlockedDiagnosticsCount] BETWEEN 0 AND 256) OR ([OutcomeCode] = N'success' AND [DocumentId] IS NOT NULL AND [DocumentFingerprint] IS NOT NULL AND [BlockedDiagnosticsCount] = 0)) AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256");

            migrationBuilder.AddForeignKey(
                name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_DocumentId",
                table: "SourceProcessorCodeCompletionReceipts",
                column: "DocumentId",
                principalTable: "SourceProcessorCodeDocuments",
                principalColumn: "SourceProcessorBranchId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
