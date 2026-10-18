using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Tiki;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class TikiScraperTests
    {
        private sealed class FakeFetcher : IHybridFetcher
        {
            private readonly Func<string, JsonElement> _respond;
            public FakeFetcher(Func<string, JsonElement> respond) => _respond = respond;
            public List<(string Path, FetchOptions? Options)> Calls { get; } = new();

            public Task<JsonElement> FetchJsonAsync(string path, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                Calls.Add((path, options));
                return Task.FromResult(_respond(path));
            }
        }

        private sealed class FakeBrowser : ITikiBrowserService
        {
            private readonly Func<JsonElement> _eventPage;
            public FakeBrowser(Func<JsonElement> eventPage) => _eventPage = eventPage;
            public Task<JsonElement> FetchTikiApiAsync(string apiPath) => throw new NotSupportedException();
            public Task<JsonElement> ScrapeEventPageAsync(string eventUrl) => Task.FromResult(_eventPage());
        }

        private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

        private static TikiScraper Create(FakeFetcher? fetcher = null, FakeBrowser? browser = null)
            => new TikiScraper(fetcher ?? new FakeFetcher(_ => Json("{}")), browser ?? new FakeBrowser(() => Json("{}")), NullLogger<TikiScraper>.Instance);

        [Fact]
        public void IdentifiesItselfAsTiki()
        {
            var scraper = Create();
            Assert.Equal("Tiki", scraper.Platform);
            Assert.True(scraper.CanHandle("https://tiki.vn/abc-p1.html"));
            Assert.False(scraper.CanHandle("https://shopee.vn/abc-p1.html"));
        }

        [Theory]
        [InlineData("https://tiki.vn/dien-thoai-iphone-15-p184036446.html?spid=1", true, "https://tiki.vn/product-p184036446.html")]
        [InlineData("https://tiki.vn/khuyen-mai", false, null)]
        public void TryNormalizeProductUrl(string link, bool ok, string? expected)
        {
            Assert.Equal(ok, Create().TryNormalizeProductUrl(link, out var normalized));
            Assert.Equal(expected, normalized);
        }

        [Fact]
        public async Task GetProductAsync_CallsDetailApiWithSpid_AndMapsFields()
        {
            var fetcher = new FakeFetcher(_ => Json("{\"name\":\"iPhone 15\",\"price\":20000000,\"original_price\":25000000,\"thumbnail_url\":\"img.jpg\",\"description\":\"mô tả\"}"));

            var product = await Create(fetcher).GetProductAsync("https://tiki.vn/iphone-p123.html?spid=456");

            Assert.Equal("/api/v2/products/123?spid=456", fetcher.Calls.Single().Path);
            Assert.Equal("123", product.ExternalId);
            Assert.Equal("iPhone 15", product.Name);
            Assert.Equal(20000000m, product.Price);
            Assert.Equal(25000000m, product.OriginalPrice);
            Assert.Equal("img.jpg", product.ImageUrl);
            Assert.Equal("mô tả", product.Description);
            Assert.Equal("https://tiki.vn/product-p123.html", product.Url);
        }

        [Fact]
        public async Task GetProductAsync_MissingFields_AreNull()
        {
            // Tầng gọi (AddProduct / PriceCrawler) tự áp giá trị mặc định như code cũ
            var product = await Create(new FakeFetcher(_ => Json("{\"price\":\"không phải số\"}"))).GetProductAsync("https://tiki.vn/x-p1.html");

            Assert.Null(product.Name);
            Assert.Null(product.Price);
            Assert.Null(product.ImageUrl);
        }

        [Fact]
        public async Task SearchAsync_BuildsPath_PassesOptions_AndParsesList()
        {
            var options = new FetchOptions { Timeout = TimeSpan.FromSeconds(5) };
            var fetcher = new FakeFetcher(_ => Json(
                "{\"data\":[" +
                "{\"id\":1,\"name\":\"A\",\"price\":100,\"thumbnail_url\":\"a.jpg\",\"url_path\":\"a-p1.html?spid=9\"}," +
                "{\"name\":\"không có id – bị bỏ\"}," +
                "{\"id\":2,\"name\":\"B\"}" +
                "]}"));

            var results = await Create(fetcher).SearchAsync("iphone 15", 10, options);

            Assert.Equal("/api/v2/products?limit=10&q=iphone%2015", fetcher.Calls.Single().Path);
            Assert.Same(options, fetcher.Calls.Single().Options);
            Assert.Equal(2, results.Count);
            Assert.Equal("1", results[0].ExternalId);
            Assert.Equal(100m, results[0].Price);
            Assert.Equal("https://tiki.vn/a-p1.html?spid=9", results[0].Url);
            Assert.Null(results[1].Price);
            Assert.Equal("", results[1].Url);
        }

        [Fact]
        public async Task SearchAsync_NoDataArray_ReturnsEmpty()
        {
            Assert.Empty(await Create(new FakeFetcher(_ => Json("{\"error\":\"x\"}"))).SearchAsync("a", 10));
        }

        [Fact]
        public async Task GetCurrentEventsAsync_ParsesBanners_AndFormatsTitle()
        {
            var fetcher = new FakeFetcher(_ => Json(
                "{\"data\":[{\"banners\":[" +
                "{\"id\":11,\"title\":\"https://tiki.vn/khuyen-mai/fonterra-chinh-hang\",\"image_url\":\"b.jpg\",\"url\":\"https://tiki.vn/khuyen-mai/fonterra-chinh-hang?itm=1\"}," +
                "{\"id\":0,\"title\":\"bị bỏ vì id = 0\",\"url\":\"https://x\"}," +
                "{\"id\":12,\"title\":\"bị bỏ vì không có url\"}" +
                "]}]}"));

            var events = await Create(fetcher).GetCurrentEventsAsync();

            Assert.Equal("https://tka.tiki.vn/widget/api/v1/banners-group?group=banner_carousel_2_8", fetcher.Calls.Single().Path);
            var evt = Assert.Single(events);
            Assert.Equal(11, evt.ExternalId);
            Assert.Equal("Fonterra Chinh Hang", evt.Title);
            Assert.Equal("https://tiki.vn/khuyen-mai/fonterra-chinh-hang?itm=1", evt.Url);
            Assert.Equal("b.jpg", evt.ImageUrl);
        }

        [Fact]
        public async Task GetCurrentEventsAsync_NoEvents_ThrowsWithRawJson()
        {
            // Hành vi cũ: Admin thấy JSON Tiki trả về ngay trên Swagger/UI để biết bị WAF chặn hay đổi cấu trúc
            var ex = await Assert.ThrowsAsync<Exception>(() => Create(new FakeFetcher(_ => Json("{\"data\":[]}"))).GetCurrentEventsAsync());

            Assert.Equal("Tiki trả về: {\"data\":[]}", ex.Message);
        }

        [Fact]
        public async Task GetEventProductsAsync_ParsesDomResult()
        {
            var browser = new FakeBrowser(() => Json(
                "{\"data\":[{\"id\":5,\"name\":\"Sữa\",\"price\":90000,\"original_price\":120000,\"thumbnail_url\":\"s.jpg\",\"url_path\":\"sua-p5.html\"}]}"));

            var product = Assert.Single(await Create(browser: browser).GetEventProductsAsync("https://tiki.vn/khuyen-mai/x"));

            Assert.Equal("5", product.ExternalId);
            Assert.Equal(90000m, product.Price);
            Assert.Equal(120000m, product.OriginalPrice);
            Assert.Equal("https://tiki.vn/sua-p5.html", product.Url);
        }

        [Fact]
        public async Task GetEventProductsAsync_BrowserError_ReturnsEmpty()
        {
            var browser = new FakeBrowser(() => throw new TimeoutException());

            Assert.Empty(await Create(browser: browser).GetEventProductsAsync("https://tiki.vn/khuyen-mai/x"));
        }
    }
}
