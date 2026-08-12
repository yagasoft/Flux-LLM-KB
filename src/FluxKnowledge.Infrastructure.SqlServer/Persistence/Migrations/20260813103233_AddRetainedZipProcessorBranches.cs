using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetainedZipProcessorBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OriginKind",
                table: "SourceRevisions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SourceActivityRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PredecessorActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SuccessorActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelationshipKind = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceActivityRelations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceActivityRelations_SourceActivities_PredecessorActivityId",
                        column: x => x.PredecessorActivityId,
                        principalTable: "SourceActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceActivityRelations_SourceActivities_SuccessorActivityId",
                        column: x => x.SuccessorActivityId,
                        principalTable: "SourceActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorBranches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InputSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ProcessorVersion = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ProcessorFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(768)", maxLength: 768, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CompletionReceiptFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CompletedMemberCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorBranches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceProcessorBranches_SourceActivities_SourceActivityId",
                        column: x => x.SourceActivityId,
                        principalTable: "SourceActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceProcessorBranches_SourceRevisions_SourceRevisionId",
                        column: x => x.SourceRevisionId,
                        principalTable: "SourceRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    OutcomeCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    EvidenceJson = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceProcessorAttempts_SourceProcessorBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceProcessorBranchMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ChildSourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChildSourceActivityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Disposition = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceProcessorBranchMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceProcessorBranchMembers_SourceProcessorBranches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "SourceProcessorBranches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivityRelations_PredecessorActivityId",
                table: "SourceActivityRelations",
                column: "PredecessorActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceActivityRelations_SuccessorActivityId",
                table: "SourceActivityRelations",
                column: "SuccessorActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorAttempts_BranchId_LeaseGeneration",
                table: "SourceProcessorAttempts",
                columns: new[] { "BranchId", "LeaseGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorBranches_SourceActivityId",
                table: "SourceProcessorBranches",
                column: "SourceActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorBranches_SourceRevisionId",
                table: "SourceProcessorBranches",
                column: "SourceRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorBranches_State_LeaseExpiresAtUtc",
                table: "SourceProcessorBranches",
                columns: new[] { "State", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorBranchMembers_BranchId_MemberFingerprint",
                table: "SourceProcessorBranchMembers",
                columns: new[] { "BranchId", "MemberFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceActivityRelations");

            migrationBuilder.DropTable(
                name: "SourceProcessorAttempts");

            migrationBuilder.DropTable(
                name: "SourceProcessorBranchMembers");

            migrationBuilder.DropTable(
                name: "SourceProcessorBranches");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "SourceRevisions");
        }
    }
}
