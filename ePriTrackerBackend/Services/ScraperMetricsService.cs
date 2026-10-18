using System.Collections.Concurrent;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services
{
    public class ScraperMetricsService : IScraperMetrics, IScraperMetricsReader
    {
        // Đếm theo tên chiến lược để thêm chiến lược mới không phải sửa class này (OCP).
        // ConcurrentDictionary.AddOrUpdate an toàn trong môi trường đa luồng của Hangfire.
        private readonly ConcurrentDictionary<string, int> _successCounts = new ConcurrentDictionary<string, int>();
        private int _failedCount = 0;

        public void RecordSuccess(string strategyName) => _successCounts.AddOrUpdate(strategyName, 1, (_, count) => count + 1);
        public void RecordFailure() => Interlocked.Increment(ref _failedCount);

        public object GetMetrics()
        {
            int httpClientCount = _successCounts.GetValueOrDefault(FetchStrategyNames.HttpClient);
            int playwrightCount = _successCounts.GetValueOrDefault(FetchStrategyNames.Playwright);
            int total = httpClientCount + playwrightCount + _failedCount;

            // Tính toán tỷ lệ phần trăm tự động
            string optimizationRate = total > 0
                ? $"{Math.Round((double)httpClientCount / total * 100, 2)}%"
                : "0%";

            // Giữ nguyên shape JSON cũ của /api/admin/metrics/scraper
            return new
            {
                TotalRequests = total,
                HttpClientSuccess = httpClientCount,
                PlaywrightSuccess = playwrightCount,
                Failed = _failedCount,
                HttpClientOptimizationRate = optimizationRate
            };
        }
    }
}
