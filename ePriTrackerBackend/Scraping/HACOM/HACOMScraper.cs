using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.Hacom
{
    /// <summary>
    /// Strategy cho HACOM: đọc dữ liệu schema.org (JSON-LD) trong trang sản phẩm,
    /// và tìm kiếm sản phẩm (cho gợi ý) bằng cách đọc HTML trang tìm kiếm (HttpClient → Playwright nếu không đọc được).
    /// </summary>
    public class HacomScraper : ISearchablePlatformScraper
    {
        public const string PlatformName = "HACOM";

        /// <summary>Thuộc tính HtmlJsonLdFetchStrategy gắn thêm vào JSON: HTML mô tả đầy đủ (JSON-LD chỉ có 1 câu SEO).</summary>
        public const string DescriptionHtmlField = "x-descriptionHtml";

        /// <summary>Đường dẫn XPath đến khối mô tả sản phẩm của HACOM.</summary>
        public const string DescriptionXPath = "//div[contains(@class, 'product-desc')] | //div[@id='tab-content-desc']";

        /// <summary>
        /// Mẫu URL trang tìm kiếm, {0} = từ khoá đã mã hoá URL. Ghi đè được bằng cấu hình "Scraping:Platforms:HACOM:SearchUrlTemplate"
        /// (không cần biên dịch lại). CẦN XÁC MINH: gõ thử vào ô tìm kiếm trên hacom.vn rồi so với URL trình duyệt hiển thị;
        /// kiểm tra thêm hacom.vn/robots.txt.
        /// </summary>
        private const string DefaultSearchUrlTemplate = "https://hacom.vn/tim?q={0}";

        private readonly IHybridFetcher _fetcher;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IPageRenderer _renderer;
        private readonly ILogger<HacomScraper> _logger;
        private readonly string _searchUrlTemplate;

        public HacomScraper(
            [FromKeyedServices(PlatformName)] IHybridFetcher fetcher,
            IHttpClientFactory httpClientFactory,
            IPageRenderer renderer,
            IConfiguration configuration,
            ILogger<HacomScraper> logger)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            string configured = configuration?[$"Scraping:Platforms:{PlatformName}:SearchUrlTemplate"];
            _searchUrlTemplate = !string.IsNullOrWhiteSpace(configured) && configured.Contains("{0}") ? configured : DefaultSearchUrlTemplate;
        }

        public string Platform => PlatformName;

        public bool CanHandle(string productUrl) => HacomUrlParser.IsHacomUrl(productUrl);

        public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
        {
            normalizedUrl = HacomUrlParser.TryGetSlug(productUrl, out var slug)
                ? HacomUrlParser.NormalizeProductUrl(slug)
                : null;
            return normalizedUrl != null;
        }

        public async Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            if (!HacomUrlParser.TryGetSlug(productUrl, out var slug))
                throw new ArgumentException("Link HACOM không chứa mã sản phẩm.", nameof(productUrl));

            string url = HacomUrlParser.NormalizeProductUrl(slug);
            JsonElement product = await _fetcher.FetchJsonAsync(url, cancellationToken: cancellationToken);
            JsonElement? offer = JsonLdProductReader.FirstOffer(product);

            return new ScrapedProduct
            {
                ExternalId = slug,
                Name = GetString(product, "name"),
                Price = offer is JsonElement o ? ParsePrice(o) : null,
                ImageUrl = GetImage(product),
                Description = GetString(product, DescriptionHtmlField) ?? GetString(product, "description"),
                Url = url,
                IsAvailable = offer is JsonElement a ? ParseAvailability(GetString(a, "availability")) : null
            };
        }

        /// <summary>
        /// Thử HttpClient trước (nhanh, có giới hạn tốc độ theo cấu hình); không đọc được sản phẩm nào
        /// (trang nạp bằng JavaScript, bị chặn, lỗi mạng) → render nhanh bằng Playwright (<see cref="PageRenderOptions.Search"/>).
        /// Không đi qua HybridFetcher nên không ghi telemetry.
        /// </summary>
        public async Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return Array.Empty<ScrapedProduct>();

            string searchUrl = SearchResultHtmlParser.BuildSearchUrl(_searchUrlTemplate, keyword);
            var sw = Stopwatch.StartNew();

            var (results, httpHtml) = await TrySearchWithHttpAsync(searchUrl, limit, options, cancellationToken);
            if (results.Count > 0)
            {
                _logger.LogInformation("[HACOM] Tìm '{Keyword}': {Count} sản phẩm bằng HttpClient ({Ms}ms) {Url}", keyword, results.Count, sw.ElapsedMilliseconds, searchUrl);
                return results;
            }

            _logger.LogInformation("[HACOM] HttpClient không đọc được sản phẩm nào ({Ms}ms), chuyển sang Playwright: {Url}", sw.ElapsedMilliseconds, searchUrl);

            string html = await _renderer.RenderAsync(searchUrl, PageRenderOptions.Search, cancellationToken);
            var rendered = SearchResultHtmlParser.Parse(html, searchUrl, ResolveProductLink, limit);
            _logger.LogInformation("[HACOM] Tìm '{Keyword}': {Count} sản phẩm bằng Playwright (tổng {Ms}ms)", keyword, rendered.Count, sw.ElapsedMilliseconds);

            if (rendered.Count == 0)
                SearchResultHtmlParser.DumpForDebug(_logger, PlatformName, searchUrl, html ?? httpHtml);

            return rendered;
        }

        private async Task<(IReadOnlyList<ScrapedProduct> Products, string Html)> TrySearchWithHttpAsync(string searchUrl, int limit, FetchOptions options, CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (options?.Timeout is TimeSpan timeout)
                    timeoutCts.CancelAfter(timeout);

                var client = _httpClientFactory.CreateClient(PlatformName);
                using var response = await client.GetAsync(searchUrl, timeoutCts.Token);
                response.EnsureSuccessStatusCode();

                string html = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                return (SearchResultHtmlParser.Parse(html, searchUrl, ResolveProductLink, limit), html);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[HACOM] Tìm kiếm bằng HttpClient thất bại ({Msg}): {Url}", ex.Message, searchUrl);
                return (Array.Empty<ScrapedProduct>(), null);
            }
        }

        // Chỉ nhận link trang sản phẩm của HACOM; trả link chuẩn hoá + slug làm mã ngoài (giống GetProductAsync)
        private static (string Url, string ExternalId)? ResolveProductLink(string url)
        {
            if (HacomUrlParser.IsHacomUrl(url) && HacomUrlParser.TryGetSlug(url, out var slug))
                return (HacomUrlParser.NormalizeProductUrl(slug), slug);

            return null;
        }

        /// <summary>
        /// schema.org ItemAvailability. Thống nhất logic có hàng/hết hàng/đặt trước.
        /// </summary>
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

        /// <summary>
        /// Giá trong JSON-LD có thể là chuỗi hoặc số.
        /// </summary>
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