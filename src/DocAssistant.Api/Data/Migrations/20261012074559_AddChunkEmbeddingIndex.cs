using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkEmbeddingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_chunks_embedding",
                table: "chunks",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_chunks_embedding",
                table: "chunks");
        }
    }
}
