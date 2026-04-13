using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrowThing.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFlagTriggerContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FlagTriggerBoardHeight",
                table: "Users",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "FlagTriggerBoardWidth",
                table: "Users",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "FlagTriggerGameId",
                table: "Users",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "FlagTriggerSeed",
                table: "Users",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "FlaggedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "FlagTriggerBoardHeight", table: "Users");

            migrationBuilder.DropColumn(name: "FlagTriggerBoardWidth", table: "Users");

            migrationBuilder.DropColumn(name: "FlagTriggerGameId", table: "Users");

            migrationBuilder.DropColumn(name: "FlagTriggerSeed", table: "Users");

            migrationBuilder.DropColumn(name: "FlaggedAt", table: "Users");
        }
    }
}
