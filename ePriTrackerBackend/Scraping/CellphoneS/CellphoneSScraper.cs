using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ePriTrackerBackend.Scraping.Abstractions;
using ePriTrackerBackend.Scraping.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ePriTrackerBackend.Scraping.CellphoneS
{
    /// <summary>
    /// Strategy cho CellphoneS:
    /// - Chi tiết sản phẩm: đọc schema.org (JSON-LD) + mô tả HTML trong trang sản phẩm (ổn định, dùng khi thêm sản phẩm và làm dự phòng).
    /// - Tìm kiếm (gợi ý) và giá theo lô (job giá): qua GraphQL nội bộ <see cref="ICellphoneSApi"/>.
    /// </summary>
    public class CellphoneSScraper : ISearchablePlatformScraper, IBatchPriceScraper
    /// Strategy cho CellphoneS: đọc dữ liệu schema.org (JSON-LD) trong trang sản phẩm,
    /// và tìm kiếm sản phẩm (cho gợi ý) bằng cách render trang tìm kiếm bằng Playwright rồi đọc HTML.
    /// LƯU Ý: robots.txt của CellphoneS cấm trang tìm kiếm (/catalogsearch/). Trước đây nhóm cố ý không cài tìm kiếm vì lý do này;
    /// việc bật lại là quyết định của nhóm. Nên đặt "Scraping:Platforms:CellphoneS:MinIntervalMs" và giữ số lần tìm ở mức thấp.
    /// </summary>
    public class CellphoneSScraper : ISearchablePlatformScraper
    {
        /// <summary>Số mã sản phẩm mỗi request lấy giá theo lô.</summary>
        public const int BatchSize = 50;

        /// <summary>Câu tìm không ra kết quả → bỏ dần từ cuối, tối đa chừng này lần gọi; ngắn hơn MinSearchWords thì thôi.</summary>
        private const int MaxSearchAttempts = 3;
        private const int MinSearchWords = 2;
        private const string NewCondition = "Mới";

        public const string PlatformName = "CellphoneS";

        /// <summary>Thuộc tính HtmlJsonLdFetchStrategy gắn thêm vào JSON: HTML mô tả đầy đủ (JSON-LD chỉ có 1 câu SEO).</summary>
        public const string DescriptionHtmlField = "x-descriptionHtml";

        /// <summary>"Tính năng nổi bật" + bài viết chi tiết. Máy cũ / sản phẩm ngừng bán không có 2 khối này.</summary>
        public const string DescriptionXPath = "//div[@id='cpsKsp'] | //div[@id='cpsContentSEO']";

        private readonly IHybridFetcher _fetcher;
        private readonly ICellphoneSApi _api;
        private readonly ILogger<CellphoneSScraper> _logger;

        public CellphoneSScraper([FromKeyedServices(PlatformName)] IHybridFetcher fetcher, ICellphoneSApi api, ILogger<CellphoneSScraper> logger)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Tìm bằng ô tìm kiếm của CellphoneS. Công cụ tìm của họ khớp chặt (tên dài / mã cấu hình lạ → 0 kết quả)
        /// nên rút gọn dần từ khoá. Chỉ lấy hàng mới (gợi ý hàng cũ cho sản phẩm mới là sai so sánh).
        /// </summary>
        public async Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            var words = (keyword ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (int attempt = 0, count = words.Length; attempt < MaxSearchAttempts && count >= MinSearchWords; attempt++, count--)
            {
                var found = await _api.QuickSearchAsync(string.Join(' ', words.Take(count)), cancellationToken);
                var products = found
                    .Where(p => p.Condition == null || p.Condition.Equals(NewCondition, StringComparison.OrdinalIgnoreCase))
                    .Select(ToScrapedProduct)
                    .Where(p => p != null)
                    .Take(limit)
                    .ToList();

                if (products.Count > 0) return products;
            }

            return Array.Empty<ScrapedProduct>();
        }

        /// <summary>
        /// Giá nhiều sản phẩm trong vài request: tra mã sản phẩm (mỗi link một lần, sau đó nơi gọi lưu lại) rồi lấy giá theo lô 50 mã.
        /// Link tra không ra / lô lỗi lẻ tẻ → bỏ qua (nơi gọi đọc JSON-LD). Lấy giá lỗi toàn bộ → ném.
        /// </summary>
        public async Task<IReadOnlyDictionary<string, BatchPriceResult>> GetCurrentPricesAsync(IReadOnlyList<BatchPriceRequest> items, CancellationToken cancellationToken = default)
        {
            var urlById = new Dictionary<long, string>();
            foreach (var item in items ?? Array.Empty<BatchPriceRequest>())
            {
                if (!TryNormalizeProductUrl(item.ProductUrl, out var url)) continue;

                long? id = long.TryParse(item.PlatformProductId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var known) && known > 0
                    ? known
                    : await TryLookupProductIdAsync(url, cancellationToken);

                if (id is long productId) urlById[productId] = item.ProductUrl;
            }

            var results = new Dictionary<string, BatchPriceResult>(StringComparer.OrdinalIgnoreCase);
            Exception firstError = null;
            int failedBatches = 0, batches = 0;

            foreach (var chunk in urlById.Keys.Chunk(BatchSize))
            {
                batches++;
                IReadOnlyList<CellphoneSApiProduct> products;
                try
                {
                    products = await _api.GetProductsByIdsAsync(chunk, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    firstError ??= ex;
                    failedBatches++;
                    continue;
                }

                foreach (var p in products)
                {
                    if (!urlById.TryGetValue(p.ProductId, out var requestedUrl)) continue;
                    var scraped = ToScrapedProduct(p);
                    if (scraped == null) continue;

                    results[requestedUrl] = new BatchPriceResult { Product = scraped, PlatformProductId = p.ProductId.ToString(CultureInfo.InvariantCulture) };
                }
            }

            // Mọi lô đều lỗi = API hỏng → để nơi gọi biết mà chuyển hẳn sang cào lẻ
            if (batches > 0 && failedBatches == batches && firstError != null) throw firstError;
            return results;
        }

        private async Task<long?> TryLookupProductIdAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                return await _api.GetProductIdByUrlAsync(url, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Không tra được mã sản phẩm CellphoneS của {Url}", url);
                return null;
            }
        }

        /// <summary>Sản phẩm từ API → ScrapedProduct. Giá 0 = không bán (hết hàng) → Price null như JSON-LD.</summary>
        public static ScrapedProduct ToScrapedProduct(CellphoneSApiProduct p)
        {
            string url = CellphoneSApiParser.ProductUrl(p.UrlPath);
            if (url == null || !CellphoneSUrlParser.TryGetSlug(url, out var slug)) return null;

            return new ScrapedProduct
            {
                ExternalId = slug,
                Name = p.Name?.Trim(),
                Price = p.DisplayPrice > 0 ? p.DisplayPrice : null,
                OriginalPrice = p.RootPrice > 0 ? p.RootPrice : null,
                ImageUrl = CellphoneSApiParser.ImageUrl(p.Thumbnail),
                Url = CellphoneSUrlParser.NormalizeProductUrl(slug)
            };
        /// <summary>
        /// Mẫu URL trang tìm kiếm, {0} = từ khoá đã mã hoá URL. Ghi đè được bằng cấu hình "Scraping:Platforms:CellphoneS:SearchUrlTemplate"
        /// (không cần biên dịch lại). CẦN XÁC MINH: gõ thử vào ô tìm kiếm trên cellphones.com.vn rồi so với URL trình duyệt hiển thị.
        /// </summary>
        private const string DefaultSearchUrlTemplate = "https://cellphones.com.vn/catalogsearch/result/?q={0}";

        private readonly IHybridFetcher _fetcher;
        private readonly IPageRenderer _renderer;
        private readonly ILogger<CellphoneSScraper> _logger;
        private readonly string _searchUrlTemplate;

        public CellphoneSScraper(
            [FromKeyedServices(PlatformName)] IHybridFetcher fetcher,
            IPageRenderer renderer,
            IConfiguration configuration,
            ILogger<CellphoneSScraper> logger)
        {
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            string configured = configuration?[$"Scraping:Platforms:{PlatformName}:SearchUrlTemplate"];
            _searchUrlTemplate = !string.IsNullOrWhiteSpace(configured) && configured.Contains("{0}") ? configured : DefaultSearchUrlTemplate;
        }

        public string Platform => PlatformName;

        public bool CanHandle(string productUrl) => CellphoneSUrlParser.IsCellphoneSUrl(productUrl);

        public bool TryNormalizeProductUrl(string productUrl, out string normalizedUrl)
        {
            normalizedUrl = CellphoneSUrlParser.TryGetSlug(productUrl, out var slug)
                ? CellphoneSUrlParser.NormalizeProductUrl(slug)
                : null;
            return normalizedUrl != null;
        }

        public async Task<ScrapedProduct> GetProductAsync(string productUrl, CancellationToken cancellationToken = default)
        {
            if (!CellphoneSUrlParser.TryGetSlug(productUrl, out var slug))
                throw new ArgumentException("Link CellphoneS không chứa mã sản phẩm.", nameof(productUrl));

            string url = CellphoneSUrlParser.NormalizeProductUrl(slug);
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
        /// Trang tìm kiếm nạp kết quả bằng JavaScript → phải render bằng trình duyệt (IPageRenderer), HttpClient không đủ.
        /// Dùng chế độ render nhanh (<see cref="PageRenderOptions.Search"/>: chờ giá xuất hiện, không cuộn 10 lần).
        /// Không áp <see cref="FetchOptions.Timeout"/> (render có timeout riêng; huỷ giữa chừng chỉ làm mất kết quả).
        /// Không đi qua HybridFetcher nên không ghi telemetry.
        /// </summary>
        public async Task<IReadOnlyList<ScrapedProduct>> SearchAsync(string keyword, int limit, FetchOptions options = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(keyword) || limit <= 0) return Array.Empty<ScrapedProduct>();

            string searchUrl = SearchResultHtmlParser.BuildSearchUrl(_searchUrlTemplate, keyword);

            var sw = Stopwatch.StartNew();
            string html = await _renderer.RenderAsync(searchUrl, PageRenderOptions.Search, cancellationToken);
            long renderMs = sw.ElapsedMilliseconds;

            var products = SearchResultHtmlParser.Parse(html, searchUrl, ResolveProductLink, limit);
            _logger.LogInformation("[CellphoneS] Tìm '{Keyword}': {Count} sản phẩm (render {RenderMs}ms, đọc {ParseMs}ms) {Url}",
                keyword, products.Count, renderMs, sw.ElapsedMilliseconds - renderMs, searchUrl);

            if (products.Count == 0)
                SearchResultHtmlParser.DumpForDebug(_logger, PlatformName, searchUrl, html);

            return products;
        }

        // Chỉ nhận link trang sản phẩm của CellphoneS; trả link chuẩn hoá + slug làm mã ngoài (giống GetProductAsync)
        private static (string Url, string ExternalId)? ResolveProductLink(string url)
        {
            if (CellphoneSUrlParser.IsCellphoneSUrl(url) && CellphoneSUrlParser.TryGetSlug(url, out var slug))
                return (CellphoneSUrlParser.NormalizeProductUrl(slug), slug);

            return null;
        }

        /// <summary>
        /// schema.org ItemAvailability, ví dụ "https://schema.org/InStock". PreOrder (đặt trước, chưa có hàng) tính là hết hàng
        /// – thống nhất với "upcoming" của Tiki. Giá trị lạ → null.
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
        /// Giá trong JSON-LD có thể là chuỗi hoặc số. Giá 0 = sàn không bán (ngừng kinh doanh) → coi như không có giá.
        /// </summary>
        private static decimal? ParsePrice(JsonElement offer)
        {
            if (!offer.TryGetProperty("price", out var p)) return null;

            decimal value;
            if (p.ValueKind == JsonValueKind.Number) value = p.GetDecimal();
            else if (p.ValueKind != JsonValueKind.String || !decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value)) return null;

            return value > 0 ? value : null;
        }

        // "image" có thể là chuỗi, mảng chuỗi, hoặc ImageObject { url }
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