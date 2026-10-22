using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <summary>
    /// Giá ban đầu chuyển từ Product (một giá chung cho mọi người) sang Item (giá lúc TỪNG người bắt đầu theo dõi) + thêm giá mục tiêu.
    /// Sửa tay so với bản EF sinh: thêm cột → chép dữ liệu cũ → rồi mới xoá cột Product.InitialPrice (EF xoá trước làm mất dữ liệu).
    /// </summary>
    public partial class MoveInitialPriceToItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InitialPrice",
                table: "Item",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TargetPrice",
                table: "Item",
                type: "decimal(18,2)",
                nullable: true);

            // Dữ liệu cũ chỉ có một giá ban đầu cho mỗi sản phẩm → gán cho mọi lượt theo dõi sản phẩm đó (giá 0 = chưa có giá → để null)
            migrationBuilder.Sql(@"
UPDATE i SET i.InitialPrice = p.InitialPrice
FROM [Item] i
JOIN [Product] p ON p.ProductId = i.ProductId
WHERE p.InitialPrice > 0;");

            migrationBuilder.DropColumn(
                name: "InitialPrice",
                table: "Product");
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
