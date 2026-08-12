using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorActionCapabilityFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatorActionCapabilityPolicies",
                columns: table => new
                {
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    DescriptorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DescriptorFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DescriptorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SafetyContractId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    HandlerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActionKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorActionCapabilityPolicies", x => new { x.PolicyId, x.PolicyRevision, x.DescriptorId, x.DescriptorFingerprint, x.DescriptorVersion, x.SafetyContractId, x.HandlerId, x.ActionKind, x.ReasonCode });
                });

            migrationBuilder.CreateTable(
                name: "OperatorActionHardDenials",
                columns: table => new
                {
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorActionHardDenials", x => x.ReasonCode);
                });

            migrationBuilder.CreateTable(
                name: "OperatorActionActionLedger",
                columns: table => new
                {
                    ActionId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    DescriptorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DescriptorFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DescriptorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SafetyContractId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    HandlerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActionKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceProcessorBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlockedRowVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    SourceProcessorForceRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorActionActionLedger", x => x.ActionId);
                    table.ForeignKey(
                        name: "FK_OperatorActionActionLedger_OperatorActionCapabilityPolicies_PolicyId_PolicyRevision_DescriptorId_DescriptorFingerprint_Descr~",
                        columns: x => new { x.PolicyId, x.PolicyRevision, x.DescriptorId, x.DescriptorFingerprint, x.DescriptorVersion, x.SafetyContractId, x.HandlerId, x.ActionKind, x.ReasonCode },
                        principalTable: "OperatorActionCapabilityPolicies",
                        principalColumns: new[] { "PolicyId", "PolicyRevision", "DescriptorId", "DescriptorFingerprint", "DescriptorVersion", "SafetyContractId", "HandlerId", "ActionKind", "ReasonCode" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperatorActionActionLedger_SourceProcessorBranches_SourceProcessorBranchId",
                        column: x => x.SourceProcessorBranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperatorActionActionLedger_SourceProcessorForceRequests_SourceProcessorForceRequestId",
                        column: x => x.SourceProcessorForceRequestId,
                        principalTable: "SourceProcessorForceRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperatorActionOperationLedger",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActionId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    IgnoreSequence = table.Column<long>(type: "bigint", nullable: true),
                    IgnoreState = table.Column<bool>(type: "bit", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorActionOperationLedger", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_OperatorActionOperationLedger_OperatorActionActionLedger_ActionId",
                        column: x => x.ActionId,
                        principalTable: "OperatorActionActionLedger",
                        principalColumn: "ActionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorActionIgnoreHeads",
                columns: table => new
                {
                    ActionId = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    IsIgnored = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorActionIgnoreHeads", x => x.ActionId);
                    table.ForeignKey(
                        name: "FK_SourceProcessorActionIgnoreHeads_OperatorActionActionLedger_ActionId",
                        column: x => x.ActionId,
                        principalTable: "OperatorActionActionLedger",
                        principalColumn: "ActionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "OperatorActionHardDenials",
                column: "ReasonCode",
                values: new object[]
                {
                    "archive-compression-ratio-limit",
                    "archive-entry-compression-unsupported",
                    "archive-entry-count-limit",
                    "archive-entry-encrypted",
                    "archive-entry-link-invalid",
                    "archive-entry-path-invalid",
                    "archive-entry-unsupported",
                    "archive-expanded-total-limit",
                    "archive-input-too-large",
                    "archive-member-identity-conflict",
                    "archive-member-not-utf8",
                    "archive-member-size-invalid",
                    "archive-member-size-limit",
                    "archive-signature-invalid",
                    "lease-expired-reconciled",
                    "legacy-office-binary-parser-unavailable",
                    "nested-archive-depth-limit",
                    "office-document-container-invalid",
                    "office-document-depth-limit",
                    "office-document-element-limit",
                    "office-document-encrypted",
                    "office-document-expanded-xml-limit",
                    "office-document-input-too-large",
                    "office-document-part-unsupported",
                    "office-document-text-limit",
                    "office-document-xml-invalid",
                    "processor-fence-invalid",
                    "processor-parser-unavailable",
                    "processor-provenance-invalid",
                    "retained-artifact-checksum-invalid",
                    "retained-artifact-missing",
                    "retained-artifact-path-invalid",
                    "retained-artifact-root-unavailable",
                    "retained-artifact-transient",
                    "source-activity-cancelled",
                    "source-activity-superseded"
                });

            migrationBuilder.Sql(
                """
                CREATE TRIGGER [TR_OperatorActionCapabilityPolicies_RejectHardDenials]
                ON [OperatorActionCapabilityPolicies]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS
                    (
                        SELECT 1 FROM inserted AS [candidate]
                        INNER JOIN [OperatorActionHardDenials] AS [denial]
                            ON [denial].[ReasonCode] = [candidate].[ReasonCode]
                    )
                        THROW 51000, 'Operator action capability policy cannot authorise a hard denial.', 1;
                END;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO [OperatorActionCapabilityPolicies]
                    ([PolicyId], [PolicyRevision], [DescriptorId], [DescriptorFingerprint], [DescriptorVersion], [SafetyContractId], [HandlerId], [ActionKind], [ReasonCode])
                SELECT DISTINCT [force].[DescriptorId], CONVERT(bigint, 1), [force].[DescriptorId], [force].[DescriptorFingerprint], [activity].[ProcessorVersion],
                    N'legacy-historical-receipt', N'retained-processor-branch-store', N'retry', N'legacy-force-request-receipt'
                FROM [SourceProcessorForceRequests] AS [force]
                INNER JOIN [SourceActivities] AS [activity] ON [activity].[Id] = [force].[SourceActivityId];

                INSERT INTO [OperatorActionActionLedger]
                    ([ActionId], [PolicyId], [PolicyRevision], [DescriptorId], [DescriptorFingerprint], [DescriptorVersion], [SafetyContractId], [HandlerId], [ActionKind], [ReasonCode], [SourceProcessorBranchId], [BlockedRowVersion], [SourceProcessorForceRequestId], [CreatedAtUtc])
                SELECT [force].[ActionId], [force].[DescriptorId], CONVERT(bigint, 1), [force].[DescriptorId], [force].[DescriptorFingerprint], [activity].[ProcessorVersion],
                    N'legacy-historical-receipt', N'retained-processor-branch-store', N'retry', N'legacy-force-request-receipt',
                    [force].[SourceProcessorBranchId], [force].[OriginalBlockedRowVersion], [force].[Id], [force].[RequestedAtUtc]
                FROM [SourceProcessorForceRequests] AS [force]
                INNER JOIN [SourceActivities] AS [activity] ON [activity].[Id] = [force].[SourceActivityId];

                INSERT INTO [OperatorActionOperationLedger] ([OperationId], [RequestFingerprint], [ActionId], [CreatedAtUtc])
                SELECT [OperationId], [RequestFingerprint], [ActionId], [RequestedAtUtc]
                FROM [SourceProcessorForceRequests];
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorActionActionLedger_PolicyId_PolicyRevision_DescriptorId_DescriptorFingerprint_DescriptorVersion_SafetyContractId_Han~",
                table: "OperatorActionActionLedger",
                columns: new[] { "PolicyId", "PolicyRevision", "DescriptorId", "DescriptorFingerprint", "DescriptorVersion", "SafetyContractId", "HandlerId", "ActionKind", "ReasonCode" });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorActionActionLedger_SourceProcessorBranchId",
                table: "OperatorActionActionLedger",
                column: "SourceProcessorBranchId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorActionActionLedger_SourceProcessorForceRequestId",
                table: "OperatorActionActionLedger",
                column: "SourceProcessorForceRequestId",
                unique: true,
                filter: "[SourceProcessorForceRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorActionOperationLedger_ActionId",
                table: "OperatorActionOperationLedger",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorActionOperationLedger_OperationId",
                table: "OperatorActionOperationLedger",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorActionIgnoreHeads_ActionId",
                table: "SourceProcessorActionIgnoreHeads",
                column: "ActionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [OperatorActionActionLedger])
                   OR EXISTS (SELECT 1 FROM [OperatorActionOperationLedger])
                   OR EXISTS (SELECT 1 FROM [SourceProcessorActionIgnoreHeads])
                    THROW 51000, 'Cannot downgrade operator action capability foundation while durable action history exists.', 1;
                """);
            migrationBuilder.DropTable(
                name: "OperatorActionHardDenials");

            migrationBuilder.DropTable(
                name: "OperatorActionOperationLedger");

            migrationBuilder.DropTable(
                name: "SourceProcessorActionIgnoreHeads");

            migrationBuilder.DropTable(
                name: "OperatorActionActionLedger");

            migrationBuilder.DropTable(
                name: "OperatorActionCapabilityPolicies");
        }
    }
}
