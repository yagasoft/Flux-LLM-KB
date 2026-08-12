using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FluxKnowledge.Infrastructure.SqlServer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOperatorActionCapabilityInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [TR_OperatorActionCapabilityPolicies_Immutable]
                ON [OperatorActionCapabilityPolicies]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Operator action capability policies are immutable.', 1;
                END;
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER [TR_OperatorActionHardDenials_Immutable]
                ON [OperatorActionHardDenials]
                AFTER UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Operator action hard denials are immutable.', 1;
                END;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER [TR_OperatorActionCapabilityPolicies_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER [TR_OperatorActionHardDenials_Immutable];");

        }
    }
}
