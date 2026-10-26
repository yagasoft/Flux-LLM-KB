using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceVerificationResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidateBindingJson",
                table: "SourceVerificationItems",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReceiptByteLength",
                table: "SourceVerificationItems",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptSha256",
                table: "SourceVerificationItems",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBaselineAtUtc",
                table: "SourceRootScanStates",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationItems_RunId_ReceiptSha256_ReceiptByteLength",
                table: "SourceVerificationItems",
                columns: new[] { "RunId", "ReceiptSha256", "ReceiptByteLength" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationItems_RunId_State_Id",
                table: "SourceVerificationItems",
                columns: new[] { "RunId", "State", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceVerificationItems_RunId_ReceiptSha256_ReceiptByteLength",
                table: "SourceVerificationItems");

            migrationBuilder.DropIndex(
                name: "IX_SourceVerificationItems_RunId_State_Id",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "CandidateBindingJson",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "ReceiptByteLength",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "ReceiptSha256",
                table: "SourceVerificationItems");

            migrationBuilder.DropColumn(
                name: "LastBaselineAtUtc",
                table: "SourceRootScanStates");
        }
    }
}
