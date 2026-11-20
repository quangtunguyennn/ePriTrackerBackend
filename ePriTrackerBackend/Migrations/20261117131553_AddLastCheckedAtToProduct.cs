using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddLastCheckedAtToProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCheckedAt",
                table: "Product",
                type: "datetimeoffset",
                nullable: true);

            // Dữ liệu cũ: chưa biết lần cào thành công gần nhất → lấy tạm lần đổi giá gần nhất
            migrationBuilder.Sql("UPDATE [Product] SET [LastCheckedAt] = [LastUpdatedAt] WHERE [LastCheckedAt] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                table: "Product");
        }
    }
}
