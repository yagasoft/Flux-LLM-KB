using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindOutlookProfileSourceRoot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceRootId",
                table: "OutlookCaptureProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [OutlookCaptureProfiles]
                SET [SourceRootId] = NEWID()
                WHERE [SourceRootId] IS NULL;

                INSERT INTO [SourceRootConfigurations]
                    ([Id], [CanonicalPath], [DisplayName], [State], [Recursive], [IncludePatternsJson],
                     [ExcludePatternsJson], [FollowLinks], [MaximumFileBytes], [AllowedClassificationsJson],
                     [CrawlMode], [ReconciliationCadenceSeconds], [ConfigurationRevision], [CreatedAtUtc], [UpdatedAtUtc])
                SELECT
                    [SourceRootId],
                    CONCAT(N'C:\.fluxknowledge-private\outlook\', REPLACE(CONVERT(nvarchar(36), [SourceRootId]), N'-', N'')),
                    N'Private Outlook capture',
                    1,
                    0,
                    N'[]',
                    N'[]',
                    0,
                    67108864,
                    N'[]',
                    0,
                    86400,
                    1,
                    [CreatedAtUtc],
                    [UpdatedAtUtc]
                FROM [OutlookCaptureProfiles];
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "SourceRootId",
                table: "OutlookCaptureProfiles",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutlookCaptureProfiles_SourceRootId",
                table: "OutlookCaptureProfiles",
                column: "SourceRootId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OutlookCaptureProfiles_SourceRootConfigurations_SourceRootId",
                table: "OutlookCaptureProfiles",
                column: "SourceRootId",
                principalTable: "SourceRootConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OutlookCaptureProfiles_SourceRootConfigurations_SourceRootId",
                table: "OutlookCaptureProfiles");

            migrationBuilder.DropIndex(
                name: "IX_OutlookCaptureProfiles_SourceRootId",
                table: "OutlookCaptureProfiles");

            migrationBuilder.Sql(
                """
                DELETE [root]
                FROM [SourceRootConfigurations] AS [root]
                INNER JOIN [OutlookCaptureProfiles] AS [profile] ON [profile].[SourceRootId] = [root].[Id]
                WHERE NOT EXISTS (
                    SELECT 1 FROM [SourceRevisions] AS [revision] WHERE [revision].[SourceRootId] = [root].[Id]);
                """);

            migrationBuilder.DropColumn(
                name: "SourceRootId",
                table: "OutlookCaptureProfiles");
        }
    }
}
