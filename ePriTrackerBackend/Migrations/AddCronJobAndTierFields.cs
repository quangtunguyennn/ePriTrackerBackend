using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    public partial class AddCronJobAndTierFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Thêm các trường cho bảng Products
            migrationBuilder.AddColumn<int>(
                name: "ActiveTrackersCount",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CrawlIntervalMinutes",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 1440);

            migrationBuilder.AddColumn<int>(
                name: "CurrentTier",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxTargetPrice",
                table: "Products",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCrawlAt",
                table: "Products",
                type: "datetimeoffset",
                nullable: true);

            // 2. Thêm Index cho bảng Products để Cronjob query nhanh
            migrationBuilder.CreateIndex(
                name: "IX_Products_NextCrawlAt_ActiveTrackersCount",
                table: "Products",
                columns: new[] { "NextCrawlAt", "ActiveTrackersCount" });

            // 3. Thêm các trường chống Spam cho bảng Items (nếu bạn dùng)
            migrationBuilder.AddColumn<bool>(
                name: "IsTargetNotified",
                table: "Items",
                type: "bit", // Hoặc boolean tùy loại SQL Database bạn dùng (PostgreSQL là boolean, SQL Server là bit)
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastNotifiedAt",
                table: "Items",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LastNotifiedPrice",
                table: "Items",
                type: "decimal(18,2)",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 1. Xóa Index bảng Products
            migrationBuilder.DropIndex(
                name: "IX_Products_NextCrawlAt_ActiveTrackersCount",
                table: "Products");

            // 2. Xóa các cột bảng Products
            migrationBuilder.DropColumn(
                name: "ActiveTrackersCount",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CrawlIntervalMinutes",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "CurrentTier",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MaxTargetPrice",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "NextCrawlAt",
                table: "Products");

            // 3. Xóa các cột bảng Items
            migrationBuilder.DropColumn(
                name: "IsTargetNotified",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "LastNotifiedAt",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "LastNotifiedPrice",
                table: "Items");
        }
    }
}