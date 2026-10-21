using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;

namespace ePriTrackerBackend.Tests.Scraping
{
    public class PlatformScraperResolverTests
    {
        /// <summary>Strategy giả: nhận link chứa host đã cho – mô phỏng thêm sàn mới mà không sửa resolver (OCP).</summary>
        private sealed class FakeScraper : IPlatformScraper
        {
            private readonly string _host;
            public FakeScraper(string platform, string host) { Platform = platform; _host = host; }

            public string Platform { get; }
            public bool CanHandle(string productUrl) => productUrl.Contains(_host);
            public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl) { normalizedUrl = productUrl; return true; }
            public Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private static readonly PlatformScraperResolver Resolver = new(new IPlatformScraper[]
        {
            new FakeScraper("Tiki", "tiki.vn"),
            new FakeScraper("Shopee", "shopee.vn")
        });

        [Theory]
        [InlineData("https://tiki.vn/a-p1.html", "Tiki")]
        [InlineData("https://shopee.vn/a-i.1.2", "Shopee")]
        public void PicksMatchingStrategy(string url, string expectedPlatform)
        {
            Assert.True(Resolver.TryResolve(url, out var scraper));
            Assert.Equal(expectedPlatform, scraper.Platform);
        }

        [Theory]
        [InlineData("https://lazada.vn/x")]
        [InlineData("")]
        [InlineData(null)]
        public void UnsupportedOrEmpty_ReturnsFalse(string? url)
        {
            Assert.False(Resolver.TryResolve(url!, out var scraper));
            Assert.Null(scraper);
        }
    }
}
