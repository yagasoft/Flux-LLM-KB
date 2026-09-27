using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCorpusPublicationVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CorpusVersion",
                table: "IndexState",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "CorpusEpoch",
                table: "IndexGenerations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CorpusVersion",
                table: "IndexGenerations",
                type: "bigint",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "IndexState",
                keyColumn: "Id",
                keyValue: 1,
                column: "CorpusVersion",
                value: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IndexState_CorpusVersion",
                table: "IndexState",
                sql: "[CorpusVersion] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IndexGenerations_CorpusStamp",
                table: "IndexGenerations",
                sql: "([CorpusEpoch] IS NULL AND [CorpusVersion] IS NULL) OR ([CorpusEpoch] IS NOT NULL AND [CorpusEpoch] <> '00000000-0000-0000-0000-000000000000' AND [CorpusVersion] IS NOT NULL AND [CorpusVersion] >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [IndexState] WHERE [CorpusVersion] <> 0)
                    OR EXISTS (SELECT 1 FROM [IndexGenerations] WHERE [CorpusEpoch] IS NOT NULL OR [CorpusVersion] IS NOT NULL)
                    THROW 51000, 'corpus-publication-version-downgrade-requires-empty-reset', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_IndexState_CorpusVersion",
                table: "IndexState");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IndexGenerations_CorpusStamp",
                table: "IndexGenerations");

            migrationBuilder.DropColumn(
                name: "CorpusVersion",
                table: "IndexState");

            migrationBuilder.DropColumn(
                name: "CorpusEpoch",
                table: "IndexGenerations");

            migrationBuilder.DropColumn(
                name: "CorpusVersion",
                table: "IndexGenerations");
        }
    }
}
