using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSegmentedPreviewWorklists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "DraftGenerationId",
                table: "IndexPublicationCandidates",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<long>(
                name: "AfterCount",
                table: "IndexPublicationCandidates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "BeforeCount",
                table: "IndexPublicationCandidates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LastAppendOrdinal",
                table: "IndexPublicationCandidates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "LastPageChecksum",
                table: "IndexPublicationCandidates",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerInstanceId",
                table: "IndexPublicationCandidates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OwnerMachineFingerprint",
                table: "IndexPublicationCandidates",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "OwnerProcessId",
                table: "IndexPublicationCandidates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OwnerStartedAtUtc",
                table: "IndexPublicationCandidates",
                type: "datetimeoffset(7)",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "WorklistComplete",
                table: "IndexPublicationCandidates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "IndexCandidateMembers",
                columns: table => new
                {
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Phase = table.Column<int>(type: "int", nullable: false),
                    VectorId = table.Column<long>(type: "bigint", nullable: false),
                    PayloadChecksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    SourceIdentityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexCandidateMembers", x => new { x.CandidateId, x.Phase, x.VectorId });
                    table.CheckConstraint("CK_IndexCandidateMembers_Phase", "[Phase] IN (0, 1) AND [VectorId] > 0");
                    table.ForeignKey(
                        name: "FK_IndexCandidateMembers_IndexPublicationCandidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "IndexPublicationCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndexCandidateMembers_VectorId",
                table: "IndexCandidateMembers",
                column: "VectorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndexCandidateMembers");

            migrationBuilder.DropColumn(
                name: "AfterCount",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "BeforeCount",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "LastAppendOrdinal",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "LastPageChecksum",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "OwnerInstanceId",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "OwnerMachineFingerprint",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "OwnerProcessId",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "OwnerStartedAtUtc",
                table: "IndexPublicationCandidates");

            migrationBuilder.DropColumn(
                name: "WorklistComplete",
                table: "IndexPublicationCandidates");

            migrationBuilder.AlterColumn<Guid>(
                name: "DraftGenerationId",
                table: "IndexPublicationCandidates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
