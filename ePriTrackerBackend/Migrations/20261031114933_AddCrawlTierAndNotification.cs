using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <summary>
    /// Cào giá theo Tier (Product.NextCrawlAt, ActiveTrackersCount, ...) + thông báo giá mục tiêu (Item.IsTargetNotified, ..., bảng Notification).
    /// Sửa tay so với bản EF sinh: có DB đã thêm các cột/bảng này bằng tay (thay cho file AddCronJobAndTierFields hỏng trước đây)
    /// → mỗi phần chỉ tạo khi chưa có; sau đó tính lại dữ liệu từ bảng Item để job cào giá chọn được sản phẩm.
    /// </summary>
    public partial class AddCrawlTierAndNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF COL_LENGTH('Product', 'ActiveTrackersCount') IS NULL
    ALTER TABLE [Product] ADD [ActiveTrackersCount] int NOT NULL DEFAULT 0;
IF COL_LENGTH('Product', 'CrawlIntervalMinutes') IS NULL
    ALTER TABLE [Product] ADD [CrawlIntervalMinutes] int NOT NULL DEFAULT 1440;
IF COL_LENGTH('Product', 'CurrentTier') IS NULL
    ALTER TABLE [Product] ADD [CurrentTier] int NOT NULL DEFAULT 3;
IF COL_LENGTH('Product', 'MaxTargetPrice') IS NULL
    ALTER TABLE [Product] ADD [MaxTargetPrice] decimal(18,2) NULL;
IF COL_LENGTH('Product', 'NextCrawlAt') IS NULL
    ALTER TABLE [Product] ADD [NextCrawlAt] datetimeoffset NULL;

IF COL_LENGTH('Item', 'IsTargetNotified') IS NULL
    ALTER TABLE [Item] ADD [IsTargetNotified] bit NOT NULL DEFAULT CAST(0 AS bit);
IF COL_LENGTH('Item', 'LastNotifiedAt') IS NULL
    ALTER TABLE [Item] ADD [LastNotifiedAt] datetimeoffset NULL;
IF COL_LENGTH('Item', 'LastNotifiedPrice') IS NULL
    ALTER TABLE [Item] ADD [LastNotifiedPrice] decimal(18,2) NULL;

IF OBJECT_ID(N'[Notification]', N'U') IS NULL
    CREATE TABLE [Notification] (
        [NotificationId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [ProductId] uniqueidentifier NULL,
        [Title] nvarchar(255) NOT NULL,
        [Content] nvarchar(max) NOT NULL,
        [IsRead] bit NOT NULL,
        [CreatedAt] datetimeoffset NOT NULL,
        CONSTRAINT [PK_Notification] PRIMARY KEY ([NotificationId])
    );");

            // Index: bản thêm tay đặt tên theo bảng 'Products' (sai) → đổi về tên EF sinh để migration sau xoá/sửa được
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Products_NextCrawlAt_ActiveTrackersCount' AND object_id = OBJECT_ID(N'[Product]'))
    EXEC sp_rename N'[Product].[IX_Products_NextCrawlAt_ActiveTrackersCount]', N'IX_Product_NextCrawlAt_ActiveTrackersCount', N'INDEX';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Product_NextCrawlAt_ActiveTrackersCount' AND object_id = OBJECT_ID(N'[Product]'))
    CREATE INDEX [IX_Product_NextCrawlAt_ActiveTrackersCount] ON [Product] ([NextCrawlAt], [ActiveTrackersCount]);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Notification_UserId_CreatedAt' AND object_id = OBJECT_ID(N'[Notification]'))
    CREATE INDEX [IX_Notification_UserId_CreatedAt] ON [Notification] ([UserId], [CreatedAt]);");

            // Tính lại dữ liệu (EXEC: cột vừa thêm ở trên, phải biên dịch câu lệnh sau khi cột đã tồn tại).
            // 1) Số người theo dõi + giá mục tiêu cao nhất lấy từ bảng Item (dữ liệu thêm tay đang lệch hoặc = 0)
            // 2) Đang có người theo dõi mà chưa có lịch cào → cào ngay ở lượt job kế tiếp
            // 3) Không ai theo dõi → Tier 4 "ngủ đông" (giống UntrackProductAsync)
            // 4) Lượt theo dõi mất giá ban đầu → giá đầu tiên (> 0) trong lịch sử giá của sản phẩm (gần đúng: Item không lưu ngày bắt đầu theo dõi)
            migrationBuilder.Sql(@"
EXEC(N'
UPDATE p SET
    ActiveTrackersCount = (SELECT COUNT(*) FROM [Item] i WHERE i.ProductId = p.ProductId),
    MaxTargetPrice      = (SELECT MAX(i.TargetPrice) FROM [Item] i WHERE i.ProductId = p.ProductId)
FROM [Product] p;

UPDATE [Product] SET NextCrawlAt = SYSDATETIMEOFFSET()
WHERE ActiveTrackersCount > 0 AND NextCrawlAt IS NULL;

UPDATE [Product] SET CurrentTier = 4, CrawlIntervalMinutes = 43200, NextCrawlAt = DATEADD(DAY, 30, SYSDATETIMEOFFSET())
WHERE ActiveTrackersCount = 0 AND CurrentTier <> 4;

UPDATE i SET InitialPrice = (SELECT TOP 1 h.Price FROM [PriceHistory] h
                             WHERE h.ProductId = i.ProductId AND h.Price > 0
                             ORDER BY h.CheckedAt)
FROM [Item] i
WHERE i.InitialPrice IS NULL;
');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notification");

            migrationBuilder.DropIndex(
                name: "IX_Product_NextCrawlAt_ActiveTrackersCount",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "ActiveTrackersCount",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "CrawlIntervalMinutes",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "CurrentTier",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "MaxTargetPrice",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "NextCrawlAt",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "IsTargetNotified",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "LastNotifiedAt",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "LastNotifiedPrice",
                table: "Item");
        }
    }
}
