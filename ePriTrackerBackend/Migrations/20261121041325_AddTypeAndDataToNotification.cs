using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeAndDataToNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Data",
                table: "Notification",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Notification",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Thông báo cũ (tiếng Việt, chưa có Type): suy loại theo tiêu đề cố định của từng loại
            migrationBuilder.Sql("UPDATE [Notification] SET [Type] = N'TargetReached' WHERE [Type] IS NULL AND [Title] LIKE N'%Giá sản phẩm đã giảm%';");
            migrationBuilder.Sql("UPDATE [Notification] SET [Type] = N'Delisted' WHERE [Type] IS NULL AND [Title] LIKE N'%không còn trên sàn%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Data",
                table: "Notification");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Notification");
        }
    }
}
