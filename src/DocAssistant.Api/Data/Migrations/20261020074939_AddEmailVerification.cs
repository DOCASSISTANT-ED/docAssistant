using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocAssistant.Api.Data.Migrations
{
    /// <inheritdoc />
    // Generated, plus the UPDATE that counts the accounts made before e-mail verification as
    // verified (docs/decisions.md #70). No Row-Level Security on purpose: email_verifications
    // is an identity table, used before the account signs in to an organization (#15). The
    // app user's grants come from the default privileges set in EnableRowLevelSecurity.
    public partial class AddEmailVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts that existed before e-mail verification count as verified (#70);
            // otherwise nobody could sign in once unverified accounts are refused.
            migrationBuilder.Sql("UPDATE users SET email_verified_at = now();");

            migrationBuilder.CreateTable(
                name: "email_verifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    window_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sends_in_window = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_verifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_email_verifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_verifications_user_id",
                table: "email_verifications",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_verifications");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                table: "users");
        }
    }
}
