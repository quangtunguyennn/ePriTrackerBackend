using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Text.Json;

namespace ePriTrackerBackend.Services
{
    public interface ITikiBrowserService
    {
        Task<JsonElement> FetchTikiApiAsync(string apiPath);
        Task<JsonElement> ScrapeEventPageAsync(string eventUrl); // Thêm mới vào Interface
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

        // ==============================================================
        // HÀM MỚI: QUÉT DOM BẰNG TRÌNH DUYỆT ẢO (DÀNH RIÊNG CHO SỰ KIỆN)
        // ==============================================================
        public async Task<JsonElement> ScrapeEventPageAsync(string eventUrl)
        {
            await EnsureBrowserReadyAsync();
            IPage? eventPage = null;

            try
            {
                // Mở tab mới độc lập để không làm hỏng session API của tab chính
                var context = _browser!.Contexts.First();
                eventPage = await context.NewPageAsync();

                // Chặn tải ảnh/video/css để tối đa tốc độ quét DOM
                await eventPage.RouteAsync("**/*", async route =>
                {
                    var type = route.Request.ResourceType;
                    if (type == "image" || type == "stylesheet" || type == "font" || type == "media" || type == "websocket")
                        await route.AbortAsync();
                    else
                        await route.ContinueAsync();
                });

                _logger.LogInformation("[TikiBrowserService] Trình duyệt ảo đang xâm nhập Landing Page: {Url}", eventUrl);

                // Mở trang sự kiện
                await eventPage.GotoAsync(eventUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

                // KỊCH BẢN THIỆN XẠ: Nhắm thẳng vào thuộc tính data-brick-id của Tiki
                await eventPage.EvaluateAsync(@"async () => {
                    let lastHeight = document.body.scrollHeight;
                    let retries = 0;
                    
                    for (let i = 0; i < 35; i++) {
                        // 1. Cuộn chuột xuống
                        window.scrollBy(0, 1000); 
                        await new Promise(resolve => setTimeout(resolve, 800)); 
                        
                        // 2. TÌM VÀ BẤM NÚT DỰA VÀO ATTRIBUTE ĐỘC NHẤT
                        let clicked = false;
                        
                        // Nhờ ảnh Inspect, ta bắt chính xác data-brick-id=""see_more_product_button""
                        const loadMoreBtns = document.querySelectorAll('[data-brick-id=""see_more_product_button""], [data-brick-label=""Xem thêm""]');
                        
                        for (const btn of loadMoreBtns) {
                            const rect = btn.getBoundingClientRect();
                            // Đảm bảo nút đang hiện trên màn hình
                            if (rect.width > 0 && rect.height > 0) {
                                try {
                                    // Cuộn nút ra giữa màn hình
                                    btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                                    
                                    // Kỹ thuật bấm nút bạo lực (bỏ qua các lớp overlay che khuất)
                                    btn.click(); 
                                    clicked = true;
                                } catch(e) {}
                            }
                        }
                        
                        // Nếu cách trên thất bại, dự phòng tìm button chứa text
                        if (!clicked) {
                            const allButtons = document.querySelectorAll('button');
                            for (const btn of allButtons) {
                                if ((btn.innerText || '').trim().toLowerCase() === 'xem thêm') {
                                    const rect = btn.getBoundingClientRect();
                                    if (rect.width > 0 && rect.height > 0) {
                                        btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                                        btn.click();
                                        clicked = true;
                                        break;
                                    }
                                }
                            }
                        }
                        
                        // Chờ Tiki load hàng
                        if (clicked) {
                            await new Promise(resolve => setTimeout(resolve, 1500)); 
                        }

                        // 3. Kiểm tra đáy
                        let newHeight = document.body.scrollHeight;
                        if (newHeight === lastHeight) {
                            retries++;
                            if (retries >= 3) break; 
                        } else {
                            retries = 0;
                            lastHeight = newHeight;
                        }
                    }
                }");

                // Script nhúng vào trình duyệt ảo: Quét toàn bộ thẻ HTML chứa link sản phẩm
                var jsScraper = @"
                    () => {
                        const items = [];
                        // Bắt tất cả các thẻ a có link chứa '-p' (Ký hiệu sản phẩm của Tiki)
                        const productLinks = document.querySelectorAll('a[href*=""-p""]');
                        
                        productLinks.forEach(link => {
                            try {
                                const url = link.href;
                                const idMatch = url.match(/-p(\d+)\.html/);
                                if (!idMatch) return;
                                
                                const id = parseInt(idMatch[1]);
                                
                                // Tránh thêm trùng lặp sản phẩm
                                if (items.find(x => x.id === id)) return;

                                const nameEl = link.querySelector('h3') || link.querySelector('[class*=""name""]');
                                const name = nameEl ? nameEl.innerText.trim() : 'Sản phẩm sự kiện';
                                
                                const imgEl = link.querySelector('img');
                                let imgUrl = '';
                                if (imgEl) {
                                    imgUrl = imgEl.getAttribute('data-src') || imgEl.src || imgEl.getAttribute('srcset')?.split(' ')[0] || '';
                                    if (imgUrl.startsWith('data:image')) imgUrl = ''; // Bỏ placeholder base64
                                }
                                
                                const priceEl = link.querySelector('[class*=""price""]');
                                let price = 0;
                                if (priceEl) {
                                    const priceText = priceEl.innerText.replace(/[^\d]/g, '');
                                    if (priceText) price = parseInt(priceText, 10);
                                }
                                
                                const originalPriceEl = link.querySelector('[class*=""original""]') || link.querySelector('del');
                                let originalPrice = price;
                                if (originalPriceEl) {
                                    const origText = originalPriceEl.innerText.replace(/[^\d]/g, '');
                                    if (origText) originalPrice = parseInt(origText, 10);
                                }
                                
                                if (price > 0) {
                                    items.push({
                                        id: id,
                                        name: name,
                                        price: price,
                                        original_price: originalPrice,
                                        thumbnail_url: imgUrl,
                                        url_path: url.replace('https://tiki.vn/', '')
                                    });
                                }
                            } catch(e) { }
                        });
                        
                        return { data: items }; 
                    }
                ";

                // Hứng kết quả DOM giả lập thành JsonElement
                var jsonResult = await eventPage.EvaluateAsync<JsonElement>(jsScraper);

                _logger.LogInformation("[TikiBrowserService] Quét DOM thành công, thu thập được danh sách mục tiêu.");
                return jsonResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TikiBrowserService] Bóc tách DOM thất bại: {Url}", eventUrl);
                // Tạo JSON rỗng an toàn để hệ thống không sập
                using var doc = JsonDocument.Parse("{ \"data\": [] }");
                return doc.RootElement.Clone();
            }
            finally
            {
                if (eventPage != null) await eventPage.CloseAsync(); // Đóng tab dọn dẹp RAM
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
        }
    }
}