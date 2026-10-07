using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    // Generated, plus a hand-written Row-Level Security part at the end of Up (EF Core
    // cannot generate RLS). The app user's table grants come from the default privileges
    // set in EnableRowLevelSecurity. See docs/decisions.md #15 and #29.
    public partial class AddChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: true),
                    section_path = table.Column<string>(type: "text", nullable: true),
                    source_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    embedding = table.Column<Vector>(type: "vector(1024)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chunks", x => x.id);
                    table.ForeignKey(
                        name: "fk_chunks_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chunks_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chunks_document_id_ordinal",
                table: "chunks",
                columns: new[] { "document_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chunks_tenant_id",
                table: "chunks",
                column: "tenant_id");

            // Same policy as documents: FORCE applies it to the table owner too, and an
            // empty or missing app.tenant_id matches no rows.
            migrationBuilder.Sql(
                """
                ALTER TABLE chunks ENABLE ROW LEVEL SECURITY;
                ALTER TABLE chunks FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON chunks
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunks");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
