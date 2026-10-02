using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalisBakalimEnik.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefreshTokenInstallationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "installation_id",
                table: "refresh_tokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "installation_id",
                table: "refresh_tokens");
        }
    }
}
