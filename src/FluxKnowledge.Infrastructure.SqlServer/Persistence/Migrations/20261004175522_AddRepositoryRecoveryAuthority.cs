using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRepositoryRecoveryAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentDiscoveryEvidenceJson",
                table: "SourceRevisions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryRecoveryBindingJson",
                table: "PipelineRecords",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [SourceRevisions] WHERE [CurrentDiscoveryEvidenceJson] IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [PipelineRecords] WHERE [RepositoryRecoveryBindingJson] IS NOT NULL)
                    THROW 51000, 'repository-recovery-authority-downgrade-requires-empty-evidence', 1;
                """);
            migrationBuilder.DropColumn(
                name: "CurrentDiscoveryEvidenceJson",
                table: "SourceRevisions");

            migrationBuilder.DropColumn(
                name: "RepositoryRecoveryBindingJson",
                table: "PipelineRecords");
        }
    }
}
