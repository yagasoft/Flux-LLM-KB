using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookBrowseTargetPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetPath",
                table: "OutlookBrowseRequests",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "TargetPathFingerprint",
                table: "OutlookBrowseRequests",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            // Rows created before targeted browsing have no immutable target provenance.
            // Terminalise them rather than permitting an old host to complete a broad result.
            migrationBuilder.Sql("""
                UPDATE [OutlookBrowseRequests]
                SET [State] = 3,
                    [FailureCode] = 3,
                    [LeaseOwner] = NULL,
                    [LeaseExpiresAtUtc] = NULL,
                    [TargetPath] = NULL,
                    [TargetPathFingerprint] = NULL
                WHERE [TargetPath] IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Targeted Outlook browse cannot be rolled back without reintroducing unsafe broad browse semantics.");
    }
}
