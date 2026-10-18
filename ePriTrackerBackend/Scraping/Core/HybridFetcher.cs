using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>
    /// Thử lần lượt các chiến lược tải theo thứ tự truyền vào (ví dụ HttpClient → Playwright).
    /// Đây là nơi DUY NHẤT ghi telemetry cho việc gọi API sàn.
    /// </summary>
    public class HybridFetcher : IHybridFetcher
    {
        private readonly IReadOnlyList<IFetchStrategy> _strategies;
        private readonly IScraperMetrics _metrics;
        private readonly ILogger<HybridFetcher> _logger;

        public HybridFetcher(IEnumerable<IFetchStrategy> strategies, IScraperMetrics metrics, ILogger<HybridFetcher> logger)
        {
            _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
            if (_strategies.Count == 0)
                throw new ArgumentException("Cần ít nhất một IFetchStrategy.", nameof(strategies));

            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<JsonElement> FetchJsonAsync(string path, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            options ??= FetchOptions.Default;

            for (int i = 0; ; i++)
            {
                var strategy = _strategies[i];
                bool isLast = i == _strategies.Count - 1;

                try
                {
                    var result = await strategy.FetchJsonAsync(path, options, cancellationToken);
                    _metrics.RecordSuccess(strategy.Name);
                    return result;
                }
                catch (Exception ex) when (!isLast && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{Strategy} thất bại ({Msg}). Chuyển sang {Next} cho: {Path}",
                        strategy.Name, ex.Message, _strategies[i + 1].Name, path);
                }
                catch (Exception) when (isLast)
                {
                    _logger.LogError("Tất cả chiến lược tải đều thất bại. Bỏ cuộc cho: {Path}", path);
                    _metrics.RecordFailure();
                    throw; // Giữ nguyên lỗi gốc để tầng trên xử lý như trước
                }
            }
        }
    }
}
