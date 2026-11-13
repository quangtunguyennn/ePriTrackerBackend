using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Gearvn
{
    /// <summary>
    /// Strategy cho GearVN: trang sản phẩm có JSON-LD "Product" + một "Offer" (đọc bằng HtmlJsonLdFetchStrategy như HACOM);
    /// trang tìm kiếm render sẵn trên server (Next.js) → đọc HTML bằng <see cref="SearchResultHtmlParser"/>, không cần Playwright.
    /// robots.txt cho phép /products và /search (chỉ chặn /api/, giỏ hàng, tài khoản), không có Crawl-delay.
    /// Trang đứng sau Cloudflare: hiện cho HttpClient qua; bị chặn thì request lỗi → job giữ giá cũ, gợi ý bỏ qua sàn này.
    /// </summary>
    public class GearvnScraper : ISearchablePlatformScraper
    {
        public const string PlatformName = "GearVN";

        /// <summary>Trang tìm kiếm (kiểm tra 31/10/2026: /search?q=... trả HTML có sẵn thẻ sản phẩm, giá gốc + giá giảm).</summary>
        public const string SearchUrlTemplate = "https://gearvn.com/search?q={0}";

        private readonly IHybridFetcher _fetcher;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<GearvnScraper> _logger;

        public GearvnScraper(
            [FromKeyedServices(PlatformName)] IHybridFetcher fetcher,
            IHttpClientFactory httpClientFactory,
            ILogger<GearvnScraper> logger)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Platform => PlatformName;

        public bool CanHandle(string productUrl) => GearvnUrlParser.IsGearvnUrl(productUrl);

        public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
        {
            normalizedUrl = GearvnUrlParser.TryGetHandle(productUrl, out var handle)
                ? GearvnUrlParser.NormalizeProductUrl(handle)
                : null;
            return normalizedUrl != null;
        }

        public async Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            if (!GearvnUrlParser.TryGetHandle(productUrl, out var handle))
                throw new ArgumentException("Link GearVN không phải trang sản phẩm.", nameof(productUrl));

            string url = GearvnUrlParser.NormalizeProductUrl(handle);
            JsonElement product = await _fetcher.FetchJsonAsync(url, cancellationToken: cancellationToken);
            return ToScrapedProduct(product, handle, url);
        }

        /// <summary>JSON-LD Product → ScrapedProduct (hàm thuần để test). Mô tả: JSON-LD chỉ có đoạn ngắn – trang chưa có khối mô tả đầy đủ trong HTML.</summary>
        public static ScrapedProduct ToScrapedProduct(JsonElement product, string handle, string url)
        {
            JsonElement? offer = JsonLdProductReader.FirstOffer(product);

            return new ScrapedProduct
            {
                ExternalId = handle,
                Name = GetString(product, "name"),
                Price = offer is JsonElement o ? ParsePrice(o) : null,
                ImageUrl = GetImage(product),
                Description = GetString(product, "description"),
                Url = url,
                IsAvailable = offer is JsonElement a ? ParseAvailability(GetString(a, "availability")) : null
            };
        }

        /// <summary>Đọc HTML trang tìm kiếm bằng HttpClient. Không đi qua HybridFetcher nên không ghi telemetry (giống HACOM).</summary>
        public async Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return Array.Empty<ScrapedProduct>();

            string searchUrl = SearchResultHtmlParser.BuildSearchUrl(SearchUrlTemplate, keyword);
            var sw = Stopwatch.StartNew();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options?.Timeout is TimeSpan timeout)
                timeoutCts.CancelAfter(timeout);

            var client = _httpClientFactory.CreateClient(PlatformName);
            using var response = await client.GetAsync(searchUrl, timeoutCts.Token);
            response.EnsureSuccessStatusCode(); // bị chặn / lỗi → SuggestionFinder ghi log và bỏ qua sàn này

            string html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var results = SearchResultHtmlParser.Parse(html, searchUrl, ResolveProductLink, limit);

            _logger.LogInformation("[GearVN] Tìm '{Keyword}': {Count} sản phẩm ({Ms}ms, HTML {Kb} KB)", keyword, results.Count, sw.ElapsedMilliseconds, html.Length / 1024);
            if (results.Count == 0)
                SearchResultHtmlParser.DumpForDebug(_logger, PlatformName, searchUrl, html);

            return results;
        }

        // Chỉ nhận link trang sản phẩm của GearVN; trả link chuẩn hoá + handle làm mã ngoài (giống GetProductAsync)
        private static (string Url, string ExternalId)? ResolveProductLink(string url)
        {
            if (GearvnUrlParser.TryGetHandle(url, out var handle))
                return (GearvnUrlParser.NormalizeProductUrl(handle), handle);

            return null;
        }

        /// <summary>schema.org ItemAvailability (cùng quy ước với CellphoneS, HACOM).</summary>
        public static bool? ParseAvailability(string availability)
        {
            if (string.IsNullOrWhiteSpace(availability)) return null;

            return availability.Trim().Split('/').Last().ToLowerInvariant() switch
            {
                "instock" or "limitedavailability" or "onlineonly" or "instoreonly" => true,
                "outofstock" or "soldout" or "discontinued" or "preorder" => false,
                _ => null
            };
        }

        /// <summary>Giá trong JSON-LD có thể là chuỗi hoặc số.</summary>
        private static decimal? ParsePrice(JsonElement offer)
        {
            if (!offer.TryGetProperty("price", out var p)) return null;

            decimal value;
            if (p.ValueKind == JsonValueKind.Number) value = p.GetDecimal();
            else if (p.ValueKind != JsonValueKind.String || !decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return null;

            return value > 0 ? value : null;
        }

        private static string GetImage(JsonElement product)
        {
            if (!product.TryGetProperty("image", out var image)) return null;
            if (image.ValueKind == JsonValueKind.Array) image = image.EnumerateArray().FirstOrDefault();
            if (image.ValueKind == JsonValueKind.String) return image.GetString();
            return image.ValueKind == JsonValueKind.Object ? GetString(image, "url") : null;
        }

        private static string GetString(JsonElement element, string property)
            => element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}
