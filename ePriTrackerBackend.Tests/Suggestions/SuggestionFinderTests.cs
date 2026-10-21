using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Suggestions;

namespace ePriTrackerBackend.Tests.Suggestions
{
    public class SuggestionFinderTests
    {
        private sealed class FakeScraper : IPlatformScraper
        {
            private readonly IReadOnlyList<ScrapedProduct> _results;
            public FakeScraper(params ScrapedProduct[] results) => _results = results;

            public string? LastKeyword { get; private set; }
            public int LastLimit { get; private set; }
            public FetchOptions? LastOptions { get; private set; }

            public string Platform => "Fake";
            public bool CanHandle(string productUrl) => productUrl.StartsWith("https://fake/");
            public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl) { normalizedUrl = productUrl; return true; }
            public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                LastKeyword = keyword;
                LastLimit = limit;
                LastOptions = options;
                return Task.FromResult(_results);
            }
        }

        private sealed class SingleResolver : IPlatformScraperResolver
        {
            private readonly IPlatformScraper _scraper;
            public SingleResolver(IPlatformScraper scraper) => _scraper = scraper;
            public bool TryResolve(string productUrl, out IPlatformScraper scraper)
            {
                scraper = _scraper.CanHandle(productUrl) ? _scraper : null!;
                return scraper != null;
            }
        }

        private static Product Original(string link = "https://fake/p1", decimal initialPrice = 1000) => new()
        {
            ProductId = Guid.NewGuid(),
            ProductName = "Tai nghe Bluetooth Sony WH-1000XM5 (Hàng chính hãng)",
            ProductLink = link,
            ImageURL = "",
            InitialPrice = initialPrice
        };

        [Fact]
        public async Task SearchesSamePlatform_FiltersByNameAndPrice_AndMapsEntity()
        {
            var scraper = new FakeScraper(
                new ScrapedProduct { Name = "Tai nghe Sony WH-1000XM4", Price = 800, ImageUrl = "x.jpg", Url = "https://fake/x" }, // giữ
                new ScrapedProduct { Name = "Tai nghe Sony giá rẻ", Price = 100, Url = "https://fake/y" },                     // < 40% → loại
                new ScrapedProduct { Name = "Tai nghe Sony bản cao cấp", Price = 1500, Url = "https://fake/z" },               // đắt hơn → loại
                new ScrapedProduct { Name = "Loa Bluetooth Sony", Price = 500, Url = "https://fake/w" });                     // khác loại → loại
            var options = new FetchOptions { Timeout = TimeSpan.FromSeconds(5) };
            var original = Original();

            var suggestions = await new SuggestionFinder(new SingleResolver(scraper), new SuggestionMatcher()).FindAsync(original, options);

            Assert.Equal("tai nghe bluetooth sony wh", scraper.LastKeyword);
            Assert.Equal(10, scraper.LastLimit);
            Assert.Same(options, scraper.LastOptions);

            var s = Assert.Single(suggestions);
            Assert.Equal(original.ProductId, s.ProductId);
            Assert.NotEqual(Guid.Empty, s.SuggestionProductId);
            Assert.Equal("Tai nghe Sony WH-1000XM4", s.ProductName);
            Assert.Equal(800m, s.Price);
            Assert.Equal("x.jpg", s.ImageURL);
            Assert.Equal("https://fake/x", s.ProductLink);
            Assert.Equal(TimeSpan.FromHours(7), s.LastUpdatedAt!.Value.Offset);
        }

        [Fact]
        public async Task NullFieldsFromScraper_GetEmptyDefaults()
        {
            // Giá gốc = 0 → không lọc theo giá; ứng viên thiếu ảnh/link vẫn được giữ với chuỗi rỗng (như code cũ)
            var scraper = new FakeScraper(new ScrapedProduct { Name = "Tai nghe Sony", Price = null });

            var s = Assert.Single(await new SuggestionFinder(new SingleResolver(scraper), new SuggestionMatcher()).FindAsync(Original(initialPrice: 0)));

            Assert.Equal(0m, s.Price);
            Assert.Equal("", s.ImageURL);
            Assert.Equal("", s.ProductLink);
        }

        [Fact]
        public async Task UnsupportedPlatform_ReturnsEmpty_WithoutSearching()
        {
            var scraper = new FakeScraper(new ScrapedProduct { Name = "Tai nghe Sony", Price = 800 });

            var result = await new SuggestionFinder(new SingleResolver(scraper), new SuggestionMatcher()).FindAsync(Original(link: "https://other/p1"));

            Assert.Empty(result);
            Assert.Null(scraper.LastKeyword);
        }

        [Fact]
        public async Task EmptyKeyword_ReturnsEmpty_WithoutSearching()
        {
            var scraper = new FakeScraper();
            var product = Original();
            product.ProductName = "Chính hãng - Freeship"; // chỉ toàn từ quảng cáo → từ khoá rỗng

            Assert.Empty(await new SuggestionFinder(new SingleResolver(scraper), new SuggestionMatcher()).FindAsync(product));
            Assert.Null(scraper.LastKeyword);
        }
    }
}
