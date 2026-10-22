using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAndEndsAtToEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndsAt",
                table: "Event",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EndsAtIsExact",
                table: "Event",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "Event",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Tiki"); // sửa tay: mọi sự kiện cũ là của Tiki (Admin đăng)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndsAt",
                table: "Event");

            migrationBuilder.DropColumn(
                name: "EndsAtIsExact",
                table: "Event");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "Event");
        }
    }
}
