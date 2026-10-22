using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddStockStatusToProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsInStock",
                table: "Product",
                type: "bit",
                nullable: false,
                // Sửa tay từ false → true: sản phẩm đã có trong DB mặc định là còn hàng.
                // Model không khai báo default (để EF luôn gửi giá trị thật khi lưu false) nên snapshot không đổi.
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutOfStockSince",
                table: "Product",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsInStock",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "OutOfStockSince",
                table: "Product");
        }
    }
}
