using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CalisBakalimEnik.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MfaAndSecurityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mfa_factors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    factor_type = table.Column<short>(type: "smallint", nullable: false),
                    secret_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    destination = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_factors", x => x.id);
                    table.CheckConstraint("ck_mfa_factor_type", "factor_type BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "fk_mfa_factors_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mfa_recovery_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_recovery_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_recovery_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<short>(type: "smallint", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_mfa_factors_primary",
                table: "mfa_factors",
                column: "user_id",
                unique: true,
                filter: "is_primary AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_mfa_factors_user_type",
                table: "mfa_factors",
                columns: new[] { "user_id", "factor_type" },
                unique: true,
                filter: "deleted_at IS NULL AND factor_type <> 4");

            migrationBuilder.CreateIndex(
                name: "ix_mfa_recovery_codes_user",
                table: "mfa_recovery_codes",
                column: "user_id",
                filter: "used_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_security_events_ip",
                table: "security_events",
                columns: new[] { "ip", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_type",
                table: "security_events",
                columns: new[] { "event_type", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_security_events_user",
                table: "security_events",
                columns: new[] { "user_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mfa_factors");

            migrationBuilder.DropTable(
                name: "mfa_recovery_codes");

            migrationBuilder.DropTable(
                name: "security_events");
        }
    }
}
