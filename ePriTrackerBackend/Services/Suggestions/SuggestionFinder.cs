using System.Diagnostics;
using System.Runtime.ExceptionServices;
using ePriTrackerBackend.Models.Entities;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Services.Suggestions
{
    /// <summary>
    /// Tìm sản phẩm thay thế rẻ hơn cho một sản phẩm đang theo dõi.
    /// </summary>
    public interface ISuggestionFinder
    {
        /// <summary>
        /// Tìm trên MỌI sàn hỗ trợ tìm kiếm (không chỉ sàn của sản phẩm gốc), lọc bằng <see cref="ISuggestionMatcher"/>,
        /// bỏ chính sản phẩm gốc, sắp theo giá tăng dần. Sàn của sản phẩm gốc chưa được hỗ trợ hoặc không tạo được từ khoá → danh sách rỗng.
        /// </summary>
        /// <exception cref="Exception">Tất cả sàn đều lỗi mạng/bị chặn – để tầng gọi tự quyết retry hay bỏ qua.</exception>
        Task<List<SuggestionProduct>> FindAsync(Product product, FetchOptions options = null, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Dùng chung cho lazy-load (SuggestionService) và job Hangfire (SuggestionCrawlService).
    /// </summary>
    public class SuggestionFinder : ISuggestionFinder
    {
        private const int SearchLimitPerPlatform = 10;
        private const int MaxSuggestions = 20;

        // Mỗi sàn có tối đa chừng này thời gian; sàn chậm bị bỏ (ghi log) thay vì kéo cả trang gợi ý chờ theo
        private static readonly TimeSpan PerPlatformTimeout = TimeSpan.FromSeconds(35);

        private readonly IPlatformScraperResolver _scrapers;
        private readonly ISuggestionMatcher _matcher;
        private readonly ILogger<SuggestionFinder> _logger;

        public SuggestionFinder(IPlatformScraperResolver scrapers, ISuggestionMatcher matcher, ILogger<SuggestionFinder> logger)
        {
            _scrapers = scrapers ?? throw new ArgumentNullException(nameof(scrapers));
            _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<SuggestionProduct>> FindAsync(Product product, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            if (product == null || !_scrapers.TryResolve(product.ProductLink, out var sourceScraper) || _scrapers.SearchablePlatforms.Count == 0)
                return new List<SuggestionProduct>();

            string keyword = _matcher.ExtractSearchKeyword(product.ProductName);
            if (string.IsNullOrWhiteSpace(keyword))
                return new List<SuggestionProduct>();

            decimal referencePrice = 10; // FIX
            sourceScraper.TryNormalizeProductUrl(product.ProductLink, out var originalLink);

            var candidates = await SearchAllPlatformsAsync(keyword, options, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7));

            // Tách từng bước lọc (cùng điều kiện như trước) để ghi log: mất ứng viên ở bước nào, của sàn nào
            var notOriginal = candidates.Where(c => !IsSameProduct(c.Url, originalLink)).ToList();

            var similar = notOriginal
                .Select(c => new { Candidate = c, Name = c.Name ?? "", Price = c.Price ?? 0 })
                .Where(x => _matcher.IsSimilarProduct(product.ProductName, x.Name))
                .ToList();

            var accepted = similar
                .Where(x => _matcher.IsPriceAcceptable(x.Price, referencePrice))
                .ToList();

            _logger.LogInformation(
                "Gợi ý cho '{Keyword}' (giá gốc {Reference:N0}): ứng viên [{All}] → khác sản phẩm gốc [{NotOriginal}] → cùng loại [{Similar}] → đạt giá [{Accepted}]",
                keyword, referencePrice,
                ByPlatform(candidates.Select(c => c.Url)),
                ByPlatform(notOriginal.Select(c => c.Url)),
                ByPlatform(similar.Select(x => x.Candidate.Url)),
                ByPlatform(accepted.Select(x => x.Candidate.Url)));

            return accepted
                .OrderBy(x => x.Price)
                .Take(MaxSuggestions)
                .Select(x => new SuggestionProduct
                {
                    SuggestionProductId = Guid.NewGuid(),
                    ProductId = product.ProductId,
                    ProductName = x.Name,
                    Price = x.Price,
                    ImageURL = x.Candidate.ImageUrl ?? "",
                    ProductLink = x.Candidate.Url ?? "",
                    LastUpdatedAt = now
                })
                .ToList();
        }

        /// <summary>
        /// Tìm song song trên mọi sàn hỗ trợ tìm kiếm. Một sàn lỗi hoặc quá <see cref="PerPlatformTimeout"/> thì bỏ qua sàn đó (ghi log);
        /// TẤT CẢ đều lỗi thì ném lỗi của sàn đầu tiên – để job Hangfire vẫn retry như trước.
        /// </summary>
        private async Task<List<ScrapedProduct>> SearchAllPlatformsAsync(string keyword, FetchOptions options, CancellationToken cancellationToken)
        {
            var platforms = _scrapers.SearchablePlatforms;
            _logger.LogInformation("Tìm gợi ý '{Keyword}' trên: {Platforms}", keyword, string.Join(", ", platforms.Select(p => p.Platform)));

            var tasks = platforms
                .Select(p => SearchOnePlatformAsync(p, keyword, options, cancellationToken))
                .ToList();

            try { await Task.WhenAll(tasks); }
            catch { /* xử lý theo từng sàn bên dưới */ }

            for (int i = 0; i < tasks.Count; i++)
            {
                if (tasks[i].IsFaulted)
                    _logger.LogWarning(tasks[i].Exception?.InnerException, "Tìm gợi ý trên {Platform} thất bại", platforms[i].Platform);
            }

            var succeeded = tasks.Where(t => t.Status == TaskStatus.RanToCompletion).ToList();
            if (succeeded.Count == 0)
                ExceptionDispatchInfo.Capture(tasks[0].Exception?.InnerException ?? new OperationCanceledException(cancellationToken)).Throw();

            return succeeded.SelectMany(t => t.Result).ToList();
        }

        // Một sàn: giới hạn thời gian, ghi thời gian chạy. Lỗi ném đồng bộ cũng thành Task lỗi nên không làm hỏng các sàn khác.
        private async Task<IReadOnlyList<ScrapedProduct>> SearchOnePlatformAsync(ISearchablePlatformScraper platform, string keyword, FetchOptions options, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(PerPlatformTimeout);

            try
            {
                var result = await platform.SearchAsync(keyword, SearchLimitPerPlatform, options, timeoutCts.Token);
                _logger.LogInformation("{Platform} trả {Count} ứng viên cho '{Keyword}' sau {Ms}ms", platform.Platform, result.Count, keyword, sw.ElapsedMilliseconds);
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{platform.Platform} tìm kiếm quá {PerPlatformTimeout.TotalSeconds:0}s.");
            }
        }

        // "Tiki=10, HACOM=3" – đếm theo sàn suy ra từ link
        private string ByPlatform(IEnumerable<string> urls)
        {
            var parts = urls
                .GroupBy(u => _scrapers.TryResolve(u, out var scraper) ? scraper.Platform : "?")
                .Select(g => $"{g.Key}={g.Count()}");

            string text = string.Join(", ", parts);
            return text.Length == 0 ? "-" : text;
        }

        private bool IsSameProduct(string candidateUrl, string originalLink)
            => originalLink != null
               && !string.IsNullOrWhiteSpace(candidateUrl)
               && _scrapers.TryResolve(candidateUrl, out var scraper)
               && scraper.TryNormalizeProductUrl(candidateUrl, out var normalized)
               && string.Equals(normalized, originalLink, StringComparison.OrdinalIgnoreCase);
    }
}