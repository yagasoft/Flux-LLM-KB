using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncrementalSourceScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceInventoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(max)", maxLength: 32767, nullable: false),
                    PathFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    FileId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ParentId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    IsDirectory = table.Column<bool>(type: "bit", nullable: false),
                    IsAncestor = table.Column<bool>(type: "bit", nullable: false),
                    BaselineEpoch = table.Column<long>(type: "bigint", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ObservationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceInventoryEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceInventoryEntries_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourcePendingChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(max)", maxLength: 32767, nullable: false),
                    PathFingerprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ReasonFlags = table.Column<long>(type: "bigint", nullable: false),
                    IsSubtree = table.Column<bool>(type: "bit", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    FirstUsn = table.Column<long>(type: "bigint", nullable: false),
                    LastUsn = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourcePendingChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourcePendingChanges_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceRootScanStates",
                columns: table => new
                {
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VolumeIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PolicyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BaselineEpoch = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BaselineStartUsn = table.Column<long>(type: "bigint", nullable: false),
                    CoveredUsn = table.Column<long>(type: "bigint", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    UnresolvedThroughUsn = table.Column<long>(type: "bigint", nullable: true),
                    LastReconciliationAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerificationPolicyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRootScanStates", x => x.SourceRootId);
                    table.ForeignKey(
                        name: "FK_SourceRootScanStates_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceVerificationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BytesRead = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceVerificationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceVerificationRuns_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceVolumeJournalCheckpoints",
                columns: table => new
                {
                    VolumeIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    JournalId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NextUsn = table.Column<long>(type: "bigint", nullable: false),
                    RegistrationGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastBatchFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceVolumeJournalCheckpoints", x => x.VolumeIdentity);
                });

            migrationBuilder.CreateTable(
                name: "SourceVerificationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AttemptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceVerificationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceVerificationItems_SourceVerificationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "SourceVerificationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceInventoryEntries_SourceRootId_FileId",
                table: "SourceInventoryEntries",
                columns: new[] { "SourceRootId", "FileId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceInventoryEntries_SourceRootId_PathFingerprint",
                table: "SourceInventoryEntries",
                columns: new[] { "SourceRootId", "PathFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourcePendingChanges_SourceRootId_PathFingerprint",
                table: "SourcePendingChanges",
                columns: new[] { "SourceRootId", "PathFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceRootScanStates_VolumeIdentity",
                table: "SourceRootScanStates",
                column: "VolumeIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationItems_RunId_InventoryEntryId",
                table: "SourceVerificationItems",
                columns: new[] { "RunId", "InventoryEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceVerificationRuns_SourceRootId_CreatedAtUtc",
                table: "SourceVerificationRuns",
                columns: new[] { "SourceRootId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceInventoryEntries");

            migrationBuilder.DropTable(
                name: "SourcePendingChanges");

            migrationBuilder.DropTable(
                name: "SourceRootScanStates");

            migrationBuilder.DropTable(
                name: "SourceVerificationItems");

            migrationBuilder.DropTable(
                name: "SourceVolumeJournalCheckpoints");

            migrationBuilder.DropTable(
                name: "SourceVerificationRuns");
        }
    }
}
