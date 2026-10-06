using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    // Hand-written. EnableRowLevelSecurity granted the tables that existed on its branch
    // (tenants, documents) plus default privileges for later tables. users and memberships
    // are created by an earlier migration, so neither covered them and registration failed
    // with "permission denied for table users". See docs/decisions.md #15 and #19.
    public partial class GrantIdentityTablesToAppUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                GRANT SELECT, INSERT, UPDATE, DELETE ON users, memberships TO docassistant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                REVOKE SELECT, INSERT, UPDATE, DELETE ON users, memberships FROM docassistant_app;
                """);
        }
    }
}
