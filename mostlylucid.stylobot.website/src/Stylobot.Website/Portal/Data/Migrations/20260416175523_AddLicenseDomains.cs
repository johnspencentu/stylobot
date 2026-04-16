using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stylobot.Website.Portal.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Domains",
                table: "licenses",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Domains",
                table: "licenses");
        }
    }
}
