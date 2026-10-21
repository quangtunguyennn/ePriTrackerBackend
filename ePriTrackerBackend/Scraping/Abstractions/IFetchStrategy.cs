using System.Text.Json;

namespace ePriTrackerBackend.Scraping.Abstractions
{
    /// <summary>
    /// Một cách tải JSON từ sàn TMĐT (Strategy Pattern), ví dụ HttpClient (nhanh) hoặc Playwright (tàng hình, chậm).
    /// </summary>
    public interface IFetchStrategy
    {
        /// <summary>Tên dùng cho log và telemetry, xem <see cref="FetchStrategyNames"/>.</summary>
        string Name { get; }

        /// <param name="path">Đường dẫn API, ví dụ "/api/v2/products/123".</param>
        Task<JsonElement> FetchJsonAsync(string path, FetchOptions options, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Tuỳ chọn cho một lần tải.
    /// </summary>
    public sealed class FetchOptions
    {
        public static readonly FetchOptions Default = new FetchOptions();

        /// <summary>
        /// Thời gian chờ tối đa cho chiến lược có hỗ trợ (HttpClient). Null = dùng mặc định của HttpClient (100 giây).
        /// Đặt ngắn để nhanh chóng chuyển sang chiến lược dự phòng khi bị bóp băng thông.
        /// </summary>
        public TimeSpan? Timeout { get; init; }
    }

    public static class FetchStrategyNames
    {
        public const string HttpClient = "HttpClient";
        public const string Playwright = "Playwright";
    }
}
