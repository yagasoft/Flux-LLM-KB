using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AlignDeferredCapabilityFingerprintCollation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ArtifactFingerprint",
                table: "DeferredCapabilities",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "ArtifactFingerprint",
                table: "DeferredCapabilities",
                type: "char(64)",
                unicode: false,
                fixedLength: true,
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "char(64)",
                oldUnicode: false,
                oldFixedLength: true,
                oldMaxLength: 64,
                oldCollation: "Latin1_General_100_BIN2");
        }
    }
}
