using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <summary>
    /// Giá ban đầu chuyển từ Product (một giá chung cho mọi người) sang Item (giá lúc TỪNG người bắt đầu theo dõi) + thêm giá mục tiêu.
    /// Sửa tay so với bản EF sinh: thêm cột → chép dữ liệu cũ → rồi mới xoá cột Product.InitialPrice (EF xoá trước làm mất dữ liệu).
    /// Có DB đã đổi cột bằng tay mà không ghi lịch sử migration → mỗi bước chỉ chạy khi cần (EXEC để câu lệnh không bị
    /// biên dịch khi cột không tồn tại).
    /// </summary>
    public partial class MoveInitialPriceToItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Item', 'InitialPrice') IS NULL
    ALTER TABLE [Item] ADD [InitialPrice] decimal(18,2) NULL;
IF COL_LENGTH('Item', 'TargetPrice') IS NULL
    ALTER TABLE [Item] ADD [TargetPrice] decimal(18,2) NULL;");

            // Dữ liệu cũ chỉ có một giá ban đầu cho mỗi sản phẩm → gán cho mọi lượt theo dõi sản phẩm đó (giá 0 = chưa có giá → để null)
            migrationBuilder.Sql(@"
IF COL_LENGTH('Product', 'InitialPrice') IS NOT NULL
BEGIN
    EXEC(N'UPDATE i SET i.InitialPrice = p.InitialPrice
           FROM [Item] i
           JOIN [Product] p ON p.ProductId = i.ProductId
           WHERE p.InitialPrice > 0 AND i.InitialPrice IS NULL;');

    DECLARE @constraint sysname;
    SELECT @constraint = d.name
    FROM sys.default_constraints d
    JOIN sys.columns c ON d.parent_column_id = c.column_id AND d.parent_object_id = c.object_id
    WHERE d.parent_object_id = OBJECT_ID(N'[Product]') AND c.name = N'InitialPrice';
    IF @constraint IS NOT NULL EXEC(N'ALTER TABLE [Product] DROP CONSTRAINT [' + @constraint + N'];');

    EXEC(N'ALTER TABLE [Product] DROP COLUMN [InitialPrice];');
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InitialPrice",
                table: "Product",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Quay lại một giá chung: lấy giá ban đầu sớm nhất (nhỏ nhất) trong các lượt theo dõi
            migrationBuilder.Sql(@"
UPDATE p SET p.InitialPrice = x.InitialPrice
FROM [Product] p
JOIN (SELECT ProductId, MIN(InitialPrice) AS InitialPrice FROM [Item] WHERE InitialPrice IS NOT NULL GROUP BY ProductId) x
  ON x.ProductId = p.ProductId;");

            migrationBuilder.DropColumn(
                name: "InitialPrice",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "TargetPrice",
                table: "Item");
        }
    }
}
