using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeKnowledgeGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalIdentity = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: false),
                    CanonicalIdentityHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Predicate = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObjectText = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    LifecycleState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ForgottenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeClaims", x => x.Id);
                    table.CheckConstraint("CK_KnowledgeClaims_Confidence", "[Confidence] >= 0 AND [Confidence] <= 1");
                    table.CheckConstraint("CK_KnowledgeClaims_ContentShape", "([ForgottenAtUtc] IS NULL AND LEN([CanonicalIdentity]) > 0 AND LEN([Subject]) > 0 AND LEN([Predicate]) > 0 AND LEN([ObjectText]) > 0) OR ([ForgottenAtUtc] IS NOT NULL AND [CanonicalIdentity] = N'' AND [Subject] = N'' AND [Predicate] = N'' AND [ObjectText] = N'')");
                    table.CheckConstraint("CK_KnowledgeClaims_Lifecycle", "[LifecycleState] IN (N'active', N'superseded', N'retracted')");
                    table.CheckConstraint("CK_KnowledgeClaims_Revision", "[Revision] > 0");
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SafeBody = table.Column<string>(type: "nvarchar(max)", maxLength: 16384, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ForgottenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeItems", x => x.Id);
                    table.CheckConstraint("CK_KnowledgeItems_ContentShape", "([ForgottenAtUtc] IS NULL AND LEN([Title]) > 0 AND LEN([SafeBody]) > 0) OR ([ForgottenAtUtc] IS NOT NULL AND [Title] = N'' AND [SafeBody] = N'')");
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeTombstones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ForgottenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeTombstones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeClaimHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    LifecycleState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Confidence = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeClaimHistory", x => x.Id);
                    table.CheckConstraint("CK_KnowledgeClaimHistory_Confidence", "[Confidence] >= 0 AND [Confidence] <= 1");
                    table.ForeignKey(
                        name: "FK_KnowledgeClaimHistory_KnowledgeClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "KnowledgeClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeRelations",
                columns: table => new
                {
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Predicate = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObjectText = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeRelations", x => x.ClaimId);
                    table.ForeignKey(
                        name: "FK_KnowledgeRelations_KnowledgeClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "KnowledgeClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeClaimHistory_ClaimId_Revision",
                table: "KnowledgeClaimHistory",
                columns: new[] { "ClaimId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeClaims_CanonicalIdentityHash",
                table: "KnowledgeClaims",
                column: "CanonicalIdentityHash",
                unique: true,
                filter: "[ForgottenAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeClaims_ForgottenAtUtc_LifecycleState",
                table: "KnowledgeClaims",
                columns: new[] { "ForgottenAtUtc", "LifecycleState" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_ForgottenAtUtc",
                table: "KnowledgeItems",
                column: "ForgottenAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelations_ObjectText",
                table: "KnowledgeRelations",
                column: "ObjectText");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeRelations_Subject",
                table: "KnowledgeRelations",
                column: "Subject");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeTombstones_TargetKind_TargetId",
                table: "KnowledgeTombstones",
                columns: new[] { "TargetKind", "TargetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeClaimHistory");

            migrationBuilder.DropTable(
                name: "KnowledgeItems");

            migrationBuilder.DropTable(
                name: "KnowledgeRelations");

            migrationBuilder.DropTable(
                name: "KnowledgeTombstones");

            migrationBuilder.DropTable(
                name: "KnowledgeClaims");
        }
    }
}
