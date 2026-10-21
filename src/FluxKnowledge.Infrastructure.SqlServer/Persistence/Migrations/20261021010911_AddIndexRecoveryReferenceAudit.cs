using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexRecoveryReferenceAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndexRecoveryAudit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    IsValid = table.Column<bool>(type: "bit", nullable: false),
                    ClassifierVersion = table.Column<int>(type: "int", nullable: false),
                    DatabaseIncarnation = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecoveryRoot = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    IndexRoot = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RootFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InventoryChecksum = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SealedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    FailurePath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexRecoveryAudit", x => x.Id);
                    table.CheckConstraint("CK_IndexRecoveryAudit_Singleton", "[Id]=1");
                });

            migrationBuilder.CreateTable(
                name: "IndexRecoveryPins",
                columns: table => new
                {
                    Id = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Packet = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    GenerationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IndexPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexRecoveryPins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndexRecoveryWriters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecoveryRoot = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    OwnerProcessId = table.Column<int>(type: "int", nullable: false),
                    OwnerStartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    OwnerMachineFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    OwnerExitSufficient = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexRecoveryWriters", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "IndexRecoveryAudit",
                columns: new[] { "Id", "ClassifierVersion", "DatabaseIncarnation", "FailurePath", "IndexRoot", "InventoryChecksum", "IsValid", "RecoveryRoot", "RootFingerprint", "SealedAtUtc" },
                values: new object[] { 1, 0, new Guid("00000000-0000-0000-0000-000000000000"), null, "", "", false, "", "", null });

            migrationBuilder.CreateIndex(
                name: "IX_IndexRecoveryPins_GenerationId",
                table: "IndexRecoveryPins",
                column: "GenerationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndexRecoveryAudit");

            migrationBuilder.DropTable(
                name: "IndexRecoveryPins");

            migrationBuilder.DropTable(
                name: "IndexRecoveryWriters");
        }
    }
}
