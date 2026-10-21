using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Text.Json;

namespace ePriTrackerBackend.Scraping.Tiki
{
    public interface ITikiBrowserService
    {
        Task<JsonElement> FetchTikiApiAsync(string apiPath);
        Task<JsonElement> ScrapeEventPageAsync(string eventUrl); // Thêm mới vào Interface
    }

    public class TikiBrowserService : ITikiBrowserService, IAsyncDisposable
    {
        private readonly ILogger<TikiBrowserService> _logger;
        private readonly IScraperMetrics _metrics;
        private IPlaywright _playwright;
        private IBrowser _browser;
        private IBrowserContext _context;

        // Chỉ được GÁN bên trong _lock. Bên ngoài lock chỉ được ĐỌC vào biến local rồi dùng biến local.
        private volatile IPage _sharedPage;
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

        public TikiBrowserService(ILogger<TikiBrowserService> logger, IScraperMetrics metrics)
        {
            _logger = logger;
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        /// <summary>
        /// Trả về page dùng chung đã sẵn sàng (đã load Tiki, có cookie).
        /// Page chỉ được "công bố" ra field _sharedPage SAU KHI khởi tạo xong hoàn toàn,
        /// nên luồng khác không bao giờ thấy một page dở dang hoặc bị set null giữa chừng.
        /// </summary>
        private async Task<IPage> GetReadyPageAsync()
        {
            var page = _sharedPage;
            if (page != null && !page.IsClosed) return page;

            await _lock.WaitAsync();
            try
            {
                page = _sharedPage;
                if (page != null && !page.IsClosed) return page;

                _logger.LogInformation("[TikiBrowserService] Khởi tạo Playwright Singleton...");
                _playwright ??= await Playwright.CreateAsync();

                if (_browser == null || !_browser.IsConnected)
                {
                    if (_browser != null)
                    {
                        try { await _browser.CloseAsync(); } catch { /* browser đã chết */ }
                    }

                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
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
                    _context = null;
                }

                // Đóng context cũ (nếu có) để không rò rỉ RAM mỗi lần khởi tạo lại
                if (_context != null)
                {
                    try { await _context.CloseAsync(); } catch { /* bỏ qua */ }
                    _context = null;
                }

                var context = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                    Locale = "vi-VN",
                    TimezoneId = "Asia/Ho_Chi_Minh",
                    BypassCSP = true
                });

                await context.AddInitScriptAsync(StealthScript);

                // Dùng biến LOCAL trong suốt quá trình khởi tạo
                var newPage = await context.NewPageAsync();

                _logger.LogInformation("[TikiBrowserService] Đang lấy Session Tiki...");
                await newPage.GotoAsync(TikiBaseUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });

                await Task.Delay(2000);

                // Chỉ công bố khi đã sẵn sàng hoàn toàn
                _context = context;
                _sharedPage = newPage;

                _logger.LogInformation("[TikiBrowserService] Đã sẵn sàng thao tác API ngầm!");
                return newPage;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Huỷ page bị hỏng. Chỉ huỷ nếu field vẫn đang trỏ đúng page đó
        /// (tránh trường hợp luồng khác đã khởi tạo page mới rồi lại bị luồng này xoá mất).
        /// </summary>
        private async Task ResetPageAsync(IPage brokenPage)
        {
            await _lock.WaitAsync();
            try
            {
                if (!ReferenceEquals(_sharedPage, brokenPage)) return;
                _sharedPage = null;

                try { await brokenPage.CloseAsync(); }
                catch (Exception closeEx)
                {
                    _logger.LogDebug(closeEx, "[TikiBrowserService] Bỏ qua lỗi khi cố đóng page đã hỏng.");
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<JsonElement> FetchTikiApiAsync(string apiPath)
        {
            var page = await GetReadyPageAsync();

            try
            {
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

                return await page.EvaluateAsync<JsonElement>(jsCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TikiBrowserService] Lỗi khi fetch {Path}.", apiPath);

                // Chỉ reset session khi page thật sự hỏng hoặc bị Tiki chặn.
                // Lỗi 404/400 của MỘT sản phẩm không được phép giết page đang dùng chung cho cả batch.
                var msg = ex.Message ?? string.Empty;
                bool sessionBroken = page.IsClosed
                    || msg.Contains("HTTP 403")
                    || msg.Contains("HTTP 429")
                    || msg.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("has been closed", StringComparison.OrdinalIgnoreCase);

                if (sessionBroken)
                {
                    _logger.LogWarning("[TikiBrowserService] Session hỏng/bị chặn, sẽ khởi tạo lại ở lần gọi sau.");
                    await ResetPageAsync(page);
                }

                throw new Exception("Lấy dữ liệu Tiki thất bại.", ex);
            }
        }


        // ==============================================================
        // HÀM MỚI: QUÉT DOM BẰNG TRÌNH DUYỆT ẢO (DÀNH RIÊNG CHO SỰ KIỆN)
        // ==============================================================
        public async Task<JsonElement> ScrapeEventPageAsync(string eventUrl)
        {
            var sharedPage = await GetReadyPageAsync();
            IPage eventPage = null;

            try
            {
                // Mở tab mới độc lập (cùng context/cookie) để không làm hỏng session API của tab chính
                eventPage = await sharedPage.Context.NewPageAsync();


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
                _metrics.RecordSuccess(FetchStrategyNames.Playwright);
                return jsonResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TikiBrowserService] Bóc tách DOM thất bại: {Url}", eventUrl);
                _metrics.RecordFailure();
                // Tạo JSON rỗng an toàn để hệ thống không sập
                using var doc = JsonDocument.Parse("{ \"data\": [] }");
                return doc.RootElement.Clone();
            }
            finally
            {
                if (eventPage != null)
                {
                    try { await eventPage.CloseAsync(); } catch { /* page đã đóng */ } // Đóng tab dọn dẹp RAM
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_context != null) await _context.CloseAsync(); } catch { }
            try { if (_browser != null) await _browser.CloseAsync(); } catch { }
            _playwright?.Dispose();
            _lock.Dispose();
        }
    }
}
