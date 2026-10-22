using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// HTML mẫu tự viết, mô phỏng đúng các dạng gặp khi khảo sát CellphoneS (20/10/2026):
    /// trang sản phẩm có Offer; trang danh mục có AggregateOffer.
    /// </summary>
    public class JsonLdProductReaderTests
    {
        private static string Page(params string[] jsonLdBlocks)
            => "<html><head>" + string.Concat(jsonLdBlocks.Select(b => $"<script type=\"application/ld+json\">{b}</script>")) + "</head><body></body></html>";

        private const string ProductBlock =
            "{\"@context\":\"https://schema.org\",\"@type\":\"Product\",\"name\":\"iPhone 17 256GB | Chính hãng\",\"sku\":\"iphone-17-256gb\"," +
            "\"image\":\"https://cdn/iphone.png\",\"offers\":{\"@type\":\"Offer\",\"price\":\"26990000\",\"priceCurrency\":\"VND\",\"availability\":\"https://schema.org/InStock\"}}";

        private const string CategoryBlock =
            "{\"@type\":\"Product\",\"name\":\"Điện thoại\",\"offers\":{\"@type\":\"AggregateOffer\",\"lowPrice\":\"440000\",\"highPrice\":\"103990000\",\"priceCurrency\":\"VND\"}}";

        private const string BreadcrumbBlock = "{\"@type\":\"BreadcrumbList\",\"itemListElement\":[]}";

        [Fact]
        public void ProductPage_ReturnsProductBlock()
        {
            var product = JsonLdProductReader.FindProduct(Page(BreadcrumbBlock, ProductBlock));

            Assert.NotNull(product);
            Assert.Equal("iPhone 17 256GB | Chính hãng", product!.Value.GetProperty("name").GetString());
            Assert.Equal("26990000", JsonLdProductReader.FirstOffer(product.Value)!.Value.GetProperty("price").GetString());
        }

        [Fact]
        public void CategoryPage_WithAggregateOffer_ReturnsNull()
        {
            Assert.Null(JsonLdProductReader.FindProduct(Page(BreadcrumbBlock, CategoryBlock)));
        }

        [Fact]
        public void OffersAsArray_UsesFirstOffer()
        {
            const string block = "{\"@type\":\"Product\",\"name\":\"A\",\"offers\":[{\"@type\":\"Offer\",\"price\":100},{\"@type\":\"Offer\",\"price\":200}]}";

            var product = JsonLdProductReader.FindProduct(Page(block));

            Assert.Equal(100, JsonLdProductReader.FirstOffer(product!.Value)!.Value.GetProperty("price").GetInt32());
        }

        [Fact]
        public void GraphAndTypeArray_AreSupported()
        {
            const string block = "{\"@context\":\"https://schema.org\",\"@graph\":[{\"@type\":\"WebPage\"},{\"@type\":[\"Product\",\"Thing\"],\"name\":\"B\",\"offers\":{\"@type\":\"Offer\",\"price\":1}}]}";

            Assert.Equal("B", JsonLdProductReader.FindProduct(Page(block))!.Value.GetProperty("name").GetString());
        }

        [Fact]
        public void BrokenBlockBeforeValidOne_IsSkipped()
        {
            Assert.NotNull(JsonLdProductReader.FindProduct(Page("{ không phải json", ProductBlock)));
        }

        [Theory]
        [InlineData("<html><body>Không có JSON-LD</body></html>")]
        [InlineData("")]
        [InlineData(null)]
        public void NoJsonLd_ReturnsNull(string? html)
        {
            Assert.Null(JsonLdProductReader.FindProduct(html!));
        }
    }
}
