using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Hacom
{
    /// <summary>
    /// Sự kiện HACOM TỰ ĐĂNG (như CellphoneS, GearVN): trang https://hacom.vn/deal (vd "Flash Sale Cuối Tuần") – mỗi tên chương trình
    /// (dealTitle) đang chạy là một sự kiện, có giờ bắt đầu/kết thúc chính xác (fromDate/toDate).
    /// - Chính: gọi thẳng API nội bộ của trang (<see cref="DealApiUrl"/>, &lt;1 giây). LƯU Ý: robots.txt của apis-web.hacom.vn ghi
    ///   "Disallow: /" – nhóm đã chọn vẫn gọi, tần suất thấp (job 15 phút/lần, kết quả nhớ <see cref="ResultLifetime"/>).
    /// - Dự phòng (API lỗi / bị chặn): mở trang /deal bằng trình duyệt (trang này robots.txt của hacom.vn cho phép) và đọc chính
    ///   phản hồi API mà trang tự tải (<see cref="IPageResponseCapture"/>, ~5–10 giây).
    /// - Cả hai lỗi → ném (job giữ nguyên sự kiện cũ, không xoá). API trả danh sách rỗng = hiện không có deal (không dùng dự phòng).
    /// </summary>
    public sealed class HacomEventSource : IAutoEventSource
    {
        public const string HttpClientName = "HACOM.DealApi";
        public const string DealApiUrl = "https://apis-web.hacom.vn/api-client/api/v1/TblProductDeal/get-list";
        public const string DealPageUrl = "https://hacom.vn/deal";
        private const string DealApiPath = "TblProductDeal/get-list";

        public const int MaxProductsPerEvent = 40;
        public static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PageCaptureTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IPageResponseCapture _pageCapture;
        private readonly ILogger<HacomEventSource> _logger;
        private readonly TimeProvider _time;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private (IReadOnlyList<ScrapedEvent> Events, DateTimeOffset ReadAt)? _lastResult;

        public HacomEventSource(IHttpClientFactory httpClientFactory, IPageResponseCapture pageCapture, ILogger<HacomEventSource> logger, TimeProvider timeProvider = null)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _pageCapture = pageCapture ?? throw new ArgumentNullException(nameof(pageCapture));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = timeProvider ?? TimeProvider.System;
        }

        public string Platform => HacomScraper.PlatformName;

        public async Task<IReadOnlyList<ScrapedEvent>> GetLiveEventsAsync(CancellationToken cancellationToken = default)
        {
            if (TryGetRecentResult(out var recent)) return recent;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (TryGetRecentResult(out recent)) return recent;

                var deals = await ReadDealsAsync(cancellationToken);
                var events = ToEvents(deals, _time.GetUtcNow());
                _lastResult = (events, _time.GetUtcNow());
                return events;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool TryGetRecentResult(out IReadOnlyList<ScrapedEvent> events)
        {
            var last = _lastResult;
            events = last?.Events;
            return last != null && _time.GetUtcNow() - last.Value.ReadAt < ResultLifetime;
        }

        private async Task<IReadOnlyList<HacomDeal>> ReadDealsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.GetAsync(DealApiUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                return HacomDealParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("⚠️ Gọi API deal HACOM lỗi ({Message}) – mở trang /deal bằng trình duyệt.", ex.Message);
            }

            // Dự phòng: lỗi ở đây thì ném lên – nơi gọi giữ nguyên dữ liệu cũ
            string json = await _pageCapture.CaptureResponseAsync(DealPageUrl, DealApiPath, PageCaptureTimeout, cancellationToken);
            return HacomDealParser.Parse(json);
        }

        /// <summary>Gom theo chương trình đang chạy; sản phẩm còn hiệu lực, có giá và link hợp lệ, theo thứ tự HACOM sắp.</summary>
        public static IReadOnlyList<ScrapedEvent> ToEvents(IReadOnlyList<HacomDeal> deals, DateTimeOffset now)
        {
            var events = new List<ScrapedEvent>();
            var running = deals
                .Where(d => d.IsActive && !string.IsNullOrWhiteSpace(d.Title) && d.ToDate > now && (d.FromDate == null || d.FromDate <= now))
                .GroupBy(d => (d.Title, d.ToDate));

            foreach (var program in running)
            {
                var products = program.OrderBy(d => d.Order)
                    .Select(ToScrapedProduct)
                    .Where(p => p != null)
                    .GroupBy(p => p.Url).Select(x => x.First())
                    .Take(MaxProductsPerEvent)
                    .ToList();

                events.Add(new ScrapedEvent
                {
                    Title = program.Key.Title,
                    Url = $"{DealPageUrl}#{Uri.EscapeDataString(program.Key.Title)}",
                    ImageUrl = products.FirstOrDefault()?.ImageUrl, // không có banner → FE ghép lưới ảnh sản phẩm
                    EndsAt = program.Key.ToDate,
                    Products = products
                });
            }
            return events;
        }

        private static ScrapedProduct ToScrapedProduct(HacomDeal deal)
        {
            if (deal.DealPrice <= 0 || string.IsNullOrWhiteSpace(deal.Slug)) return null;
            string url = HacomUrlParser.NormalizeProductUrl(deal.Slug.ToLowerInvariant());
            if (!HacomUrlParser.TryGetSlug(url, out var slug)) return null;

            decimal original = Math.Max(deal.MarketPrice, deal.RegularPrice);
            return new ScrapedProduct
            {
                ExternalId = slug,
                Name = deal.Name,
                Price = deal.DealPrice,
                OriginalPrice = original > deal.DealPrice ? original : null,
                ImageUrl = deal.ImageUrl,
                Url = url
            };
        }
    }
}
