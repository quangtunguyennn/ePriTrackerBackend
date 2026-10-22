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
            return (flashSales ?? homeFlashSales ?? Array.Empty<ScrapedEvent>()).Concat(deals).ToList();
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
