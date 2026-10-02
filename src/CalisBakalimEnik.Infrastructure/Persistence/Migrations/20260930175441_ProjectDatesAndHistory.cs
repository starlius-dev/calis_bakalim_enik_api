using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalisBakalimEnik.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectDatesAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "finished_on",
                table: "projects",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    field = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    old_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    new_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_changes_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_changes_project",
                table: "project_changes",
                columns: new[] { "owner_id", "project_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_project_changes_project_id",
                table: "project_changes",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_changes");

            migrationBuilder.DropColumn(
                name: "finished_on",
                table: "projects");
        }
    }
}
