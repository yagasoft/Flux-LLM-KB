using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowIdentitylessBlockedOutlookExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ProfileId",
                table: "OutlookCaptureExports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "FolderId",
                table: "OutlookCaptureExports",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutlookCaptureExports_IdentityRequiredUnlessBlocked",
                table: "OutlookCaptureExports",
                sql: "([State] = 4 AND [ProfileId] IS NULL AND [FolderId] IS NULL) OR ([ProfileId] IS NOT NULL AND [FolderId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE [operation]
                FROM [OutlookCaptureOperations] AS [operation]
                INNER JOIN [OutlookCaptureExports] AS [export]
                    ON [export].[Id] = [operation].[ResourceId]
                WHERE [export].[State] = 4
                  AND [export].[ProfileId] IS NULL
                  AND [export].[FolderId] IS NULL
                  AND [operation].[Kind] = N'ingest-ready-export';

                DELETE FROM [OutlookCaptureExports]
                WHERE [State] = 4
                  AND [ProfileId] IS NULL
                  AND [FolderId] IS NULL;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_OutlookCaptureExports_IdentityRequiredUnlessBlocked",
                table: "OutlookCaptureExports");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProfileId",
                table: "OutlookCaptureExports",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "FolderId",
                table: "OutlookCaptureExports",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
