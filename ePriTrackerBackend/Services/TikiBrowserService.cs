using Microsoft.Playwright;
using System.Text.Json;

namespace ePriTrackerBackend.Services
{
    public interface ITikiBrowserService
    {
        Task<JsonElement> FetchTikiApiAsync(string apiPath);
    }

    public class TikiBrowserService : ITikiBrowserService, IAsyncDisposable
    {
        private readonly ILogger<TikiBrowserService> _logger;
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IPage? _sharedPage;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private const string TikiBaseUrl = "https://tiki.vn";

        // Cập nhật Stealth Script sâu hơn
        private const string StealthScript = @"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.navigator.chrome = { runtime: {} };
            Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
            Object.defineProperty(navigator, 'languages', { get: () => ['vi-VN', 'vi', 'en-US', 'en'] });
            const originalQuery = window.navigator.permissions.query;
            window.navigator.permissions.query = (parameters) => (
                parameters.name === 'notifications' ? 
                Promise.resolve({ state: Notification.permission }) : 
                originalQuery(parameters)
            );
        ";

        public TikiBrowserService(ILogger<TikiBrowserService> logger)
        {
            _logger = logger;
        }

        private async Task EnsureBrowserReadyAsync()
        {
            if (_sharedPage != null && !_sharedPage.IsClosed) return;

            await _lock.WaitAsync();
            try
            {
                if (_sharedPage != null && !_sharedPage.IsClosed) return;

                _logger.LogInformation("[TikiBrowserService] Khởi tạo Playwright Singleton...");
                _playwright ??= await Playwright.CreateAsync();

                _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = new[]
                    {
                        "--disable-blink-features=AutomationControlled",
                        "--no-sandbox",
                        "--disable-infobars",
                        "--disable-dev-shm-usage",
                        "--disable-extensions",
                        "--mute-audio",
                        "--disable-gpu"
                    }
                });

                var context = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    Locale = "vi-VN",
                    TimezoneId = "Asia/Ho_Chi_Minh",
                    BypassCSP = true
                });

                await context.AddInitScriptAsync(StealthScript);
                _sharedPage = await context.NewPageAsync();

                // Chặn toàn bộ mọi thứ không phải là API (Tối đa hoá RAM và Băng thông)
                await _sharedPage.RouteAsync("**/*", async route =>
                {
                    var type = route.Request.ResourceType;
                    if (type == "image" || type == "stylesheet" || type == "font" || type == "media" || type == "websocket")
                        await route.AbortAsync();
                    else
                        await route.ContinueAsync();
                });

                // Load trang chủ 1 lần duy nhất để lấy Cookie xịn
                _logger.LogInformation("[TikiBrowserService] Đang lấy Session Tiki...");
                await _sharedPage.GotoAsync(TikiBaseUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Task.Delay(1000);
                await _sharedPage.EvaluateAsync("window.scrollBy(0, document.body.scrollHeight / 3)");

                _logger.LogInformation("[TikiBrowserService] Đã sẵn sàng thao tác API ngầm!");
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<JsonElement> FetchTikiApiAsync(string apiPath)
        {
            await EnsureBrowserReadyAsync();

            try
            {
                // KỸ THUẬT QUAN TRỌNG: Gọi Fetch API ngay trong Context của trang Tiki.
                // Điều này làm cho Tiki lầm tưởng request đến từ ứng dụng React/NextJS của chính họ.
                var jsCode = $@"
                    async () => {{
                        const response = await fetch('{apiPath}', {{
                            method: 'GET',
                            headers: {{
                                'Accept': 'application/json, text/plain, */*'
                            }}
                        }});
                        if (!response.ok) throw new Error('HTTP ' + response.status);
                        return await response.json();
                    }}
                ";

                // Playwright tự động convert JSON trả về từ JS sang JsonElement của .NET
                var jsonResult = await _sharedPage!.EvaluateAsync<JsonElement>(jsCode);
                return jsonResult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TikiBrowserService] Lỗi khi fetch {Path}. Thử tải lại session...", apiPath);

                // Nếu bị chặn hoặc crash, xử lý dọn dẹp an toàn
                if (_sharedPage != null)
                {
                    try
                    {
                        // Thử đóng tab, nhưng có thể trình duyệt đã crash hẳn rồi
                        await _sharedPage.CloseAsync();
                    }
                    catch (Exception closeEx)
                    {
                        // Chỉ log debug, không ném lỗi ra ngoài để tránh che mất lỗi chính (ex)
                        _logger.LogDebug(closeEx, "[TikiBrowserService] Bỏ qua lỗi khi cố đóng page đã hỏng.");
                    }
                    finally
                    {
                        // BẮT BUỘC phải set về null dù CloseAsync có thành công hay không
                        _sharedPage = null;
                    }
                }

                // Ném lỗi ra ngoài kèm the inner exception để tầng trên (Controller/Service gọi nó) biết
                throw new Exception("Lấy dữ liệu Tiki thất bại, đang thiết lập lại vòng đời trình duyệt.", ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
        }
    }
}