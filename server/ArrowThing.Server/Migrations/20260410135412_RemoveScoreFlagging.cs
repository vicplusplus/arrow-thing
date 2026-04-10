using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrowThing.Server.Migrations
{
    /// <inheritdoc />
    public partial class RemoveScoreFlagging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FlagReason", table: "Scores");

            migrationBuilder.DropColumn(name: "Flagged", table: "Scores");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FlagReason",
                table: "Scores",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "Flagged",
                table: "Scores",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );
        }
    }
}
