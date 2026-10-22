using Microsoft.Playwright;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>Mở trang bằng trình duyệt thật (chạy JavaScript) và trả HTML sau khi render.</summary>
    public interface IPageRenderer
    {
        Task<string> RenderAsync(string url, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Trình duyệt Chromium headless dùng chung, mỗi lần chỉ render MỘT trang (nhẹ máy, không dồn request vào sàn).
    /// Không tải ảnh thật (trả ảnh 1×1), chặn font/video cho nhanh – thẻ &lt;img src&gt; vẫn giữ link thật. Cuộn trang để nạp các khối lazy-load.
    /// Tách riêng với TikiBrowserService (trang Tiki cần phiên/cookie riêng).
    /// </summary>
    public sealed class PlaywrightPageRenderer : IPageRenderer, IAsyncDisposable
    {
        private const int NavigationTimeoutMs = 45000;
        private const int ScrollSteps = 10;
        private const int ScrollStepPx = 1200;
        private const int ScrollPauseMs = 700;
        private const int SettleMs = 1500;

        // GIF 1×1 trong suốt (43 byte)
        private static readonly byte[] TransparentGif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ILogger<PlaywrightPageRenderer> _logger;
        private IPlaywright _playwright;
        private IBrowser _browser;

        public PlaywrightPageRenderer(ILogger<PlaywrightPageRenderer> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> RenderAsync(string url, CancellationToken cancellationToken = default)
        {
            await _lock.WaitAsync(cancellationToken);
            IBrowserContext context = null;
            try
            {
                var browser = await GetBrowserAsync();
                context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                    Locale = "vi-VN",
                    ViewportSize = new ViewportSize { Width = 1366, Height = 900 }
                });

                // Ảnh: trả ảnh 1×1 thay vì chặn – chặn (abort) làm trang tưởng ảnh lỗi và thay link thật bằng ảnh giữ chỗ
                // (CellphoneS đổi src sang "placehoder.png"). Font/video không ảnh hưởng dữ liệu → chặn cho nhanh.
                await context.RouteAsync("**/*", route => route.Request.ResourceType switch
                {
                    "image" => route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "image/gif", BodyBytes = TransparentGif }),
                    "media" or "font" => route.AbortAsync(),
                    _ => route.ContinueAsync()
                });

                var page = await context.NewPageAsync();
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavigationTimeoutMs });

                for (int i = 0; i < ScrollSteps; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await page.Mouse.WheelAsync(0, ScrollStepPx);
                    await page.WaitForTimeoutAsync(ScrollPauseMs);
                }
                await page.WaitForTimeoutAsync(SettleMs);

                return await page.ContentAsync();
            }
            finally
            {
                if (context != null)
                {
                    try { await context.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Đóng context Playwright lỗi."); }
                }
                _lock.Release();
            }
        }

        // Gọi trong _lock. Trình duyệt chết (crash) → mở lại.
        private async Task<IBrowser> GetBrowserAsync()
        {
            if (_browser is { IsConnected: true }) return _browser;

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return _browser;
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_browser != null) await _browser.CloseAsync(); } catch { /* đang tắt server */ }
            _playwright?.Dispose();
            _lock.Dispose();
        }
    }
}
