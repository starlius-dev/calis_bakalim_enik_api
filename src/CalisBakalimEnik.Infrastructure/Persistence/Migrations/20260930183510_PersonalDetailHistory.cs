using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalisBakalimEnik.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersonalDetailHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "personal_detail_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    old_value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    new_value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personal_detail_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_personal_detail_changes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_personal_detail_changes_changed_at",
                table: "personal_detail_changes",
                column: "changed_at");

            migrationBuilder.CreateIndex(
                name: "ix_personal_detail_changes_user",
                table: "personal_detail_changes",
                columns: new[] { "user_id", "changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal_detail_changes");
        }
    }
}
