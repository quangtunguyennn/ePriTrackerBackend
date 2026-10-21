using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;

namespace ePriTrackerBackend.Scraping.Tiki
{
    /// <summary>
    /// Tầng 2: gọi API Tiki từ bên trong trình duyệt ảo Playwright (có cookie/session thật, vượt WAF). Chậm hơn tầng 1.
    /// </summary>
    public class TikiPlaywrightFetchStrategy : IFetchStrategy
    {
        private readonly ITikiBrowserService _browser;

        public TikiPlaywrightFetchStrategy(ITikiBrowserService browser)
        {
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        }

        public string Name => FetchStrategyNames.Playwright;

        // Timeout/CancellationToken chưa được TikiBrowserService hỗ trợ – giữ hành vi cũ
        public Task<JsonElement> FetchJsonAsync(string path, FetchOptions options, CancellationToken cancellationToken = default)
            => _browser.FetchTikiApiAsync(path);
    }
}
