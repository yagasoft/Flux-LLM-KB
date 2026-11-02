using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVectorPublicationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Vectors_PublicationMetadata",
                table: "Vectors",
                column: "VectorId")
                .Annotation("SqlServer:Include", new[] { "TextChunkId", "SourceRevision", "PayloadChecksum", "TextChunkContentHash", "SearchInputHash", "IsDeleted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Vectors_PublicationMetadata",
                table: "Vectors");
        }
    }
}
