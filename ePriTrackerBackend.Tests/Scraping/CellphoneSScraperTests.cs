using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.CellphoneS;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class CellphoneSScraperTests
    {
        private sealed class FakeFetcher : IHybridFetcher
        {
            private readonly string _json;
            public FakeFetcher(string json) => _json = json;
            public string? LastPath { get; private set; }

            public Task<JsonElement> FetchJsonAsync(string path, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                LastPath = path;
                return Task.FromResult(JsonDocument.Parse(_json).RootElement.Clone());
            }
        }

        private static string ProductJson(string price, string availability = "InStock", string image = "\"https://cdn/robot.png\"")
            => "{\"@type\":\"Product\",\"name\":\"Robot hút bụi lau nhà Roborock Saros 20 Flow\",\"sku\":\"RB-123\",\"description\":\"Mô tả\"," +
               $"\"image\":{image},\"offers\":{{\"@type\":\"Offer\",\"price\":{price},\"priceCurrency\":\"VND\",\"availability\":\"https://schema.org/{availability}\"}}}}";

        [Fact]
        public void IsNotSearchable_BecauseRobotsTxtForbidsSearch()
        {
            Assert.False(typeof(ISearchablePlatformScraper).IsAssignableFrom(typeof(CellphoneSScraper)));
        }

        [Fact]
        public async Task GetProduct_FetchesNormalizedUrl_AndMapsFields()
        {
            var fetcher = new FakeFetcher(ProductJson("\"24990000\""));
            var scraper = new CellphoneSScraper(fetcher);

            var product = await scraper.GetProductAsync("https://cellphones.com.vn/Robot-Hut-Bui-Lau-Nha-Roborock-Saros-20-Flow.html?utm=x");

            Assert.Equal("https://cellphones.com.vn/robot-hut-bui-lau-nha-roborock-saros-20-flow.html", fetcher.LastPath);
            Assert.Equal("robot-hut-bui-lau-nha-roborock-saros-20-flow", product.ExternalId); // từ link, không phải sku
            Assert.Equal("Robot hút bụi lau nhà Roborock Saros 20 Flow", product.Name);
            Assert.Equal(24990000m, product.Price);
            Assert.Equal("https://cdn/robot.png", product.ImageUrl);
            Assert.Equal("Mô tả", product.Description);
            Assert.Equal(fetcher.LastPath, product.Url);
            Assert.True(product.IsAvailable);
            Assert.True(product.IsInStock);
        }

        [Fact]
        public async Task Description_PrefersFullHtml_FallsBackToJsonLdDescription()
        {
            string json = ProductJson("\"1\"");
            string withHtml = json[..^1] + $",\"{CellphoneSScraper.DescriptionHtmlField}\":\"<p>Bài viết đầy đủ</p>\"}}";

            var full = await new CellphoneSScraper(new FakeFetcher(withHtml)).GetProductAsync("https://cellphones.com.vn/a.html");
            var fallback = await new CellphoneSScraper(new FakeFetcher(ProductJson("\"1\""))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal("<p>Bài viết đầy đủ</p>", full.Description);
            Assert.Equal("Mô tả", fallback.Description); // máy cũ / ngừng bán: trang không có khối mô tả
        }

        [Fact]
        public async Task OutOfStockWithPrice_KeepsPrice_ButNotInStock()
        {
            // Như mẫu khảo sát "iphone-15-pro-128gb-...-cu-xuoc-can": OutOfStock nhưng vẫn có giá 16.390.000
            var product = await new CellphoneSScraper(new FakeFetcher(ProductJson("\"16390000\"", "OutOfStock"))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal(16390000m, product.Price);
            Assert.False(product.IsAvailable);
            Assert.False(product.IsInStock);
        }

        [Theory]
        [InlineData("\"0\"")]     // sản phẩm ngừng bán: giá 0 (5/15 mẫu khảo sát)
        [InlineData("0")]
        [InlineData("\"\"")]
        [InlineData("\"liên hệ\"")]
        public async Task ZeroOrMissingPrice_IsNull(string price)
        {
            var product = await new CellphoneSScraper(new FakeFetcher(ProductJson(price, "OutOfStock"))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Null(product.Price);
        }

        [Fact]
        public async Task NumericPrice_IsRead()
        {
            var product = await new CellphoneSScraper(new FakeFetcher(ProductJson("4890000"))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal(4890000m, product.Price);
        }

        [Theory]
        [InlineData("[\"https://cdn/1.png\",\"https://cdn/2.png\"]", "https://cdn/1.png")]
        [InlineData("{\"@type\":\"ImageObject\",\"url\":\"https://cdn/obj.png\"}", "https://cdn/obj.png")]
        public async Task ImageVariants_AreSupported(string image, string expected)
        {
            var product = await new CellphoneSScraper(new FakeFetcher(ProductJson("1", image: image))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal(expected, product.ImageUrl);
        }

        [Fact]
        public async Task LinkWithoutProductSlug_Throws()
        {
            var scraper = new CellphoneSScraper(new FakeFetcher(ProductJson("1")));

            await Assert.ThrowsAsync<ArgumentException>(() => scraper.GetProductAsync("https://cellphones.com.vn/laptop/asus.html"));
        }

        [Theory]
        [InlineData("https://cellphones.com.vn/iphone-17-256gb.html?x=1", true, "https://cellphones.com.vn/iphone-17-256gb.html")]
        [InlineData("https://cellphones.com.vn/laptop/asus.html", false, null)]
        public void TryNormalizeProductUrl(string url, bool ok, string? expected)
        {
            var scraper = new CellphoneSScraper(new FakeFetcher("{}"));

            Assert.Equal(ok, scraper.TryNormalizeProductUrl(url, out var normalized));
            Assert.Equal(expected, normalized);
            Assert.True(scraper.CanHandle(url));
            Assert.Equal("CellphoneS", scraper.Platform);
        }
    }
}
