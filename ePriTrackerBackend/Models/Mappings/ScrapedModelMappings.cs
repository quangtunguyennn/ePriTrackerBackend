using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Models.Mappings
{
    /// <summary>
    /// Chuyển dữ liệu cào được (không phụ thuộc sàn) sang entity/DTO của API.
    /// Giá trị mặc định giữ nguyên như code cũ để response không đổi.
    /// </summary>
    public static class ScrapedModelMappings
    {
        public static Event ToEventEntity(this ScrapedEvent scraped) => new Event
        {
            TikiEventId = scraped.ExternalId,
            Title = scraped.Title,
            EventLink = scraped.Url,
            ImageUrl = scraped.ImageUrl,
            CreatedAt = DateTime.UtcNow
        };

        public static LiveEventProductDTO ToLiveEventProductDto(this ScrapedProduct scraped)
        {
            decimal price = scraped.Price ?? 0;

            return new LiveEventProductDTO
            {
                ProductId = scraped.ExternalId,
                ProductName = scraped.Name ?? "Sản phẩm ẩn",
                Desc = scraped.Description ?? "", // vd flash sale CellphoneS: "Đã bán 1/10 suất"
                InitialPrice = scraped.OriginalPrice ?? price, // Không có giá gốc → coi như không giảm
                LatestPrice = price,
                ImageURL = scraped.ImageUrl ?? "",
                ProductLink = scraped.Url ?? "",
                LastUpdatedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))
            };
        }
    }
}
