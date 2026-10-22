using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Services.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ePriTrackerBackend.Tests.Suggestions
{
    public class SuggestionFinderTests
    {
        /// <summary>Sàn giả có tìm kiếm: nhận link bắt đầu bằng <c>prefix</c>; chuẩn hoá link = bỏ query.</summary>
        private sealed class SearchableFake : ISearchablePlatformScraper
        {
            private readonly string _prefix;
            private readonly Func<IReadOnlyList<ScrapedProduct>> _search;

            public SearchableFake(string platform, string prefix, params ScrapedProduct[] results) : this(platform, prefix, () => results) { }

            public SearchableFake(string platform, string prefix, Func<IReadOnlyList<ScrapedProduct>> search)
            {
                Platform = platform;
                _prefix = prefix;
                _search = search;
            }

            public string? LastKeyword { get; private set; }
            public int LastLimit { get; private set; }
            public FetchOptions? LastOptions { get; private set; }

            public string Platform { get; }
            public bool CanHandle(string productUrl) => productUrl.StartsWith(_prefix);
            public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl) { normalizedUrl = productUrl.Split('?')[0]; return true; }
            public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions? options = null, CancellationToken cancellationToken = default)
            {
                LastKeyword = keyword;
                LastLimit = limit;
                LastOptions = options;
                return Task.FromResult(_search());
            }
        }

        /// <summary>Sàn giả KHÔNG có tìm kiếm (giống CellphoneS).</summary>
        private sealed class NonSearchableFake : IPlatformScraper
        {
            public string Platform => "NoSearch";
            public bool CanHandle(string productUrl) => productUrl.StartsWith("https://nosearch/");
            public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl) { normalizedUrl = productUrl; return true; }
            public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class Resolver : IPlatformScraperResolver
        {
            private readonly IReadOnlyList<IPlatformScraper> _scrapers;
            public Resolver(params IPlatformScraper[] scrapers) => _scrapers = scrapers;

            public bool TryResolve(string productUrl, out IPlatformScraper scraper)
            {
                scraper = _scrapers.FirstOrDefault(s => s.CanHandle(productUrl))!;
                return scraper != null;
            }

            public IReadOnlyList<ISearchablePlatformScraper> SearchablePlatforms => _scrapers.OfType<ISearchablePlatformScraper>().ToList();
        }

        private static SuggestionFinder Finder(params IPlatformScraper[] scrapers)
            => new(new Resolver(scrapers), new SuggestionMatcher(), NullLogger<SuggestionFinder>.Instance);

        // Giá tham chiếu của gợi ý = giá hiện tại (giá ban đầu nay lưu theo từng người ở bảng Item)
        private static Product Original(string link = "https://fake/p1", decimal latestPrice = 1000) => new()
        {
            ProductId = Guid.NewGuid(),
            ProductName = "Tai nghe Bluetooth Sony WH-1000XM5 (Hàng chính hãng)",
            ProductLink = link,
            ImageURL = "",
            LatestPrice = latestPrice
        };

        private static ScrapedProduct Candidate(string name, decimal? price, string url) => new() { Name = name, Price = price, Url = url };

        [Fact]
        public async Task FiltersByNameAndPrice_AndMapsEntity()
        {
            var scraper = new SearchableFake("Fake", "https://fake/",
                new ScrapedProduct { Name = "Tai nghe Sony WH-1000XM4", Price = 800, ImageUrl = "x.jpg", Url = "https://fake/x" }, // giữ
                Candidate("Tai nghe Sony giá rẻ", 100, "https://fake/y"),                                                        // < 40% → loại
                Candidate("Tai nghe Sony bản cao cấp", 1500, "https://fake/z"),                                                  // đắt hơn → loại
                Candidate("Loa Bluetooth Sony", 500, "https://fake/w"));                                                         // khác loại → loại
            var options = new FetchOptions { Timeout = TimeSpan.FromSeconds(5) };
            var original = Original();

            var suggestions = await Finder(scraper).FindAsync(original, options);

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
            var scraper = new SearchableFake("Fake", "https://fake/", new ScrapedProduct { Name = "Tai nghe Sony", Price = null });

            var s = Assert.Single(await Finder(scraper).FindAsync(Original(latestPrice: 0)));

            Assert.Equal(0m, s.Price);
            Assert.Equal("", s.ImageURL);
            Assert.Equal("", s.ProductLink);
        }

        [Fact]
        public async Task UnsupportedPlatform_ReturnsEmpty_WithoutSearching()
        {
            var scraper = new SearchableFake("Fake", "https://fake/", Candidate("Tai nghe Sony", 800, "https://fake/x"));

            Assert.Empty(await Finder(scraper).FindAsync(Original(link: "https://other/p1")));
            Assert.Null(scraper.LastKeyword);
        }

        [Fact]
        public async Task EmptyKeyword_ReturnsEmpty_WithoutSearching()
        {
            var scraper = new SearchableFake("Fake", "https://fake/");
            var product = Original();
            product.ProductName = "Chính hãng - Freeship"; // chỉ toàn từ quảng cáo → từ khoá rỗng

            Assert.Empty(await Finder(scraper).FindAsync(product));
            Assert.Null(scraper.LastKeyword);
        }

        [Fact]
        public async Task MergesAllSearchablePlatforms_CheapestFirst()
        {
            var a = new SearchableFake("A", "https://a/", Candidate("Tai nghe Sony A", 900, "https://a/1"), Candidate("Tai nghe Sony A2", 600, "https://a/2"));
            var b = new SearchableFake("B", "https://b/", Candidate("Tai nghe Sony B", 700, "https://b/1"));

            var result = await Finder(a, b).FindAsync(Original(link: "https://a/p1"));

            Assert.Equal(new[] { 600m, 700m, 900m }, result.Select(s => s.Price));
        }

        [Fact]
        public async Task ProductOnNonSearchablePlatform_GetsSuggestionsFromOtherPlatforms()
        {
            // Sản phẩm CellphoneS (không tìm kiếm được) → gợi ý lấy từ sàn có tìm kiếm (Tiki)
            var tiki = new SearchableFake("Tiki", "https://fake/", Candidate("Tai nghe Sony WH-1000XM4", 800, "https://fake/x"));

            var result = await Finder(new NonSearchableFake(), tiki).FindAsync(Original(link: "https://nosearch/p1"));

            Assert.Equal("https://fake/x", Assert.Single(result).ProductLink);
        }

        [Fact]
        public async Task ExcludesTheOriginalProduct()
        {
            var scraper = new SearchableFake("Fake", "https://fake/",
                Candidate("Tai nghe Bluetooth Sony WH-1000XM5", 1000, "https://fake/p1?spid=9"), // chính nó (khác query)
                Candidate("Tai nghe Sony WH-1000XM4", 800, "https://fake/x"));

            var result = await Finder(scraper).FindAsync(Original(link: "https://fake/p1"));

            Assert.Equal("https://fake/x", Assert.Single(result).ProductLink);
        }

        [Fact]
        public async Task KeepsAtMost20_Cheapest()
        {
            var many = Enumerable.Range(1, 25).Select(i => Candidate($"Tai nghe Sony {i}", 400 + i * 10, $"https://fake/{i}")).ToArray();

            var result = await Finder(new SearchableFake("Fake", "https://fake/", many)).FindAsync(Original());

            Assert.Equal(20, result.Count);
            Assert.Equal(410m, result.First().Price);
            Assert.Equal(600m, result.Last().Price);
        }

        [Fact]
        public async Task OnePlatformFails_OthersStillReturned()
        {
            var broken = new SearchableFake("Broken", "https://broken/", () => throw new HttpRequestException("403"));
            var ok = new SearchableFake("Ok", "https://fake/", Candidate("Tai nghe Sony WH-1000XM4", 800, "https://fake/x"));

            var result = await Finder(broken, ok).FindAsync(Original());

            Assert.Single(result);
        }

        [Fact]
        public async Task AllPlatformsFail_ThrowsOriginalError_SoJobCanRetry()
        {
            var error = new HttpRequestException("403");
            var broken = new SearchableFake("Broken", "https://fake/", () => throw error);

            var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Finder(broken).FindAsync(Original()));

            Assert.Same(error, thrown);
        }

        [Fact]
        public async Task NoSearchablePlatforms_ReturnsEmpty()
        {
            Assert.Empty(await Finder(new NonSearchableFake()).FindAsync(Original(link: "https://nosearch/p1")));
        }
    }
}
