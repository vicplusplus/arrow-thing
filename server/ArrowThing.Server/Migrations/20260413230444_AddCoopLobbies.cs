using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrowThing.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCoopLobbies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoopColor",
                table: "Users",
                type: "text",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "Lobbies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(
                        type: "character varying(6)",
                        maxLength: 6,
                        nullable: false
                    ),
                    Name = table.Column<string>(
                        type: "character varying(40)",
                        maxLength: 40,
                        nullable: false
                    ),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    MaxArrowLength = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    GeneratedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    CompletedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    DeletedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    LastActivityAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    SnapshotStrippedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lobbies", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "LobbyEvents",
                columns: table => new
                {
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<short>(type: "smallint", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArrowIndex = table.Column<int>(type: "integer", nullable: true),
                    TapX = table.Column<float>(type: "real", nullable: true),
                    TapY = table.Column<float>(type: "real", nullable: true),
                    CreatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LobbyEvents", x => new { x.LobbyId, x.Seq });
                }
            );

            migrationBuilder.CreateTable(
                name: "LobbyRegistrations",
                columns: table => new
                {
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayNameAtJoin = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: false
                    ),
                    ColorAtJoin = table.Column<string>(
                        type: "character varying(7)",
                        maxLength: 7,
                        nullable: false
                    ),
                    ClearCount = table.Column<int>(type: "integer", nullable: false),
                    AccumulatedMillis = table.Column<long>(type: "bigint", nullable: false),
                    JoinedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    FirstClearAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    FinishedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    LastActivityAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LobbyRegistrations", x => new { x.LobbyId, x.UserId });
                }
            );

            migrationBuilder.CreateTable(
                name: "LobbySnapshots",
                columns: table => new
                {
                    LobbyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<short>(type: "smallint", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    UpdatedAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LobbySnapshots", x => x.LobbyId);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Lobbies_Code",
                table: "Lobbies",
                column: "Code",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_Lobbies_LastActivityAt",
                table: "Lobbies",
                column: "LastActivityAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_Lobbies_OwnerUserId_Status",
                table: "Lobbies",
                columns: new[] { "OwnerUserId", "Status" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_LobbyRegistrations_UserId",
                table: "LobbyRegistrations",
                column: "UserId"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Lobbies");

            migrationBuilder.DropTable(name: "LobbyEvents");

            migrationBuilder.DropTable(name: "LobbyRegistrations");

            migrationBuilder.DropTable(name: "LobbySnapshots");

            migrationBuilder.DropColumn(name: "CoopColor", table: "Users");
        }
    }
}
