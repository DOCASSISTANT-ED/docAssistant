using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_platform_admin",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "email_domains",
                table: "tenants",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "normalized_name",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "registration_code_hash",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Existing companies get the same normalized name Tenant.NormalizeName gives a new
            // one (docs/decisions.md #70): trimmed, one space between words, i ı İ I all as I,
            // upper case. Filled before the unique index, which empty names would break. If
            // two existing companies share a normalized name, the index cannot be created and
            // the migration stops: one of them has to be renamed first.
            migrationBuilder.Sql(
                """
                UPDATE tenants
                SET normalized_name = upper(translate(
                    regexp_replace(regexp_replace(name, '^\s+|\s+$', '', 'g'), '\s+', ' ', 'g'),
                    'iıİ', 'III'));
                """);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_normalized_name",
                table: "tenants",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_registration_code_hash",
                table: "tenants",
                column: "registration_code_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenants_normalized_name",
                table: "tenants");

            migrationBuilder.DropIndex(
                name: "ix_tenants_registration_code_hash",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "is_platform_admin",
                table: "users");

            migrationBuilder.DropColumn(
                name: "email_domains",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "normalized_name",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "registration_code_hash",
                table: "tenants");
        }
    }
}
