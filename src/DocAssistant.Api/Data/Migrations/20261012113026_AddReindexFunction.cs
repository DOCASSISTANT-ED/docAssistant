using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    // Hand-written. Re-indexing (docs/decisions.md #54) must find, across every tenant, the
    // documents whose chunks have no embedding or one made by another model. The app
    // connects as docassistant_app, which RLS limits to one tenant at a time, so this
    // function runs with its owner's rights and returns only document and tenant ids, as
    // unfinished_documents() does for recovery (#34).
    public partial class AddReindexFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IS DISTINCT FROM: also true when embedding_model is NULL.
            // SECURITY DEFINER and SET search_path: see AddIngestionRecoveryFunction.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION documents_to_reindex(current_model text)
                RETURNS TABLE (document_id uuid, tenant_id uuid)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = public, pg_temp
                AS $$
                    SELECT d.id, d.tenant_id
                    FROM documents AS d
                    WHERE EXISTS (
                        SELECT 1
                        FROM chunks AS c
                        WHERE c.document_id = d.id
                          AND (c.embedding IS NULL OR c.embedding_model IS DISTINCT FROM current_model))
                    ORDER BY d.created_at, d.id
                $$;
                """);

            // Functions are callable by everyone by default; only the app user may call this.
            migrationBuilder.Sql(
                """
                REVOKE ALL ON FUNCTION documents_to_reindex(text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION documents_to_reindex(text) TO docassistant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION documents_to_reindex(text);");
        }
    }
}
