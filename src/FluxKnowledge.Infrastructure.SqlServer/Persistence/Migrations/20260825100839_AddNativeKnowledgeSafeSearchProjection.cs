using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeKnowledgeSafeSearchProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KnowledgeItems_ForgottenAtUtc",
                table: "KnowledgeItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeItems_ContentShape",
                table: "KnowledgeItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeClaims_ContentShape",
                table: "KnowledgeClaims");

            migrationBuilder.AddColumn<string>(
                name: "SafeSearchText",
                table: "KnowledgeItems",
                type: "nvarchar(max)",
                maxLength: 16384,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SafeSearchText",
                table: "KnowledgeClaims",
                type: "nvarchar(max)",
                maxLength: 4096,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE [KnowledgeItems] SET [SafeSearchText] = CONCAT([Title], NCHAR(10), [SafeBody]) WHERE [ForgottenAtUtc] IS NULL;");
            migrationBuilder.Sql("UPDATE [KnowledgeClaims] SET [SafeSearchText] = CONCAT([Subject], NCHAR(10), [Predicate], NCHAR(10), [ObjectText]) WHERE [ForgottenAtUtc] IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_ForgottenAtUtc_Title",
                table: "KnowledgeItems",
                columns: new[] { "ForgottenAtUtc", "Title" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeItems_ContentShape",
                table: "KnowledgeItems",
                sql: "([ForgottenAtUtc] IS NULL AND LEN([Title]) > 0 AND LEN([SafeBody]) > 0 AND LEN([SafeSearchText]) > 0) OR ([ForgottenAtUtc] IS NOT NULL AND [Title] = N'' AND [SafeBody] = N'' AND [SafeSearchText] = N'')");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeClaims_ForgottenAtUtc_LifecycleState_Subject",
                table: "KnowledgeClaims",
                columns: new[] { "ForgottenAtUtc", "LifecycleState", "Subject" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeClaims_ContentShape",
                table: "KnowledgeClaims",
                sql: "([ForgottenAtUtc] IS NULL AND LEN([CanonicalIdentity]) > 0 AND LEN([Subject]) > 0 AND LEN([Predicate]) > 0 AND LEN([ObjectText]) > 0 AND LEN([SafeSearchText]) > 0) OR ([ForgottenAtUtc] IS NOT NULL AND [CanonicalIdentity] = N'' AND [Subject] = N'' AND [Predicate] = N'' AND [ObjectText] = N'' AND [SafeSearchText] = N'')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_KnowledgeItems_ForgottenAtUtc_Title",
                table: "KnowledgeItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeItems_ContentShape",
                table: "KnowledgeItems");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeClaims_ForgottenAtUtc_LifecycleState_Subject",
                table: "KnowledgeClaims");

            migrationBuilder.DropCheckConstraint(
                name: "CK_KnowledgeClaims_ContentShape",
                table: "KnowledgeClaims");

            migrationBuilder.DropColumn(
                name: "SafeSearchText",
                table: "KnowledgeItems");

            migrationBuilder.DropColumn(
                name: "SafeSearchText",
                table: "KnowledgeClaims");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeItems_ForgottenAtUtc",
                table: "KnowledgeItems",
                column: "ForgottenAtUtc");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeItems_ContentShape",
                table: "KnowledgeItems",
                sql: "([ForgottenAtUtc] IS NULL AND LEN([Title]) > 0 AND LEN([SafeBody]) > 0) OR ([ForgottenAtUtc] IS NOT NULL AND [Title] = N'' AND [SafeBody] = N'')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_KnowledgeClaims_ContentShape",
                table: "KnowledgeClaims",
                sql: "([ForgottenAtUtc] IS NULL AND LEN([CanonicalIdentity]) > 0 AND LEN([Subject]) > 0 AND LEN([Predicate]) > 0 AND LEN([ObjectText]) > 0) OR ([ForgottenAtUtc] IS NOT NULL AND [CanonicalIdentity] = N'' AND [Subject] = N'' AND [Predicate] = N'' AND [ObjectText] = N'')");
        }
    }
}
