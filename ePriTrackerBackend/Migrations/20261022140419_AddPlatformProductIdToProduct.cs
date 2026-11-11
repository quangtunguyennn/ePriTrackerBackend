using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformProductIdToProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Có DB đã thêm cột bằng tay mà không ghi lịch sử migration → chỉ thêm khi chưa có
            migrationBuilder.Sql(@"
IF COL_LENGTH('Product', 'PlatformProductId') IS NULL
    ALTER TABLE [Product] ADD [PlatformProductId] nvarchar(50) NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlatformProductId",
                table: "Product");
        }
    }
}
