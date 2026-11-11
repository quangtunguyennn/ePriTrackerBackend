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

        // Cuộn + bấm "Xem thêm" để trang nạp thêm sản phẩm. Danh sách Tiki nạp chậm và thất thường (cùng trang, hai lần cào
        // từng ra 387 và 40 sản phẩm khi dừng ngay lúc chiều cao trang chưa đổi) → chờ sản phẩm đầu tiên xuất hiện (~6 giây),
        // dừng khi SỐ SẢN PHẨM không tăng qua 2 nhịp liên tiếp, tối đa 8 nhịp.
        // Chỉ bấm <button> "Xem thêm" (nạp thêm tại chỗ), không bấm link cùng nhãn – link dẫn sang trang khác làm hỏng lượt đọc.
        private const string ScrollAndExpandScript = @"async () => {
            const sleep = (ms) => new Promise(resolve => setTimeout(resolve, ms));
            const count = () => document.querySelectorAll('[data-view-id=""product_list_item""], a.product-item, div.product-item, a[href*=""-p""]').length;

            for (let w = 0; w < 20 && count() === 0; w++) await sleep(300);

            let lastCount = count(), idle = 0;
            for (let i = 0; i < 8 && idle < 2; i++) {
                window.scrollBy(0, 1500);
                await sleep(700);

                let clicked = false;
                const loadMoreBtns = document.querySelectorAll('button[data-brick-id=""see_more_product_button""], button[data-brick-label=""Xem thêm""]');
                for (const btn of loadMoreBtns) {
                    const rect = btn.getBoundingClientRect();
                    if (rect.width > 0 && rect.height > 0) {
                        try { btn.scrollIntoView({ block: 'center' }); btn.click(); clicked = true; } catch (e) {}
                    }
                }
                if (clicked) await sleep(1200);

                const c = count();
                if (c > lastCount) { lastCount = c; idle = 0; } else { idle++; }
            }
        }";

        /// <summary>
        /// Đọc sản phẩm trên trang sự kiện → JSON dạng API { data: [{ id, name, price, original_price, thumbnail_url, url_path }] }.
        /// 1) Thẻ sản phẩm kiểu cũ (data-view-id="product_list_item", .product-item) – trang /khuyen-mai/ hiện tại.
        /// 2) Không thấy thẻ nào → dự phòng: mọi link tới trang sản phẩm Tiki (…-p123) có chữ giá "₫"
        ///    (trang /bo-suu-tap/ dùng thẻ &lt;a class="sc-…"&gt; sinh tự động, link không có ".html").
        /// Public để test chạy script trên HTML mẫu bằng trình duyệt thật.
        /// </summary>
        public const string EventProductsScript = @"
            () => {
                const items = [];
                const toAbs = (u) => u.startsWith('//') ? 'https:' + u : (u.startsWith('/') ? 'https://tiki.vn' + u : u);
                const parsePrice = (t) => parseInt((t || '').replace(/[^\d]/g, ''), 10) || 0;
                const isStrike = (el) => { try { return getComputedStyle(el).textDecorationLine.includes('line-through'); } catch (e) { return false; } };
                const idFromUrl = (u) => { const m = (u || '').match(/-p(\d+)(?:\.html)?(?:[?#/]|$)/); return m ? parseInt(m[1], 10) : 0; };
                // url_path luôn có đuôi .html, bỏ query – để TikiUrlParser nhận ra mã sản phẩm
                const toPath = (u) => { let p = u.split('?')[0].split('#')[0].replace('https://tiki.vn/', ''); if (!p.endsWith('.html')) p += '.html'; return p; };
                // Ảnh sản phẩm: thẻ Top 10 có ảnh huy chương xếp hạng (alt=""sale_rank"", /ts/upload/) đứng TRƯỚC ảnh sản phẩm
                // → ưu tiên ảnh /product/, bỏ huy chương/nhãn; không có thì lấy ảnh đầu tiên không phải nhãn
                const badgeAlts = ['sale_rank', 'product_image_badge', 'badge', 'icon'];
                const srcOf = (el) => ((el.tagName === 'SOURCE' ? el.getAttribute('srcset') : (el.getAttribute('data-src') || el.getAttribute('src') || el.getAttribute('srcset'))) || '').split(' ')[0];
                const imageOf = (node) => {
                    const candidates = Array.from(node.querySelectorAll('picture source, img'))
                        .filter(el => !badgeAlts.includes((el.getAttribute('alt') || '').toLowerCase()))
                        .map(srcOf)
                        .filter(src => src && !src.startsWith('data:image'));
                    return candidates.find(src => src.includes('/product/')) || candidates.find(src => !src.includes('/ts/upload/')) || candidates[0] || '';
                };

                // 1) THẺ SẢN PHẨM KIỂU CŨ
                document.querySelectorAll('[data-view-id=""product_list_item""], a.product-item, div.product-item').forEach(node => {
                    try {
                        const linkEl = node.tagName === 'A' ? node : node.querySelector('a');
                        if (!linkEl) return;
                        let url = linkEl.getAttribute('href') || '';
                        if (!url) return;
                        url = toAbs(url);

                        let id = 0;
                        const trackingData = node.getAttribute('data-view-content') || linkEl.getAttribute('data-view-content');
                        if (trackingData) { try { id = JSON.parse(trackingData).click_data.id; } catch (e) {} }
                        if (!id) id = idFromUrl(url);
                        if (!id || items.find(x => x.id === id)) return;

                        let name = '';
                        const h3El = node.querySelector('h3');
                        if (h3El) name = h3El.textContent.trim();
                        const imgEl = node.querySelector('img');
                        if (!name && imgEl) name = imgEl.getAttribute('alt') || '';
                        if (!name || name === 'product_image_badge') {
                            const lines = node.innerText.split('\n').map(x => x.trim()).filter(x => x !== '');
                            if (lines.length > 0) name = lines[0];
                        }

                        let price = 0, originalPrice = 0;
                        const priceEl = node.querySelector('.price-discount__price') || node.querySelector('[class*=""price""]:not([class*=""original""])');
                        if (priceEl && priceEl.textContent.includes('₫')) price = parsePrice(priceEl.textContent);
                        const origPriceEl = node.querySelector('.price-discount__original-price') || node.querySelector('[class*=""original""]');
                        if (origPriceEl && origPriceEl.textContent.includes('₫')) originalPrice = parsePrice(origPriceEl.textContent);
                        if (price === 0) {
                            const all = Array.from(node.querySelectorAll('*'))
                                .filter(el => el.children.length <= 1 && el.textContent.includes('₫'))
                                .map(el => parsePrice(el.textContent)).filter(v => v > 0);
                            if (all.length > 0) { price = all[0]; originalPrice = all.length > 1 ? all[1] : price; }
                        }
                        if (originalPrice === 0) originalPrice = price;

                        // Sản phẩm quảng cáo (Tiki Ads) có link theo dõi //tka.tiki.vn/pixel/... thay vì link sản phẩm → dựng lại từ mã
                        const urlPath = idFromUrl(url) === id ? toPath(url) : 'product-p' + id + '.html';
                        if (id > 0 && name) items.push({ id, name, price, original_price: originalPrice, thumbnail_url: imageOf(node), url_path: urlPath });
                    } catch (e) { }
                });

                // 2) DỰ PHÒNG: link sản phẩm có giá
                if (items.length === 0) {
                    document.querySelectorAll('a[href]').forEach(a => {
                        try {
                            const url = toAbs(a.getAttribute('href') || '');
                            const id = idFromUrl(url);
                            if (!id || items.find(x => x.id === id) || !a.innerText.includes('₫')) return;

                            // Giá: phần tử lá có '₫'; chữ gạch ngang = giá gốc
                            const priceEls = Array.from(a.querySelectorAll('*')).filter(el => el.children.length === 0 && el.textContent.includes('₫'));
                            const current = priceEls.filter(el => !isStrike(el)).map(el => parsePrice(el.textContent)).filter(v => v > 0);
                            const struck = priceEls.filter(isStrike).map(el => parsePrice(el.textContent)).filter(v => v > 0);
                            const price = current[0] || struck[0] || 0;
                            if (!price) return;
                            const originalPrice = struck.find(v => v > price) || price;

                            // Tên: h3 / title / alt ảnh / dòng chữ đầu không phải giá, % giảm, nhãn ngắn
                            let name = (a.querySelector('h3')?.textContent || a.getAttribute('title') || '').trim();
                            if (!name) {
                                const lines = a.innerText.split('\n').map(x => x.trim())
                                    .filter(x => x.length >= 6 && !x.includes('₫') && !/^-?\d+%$/.test(x));
                                name = lines[0] || a.querySelector('img')?.getAttribute('alt') || '';
                            }
                            if (!name) return;

                            items.push({ id, name, price, original_price: originalPrice, thumbnail_url: imageOf(a), url_path: toPath(url) });
                        } catch (e) { }
                    });
                }

                return { data: items };
            }
        ";

        private static bool IsNavigationError(PlaywrightException ex)
            => ex.Message.Contains("Execution context was destroyed", StringComparison.OrdinalIgnoreCase)
               || ex.Message.Contains("navigation", StringComparison.OrdinalIgnoreCase);

        private static async Task WaitForPageSettledAsync(IPage page)
        {
            try { await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 10000 }); }
            catch (Exception ex) when (ex is PlaywrightException or System.TimeoutException) { /* đọc được gì thì đọc */ }
            await page.WaitForTimeoutAsync(1500); // để trang render xong danh sách sau khi tải lại
        }


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

                // Cuộn + bấm "Xem thêm" để nạp thêm sản phẩm. Một số trang tự tải lại / chuyển trang giữa chừng
                // ("Execution context was destroyed") → chờ trang ổn định rồi vẫn đọc sản phẩm đang có, không bỏ cả lượt.
                try
                {
                    await eventPage.EvaluateAsync(ScrollAndExpandScript);
                }
                catch (PlaywrightException ex) when (IsNavigationError(ex))
                {
                    _logger.LogWarning("[TikiBrowserService] Trang tự chuyển/tải lại trong lúc cuộn, đọc sản phẩm hiện có: {Url}", eventUrl);
                    await WaitForPageSettledAsync(eventPage);
                }

                JsonElement jsonResult;
                try
                {
                    jsonResult = await eventPage.EvaluateAsync<JsonElement>(EventProductsScript);
                }
                catch (PlaywrightException ex) when (IsNavigationError(ex))
                {
                    await WaitForPageSettledAsync(eventPage);
                    jsonResult = await eventPage.EvaluateAsync<JsonElement>(EventProductsScript);
                }

                int count = jsonResult.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 0;
                _logger.LogInformation("[TikiBrowserService] Quét DOM xong: {Count} sản phẩm ({Url})", count, eventUrl);
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
