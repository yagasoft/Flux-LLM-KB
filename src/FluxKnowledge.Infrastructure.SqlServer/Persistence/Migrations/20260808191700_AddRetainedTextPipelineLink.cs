using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetainedTextPipelineLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceRevisionId",
                table: "PipelineRecords",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRecords_SourceRevisionId",
                table: "PipelineRecords",
                column: "SourceRevisionId",
                unique: true,
                filter: "[SourceRevisionId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PipelineRecords_SourceRevisions_SourceRevisionId",
                table: "PipelineRecords",
                column: "SourceRevisionId",
                principalTable: "SourceRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PipelineRecords_SourceRevisions_SourceRevisionId",
                table: "PipelineRecords");

            migrationBuilder.DropIndex(
                name: "IX_PipelineRecords_SourceRevisionId",
                table: "PipelineRecords");

            migrationBuilder.DropColumn(
                name: "SourceRevisionId",
                table: "PipelineRecords");
        }
    }
}
