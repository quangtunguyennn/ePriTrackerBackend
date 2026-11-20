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
        private readonly string _platform;

        /// <param name="platform">Tên sàn – để trang Admin thống kê theo sàn.</param>
        public HybridFetcher(IEnumerable<IFetchStrategy> strategies, IScraperMetrics metrics, ILogger<HybridFetcher> logger, string platform = null)
        {
            _platform = platform ?? "?";
            _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
            if (_strategies.Count == 0)
                throw new ArgumentException("Cần ít nhất một IFetchStrategy.", nameof(strategies));

            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<JsonElement> FetchJsonAsync(string path, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            options ??= FetchOptions.Default;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; ; i++)
            {
                var strategy = _strategies[i];
                bool isLast = i == _strategies.Count - 1;

                try
                {
                    var result = await strategy.FetchJsonAsync(path, options, cancellationToken);
                    _metrics.RecordSuccess(strategy.Name);
                    _metrics.Record(new ScrapeRecord
                    {
                        Platform = _platform, Operation = ScrapeOperations.Product, Success = true,
                        Strategy = strategy.Name, UsedFallback = i > 0, Duration = stopwatch.Elapsed, Target = path
                    });
                    return result;
                }
                // 404/410 = sản phẩm không tồn tại: chiến lược dự phòng (vd Playwright – để vượt chặn) cũng chỉ nhận 404 sau ~18 giây
                // và còn làm mất mã trạng thái → dừng ngay, ném nguyên lỗi để job giá nhận ra "không còn trên sàn"
                catch (Exception ex) when (!isLast && !cancellationToken.IsCancellationRequested && !ScrapeErrors.IsNotFound(ex))
                {
                    _logger.LogWarning("{Strategy} thất bại ({Msg}). Chuyển sang {Next} cho: {Path}",
                        strategy.Name, ex.Message, _strategies[i + 1].Name, path);
                }
                catch (Exception ex) when (isLast || ScrapeErrors.IsNotFound(ex))
                {
                    if (isLast) _logger.LogError("Tất cả chiến lược tải đều thất bại. Bỏ cuộc cho: {Path}", path);
                    else _logger.LogWarning("{Strategy}: sàn báo không tồn tại ({Msg}), không chuyển dự phòng: {Path}", strategy.Name, ex.Message, path);
                    _metrics.RecordFailure();
                    _metrics.Record(new ScrapeRecord
                    {
                        Platform = _platform, Operation = ScrapeOperations.Product, Success = false,
                        Duration = stopwatch.Elapsed, Target = path, Error = ex.Message
                    });
                    throw; // Giữ nguyên lỗi gốc để tầng trên xử lý như trước
                }
            }
        }
    }
}
