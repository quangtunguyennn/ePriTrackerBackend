using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Sự kiện CellphoneS TỰ ĐĂNG:
    /// - Flash sale: qua GraphQL (<see cref="ICellphoneSApi"/>) – ~1,5 giây, có giờ bắt đầu/kết thúc chính xác, số suất.
    /// - "Deal sốc mỗi ngày": danh sách do website TỰ TÍNH từ nhiều danh mục (không phải chiến dịch có API) → đọc từ trang chủ
    ///   đã render (<see cref="IPageRenderer"/>), thưa (mỗi <see cref="HomeRenderInterval"/>), giữa các lần dùng kết quả cũ.
    /// - API flash sale lỗi → render trang chủ ngay và lấy flash sale từ đó (bộ đọc HTML <see cref="CellphoneSHomeEventParser"/>).
    /// - Hot sale (tab "BẮT NHỊP SĂN DEAL", "HOT SALE CUỐI TUẦN – Phụ kiện, PC"...): mỗi nhóm trong cấu hình trang chủ là một sự kiện,
    ///   qua GraphQL sale_off_group (giá hot sale + giờ kết thúc chính xác); API lỗi một lượt → dùng lại kết quả lần trước còn hạn
    ///   (không thì job sẽ xoá nhầm sự kiện có giờ chính xác vì "sàn không còn trả về").
    /// Ưu đãi thẻ ngân hàng (/uu-dai-doi-tac) không lấy: giá trên trang không giảm.
    /// </summary>
    public sealed class CellphoneSEventSource : IAutoEventSource
    {
        /// <summary>Render trang chủ (Playwright, ~10–20 giây) tối đa mỗi khoảng này khi API flash sale vẫn chạy tốt.</summary>
        public static readonly TimeSpan HomeRenderInterval = TimeSpan.FromHours(2);

        /// <summary>Kết quả render cũ quá hạn này thì thôi dùng (sự kiện cuốn chiếu sẽ tự hết hạn).</summary>
        public static readonly TimeSpan HomeEventsMaxAge = TimeSpan.FromHours(6);

        private readonly ICellphoneSApi _api;
        private readonly IPageRenderer _renderer;
        private readonly ILogger<CellphoneSEventSource> _logger;
        private readonly TimeProvider _time;

        // Singleton, chỉ AutoEventSyncService gọi (đã khoá tuần tự) → không cần lock
        private IReadOnlyList<ScrapedEvent> _homeEvents = Array.Empty<ScrapedEvent>();
        private DateTimeOffset _lastRender = DateTimeOffset.MinValue;        // lần render THÀNH CÔNG gần nhất (tuổi của deal)
        private DateTimeOffset _lastRenderAttempt = DateTimeOffset.MinValue; // render lỗi cũng chờ đủ khoảng mới thử lại
        private IReadOnlyList<ScrapedEvent> _lastHotSales = Array.Empty<ScrapedEvent>(); // dự phòng khi API hot sale lỗi

        public CellphoneSEventSource(ICellphoneSApi api, IPageRenderer renderer, ILogger<CellphoneSEventSource> logger, TimeProvider timeProvider = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = timeProvider ?? TimeProvider.System;
        }

        public string Platform => CellphoneSScraper.PlatformName;

        public async Task<IReadOnlyList<ScrapedEvent>> GetLiveEventsAsync(CancellationToken cancellationToken = default)
        {
            var now = _time.GetUtcNow();

            IReadOnlyList<ScrapedEvent> flashSales = null;
            Exception apiError = null;
            try
            {
                flashSales = await GetFlashSalesFromApiAsync(now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                apiError = ex;
                _logger.LogWarning(ex, "⚠️ API flash sale CellphoneS lỗi – chuyển sang đọc trang chủ.");
            }

            IReadOnlyList<ScrapedEvent> homeFlashSales = null;
            if (apiError != null || now - _lastRenderAttempt >= HomeRenderInterval)
            {
                _lastRenderAttempt = now;
                try
                {
                    string html = await _renderer.RenderAsync(CellphoneSHomeEventParser.HomeUrl, cancellationToken);
                    var parsed = CellphoneSHomeEventParser.Parse(html, now);

                    // Khối có giờ kết thúc = flash sale (chỉ dùng khi API lỗi); không có = deal cuốn chiếu
                    homeFlashSales = parsed.Where(e => e.EndsAt != null).ToList();
                    _homeEvents = parsed.Where(e => e.EndsAt == null).ToList();
                    _lastRender = now;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && apiError == null)
                {
                    // API vẫn tốt → trả flash sale; deal dùng kết quả cũ (nếu chưa quá hạn)
                    _logger.LogWarning(ex, "⚠️ Không đọc được trang chủ CellphoneS, giữ deal của lần trước.");
                }
                // Cả API lẫn trang chủ đều lỗi → ném (nơi gọi giữ nguyên dữ liệu cũ)
            }

            var deals = now - _lastRender < HomeEventsMaxAge ? _homeEvents : Array.Empty<ScrapedEvent>();
            var hotSales = await GetHotSalesAsync(now, cancellationToken);
            return (flashSales ?? homeFlashSales ?? Array.Empty<ScrapedEvent>()).Concat(hotSales).Concat(deals).ToList();
        }

        private async Task<IReadOnlyList<ScrapedEvent>> GetHotSalesAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            try
            {
                _lastHotSales = await GetHotSalesFromApiAsync(now, cancellationToken);
                return _lastHotSales;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "⚠️ API hot sale CellphoneS lỗi – dùng lại kết quả lần trước.");
                return _lastHotSales.Where(e => e.EndsAt > now).ToList();
            }
        }

        /// <summary>
        /// Cấu hình trang chủ → các nhóm hot sale → nhóm ĐANG chạy (from_date ≤ bây giờ &lt; to_date) → sản phẩm (tên, ảnh, link, giá gốc
        /// tra theo lô) với giá hot sale của nhóm.
        /// </summary>
        private async Task<IReadOnlyList<ScrapedEvent>> GetHotSalesFromApiAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var events = new List<ScrapedEvent>();

            foreach (var groupRef in await _api.GetHomeHotSaleGroupsAsync(cancellationToken))
            {
                string url = $"{CellphoneSHomeEventParser.HomeUrl}#hotsale-{groupRef.GroupUd}";
                try
                {
                    var evt = await GetHotSaleEventAsync(groupRef, url, now, cancellationToken);
                    if (evt != null) events.Add(evt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Một nhóm lỗi không kéo theo các nhóm khác; nhóm này giữ kết quả lần trước còn hạn
                    _logger.LogWarning("⚠️ Không đọc được hot sale {Group}: {Message}", groupRef.GroupUd, ex.Message);
                    var previous = _lastHotSales.FirstOrDefault(e => e.Url == url && e.EndsAt > now);
                    if (previous != null) events.Add(previous);
                }
            }

            return events;
        }

        private async Task<ScrapedEvent> GetHotSaleEventAsync(CellphoneSHotSaleGroupRef groupRef, string url, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var group = await _api.GetSaleOffGroupAsync(groupRef.GroupUd, cancellationToken);
            if (group?.ToDate == null || now >= group.ToDate || (group.FromDate != null && group.FromDate > now) || group.Items.Count == 0) return null;

            var catalog = (await _api.GetProductsByIdsAsync(group.Items.Select(i => i.ProductId).ToList(), cancellationToken))
                .GroupBy(p => p.ProductId)
                .ToDictionary(g => g.Key, g => g.First());

            var products = group.Items
                .Select(i => catalog.TryGetValue(i.ProductId, out var c) ? ToScrapedProduct(i, c) : null)
                .Where(p => p != null)
                .ToList();

            return new ScrapedEvent
            {
                Title = groupRef.Title,
                Url = url,
                ImageUrl = products.FirstOrDefault()?.ImageUrl,
                EndsAt = group.ToDate,
                Products = products
            };
        }

        private static ScrapedProduct ToScrapedProduct(CellphoneSSaleOffItem item, CellphoneSApiProduct catalog)
        {
            var product = CellphoneSScraper.ToScrapedProduct(catalog);
            if (product == null) return null;

            decimal price = item.SalePrice > 0 ? item.SalePrice : product.Price ?? 0;
            decimal original = Math.Max(product.OriginalPrice ?? 0, product.Price ?? 0); // giá niêm yết / giá bán thường

            return new ScrapedProduct
            {
                ExternalId = product.ExternalId,
                Name = product.Name,
                Price = price > 0 ? price : null,
                OriginalPrice = original > price ? original : null,
                ImageUrl = product.ImageUrl,
                Url = product.Url,
                Description = item.Note
            };
        }

        /// <summary>
        /// Cấu hình trang chủ → nhóm flash sale → khung ĐANG chạy → sản phẩm + giá → link (tra theo lô).
        /// Trước giờ mở bán CellphoneS giấu giá flash (=0) → dùng giá đặc biệt; khung chưa chạy thì chưa đăng.
        /// </summary>
        private async Task<IReadOnlyList<ScrapedEvent>> GetFlashSalesFromApiAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            var events = new List<ScrapedEvent>();

            foreach (string groupUd in await _api.GetHomeFlashSaleGroupUdsAsync(cancellationToken))
            {
                var group = await _api.GetFlashSaleGroupAsync(groupUd, cancellationToken);
                if (group == null) continue;

                foreach (var program in group.Programs.Where(p => p.IsActive && p.StartsAt <= now && now < p.EndsAt))
                {
                    var slots = (await _api.GetFlashSaleSlotsAsync(program.Id, cancellationToken)).Where(s => s.IsActive).ToList();
                    if (slots.Count == 0) continue;

                    var catalog = (await _api.GetProductsByIdsAsync(slots.Select(s => s.ProductId).ToList(), cancellationToken))
                        .GroupBy(p => p.ProductId)
                        .ToDictionary(g => g.Key, g => g.First());

                    var products = slots
                        .Select(s => catalog.TryGetValue(s.ProductId, out var c) ? ToScrapedProduct(s, c) : null)
                        .Where(p => p != null)
                        .ToList();

                    events.Add(new ScrapedEvent
                    {
                        ExternalId = program.Id,
                        Title = $"{group.Title ?? "Flash sale"} ({program.Title})",
                        Url = $"{CellphoneSHomeEventParser.HomeUrl}#flashsale-{program.Id}",
                        ImageUrl = products.FirstOrDefault()?.ImageUrl,
                        EndsAt = program.EndsAt,
                        Products = products
                    });
                }
            }

            return events;
        }

        private static ScrapedProduct ToScrapedProduct(CellphoneSFlashSaleSlot slot, CellphoneSApiProduct catalog)
        {
            var product = CellphoneSScraper.ToScrapedProduct(catalog);
            if (product == null) return null;

            decimal price = slot.FlashPrice > 0 ? slot.FlashPrice
                : slot.SpecialPrice > 0 ? slot.SpecialPrice
                : product.Price ?? 0;
            decimal original = slot.NormalPrice > 0 ? slot.NormalPrice : product.OriginalPrice ?? 0;

            return new ScrapedProduct
            {
                ExternalId = product.ExternalId,
                Name = string.IsNullOrWhiteSpace(slot.Name) ? product.Name : slot.Name,
                Price = price > 0 ? price : null,
                OriginalPrice = original > price ? original : null,
                ImageUrl = CellphoneSApiParser.ImageUrl(slot.Thumbnail) ?? product.ImageUrl,
                Url = product.Url,
                Description = slot.MaxSlot > 0 ? $"Đã bán {slot.Sold}/{slot.MaxSlot} suất" : null
            };
        }
    }
}
