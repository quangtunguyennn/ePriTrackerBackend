using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Tiki
{
    /// <summary>
    /// Strategy cho sàn Tiki: lấy chi tiết/tìm kiếm sản phẩm qua API (Hybrid: HttpClient → Playwright)
    /// và quét trang sự kiện khuyến mãi (Playwright).
    /// </summary>
    public class TikiScraper : IPlatformScraper, IEventScraper
    {
        public const string PlatformName = "Tiki";

        // API banner sự kiện nằm ở tên miền khác (tka.tiki.vn)
        private const string EventBannerApiUrl = "https://tka.tiki.vn/widget/api/v1/banners-group?group=banner_carousel_2_8";

        private readonly IHybridFetcher _fetcher;
        private readonly ITikiBrowserService _browser;
        private readonly ILogger<TikiScraper> _logger;

        // Mỗi sàn có bộ fetcher (thứ tự chiến lược tải) riêng → lấy bản đăng ký theo key "Tiki"
        public TikiScraper(
            [FromKeyedServices(PlatformName)] IHybridFetcher fetcher,
            ITikiBrowserService browser,
            ILogger<TikiScraper> logger)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Platform => PlatformName;

        public bool CanHandle(string productUrl) => TikiUrlParser.IsTikiUrl(productUrl);

        public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
        {
            normalizedUrl = TikiUrlParser.TryGetProductId(productUrl, out var productId)
                ? TikiUrlParser.NormalizeProductUrl(productId)
                : null;
            return normalizedUrl != null;
        }

        public async Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            string apiPath = TikiUrlParser.BuildProductApiPath(productUrl);
            TikiUrlParser.TryGetProductId(productUrl, out var productId);

            JsonElement root = await _fetcher.FetchJsonAsync(apiPath, cancellationToken: cancellationToken);

            return new ScrapedProduct
            {
                ExternalId = productId,
                Name = GetString(root, "name"),
                Price = GetDecimal(root, "price"),
                OriginalPrice = GetDecimal(root, "original_price"),
                ImageUrl = GetString(root, "thumbnail_url"),
                Description = GetString(root, "description"),
                Url = TikiUrlParser.NormalizeProductUrl(productId)
            };
        }

        public async Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            string apiPath = TikiUrlParser.BuildSearchApiPath(keyword, limit);
            JsonElement root = await _fetcher.FetchJsonAsync(apiPath, options, cancellationToken);
            return ParseProductList(root);
        }

        public async Task<IReadOnlyList<ScrapedEvent>> GetCurrentEventsAsync(CancellationToken cancellationToken = default)
        {
            var events = new List<ScrapedEvent>();

            try
            {
                JsonElement root = await _fetcher.FetchJsonAsync(EventBannerApiUrl, cancellationToken: cancellationToken);

                // Bắt lấy toàn bộ JSON thô mà Tiki trả về
                string rawJson = root.ValueKind != JsonValueKind.Undefined ? root.GetRawText() : "Dữ liệu trả về rỗng (WAF chặn)";

                if (root.TryGetProperty("data", out JsonElement dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var groupItem in dataArray.EnumerateArray())
                    {
                        if (!groupItem.TryGetProperty("banners", out JsonElement bannersArray) || bannersArray.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var item in bannersArray.EnumerateArray())
                        {
                            int tikiId = item.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                            string rawTitleUrl = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                            string imageUrl = item.TryGetProperty("image_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
                            string eventUrl = item.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";

                            if (tikiId > 0 && !string.IsNullOrEmpty(eventUrl))
                            {
                                events.Add(new ScrapedEvent
                                {
                                    ExternalId = tikiId,
                                    Title = TikiUrlParser.FormatEventTitle(rawTitleUrl),
                                    Url = eventUrl,
                                    ImageUrl = imageUrl
                                });
                            }
                        }
                    }
                }

                // BẪY BẮT LỖI: Nếu lấy được 0 sự kiện, ném thẳng JSON của Tiki ra để Admin thấy trên Swagger/UI
                if (events.Count == 0)
                {
                    throw new Exception($"Tiki trả về: {rawJson}");
                }

                return events;
            }
            catch (Exception ex)
            {
                // Giữ hành vi cũ: chỉ mang message ra ngoài để Controller trả về cho client
                throw new Exception(ex.Message);
            }
        }

        public async Task<IReadOnlyList<ScrapedProduct>> GetEventProductsAsync(string eventUrl, CancellationToken cancellationToken = default)
        {
            try
            {
                // Playwright bóc tách DOM của trang Landing Page, trả về JSON giả lập dạng API { data: [...] }
                JsonElement root = await _browser.ScrapeEventPageAsync(eventUrl);
                return ParseProductList(root);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TikiScraper] Lỗi cào sản phẩm sự kiện: {Url}", eventUrl);
                return Array.Empty<ScrapedProduct>();
            }
        }

        /// <summary>
        /// Đọc mảng "data" chung cho kết quả tìm kiếm API và kết quả quét DOM trang sự kiện.
        /// Phần tử không có "id" bị bỏ qua.
        /// </summary>
        private static IReadOnlyList<ScrapedProduct> ParseProductList(JsonElement root)
        {
            var products = new List<ScrapedProduct>();

            if (!root.TryGetProperty("data", out JsonElement dataArray) || dataArray.ValueKind != JsonValueKind.Array)
                return products;

            foreach (var item in dataArray.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idProp)) continue;

                products.Add(new ScrapedProduct
                {
                    ExternalId = idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out var id) ? id.ToString() : idProp.ToString(),
                    Name = GetString(item, "name"),
                    Price = GetDecimal(item, "price"),
                    OriginalPrice = GetDecimal(item, "original_price"),
                    ImageUrl = GetString(item, "thumbnail_url"),
                    Url = TikiUrlParser.ToAbsoluteUrl(GetString(item, "url_path"))
                });
            }

            return products;
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

        private static decimal? GetDecimal(JsonElement element, string property)
            => element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetDecimal() : null;
    }
}
