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

                // KỊCH BẢN THIỆN XẠ: Rút ngắn thời gian quét mẫu để Frontend không bị Timeout
                await eventPage.EvaluateAsync(@"async () => {
            let lastHeight = document.body.scrollHeight;
            
            // CHỈ CUỘN 5 LẦN ĐỂ PREVIEW NHANH (~5-7 giây)
            // Nếu muốn cào sâu hơn, hãy để Hangfire gọi riêng một hàm khác với số vòng lặp cao hơn
            for (let i = 0; i < 5; i++) {
                window.scrollBy(0, 1500); 
                await new Promise(resolve => setTimeout(resolve, 600)); 
                
                let clicked = false;
                const loadMoreBtns = document.querySelectorAll('[data-brick-id=""see_more_product_button""], [data-brick-label=""Xem thêm""]');
                
                for (const btn of loadMoreBtns) {
                    const rect = btn.getBoundingClientRect();
                    if (rect.width > 0 && rect.height > 0) {
                        try {
                            btn.scrollIntoView({ behavior: 'smooth', block: 'center' });
                            btn.click(); 
                            clicked = true;
                        } catch(e) {}
                    }
                }
                
                if (clicked) {
                    await new Promise(resolve => setTimeout(resolve, 1000)); 
                }

                let newHeight = document.body.scrollHeight;
                if (newHeight === lastHeight) break; 
                lastHeight = newHeight;
            }
        }");

                // KỊCH BẢN MỚI: BÓC TÁCH CHUẨN XÁC DỰA TRÊN HTML THỰC TẾ
                var jsScraper = @"
            () => {
                const items = [];
                // Bắt bao quát hơn: Theo id hoặc class chứa chữ product-item
                const productNodes = document.querySelectorAll('[data-view-id=""product_list_item""], a.product-item, div.product-item');
                
                productNodes.forEach(node => {
                    try {
                        const linkEl = node.tagName === 'A' ? node : node.querySelector('a');
                        if (!linkEl) return;
                        
                        let url = linkEl.getAttribute('href') || '';
                        if (!url) return;
                        if (url.startsWith('/')) url = 'https://tiki.vn' + url;

                        let id = 0;
                        const trackingData = node.getAttribute('data-view-content') || linkEl.getAttribute('data-view-content');
                        if (trackingData) {
                            try { id = JSON.parse(trackingData).click_data.id; } catch(e) {}
                        }
                        
                        if (id === 0) {
                            const idMatch = url.match(/-p(\d+)\.html/);
                            if (idMatch) id = parseInt(idMatch[1]);
                        }
                        
                        if (id === 0 || items.find(x => x.id === id)) return;

                        // 1. LẤY TÊN (Dùng thẻ H3 hoặc alt của ảnh)
                        let name = '';
                        const h3El = node.querySelector('h3');
                        if (h3El) name = h3El.textContent.trim();
                        
                        const imgEl = node.querySelector('img');
                        if (!name && imgEl) name = imgEl.getAttribute('alt') || '';
                        
                        if (!name || name === 'product_image_badge') {
                            const textLines = node.innerText.split('\n').map(x => x.trim()).filter(x => x !== '');
                            if (textLines.length > 0) name = textLines[0];
                        }

                        // 2. LẤY ẢNH
                        let imgUrl = '';
                        const sourceEl = node.querySelector('picture source');
                        if (sourceEl) imgUrl = sourceEl.getAttribute('srcset') || '';
                        if (!imgUrl && imgEl) imgUrl = imgEl.getAttribute('data-src') || imgEl.src || imgEl.getAttribute('srcset') || '';
                        
                        imgUrl = imgUrl.split(' ')[0]; 
                        if (imgUrl.startsWith('data:image')) imgUrl = '';

                        // 3. LẤY GIÁ BÁN VÀ GIÁ GỐC (Dò tìm thông minh)
                        let price = 0;
                        let originalPrice = 0;

                        // Ưu tiên 1: Lấy theo class thực tế của Tiki
                        const priceEl = node.querySelector('.price-discount__price') || node.querySelector('[class*=""price""]:not([class*=""original""])');
                        if (priceEl && priceEl.textContent.includes('₫')) {
                            price = parseInt(priceEl.textContent.replace(/[^\d]/g, ''), 10) || 0;
                        }

                        const origPriceEl = node.querySelector('.price-discount__original-price') || node.querySelector('[class*=""original""]');
                        if (origPriceEl && origPriceEl.textContent.includes('₫')) {
                            originalPrice = parseInt(origPriceEl.textContent.replace(/[^\d]/g, ''), 10) || 0;
                        }

                        // Ưu tiên 2: Vét cạn (Nếu Tiki đổi class, tìm thẻ bất kỳ chứa chữ ₫)
                        if (price === 0) {
                            const allNodesWithPrice = Array.from(node.querySelectorAll('*'))
                                .filter(el => el.children.length <= 1 && el.textContent.includes('₫'))
                                .map(el => parseInt(el.textContent.replace(/[^\d]/g, ''), 10))
                                .filter(val => !isNaN(val) && val > 0);
                                
                            if (allNodesWithPrice.length > 0) {
                                price = allNodesWithPrice[0];
                                originalPrice = allNodesWithPrice.length > 1 ? allNodesWithPrice[1] : price;
                            }
                        }

                        if (originalPrice === 0) originalPrice = price;

                        // Chỉ add vào list khi có tên và id hợp lệ
                        if (id > 0 && name) {
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