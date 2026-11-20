namespace ePriTrackerBackend.Scraping.Abstractions
{
    /// <summary>
    /// Ghi nhận kết quả mỗi lần lấy dữ liệu từ sàn TMĐT (telemetry Hybrid Scraping).
    /// </summary>
    public interface IScraperMetrics
    {
        /// <param name="strategyName">Tên chiến lược đã lấy dữ liệu thành công, xem <see cref="FetchStrategyNames"/>.</param>
        void RecordSuccess(string strategyName);

        /// <summary>Tất cả chiến lược đều thất bại.</summary>
        void RecordFailure();

        /// <summary>
        /// Số liệu chi tiết theo sàn + loại thao tác cho trang Admin (giám sát cào). Độc lập với 2 bộ đếm cũ ở trên
        /// (bộ đếm cũ giữ nguyên để /api/admin/metrics/scraper trả đúng con số như trước).
        /// </summary>
        void Record(ScrapeRecord record);
    }

    /// <summary>
    /// Đọc số liệu telemetry (tách riêng khỏi phần ghi – ISP).
    /// </summary>
    public interface IScraperMetricsReader
    {
        object GetMetrics();

        /// <summary>Số liệu theo sàn + loại thao tác và các lỗi gần nhất (chỉ trong bộ nhớ – mất khi khởi động lại server).</summary>
        ScraperMetricsSnapshot GetSnapshot();
    }

    /// <summary>Loại thao tác với sàn, dùng làm nhóm thống kê.</summary>
    public static class ScrapeOperations
    {
        /// <summary>Lấy một trang sản phẩm (thêm sản phẩm, job giá cào lẻ).</summary>
        public const string Product = "Product";

        /// <summary>Tìm kiếm trên sàn (gợi ý sản phẩm).</summary>
        public const string Search = "Search";

        /// <summary>Lấy giá theo lô (job giá).</summary>
        public const string BatchPrice = "BatchPrice";

        /// <summary>Đọc sự kiện / sản phẩm của sự kiện.</summary>
        public const string Events = "Events";
    }

    /// <summary>Một lần gọi sàn.</summary>
    public sealed record ScrapeRecord
    {
        public string Platform { get; init; }
        public string Operation { get; init; }
        public bool Success { get; init; }

        /// <summary>Chiến lược đã lấy được dữ liệu (nếu có), vd HttpClient / Playwright.</summary>
        public string Strategy { get; init; }

        /// <summary>Chiến lược đầu tiên lỗi, phải dùng chiến lược dự phòng.</summary>
        public bool UsedFallback { get; init; }

        public TimeSpan Duration { get; init; }

        /// <summary>Link / từ khoá / mô tả ngắn của thứ được lấy – hiện kèm lỗi để Admin biết lỗi ở đâu.</summary>
        public string Target { get; init; }

        public string Error { get; init; }
    }

    public sealed class ScraperMetricsSnapshot
    {
        /// <summary>Thời điểm bắt đầu đếm (server khởi động).</summary>
        public DateTimeOffset StartedAt { get; init; }

        public IReadOnlyList<PlatformOperationStats> Operations { get; init; } = Array.Empty<PlatformOperationStats>();

        /// <summary>Mới nhất trước.</summary>
        public IReadOnlyList<ScrapeErrorEntry> RecentErrors { get; init; } = Array.Empty<ScrapeErrorEntry>();
    }

    public sealed class PlatformOperationStats
    {
        public string Platform { get; init; }
        public string Operation { get; init; }
        public int Requests { get; init; }
        public int Successes { get; init; }
        public int Failures { get; init; }

        /// <summary>Thành công nhưng phải dùng chiến lược dự phòng (vd Tiki: Playwright).</summary>
        public int FallbackSuccesses { get; init; }

        public double AverageDurationMs { get; init; }
        public DateTimeOffset? LastSuccessAt { get; init; }
        public DateTimeOffset? LastFailureAt { get; init; }
    }

    public sealed class ScrapeErrorEntry
    {
        public DateTimeOffset At { get; init; }
        public string Platform { get; init; }
        public string Operation { get; init; }
        public string Target { get; init; }
        public string Error { get; init; }
    }

    /// <summary>Không ghi gì – cho nơi không cần telemetry (test, dựng tay).</summary>
    public sealed class NullScraperMetrics : IScraperMetrics
    {
        public static readonly NullScraperMetrics Instance = new();
        private NullScraperMetrics() { }
        public void RecordSuccess(string strategyName) { }
        public void RecordFailure() { }
        public void Record(ScrapeRecord record) { }
    }
}
