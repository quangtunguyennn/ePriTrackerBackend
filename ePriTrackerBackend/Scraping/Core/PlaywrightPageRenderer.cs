using Microsoft.Playwright;

namespace ePriTrackerBackend.Scraping.Core
{
    /// <summary>Tuỳ chọn render một trang. <see cref="Default"/> giữ hành vi cũ (nạp trang đầy đủ, cuộn 10 lần); <see cref="Search"/> dành cho trang kết quả tìm kiếm (nhanh).</summary>
    public sealed record PageRenderOptions
    {
        public int NavigationTimeoutMs { get; init; } = 45000;
        public int ScrollSteps { get; init; } = 10;
        public int ScrollStepPx { get; init; } = 1200;
        public int ScrollPauseMs { get; init; } = 700;
        public int SettleMs { get; init; } = 1500;

        /// <summary>Chờ tới khi trong trang xuất hiện chuỗi giá tiền (vd "26.990.000đ") thay vì chờ cố định; quá hạn vẫn lấy HTML hiện có.</summary>
        public bool WaitForPrice { get; init; }
        public int WaitForPriceTimeoutMs { get; init; } = 8000;

        /// <summary>Chặn CSS – không ảnh hưởng dữ liệu, trang nạp nhanh hơn.</summary>
        public bool BlockStylesheets { get; init; }

        public static PageRenderOptions Default { get; } = new();

        public static PageRenderOptions Search { get; } = new()
        {
            NavigationTimeoutMs = 20000,
            ScrollSteps = 1,
            ScrollPauseMs = 500,
            SettleMs = 300,
            WaitForPrice = true,
            WaitForPriceTimeoutMs = 8000,
            BlockStylesheets = true
        };
    }

    /// <summary>Mở trang bằng trình duyệt thật (chạy JavaScript) và trả HTML sau khi render.</summary>
    public interface IPageRenderer
    {
        Task<string> RenderAsync(string url, CancellationToken cancellationToken = default);

        Task<string> RenderAsync(string url, PageRenderOptions options, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Trình duyệt Chromium headless dùng chung, tối đa <see cref="MaxConcurrentPages"/> trang cùng lúc (mỗi trang một context riêng,
    /// nên cookie không lẫn). Không tải ảnh thật (trả ảnh 1×1), chặn font/video cho nhanh – thẻ &lt;img src&gt; vẫn giữ link thật.
    /// Tách riêng với TikiBrowserService (trang Tiki cần phiên/cookie riêng).
    /// </summary>
    public sealed class PlaywrightPageRenderer : IPageRenderer, IAsyncDisposable
    {
        // 2 trang song song: đủ để CellphoneS và HACOM tìm cùng lúc nhưng vẫn nhẹ máy
        private const int MaxConcurrentPages = 2;

        // Có chuỗi giá tiền trong chữ hiển thị chưa
        private const string PricePresentScript =
            @"() => /\d{1,3}([.,]\d{3})+\s*(đ|₫|vnđ|vnd)/i.test(document.body ? document.body.innerText : '')";

        // GIF 1×1 trong suốt (43 byte)
        private static readonly byte[] TransparentGif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

        private readonly SemaphoreSlim _slots = new(MaxConcurrentPages, MaxConcurrentPages);
        private readonly SemaphoreSlim _browserLock = new(1, 1);
        private readonly ILogger<PlaywrightPageRenderer> _logger;
        private IPlaywright _playwright;
        private IBrowser _browser;

        public PlaywrightPageRenderer(ILogger<PlaywrightPageRenderer> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<string> RenderAsync(string url, CancellationToken cancellationToken = default)
            => RenderAsync(url, PageRenderOptions.Default, cancellationToken);

        public async Task<string> RenderAsync(string url, PageRenderOptions options, CancellationToken cancellationToken = default)
        {
            options ??= PageRenderOptions.Default;

            await _slots.WaitAsync(cancellationToken);
            IBrowserContext context = null;
            try
            {
                var browser = await GetBrowserAsync(cancellationToken);
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
                    "stylesheet" when options.BlockStylesheets => route.AbortAsync(),
                    _ => route.ContinueAsync()
                });

                var page = await context.NewPageAsync();
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = options.NavigationTimeoutMs });

                if (options.WaitForPrice)
                {
                    try
                    {
                        await page.WaitForFunctionAsync(PricePresentScript, null,
                            new PageWaitForFunctionOptions { Timeout = options.WaitForPriceTimeoutMs, PollingInterval = 250 });
                    }
                    catch (PlaywrightException ex)
                    {
                        // Hết hạn chờ giá: có thể trang không có kết quả → vẫn trả HTML hiện có để tầng trên quyết định
                        _logger.LogDebug(ex, "Chưa thấy giá tiền trên trang sau {Ms}ms: {Url}", options.WaitForPriceTimeoutMs, url);
                    }
                }

                for (int i = 0; i < options.ScrollSteps; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await page.Mouse.WheelAsync(0, options.ScrollStepPx);
                    await page.WaitForTimeoutAsync(options.ScrollPauseMs);
                }

                if (options.SettleMs > 0)
                    await page.WaitForTimeoutAsync(options.SettleMs);

                return await page.ContentAsync();
            }
            finally
            {
                if (context != null)
                {
                    try { await context.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Đóng context Playwright lỗi."); }
                }
                _slots.Release();
            }
        }

        // Nhiều trang chạy song song nên việc mở trình duyệt phải có khoá riêng. Trình duyệt chết (crash) → mở lại.
        private async Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
        {
            if (_browser is { IsConnected: true }) return _browser;

            await _browserLock.WaitAsync(cancellationToken);
            try
            {
                if (_browser is { IsConnected: true }) return _browser;

                _playwright ??= await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
                return _browser;
            }
            finally
            {
                _browserLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_browser != null) await _browser.CloseAsync(); } catch { /* đang tắt server */ }
            _playwright?.Dispose();
            _slots.Dispose();
            _browserLock.Dispose();
        }
    }
}