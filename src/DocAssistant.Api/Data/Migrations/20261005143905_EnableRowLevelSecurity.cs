using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    // Hand-written: EF Core cannot generate grants or Row-Level Security.
    // Requires the docassistant_app role (docker/postgres/init/02-app-user.sh).
    // See docs/decisions.md #15 and #19.
    public partial class EnableRowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The app user starts with no access; it gets exactly what is granted here.
            migrationBuilder.Sql(
                """
                GRANT USAGE ON SCHEMA public TO docassistant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON tenants, documents TO docassistant_app;
                """);

            // Tables created by later migrations are granted automatically.
            migrationBuilder.Sql(
                """
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO docassistant_app;
                """);

            // FORCE applies the policy to the table owner as well. Superusers still bypass it,
            // which is why the app must never connect as one.
            migrationBuilder.Sql(
                """
                ALTER TABLE documents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE documents FORCE ROW LEVEL SECURITY;
                """);

            // USING: which rows can be read, updated or deleted.
            // WITH CHECK: which rows can be written (blocks moving a row to another tenant).
            // An empty or missing app.tenant_id becomes NULL and matches no rows.
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON documents
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY tenant_isolation ON documents;
                ALTER TABLE documents NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE documents DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.Sql(
                """
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                    REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM docassistant_app;
                REVOKE SELECT, INSERT, UPDATE, DELETE ON tenants, documents FROM docassistant_app;
                REVOKE USAGE ON SCHEMA public FROM docassistant_app;
                """);
        }
    }
}
