using ePriTrackerBackend.Models.Mappings;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Tests.Mappings
{
    /// <summary>
    /// Giá trị mặc định phải giống code cũ trong ProductRepository.GetLiveProductsFromEventAsync / EventRepository
    /// vì FE (EventDetail.jsx) đọc trực tiếp các trường này.
    /// </summary>
    public class ScrapedModelMappingsTests
    {
        [Fact]
        public void LiveEventProduct_FullData()
        {
            var dto = new ScrapedProduct
            {
                ExternalId = "5", Name = "Sữa", Price = 90000, OriginalPrice = 120000, ImageUrl = "s.jpg", Url = "https://tiki.vn/sua-p5.html"
            }.ToLiveEventProductDto();

            Assert.Equal("5", dto.ProductId);
            Assert.Equal("Sữa", dto.ProductName);
            Assert.Equal(120000m, dto.InitialPrice);
            Assert.Equal(90000m, dto.LatestPrice);
            Assert.Equal("s.jpg", dto.ImageURL);
            Assert.Equal("https://tiki.vn/sua-p5.html", dto.ProductLink);
            Assert.Equal("", dto.Desc);
            Assert.Equal(TimeSpan.FromHours(7), dto.LastUpdatedAt.Offset);
        }

        [Fact]
        public void LiveEventProduct_MissingData_UsesLegacyDefaults()
        {
            var dto = new ScrapedProduct { ExternalId = "6", Price = 50000 }.ToLiveEventProductDto();

            Assert.Equal("Sản phẩm ẩn", dto.ProductName);
            Assert.Equal(50000m, dto.InitialPrice); // không có giá gốc → bằng giá bán
            Assert.Equal("", dto.ImageURL);
            Assert.Equal("", dto.ProductLink);

            var noPrice = new ScrapedProduct { ExternalId = "7" }.ToLiveEventProductDto();
            Assert.Equal(0m, noPrice.LatestPrice);
            Assert.Equal(0m, noPrice.InitialPrice);
        }

        [Fact]
        public void Event_MapsFields()
        {
            var before = DateTime.UtcNow;
            var evt = new ScrapedEvent { ExternalId = 11, Title = "Fonterra Chinh Hang", Url = "https://tiki.vn/k", ImageUrl = "b.jpg" }.ToEventEntity();

            Assert.Equal(11, evt.TikiEventId);
            Assert.Equal("Fonterra Chinh Hang", evt.Title);
            Assert.Equal("https://tiki.vn/k", evt.EventLink);
            Assert.Equal("b.jpg", evt.ImageUrl);
            Assert.False(evt.IsPublished);
            Assert.InRange(evt.CreatedAt, before, DateTime.UtcNow);
        }
    }
}
