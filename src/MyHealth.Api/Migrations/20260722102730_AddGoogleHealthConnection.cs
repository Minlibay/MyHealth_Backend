using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyHealth.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGoogleHealthConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleHealthConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshToken = table.Column<string>(type: "text", nullable: false),
                    Scopes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleHealthConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GoogleHealthConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GoogleHealthConnections_UserId",
                table: "GoogleHealthConnections",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleHealthConnections");
        }
    }
}
