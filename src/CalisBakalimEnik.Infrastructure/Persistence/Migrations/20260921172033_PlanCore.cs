using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalisBakalimEnik.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlanCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "focus_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    planned_blocks = table.Column<short>(type: "smallint", nullable: false),
                    done_blocks = table.Column<short>(type: "smallint", nullable: false),
                    focus_seconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_focus_sessions", x => x.id);
                    table.CheckConstraint("ck_focus_seconds", "focus_seconds >= 0");
                    table.ForeignKey(
                        name: "fk_focus_sessions_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "schedule_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    day_of_week = table.Column<short>(type: "smallint", nullable: false),
                    starts_at = table.Column<TimeOnly>(type: "time", nullable: false),
                    ends_at = table.Column<TimeOnly>(type: "time", nullable: false),
                    location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    recurrence_rule = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedule_entries", x => x.id);
                    table.CheckConstraint("ck_schedule_day", "day_of_week BETWEEN 1 AND 7");
                    table.CheckConstraint("ck_schedule_span", "ends_at > starts_at");
                });

            migrationBuilder.CreateTable(
                name: "terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terms", x => x.id);
                    table.CheckConstraint("ck_terms_span", "ends_on >= starts_on");
                });

            migrationBuilder.CreateIndex(
                name: "ix_focus_owner_day",
                table: "focus_sessions",
                columns: new[] { "owner_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_focus_sessions_task_id",
                table: "focus_sessions",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedule_day",
                table: "schedule_entries",
                columns: new[] { "owner_id", "day_of_week" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_terms_current",
                table: "terms",
                column: "owner_id",
                unique: true,
                filter: "is_current AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "focus_sessions");

            migrationBuilder.DropTable(
                name: "schedule_entries");

            migrationBuilder.DropTable(
                name: "terms");
        }
    }
}
