using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResumableSourceDeletionCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastCommittedLeaseId",
                table: "SourceDeletionOperations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastProgressAtUtc",
                table: "SourceDeletionOperations",
                type: "datetimeoffset(7)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MembershipRowsDeleted",
                table: "SourceDeletionOperations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "MembershipVectorCursor",
                table: "SourceDeletionOperations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResumePhase",
                table: "SourceDeletionOperations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceDeletionOperations_MembershipRowsDeleted",
                table: "SourceDeletionOperations",
                sql: "[MembershipRowsDeleted] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SourceDeletionOperations_MembershipVectorCursor",
                table: "SourceDeletionOperations",
                sql: "[MembershipVectorCursor] IS NULL OR [MembershipVectorCursor] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceDeletionOperations_MembershipRowsDeleted",
                table: "SourceDeletionOperations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SourceDeletionOperations_MembershipVectorCursor",
                table: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "LastCommittedLeaseId",
                table: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "LastProgressAtUtc",
                table: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "MembershipRowsDeleted",
                table: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "MembershipVectorCursor",
                table: "SourceDeletionOperations");

            migrationBuilder.DropColumn(
                name: "ResumePhase",
                table: "SourceDeletionOperations");
        }
    }
}
