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
    }

    /// <summary>
    /// Đọc số liệu telemetry (tách riêng khỏi phần ghi – ISP).
    /// </summary>
    public interface IScraperMetricsReader
    {
        object GetMetrics();
    }
}
