using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    // Hand-written. At startup the app must find unfinished documents of every tenant, but
    // it connects as docassistant_app, which RLS limits to one tenant at a time. This
    // function runs with its owner's rights (the migration user) and returns only document
    // and tenant ids, nothing else. See docs/decisions.md #34.
    public partial class AddIngestionRecoveryFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SECURITY DEFINER: runs as the owner, so RLS does not hide other tenants' rows.
            // SET search_path: the function always reads public.documents, never a table
            // a caller could slip in under the same name.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION unfinished_documents()
                RETURNS TABLE (document_id uuid, tenant_id uuid)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = public, pg_temp
                AS $$
                    SELECT id, tenant_id
                    FROM documents
                    WHERE status IN ('Pending', 'Processing')
                    ORDER BY created_at
                $$;
                """);

            // Functions are callable by everyone by default; only the app user may call this.
            migrationBuilder.Sql(
                """
                REVOKE ALL ON FUNCTION unfinished_documents() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION unfinished_documents() TO docassistant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION unfinished_documents();");
        }
    }
}
