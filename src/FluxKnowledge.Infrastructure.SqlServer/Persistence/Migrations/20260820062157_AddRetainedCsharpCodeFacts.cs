using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetainedCsharpCodeFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceActivities_SourceRevisionId_ActivityKind_ProcessorVersion_InputFingerprint",
                table: "SourceActivities");

            migrationBuilder.AddColumn<string>(
                name: "DescriptorFingerprint",
                table: "SourceActivities",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: false,
                defaultValue: "b0fe7acd8ced58bf9215c12938f5bbc75b722323f3553f2705959467029a4fb5",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceProcessorAttempts_BranchId_Id",
                table: "SourceProcessorAttempts",
                columns: new[] { "BranchId", "Id" });

            migrationBuilder.CreateTable(
                name: "SourceProcessorCodeBlockedDiagnostics",
                columns: table => new
                {
                    SourceProcessorBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceProcessorAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    DiagnosticId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    SpanStartUtf16 = table.Column<int>(type: "int", nullable: false),
                    SpanLengthUtf16 = table.Column<int>(type: "int", nullable: false),
                    Representation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ScannedMessage = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    WithheldReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    BlockedDiagnosticFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorCodeBlockedDiagnostics", x => new { x.SourceProcessorBranchId, x.SourceProcessorAttemptId, x.Ordinal });
                    table.CheckConstraint("CK_SourceProcessorCodeBlockedDiagnostics_Representation", "[Ordinal] BETWEEN 0 AND 255 AND [Severity] BETWEEN 0 AND 3 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND (([Representation] = N'scanned' AND [ScannedMessage] IS NOT NULL AND [WithheldReason] IS NULL) OR ([Representation] = N'withheld' AND [ScannedMessage] IS NULL AND [WithheldReason] = N'secret-content-withheld'))");
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeBlockedDiagnostics_SourceProcessorAttempts_SourceProcessorBranchId_SourceProcessorAttemptId",
                        columns: x => new { x.SourceProcessorBranchId, x.SourceProcessorAttemptId },
                        principalTable: "SourceProcessorAttempts",
                        principalColumns: new[] { "BranchId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeBlockedDiagnostics_SourceProcessorBranches_SourceProcessorBranchId",
                        column: x => x.SourceProcessorBranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorCodeDocuments",
                columns: table => new
                {
                    SourceProcessorBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RetainedArtifactSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    DescriptorFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ParserFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    HandlerImplementationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    DecodedCharacterCount = table.Column<int>(type: "int", nullable: false),
                    LineCount = table.Column<int>(type: "int", nullable: false),
                    SymbolCount = table.Column<int>(type: "int", nullable: false),
                    ReferenceCount = table.Column<int>(type: "int", nullable: false),
                    DiagnosticsCount = table.Column<int>(type: "int", nullable: false),
                    WithheldSymbolCount = table.Column<int>(type: "int", nullable: false),
                    WithheldReferenceCount = table.Column<int>(type: "int", nullable: false),
                    WithheldDiagnosticCount = table.Column<int>(type: "int", nullable: false),
                    ReceiptDiagnosticCodeCount = table.Column<int>(type: "int", nullable: false),
                    DocumentFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CompletionFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorCodeDocuments", x => x.SourceProcessorBranchId);
                    table.CheckConstraint("CK_SourceProcessorCodeDocuments_Counts", "[DecodedCharacterCount] >= 0 AND [LineCount] >= 0 AND [SymbolCount] >= 0 AND [ReferenceCount] >= 0 AND [DiagnosticsCount] BETWEEN 0 AND 256 AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256");
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeDocuments_SourceProcessorBranches_SourceProcessorBranchId",
                        column: x => x.SourceProcessorBranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeDocuments_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorCodeCompletionReceipts",
                columns: table => new
                {
                    SourceProcessorBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceProcessorAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivityKind = table.Column<int>(type: "int", nullable: false),
                    ProcessorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DescriptorFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ParserFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    RetainedArtifactSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    HandlerImplementationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OutcomeCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CompletionFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    WithheldSymbolCount = table.Column<int>(type: "int", nullable: false),
                    WithheldReferenceCount = table.Column<int>(type: "int", nullable: false),
                    WithheldDiagnosticCount = table.Column<int>(type: "int", nullable: false),
                    BlockedDiagnosticsCount = table.Column<int>(type: "int", nullable: false),
                    ReceiptDiagnosticCodeCount = table.Column<int>(type: "int", nullable: false),
                    ReceiptDiagnosticCodesWire = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorCodeCompletionReceipts", x => x.SourceProcessorBranchId);
                    table.CheckConstraint("CK_SourceProcessorCodeCompletionReceipts_Outcome", "(([OutcomeCode] = N'csharp-code-syntax-invalid' AND [DocumentId] IS NULL AND [DocumentFingerprint] IS NULL AND [BlockedDiagnosticsCount] BETWEEN 0 AND 256) OR ([OutcomeCode] = N'success' AND [DocumentId] IS NOT NULL AND [DocumentFingerprint] IS NOT NULL AND [BlockedDiagnosticsCount] = 0)) AND [WithheldSymbolCount] >= 0 AND [WithheldReferenceCount] >= 0 AND [WithheldDiagnosticCount] BETWEEN 0 AND 256 AND [ReceiptDiagnosticCodeCount] BETWEEN 0 AND 256");
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorAttempts_SourceProcessorBranchId_SourceProcessorAttemptId",
                        columns: x => new { x.SourceProcessorBranchId, x.SourceProcessorAttemptId },
                        principalTable: "SourceProcessorAttempts",
                        principalColumns: new[] { "BranchId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorBranches_SourceProcessorBranchId",
                        column: x => x.SourceProcessorBranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeCompletionReceipts_SourceProcessorCodeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "SourceProcessorCodeDocuments",
                        principalColumn: "SourceProcessorBranchId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeCompletionReceipts_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorCodeDiagnostics",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    DiagnosticId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<byte>(type: "tinyint", nullable: false),
                    SpanStartUtf16 = table.Column<int>(type: "int", nullable: false),
                    SpanLengthUtf16 = table.Column<int>(type: "int", nullable: false),
                    Representation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ScannedMessage = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    WithheldReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DiagnosticFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorCodeDiagnostics", x => new { x.DocumentId, x.Ordinal });
                    table.CheckConstraint("CK_SourceProcessorCodeDiagnostics_Representation", "[Ordinal] BETWEEN 0 AND 255 AND [Severity] BETWEEN 0 AND 3 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND (([Representation] = N'scanned' AND [ScannedMessage] IS NOT NULL AND [WithheldReason] IS NULL) OR ([Representation] = N'withheld' AND [ScannedMessage] IS NULL AND [WithheldReason] = N'secret-content-withheld'))");
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeDiagnostics_SourceProcessorCodeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "SourceProcessorCodeDocuments",
                        principalColumn: "SourceProcessorBranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorCodeReferences",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    RelationshipKindCode = table.Column<int>(type: "int", nullable: false),
                    SourceSymbolOrdinal = table.Column<int>(type: "int", nullable: true),
                    TargetDisplay = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    SpanStartUtf16 = table.Column<int>(type: "int", nullable: false),
                    SpanLengthUtf16 = table.Column<int>(type: "int", nullable: false),
                    ReferenceFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorCodeReferences", x => new { x.DocumentId, x.Ordinal });
                    table.CheckConstraint("CK_SourceProcessorCodeReferences_Bounds", "[Ordinal] >= 0 AND [RelationshipKindCode] >= 0 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0");
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeReferences_SourceProcessorCodeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "SourceProcessorCodeDocuments",
                        principalColumn: "SourceProcessorBranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorCodeSymbols",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    DeclarationKindCode = table.Column<int>(type: "int", nullable: false),
                    LocalName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    QualifiedName = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    RenderedSignature = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    Modifiers = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    LexicalParentOrdinal = table.Column<int>(type: "int", nullable: false),
                    SpanStartUtf16 = table.Column<int>(type: "int", nullable: false),
                    SpanLengthUtf16 = table.Column<int>(type: "int", nullable: false),
                    SymbolFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorCodeSymbols", x => new { x.DocumentId, x.Ordinal });
                    table.CheckConstraint("CK_SourceProcessorCodeSymbols_Bounds", "[Ordinal] >= 0 AND [DeclarationKindCode] >= 0 AND [SpanStartUtf16] >= 0 AND [SpanLengthUtf16] >= 0 AND [LexicalParentOrdinal] >= -1");
                    table.ForeignKey(
                        name: "FK_SourceProcessorCodeSymbols_SourceProcessorCodeDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "SourceProcessorCodeDocuments",
                        principalColumn: "SourceProcessorBranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "OperatorActionHardDenials",
                column: "ReasonCode",
                values: new object[]
                {
                    "csharp-code-depth-limit",
                    "csharp-code-diagnostic-limit",
                    "csharp-code-identifier-limit",
                    "csharp-code-input-not-utf8",
                    "csharp-code-input-too-large",
                    "csharp-code-node-limit",
                    "csharp-code-reference-limit",
                    "csharp-code-signature-limit",
                    "csharp-code-symbol-limit",
                    "csharp-code-syntax-invalid",
                    "csharp-code-text-limit"
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivities_SourceRevisionId_ActivityKind_ProcessorVersion_DescriptorFingerprint_InputFingerprint",
                table: "SourceActivities",
                columns: new[] { "SourceRevisionId", "ActivityKind", "ProcessorVersion", "DescriptorFingerprint", "InputFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeBlockedDiagnostics_SourceProcessorBranchId_SourceProcessorAttemptId_BlockedDiagnosticFingerprint",
                table: "SourceProcessorCodeBlockedDiagnostics",
                columns: new[] { "SourceProcessorBranchId", "SourceProcessorAttemptId", "BlockedDiagnosticFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeBlockedDiagnostics_SourceProcessorBranchId_SourceProcessorAttemptId_Ordinal",
                table: "SourceProcessorCodeBlockedDiagnostics",
                columns: new[] { "SourceProcessorBranchId", "SourceProcessorAttemptId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_DocumentId",
                table: "SourceProcessorCodeCompletionReceipts",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_SourceProcessorBranchId_SourceProcessorAttemptId",
                table: "SourceProcessorCodeCompletionReceipts",
                columns: new[] { "SourceProcessorBranchId", "SourceProcessorAttemptId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeCompletionReceipts_SourceRevisionId",
                table: "SourceProcessorCodeCompletionReceipts",
                column: "SourceRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeDiagnostics_DocumentId_DiagnosticFingerprint",
                table: "SourceProcessorCodeDiagnostics",
                columns: new[] { "DocumentId", "DiagnosticFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeDiagnostics_DocumentId_Severity_Ordinal",
                table: "SourceProcessorCodeDiagnostics",
                columns: new[] { "DocumentId", "Severity", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeDocuments_SourceRevisionId",
                table: "SourceProcessorCodeDocuments",
                column: "SourceRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeReferences_DocumentId_ReferenceFingerprint",
                table: "SourceProcessorCodeReferences",
                columns: new[] { "DocumentId", "ReferenceFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorCodeSymbols_DocumentId_SymbolFingerprint",
                table: "SourceProcessorCodeSymbols",
                columns: new[] { "DocumentId", "SymbolFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceProcessorCodeBlockedDiagnostics");

            migrationBuilder.DropTable(
                name: "SourceProcessorCodeCompletionReceipts");

            migrationBuilder.DropTable(
                name: "SourceProcessorCodeDiagnostics");

            migrationBuilder.DropTable(
                name: "SourceProcessorCodeReferences");

            migrationBuilder.DropTable(
                name: "SourceProcessorCodeSymbols");

            migrationBuilder.DropTable(
                name: "SourceProcessorCodeDocuments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceProcessorAttempts_BranchId_Id",
                table: "SourceProcessorAttempts");

            migrationBuilder.DropIndex(
                name: "IX_SourceActivities_SourceRevisionId_ActivityKind_ProcessorVersion_DescriptorFingerprint_InputFingerprint",
                table: "SourceActivities");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-depth-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-diagnostic-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-identifier-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-input-not-utf8");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-input-too-large");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-node-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-reference-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-signature-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-symbol-limit");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-syntax-invalid");

            migrationBuilder.DeleteData(
                table: "OperatorActionHardDenials",
                keyColumn: "ReasonCode",
                keyValue: "csharp-code-text-limit");

            migrationBuilder.DropColumn(
                name: "DescriptorFingerprint",
                table: "SourceActivities");

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivities_SourceRevisionId_ActivityKind_ProcessorVersion_InputFingerprint",
                table: "SourceActivities",
                columns: new[] { "SourceRevisionId", "ActivityKind", "ProcessorVersion", "InputFingerprint" },
                unique: true);
        }
    }
}
