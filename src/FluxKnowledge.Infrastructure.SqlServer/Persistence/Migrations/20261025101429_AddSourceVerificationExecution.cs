using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceVerificationExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "SourceVerificationRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AuthorityJson",
                table: "SourceVerificationRuns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Manual",
                table: "SourceVerificationRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextBatchAtUtc",
                table: "SourceVerificationRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalPolicyJson",
                table: "SourceVerificationRuns",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "SourceVerificationRuns",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RetainedBytesRead",
                table: "SourceVerificationRuns",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledForUtc",
                table: "SourceVerificationRuns",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceBytesRead",
                table: "SourceVerificationRuns",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "SourceVerificationItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BindingJson",
                table: "SourceVerificationItems",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "SourceVerificationItems",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptJson",
                table: "SourceVerificationItems",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VerificationRunId",
                table: "SourceScanRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "VerificationPaused",
                table: "SourceRootScanStates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "VerificationPolicyRevision",
                table: "SourceRootScanStates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationRuns_Active_NextBatchAtUtc",
                table: "SourceVerificationRuns",
                columns: new[] { "Active", "NextBatchAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationRuns_SourceRootId",
                table: "SourceVerificationRuns",
                column: "SourceRootId",
                unique: true,
                filter: "[Active] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceVerificationRuns_Active_NextBatchAtUtc",
                table: "SourceVerificationRuns");

            migrationBuilder.DropIndex(
                name: "IX_SourceVerificationRuns_SourceRootId",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "Active",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "AuthorityJson",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "Manual",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "NextBatchAtUtc",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "OriginalPolicyJson",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "RetainedBytesRead",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "ScheduledForUtc",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "SourceBytesRead",
                table: "SourceVerificationRuns");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "BindingJson",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "ReceiptJson",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "VerificationRunId",
                table: "SourceScanRequests");

            migrationBuilder.DropColumn(
                name: "VerificationPaused",
                table: "SourceRootScanStates");

            migrationBuilder.DropColumn(
                name: "VerificationPolicyRevision",
                table: "SourceRootScanStates");
        }
    }
}
