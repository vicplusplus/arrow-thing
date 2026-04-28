using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrowThing.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEndlessScores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndlessScores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    BoardWidth = table.Column<int>(type: "integer", nullable: false),
                    BoardHeight = table.Column<int>(type: "integer", nullable: false),
                    TuningsVersion = table.Column<int>(type: "integer", nullable: false),
                    Clears = table.Column<int>(type: "integer", nullable: false),
                    LongestCombo = table.Column<int>(type: "integer", nullable: false),
                    DurationSeconds = table.Column<double>(
                        type: "double precision",
                        nullable: false
                    ),
                    ReplayJson = table.Column<string>(type: "text", nullable: false),
                    ReplayJsonGz = table.Column<byte[]>(type: "bytea", nullable: true),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    UpdatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndlessScores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EndlessScores_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_EndlessScores_BoardWidth_BoardHeight_Clears_DurationSeconds",
                table: "EndlessScores",
                columns: new[] { "BoardWidth", "BoardHeight", "Clears", "DurationSeconds" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_EndlessScores_UserId_BoardWidth_BoardHeight",
                table: "EndlessScores",
                columns: new[] { "UserId", "BoardWidth", "BoardHeight" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "EndlessScores");
        }
    }
}
