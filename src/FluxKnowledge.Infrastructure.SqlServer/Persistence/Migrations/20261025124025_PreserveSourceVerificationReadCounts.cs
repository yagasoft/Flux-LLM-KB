using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveSourceVerificationReadCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReadCountsIncomplete",
                table: "SourceVerificationRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Older execution deferred totals until finalisation, so an earlier crash cannot be ruled out.
            // Defer column resolution when EF emits ADD and UPDATE in one idempotent batch.
            migrationBuilder.Sql("EXEC(N'UPDATE [SourceVerificationRuns] SET [ReadCountsIncomplete] = 1;');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadCountsIncomplete",
                table: "SourceVerificationRuns");
        }
    }
}
