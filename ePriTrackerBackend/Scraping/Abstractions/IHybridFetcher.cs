using System.Text.Json;

namespace ePriTrackerBackend.Scraping.Abstractions
{
    /// <summary>
    /// Hybrid Scraping: thử lần lượt các <see cref="IFetchStrategy"/> theo thứ tự ưu tiên,
    /// chiến lược trước thất bại thì chuyển sang chiến lược sau. Ghi telemetry tại một chỗ duy nhất.
    /// </summary>
    public interface IHybridFetcher
    {
        /// <exception cref="Exception">Ném lại lỗi của chiến lược cuối cùng khi tất cả đều thất bại.</exception>
        Task<JsonElement> FetchJsonAsync(string path, FetchOptions options = null, CancellationToken cancellationToken = default);
    }
}
