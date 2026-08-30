using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeV1OperationLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NativeOperationFenceTargets",
                columns: table => new
                {
                    TargetId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Value = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeOperationFenceTargets", x => x.TargetId);
                });

            migrationBuilder.CreateTable(
                name: "NativeOperationIntents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActorSurface = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ConfirmationHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TargetMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeOperationIntents", x => x.Id);
                    table.CheckConstraint("CK_NativeOperationIntents_ConfirmationHash", "LEN([ConfirmationHash]) = 64 AND [ConfirmationHash] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_NativeOperationIntents_Expiry", "[ExpiresAtUtc] > [CreatedAtUtc]");
                    table.CheckConstraint("CK_NativeOperationIntents_RequestFingerprint", "LEN([RequestFingerprint]) = 64 AND [RequestFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.CheckConstraint("CK_NativeOperationIntents_TargetMetadataBounded", "DATALENGTH([TargetMetadataJson]) <= 32768");
                });

            migrationBuilder.CreateTable(
                name: "NativeOperationReceipts",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IntentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ActorSurface = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IdempotencyKey = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Outcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ReasonCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NativeOperationReceipts", x => x.OperationId);
                    table.CheckConstraint("CK_NativeOperationReceipts_IdempotencyKey", "DATALENGTH([IdempotencyKey]) > 0 AND DATALENGTH([IdempotencyKey]) <= 128");
                    table.CheckConstraint("CK_NativeOperationReceipts_RequestFingerprint", "LEN([RequestFingerprint]) = 64 AND [RequestFingerprint] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9a-f]%'");
                    table.ForeignKey(
                        name: "FK_NativeOperationReceipts_NativeOperationIntents_IntentId",
                        column: x => x.IntentId,
                        principalTable: "NativeOperationIntents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NativeOperationIntents_ConfirmationHash",
                table: "NativeOperationIntents",
                column: "ConfirmationHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NativeOperationIntents_ExpiresAtUtc_ConsumedAtUtc",
                table: "NativeOperationIntents",
                columns: new[] { "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NativeOperationReceipts_ActorSurface_IdempotencyKey",
                table: "NativeOperationReceipts",
                columns: new[] { "ActorSurface", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NativeOperationReceipts_IntentId",
                table: "NativeOperationReceipts",
                column: "IntentId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NativeOperationFenceTargets");

            migrationBuilder.DropTable(
                name: "NativeOperationReceipts");

            migrationBuilder.DropTable(
                name: "NativeOperationIntents");
        }
    }
}
