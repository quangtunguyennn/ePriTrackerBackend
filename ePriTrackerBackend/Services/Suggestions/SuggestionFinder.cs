using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services.Suggestions
{
    /// <summary>
    /// Tìm sản phẩm thay thế rẻ hơn cho một sản phẩm đang theo dõi.
    /// </summary>
    public interface ISuggestionFinder
    {
        /// <summary>
        /// Tìm trên cùng sàn với sản phẩm gốc, lọc bằng <see cref="ISuggestionMatcher"/>.
        /// Sàn chưa được hỗ trợ hoặc không tạo được từ khoá → danh sách rỗng.
        /// </summary>
        /// <exception cref="Exception">Lỗi mạng/bị chặn khi tìm kiếm – để tầng gọi tự quyết retry hay bỏ qua.</exception>
        Task<List<SuggestionProduct>> FindAsync(Product product, FetchOptions options = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Dùng chung cho lazy-load (ProductRepository) và job Hangfire (SuggestionCrawlService) – trước đây mỗi nơi tự viết.
    /// </summary>
    public class SuggestionFinder : ISuggestionFinder
    {
        private const int SearchLimit = 10;

        private readonly IPlatformScraperResolver _scrapers;
        private readonly ISuggestionMatcher _matcher;

        public SuggestionFinder(IPlatformScraperResolver scrapers, ISuggestionMatcher matcher)
        {
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        }

        public async Task<List<SuggestionProduct>> FindAsync(Product product, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            if (product == null || !_scrapers.TryResolve(product.ProductLink, out var scraper))
                return new List<SuggestionProduct>();

            string keyword = _matcher.ExtractSearchKeyword(product.ProductName);
            if (string.IsNullOrWhiteSpace(keyword))
                return new List<SuggestionProduct>();

            decimal referencePrice = _matcher.GetReferencePrice(product.InitialPrice, product.LatestPrice);
            var results = await scraper.SearchAsync(keyword, SearchLimit, options, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

            var suggestions = new List<SuggestionProduct>();
            foreach (var candidate in results)
            {
                string candidateName = candidate.Name ?? "";
                decimal candidatePrice = candidate.Price ?? 0;

                if (!_matcher.IsSimilarProduct(product.ProductName, candidateName)) continue;
                if (!_matcher.IsPriceAcceptable(candidatePrice, referencePrice)) continue;

                suggestions.Add(new SuggestionProduct
                {
                    SuggestionProductId = Guid.NewGuid(),
                    ProductId = product.ProductId,
                    ProductName = candidateName,
                    Price = candidatePrice,
                    ImageURL = candidate.ImageUrl ?? "",
                    ProductLink = candidate.Url ?? "",
                    LastUpdatedAt = now
                });
            }

            return suggestions;
        }
    }
}
