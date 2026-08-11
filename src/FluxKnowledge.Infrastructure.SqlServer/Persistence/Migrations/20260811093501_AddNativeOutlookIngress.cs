using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeOutlookIngress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutlookBrowseRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfigurationRevision = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(768)", maxLength: 768, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    FailureCode = table.Column<int>(type: "int", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookBrowseRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutlookBrowseResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BrowseRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookBrowseResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCaptureExports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourceFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ManifestHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    RelativeSpoolPath = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true, collation: "Latin1_General_100_BIN2"),
                    State = table.Column<int>(type: "int", nullable: false),
                    SourceRevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCaptureExports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCaptureFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoreId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    FolderEntryId = table.Column<string>(type: "nvarchar(max)", nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Basis = table.Column<int>(type: "int", nullable: false),
                    CursorUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    CursorFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCaptureFolders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCaptureOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Accepted = table.Column<bool>(type: "bit", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCaptureOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCaptureProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SpoolRoot = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false, collation: "Latin1_General_100_BIN2"),
                    IncrementalBasis = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ConfigurationRevision = table.Column<long>(type: "bigint", nullable: false),
                    CadenceTicks = table.Column<long>(type: "bigint", nullable: false),
                    MaximumOverlapTicks = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCaptureProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutlookCatchUps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CoalescingKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Provenance = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(768)", maxLength: 768, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    LastHeartbeatAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutlookCatchUps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookBrowseRequests_State_ExpiresAtUtc",
                table: "OutlookBrowseRequests",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutlookBrowseResults_BrowseRequestId_FolderId",
                table: "OutlookBrowseResults",
                columns: new[] { "BrowseRequestId", "FolderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureOperations_OperationId",
                table: "OutlookCaptureOperations",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCatchUps_ProfileId_CoalescingKey_State",
                table: "OutlookCatchUps",
                columns: new[] { "ProfileId", "CoalescingKey", "State" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCatchUps_State_NotBeforeUtc_LeaseExpiresAtUtc",
                table: "OutlookCatchUps",
                columns: new[] { "State", "NotBeforeUtc", "LeaseExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutlookBrowseRequests");

            migrationBuilder.DropTable(
                name: "OutlookBrowseResults");

            migrationBuilder.DropTable(
                name: "OutlookCaptureExports");

            migrationBuilder.DropTable(
                name: "OutlookCaptureFolders");

            migrationBuilder.DropTable(
                name: "OutlookCaptureOperations");

            migrationBuilder.DropTable(
                name: "OutlookCaptureProfiles");

            migrationBuilder.DropTable(
                name: "OutlookCatchUps");
        }
    }
}
