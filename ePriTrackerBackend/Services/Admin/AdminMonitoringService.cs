using ePriTrackerBackend.Models.DTOs;
using ePriTrackerBackend.Repositories;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services.Admin
{
    /// <summary>Tab "Giám sát cào" của trang Admin: số liệu cào theo sàn, job nền, sức khoẻ dữ liệu, lỗi gần đây.</summary>
    public interface IAdminMonitoringService
    {
        Task<AdminMonitoringDTO> GetMonitoringAsync(CancellationToken cancellationToken = default);
    }

    public class AdminMonitoringService : IAdminMonitoringService
    {
        /// <summary>Tới lịch cào quá chừng này mà chưa cào → "quá hạn" (job chạy mỗi phút, mỗi lượt tối đa 50 sản phẩm).</summary>
        public static readonly TimeSpan OverdueAfter = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Không lấy được dữ liệu từ sàn quá chừng này → "lâu không cào được" (Tier thấp nhất cào lại sau 24 giờ).
        /// Tính theo LastCheckedAt (lần cào thành công) chứ không theo LastUpdatedAt (lần GIÁ ĐỔI – giá đứng yên lâu là bình thường).
        /// </summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);

        public const string UnknownPlatform = "Other";

        private readonly IAdminRepository _admin;
        private readonly IScraperMetricsReader _metrics;
        private readonly IJobStatusReader _jobs;
        private readonly IPlatformScraperResolver _resolver;
        private readonly IEnumerable<IPlatformScraper> _platforms;
        private readonly TimeProvider _time;
        private readonly ILogger<AdminMonitoringService> _logger;

        public AdminMonitoringService(
            IAdminRepository admin,
            IScraperMetricsReader metrics,
            IJobStatusReader jobs,
            IPlatformScraperResolver resolver,
            IEnumerable<IPlatformScraper> platforms,
            ILogger<AdminMonitoringService> logger,
            TimeProvider timeProvider = null)
        {
            _admin = admin ?? throw new ArgumentNullException(nameof(admin));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _platforms = platforms ?? throw new ArgumentNullException(nameof(platforms));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = timeProvider ?? TimeProvider.System;
        }

        public async Task<AdminMonitoringDTO> GetMonitoringAsync(CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow();
            var snapshot = _metrics.GetSnapshot();

            // Sức khoẻ dữ liệu: chỉ sản phẩm còn người theo dõi (job giá chỉ cào những sản phẩm này)
            var tracked = (await _admin.GetProductRowsAsync(cancellationToken))
                .Where(p => p.ActiveTrackersCount > 0)
                .GroupBy(p => PlatformOf(p.ProductLink))
                .ToDictionary(g => g.Key, g => g.ToList());

            // Mọi sàn đã đăng ký + sàn xuất hiện trong số liệu/dữ liệu (vd link cũ không còn sàn hỗ trợ)
            var platformNames = _platforms.Select(p => p.Platform)
                .Concat(snapshot.Operations.Select(o => o.Platform))
                .Concat(tracked.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new AdminMonitoringDTO
            {
                GeneratedAt = now,
                MetricsSince = snapshot.StartedAt,
                RecentErrors = snapshot.RecentErrors.ToList(),
                Platforms = platformNames.Select(name =>
                {
                    var products = tracked.GetValueOrDefault(name) ?? new List<AdminProductRow>();
                    var crawled = products.Where(p => !p.IsDelisted).ToList(); // sản phẩm đã gỡ ngưng cào → không phải "quá hạn"
                    return new PlatformMonitoringDTO
                    {
                        Platform = name,
                        TrackedProducts = products.Count,
                        OverdueProducts = crawled.Count(p => p.NextCrawlAt.HasValue && now - p.NextCrawlAt.Value > OverdueAfter),
                        StaleProducts = crawled.Count(p => (p.LastCheckedAt ?? p.LastUpdatedAt) is not DateTimeOffset checkedAt || now - checkedAt > StaleAfter),
                        OutOfStockProducts = crawled.Count(p => !p.IsInStock),
                        DelistedProducts = products.Count(p => p.IsDelisted),
                        Operations = snapshot.Operations.Where(o => string.Equals(o.Platform, name, StringComparison.OrdinalIgnoreCase)).ToList()
                    };
                }).ToList()
            };

            try
            {
                result.Jobs = _jobs.GetRecurringJobs().ToList();
                result.JobCounts = _jobs.GetJobCounts();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không đọc được trạng thái job Hangfire.");
                result.JobsError = "Could not read Hangfire job status: " + ex.Message;
            }

            return result;
        }

        private string PlatformOf(string link)
            => !string.IsNullOrWhiteSpace(link) && _resolver.TryResolve(link, out var scraper) ? scraper.Platform : UnknownPlatform;
    }
}
