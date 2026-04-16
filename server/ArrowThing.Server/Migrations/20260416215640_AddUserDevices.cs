using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArrowThing.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceOtpCode",
                table: "Users",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "DeviceOtpCodeExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "DeviceOtpPendingDeviceIdHash",
                table: "Users",
                type: "text",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "LastDeviceOtpEmailAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "UserDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceIdHash = table.Column<string>(type: "text", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    LastSeenAt = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    UserAgent = table.Column<string>(
                        type: "character varying(512)",
                        maxLength: 512,
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserDevices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_UserDevices_UserId",
                table: "UserDevices",
                column: "UserId"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserDevices");

            migrationBuilder.DropColumn(name: "DeviceOtpCode", table: "Users");

            migrationBuilder.DropColumn(name: "DeviceOtpCodeExpiresAt", table: "Users");

            migrationBuilder.DropColumn(name: "DeviceOtpPendingDeviceIdHash", table: "Users");

            migrationBuilder.DropColumn(name: "LastDeviceOtpEmailAt", table: "Users");
        }
    }
}
