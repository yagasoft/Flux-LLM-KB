using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOperatorActionRequestPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorForceRequests_OriginalOutcome",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorForceRequests_StateShape",
                table: "SourceProcessorForceRequests");

            migrationBuilder.AddColumn<string>(
                name: "ActionKind",
                table: "SourceProcessorForceRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "DescriptorVersion",
                table: "SourceProcessorForceRequests",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "HandlerId",
                table: "SourceProcessorForceRequests",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<Guid>(
                name: "PolicyId",
                table: "SourceProcessorForceRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "PolicyReasonCode",
                table: "SourceProcessorForceRequests",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<long>(
                name: "PolicyRevision",
                table: "SourceProcessorForceRequests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "SafetyContractId",
                table: "SourceProcessorForceRequests",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.Sql(
                """
                UPDATE [force]
                SET [PolicyId] = [action].[PolicyId],
                    [PolicyRevision] = [action].[PolicyRevision],
                    [DescriptorVersion] = [action].[DescriptorVersion],
                    [SafetyContractId] = [action].[SafetyContractId],
                    [HandlerId] = [action].[HandlerId],
                    [ActionKind] = [action].[ActionKind],
                    [PolicyReasonCode] = [action].[ReasonCode]
                FROM [SourceProcessorForceRequests] AS [force]
                INNER JOIN [OperatorActionActionLedger] AS [action]
                    ON [action].[SourceProcessorForceRequestId] = [force].[Id];

                IF EXISTS
                (
                    SELECT 1
                    FROM [SourceProcessorForceRequests] AS [force]
                    LEFT JOIN [OperatorActionCapabilityPolicies] AS [policy]
                        ON [policy].[PolicyId] = [force].[PolicyId]
                       AND [policy].[PolicyRevision] = [force].[PolicyRevision]
                       AND [policy].[DescriptorId] = [force].[DescriptorId]
                       AND [policy].[DescriptorFingerprint] = [force].[DescriptorFingerprint]
                       AND [policy].[DescriptorVersion] = [force].[DescriptorVersion]
                       AND [policy].[SafetyContractId] = [force].[SafetyContractId]
                       AND [policy].[HandlerId] = [force].[HandlerId]
                       AND [policy].[ActionKind] = [force].[ActionKind]
                       AND [policy].[ReasonCode] = [force].[PolicyReasonCode]
                    WHERE [policy].[PolicyId] IS NULL
                )
                    THROW 51000, 'Operator action request policy backfill is incomplete.', 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorForceRequests_PolicyId_PolicyRevision_DescriptorId_DescriptorFingerprint_DescriptorVersion_SafetyContractId_H~",
                table: "SourceProcessorForceRequests",
                columns: new[] { "PolicyId", "PolicyRevision", "DescriptorId", "DescriptorFingerprint", "DescriptorVersion", "SafetyContractId", "HandlerId", "ActionKind", "PolicyReasonCode" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorForceRequests_OriginalOutcome",
                table: "SourceProcessorForceRequests",
                sql: "[OriginalOutcomeCode] <> N''");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorForceRequests_StateShape",
                table: "SourceProcessorForceRequests",
                sql: "(([State] = 0 AND [ClaimedAtUtc] IS NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR ([State] = 1 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR ([State] = 2 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'completed') OR ([State] = 3 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IS NOT NULL) OR ([State] = 4 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'force-request-transient') OR ([State] = 5 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IN (N'force-request-cancelled', N'force-request-descriptor-disabled') AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL) OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL))) OR ([State] = 6 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReasonCode] = N'force-request-claim-expired') OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReasonCode] = N'lease-expired-reconciled'))))");

            migrationBuilder.AddForeignKey(
                name: "FK_SourceProcessorForceRequests_OperatorActionCapabilityPolicies_PolicyId_PolicyRevision_DescriptorId_DescriptorFingerprint_Des~",
                table: "SourceProcessorForceRequests",
                columns: new[] { "PolicyId", "PolicyRevision", "DescriptorId", "DescriptorFingerprint", "DescriptorVersion", "SafetyContractId", "HandlerId", "ActionKind", "PolicyReasonCode" },
                principalTable: "OperatorActionCapabilityPolicies",
                principalColumns: new[] { "PolicyId", "PolicyRevision", "DescriptorId", "DescriptorFingerprint", "DescriptorVersion", "SafetyContractId", "HandlerId", "ActionKind", "ReasonCode" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceProcessorForceRequests_OperatorActionCapabilityPolicies_PolicyId_PolicyRevision_DescriptorId_DescriptorFingerprint_Des~",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropIndex(
                name: "IX_SourceProcessorForceRequests_PolicyId_PolicyRevision_DescriptorId_DescriptorFingerprint_DescriptorVersion_SafetyContractId_H~",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorForceRequests_OriginalOutcome",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceProcessorForceRequests_StateShape",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "ActionKind",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "DescriptorVersion",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "HandlerId",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "PolicyId",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "PolicyReasonCode",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                table: "SourceProcessorForceRequests");

            migrationBuilder.DropColumn(
                name: "SafetyContractId",
                table: "SourceProcessorForceRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorForceRequests_OriginalOutcome",
                table: "SourceProcessorForceRequests",
                sql: "[OriginalOutcomeCode] IN (N'office-document-container-invalid', N'office-document-encrypted', N'office-document-xml-invalid', N'office-document-expanded-xml-limit', N'office-document-element-limit', N'office-document-depth-limit', N'office-document-text-limit', N'office-document-part-unsupported', N'office-document-input-too-large')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceProcessorForceRequests_StateShape",
                table: "SourceProcessorForceRequests",
                sql: "(([State] = 0 AND [ClaimedAtUtc] IS NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR ([State] = 1 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NULL AND [TerminalReasonCode] IS NULL) OR ([State] = 2 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'completed') OR ([State] = 3 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IN (N'office-document-container-invalid', N'office-document-encrypted', N'office-document-xml-invalid', N'office-document-expanded-xml-limit', N'office-document-element-limit', N'office-document-depth-limit', N'office-document-text-limit', N'office-document-part-unsupported', N'office-document-input-too-large', N'retained-artifact-missing', N'retained-artifact-path-invalid', N'retained-artifact-checksum-invalid')) OR ([State] = 4 AND [ClaimedAtUtc] IS NOT NULL AND [TerminalAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] = N'force-request-transient') OR ([State] = 5 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND [TerminalReasonCode] IN (N'force-request-cancelled', N'force-request-descriptor-disabled') AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL) OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL))) OR ([State] = 6 AND [TerminalAtUtc] IS NOT NULL AND [TerminalReceiptFingerprint] IS NOT NULL AND (([ClaimedAtUtc] IS NULL AND [ForceAttemptBranchId] IS NULL AND [TerminalReasonCode] = N'force-request-claim-expired') OR ([ClaimedAtUtc] IS NOT NULL AND [ForceAttemptBranchId] IS NOT NULL AND [TerminalReasonCode] = N'lease-expired-reconciled'))))");
        }
    }
}
