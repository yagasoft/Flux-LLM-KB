using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase3BWatcherCorpusEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "AuditEvents",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "EventFamily",
                table: "AuditEvents",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Severity",
                table: "AuditEvents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceActivityId",
                table: "AuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceRevisionId",
                table: "AuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceRootId",
                table: "AuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceScanRequestId",
                table: "AuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceRootWatchStates",
                columns: table => new
                {
                    SourceRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstSignalAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    LastSignalAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    SignalCount = table.Column<int>(type: "int", nullable: false),
                    DebounceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    DueAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRootWatchStates", x => x.SourceRootId);
                    table.ForeignKey(
                        name: "FK_SourceRootWatchStates_SourceRootConfigurations_SourceRootId",
                        column: x => x.SourceRootId,
                        principalTable: "SourceRootConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAtUtc_Id",
                table: "AuditEvents",
                columns: new[] { "OccurredAtUtc", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SourceActivityId",
                table: "AuditEvents",
                column: "SourceActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SourceRevisionId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "SourceRevisionId", "OccurredAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SourceRootId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "SourceRootId", "OccurredAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SourceScanRequestId",
                table: "AuditEvents",
                column: "SourceScanRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceRootWatchStates_DueAtUtc_LeaseExpiresAtUtc",
                table: "SourceRootWatchStates",
                columns: new[] { "DueAtUtc", "LeaseExpiresAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_SourceActivities_SourceActivityId",
                table: "AuditEvents",
                column: "SourceActivityId",
                principalTable: "SourceActivities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_SourceRevisions_SourceRevisionId",
                table: "AuditEvents",
                column: "SourceRevisionId",
                principalTable: "SourceRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_SourceRootConfigurations_SourceRootId",
                table: "AuditEvents",
                column: "SourceRootId",
                principalTable: "SourceRootConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEvents_SourceScanRequests_SourceScanRequestId",
                table: "AuditEvents",
                column: "SourceScanRequestId",
                principalTable: "SourceScanRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                IF EXISTS (SELECT 1 FROM [SourceRootWatchStates])
                    THROW 51000, 'Cannot downgrade Phase 3B while durable watcher state exists.', 1;

                IF EXISTS
                (
                    SELECT 1
                    FROM [AuditEvents]
                    WHERE [SourceRootId] IS NOT NULL
                       OR [SourceScanRequestId] IS NOT NULL
                       OR [SourceRevisionId] IS NOT NULL
                       OR [SourceActivityId] IS NOT NULL
                       OR [CorrelationId] IS NOT NULL
                       OR [EventFamily] IS NOT NULL
                       OR [Severity] IS NOT NULL
                )
                    THROW 51000, 'Cannot downgrade Phase 3B while correlated durable audit events exist.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_SourceActivities_SourceActivityId",
                table: "AuditEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_SourceRevisions_SourceRevisionId",
                table: "AuditEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_SourceRootConfigurations_SourceRootId",
                table: "AuditEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_AuditEvents_SourceScanRequests_SourceScanRequestId",
                table: "AuditEvents");

            migrationBuilder.DropTable(
                name: "SourceRootWatchStates");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_OccurredAtUtc_Id",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SourceActivityId",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SourceRevisionId_OccurredAtUtc",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SourceRootId_OccurredAtUtc",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_SourceScanRequestId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "EventFamily",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "SourceActivityId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "SourceRevisionId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "SourceRootId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "SourceScanRequestId",
                table: "AuditEvents");
        }
    }
}
