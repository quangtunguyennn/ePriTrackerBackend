using System.Text.RegularExpressions;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Tests.Helpers
{
    /// <summary>
    /// Sàn giả "fake.vn" cho test service: link dạng https://fake.vn/abc-p123.html, không gọi mạng.
    /// </summary>
    internal sealed class FakePlatformScraper : ISearchablePlatformScraper
    {
        private static readonly Regex IdRegex = new(@"-p(\d+)\.html");

        /// <summary>Sản phẩm theo link đã chuẩn hoá. Link không có trong đây → GetProductAsync ném lỗi (như bị chặn).</summary>
        public Dictionary<string, ScrapedProduct> Products { get; } = new();
        public List<string> RequestedUrls { get; } = new();

        public static string Normalized(string id) => $"https://fake.vn/product-p{id}.html";

        public string Platform => "Fake";

        public bool CanHandle(string productUrl) => productUrl != null && productUrl.StartsWith("https://fake.vn/");

        public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
        {
            var match = IdRegex.Match(productUrl ?? "");
            normalizedUrl = match.Success ? Normalized(match.Groups[1].Value) : null!;
            return match.Success;
        }

        public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(productUrl);
            TryNormalizeProductUrl(productUrl, out var normalized);
            return Products.TryGetValue(normalized ?? "", out var product)
                ? Task.FromResult(product)
                : Task.FromException<ScrapedProduct>(new HttpRequestException("Lấy dữ liệu thất bại."));
        }

        public Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScrapedProduct>>(Array.Empty<ScrapedProduct>());
    }

    internal sealed class SingleScraperResolver : IPlatformScraperResolver
    {
        private readonly IPlatformScraper _scraper;
        public SingleScraperResolver(IPlatformScraper scraper) => _scraper = scraper;

        public bool TryResolve(string productUrl, out IPlatformScraper scraper)
        {
            scraper = _scraper.CanHandle(productUrl) ? _scraper : null!;
            return scraper != null;
        }

        public IReadOnlyList<ISearchablePlatformScraper> SearchablePlatforms
            => _scraper is ISearchablePlatformScraper s ? new[] { s } : Array.Empty<ISearchablePlatformScraper>();
    }

    internal sealed class FakeEventScraper : IEventScraper
    {
        public string Platform => "Tiki";

        public List<ScrapedEvent> CurrentEvents { get; } = new();

        /// <summary>Sản phẩm theo link sự kiện. Link có trong <see cref="FailingUrls"/> → ném lỗi.</summary>
        public Dictionary<string, List<ScrapedProduct>> ProductsByEventUrl { get; } = new();
        public HashSet<string> FailingUrls { get; } = new();
        public int ProductCalls { get; private set; }

        public Task<IReadOnlyList<ScrapedEvent>> GetCurrentEventsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScrapedEvent>>(CurrentEvents);

        public Task<IReadOnlyList<ScrapedProduct>> GetEventProductsAsync(string eventUrl, CancellationToken cancellationToken = default)
        {
            ProductCalls++;
            if (FailingUrls.Contains(eventUrl)) throw new TimeoutException("Playwright timeout");
            return Task.FromResult<IReadOnlyList<ScrapedProduct>>(
                ProductsByEventUrl.TryGetValue(eventUrl, out var products) ? products : new List<ScrapedProduct>());
        }
    }
}
