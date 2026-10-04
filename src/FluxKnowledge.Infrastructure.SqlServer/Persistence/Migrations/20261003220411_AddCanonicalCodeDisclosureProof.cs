using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalCodeDisclosureProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CanonicalCodeDisclosureProofs",
                columns: table => new
                {
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CanonicalHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    CanonicalLength = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    SpanCount = table.Column<int>(type: "int", nullable: false),
                    Checksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalCodeDisclosureProofs", x => new { x.ArtifactId, x.Fingerprint });
                    table.CheckConstraint("CK_CodeDisclosureProof_CanonicalHash", "LEN([CanonicalHash]) = 64 AND [CanonicalHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_CodeDisclosureProof_Checksum", "LEN([Checksum]) = 64 AND [Checksum] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_CodeDisclosureProof_LengthCount", "[CanonicalLength] >= 0 AND [SpanCount] >= 0 AND ([State] = 0 OR [SpanCount] = 0)");
                    table.CheckConstraint("CK_CodeDisclosureProof_State", "[State] BETWEEN 0 AND 2");
                    table.ForeignKey(
                        name: "FK_CanonicalCodeDisclosureProofs_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CanonicalCodeDisclosureSpans",
                columns: table => new
                {
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Start = table.Column<int>(type: "int", nullable: false),
                    End = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Checksum = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalCodeDisclosureSpans", x => new { x.ArtifactId, x.Fingerprint, x.Start, x.End, x.Kind });
                    table.CheckConstraint("CK_CodeDisclosureSpan_Checksum", "LEN([Checksum]) = 64 AND [Checksum] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_CodeDisclosureSpan_Range", "[Start] >= 0 AND [End] > [Start] AND ([Kind] = 1 OR ([Kind] = 0 AND [End] = [Start] + 1))");
                    table.ForeignKey(
                        name: "FK_CanonicalCodeDisclosureSpans_CanonicalCodeDisclosureProofs_ArtifactId_Fingerprint",
                        columns: x => new { x.ArtifactId, x.Fingerprint },
                        principalTable: "CanonicalCodeDisclosureProofs",
                        principalColumns: new[] { "ArtifactId", "Fingerprint" },
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanonicalCodeDisclosureSpans");

            migrationBuilder.DropTable(
                name: "CanonicalCodeDisclosureProofs");
        }
    }
}
