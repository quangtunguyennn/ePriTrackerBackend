using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>
    /// Context của Strategy Pattern: nhận tất cả <see cref="IPlatformScraper"/> đã đăng ký trong DI
    /// và chọn strategy đầu tiên nhận xử lý link.
    /// </summary>
    public class PlatformScraperResolver : IPlatformScraperResolver
    {
        private readonly IReadOnlyList<IPlatformScraper> _scrapers;

        public PlatformScraperResolver(IEnumerable<IPlatformScraper> scrapers)
        {
            _scrapers = scrapers?.ToList() ?? throw new ArgumentNullException(nameof(scrapers));
        }

        public bool TryResolve(string productUrl, out IPlatformScraper scraper)
        {
            scraper = string.IsNullOrWhiteSpace(productUrl)
                ? null
                : _scrapers.FirstOrDefault(s => s.CanHandle(productUrl));
            return scraper != null;
        }
    }
}
