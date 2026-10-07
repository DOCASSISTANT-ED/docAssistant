using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentUploadFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited. The columns a document cannot exist without (file_name, content_type,
            // size_bytes, storage_key, uploaded_by_user_id) are added without a default, so a row
            // can never silently get an empty file name or storage key. That needs an empty
            // table: before this migration there was no upload endpoint, so any existing rows
            // are leftovers from manual experiments and have no file behind them.
            migrationBuilder.Sql("DELETE FROM documents;");

            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "content_type",
                table: "documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                table: "documents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "file_name",
                table: "documents",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processed_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "size_bytes",
                table: "documents",
                type: "bigint",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "storage_key",
                table: "documents",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false);

            migrationBuilder.AddColumn<Guid>(
                name: "uploaded_by_user_id",
                table: "documents",
                type: "uuid",
                nullable: false);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "documents",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_documents_storage_key",
                table: "documents",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_uploaded_by_user_id",
                table: "documents",
                column: "uploaded_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_documents_users_uploaded_by_user_id",
                table: "documents",
                column: "uploaded_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_documents_users_uploaded_by_user_id",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_documents_storage_key",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_documents_uploaded_by_user_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "content_type",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "file_name",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "processed_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "size_bytes",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "storage_key",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "uploaded_by_user_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "version",
                table: "documents");
        }
    }
}
