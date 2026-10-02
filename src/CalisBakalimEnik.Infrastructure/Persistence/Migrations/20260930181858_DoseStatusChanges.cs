using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalisBakalimEnik.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DoseStatusChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "previous_status",
                table: "medication_doses",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "previous_taken_at",
                table: "medication_doses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "status_changed_at",
                table: "medication_doses",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "previous_status",
                table: "medication_doses");

            migrationBuilder.DropColumn(
                name: "previous_taken_at",
                table: "medication_doses");

            migrationBuilder.DropColumn(
                name: "status_changed_at",
                table: "medication_doses");
        }
    }
}
