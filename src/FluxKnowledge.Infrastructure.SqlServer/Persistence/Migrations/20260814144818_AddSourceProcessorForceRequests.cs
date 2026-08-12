using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceProcessorForceRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_SourceProcessorAttempts_BranchId_LeaseGeneration",
                table: "SourceProcessorAttempts",
                columns: new[] { "BranchId", "LeaseGeneration" });

            migrationBuilder.CreateTable(
                name: "SourceProcessorForceRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActionId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceProcessorBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DescriptorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DescriptorFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExpectedInputSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OriginalBlockedLeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    OriginalBlockedRowVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    OriginalOutcomeCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<byte>(type: "tinyint", nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ClaimExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    TerminalAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ForceAttemptBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ForceAttemptLeaseGeneration = table.Column<long>(type: "bigint", nullable: true),
                    TerminalReceiptFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    TerminalReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorForceRequests", x => x.Id);
                    table.CheckConstraint("CK_SourceProcessorForceRequests_ActionId", "LEN([ActionId]) = 64 AND [ActionId] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_AttemptBinding", "([ForceAttemptBranchId] IS NULL AND [ForceAttemptLeaseGeneration] IS NULL) OR ([ForceAttemptBranchId] IS NOT NULL AND [ForceAttemptLeaseGeneration] IS NOT NULL AND [ForceAttemptBranchId] = [SourceProcessorBranchId])");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_ExpectedInputSha256", "LEN([ExpectedInputSha256]) = 64 AND [ExpectedInputSha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_OriginalOutcome", "[OriginalOutcomeCode] IN (N'office-document-container-invalid', N'office-document-encrypted', N'office-document-xml-invalid', N'office-document-expanded-xml-limit', N'office-document-element-limit', N'office-document-depth-limit', N'office-document-text-limit', N'office-document-part-unsupported', N'office-document-input-too-large')");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_RequestFingerprint", "LEN([RequestFingerprint]) = 64 AND [RequestFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_State", "[State] IN (0, 1, 2, 3, 4, 5, 6)");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_StateShape", "(([State] = 0 AND [ClaimedAtUtc] IS NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR ([State] = 1 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR ([State] = 2 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'completed') OR ([State] = 3 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IN (N'office-document-container-invalid', N'office-document-encrypted', N'office-document-xml-invalid', N'office-document-expanded-xml-limit', N'office-document-element-limit', N'office-document-depth-limit', N'office-document-text-limit', N'office-document-part-unsupported', N'office-document-input-too-large', N'retained-artifact-missing', N'retained-artifact-path-invalid', N'retained-artifact-checksum-invalid')) OR ([State] = 4 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'force-request-transient') OR ([State] = 5 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IN (N'force-request-cancelled', N'force-request-descriptor-disabled') AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL) OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL))) OR ([State] = 6 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReasonCode] = N'force-request-claim-expired') OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReasonCode] = N'lease-expired-reconciled'))))");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_TerminalReceiptFingerprint", "[TerminalReceiptFingerprint] IS NULL OR (LEN([TerminalReceiptFingerprint]) = 64 AND [TerminalReceiptFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%')");
                    table.CheckConstraint("CK_SourceProcessorForceRequests_Timestamps", "[RequestedAtUtc] <= [ClaimExpiresAtUtc] AND ([ClaimedAtUtc] IS NULL OR [ClaimedAtUtc] >= [RequestedAtUtc]) AND ([TerminalAtUtc] IS NULL OR [TerminalAtUtc] >= [RequestedAtUtc]) AND ([TerminalAtUtc] IS NULL OR [ClaimedAtUtc] IS NULL OR [TerminalAtUtc] >= [ClaimedAtUtc])");
                    table.ForeignKey(
                        name: "FK_SourceProcessorForceRequests_SourceActivities_SourceActivityId",
                        column: x => x.SourceActivityId,
                        principalTable: "SourceActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorForceRequests_SourceProcessorAttempts_ForceAttemptBranchId_ForceAttemptLeaseGeneration",
                        columns: x => new { x.ForceAttemptBranchId, x.ForceAttemptLeaseGeneration },
                        principalTable: "SourceProcessorAttempts",
                        principalColumns: new[] { "BranchId", "LeaseGeneration" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorForceRequests_SourceProcessorBranches_SourceProcessorBranchId",
                        column: x => x.SourceProcessorBranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorForceRequests_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_ActionId",
                table: "SourceProcessorForceRequests",
                column: "ActionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_ForceAttemptBranchId_ForceAttemptLeaseGeneration",
                table: "SourceProcessorForceRequests",
                columns: new[] { "ForceAttemptBranchId", "ForceAttemptLeaseGeneration" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_OperationId",
                table: "SourceProcessorForceRequests",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_SourceActivityId",
                table: "SourceProcessorForceRequests",
                column: "SourceActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_SourceProcessorBranchId_DescriptorId_DescriptorFingerprint_OriginalBlockedRowVersion",
                table: "SourceProcessorForceRequests",
                columns: new[] { "SourceProcessorBranchId", "DescriptorId", "DescriptorFingerprint", "OriginalBlockedRowVersion" },
                unique: true,
                filter: "[State] IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_SourceRevisionId",
                table: "SourceProcessorForceRequests",
                column: "SourceRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_State_ClaimExpiresAtUtc",
                table: "SourceProcessorForceRequests",
                columns: new[] { "State", "ClaimExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceProcessorForceRequests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SourceProcessorAttempts_BranchId_LeaseGeneration",
                table: "SourceProcessorAttempts");
        }
    }
}
