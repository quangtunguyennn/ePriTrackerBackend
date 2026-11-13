using System.Collections.Concurrent;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Gearvn
{
    /// <summary>
    /// Sự kiện GearVN TỰ ĐĂNG (như CellphoneS, không qua Admin): mỗi chương trình đang chạy trên https://gearvn.com/khuyen-mai
    /// là một sự kiện, có giờ bắt đầu/kết thúc chính xác (startAt/endAt) → hết giờ hoặc GearVN gỡ thì job đồng bộ tự xoá.
    /// - Danh sách: <see cref="MaxListPages"/> trang đầu (~25 chương trình), đọc payload Next.js bằng <see cref="GearvnPromotionParser"/>.
    /// - Sản phẩm: chỉ sản phẩm chính (role "condition"), bỏ quà tặng/mua kèm; payload không có giá/ảnh → đọc trang sản phẩm
    ///   (JSON-LD, <see cref="GearvnScraper"/>), tối đa <see cref="MaxProductsPerEvent"/> sản phẩm còn hàng mỗi chương trình,
    ///   nhớ <see cref="ProductDetailsLifetime"/> (nhiều chương trình dùng chung sản phẩm, job chạy 15 phút/lần).
    /// - Cả lượt đọc được nhớ <see cref="ResultLifetime"/> và chỉ một lượt chạy tại một thời điểm (job + người dùng mở sự kiện lúc cache trống).
    /// Đọc trang danh sách đầu tiên lỗi / không thấy chương trình nào → ném lỗi (job giữ nguyên sự kiện cũ, không xoá).
    /// </summary>
    public sealed class GearvnEventSource : IAutoEventSource
    {
        public const int MaxListPages = 3;
        public const int MaxProductsPerEvent = 6;
        private const int MaxProductCandidatesPerEvent = 10; // thử thêm vài sản phẩm phòng khi có cái hết hàng / lỗi
        private const int MaxOfferLength = 90;

        public static readonly TimeSpan ProductDetailsLifetime = TimeSpan.FromHours(6);
        public static readonly TimeSpan ResultLifetime = TimeSpan.FromMinutes(10);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IPlatformScraper _productScraper;
        private readonly ILogger<GearvnEventSource> _logger;
        private readonly TimeProvider _time;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ConcurrentDictionary<string, (ScrapedProduct Product, DateTimeOffset FetchedAt)> _productDetails = new(StringComparer.OrdinalIgnoreCase);
        private (IReadOnlyList<ScrapedEvent> Events, DateTimeOffset ReadAt)? _lastResult;

        public GearvnEventSource(IHttpClientFactory httpClientFactory, GearvnScraper productScraper, ILogger<GearvnEventSource> logger, TimeProvider timeProvider = null)
            : this(httpClientFactory, (IPlatformScraper)productScraper, logger, timeProvider)
        {
        }

        /// <summary>Cho test: thay trang sản phẩm bằng scraper giả.</summary>
        public GearvnEventSource(IHttpClientFactory httpClientFactory, IPlatformScraper productScraper, ILogger<GearvnEventSource> logger, TimeProvider timeProvider = null)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _productScraper = productScraper ?? throw new ArgumentNullException(nameof(productScraper));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _time = timeProvider ?? TimeProvider.System;
        }

        public string Platform => GearvnScraper.PlatformName;

        public async Task<IReadOnlyList<ScrapedEvent>> GetLiveEventsAsync(CancellationToken cancellationToken = default)
        {
            if (TryGetRecentResult(out var recent)) return recent;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (TryGetRecentResult(out recent)) return recent; // lượt khác vừa đọc xong trong lúc chờ

                var now = _time.GetUtcNow();
                var promotions = await ReadPromotionsAsync(cancellationToken);
                var running = promotions
                    .Where(p => p.EndAt > now && (p.StartAt == null || p.StartAt <= now))
                    .GroupBy(p => p.Slug, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .ToList();

                var events = new List<ScrapedEvent>();
                foreach (var promotion in running)
                {
                    var products = await GetProductsAsync(promotion, now, cancellationToken);
                    events.Add(new ScrapedEvent
                    {
                        ExternalId = StableId(promotion.Slug),
                        Title = promotion.Title,
                        Url = GearvnPromotionParser.PromotionUrl(promotion.Slug),
                        ImageUrl = promotion.BannerUrl,
                        ImageIsBanner = !string.IsNullOrWhiteSpace(promotion.BannerUrl), // banner 16:9 riêng của chương trình
                        EndsAt = promotion.EndAt,
                        Products = products
                    });
                }

                _logger.LogInformation("[GearVN] {Running}/{Total} chương trình khuyến mãi đang chạy, {WithProducts} có sản phẩm.",
                    running.Count, promotions.Count, events.Count(e => e.Products.Count > 0));

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

        private async Task<List<GearvnPromotion>> ReadPromotionsAsync(CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(GearvnScraper.PlatformName);
            var all = new List<GearvnPromotion>();

            for (int page = 1; page <= MaxListPages; page++)
            {
                string url = GearvnPromotionParser.ListPageUrl(page);
                IReadOnlyList<GearvnPromotion> promotions;
                try
                {
                    using var response = await client.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    promotions = GearvnPromotionParser.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                }
                catch (Exception ex) when (page > 1 && ex is not OperationCanceledException)
                {
                    _logger.LogWarning("[GearVN] Không đọc được trang khuyến mãi {Page}: {Message}", page, ex.Message);
                    break; // đã có trang 1 – dùng tạm phần đọc được
                }

                if (promotions.Count == 0)
                {
                    if (page == 1) throw new InvalidOperationException("Không đọc được chương trình nào từ trang khuyến mãi GearVN (trang đổi giao diện hoặc bị chặn).");
                    break; // hết trang
                }
                all.AddRange(promotions);
            }
            return all;
        }

        private async Task<IReadOnlyList<ScrapedProduct>> GetProductsAsync(GearvnPromotion promotion, DateTimeOffset now, CancellationToken cancellationToken)
        {
            string offer = Shorten(DescribeOffer(promotion));
            var result = new List<ScrapedProduct>();

            var candidates = promotion.Products
                .Where(p => string.Equals(p.Role, "condition", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxProductCandidatesPerEvent);

            foreach (var path in candidates)
            {
                if (result.Count >= MaxProductsPerEvent) break;
                if (!_productScraper.TryNormalizeProductUrl(GearvnUrlParser.BaseUrl + path, out var url)) continue;

                var details = await GetProductDetailsAsync(url, now, cancellationToken);
                if (details == null || !details.IsInStock) continue;

                result.Add(new ScrapedProduct
                {
                    ExternalId = details.ExternalId,
                    Name = details.Name,
                    Price = details.Price,
                    ImageUrl = details.ImageUrl,
                    Url = url,
                    Description = offer, // thẻ sản phẩm hiện ưu đãi đi kèm, vd "Tặng chuột Logitech G304"
                    IsAvailable = true
                });
            }
            return result;
        }

        private async Task<ScrapedProduct> GetProductDetailsAsync(string url, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (_productDetails.TryGetValue(url, out var cached) && now - cached.FetchedAt < ProductDetailsLifetime)
                return cached.Product;

            try
            {
                var product = await _productScraper.GetProductAsync(url, cancellationToken);
                _productDetails[url] = (product, now);
                return product;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[GearVN] Không đọc được sản phẩm khuyến mãi {Url}: {Message}", url, ex.Message);
                return null;
            }
        }

        // Nội dung ưu đãi hiện trên thẻ sản phẩm: "offer" của chương trình (bỏ mã nội bộ đầu dòng, vd "PBK-20260619-0083 · ");
        // không có thì tên quà tặng ("Tặng: Chuột G304", hoặc nhãn deal của quà, vd "Đồng giá 990.000đ: Bàn phím K3")
        private static string DescribeOffer(GearvnPromotion promotion)
        {
            string offer = InternalCodePrefix.Replace(promotion.Offer ?? "", "").Trim();
            if (offer.Length > 0) return offer;

            var gift = promotion.Products.FirstOrDefault(p => string.Equals(p.Role, "gift", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Name));
            if (gift == null) return null;
            string label = string.IsNullOrWhiteSpace(gift.DealLabel) || gift.DealLabel.Equals("Tặng kèm", StringComparison.OrdinalIgnoreCase) ? "Tặng" : gift.DealLabel;
            return $"{label}: {gift.Name}";
        }

        private static readonly System.Text.RegularExpressions.Regex InternalCodePrefix =
            new(@"^[A-Z]{2,6}-\d{6,}-\d+\s*[·\-–:]\s*", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string Shorten(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            return text.Length <= MaxOfferLength ? text : text[..(MaxOfferLength - 1)].TrimEnd() + "…";
        }

        // ScrapedEvent.ExternalId là số; chương trình GearVN định danh bằng slug → băm ổn định (FNV-1a), không đổi giữa các lần chạy
        private static int StableId(string slug)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in slug.ToLowerInvariant()) { hash ^= c; hash *= 16777619; }
                return (int)(hash & 0x7FFFFFFF);
            }
        }
    }
}
