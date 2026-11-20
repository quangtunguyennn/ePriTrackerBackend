using System.Collections.Concurrent;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Services
{
    public class ScraperMetricsService : IScraperMetrics, IScraperMetricsReader
    {
        public const int MaxRecentErrors = 50;
        private const int MaxTextLength = 300;

        // Đếm theo tên chiến lược để thêm chiến lược mới không phải sửa class này (OCP).
        // ConcurrentDictionary.AddOrUpdate an toàn trong môi trường đa luồng của Hangfire.
        private readonly ConcurrentDictionary<string, int> _successCounts = new ConcurrentDictionary<string, int>();
        private int _failedCount = 0;

        // Số liệu chi tiết cho trang Admin: theo (sàn, loại thao tác) + hàng đợi lỗi gần nhất – ghi/đọc dưới một khoá
        private readonly object _detailLock = new();
        private readonly Dictionary<(string Platform, string Operation), StatsBucket> _buckets = new();
        private readonly LinkedList<ScrapeErrorEntry> _recentErrors = new();
        private readonly TimeProvider _time;
        private readonly DateTimeOffset _startedAt;

        public ScraperMetricsService(TimeProvider timeProvider = null)
        {
            _time = timeProvider ?? TimeProvider.System;
            _startedAt = _time.GetUtcNow();
        }

        public void RecordSuccess(string strategyName) => _successCounts.AddOrUpdate(strategyName, 1, (_, count) => count + 1);
        public void RecordFailure() => Interlocked.Increment(ref _failedCount);

        public void Record(ScrapeRecord record)
        {
            if (record == null) return;

            var now = _time.GetUtcNow();
            var key = (record.Platform ?? "?", record.Operation ?? "?");

            lock (_detailLock)
            {
                if (!_buckets.TryGetValue(key, out var bucket)) _buckets[key] = bucket = new StatsBucket();

                bucket.Requests++;
                bucket.TotalDurationMs += Math.Max(0, record.Duration.TotalMilliseconds);
                if (record.Success)
                {
                    bucket.Successes++;
                    if (record.UsedFallback) bucket.FallbackSuccesses++;
                    bucket.LastSuccessAt = now;
                    return;
                }

                bucket.Failures++;
                bucket.LastFailureAt = now;

                _recentErrors.AddFirst(new ScrapeErrorEntry
                {
                    At = now,
                    Platform = key.Item1,
                    Operation = key.Item2,
                    Target = Shorten(record.Target),
                    Error = Shorten(record.Error)
                });
                if (_recentErrors.Count > MaxRecentErrors) _recentErrors.RemoveLast();
            }
        }

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

        public ScraperMetricsSnapshot GetSnapshot()
        {
            lock (_detailLock)
            {
                return new ScraperMetricsSnapshot
                {
                    StartedAt = _startedAt,
                    Operations = _buckets
                        .OrderBy(x => x.Key.Platform).ThenBy(x => x.Key.Operation)
                        .Select(x => new PlatformOperationStats
                        {
                            Platform = x.Key.Platform,
                            Operation = x.Key.Operation,
                            Requests = x.Value.Requests,
                            Successes = x.Value.Successes,
                            Failures = x.Value.Failures,
                            FallbackSuccesses = x.Value.FallbackSuccesses,
                            AverageDurationMs = x.Value.Requests > 0 ? Math.Round(x.Value.TotalDurationMs / x.Value.Requests, 1) : 0,
                            LastSuccessAt = x.Value.LastSuccessAt,
                            LastFailureAt = x.Value.LastFailureAt
                        })
                        .ToList(),
                    RecentErrors = _recentErrors.ToList()
                };
            }
        }

        private static string Shorten(string text)
            => text == null || text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "…";

        private sealed class StatsBucket
        {
            public int Requests;
            public int Successes;
            public int Failures;
            public int FallbackSuccesses;
            public double TotalDurationMs;
            public DateTimeOffset? LastSuccessAt;
            public DateTimeOffset? LastFailureAt;
        }
    }
}
