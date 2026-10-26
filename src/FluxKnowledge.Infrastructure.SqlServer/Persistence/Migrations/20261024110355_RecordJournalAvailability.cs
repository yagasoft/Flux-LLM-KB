using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordJournalAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastFailureCode",
                table: "SourceVolumeJournalCheckpoints",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UnavailableSinceUtc",
                table: "SourceVolumeJournalCheckpoints",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastFailureCode",
                table: "SourceVolumeJournalCheckpoints");

            migrationBuilder.DropColumn(
                name: "UnavailableSinceUtc",
                table: "SourceVolumeJournalCheckpoints");
        }
    }
}
