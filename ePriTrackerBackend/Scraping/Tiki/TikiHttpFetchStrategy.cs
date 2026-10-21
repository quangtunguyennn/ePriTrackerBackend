using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Scraping.Tiki
{
    /// <summary>
    /// Tầng 1: gọi thẳng API Tiki bằng HttpClient (nhanh, tốn ít tài nguyên). Hay bị WAF chặn (403/429).
    /// </summary>
    public class TikiHttpFetchStrategy : IFetchStrategy
    {
        public const string HttpClientName = "Tiki";
        private const string TikiBaseUrl = "https://tiki.vn";

        private readonly IHttpClientFactory _httpClientFactory;

        public TikiHttpFetchStrategy(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public string Name => FetchStrategyNames.HttpClient;

        public async Task<JsonElement> FetchJsonAsync(string path, FetchOptions options, CancellationToken cancellationToken = default)
        {
            string fullUrl = BuildUrl(path);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options?.Timeout is TimeSpan timeout)
                timeoutCts.CancelAfter(timeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(fullUrl, timeoutCts.Token);

            // Bị chặn (403, 429, 503...) → HttpRequestException → HybridFetcher chuyển sang Playwright
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            return document.RootElement.Clone(); // Clone để giữ data khi document bị dispose
        }

        /// <summary>
        /// Đường dẫn tương đối → ghép với https://tiki.vn. URL tuyệt đối (vd: API banner ở tka.tiki.vn) dùng nguyên,
        /// nhưng chỉ chấp nhận tên miền Tiki để HttpClient này không bị dùng gọi sang site khác.
        /// </summary>
        private static string BuildUrl(string path)
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out _)) return $"{TikiBaseUrl}{path}";

            return TikiUrlParser.IsTikiUrl(path)
                ? path
                : throw new ArgumentException("Chỉ gọi được tên miền Tiki.", nameof(path));
        }
    }
}
