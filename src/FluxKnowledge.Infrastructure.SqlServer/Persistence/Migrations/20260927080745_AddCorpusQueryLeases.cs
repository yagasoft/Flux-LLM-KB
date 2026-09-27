using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorpusQueryLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CorpusQueryLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorpusEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorpusVersion = table.Column<long>(type: "bigint", nullable: false),
                    ModelFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    OwnerInstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerProcessId = table.Column<int>(type: "int", nullable: false),
                    OwnerStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OwnerMachineFingerprint = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SqlSessionId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorpusQueryLeases", x => x.Id);
                    table.CheckConstraint("CK_CorpusQueryLeases_MachineFingerprint", "LEN([OwnerMachineFingerprint]) = 64 AND [OwnerMachineFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_CorpusQueryLeases_ModelFingerprint", "DATALENGTH([ModelFingerprint]) > 0 AND UNICODE(RIGHT([ModelFingerprint], 1)) NOT IN (9, 10, 11, 12, 13, 32, 133, 160, 5760, 8192, 8193, 8194, 8195, 8196, 8197, 8198, 8199, 8200, 8201, 8202, 8232, 8233, 8239, 8287, 12288)");
                    table.CheckConstraint("CK_CorpusQueryLeases_Ownership", "[OwnerInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [OwnerProcessId] > 0 AND [SqlSessionId] > 0");
                    table.CheckConstraint("CK_CorpusQueryLeases_Stamp", "[CorpusEpoch] <> '00000000-0000-0000-0000-000000000000' AND [CorpusVersion] >= 0 AND [Dimensions] > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CorpusQueryLeases_GenerationId",
                table: "CorpusQueryLeases",
                column: "GenerationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [CorpusQueryLeases])
                    THROW 51000, 'corpus-query-lease-downgrade-requires-drain', 1;
                """);
            migrationBuilder.DropTable(
                name: "CorpusQueryLeases");
        }
    }
}
