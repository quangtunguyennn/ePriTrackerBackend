using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ePriTrackerBackend.Migrations
{
    /// <inheritdoc />
    public partial class AddPreviewImagesToEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviewImageUrls",
                table: "Event",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviewImageUrls",
                table: "Event");
        }
    }
}
