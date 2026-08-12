using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRetainedProcessorBranchMemberChildForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorBranchMembers_ChildSourceActivityId",
                table: "SourceProcessorBranchMembers",
                column: "ChildSourceActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceProcessorBranchMembers_ChildSourceRevisionId",
                table: "SourceProcessorBranchMembers",
                column: "ChildSourceRevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_SourceProcessorBranchMembers_SourceActivities_ChildSourceActivityId",
                table: "SourceProcessorBranchMembers",
                column: "ChildSourceActivityId",
                principalTable: "SourceActivities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SourceProcessorBranchMembers_SourceRevisions_ChildSourceRevisionId",
                table: "SourceProcessorBranchMembers",
                column: "ChildSourceRevisionId",
                principalTable: "SourceRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceProcessorBranchMembers_SourceActivities_ChildSourceActivityId",
                table: "SourceProcessorBranchMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_SourceProcessorBranchMembers_SourceRevisions_ChildSourceRevisionId",
                table: "SourceProcessorBranchMembers");

            migrationBuilder.DropIndex(
                name: "IX_SourceProcessorBranchMembers_ChildSourceActivityId",
                table: "SourceProcessorBranchMembers");

            migrationBuilder.DropIndex(
                name: "IX_SourceProcessorBranchMembers_ChildSourceRevisionId",
                table: "SourceProcessorBranchMembers");
        }
    }
}
