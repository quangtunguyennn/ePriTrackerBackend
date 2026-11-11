using System.Diagnostics;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using ePriTrackerBackend.Scraping.Gearvn;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace ePriTrackerBackend.Tests.Scraping
{
    /// <summary>
    /// Chạy THẬT trên gearvn.com (mạng) – kiểm tra JSON-LD trang sản phẩm và HTML trang tìm kiếm còn khớp bộ đọc.
    /// Mặc định bỏ qua; bật bằng biến môi trường EPRI_LIVE_TESTS=1. In thời gian từng request để đo hiệu suất.
    /// </summary>
    public class GearvnLiveTests
    {
        private static bool Live => Environment.GetEnvironmentVariable("EPRI_LIVE_TESTS") == "1";

        private readonly ITestOutputHelper _output;
        public GearvnLiveTests(ITestOutputHelper output) => _output = output;

        private sealed class RealFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml");
                client.DefaultRequestHeaders.Add("Accept-Language", "vi-VN,vi;q=0.9");
                return client;
            }
        }

        // Như HybridFetcher nhưng không cần telemetry
        private sealed class DirectFetcher : IHybridFetcher
        {
            private readonly IFetchStrategy _strategy;
            public DirectFetcher(IFetchStrategy strategy) => _strategy = strategy;
            public Task<JsonElement> FetchJsonAsync(string path, FetchOptions? options = null, CancellationToken cancellationToken = default)
                => _strategy.FetchJsonAsync(path, options ?? FetchOptions.Default, cancellationToken);
        }

        private static GearvnScraper Scraper()
        {
            var factory = new RealFactory();
            return new GearvnScraper(new DirectFetcher(new HtmlJsonLdFetchStrategy(factory, GearvnScraper.PlatformName)), factory, NullLogger<GearvnScraper>.Instance);
        }

        [Theory]
        [InlineData("https://gearvn.com/products/logitech-g502-hero")]
        [InlineData("https://gearvn.com/products/ban-phim-co-asus-rog-falchion-ace-75-he-v2x-red")]
        public async Task ProductPage_HasPriceNameImage(string url)
        {
            if (!Live) return;

            var sw = Stopwatch.StartNew();
            var product = await Scraper().GetProductAsync(url);
            _output.WriteLine($"{sw.ElapsedMilliseconds} ms | {product.Name} | {product.Price:N0} | còn hàng: {product.IsInStock} | {product.ImageUrl} | mô tả {product.Description?.Length ?? 0} ký tự");

            Assert.False(string.IsNullOrWhiteSpace(product.Name));
            Assert.StartsWith("https://", product.ImageUrl);
            if (product.IsInStock) Assert.True(product.Price > 0);
        }

        [Fact]
        public async Task PromotionEvents_RealPage()
        {
            if (!Live) return;

            var factory = new RealFactory();
            var source = new GearvnEventSource(factory, (IPlatformScraper)Scraper(), NullLogger<GearvnEventSource>.Instance);
            var sw = Stopwatch.StartNew();
            var events = await source.GetLiveEventsAsync();
            _output.WriteLine($"{events.Count} chương trình đang chạy, đọc trong {sw.ElapsedMilliseconds} ms");
            foreach (var e in events)
            {
                _output.WriteLine($"{e.Title} | hết hạn {e.EndsAt:dd/MM/yyyy HH:mm} | {e.Products.Count} sản phẩm | {e.Url}");
                foreach (var p in e.Products.Take(2)) _output.WriteLine($"     {p.Price,12:N0} | {p.Name} | {p.Description}");
            }

            Assert.NotEmpty(events);
            Assert.All(events, e => Assert.True(e.EndsAt > DateTimeOffset.UtcNow));
            Assert.Contains(events, e => e.Products.Count >= 4); // đủ điều kiện đăng (AutoEventSyncService.MinProducts)
        }

        [Theory]
        [InlineData("chuột logitech g502")]
        [InlineData("bàn phím cơ asus rog")]
        [InlineData("laptop asus tuf gaming")]
        public async Task Search_ReturnsCleanCards(string keyword)
        {
            if (!Live) return;

            var sw = Stopwatch.StartNew();
            var results = await Scraper().SearchAsync(keyword, 10);
            _output.WriteLine($"'{keyword}': {results.Count} kết quả trong {sw.ElapsedMilliseconds} ms");
            foreach (var p in results) _output.WriteLine($"   {p.Price,12:N0} | {p.Name} | {p.Url}");

            Assert.NotEmpty(results);
            Assert.All(results, p =>
            {
                Assert.True(p.Price >= 1000);
                Assert.DoesNotContain("Giảm", p.Name);             // tên sạch, không lẫn nhãn khuyến mãi
                Assert.StartsWith("https://gearvn.com/products/", p.Url);
            });
        }
    }
}
