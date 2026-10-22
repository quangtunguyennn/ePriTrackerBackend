using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.CellphoneS;
using ePriTrackerBackend.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

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

        private static CellphoneSScraper Create(IHybridFetcher fetcher, FakeCellphoneSApi? api = null)
            => new(fetcher, api ?? new FakeCellphoneSApi(), NullLogger<CellphoneSScraper>.Instance);

        private static CellphoneSScraper Create(FakeCellphoneSApi api) => Create(new FakeFetcher("{}"), api);

        [Fact]
        public void SupportsSearchAndBatchPrices()
        {
            Assert.True(typeof(ISearchablePlatformScraper).IsAssignableFrom(typeof(CellphoneSScraper)));
            Assert.True(typeof(IBatchPriceScraper).IsAssignableFrom(typeof(CellphoneSScraper)));
        }

        // ---------- Tìm kiếm (gợi ý) ----------

        [Fact]
        public async Task Search_MapsFields_SkipsUsedProducts()
        {
            var api = new FakeCellphoneSApi();
            api.SearchResults["laptop asus tuf gaming a16"] = new()
            {
                FakeCellphoneSApi.Product(131954, "laptop-asus-tuf-gaming-a16-fa607nuq-rl007w", 35990000, 44490000, name: "Laptop ASUS TUF Gaming A16 FA607NUQ-RL007W"),
                FakeCellphoneSApi.Product(2, "laptop-asus-tuf-gaming-a16-cu", 30000000, 40000000, condition: "Cũ"),
                FakeCellphoneSApi.Product(3, "laptop-asus-tuf-gaming-a16-fa608umi-tu162w", 0, 51990000)
            };

            var results = await Create(api).SearchAsync("laptop asus tuf gaming a16", 10);

            Assert.Equal(2, results.Count); // hàng cũ bị loại
            var tuf = results[0];
            Assert.Equal("Laptop ASUS TUF Gaming A16 FA607NUQ-RL007W", tuf.Name);
            Assert.Equal(35990000m, tuf.Price);
            Assert.Equal(44490000m, tuf.OriginalPrice);
            Assert.Equal("https://cellphones.com.vn/laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.html", tuf.Url);
            Assert.Equal("https://cdn2.cellphones.com.vn/358x/media/catalog/product/x/laptop-asus-tuf-gaming-a16-fa607nuq-rl007w.png", tuf.ImageUrl);
            Assert.Null(results[1].Price); // giá 0 = không bán
            Assert.False(results[1].IsInStock);
        }

        [Fact]
        public async Task Search_NoResults_DropsWordsFromEnd_UpToThreeCalls()
        {
            var api = new FakeCellphoneSApi();
            api.SearchResults["dien thoai samsung galaxy"] = new() { FakeCellphoneSApi.Product(1, "samsung-galaxy-a08", 4790000) };

            var results = await Create(api).SearchAsync("dien thoai samsung galaxy a08 5g", 10);

            Assert.Equal(new[] { "dien thoai samsung galaxy a08 5g", "dien thoai samsung galaxy a08", "dien thoai samsung galaxy" }, api.SearchedTerms);
            Assert.Single(results);
        }

        [Fact]
        public async Task Search_GivesUpAfterThreeCalls_OrWhenKeywordTooShort()
        {
            var api = new FakeCellphoneSApi();

            Assert.Empty(await Create(api).SearchAsync("a b c d e f", 10));
            Assert.Equal(3, api.SearchedTerms.Count);

            api.SearchedTerms.Clear();
            Assert.Empty(await Create(api).SearchAsync("iphone", 10));
            Assert.Empty(api.SearchedTerms); // 1 từ: quá chung chung, không tìm
        }

        // ---------- Giá theo lô (job giá) ----------

        [Fact]
        public async Task BatchPrices_UsesStoredIds_LooksUpMissingOnce_SkipsUnknown()
        {
            var api = new FakeCellphoneSApi();
            api.Catalog[1] = FakeCellphoneSApi.Product(1, "san-pham-a", 1000, 1200);
            api.Catalog[2] = FakeCellphoneSApi.Product(2, "san-pham-b", 0, 2000);
            api.IdsByUrl["https://cellphones.com.vn/san-pham-b.html"] = 2;

            var prices = await Create(api).GetCurrentPricesAsync(new[]
            {
                new BatchPriceRequest { ProductUrl = "https://cellphones.com.vn/san-pham-a.html", PlatformProductId = "1" },
                new BatchPriceRequest { ProductUrl = "https://cellphones.com.vn/san-pham-b.html" },
                new BatchPriceRequest { ProductUrl = "https://cellphones.com.vn/khong-tra-duoc.html" }
            });

            Assert.Equal(new[] { "https://cellphones.com.vn/san-pham-b.html", "https://cellphones.com.vn/khong-tra-duoc.html" }, api.UrlLookups);
            Assert.Single(api.CatalogRequests); // 1 request cho cả lô
            Assert.Equal(2, prices.Count);
            Assert.Equal(1000m, prices["https://cellphones.com.vn/san-pham-a.html"].Product.Price);
            var b = prices["https://cellphones.com.vn/san-pham-b.html"];
            Assert.Equal("2", b.PlatformProductId); // nơi gọi lưu lại
            Assert.Null(b.Product.Price);           // giá 0 = hết hàng
            Assert.False(prices.ContainsKey("https://cellphones.com.vn/khong-tra-duoc.html")); // → nơi gọi đọc JSON-LD
        }

        [Fact]
        public async Task BatchPrices_SplitsIntoChunksOf50_OneBadChunkDoesNotLoseOthers()
        {
            var api = new FakeCellphoneSApi();
            var requests = Enumerable.Range(1, 120).Select(i =>
            {
                api.Catalog[i] = FakeCellphoneSApi.Product(i, $"sp-{i}", 1000 + i);
                return new BatchPriceRequest { ProductUrl = $"https://cellphones.com.vn/sp-{i}.html", PlatformProductId = i.ToString() };
            }).ToList();
            api.FailingCatalogIds.Add(60); // lô 2 (51–100) lỗi

            var prices = await Create(api).GetCurrentPricesAsync(requests);

            Assert.Equal(3, api.CatalogRequests.Count);
            Assert.Equal(70, prices.Count);
            Assert.False(prices.ContainsKey("https://cellphones.com.vn/sp-60.html"));
        }

        [Fact]
        public async Task BatchPrices_AllChunksFail_Throws()
        {
            var api = new FakeCellphoneSApi();
            api.Catalog[1] = FakeCellphoneSApi.Product(1, "a", 1);
            api.FailingCatalogIds.Add(1);

            await Assert.ThrowsAsync<HttpRequestException>(() => Create(api).GetCurrentPricesAsync(new[]
            {
                new BatchPriceRequest { ProductUrl = "https://cellphones.com.vn/a.html", PlatformProductId = "1" }
            }));
        }

        [Fact]
        public async Task GetProduct_FetchesNormalizedUrl_AndMapsFields()
        {
            var fetcher = new FakeFetcher(ProductJson("\"24990000\""));
            var scraper = Create(fetcher);

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

            var full = await Create(new FakeFetcher(withHtml)).GetProductAsync("https://cellphones.com.vn/a.html");
            var fallback = await Create(new FakeFetcher(ProductJson("\"1\""))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal("<p>Bài viết đầy đủ</p>", full.Description);
            Assert.Equal("Mô tả", fallback.Description); // máy cũ / ngừng bán: trang không có khối mô tả
        }

        [Fact]
        public async Task OutOfStockWithPrice_KeepsPrice_ButNotInStock()
        {
            // Như mẫu khảo sát "iphone-15-pro-128gb-...-cu-xuoc-can": OutOfStock nhưng vẫn có giá 16.390.000
            var product = await Create(new FakeFetcher(ProductJson("\"16390000\"", "OutOfStock"))).GetProductAsync("https://cellphones.com.vn/a.html");

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
            var product = await Create(new FakeFetcher(ProductJson(price, "OutOfStock"))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Null(product.Price);
        }

        [Fact]
        public async Task NumericPrice_IsRead()
        {
            var product = await Create(new FakeFetcher(ProductJson("4890000"))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal(4890000m, product.Price);
        }

        [Theory]
        [InlineData("[\"https://cdn/1.png\",\"https://cdn/2.png\"]", "https://cdn/1.png")]
        [InlineData("{\"@type\":\"ImageObject\",\"url\":\"https://cdn/obj.png\"}", "https://cdn/obj.png")]
        public async Task ImageVariants_AreSupported(string image, string expected)
        {
            var product = await Create(new FakeFetcher(ProductJson("1", image: image))).GetProductAsync("https://cellphones.com.vn/a.html");

            Assert.Equal(expected, product.ImageUrl);
        }

        [Fact]
        public async Task LinkWithoutProductSlug_Throws()
        {
            var scraper = Create(new FakeFetcher(ProductJson("1")));

            await Assert.ThrowsAsync<ArgumentException>(() => scraper.GetProductAsync("https://cellphones.com.vn/laptop/asus.html"));
        }

        [Theory]
        [InlineData("https://cellphones.com.vn/iphone-17-256gb.html?x=1", true, "https://cellphones.com.vn/iphone-17-256gb.html")]
        [InlineData("https://cellphones.com.vn/laptop/asus.html", false, null)]
        public void TryNormalizeProductUrl(string url, bool ok, string? expected)
        {
            var scraper = Create(new FakeFetcher("{}"));

            Assert.Equal(ok, scraper.TryNormalizeProductUrl(url, out var normalized));
            Assert.Equal(expected, normalized);
            Assert.True(scraper.CanHandle(url));
            Assert.Equal("CellphoneS", scraper.Platform);
        }
    }
}
