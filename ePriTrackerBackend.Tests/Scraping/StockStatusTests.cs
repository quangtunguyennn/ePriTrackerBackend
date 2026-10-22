using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Scraping.Tiki;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>Quy tắc còn/hết hàng đã thống nhất (20/10/2026).</summary>
    public class StockStatusTests
    {
        [Theory]
        [InlineData(1000, true, true)]
        [InlineData(1000, null, true)]   // sàn không cho biết + có giá → còn hàng
        [InlineData(1000, false, false)] // sàn báo hết hàng dù có giá
        [InlineData(0, true, false)]     // không có giá → hết hàng
        [InlineData(null, null, false)]
        public void ScrapedProduct_IsInStock(int? price, bool? available, bool expected)
        {
            Assert.Equal(expected, new ScrapedProduct { Price = price, IsAvailable = available }.IsInStock);
        }

        [Theory]
        [InlineData("available", true)]
        [InlineData("AVAILABLE", true)]
        [InlineData("out_of_stock", false)]
        [InlineData("discontinue", false)]
        [InlineData("discontinued", false)]
        [InlineData("upcoming", false)] // sắp về hàng – đã thống nhất tính là hết hàng
        [InlineData("gia_tri_la", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void Tiki_InventoryStatus(string? status, bool? expected)
        {
            Assert.Equal(expected, TikiScraper.ParseInventoryStatus(status!));
        }

        [Theory]
        [InlineData("https://schema.org/InStock", true)]
        [InlineData("http://schema.org/InStock", true)]
        [InlineData("InStock", true)]
        [InlineData("https://schema.org/LimitedAvailability", true)]
        [InlineData("https://schema.org/OutOfStock", false)]
        [InlineData("https://schema.org/SoldOut", false)]
        [InlineData("https://schema.org/Discontinued", false)]
        [InlineData("https://schema.org/PreOrder", false)] // đặt trước = chưa có hàng (giống "upcoming" của Tiki)
        [InlineData("https://schema.org/GiaTriLa", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void CellphoneS_Availability(string? availability, bool? expected)
        {
            Assert.Equal(expected, CellphoneSScraper.ParseAvailability(availability!));
        }
    }
}
