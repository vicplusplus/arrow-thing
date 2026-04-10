using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrowThing.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUserFlagging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FlagReason",
                table: "Users",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "Flagged",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FlagReason", table: "Users");

            migrationBuilder.DropColumn(name: "Flagged", table: "Users");
        }
    }
}
